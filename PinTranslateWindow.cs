using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LST;

internal enum PinMode { Pin, Ocr, Translate }

/// <summary>
/// 贴图 + 原位智能窗口：在框选位置原样贴出截图（置顶）。
/// 可在其上识字（淡蓝框标出文字块）、原位翻译（把译文按段落覆盖回原文位置）。
/// 滚轮缩放 · 左键拖动 · 单击文字块复制 · 空格切换原文/译文 · 右键菜单 · Esc 关闭。
/// </summary>
internal sealed class PinTranslateWindow : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private const uint SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    /// <summary>一个文字块：同一段落的相邻行合并而成。</summary>
    private sealed class TextBlock
    {
        public List<int> Lines = new();
        public Rectangle Box;
        public string Source = "";
        public string Translation = "";
    }

    private readonly Bitmap _original;
    private readonly Rectangle _screenRect;
    private readonly AppSettings _settings;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _hintTimer = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 150 };
    private OverlayToolbar? _bar;

    private Bitmap? _translated;
    private bool _showTranslated;
    private string _status = "";
    private bool _statusError;
    private List<OcrLine> _lines = new();
    private List<TextBlock> _blocks = new();
    private CancellationTokenSource? _cts;                       // 每次 识字/翻译 请求一个，新请求会取消旧请求
    private readonly CancellationTokenSource _life = new();       // 窗口生命周期：关闭时取消尚未开始推理的 OCR
    private Task<List<OcrLine>>? _ocrTask;                        // 同一窗口只跑一次 OCR，重复点击共用同一个任务
    private TranslationService? _translator;

    private string _mergeMode;
    private readonly List<ToolStripMenuItem> _mergeItems = new();
    private bool _mouseDown, _dragged;
    private Point _downScreen, _downLocation;

    private bool ShowingTranslated => _showTranslated && _translated != null;
    private float Zoom => _original.Width == 0 ? 1f : (float)ClientSize.Width / _original.Width;

    /// <param name="original">窗口接管该位图的所有权，关闭时释放。</param>
    public PinTranslateWindow(Bitmap original, Rectangle screenRect, AppSettings settings)
    {
        _original = original;
        _screenRect = screenRect;
        _settings = settings;
        _mergeMode = NormalizeMode(settings.MergeMode);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Text = "LightShot Translate - 贴图";
        Icon = AppIcon.Window;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.Black;
        Bounds = screenRect;

        _menu.Items.Add("切换 原文 / 译文（空格）", null, (_, _) => Toggle());
        _menu.Items.Add("翻译 / 重新翻译（T）", null, async (_, _) => await RunPipelineAsync(true));
        _menu.Items.Add("识字（只识别）", null, async (_, _) => await RunPipelineAsync(false));
        _menu.Items.Add("查看 / 编辑识别文字…", null, (_, _) => ShowTextWindow());

        var mergeMenu = new ToolStripMenuItem("段落合并（M 切换）");
        foreach (var (key, label) in MergeModes)
        {
            var item = new ToolStripMenuItem(label) { Tag = key, Checked = key == _mergeMode };
            item.Click += (_, _) => SetMergeMode(key);
            mergeMenu.DropDownItems.Add(item);
            _mergeItems.Add(item);
        }
        _menu.Items.Add(mergeMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("复制图片", null, (_, _) => CopyImage());
        _menu.Items.Add("复制译文文本", null, (_, _) => CopyText(TranslatedTexts()));
        _menu.Items.Add("复制原文文本", null, (_, _) => CopyText(_lines.Select(l => l.Text)));
        _menu.Items.Add("另存为图片…", null, (_, _) => SaveImage());
        _menu.Items.Add("还原 100% 大小（1）", null, (_, _) => ResetSize());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("关闭（Esc）", null, (_, _) => Close());
        ContextMenuStrip = _menu;

        // 悬停时出现的紧凑图标工具栏
        _bar = new OverlayToolbar(compact: true) { Visible = false };
        _bar.Hovered += k => { if (k is OverlayToolbar.Kind.Ocr or OverlayToolbar.Kind.Translate) OcrService.Prewarm(); };
        _bar.Add(OverlayToolbar.Kind.Copy, "复制", CopyAllText);
        _bar.Add(OverlayToolbar.Kind.Ocr, "识字", async () => await RunPipelineAsync(false));
        _bar.Add(OverlayToolbar.Kind.Translate, "翻译", async () => await RunPipelineAsync(true));
        _bar.Add(OverlayToolbar.Kind.Swap, "原/译", Toggle);
        _bar.Add(OverlayToolbar.Kind.Close, "关闭", Close);
        Controls.Add(_bar);

        _hoverTimer.Tick += (_, _) =>
        {
            bool inside = Bounds.Contains(Cursor.Position) || _menu.Visible;
            if (_bar != null && _bar.Visible != inside) _bar.Visible = inside;
        };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _status = ""; Invalidate(); };
    }

    // ---------- 主流程 ----------
    public async Task StartAsync(TranslationService translator, PinMode mode)
    {
        _translator = translator;
        if (mode == PinMode.Pin)
        {
            SetStatus("已贴图 · 滚轮缩放 · 拖动移动 · 悬停显示按钮 · Esc 关闭", 5000);
            return;
        }
        await RunPipelineAsync(mode == PinMode.Translate);
    }

    /// <summary>必要时先识字；translate 为 true 时再翻译。</summary>
    private async Task RunPipelineAsync(bool translate)
    {
        if (_translator == null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            if (_lines.Count == 0)
            {
                if (_ocrTask == null)
                {
                    SetStatus(OcrService.IsLoaded ? "识别文字中…" : "首次加载 OCR 模型，请稍候…");
                    // 像素拷贝在此（UI 线程）同步完成，之后 _original 的释放/重绘不影响后台识别
                    _ocrTask = OcrService.RecognizeAsync(_original, _life.Token);
                }
                List<OcrLine> found;
                try { found = await _ocrTask; }
                catch { _ocrTask = null; throw; }   // 失败/取消后允许重试
                if (IsDisposed) return;
                ct.ThrowIfCancellationRequested();  // 已被更新的请求取代：由新请求负责后续，避免旧结果覆盖
                _lines = found;
                if (_lines.Count == 0) { SetStatus("没有识别到文字（Esc 关闭）", 4000); return; }
                _blocks = BuildBlocks(_lines, _mergeMode);
                Invalidate();
            }

            if (translate)
                await TranslateAsync(ct);
            else
                SetStatus($"✓ 识别 {_lines.Count} 行 · 点文字框复制该行 · T：翻译 · Esc：关闭", 5000);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) SetStatus("失败：" + ex.Message, 0, error: true);
        }
    }

    private async Task TranslateAsync(CancellationToken ct)
    {
        SetStatus("翻译中…（文字将发送至你配置的 API）");
        var sources = _blocks.Select(b => b.Source).ToList();
        var result = await _translator!.TranslateLinesAsync(sources, _settings, ct);
        if (IsDisposed || ct.IsCancellationRequested) return;

        for (int i = 0; i < _blocks.Count && i < result.Length; i++) _blocks[i].Translation = result[i];

        var old = _translated;
        _translated = BuildTranslatedImage();
        old?.Dispose();
        _showTranslated = true;
        SetStatus("✓ 点文字块复制译文 · 空格：原文/译文 · 滚轮缩放 · Esc：关闭", 5000);
        Invalidate();
    }

    // ---------- 段落合并 ----------
    private static readonly Regex ListItem = new(
        @"^\s*([•·●○▪\-\*–—]|\(?\d{1,3}[\.\)、）]|[A-Za-z][\.\)])\s", RegexOptions.Compiled);

    /// <summary>
    /// 把版面上连续的行合并成段落：行高相近、上下间距小、左对齐、上一行接近段落右边界（说明是自然换行），
    /// 且下一行不是列表项。标题、短行、代码行因此不会被误合并。
    /// </summary>
    /// <summary>合并参数（单位：行高的倍数）。</summary>
    private readonly record struct MergeParams(double MaxGap, double MaxHeightRatio, double LeftTol, double RightEarly, double ColTol);

    private static readonly (string Key, string Label)[] MergeModes =
    {
        ("Off", "关闭（逐行翻译）"), ("Strict", "严格（少合并）"), ("Standard", "标准"), ("Loose", "宽松（多合并）")
    };

    private static MergeParams ParamsFor(string mode) => mode switch
    {
        "Strict" => new MergeParams(0.6, 1.2, 0.8, 1.0, 1.5),
        "Loose" => new MergeParams(1.3, 1.6, 2.5, 4.0, 6.0),
        _ => new MergeParams(0.9, 1.35, 1.6, 2.0, 3.0)
    };

    private static string NormalizeMode(string? mode) =>
        MergeModes.Any(m => m.Key == mode) ? mode! : "Standard";

    private static List<TextBlock> BuildBlocks(List<OcrLine> lines, string mode)
    {
        int n = lines.Count;

        if (mode == "Off")
            return lines.Select((l, i) => new TextBlock { Lines = { i }, Box = l.Box, Source = l.Text }).ToList();

        var mp = ParamsFor(mode);
        var colRight = new int[n];
        for (int i = 0; i < n; i++)
        {
            colRight[i] = lines[i].Box.Right;
            for (int j = 0; j < n; j++)
                if (Math.Abs(lines[j].Box.Left - lines[i].Box.Left) <= lines[i].Box.Height * 1.5)
                    colRight[i] = Math.Max(colRight[i], lines[j].Box.Right);
        }

        var blocks = new List<TextBlock>();
        for (int i = 0; i < n; i++)
        {
            var cur = lines[i];
            TextBlock? target = null;
            int bestGap = int.MaxValue;
            foreach (var b in blocks)
            {
                int li = b.Lines[^1];
                if (!CanAppend(b, lines[li], colRight[li], cur, mp)) continue;
                int gap = Math.Abs(cur.Box.Top - lines[li].Box.Bottom);
                if (gap < bestGap) { bestGap = gap; target = b; }
            }

            if (target == null) { target = new TextBlock(); blocks.Add(target); }
            target.Lines.Add(i);
            target.Box = target.Lines.Count == 1 ? cur.Box : Rectangle.Union(target.Box, cur.Box);
            target.Source = target.Lines.Count == 1 ? cur.Text : JoinText(target.Source, cur.Text);
        }
        return blocks;
    }

    private static bool CanAppend(TextBlock b, OcrLine last, int lastColRight, OcrLine cur, MergeParams mp)
    {
        int hMin = Math.Min(last.Box.Height, cur.Box.Height);
        int hMax = Math.Max(last.Box.Height, cur.Box.Height);
        if (hMin <= 0 || hMax > hMin * mp.MaxHeightRatio) return false;            // 字号差异大：标题/正文
        int gap = cur.Box.Top - last.Box.Bottom;
        if (gap < -hMin * 0.3 || gap > hMin * mp.MaxGap) return false;              // 必须紧挨着
        int overlap = Math.Min(last.Box.Right, cur.Box.Right) - Math.Max(last.Box.Left, cur.Box.Left);
        if (overlap <= 0) return false;
        if (Math.Abs(cur.Box.Left - last.Box.Left) > hMax * mp.LeftTol) return false;   // 左对齐（允许首行缩进）
        if (last.Box.Right < b.Box.Right - hMax * mp.RightEarly) return false;      // 上一行没写满：段落已结束
        if (last.Box.Right < lastColRight - hMax * mp.ColTol) return false;         // 上一行明显短于整列
        if (ListItem.IsMatch(cur.Text)) return false;                               // 新的列表项
        return true;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);

    private static string JoinText(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return IsCjk(a[^1]) || IsCjk(b[0]) ? a + b : a + " " + b;
    }

    /// <summary>切换合并方式：重建文字块；若已翻译则自动按新分块重新翻译。</summary>
    private void SetMergeMode(string key)
    {
        _mergeMode = NormalizeMode(key);
        foreach (var it in _mergeItems) it.Checked = (string?)it.Tag == _mergeMode;
        var label = MergeModes.First(m => m.Key == _mergeMode).Label;

        if (_lines.Count == 0) { SetStatus("段落合并：" + label, 2500); return; }

        bool hadTranslation = _translated != null;
        _blocks = BuildBlocks(_lines, _mergeMode);
        _translated?.Dispose();
        _translated = null;
        _showTranslated = false;
        Invalidate();

        if (hadTranslation)
        {
            SetStatus($"段落合并：{label}，重新翻译中…");
            _ = RunPipelineAsync(true);
        }
        else SetStatus($"段落合并：{label}（共 {_blocks.Count} 块）", 2500);
    }

    private void CycleMergeMode()
    {
        int i = Array.FindIndex(MergeModes, m => m.Key == _mergeMode);
        SetMergeMode(MergeModes[(i + 1) % MergeModes.Length].Key);
    }

    private IEnumerable<string> TranslatedTexts() =>
        _blocks.Select(b => string.IsNullOrWhiteSpace(b.Translation) ? b.Source : b.Translation);

    // ---------- 合成译文图 ----------
    private Bitmap BuildTranslatedImage()
    {
        int w = _original.Width, h = _original.Height;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(_original, 0, 0, w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var px = new PixelBuffer(_original);
        var bounds = new Rectangle(0, 0, w, h);

        foreach (var b in _blocks)
        {
            var text = b.Translation.Trim();
            if (text.Length == 0 || text == b.Source) continue;

            // 逐行取背景色并抹掉原文；文字色取第一行
            Color fg = Color.Black;
            for (int k = 0; k < b.Lines.Count; k++)
            {
                var lb = Rectangle.Intersect(Rectangle.Inflate(_lines[b.Lines[k]].Box, 2, 1), bounds);
                if (lb.Width < 2 || lb.Height < 2) continue;
                var (bg, f) = px.Analyze(lb);
                using var fill = new SolidBrush(bg);
                g.FillRectangle(fill, lb);
                if (k == 0) fg = f;
            }

            var box = Rectangle.Intersect(Rectangle.Inflate(b.Box, 2, 1), bounds);
            DrawFitted(g, text, box, fg, b.Lines.Count);
        }
        return bmp;
    }

    /// <summary>在块区域内自动换行，字号从“行高的 80%”逐步缩小到能放下为止；保证一定会画出文字。</summary>
    private static void DrawFitted(Graphics g, string text, Rectangle box, Color color, int lineCount)
    {
        using var sf = new StringFormat(StringFormatFlags.NoClip)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = lineCount <= 1 ? StringAlignment.Center : StringAlignment.Near
        };
        using var brush = new SolidBrush(color);

        float lineH = box.Height / (float)Math.Max(1, lineCount);
        float size = Math.Max(9f, lineH * 0.78f);
        while (true)
        {
            using var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
            var m = g.MeasureString(text, font, box.Width, sf);
            if (m.Height <= box.Height + 1 || size <= 7f)
            {
                g.DrawString(text, font, brush, box, sf);
                return;
            }
            size -= 0.5f;
        }
    }

    // ---------- 绘制 ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var img = ShowingTranslated ? _translated! : _original;
        float s = Zoom;

        g.InterpolationMode = Math.Abs(s - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(img, 0, 0, ClientSize.Width, ClientSize.Height);

        // 仅识别、尚未翻译：标出每个文字块（段落）的位置
        if (!ShowingTranslated && _blocks.Count > 0)
        {
            var state = g.Save();
            g.ScaleTransform(s, s);
            using var fill = new SolidBrush(Color.FromArgb(34, 0, 150, 255));
            using var edge = new Pen(Color.FromArgb(170, 0, 150, 255), 1f / s);
            foreach (var l in _lines)
            {
                g.FillRectangle(fill, l.Box);
                g.DrawRectangle(edge, l.Box);
            }
            g.Restore(state);
        }

        g.ResetTransform();
        using (var pen = new Pen(Color.FromArgb(0, 150, 255), 2))
            g.DrawRectangle(pen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);

        if (_status.Length > 0)
        {
            using var font = new Font("Microsoft YaHei UI", 9f);
            float maxW = Math.Max(80, ClientSize.Width - 20);
            var size = g.MeasureString(_status, font, (int)maxW);
            var rect = new RectangleF(6, 6, size.Width + 10, size.Height + 6);
            using var bg = new SolidBrush(_statusError ? Color.FromArgb(225, 160, 30, 30) : Color.FromArgb(215, 20, 20, 20));
            g.FillRectangle(bg, rect);
            g.DrawString(_status, font, Brushes.White, new RectangleF(rect.X + 5, rect.Y + 3, maxW, size.Height));
        }
    }

    private void SetStatus(string text, int hideAfterMs = 0, bool error = false)
    {
        _status = text;
        _statusError = error;
        _hintTimer.Stop();
        if (hideAfterMs > 0) { _hintTimer.Interval = hideAfterMs; _hintTimer.Start(); }
        Invalidate();
    }

    // ---------- 交互 ----------
    private void Toggle()
    {
        if (_translated == null) return;
        _showTranslated = !_showTranslated;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _mouseDown = true; _dragged = false;
            _downScreen = Cursor.Position; _downLocation = Location;
            Focus();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseDown && MouseButtons == MouseButtons.Left)
        {
            var dx = Cursor.Position.X - _downScreen.X;
            var dy = Cursor.Position.Y - _downScreen.Y;
            if (!_dragged && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _dragged = true;
            if (_dragged) Location = new Point(_downLocation.X + dx, _downLocation.Y + dy);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _mouseDown && !_dragged) CopyAt(e.Location);
        _mouseDown = false;
        base.OnMouseUp(e);
    }

    /// <summary>滚轮缩放（以指针为锚点），范围 20%–600%。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        float factor = e.Delta > 0 ? 1.1f : 1f / 1.1f;
        float cur = Zoom;
        float next = Math.Clamp(cur * factor, 0.2f, 6f);
        ResizeTo(next, Cursor.Position);
        base.OnMouseWheel(e);
    }

    private void ResizeTo(float scale, Point anchorScreen)
    {
        int newW = Math.Max(40, (int)Math.Round(_original.Width * scale));
        int newH = Math.Max(24, (int)Math.Round(_original.Height * scale));
        float relX = Width == 0 ? 0 : (anchorScreen.X - Left) / (float)Width;
        float relY = Height == 0 ? 0 : (anchorScreen.Y - Top) / (float)Height;
        int nx = (int)Math.Round(anchorScreen.X - relX * newW);
        int ny = (int)Math.Round(anchorScreen.Y - relY * newH);
        SetWindowPos(Handle, IntPtr.Zero, nx, ny, newW, newH, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ResetSize() => ResizeTo(1f, new Point(Left + Width / 2, Top + Height / 2));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionBar();
        Invalidate();
    }

    private void PositionBar()
    {
        if (_bar == null) return;
        _bar.Location = new Point(Math.Max(2, ClientSize.Width - _bar.Width - 4), 4);
        _bar.BringToFront();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Space) { Toggle(); e.Handled = true; }
        else if (e.KeyCode == Keys.M && !e.Control) { CycleMergeMode(); e.Handled = true; }
        else if (e.KeyCode == Keys.T && !e.Control) { _ = RunPipelineAsync(true); e.Handled = true; }
        else if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) { ResetSize(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.C) { CopyAllText(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    // ---------- 复制 / 保存 ----------
    private void CopyAt(Point p)
    {
        float s = Zoom;
        var q = new Point((int)(p.X / s), (int)(p.Y / s));
        if (ShowingTranslated)
        {
            foreach (var b in _blocks)
                if (Rectangle.Inflate(b.Box, 2, 2).Contains(q))
                {
                    Copied(string.IsNullOrWhiteSpace(b.Translation) ? b.Source : b.Translation);
                    return;
                }
        }
        else
        {
            foreach (var l in _lines)
                if (Rectangle.Inflate(l.Box, 2, 2).Contains(q)) { Copied(l.Text); return; }
        }
    }

    private void Copied(string text)
    {
        try { Clipboard.SetDataObject(text, true, 5, 100); } catch { }
        SetStatus("已复制：" + (text.Length > 40 ? text[..40] + "…" : text), 2000);
    }

    private void CopyAllText()
    {
        var all = ShowingTranslated ? TranslatedTexts() : _lines.Select(l => l.Text);
        var text = string.Join("\n", all);
        if (text.Length == 0)
        {
            CopyImage();      // 还没识字时，“复制”复制图片
            SetStatus("已复制图片", 2000);
            return;
        }
        try { Clipboard.SetDataObject(text, true, 5, 100); } catch { }
        SetStatus("已复制全部文字", 2000);
    }

    private void CopyImage()
    {
        var img = ShowingTranslated ? _translated! : _original;
        using var copy = new Bitmap(img);   // copy:true 时剪贴板已取走数据，临时位图可以立即释放
        try { Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, copy), true, 5, 100); } catch { }
    }

    private static void CopyText(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        if (text.Length == 0) return;
        try { Clipboard.SetDataObject(text, true, 5, 100); } catch { }
    }

    private void ShowTextWindow()
    {
        if (_lines.Count == 0 || _translator == null) return;
        var form = new ResultForm(string.Join("\n", _lines.Select(l => l.Text)),
            (t, ct) => _translator.TranslateAsync(t, _settings, ct));
        form.Show();
    }

    private void SaveImage()
    {
        var img = ShowingTranslated ? _translated! : _original;
        try { Directory.CreateDirectory(_settings.SaveDirectory); } catch { }
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"LST_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.Exists(_settings.SaveDirectory) ? _settings.SaveDirectory : ""
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) img.Save(dlg.FileName, ImageFormat.Png);
    }

    // ---------- 窗口定位 / 生命周期 ----------
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, HWND_TOPMOST, _screenRect.X, _screenRect.Y, _screenRect.Width, _screenRect.Height, SWP_SHOWWINDOW);
        PositionBar();
        _hoverTimer.Start();
        Activate();
        SetForegroundWindow(Handle);
        Focus();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) return;   // 拖到不同缩放的显示器时保持像素大小不变
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Cancel();
        _life.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _hintTimer.Dispose();
        _hoverTimer.Dispose();
        _menu.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 图片在 Dispose 里释放：窗口正常关闭、或构造后 Show 失败，两种路径都不会泄漏（重复 Dispose 无害）
            _translated?.Dispose();
            _original.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- 像素分析：估算背景色与文字色 ----------
    private sealed class PixelBuffer
    {
        private readonly byte[] _buf;
        private readonly int _stride, _w, _h;

        public PixelBuffer(Bitmap bmp)
        {
            _w = bmp.Width; _h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                _stride = data.Stride;
                _buf = new byte[Math.Abs(_stride) * _h];
                Marshal.Copy(data.Scan0, _buf, 0, _buf.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        private (int r, int g, int b) At(int x, int y)
        {
            int o = y * _stride + x * 4;
            return (_buf[o + 2], _buf[o + 1], _buf[o]);
        }

        public (Color bg, Color fg) Analyze(Rectangle box)
        {
            var rs = new List<int>(); var gs = new List<int>(); var bs = new List<int>();
            void Add(int x, int y)
            {
                x = Math.Clamp(x, 0, _w - 1); y = Math.Clamp(y, 0, _h - 1);
                var (r, g, b) = At(x, y); rs.Add(r); gs.Add(g); bs.Add(b);
            }
            for (int x = box.Left; x < box.Right; x++) { Add(x, box.Top); Add(x, box.Bottom - 1); }
            for (int y = box.Top; y < box.Bottom; y++) { Add(box.Left, y); Add(box.Right - 1, y); }
            int br = Median(rs), bgc = Median(gs), bb = Median(bs);
            var bg = Color.FromArgb(br, bgc, bb);

            int maxD = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    var (r, g, b) = At(x, y);
                    maxD = Math.Max(maxD, Dist(r, g, b, br, bgc, bb));
                }

            int thr = (int)(maxD * 0.6);
            long sr = 0, sg = 0, sb = 0, n = 0;
            if (maxD > 40 * 40)
            {
                for (int y = box.Top; y < box.Bottom; y++)
                    for (int x = box.Left; x < box.Right; x++)
                    {
                        var (r, g, b) = At(x, y);
                        if (Dist(r, g, b, br, bgc, bb) >= thr) { sr += r; sg += g; sb += b; n++; }
                    }
            }

            Color fg = n > 0 ? Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n)) : ContrastOf(bg);
            if (Math.Abs(Luma(fg) - Luma(bg)) < 90) fg = ContrastOf(bg);
            return (bg, fg);
        }

        private static int Dist(int r1, int g1, int b1, int r2, int g2, int b2)
            => (r1 - r2) * (r1 - r2) + (g1 - g2) * (g1 - g2) + (b1 - b2) * (b1 - b2);

        private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        private static Color ContrastOf(Color bg) => Luma(bg) > 140 ? Color.FromArgb(24, 24, 24) : Color.White;

        private static int Median(List<int> v)
        {
            v.Sort();
            return v.Count == 0 ? 0 : v[v.Count / 2];
        }
    }
}
