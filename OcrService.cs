using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace LST;

/// <summary>一行识别结果：文字 + 在原图中的位置（物理像素）。</summary>
internal sealed record OcrLine(string Text, Rectangle Box, float Score);

/// <summary>
/// OCR 客户端。OCR（ONNX Runtime + PP-OCRv4 移动端模型）跑在独立的子进程里（本程序以 --ocr-worker 启动自己），
/// 托盘主进程不加载 ONNX Runtime 和模型。空闲超时后直接结束子进程：内存由操作系统整块回收，
/// 不依赖 Dispose / GC 把原生堆还给系统（后者在推理库上往往做不到，工作集会一直停在高位）。
/// 驻留模式：Light = 30 秒 / Balanced = 5 分钟（默认）/ Resident = 常驻到程序退出。
/// </summary>
internal static class OcrService
{
    private const int Magic = 0x3254534C;
    private const string NativeWorkerName = "lst-ocr.exe";
    private const long LightMs = 30_000, BalancedMs = 300_000;

    private static readonly SemaphoreSlim Gate = new(1, 1);       // 同一时刻只有一个请求在与子进程通信
    private static readonly object Cfg = new();
    private static readonly System.Threading.Timer TrimTimer = new(_ => OnTrimTick(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly System.Threading.Timer IdleTimer =
        new(_ => OnIdleTick(), null, Timeout.Infinite, Timeout.Infinite);

    // 以下字段只在持有 Gate 时读写（_running、_lastUseTicks 例外，见各自注释）
    private static Process? _proc;
    private static Stream? _wr, _rd;                              // 与子进程通信的两条流（原生版：stdin/stdout；托管版：匿名管道）
    private static volatile bool _warm;                            // 当前子进程的引擎已创建并预热
    private static volatile bool _speculative;                    // 子进程由预热拉起、还没服务过真实请求：空闲 20 秒就结束
    private static int _prewarmPending;
    private const long SpeculativeIdleMs = 20_000;
    private const int TrimDelayMs = 15_000;                       // 识别停下 15 秒后才 trim 子进程（连续识别保持热）
    private static bool _nativeFailed;                            // 原生子进程起不来/中途崩溃后，本次运行改用托管版
    private static BinaryWriter? _toChild;
    private static BinaryReader? _fromChild;
    private static readonly StringBuilder Stderr = new();
    private static volatile bool _running;
    private static long _lastUseTicks;
    private static long _idleMs = BalancedMs;                      // 受 Cfg 保护，< 0 表示常驻
    private static volatile int _detPixels = 100_000;             // 单次检测的像素预算；变更后下次识别时重启子进程
    private static int _procPixels;

    public static bool IsLoaded => _running;

    /// <summary>keepMode：Light / Balanced / Resident。设置变更后调用。</summary>
    public static void Configure(string? keepMode, string? quality)
    {
        lock (Cfg) _idleMs = keepMode switch { "Light" => LightMs, "Resident" => -1, _ => BalancedMs };
        _detPixels = quality switch { "Balanced" => 200_000, "Accuracy" => 400_000, _ => 100_000 };
        ScheduleIdle();
    }

    /// <summary>
    /// 调用线程（UI 线程）只做一次廉价的像素拷贝（进非托管缓冲），之后位图可以随意释放/重绘；
    /// 管道通信、检测、识别全部在后台线程/子进程。
    /// </summary>
    public static async Task<List<OcrLine>> RecognizeAsync(Bitmap image, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int w = image.Width, h = image.Height;
        long tPack = Perf.Now();
        var buf = PixelBuffer.FromBitmap(image);
        if (Perf.Enabled) Perf.Log($"ocr pack {w}x{h}: {Perf.Ms(tPack):F1} ms ({buf.Length / 1048576.0:F1} MB)");
        Task<List<OcrLine>> work;
        try
        {
            // 阻塞式管道读写放在专用线程上（LongRunning），不占用线程池：线程池被占满时取消/超时回调也能及时执行
            work = Task.Factory.StartNew(() => Exchange(buf, w, h, ct), CancellationToken.None,
                                         TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { buf.Dispose(); throw; }
        _ = work.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // 被放弃的任务出错时也要“观察”异常
        // 调用方取消后立即返回；原生推理无法中断，已发出的请求会在后台跑完（结果丢弃），超时则由看门狗终止子进程
        return await work.WaitAsync(ct);
    }

    /// <summary>
    /// 预热：在用户还没点“识字/翻译”时（热键按下、鼠标移到按钮上）提前拉起子进程并让引擎完成首次推理的一次性初始化，
    /// 把冷启动延迟藏到用户选区/移动鼠标的时间里。预热出来但没被使用的子进程 20 秒后自动结束。非阻塞、可重复调用。
    /// </summary>
    public static void Prewarm()
    {
        if (Interlocked.Exchange(ref _prewarmPending, 1) == 1) return;
        _ = Task.Factory.StartNew(() =>
        {
            try { PrewarmCore(); }
            catch { /* 预热失败不打扰用户：真正识别时会再次尝试并报告错误 */ }
            finally { Volatile.Write(ref _prewarmPending, 0); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void PrewarmCore()
    {
        if (!Gate.Wait(0)) return;   // 正有请求在用：无需预热
        try
        {
            long t0 = Perf.Now();
            bool started = EnsureStartedLocked();
            if (!started && _warm) return;   // 已经是热的

            bool timedOut = false;
            var proc = _proc!;
            using var watchdog = new System.Threading.Timer(_ => { timedOut = true; try { proc.Kill(true); } catch { } },
                                                            null, 60_000, Timeout.Infinite);
            try
            {
                var bw = _toChild!;
                bw.Write(Magic); bw.Write(0); bw.Write(0); bw.Write(0);   // 0×0 的空帧 = 预热请求
                bw.Flush();
                ReadResponse(_fromChild!);
                _warm = true;
                if (started) _speculative = true;   // 没人用的预热进程只活 20 秒
                if (Perf.Enabled) Perf.Log($"ocr prewarm {(started ? "cold" : "warm")}: {Perf.Ms(t0):F0} ms");
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                if (!timedOut && _wr is not AnonymousPipeServerStream) _nativeFailed = true;
                KillLocked();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastUseTicks, Environment.TickCount64);
            ScheduleIdle();
            Gate.Release();
        }
    }

    private static List<OcrLine> ReadResponse(BinaryReader br)
    {
        if (br.ReadByte() != 0) throw new InvalidOperationException(br.ReadString());
        int n = br.ReadInt32();
        var lines = new List<OcrLine>(n);
        for (int i = 0; i < n; i++)
        {
            var box = new Rectangle(br.ReadInt32(), br.ReadInt32(), br.ReadInt32(), br.ReadInt32());
            float score = br.ReadSingle();
            lines.Add(new OcrLine(br.ReadString(), box, score));
        }
        return lines;
    }

    private static List<OcrLine> Exchange(PixelBuffer buf, int w, int h, CancellationToken ct)
    {
        try { return ExchangeCore(buf, w, h, ct); }
        finally { buf.Dispose(); }   // 任何路径（取消、超时、异常）都释放非托管缓冲
    }

    private static List<OcrLine> ExchangeCore(PixelBuffer buf, int w, int h, CancellationToken ct)
    {
        int len = buf.Length;
        long tQueue = Perf.Now();
        Gate.Wait(ct);   // 排队期间被取消（窗口已关闭）：直接退出，不占用也不启动子进程
        try
        {
            ct.ThrowIfCancellationRequested();
            double queueMs = Perf.Ms(tQueue);
            long tIo = Perf.Now();
            bool started = EnsureStartedLocked();

            // 看门狗：整个请求（含冷启动加载模型）超过时限就终止子进程，被阻塞的管道读写随之失败，下次识别会重建子进程
            bool timedOut = false;
            var proc = _proc!;
            using var watchdog = new System.Threading.Timer(_ =>
            {
                timedOut = true;
                try { proc.Kill(true); } catch { }
            }, null, RequestTimeoutMs(w, h, started && !_warm), Timeout.Infinite);
            try
            {
                var bw = _toChild!;
                bw.Write(Magic); bw.Write(w); bw.Write(h); bw.Write(len);
                bw.Flush();
                buf.WriteTo(_wr!);
                _wr!.Flush();
                buf.Dispose();                   // 已发给子进程：立即归还非托管内存（不等响应）

                var lines = ReadResponse(_fromChild!);
                _warm = true; _speculative = false;
                if (Perf.Enabled) Perf.Log($"ocr {w}x{h}: queue {queueMs:F0} ms, round-trip {Perf.Ms(tIo):F0} ms ({(started ? "cold start" : "warm")}), {lines.Count} lines");
                return lines;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                string err; lock (Stderr) err = Stderr.ToString().Trim();
                if (!timedOut && _proc != null && _wr is not AnonymousPipeServerStream) _nativeFailed = true;   // 原生子进程中途崩溃：下次改用托管版
                KillLocked();
                if (timedOut)
                    throw new TimeoutException("OCR 识别超时，已终止并重建识别进程，请重试。" + (err.Length > 0 ? "\n" + err : ""), ex);
                throw new InvalidOperationException(
                    "OCR 子进程异常退出（常见原因：缺少 VC++ 运行库 vc_redist.x64、models 文件夹缺失，或内存不足）。" +
                    (err.Length > 0 ? "\n" + err : ""), ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastUseTicks, Environment.TickCount64);
            ScheduleIdle();
            TrimTimer.Change(TrimDelayMs, Timeout.Infinite);   // 连续识别时不 trim（热路径不吃缺页）；停下一段时间后再把子进程的工作集换出
            Gate.Release();
        }
    }

    /// <summary>单次请求时限：基础 30 秒 + 每百万像素 15 秒（上限 3 分钟）；冷启动（加载模型）再加 20 秒。环境变量 LST_OCR_TIMEOUT_MS 可覆盖（排查/测试用）。</summary>
    private static int RequestTimeoutMs(int w, int h, bool coldStart)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("LST_OCR_TIMEOUT_MS"), out int o) && o > 0) return o;
        long ms = 30_000 + (long)((long)w * h / 1_000_000.0 * 15_000) + (coldStart ? 20_000 : 0);
        return (int)Math.Min(ms, 180_000);
    }

    // ---------- 子进程生命周期（调用方须持有 Gate） ----------
    /// <returns>是否新启动了子进程（冷启动）。</returns>
    private static bool EnsureStartedLocked()
    {
        int px = _detPixels;   // 取一次快照，启动参数与记录的值保持一致
        if (_running && _procPixels == px && _proc is { HasExited: false }) return false;
        KillLocked();

        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);   // 限制推理线程：避免 CPU 被吃满，也减少每线程的缓冲
        string native = Path.Combine(AppContext.BaseDirectory, NativeWorkerName);
        bool wantNative = !_nativeFailed && File.Exists(native)
                          && !string.Equals(Environment.GetEnvironmentVariable("LST_OCR_WORKER"), "managed", StringComparison.OrdinalIgnoreCase);

        if (wantNative)
        {
            try { StartNativeLocked(native, threads, px); return true; }
            catch { _nativeFailed = true; KillLocked(); /* 原生版启动失败：退回托管版 */ }
        }
        StartManagedLocked(threads, px);
        return true;
    }

    /// <summary>原生 C++ 子进程（lst-ocr.exe）：不含 .NET 运行时，识别时占用更低。协议与托管版相同，走 stdin/stdout 二进制流。</summary>
    private static void StartNativeLocked(string exe, int threads, int px)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add(threads.ToString());
        psi.ArgumentList.Add(px.ToString());
        AttachAndRegisterLocked(psi, null, null, px);
    }

    /// <summary>托管（.NET）子进程：本程序以 --ocr-worker 启动自己，经匿名管道通信。原生版不可用时的后备。</summary>
    private static void StartManagedLocked(int threads, int px)
    {
        var p2c = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var c2p = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(OcrWorker.Arg);
        psi.ArgumentList.Add(p2c.GetClientHandleAsString());
        psi.ArgumentList.Add(c2p.GetClientHandleAsString());
        psi.ArgumentList.Add(threads.ToString());
        psi.ArgumentList.Add(px.ToString());
        AttachAndRegisterLocked(psi, p2c, c2p, px);
    }

    private static void AttachAndRegisterLocked(ProcessStartInfo psi, AnonymousPipeServerStream? p2c, AnonymousPipeServerStream? c2p, int px)
    {
        var p = new Process { StartInfo = psi };
        lock (Stderr) Stderr.Clear();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (Stderr) { Stderr.AppendLine(e.Data); if (Stderr.Length > 4000) Stderr.Remove(0, Stderr.Length - 4000); }
        };
        try { p.Start(); }
        catch { p2c?.Dispose(); c2p?.Dispose(); p.Dispose(); throw; }
        p.BeginErrorReadLine();
        p2c?.DisposeLocalCopyOfClientHandle();
        c2p?.DisposeLocalCopyOfClientHandle();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // 有别的程序要用 CPU 时优先让路

        Stream wr = p2c ?? p.StandardInput.BaseStream;
        Stream rd = c2p ?? p.StandardOutput.BaseStream;
        _proc = p; _wr = wr; _rd = rd;
        _toChild = new BinaryWriter(wr, Encoding.UTF8, leaveOpen: true);
        _fromChild = new BinaryReader(rd, Encoding.UTF8, leaveOpen: true);
        _procPixels = px;
        _running = true;
    }

    private static void KillLocked()
    {
        _running = false; _warm = false; _speculative = false;
        try { _wr?.Dispose(); } catch { }   // 关闭写端 → 子进程读到 EOF 自行退出
        try { _rd?.Dispose(); } catch { }
        if (_proc != null)
        {
            try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
            try { _proc.Dispose(); } catch { }
        }
        _proc = null; _wr = null; _rd = null; _toChild = null; _fromChild = null;
    }

    // ---------- 空闲回收 ----------
    private static void ScheduleIdle()
    {
        long ms = EffectiveIdleMs();
        if (!_running || ms < 0) { IdleTimer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        long due = Math.Max(0, Interlocked.Read(ref _lastUseTicks) + ms - Environment.TickCount64);
        IdleTimer.Change(due + 50, Timeout.Infinite);
    }

    /// <summary>空闲多久后结束子进程；预热出来还没被使用的子进程只活 20 秒（用户没选识字/翻译就不该一直占着内存）。</summary>
    private static long EffectiveIdleMs()
    {
        long ms; lock (Cfg) ms = _idleMs;
        if (_speculative) ms = ms < 0 ? SpeculativeIdleMs : Math.Min(ms, SpeculativeIdleMs);
        return ms;
    }

    private static void OnTrimTick()
    {
        if (!Gate.Wait(0)) return;   // 有新请求在用：不 trim
        try { if (_running && _proc is { HasExited: false } p) MemoryTrim.TrimProcess(p); }
        catch { }
        finally { Gate.Release(); }
    }

    private static void OnIdleTick()
    {
        if (!Gate.Wait(0)) { IdleTimer.Change(1000, Timeout.Infinite); return; }   // 正在识别：稍后再看
        try
        {
            long ms = EffectiveIdleMs();
            if (!_running || ms < 0) return;
            long remain = _lastUseTicks + ms - Environment.TickCount64;
            if (remain > 0) { IdleTimer.Change(remain + 50, Timeout.Infinite); return; }   // 期间又被使用过：顺延
            KillLocked();
        }
        finally { Gate.Release(); }
    }

    public static void Shutdown()
    {
        IdleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        TrimTimer.Change(Timeout.Infinite, Timeout.Infinite);
        bool got = Gate.Wait(500);
        try { KillLocked(); }
        finally { if (got) Gate.Release(); }
    }
}
