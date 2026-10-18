using System.Diagnostics;

namespace LST;

/// <summary>
/// 轻量性能追踪：设置环境变量 LST_PERF=1 后，把各阶段耗时追加到 %TEMP%\lst-perf.log。
/// 先测量再优化——热键到遮罩出现、OCR 排队/打包/往返的真实耗时都从这里取。未开启时开销为一次布尔判断。
/// </summary>
internal static class Perf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("LST_PERF") == "1";
    private static readonly object Lock = new();

    /// <summary>最近一次按下截图热键的时刻（Stopwatch 时间戳）。</summary>
    public static long HotkeyTick;

    public static long Now() => Stopwatch.GetTimestamp();
    public static double Ms(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

    public static void Log(string message)
    {
        if (!Enabled) return;
        lock (Lock)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "lst-perf.log"), $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
