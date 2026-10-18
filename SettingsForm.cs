using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace LST;

/// <summary>
/// 统一设置面板：左侧导航 + 右侧卡片式表单。
/// 窗口只创建一次，关闭时隐藏；再次打开只刷新数据，避免重复构建控件带来的卡顿与闪烁。
/// </summary>
internal sealed class SettingsForm : Form
{
    // ---------- 配色 / 字体 ----------
    private static readonly Color PageBg = Color.FromArgb(0xF6, 0xF7, 0xF9);
    private static readonly Color SideBg = Color.FromArgb(0xF1, 0xF3, 0xF6);
    private static readonly Color CardBg = Color.White;
    private static readonly Color Line = Color.FromArgb(0xE4, 0xE7, 0xEC);
    private static readonly Color Ink = Color.FromArgb(0x1F, 0x23, 0x29);
    private static readonly Color Sub = Color.FromArgb(0x7B, 0x82, 0x8C);
    private static readonly Color Accent = Color.FromArgb(0x3B, 0x82, 0xF6);
    private static readonly Color AccentSoft = Color.FromArgb(0xEA, 0xF1, 0xFE);
    private static readonly Color HoverBg = Color.FromArgb(0xE7, 0xEB, 0xF1);
    private static readonly Color OkGreen = Color.FromArgb(0x2E, 0x9E, 0x5B);
    private static readonly Color ErrRed = Color.FromArgb(0xD1, 0x43, 0x43);

    private static readonly Font UiFont = new("Microsoft YaHei UI", 9f);
    private static readonly Font NavFont = new("Microsoft YaHei UI", 9.5f);
    private static readonly Font NavBoldFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private static readonly Font BrandFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    private static readonly Font PageTitleFont = new("Microsoft YaHei UI", 14f, FontStyle.Bold);
    private static readonly Font CardTitleFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private static readonly Font SmallFont = new("Microsoft YaHei UI", 8.5f);

    // ---------- 数据 ----------
    private AppSettings _s;
    private readonly FloatBall.AppCtx? _ball;
    private readonly FloatBall.Cfg? _bc;
    private readonly Action<AppSettings> _onSaved;

    private static readonly string[][] Presets =
    {
        new[] { "OpenAI", "https://api.openai.com/v1" },
        new[] { "DeepSeek", "https://api.deepseek.com/v1" },
        new[] { "硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1" },
        new[] { "Moonshot (Kimi)", "https://api.moonshot.cn/v1" },
        new[] { "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4" },
        new[] { "阿里云百炼 (DashScope)", "https://dashscope.aliyuncs.com/compatible-mode/v1" },
        new[] { "OpenRouter", "https://openrouter.ai/api/v1" },
        new[] { "Ollama 本地", "http://localhost:11434/v1" },
    };

    private static readonly string[] MergeKeys = { "Off", "Strict", "Standard", "Loose" };
    private static readonly string[] KeepKeys = { "Light", "Balanced", "Resident" };
    private static readonly string[] QualityKeys = { "Memory", "Balanced", "Accuracy" };

    // ---------- 通用 / API ----------
    private readonly ComboBox _preset = new WheelCombo { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _baseUrl = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _apiKey = new() { BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true };
    private readonly TextBox _model = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _target = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _autoStart = NewCheck("开机自动启动");
    private readonly Label _testResult = new()
    {
        AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Sub,
        BackColor = CardBg, Margin = new Padding(10, 7, 0, 0)
    };
    private readonly FlatButton _test = new("测试连接", true) { Width = 112, Height = 32, Outline = true };

    // ---------- 截图 / OCR ----------
    private readonly TextBox _hkCapture = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _hkOcr = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _hkTranslate = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _hkPin = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _saveDir = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly ComboBox _merge = new WheelCombo { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _keep = new WheelCombo { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _quality = new WheelCombo { DropDownStyle = ComboBoxStyle.DropDownList };

    // ---------- AI 悬浮球 / 划词 ----------
    private readonly CheckBox _ballShow = NewCheck("显示悬浮球");
    private readonly CheckBox _selBar = NewCheck("划词工具栏（选中文字后弹出「翻译 / 提问 / 复制」）");
    private readonly CheckBox _dark = NewCheck("深色主题（对话面板）");
    private readonly CheckBox _anim = NewCheck("悬浮球动效");
    private readonly CheckBox _stream = NewCheck("流式输出");
    private readonly CheckBox _saveHist = NewCheck("保存聊天记录（最近 60 条）");
    private readonly TextBox _hkToggle = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _hkAsk = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _hkGrabTr = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _sysPrompt = new()
    {
        BorderStyle = BorderStyle.None, Multiline = true,
        ScrollBars = ScrollBars.None, AcceptsReturn = true, WordWrap = true
    };
    private readonly NumericUpDown _ballSize = new WheelNumeric { Minimum = 44, Maximum = 96, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Left };
    private readonly NumericUpDown _idleOpacity = new WheelNumeric { Minimum = 30, Maximum = 100, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Left };
    private readonly NumericUpDown _maxCtx = new WheelNumeric { Minimum = 2, Maximum = 40, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Left };
    private readonly Label _histInfo = new()
    {
        AutoSize = true, Font = SmallFont, ForeColor = Sub, BackColor = CardBg, MaximumSize = new Size(520, 0)
    };
    private readonly FlatButton _viewHistoryBtn = new("查看记录", false) { Width = 100, Height = 32, Margin = Padding.Empty };
    private readonly FlatButton _exportHistoryBtn = new("导出记录", false) { Width = 100, Height = 32, Margin = new Padding(8, 0, 0, 0) };

    // ---------- 外壳 ----------
    private readonly NavList _nav = new() { Dock = DockStyle.Fill };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = PageBg };
    private readonly PagePanel?[] _pages = new PagePanel?[3];
    private readonly FlatButton _saveBtn = new("保存", true) { Width = 96, Height = 34 };
    private readonly FlatButton _cancelBtn = new("取消", false) { Width = 96, Height = 34, Margin = new Padding(8, 0, 0, 0) };

    private int _currentPage = -1;
    private bool _warming, _allowClose, _saving, _prewarmed;
    private int _saveGen, _testGen;

    public SettingsForm(AppSettings current, FloatBall.AppCtx? ball, Action<AppSettings> onSaved)
    {
        _s = current.Clone();
        _ball = ball;
        _bc = ball?.Cfg;
        _onSaved = onSaved;

        Text = "LightShot Translate 设置";
        Icon = AppIcon.Window;
        Font = UiFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 700);
        BackColor = PageBg;
        DoubleBuffered = true;

        AddComboItems();
        BindValues();

        _nav.Items = _bc == null
            ? new[] { "通用与 API", "截图与 OCR" }
            : new[] { "通用与 API", "截图与 OCR", "AI 悬浮球" };
        _nav.SelectedIndexChanged += (_, _) => SelectPage(_nav.SelectedIndex);

        ConfigureControls();
        BuildShell();

        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_preset.SelectedIndex > 0) _baseUrl.Text = Presets[_preset.SelectedIndex - 1][1];
        };
        _test.Click += (_, _) => _ = TestConnectionAsync();
        _cancelBtn.Click += (_, _) => { _saveGen++; Hide(); };
        _saveBtn.Click += (_, _) => SaveAsync();

