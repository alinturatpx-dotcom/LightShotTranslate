using System.Runtime.InteropServices;
using System.Text;

namespace LST;

/// <summary>
/// 智能选区：截图瞬间记录所有可见顶层窗口；鼠标移动时返回“指针下由小到大”的候选区域链
/// （最内层控件 → … → 窗口 → 整个显示器），滚轮可在链上切换范围。坐标均为物理像素（虚拟桌面坐标）。
/// </summary>
internal sealed class WindowFinder
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWnd, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;

    private readonly List<(IntPtr Hwnd, Rectangle Rect)> _windows = new();
    // 截图是冻结画面，窗口布局以截图瞬间为准：每个窗口的子控件只枚举一次，之后鼠标移动只做内存过滤
    private readonly Dictionary<IntPtr, Rectangle[]> _childCache = new();
    private Rectangle[] _monitors = Array.Empty<Rectangle>();
    private readonly StringBuilder _cls = new(64);

    /// <summary>必须在显示遮罩之前调用。</summary>
    public static WindowFinder Capture()
    {
        var f = new WindowFinder();
        f._monitors = Screen.AllScreens.Select(s => s.Bounds).ToArray();
        EnumWindows((h, _) =>
        {
            if (f.TryGetRect(h, out var r)) f._windows.Add((h, r));
            return true;   // EnumWindows 按 Z 序从上到下枚举
        }, IntPtr.Zero);
        return f;
    }

    private bool TryGetRect(IntPtr h, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        if (!IsWindowVisible(h) || IsIconic(h)) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return false;   // 点击穿透的透明窗口（桌宠等）

        _cls.Clear();
        GetClassName(h, _cls, _cls.Capacity);
        var cls = _cls.ToString();
        if (cls is "Progman" or "WorkerW") return false;                              // 桌面本身

        RECT rc;
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out rc, Marshal.SizeOf<RECT>()) != 0 &&
            !GetWindowRect(h, out rc)) return false;
        rect = Rectangle.FromLTRB(rc.L, rc.T, rc.R, rc.B);
        return rect.Width > 8 && rect.Height > 8;
    }

    /// <summary>返回指针处的候选区域，由小到大，至少包含所在显示器。</summary>
    public List<Rectangle> GetChain(Point p)
    {
        var chain = new List<Rectangle>();

        foreach (var (hwnd, wr) in _windows)
        {
            if (!wr.Contains(p)) continue;

            var kids = new List<Rectangle>();
            foreach (var cr in GetChildRects(hwnd, wr))
                if (cr.Contains(p)) kids.Add(cr);

            foreach (var k in kids.OrderBy(Area)) AddIfLarger(chain, k);
            AddIfLarger(chain, wr);
            break;   // 最上层包含该点的窗口即为目标
        }

        AddIfLarger(chain, MonitorAt(p));
        return chain;
    }

    private Rectangle[] GetChildRects(IntPtr hwnd, Rectangle wr)
    {
        if (_childCache.TryGetValue(hwnd, out var cached)) return cached;
        var list = new List<Rectangle>();
        EnumChildWindows(hwnd, (c, _) =>
        {
            if (IsWindowVisible(c) && GetWindowRect(c, out var cr))
            {
                var rr = Rectangle.Intersect(Rectangle.FromLTRB(cr.L, cr.T, cr.R, cr.B), wr);
                if (rr.Width >= 8 && rr.Height >= 8) list.Add(rr);
            }
            return true;
        }, IntPtr.Zero);
        var arr = list.ToArray();
        _childCache[hwnd] = arr;
        return arr;
    }

    private Rectangle MonitorAt(Point p)
    {
        foreach (var m in _monitors) if (m.Contains(p)) return m;
        return _monitors.Length > 0 ? _monitors[0] : Screen.FromPoint(p).Bounds;
    }

    private static long Area(Rectangle r) => (long)r.Width * r.Height;

    private static void AddIfLarger(List<Rectangle> chain, Rectangle r)
    {
        if (chain.Count == 0 || Area(r) > Area(chain[^1])) chain.Add(r);
    }
}
