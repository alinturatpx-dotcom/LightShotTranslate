using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LST;

/// <summary>
/// 非托管像素缓冲（BGR，行紧密排列，无 GDI+ 行对齐填充）。不进 GC 堆 / 大对象堆：
/// Dispose 后内存立刻归还系统，不需要靠 GC 回收，也就不必为大图手动 GC.Collect()。
/// </summary>
internal sealed unsafe class PixelBuffer : IDisposable
{
    private byte* _p;
    public int Length { get; }

    private PixelBuffer(byte* p, int length) { _p = p; Length = length; }

    /// <summary>在调用线程（UI 线程）做一次廉价的行拷贝；之后位图可以随意释放/重绘。</summary>
    public static PixelBuffer FromBitmap(Bitmap image)
    {
        int w = image.Width, h = image.Height, row = checked(w * 3), len = checked(row * h);
        var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte* p = (byte*)NativeMemory.Alloc((nuint)len);
        try
        {
            byte* src = (byte*)data.Scan0;
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * data.Stride, p + (long)y * row, row, row);
            return new PixelBuffer(p, len);
        }
        catch { NativeMemory.Free(p); throw; }
        finally { image.UnlockBits(data); }
    }

    /// <summary>分块写入目标流（不产生任何托管大数组）。</summary>
    public void WriteTo(Stream s)
    {
        if (_p == null) throw new ObjectDisposedException(nameof(PixelBuffer));
        using var ums = new UnmanagedMemoryStream(_p, Length);
        ums.CopyTo(s, 1 << 16);
    }

    public void Dispose()
    {
        var p = _p; _p = null;
        if (p != null) NativeMemory.Free(p);
        GC.SuppressFinalize(this);
    }

    ~PixelBuffer() => Dispose();   // 兜底：正常路径都会显式 Dispose
}
