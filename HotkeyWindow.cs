using System.Runtime.InteropServices;

namespace LST;

/// <summary>用隐藏窗口接收 WM_HOTKEY：系统热键消息，不轮询，空闲 CPU≈0。</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly List<int> _ids = new();
    public event Action<int>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    /// <summary>spec 例如 "Alt+A"、"Ctrl+Shift+F1"。成功返回 true。</summary>
    public bool Register(int id, string spec)
    {
        if (!TryParse(spec, out uint mod, out uint vk)) return false;
        if (!RegisterHotKey(Handle, id, mod | MOD_NOREPEAT, vk)) return false;
        _ids.Add(id);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids) UnregisterHotKey(Handle, id);
        _ids.Clear();
    }

    public static bool TryParse(string spec, out uint mod, out uint vk)
    {
        mod = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var parts = spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;           // 至少一个修饰键 + 一个主键，避免吞掉普通按键

        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "alt": mod |= MOD_ALT; break;
                case "ctrl": case "control": mod |= MOD_CONTROL; break;
                case "shift": mod |= MOD_SHIFT; break;
                case "win": mod |= MOD_WIN; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Keys>(parts[^1], ignoreCase: true, out var key)) return false;
        vk = (uint)key;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
