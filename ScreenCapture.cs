using System.Drawing.Imaging;

namespace LST;

internal static class ScreenCapture
{
    /// <summary>
    /// 一次性截取整个虚拟桌面（所有显示器），必须在显示遮罩之前调用。
    /// 进程为 PerMonitorV2 感知，因此 VirtualScreen 与位图均为物理像素。
    /// </summary>
    public static Bitmap CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
