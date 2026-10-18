using System.IO.Pipes;
using System.Text;

namespace LST;

/// <summary>
/// OCR 工作进程入口（--ocr-worker）。只有这个进程会加载 ONNX Runtime 和模型。
/// 协议（小端）：请求 = Magic,w,h,len,BGR 像素；响应 = 0,n,n×(x,y,w,h,score,text) 或 1,错误信息。
/// 父进程关闭管道（或退出）后读到 EOF，自行退出。
/// </summary>
internal static class OcrWorker
{
    public const string Arg = "--ocr-worker";
    private const int Magic = 0x3254534C;
    private const float MinScore = 0.5f;

    // args: --ocr-worker <父→子管道句柄> <子→父管道句柄> <推理线程数> <单次检测像素预算>
    public static int Run(string[] a)
    {
        int threads = int.Parse(a[3]);
        int detPixels = int.Parse(a[4]);
        bool log = Environment.GetEnvironmentVariable("LST_OCR_LOG") == "1";   // 排查内存时设置，见 README
        OnnxOcrEngine? engine = null;
        try
        {
            using var inp = new AnonymousPipeClientStream(PipeDirection.In, a[1]);
            using var outp = new AnonymousPipeClientStream(PipeDirection.Out, a[2]);
            using var br = new BinaryReader(inp, Encoding.UTF8, leaveOpen: true);
            using var bw = new BinaryWriter(outp, Encoding.UTF8, leaveOpen: true);

            while (true)
            {
                int magic;
                try { magic = br.ReadInt32(); } catch (EndOfStreamException) { break; }
                if (magic != Magic) break;
                int w = br.ReadInt32(), h = br.ReadInt32(), len = br.ReadInt32();
                byte[] buf = br.ReadBytes(len);
                bool warm = w == 0 && h == 0 && len == 0;   // 预热请求（0×0 空帧）
                if (buf.Length != len || (!warm && len != w * h * 3)) break;

                byte[] reply;
                try
                {
                    engine ??= new OnnxOcrEngine(Path.Combine(AppContext.BaseDirectory, "models"), threads, detPixels);
                    if (warm) { engine.Warmup(); reply = Encode(new List<OnnxOcrEngine.Line>()); }
                    else reply = Encode(engine.Run(buf, w, h, MinScore));
                }
                catch (Exception ex) { reply = Error(Describe(ex)); }
                buf = Array.Empty<byte>();
                bw.Write(reply);
                bw.Flush();

                if (log) LogPeak(w, h);
            }
        }
        catch (IOException) { /* 父进程已退出 */ }
        finally { engine?.Dispose(); }
        return 0;
    }

    private static void LogPeak(int w, int h)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "lst-ocr.log"),
                $"{DateTime.Now:HH:mm:ss}  {w}x{h}  峰值工作集 {p.PeakWorkingSet64 / 1048576} MB  峰值私有 {p.PeakPagedMemorySize64 / 1048576} MB{Environment.NewLine}");
        }
        catch { }
    }

    private static string Describe(Exception ex) => ex is DllNotFoundException or BadImageFormatException or TypeInitializationException
        ? "ONNX Runtime 原生库加载失败。请确认已安装 VC++ 运行库（vc_redist.x64）且程序以 x64 运行。详情：" + ex.Message
        : ex.Message;

    private static byte[] Error(string msg)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { w.Write((byte)1); w.Write(msg); }
        return ms.ToArray();
    }

    private static byte[] Encode(List<OnnxOcrEngine.Line> lines)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            bw.Write((byte)0);
            bw.Write(lines.Count);
            foreach (var l in lines)
            {
                bw.Write(l.Box.X); bw.Write(l.Box.Y); bw.Write(l.Box.Width); bw.Write(l.Box.Height);
                bw.Write(l.Score); bw.Write(l.Text);
            }
        }
        return ms.ToArray();
    }
}
