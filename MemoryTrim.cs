using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LST;

/// <summary>
/// 把当前进程暂时用不到的页换出工作集（任务管理器里的“内存”列会立刻降下来，需要时自动换回）。
/// 托盘常驻程序空闲时调用；OCR 子进程每次识别结束后调用。
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>换出另一个进程（我们启动的 OCR 子进程）的工作集。</summary>
    public static void TrimProcess(Process p)
    {
        try { EmptyWorkingSet(p.Handle); } catch { }
    }

    public static void TrimSelf()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { }
    }
}