        AcceptButton = _saveBtn;
        CancelButton = _cancelBtn;

        _nav.SelectedIndex = 0;
    }

    // ======================================================================
    // 对外：打开 / 刷新 / 预热
    // ======================================================================

    /// <summary>把最新配置刷新到已创建的控件上（窗口隐藏期间配置可能被别处修改）。</summary>
    public void Reload(AppSettings current)
    {
        _s = current.Clone();
        BindValues();
        _testGen++;
        _saveGen++;
        _saving = false;
        _saveBtn.Enabled = true;
    }

    /// <summary>把窗口居中到当前鼠标所在的显示器。</summary>
    public void CenterOnScreen()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
    }

    /// <summary>启动后在空闲时调用：建好全部页面与窗口句柄，使第一次打开也没有构建延迟。</summary>
    public void Prewarm()
    {
        if (_prewarmed || _warming || _allowClose || IsDisposed) return;
        _prewarmed = true;
        for (int i = 0; i < _nav.Items.Length; i++) EnsurePage(i);
        if (Visible) return;   // 用户在预热前已经自己打开了设置：控件已建好，不要再挪窗口
        try
        {
            _warming = true;
            Opacity = 0;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            _ = Handle;
            Show();
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                Hide();
                _warming = false;
                Opacity = 1;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.CenterScreen;
            }));
        }
        catch
        {
            _warming = false;
            try { Hide(); } catch { }
            Opacity = 1;
            ShowInTaskbar = true;
        }
    }

    /// <summary>退出程序时真正关闭窗口。</summary>
    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    protected override bool ShowWithoutActivation => _warming;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible || _warming) return;
        // 每次打开都把焦点放在分类导航上，而不是让第一个下拉框处于高亮状态
        BeginInvoke((Action)(() =>
        {
            if (Visible && !IsDisposed) _nav.Focus();
        }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            _saveGen++;
            _saving = false;
            _saveBtn.Enabled = true;
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // ======================================================================
    // 外壳：侧边栏 / 底部按钮 / 页面切换
    // ======================================================================

    private void BuildShell()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = PageBg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        Controls.Add(root);

        var side = BuildSidebar();
        root.Controls.Add(side, 0, 0);
        root.SetRowSpan(side, 2);
        root.Controls.Add(_host, 1, 0);
        root.Controls.Add(BuildFooter(), 1, 1);
    }

    private Control BuildSidebar()
    {
        var navH = _nav.Items.Length * 48 + 10;
        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = SideBg };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, navH));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        side.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, side.Width - 1, 0, side.Width - 1, side.Height);
        };

        var brand = new Panel { Dock = DockStyle.Fill, BackColor = SideBg, Margin = Padding.Empty };
        var icon = new PictureBox
        {
            Image = AppIcon.Window.ToBitmap(), SizeMode = PictureBoxSizeMode.StretchImage,
            Location = new Point(20, 21), Size = new Size(32, 32), BackColor = SideBg
        };
        brand.Controls.Add(icon);
        brand.Controls.Add(new Label
        {
            Text = "LightShot", Font = BrandFont, ForeColor = Ink, AutoSize = true,
            Location = new Point(62, 18), BackColor = SideBg
        });
        brand.Controls.Add(new Label
        {
            Text = "Translate · 设置", Font = SmallFont, ForeColor = Sub, AutoSize = true,
            Location = new Point(63, 42), BackColor = SideBg
        });
        side.Controls.Add(brand, 0, 0);
        side.Controls.Add(_nav, 0, 1);

        var ver = typeof(SettingsForm).Assembly.GetName().Version;
        side.Controls.Add(new Label
        {
            Text = ver == null ? "v0.7.0" : $"v{ver.Major}.{ver.Minor}.{ver.Build}",
            Font = SmallFont, ForeColor = Sub, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(22, 0, 0, 6), BackColor = SideBg
        }, 0, 3);
        return side;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = CardBg, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(24, 0, 20, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, 0.5f, footer.Width, 0.5f);
        };
        footer.Controls.Add(new Label
        {
            Text = "修改后点击「保存」生效", Font = SmallFont, ForeColor = Sub, BackColor = CardBg,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        }, 0, 0);
        var bar = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, BackColor = CardBg, Margin = Padding.Empty, Padding = Padding.Empty,
            Anchor = AnchorStyles.Right
        };
        bar.Controls.Add(_cancelBtn);
        bar.Controls.Add(_saveBtn);
        footer.Controls.Add(bar, 1, 0);
        return footer;
    }

    private PagePanel? EnsurePage(int index)
    {
        if (index < 0 || index >= _nav.Items.Length) return null;
        if (_pages[index] is { } built) return built;
        var page = index switch
        {
            0 => BuildGeneralPage(),
            1 => BuildCapturePage(),
            _ => BuildBallPage()
        };
        page.Visible = false;
        _pages[index] = page;
        _host.Controls.Add(page);
        return page;
    }

    private void SelectPage(int index)
    {
        if (index < 0 || index == _currentPage) return;
        var page = EnsurePage(index);
        if (page == null) return;
        _host.SuspendLayout();
        if (_currentPage >= 0 && _pages[_currentPage] is { } old) old.Visible = false;
        _currentPage = index;
        page.Visible = true;
        page.BringToFront();
        _host.ResumeLayout(true);
        if (index == 2) RefreshHistoryInfo();   // 切回 AI 页时刷新记录条数
    }

    // ======================================================================
    // 页面与卡片
    // ======================================================================

    private PagePanel BuildGeneralPage()
    {
        var page = NewPage("通用与 API", "截图翻译与 AI 悬浮球共用这一份 API 配置。", out var stack);

        var api = NewCard("API 服务");
        AddRow(api, "服务商预设", _preset);
        AddRow(api, "API 地址", _baseUrl);
        AddRow(api, "API Key", _apiKey);
        AddRow(api, "模型", _model);
        AddRow(api, "目标语言", _target);
        var testRow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = CardBg, Margin = Padding.Empty
        };
        testRow.Controls.Add(_test);
        testRow.Controls.Add(_testResult);
        AddRow(api, "连接测试", testRow);
        AddNote(api, "API 地址 / Key / 模型只有这一份：截图翻译和 AI 悬浮球共用。\r\n" +
                     "Key 使用 Windows DPAPI 加密保存，仅当前用户可读；翻译 / 提问的文字会发送到上面的 API 地址。");
        AddStack(stack, api);

        var startup = NewCard("启动");
        AddFull(startup, _autoStart, last: true, stretch: false);
        AddStack(stack, startup);
        return page;
    }

    private PagePanel BuildCapturePage()
    {
        var page = NewPage("截图与 OCR", "快捷键、识别性能与截图保存位置。", out var stack);

        var keys = NewCard("快捷键");
        AddRow(keys, "截图", _hkCapture);
        AddRow(keys, "截图识字", _hkOcr);
        AddRow(keys, "截图翻译", _hkTranslate);
        AddRow(keys, "贴图", _hkPin, last: true);
        AddNote(keys, "格式：Alt+A、Ctrl+Shift+F1（需至少一个修饰键），所有快捷键不能重复。");
        AddStack(stack, keys);

        var ocr = NewCard("OCR 与翻译");
        AddRow(ocr, "OCR 模型驻留", _keep);
        AddRow(ocr, "OCR 识别内存", _quality);
        AddRow(ocr, "翻译段落合并", _merge, last: true);
        AddNote(ocr, "识别内存只影响大图的速度与占用，不影响识别精度。");
        AddStack(stack, ocr);

        var save = NewCard("保存");
        var dirPanel = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1,
            BackColor = CardBg, Margin = Padding.Empty
        };
        dirPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _saveDir.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var browse = new FlatButton("浏览…", false) { Width = 82, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _saveDir.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _saveDir.Text = dlg.SelectedPath;
        };
        dirPanel.Controls.Add(_saveDir, 0, 0);
        dirPanel.Controls.Add(browse, 1, 0);
        AddRow(save, "默认保存目录", dirPanel, last: true);
        AddStack(stack, save);
        return page;
    }

    private PagePanel BuildBallPage()
    {
        var page = NewPage("AI 悬浮球", "桌面悬浮球、划词工具栏与对话面板。", out var stack);

        var vis = NewCard("显示");
        AddFull(vis, _ballShow, stretch: false);
        AddFull(vis, _selBar, last: true, stretch: false);
        AddNote(vis, "划词工具栏只在你点按钮时才读取选中的文字；终端类窗口自动忽略。");
        AddStack(stack, vis);

        var keys = NewCard("快捷键");
        AddRow(keys, "呼出 / 隐藏面板", _hkToggle);
        AddRow(keys, "取词提问", _hkAsk);
        AddRow(keys, "取词翻译", _hkGrabTr, last: true);
        AddNote(keys, "取词快捷键：先选中文字，再按快捷键。");
        AddStack(stack, keys);

        var look = NewCard("外观与行为");
        AddRow(look, "悬浮球大小", NumWithSuffix(_ballSize, "px"));
        AddRow(look, "闲置透明度", NumWithSuffix(_idleOpacity, "%"));
        AddRow(look, "问答上下文", NumWithSuffix(_maxCtx, "条"));
        var toggles = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2,
            BackColor = CardBg, Margin = new Padding(0, 2, 0, 0)
        };
        toggles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        toggles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _dark.Margin = new Padding(0, 4, 12, 6);
        _anim.Margin = new Padding(0, 4, 0, 6);
        _stream.Margin = new Padding(0, 0, 12, 4);
        _saveHist.Margin = new Padding(0, 0, 0, 4);
        toggles.Controls.Add(_dark, 0, 0);
        toggles.Controls.Add(_anim, 1, 0);
        toggles.Controls.Add(_stream, 0, 1);
        toggles.Controls.Add(_saveHist, 1, 1);
        AddFull(look, toggles, last: true);
        AddStack(stack, look);

        var prompt = NewCard("系统提示词");
        AddFull(prompt, new TextShell(_sysPrompt) { Height = 116 }, last: true);
        AddNote(prompt, "这段提示词会随每次提问一起发送给模型；内容较长时可用滚轮浏览。");
        AddStack(stack, prompt);

        var data = NewCard("对话记录");
        AddFull(data, _histInfo, stretch: false);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = CardBg, Margin = Padding.Empty
        };
        _viewHistoryBtn.Click += (_, _) =>
        {
            using var viewer = new HistoryForm(_ball!.SnapshotChat(), _ball.SnapshotTranslate());
            viewer.ShowDialog(this);
        };
        _exportHistoryBtn.Click += (_, _) =>
            ExportHistory(this, _ball!.SnapshotChat(), _ball.SnapshotTranslate());
        var clear = new FlatButton("清空聊天记录", false) { Width = 128, Height = 32, Margin = new Padding(8, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "清空问答和翻译的全部聊天记录？", "清空聊天记录",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            {
                _ball?.ClearHistory();
                RefreshHistoryInfo();
            }
        };
        actions.Controls.Add(_viewHistoryBtn);
        actions.Controls.Add(_exportHistoryBtn);
        actions.Controls.Add(clear);
        AddFull(data, actions, last: true, stretch: false);
        AddStack(stack, data);
        return page;
    }

    private static PagePanel NewPage(string title, string subtitle, out TableLayoutPanel stack)
    {
        var page = new PagePanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageBg,
            Padding = new Padding(26, 22, 26, 18)
        };
        stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, BackColor = PageBg, Margin = Padding.Empty, Padding = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(stack);

        var head = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            BackColor = PageBg, Margin = new Padding(2, 0, 0, 18), Padding = Padding.Empty
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.Controls.Add(new Label
        {
            Text = title, Font = PageTitleFont, ForeColor = Ink, AutoSize = true,
            BackColor = PageBg, Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);
        head.Controls.Add(new Label
        {
            Text = subtitle, Font = UiFont, ForeColor = Sub, AutoSize = true,
            MaximumSize = new Size(520, 0), BackColor = PageBg, Margin = Padding.Empty
        }, 0, 1);
        AddStack(stack, head);
        return page;
    }

    private static CardPanel NewCard(string title)
    {
        var card = new CardPanel
        {
            ColumnCount = 2, RowCount = 0, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(18, 14, 18, 4), BackColor = CardBg
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int r = card.RowCount++;
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = title, Font = CardTitleFont, ForeColor = Ink, AutoSize = true,
            BackColor = CardBg, Margin = new Padding(0, 0, 0, 12)
        };
        card.Controls.Add(label, 0, r);
        card.SetColumnSpan(label, 2);
        return card;
    }

    private static void AddStack(TableLayoutPanel stack, Control c)
    {
        int r = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Top;
        stack.Controls.Add(c, 0, r);
    }

    private static void AddRow(CardPanel card, string label, Control editor, bool last = false)
    {
        int r = card.RowCount++;
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(new Label
        {
            Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Sub,
            BackColor = CardBg, Margin = new Padding(0, 4, 12, last ? 4 : 10)
        }, 0, r);
        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        editor.Margin = new Padding(0, 0, 0, last ? 4 : 10);
        card.Controls.Add(editor, 1, r);
    }

    private static void AddFull(CardPanel card, Control ctl, bool last = false, bool stretch = true)
    {
        int r = card.RowCount++;
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ctl.Anchor = stretch ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
        ctl.Margin = new Padding(0, 0, 0, last ? 4 : 10);
        card.Controls.Add(ctl, 0, r);
        card.SetColumnSpan(ctl, 2);
    }

    private static void AddNote(CardPanel card, string text)
    {
        var note = new Label
        {
            Text = text, AutoSize = true, Font = SmallFont, ForeColor = Sub, BackColor = CardBg,
            MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 8)
        };
        AddFull(card, note, last: true, stretch: false);
    }

    private static FlowLayoutPanel NumWithSuffix(NumericUpDown num, string suffix)
    {
        num.Width = 104;
        num.Margin = new Padding(0, 0, 8, 0);
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = CardBg, Margin = Padding.Empty
        };
        row.Controls.Add(num);
        row.Controls.Add(new Label
        {
            Text = suffix, AutoSize = true, ForeColor = Sub, BackColor = CardBg,
            Margin = new Padding(0, 6, 0, 0)
        });
        return row;
    }

    // ======================================================================
    // 数据绑定 / 控件样式
    // ======================================================================

    private void AddComboItems()
    {
        _preset.Items.Add("（选择服务商预设）");
        foreach (var p in Presets) _preset.Items.Add(p[0]);
        _merge.Items.AddRange(new object[]
        {
            "关闭（逐行翻译）", "严格（少合并）", "标准", "宽松（多合并）"
        });
        _keep.Items.AddRange(new object[]
        {
            "极致轻量（空闲 30 秒释放）", "平衡（空闲 5 分钟释放）", "性能优先（常驻内存）"
        });
        _quality.Items.AddRange(new object[]
        {
            "省内存（默认，大图识别稍慢）", "平衡", "高速（识别时占用更高）"
        });
    }

    private void ConfigureControls()
    {
        _preset.Margin = new Padding(0, 0, 0, 10);
        foreach (var cb in new[] { _preset, _merge, _keep, _quality }) cb.BackColor = CardBg;
        _test.Margin = new Padding(0, 2, 0, 0);
        _sysPrompt.Font = UiFont;
        _sysPrompt.BackColor = CardBg;
        _saveDir.Font = UiFont;
    }

    private void BindValues()
    {
        int pi = 0;
        for (int i = 0; i < Presets.Length; i++)
            if (string.Equals(Presets[i][1], _s.ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                pi = i + 1;
                break;
            }
        _preset.SelectedIndex = pi;
        _baseUrl.Text = _s.ApiBaseUrl;
        _apiKey.Text = _s.ApiKey;
        _model.Text = _s.Model;
        _target.Text = _s.TargetLanguage;
        _autoStart.Checked = _s.AutoStart;

        _saveDir.Text = _s.SaveDirectory;
        _merge.SelectedIndex = Idx(MergeKeys, _s.MergeMode, 2);
        _keep.SelectedIndex = Idx(KeepKeys, _s.OcrKeepMode, 1);
        _quality.SelectedIndex = Idx(QualityKeys, _s.OcrQuality, 0);
        _hkCapture.Text = _s.HotkeyCapture;
        _hkOcr.Text = _s.HotkeyOcr;
        _hkTranslate.Text = _s.HotkeyTranslate;
        _hkPin.Text = _s.HotkeyPin;

        if (_bc != null)
        {
            _ballShow.Checked = _bc.BallVisible;
            _selBar.Checked = _bc.SelBar;
            _dark.Checked = _bc.Dark;
            _anim.Checked = _bc.Animate;
            _stream.Checked = _bc.Stream;
            _saveHist.Checked = _bc.SaveHistory;
            _ballSize.Value = Math.Clamp(_bc.BallSize, (int)_ballSize.Minimum, (int)_ballSize.Maximum);
            _idleOpacity.Value = Math.Clamp(_bc.IdleOpacity, (int)_idleOpacity.Minimum, (int)_idleOpacity.Maximum);
            _maxCtx.Value = Math.Clamp(_bc.MaxContext, (int)_maxCtx.Minimum, (int)_maxCtx.Maximum);
            _hkToggle.Text = _bc.HotkeyToggle;
            _hkAsk.Text = _bc.HotkeyGrab;
            _hkGrabTr.Text = _bc.HotkeyTranslate;
            _sysPrompt.Text = _bc.SystemPrompt;
        }

        _testResult.ForeColor = Sub;
        _testResult.Text = "";
        _test.Enabled = true;
        RefreshHistoryInfo();
    }

    private void RefreshHistoryInfo()
    {
        if (_ball == null) return;
        int qa = _ball.ChatCount, tr = _ball.TranslateCount;
        _histInfo.Text = qa + tr == 0
            ? "暂无记录（仅保存在本机）"
            : $"问答 {qa} 条 · 翻译 {tr} 条（仅保存在本机）";
        _exportHistoryBtn.Enabled = qa + tr > 0;
    }

    private static int Idx(string[] keys, string value, int fallback)
    {
        int i = Array.IndexOf(keys, value);
        return i >= 0 ? i : fallback;
    }

    private static CheckBox NewCheck(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Ink, BackColor = CardBg
    };

    // ======================================================================
    // 测试连接 / 保存
    // ======================================================================

    private async Task TestConnectionAsync()
    {
        string url = _baseUrl.Text.Trim(), key = _apiKey.Text.Trim(), model = _model.Text.Trim();
        if (url.Length == 0 || key.Length == 0 || model.Length == 0)
        {
            _testResult.ForeColor = ErrRed;
            _testResult.Text = "请先填写 API 地址、Key 和模型。";
            return;
        }

        int gen = ++_testGen;
        _test.Enabled = false;
        _testResult.ForeColor = Sub;
        _testResult.Text = "测试中…";
        var reply = new StringBuilder();
        string? err = null;
        try
        {
            await Task.Run(() =>
            {
                var msgs = new List<Dictionary<string, object>>
                {
                    FloatBall.ChatClient.M("user", "只回复两个字母：OK")
                };
                err = new FloatBall.ChatClient().Run(url, key, model, false, msgs, t => reply.Append(t));
            });
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        if (IsDisposed || gen != _testGen) return;

        _test.Enabled = true;
        if (err == null)
        {
            _testResult.ForeColor = OkGreen;
            _testResult.Text = "连接成功 ✓ 模型回复：" + reply.ToString().Trim();
        }
        else
        {
            _testResult.ForeColor = ErrRed;
            _testResult.Text = err;
        }
    }

    private async void SaveAsync()
    {
        if (_saving) return;

        // 所有快捷键一起校验格式 + 查重（截图 4 个 + 悬浮球 3 个）
        var specs = new List<(string Name, TextBox Box)>
        {
            ("截图", _hkCapture), ("截图识字", _hkOcr), ("截图翻译", _hkTranslate), ("贴图", _hkPin)
        };
        if (_bc != null)
        {
            specs.Add(("呼出面板", _hkToggle));
            specs.Add(("取词提问", _hkAsk));
            specs.Add(("取词翻译", _hkGrabTr));
        }
        var seen = new Dictionary<(uint, uint), string>();
        foreach (var (name, box) in specs)
        {
            if (!HotkeyWindow.TryParse(box.Text, out uint mod, out uint vk))
            {
                MessageBox.Show(this, $"「{name}」快捷键格式无效：{box.Text}", "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (seen.TryGetValue((mod, vk), out var other))
            {
                MessageBox.Show(this, $"「{name}」和「{other}」的快捷键重复，请改成不同的组合。", "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            seen[(mod, vk)] = name;
        }

        var dir = _saveDir.Text.Trim();
        if (dir.Length == 0)
        {
            MessageBox.Show(this, "默认保存目录不能为空。", "LightShot Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int gen = ++_saveGen;
        _saving = true;
        _saveBtn.Enabled = false;
        string? dirError = null;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".lst_write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            });
        }
        catch (Exception ex)
        {
            dirError = ex.Message;
        }

        if (IsDisposed) return;
        _saving = false;
        _saveBtn.Enabled = true;
        if (gen != _saveGen || !Visible) return;   // 等待期间用户点了取消 / 关闭

        if (dirError != null)
        {
            MessageBox.Show(this, "默认保存目录不可用：" + dirError, "LightShot Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyToSettings();
        try
        {
            _s.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存设置失败：" + ex.Message, "LightShot Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyToBall();
        _onSaved(_s);
        Hide();
    }

    private void ApplyToSettings()
    {
        _s.HotkeyCapture = _hkCapture.Text.Trim();
        _s.HotkeyOcr = _hkOcr.Text.Trim();
        _s.HotkeyTranslate = _hkTranslate.Text.Trim();
        _s.HotkeyPin = _hkPin.Text.Trim();
        _s.ApiBaseUrl = _baseUrl.Text.Trim();
        _s.ApiKey = _apiKey.Text.Trim();
        _s.Model = _model.Text.Trim();
        _s.TargetLanguage = _target.Text.Trim();
        _s.SaveDirectory = _saveDir.Text.Trim();
        _s.MergeMode = MergeKeys[Math.Clamp(_merge.SelectedIndex, 0, MergeKeys.Length - 1)];
        _s.OcrQuality = QualityKeys[Math.Clamp(_quality.SelectedIndex, 0, QualityKeys.Length - 1)];
        _s.OcrKeepMode = KeepKeys[Math.Clamp(_keep.SelectedIndex, 0, KeepKeys.Length - 1)];
        _s.AutoStart = _autoStart.Checked;
    }

    private void ApplyToBall()
    {
        if (_bc == null) return;
        _bc.BallVisible = _ballShow.Checked;
        _bc.SelBar = _selBar.Checked;
        _bc.HotkeyToggle = _hkToggle.Text.Trim();
        _bc.HotkeyGrab = _hkAsk.Text.Trim();
        _bc.HotkeyTranslate = _hkGrabTr.Text.Trim();
        _bc.BallSize = (int)_ballSize.Value;
        _bc.IdleOpacity = (int)_idleOpacity.Value;
        _bc.MaxContext = (int)_maxCtx.Value;
        _bc.SystemPrompt = _sysPrompt.Text.Trim();
        _bc.Dark = _dark.Checked;
        _bc.Animate = _anim.Checked;
        _bc.Stream = _stream.Checked;
        _bc.SaveHistory = _saveHist.Checked;
    }

    // ======================================================================
    // 记录查看 / 导出
    // ======================================================================

    private static void ExportHistory(IWin32Window owner, List<FloatBall.Msg> qa, List<FloatBall.Msg> tr)
    {
        if (qa.Count + tr.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            Title = "导出对话记录",
            Filter = "文本文件 (*.txt)|*.txt|Markdown 文件 (*.md)|*.md",
            FileName = $"LST_Chat_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        bool markdown = Path.GetExtension(dlg.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(dlg.FileName, BuildHistoryText(qa, tr, markdown), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "导出失败：" + ex.Message, "LightShot Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string BuildHistoryText(List<FloatBall.Msg> qa, List<FloatBall.Msg> tr, bool markdown)
    {
        var sb = new StringBuilder();
        string nl = markdown ? "\n" : "\r\n";
        sb.Append(markdown ? "# LightShot Translate 对话记录" : "LightShot Translate — AI 对话记录").Append(nl).Append(nl);
        sb.Append("导出时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(nl).Append(nl);
        AppendHistorySection(sb, "问答记录", qa, 0, markdown, nl);
        AppendHistorySection(sb, "翻译记录", tr, 1, markdown, nl);
        return sb.ToString();
    }

    private static void AppendHistorySection(StringBuilder sb, string title, List<FloatBall.Msg> msgs,
                                             int mode, bool markdown, string nl)
    {
        sb.Append(markdown ? $"## {title}（{msgs.Count} 条）" : $"========== {title}（{msgs.Count} 条） ==========")
          .Append(nl).Append(nl);
        if (msgs.Count == 0)
        {
            sb.Append("（暂无记录）").Append(nl).Append(nl);
            return;
        }
        foreach (var m in msgs)
        {
            string time = string.IsNullOrEmpty(m.Time) ? "" : $"  {m.Time}";
            sb.Append(markdown ? $"**{RoleLabel(m, mode)}{time}**" : $"[{RoleLabel(m, mode)}]{time}").Append(nl);
            sb.Append((m.Content ?? "").Replace("\r\n", "\n").Replace("\n", nl));
            sb.Append(nl).Append(nl);
        }
    }

    private static string RoleLabel(FloatBall.Msg m, int mode)
    {
        bool user = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase);
        if (mode == 0) return user ? "你" : "AI";
        return user ? "原文" : "译文";
    }

    // ======================================================================
    // 自绘控件
    // ======================================================================

    private const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static Control? FindScroller(Control? c)
    {
        while (c != null && !(c is ScrollableControl { AutoScroll: true })) c = c.Parent;
        return c;
    }

    // 滚轮经过未聚焦的下拉框 / 数字框时，把消息转给页面滚动，避免误改设置
    private static bool ForwardWheel(Message m, Control source)
    {
        var scroller = FindScroller(source.Parent);
        if (scroller == null) return false;
        SendMessage(scroller.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
        return true;
    }

    private sealed class WheelNumeric : NumericUpDown
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && !Focused && ForwardWheel(m, this)) return;
            base.WndProc(ref m);
        }
    }

    private sealed class WheelCombo : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && !Focused && ForwardWheel(m, this)) return;
            base.WndProc(ref m);
        }
    }

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0, radius * 2);
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private sealed class PagePanel : Panel
    {
        public PagePanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }
    }

    private sealed class CardPanel : TableLayoutPanel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(PageBg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 10f);
            using var brush = new SolidBrush(CardBg);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 10f);
            using var pen = new Pen(Line);
            e.Graphics.DrawPath(pen, path);
        }
    }

    private sealed class NavList : Control
    {
        private const int ItemH = 42, Gap = 6, PadX = 12, PadTop = 4;
        private int _selected = -1, _hover = -1;

        public string[] Items = Array.Empty<string>();
        public event EventHandler? SelectedIndexChanged;

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < -1) value = -1;
                if (value >= Items.Length) value = Items.Length - 1;
                if (value == _selected) return;
                _selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public NavList()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = SideBg;
            Cursor = Cursors.Hand;
            Font = NavFont;
            TabStop = true;
        }

        private int ItemAt(int y)
        {
            int step = ItemH + Gap;
            int i = (y - PadTop) / step;
            if (i < 0 || i >= Items.Length) return -1;
            return (y - PadTop) % step <= ItemH ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = ItemAt(e.Y);
            if (h != _hover)
            {
                _hover = h;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1)
            {
                _hover = -1;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                int i = ItemAt(e.Y);
                if (i >= 0) SelectedIndex = i;
            }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int target = _selected;
            if (e.KeyCode == Keys.Down) target = Math.Min(Items.Length - 1, _selected + 1);
            else if (e.KeyCode == Keys.Up) target = Math.Max(0, _selected - 1);
            else if (e.KeyCode == Keys.Home) target = 0;
            else if (e.KeyCode == Keys.End) target = Items.Length - 1;
            if (target != _selected && target >= 0) { SelectedIndex = target; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Items.Length; i++)
            {
                var r = new RectangleF(PadX, PadTop + i * (ItemH + Gap), Width - PadX * 2, ItemH);
                bool sel = i == _selected;
                if (sel || i == _hover)
                {
                    using var path = Round(r, 8f);
                    using var brush = new SolidBrush(sel ? AccentSoft : HoverBg);
                    g.FillPath(brush, path);
                }
                if (sel)
                {
                    using var bar = Round(new RectangleF(r.X, r.Y + (ItemH - 18) / 2f, 3f, 18f), 1.5f);
                    using var brush = new SolidBrush(Accent);
                    g.FillPath(brush, bar);
                }
                var text = new Rectangle((int)r.X + 16, (int)r.Y, (int)r.Width - 22, ItemH);
                TextRenderer.DrawText(g, Items[i], sel ? NavBoldFont : NavFont, text,
                    sel ? Accent : Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>只读的对话记录查看窗口：问答 / 翻译切换，可直接选中复制或导出。</summary>
    private sealed class HistoryForm : Form
    {
        private readonly List<FloatBall.Msg> _qa, _tr;
        private readonly RichTextBox _view = new()
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = CardBg, Font = UiFont,
            DetectUrls = true, Dock = DockStyle.Fill, WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        private readonly FlatButton _qaBtn, _trBtn, _exportBtn, _closeBtn;
        private int _mode;

        public HistoryForm(List<FloatBall.Msg> qa, List<FloatBall.Msg> tr)
        {
            _qa = qa;
            _tr = tr;
            Text = "AI 对话记录";
            Icon = AppIcon.Window;
            Font = UiFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 600);
            BackColor = PageBg;
            DoubleBuffered = true;

            _qaBtn = new FlatButton($"问答记录 · {qa.Count}", true) { Width = 140, Height = 34, BackColor = PageBg };
            _trBtn = new FlatButton($"翻译记录 · {tr.Count}", false)
            { Width = 140, Height = 34, BackColor = PageBg, Margin = new Padding(8, 0, 0, 0) };
            _exportBtn = new FlatButton("导出记录", false) { Width = 104, Height = 34, BackColor = PageBg };
            _closeBtn = new FlatButton("关闭", false) { Width = 88, Height = 34, BackColor = PageBg };
            _exportBtn.Enabled = qa.Count + tr.Count > 0;

            BuildLayout();
            _qaBtn.Click += (_, _) => Switch(0);
            _trBtn.Click += (_, _) => Switch(1);
            _exportBtn.Click += (_, _) => ExportHistory(this, _qa, _tr);
            _closeBtn.Click += (_, _) => Close();
            CancelButton = _closeBtn;

            Switch(qa.Count == 0 && tr.Count > 0 ? 1 : 0);
        }

        private void Switch(int mode)
        {
            _mode = mode;
            _qaBtn.Primary = mode == 0;
            _trBtn.Primary = mode == 1;
            Render();
        }

        private void Render()
        {
            _view.SuspendLayout();
            try
            {
                _view.Clear();
                var list = _mode == 0 ? _qa : _tr;
                if (list.Count == 0)
                {
                    _view.SelectionFont = UiFont;
                    _view.SelectionColor = Sub;
                    _view.AppendText(_mode == 0 ? "暂无问答记录。" : "暂无翻译记录。");
                }
                else
                {
                    foreach (var m in list)
                    {
                        string time = string.IsNullOrEmpty(m.Time) ? "" : "  " + m.Time;
                        _view.SelectionFont = NavBoldFont;
                        _view.SelectionColor = Accent;
                        _view.AppendText(RoleLabel(m, _mode) + time + Environment.NewLine);
                        _view.SelectionFont = UiFont;
                        _view.SelectionColor = Ink;
                        _view.AppendText((m.Content ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
                        _view.AppendText(Environment.NewLine + Environment.NewLine);
                    }
                }
                _view.SelectionStart = 0;
                _view.ScrollToCaret();
            }
            finally
            {
                _view.ResumeLayout();
            }
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = PageBg,
                Padding = new Padding(24, 20, 24, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var head = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
                BackColor = PageBg, Margin = new Padding(0, 0, 0, 14)
            };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.Controls.Add(new Label
            {
                Text = "AI 对话记录", Font = PageTitleFont, ForeColor = Ink, AutoSize = true,
                BackColor = PageBg, Margin = new Padding(0, 0, 0, 4)
            }, 0, 0);
            head.Controls.Add(new Label
            {
                Text = "问答与翻译记录只保存在本机；可在设置中清空。", Font = UiFont, ForeColor = Sub,
                AutoSize = true, BackColor = PageBg, Margin = Padding.Empty
            }, 0, 1);
            root.Controls.Add(head, 0, 0);

            var toolbar = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
                BackColor = PageBg, Margin = new Padding(0, 0, 0, 10), Dock = DockStyle.Top
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var tabs = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                BackColor = PageBg, Margin = Padding.Empty
            };
            tabs.Controls.Add(_qaBtn);
            tabs.Controls.Add(_trBtn);
            var exportBar = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                BackColor = PageBg, Margin = Padding.Empty, Anchor = AnchorStyles.Right
            };
            exportBar.Controls.Add(_exportBtn);
            toolbar.Controls.Add(tabs, 0, 0);
            toolbar.Controls.Add(exportBar, 1, 0);
            root.Controls.Add(toolbar, 0, 1);

            var well = new CardPanel
            {
                ColumnCount = 1, RowCount = 1, Dock = DockStyle.Fill, Padding = new Padding(12),
                BackColor = CardBg, Margin = new Padding(0, 0, 0, 6)
            };
            well.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            well.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            well.Controls.Add(_view, 0, 0);
            root.Controls.Add(well, 0, 2);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = PageBg, Margin = Padding.Empty
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(new Label
            {
                Text = "只读 · 可直接选择并复制文字", Font = SmallFont, ForeColor = Sub, BackColor = PageBg,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
            }, 0, 0);
            var closeBar = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                BackColor = PageBg, Margin = Padding.Empty, Anchor = AnchorStyles.Right
            };
            closeBar.Controls.Add(_closeBtn);
            footer.Controls.Add(closeBar, 1, 0);
            root.Controls.Add(footer, 0, 3);
        }
    }

    /// <summary>圆角输入框外壳：去掉系统滚动条 / 双层边框，聚焦时描边强调色。</summary>
    private sealed class TextShell : Panel
    {
        private readonly TextBox _box;

        public TextShell(TextBox box)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = CardBg;
            Padding = new Padding(10, 8, 10, 8);
            Cursor = Cursors.IBeam;
            _box = box;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = CardBg;
            box.Font = UiFont;
            box.Dock = DockStyle.Fill;
            box.GotFocus += (_, _) => Invalidate();
            box.LostFocus += (_, _) => Invalidate();
            Controls.Add(box);
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(CardBg);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool hot = _box.Focused;
            using var path = Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8f);
            using var pen = new Pen(hot ? Accent : Line, hot ? 1.4f : 1f);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }
    }

    private sealed class FlatButton : Button
    {
        private bool _accent;
        private bool _over, _down;

        /// <summary>强调色描边按钮（次要操作，例如“测试连接”）。</summary>
        public bool Outline;

        /// <summary>用于“问答 / 翻译”这类的切换按钮。</summary>
        public bool Primary
        {
            get => _accent;
            set
            {
                if (_accent == value) return;
                _accent = value;
                Invalidate();
            }
        }

        public FlatButton(string text, bool accent)
        {
            Text = text;
            _accent = accent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = CardBg;
            ForeColor = Ink;
            Font = UiFont;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);   // 圆角外侧必须清成父级底色，否则会露出黑色缓冲角
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, border, ink;
            if (_accent && Outline)
            {
                fill = _over || _down ? Color.FromArgb(0xEF, 0xF5, 0xFF) : Color.White;
                border = _over || _down ? Accent : Color.FromArgb(0xC5, 0xD8, 0xF7);
                ink = Accent;
            }
            else if (_accent)
            {
                fill = Color.FromArgb(0x3B, 0x82, 0xF6);
                if (_over) fill = Color.FromArgb(0x33, 0x77, 0xE8);
                if (_down) fill = Color.FromArgb(0x2A, 0x6B, 0xD6);
                border = fill;
                ink = Color.White;
            }
            else
            {
                fill = _over || _down ? Color.FromArgb(0xF3, 0xF5, 0xF8) : Color.White;
                border = _over ? Color.FromArgb(0xC9, 0xD0, 0xD9) : Line;
                ink = Ink;
            }
            if (!Enabled)
            {
                fill = _accent && !Outline ? Color.FromArgb(0xBE, 0xD2, 0xF4) : Color.FromArgb(0xF5, 0xF6, 0xF8);
                border = _accent && !Outline ? fill : Line;
                ink = _accent && !Outline ? Color.White : Sub;
            }

            using var path = Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8f);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(border)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
