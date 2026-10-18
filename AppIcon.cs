namespace LST;

/// <summary>程序图标（嵌入资源 app.ico，多尺寸）。托盘用小图标，窗口用默认尺寸。</summary>
internal static class AppIcon
{
    private static Icon? _small, _window;

    public static Icon Small => _small ??= Load(SystemInformation.SmallIconSize);
    public static Icon Window => _window ??= Load(null);

    private static Icon Load(Size? size)
    {
        try
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
            if (s != null) return size is { } z ? new Icon(s, z) : new Icon(s);
        }
        catch { /* 回退到系统默认图标 */ }
        return (Icon)SystemIcons.Application.Clone();
    }
}
