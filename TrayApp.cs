using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LST;

/// <summary>托盘常驻：空闲时只有托盘图标 + 热键消息窗口，截图/OCR/翻译全部按需创建。</summary>
internal sealed class TrayApp : ApplicationContext, FloatBall.IBallHost
{
    private const int IdCapture = 1, IdOcr = 2, IdTranslate = 3, IdPin = 4;

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys = new();
    private readonly TranslationService _translator = new();
    private AppSettings _settings = AppSettings.Load();

    private SelectionOverlay? _overlay;
    private SettingsForm? _settingsForm;
    private FloatBall.AppCtx? _ball;   // AI 悬浮球模块（设置里可关闭；关闭时进程里完全不创建）

    // 空闲时把工作集换出：托盘程序平时几乎不碰这些页，任务管理器里的占用会降到很低
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = 15000 };
    private bool _activity = true;   // 启动后第一次空闲也 trim 一次
    private long _lastActivity = Environment.TickCount64;

    // 启动后空闲时预建设置窗口：第一次打开不必再承担控件创建 / 布局 / 首帧绘制的延迟
    private readonly System.Windows.Forms.Timer _warmTimer = new() { Interval = 1200 };

    public TrayApp()
    {
        OcrService.Configure(_settings.OcrKeepMode, _settings.OcrQuality);
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Small,
            Text = "LightShot Translate",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => StartCapture(CaptureAction.Copy);

        _hotkeys.Pressed += id => StartCapture(id switch
        {
            IdOcr => CaptureAction.Ocr,
            IdTranslate => CaptureAction.Translate,
            IdPin => CaptureAction.Pin,
            _ => CaptureAction.Copy
        });
        RegisterHotkeys();

        // AI 悬浮球是截图工具的附属功能：共用同一份 API 设置、同一个设置面板、同一个托盘
        FloatBall.Shared.Url = () => _settings.ApiBaseUrl;
        FloatBall.Shared.Mdl = () => _settings.Model;
        FloatBall.Shared.Key = () => _settings.ApiKey;
        try
        {
            _ball = new FloatBall.AppCtx(this);
            _ball.FillTrayMenu(_tray.ContextMenuStrip!, 4);   // 放在“贴图”之后
        }
        catch (Exception ex)
        {
            _ball = null;   // 悬浮球出错不能拖累截图功能
            Notify("AI 悬浮球启动失败（截图功能不受影响）：" + ex.Message, error: true);
        }

        _trimTimer.Tick += (_, _) =>
        {
            // 只看“可见”的窗口：设置窗口关闭后是隐藏复用的，不能一直阻止回收
            if (Application.OpenForms.Cast<Form>().Any(f => f.Visible && f.GetType().Namespace != "FloatBall")) { _activity = true; _lastActivity = Environment.TickCount64; return; }   // 有窗口在用：等它们关掉后再 trim
            if (!_activity) return;
            if (Environment.TickCount64 - _lastActivity < 45_000) return;   // 刚用完不 trim：下一次热键响应不吃缺页，空闲 45 秒后才换出
            _activity = false;
            MemoryTrim.TrimSelf();
        };
        _trimTimer.Start();

        _warmTimer.Tick += (_, _) =>
        {
            _warmTimer.Stop();
            _warmTimer.Dispose();
            long t = Perf.Now();
            try { EnsureSettingsForm().Prewarm(); } catch { /* 预热失败不影响正常打开 */ }
            if (Perf.Enabled) Perf.Log($"settings prewarmed: {Perf.Ms(t):F0} ms");
        };
        _warmTimer.Start();

        if (string.IsNullOrEmpty(_settings.ApiKey))
            Notify("已在托盘运行。使用截图翻译前，请在「设置」中填写 API Key。");
    }

    // ---------- 托盘与热键 ----------
    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("区域截图", null, (_, _) => StartCapture(CaptureAction.Copy));
        m.Items.Add("截图识字", null, (_, _) => StartCapture(CaptureAction.Ocr));
        m.Items.Add("截图翻译", null, (_, _) => StartCapture(CaptureAction.Translate));
        m.Items.Add("贴图", null, (_, _) => StartCapture(CaptureAction.Pin));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("打开保存目录", null, (_, _) => OpenSaveDir());
        m.Items.Add("设置…", null, (_, _) => ShowSettings());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (_, _) => ExitThread());
        return m;
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!_hotkeys.Register(IdCapture, _settings.HotkeyCapture)) failed.Add(_settings.HotkeyCapture);
        if (!_hotkeys.Register(IdOcr, _settings.HotkeyOcr)) failed.Add(_settings.HotkeyOcr);
        if (!_hotkeys.Register(IdTranslate, _settings.HotkeyTranslate)) failed.Add(_settings.HotkeyTranslate);
        if (!_hotkeys.Register(IdPin, _settings.HotkeyPin)) failed.Add(_settings.HotkeyPin);
        if (failed.Count > 0)
            Notify("以下快捷键注册失败（可能被其他软件占用）：" + string.Join("、", failed) + "。可在设置中更换。", error: true);
    }

    private SettingsForm EnsureSettingsForm()
    {
        if (_settingsForm is { IsDisposed: false }) return _settingsForm;
        _settingsForm = new SettingsForm(_settings, _ball, s =>
        {
            _settings = s;
            RegisterHotkeys();
            OcrService.Configure(s.OcrKeepMode, s.OcrQuality);
            _ball?.ApplySettings();
        });
        return _settingsForm;
    }

    private void ShowSettings()
    {
        var form = EnsureSettingsForm();
        if (form.Visible) { form.Activate(); return; }

        // 托盘菜单还在收起时直接 Show 会有可见的停顿/闪烁：等当前消息处理完再显示。
        // 窗口已预热，这里只刷新一次数据，打开是即时的。
        _ = form.Handle;   // BeginInvoke 需要句柄；首次打开早于预热时在这里创建
        form.BeginInvoke((Action)(() =>
        {
            if (form.IsDisposed) return;
            if (form.Visible) { form.Activate(); return; }
            long t = Perf.Now();
            form.Reload(_settings);
            form.CenterOnScreen();
            form.Show();
            form.Activate();
            if (Perf.Enabled) Perf.Log($"settings shown: {Perf.Ms(t):F0} ms after menu click");
        }));
    }

    // ---------- 截图流程 ----------
    private void StartCapture(CaptureAction defaultAction)
    {
        if (_overlay != null) return;
        Perf.HotkeyTick = Perf.Now();
        _activity = true; _lastActivity = Environment.TickCount64;
        // 明确要识字/翻译的热键：此刻就预热 OCR 子进程，冷启动延迟藏在用户选区的时间里
        if (defaultAction is CaptureAction.Ocr or CaptureAction.Translate) OcrService.Prewarm();
        try
        {
            // 先截屏、后显示遮罩，避免把遮罩截进去。枚举窗口（用于悬停自动框选）与截屏互不依赖，并行做
            var finderTask = Task.Run(WindowFinder.Capture);
            Bitmap shot; Rectangle bounds;
            try { shot = ScreenCapture.CaptureVirtualScreen(out bounds); }
            catch { _ = finderTask.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); throw; }
            if (Perf.Enabled) Perf.Log($"capture screen {bounds.Width}x{bounds.Height}: {Perf.Ms(Perf.HotkeyTick):F0} ms after hotkey");
            var finder = finderTask.GetAwaiter().GetResult();
            if (Perf.Enabled) Perf.Log($"window finder ready: {Perf.Ms(Perf.HotkeyTick):F0} ms after hotkey");
            var overlay = new SelectionOverlay(shot, bounds, defaultAction, finder);
            if (Perf.Enabled) Perf.Log($"overlay built: {Perf.Ms(Perf.HotkeyTick):F0} ms after hotkey");
            _overlay = overlay;
            overlay.FormClosed += (_, _) =>
            {
                _overlay = null;
                _ball?.CaptureEnded();
                if (overlay.ChosenAction is { } action && overlay.Cropped is { } crop)
                    _ = HandleAsync(action, crop, overlay.ScreenSelection);
            };
            overlay.Show();
        }
        catch (Exception ex)
        {
            _overlay = null;
            _ball?.CaptureEnded();
            Notify("截图失败：" + ex.Message, error: true);
        }
    }

    private async Task HandleAsync(CaptureAction action, Bitmap crop, Rectangle screenRect)
    {
        // 图片所有权约定：窗口构造函数【成功返回】之后才由窗口接管 crop（窗口 Dispose 时释放）；
        // 构造失败（抛异常）时仍由本方法的 finally 释放，不会泄漏。
        bool transferred = false;
        PinTranslateWindow? win = null;
        try
        {
            // 此方法由遮罩的 FormClosed 同步调用：先让出一小会儿，等遮罩真正消失、桌面重绘完，
            // 再做剪贴板渲染 / 写文件这些可能卡住 UI 线程的活（首次使用时尤其慢，虚拟机里会表现为黑屏闪一下）
            if (action is CaptureAction.Copy or CaptureAction.Save) await Task.Delay(60);

            switch (action)
            {
                case CaptureAction.Copy:
                    Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, crop), true, 5, 100);
                    break;
                case CaptureAction.Save:
                    {
                        var path = ChooseSavePath();
                        if (path == null) break;                      // 用户在“另存为”里取消
                        await Task.Run(() => SaveImage(crop, path));   // PNG 编码和写盘放到后台线程
                        break;
                    }
                case CaptureAction.Pin:
                case CaptureAction.Ocr:
                case CaptureAction.Translate:
                    // 贴图 / 识字 / 翻译共用“原位智能窗口”：先贴在原位置，识字标出文字，按 T 原位翻译
                    win = new PinTranslateWindow(crop, screenRect, _settings);
                    transferred = true;
                    win.Show();
                    await win.StartAsync(_translator, action switch
                    {
                        CaptureAction.Translate => PinMode.Translate,
                        CaptureAction.Ocr => PinMode.Ocr,
                        _ => PinMode.Pin
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            try { win?.Dispose(); } catch { }   // 窗口已构造但 Show 失败：Dispose 会连带释放它接管的图片
            Notify("操作失败：" + ex.Message, error: true);
        }
        finally
        {
            if (!transferred) crop.Dispose();
        }
    }

    /// <summary>点“保存”时弹出“另存为”：可改目录、文件名、格式。默认目录 = 上次保存的目录（首次为设置里的默认目录）。返回 null 表示取消。</summary>
    private string? ChooseSavePath()
    {
        string dir = _settings.SaveDirectory;
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        // 托盘程序没有前台窗口，用一个看不见的置顶窗口作为对话框的所有者，避免对话框跑到别的窗口后面
        using var owner = new Form
        {
            TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1)
        };
        owner.Show();
        using var dlg = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            FileName = $"LST_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = dir,
            OverwritePrompt = true,
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;

        var chosen = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(chosen) && !string.Equals(chosen, _settings.SaveDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _settings.SaveDirectory = chosen;   // 记住这次选的目录，下次从这里开始
            try { _settings.Save(); } catch { }
        }
        return dlg.FileName;
    }

    private static void SaveImage(Bitmap bmp, string path)
    {
        var fmt = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            _ => ImageFormat.Png
        };
        bmp.Save(path, fmt);
    }

    private void OpenSaveDir()
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
            System.Diagnostics.Process.Start("explorer.exe", _settings.SaveDirectory);
        }
        catch { }
    }

    // ---------- 杂项 ----------
    private void Notify(string text, bool error = false)
    {
        _tray.ShowBalloonTip(3000, "LightShot Translate", text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    // ---------- AI 悬浮球（IBallHost）----------
    void FloatBall.IBallHost.Capture(string kind) => StartCapture(kind switch
    {
        "ocr" => CaptureAction.Ocr,
        "translate" => CaptureAction.Translate,
        "pin" => CaptureAction.Pin,
        _ => CaptureAction.Copy
    });

    void FloatBall.IBallHost.ShowSettings() => ShowSettings();

    void FloatBall.IBallHost.Notify(string title, string text, bool warn) =>
        _tray.ShowBalloonTip(3000, title, text, warn ? ToolTipIcon.Warning : ToolTipIcon.Info);

    void FloatBall.IBallHost.ExitAll() => ExitThread();

    ContextMenuStrip FloatBall.IBallHost.TrayMenu => _tray.ContextMenuStrip!;

    protected override void ExitThreadCore()
    {
        try { _ball?.Shutdown(); } catch { }
        try { _settingsForm?.Dispose(); } catch { }
        _warmTimer.Dispose();
        _trimTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        OcrService.Shutdown();
        base.ExitThreadCore();
    }
}
