using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LST;

internal enum CaptureAction { Copy, Save, Ocr, Translate, Pin }

/// <summary>
/// 全屏遮罩：显示冻结的屏幕。
/// 鼠标移动 → 自动高亮指针下的窗口/控件（滚轮调整范围）；单击 → 选中高亮区域；拖动 → 自由框选。
/// 选中后出现图标工具栏；Esc 退出，右键取消选区/退出。
/// </summary>
internal sealed class SelectionOverlay : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Bitmap _shot;
    private readonly Bitmap _dimmed;   // 预先合成好的“变暗截图”：鼠标移动时只做不透明拷贝，不再每帧做 alpha 混合

    // 高频绘制路径复用的 GDI+ 对象（不再每次 OnPaint 都 new / Dispose）
    private readonly Font _labelFont = new("Microsoft YaHei UI", 9f);
    private readonly Font _hintFont = new("Microsoft YaHei UI", 10f);
    private readonly Pen _borderPen = new(Color.FromArgb(70, 150, 255), 2);
    private readonly SolidBrush _labelBg = new(Color.FromArgb(225, 18, 24, 38));
    private readonly SolidBrush _hintBg = new(Color.FromArgb(220, 22, 30, 46));
    private RectangleF _hintBox;
    private PointF _hintTextPos;
    private readonly Rectangle _virtualBounds;
    private readonly CaptureAction _defaultAction;
    private readonly WindowFinder _finder;

    private Point _downPoint;
    private Rectangle _sel;      // 已确定/正在拖动的选区（客户区坐标）
    private Rectangle _hover;    // 自动高亮的候选区域
    private bool _dragging, _dragMoved;
    private OverlayToolbar? _toolbar;
    private List<Rectangle> _chain = new();   // 屏幕坐标，由小到大
    private int _level;
    private bool _locked;   // 滚轮调整过范围后锁定：指针仍在该区域内时不再被鼠标微动重置

    // ---------- 画笔 ----------
    private sealed class Stroke
    {
        public Color Color;
        public float Width;
        public readonly List<Point> Points = new();
        public Point[]? Cached;   // 完成后缓存，重绘时不再反复分配数组
    }

    private readonly List<Stroke> _strokes = new();
    private Stroke? _current;
    private bool _penMode;
    private Color _penColor = PenBar.Palette[0];
    private float _penWidth = PenBar.Widths[1];
    private int _penColorIdx, _penWidthIdx = 1;   // 关掉再开画笔时沿用上次的选择
    private PenBar? _penBar;

    public CaptureAction? ChosenAction { get; private set; }
    /// <summary>裁剪后的图片，由调用方负责 Dispose。</summary>
    public Bitmap? Cropped { get; private set; }
    /// <summary>选区在屏幕上的位置（物理像素，虚拟桌面坐标）。</summary>
    public Rectangle ScreenSelection { get; private set; }

    public SelectionOverlay(Bitmap shot, Rectangle virtualBounds, CaptureAction defaultAction, WindowFinder finder)
    {
        _shot = shot;
        _dimmed = BuildDimmed(shot);
        _virtualBounds = virtualBounds;
        _defaultAction = defaultAction;
        _finder = finder;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualBounds;
        ShowInTaskbar = false;
        Text = "LightShot Translate";
        Icon = AppIcon.Window;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        BackColor = Color.Black;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, HWND_TOPMOST, _virtualBounds.X, _virtualBounds.Y,
            _virtualBounds.Width, _virtualBounds.Height, SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(Handle);
        Focus();
        UpdateHover(PointToClient(Cursor.Position));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    // ---------- 绘制 ----------
    private static Bitmap BuildDimmed(Bitmap shot)
    {
        var bmp = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
        g.CompositingMode = CompositingMode.SourceOver;
        using var dim = new SolidBrush(Color.FromArgb(125, 8, 14, 26));
        g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
        return bmp;
    }

    private bool _firstPaintLogged;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Perf.Enabled && !_firstPaintLogged) { _firstPaintLogged = true; Perf.Log($"overlay first paint: {Perf.Ms(Perf.HotkeyTick):F0} ms after hotkey"); }
        var g = e.Graphics;
        var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, _shot.Width, _shot.Height));
        if (clip.IsEmpty) return;

        // 只重绘受影响的区域：底图 = 已变暗的缓存，选区内 = 原图；两者都是不透明直拷（SourceCopy）
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_dimmed, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);

        var active = !_sel.IsEmpty ? _sel : _hover;
        var inner = Rectangle.Intersect(active, clip);
        if (!inner.IsEmpty)
            g.DrawImage(_shot, inner, inner.X, inner.Y, inner.Width, inner.Height, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;

        if ((_strokes.Count > 0 || _current != null) && !_sel.IsEmpty)
        {
            var saved = g.Save();
            g.IntersectClip(_sel);                       // 笔迹只在选区内可见
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var s in _strokes) DrawStroke(g, s);
            if (_current != null) DrawStroke(g, _current);
            g.Restore(saved);
        }

        if (active.Width > 0 && active.Height > 0)
        {
            g.DrawRectangle(_borderPen, active);
            DrawSizeLabel(g, active, _sel.IsEmpty && _chain.Count > 1 ? $"   滚轮 {_level + 1}/{_chain.Count}" : "");
        }

        if (_sel.IsEmpty) DrawHint(g);
    }

    private void DrawSizeLabel(Graphics g, Rectangle r, string extra)
    {
        string text = $"{r.Width} × {r.Height} px{extra}";
        var size = g.MeasureString(text, _labelFont);
        float ly = r.Y - size.Height - 6;
        if (ly < 0) ly = r.Y + 6;
        var box = new RectangleF(r.X, ly, size.Width + 12, size.Height + 2);
        g.FillRectangle(_labelBg, box);
        g.DrawString(text, _labelFont, Brushes.White, box.X + 6, box.Y + 1);
    }

    private void DrawHint(Graphics g)
    {
        const string text = "单击选中高亮区域  ·  拖动自由框选  ·  滚轮调整范围  ·  Esc / 右键 退出截图";
        if (_hintBox.IsEmpty)   // 提示条文字固定，只测量一次
        {
            var ps = Screen.PrimaryScreen?.Bounds ?? _virtualBounds;
            var size = g.MeasureString(text, _hintFont);
            float x = ps.X - _virtualBounds.X + (ps.Width - size.Width) / 2f;
            float y = ps.Y - _virtualBounds.Y + 24;
            _hintBox = new RectangleF(x - 14, y - 6, size.Width + 28, size.Height + 12);
            _hintTextPos = new PointF(x, y);
        }
        if (!g.ClipBounds.IntersectsWith(_hintBox)) return;
        g.FillRectangle(_hintBg, _hintBox);
        g.DrawString(text, _hintFont, Brushes.White, _hintTextPos);
    }

    private static Rectangle Dirty(Rectangle r) =>
        r.IsEmpty ? r : new Rectangle(r.X - 6, r.Y - 36, r.Width + 12, r.Height + 42);

    private void Repaint(Rectangle a, Rectangle b)
    {
        a = Dirty(a); b = Dirty(b);
        if (a.IsEmpty && b.IsEmpty) return;
        Invalidate(a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b));
    }

    private void SetSelection(Rectangle next)
    {
        if (next == _sel) return;
        bool hintChange = _sel.IsEmpty != next.IsEmpty;
        var old = _sel;
        _sel = next;
        if (hintChange) Invalidate(); else Repaint(old, next);
    }

    private void SetHover(Rectangle next)
    {
        if (next == _hover) return;   // 指针仍在同一控件内：不触发任何重绘
        var old = _hover;
        _hover = next;
        Repaint(old, next);
    }

    // ---------- 智能高亮 ----------
    private void UpdateHover(Point client)
    {
        var sp = new Point(client.X + _virtualBounds.X, client.Y + _virtualBounds.Y);
        if (_locked && _chain.Count > 0 && _chain[_level].Contains(sp)) return;
        _locked = false;
        var chain = _finder.GetChain(sp);
        if (chain.Count == 0) return;
        if (_chain.Count == 0 || _chain[0] != chain[0]) _level = 0;   // 换到了别的控件，范围级别归零
        _chain = chain;
        _level = Math.Clamp(_level, 0, _chain.Count - 1);
        ApplyHover();
    }

    private void ApplyHover()
    {
        var r = _chain[_level];
        var local = new Rectangle(r.X - _virtualBounds.X, r.Y - _virtualBounds.Y, r.Width, r.Height);
        SetHover(Rectangle.Intersect(local, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_sel.IsEmpty && !_dragging && _chain.Count > 0)
        {
            _level = Math.Clamp(_level + (e.Delta > 0 ? 1 : -1), 0, _chain.Count - 1);   // 上滚=更大，下滚=更小
            _locked = _level > 0;
            ApplyHover();
        }
        base.OnMouseWheel(e);
    }

    // ---------- 鼠标 ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _locked = false;
            if (!_sel.IsEmpty)
            {
                HideToolbar();
                ResetPen();
                SetSelection(Rectangle.Empty);
                UpdateHover(e.Location);
            }
            else CloseNow();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_penMode && !_sel.IsEmpty && _sel.Contains(e.Location))
        {
            // 画笔模式：在选区内按下 = 开始一笔
            _current = new Stroke { Color = _penColor, Width = _penWidth };
            _current.Points.Add(ClampToSel(e.Location));
            InvalidateStroke(_current.Points[0], _current.Points[0], _penWidth);
            return;
        }

        HideToolbar();
        ResetPen();
        SetSelection(Rectangle.Empty);
        _locked = false;
        _downPoint = e.Location;
        _dragging = true;
        _dragMoved = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_current != null)
        {
            var p = ClampToSel(e.Location);
            var last = _current.Points[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2)   // 过滤过密的采样点
            {
                _current.Points.Add(p);
                InvalidateStroke(last, p, _current.Width);
            }
            return;
        }
        if (_dragging)
        {
            if (!_dragMoved)
            {
                if (Math.Abs(e.X - _downPoint.X) <= 3 && Math.Abs(e.Y - _downPoint.Y) <= 3) return;
                _dragMoved = true;
                SetHover(Rectangle.Empty);
            }
            int x1 = Math.Clamp(Math.Min(_downPoint.X, e.X), 0, ClientSize.Width);
            int y1 = Math.Clamp(Math.Min(_downPoint.Y, e.Y), 0, ClientSize.Height);
            int x2 = Math.Clamp(Math.Max(_downPoint.X, e.X), 0, ClientSize.Width);
            int y2 = Math.Clamp(Math.Max(_downPoint.Y, e.Y), 0, ClientSize.Height);
            SetSelection(new Rectangle(x1, y1, x2 - x1, y2 - y1));
            return;
        }
        if (_sel.IsEmpty) UpdateHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_current != null && e.Button == MouseButtons.Left)
        {
            _current.Cached = _current.Points.ToArray();
            _strokes.Add(_current);
            _current = null;
            return;
        }
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;

        if (_dragMoved)
        {
            if (_sel.Width < 4 || _sel.Height < 4) { SetSelection(Rectangle.Empty); UpdateHover(e.Location); return; }
            Commit();
        }
        else if (!_hover.IsEmpty)
        {
            // 单击：选中自动高亮的区域
            var picked = _hover;
            SetHover(Rectangle.Empty);
            SetSelection(picked);
            Commit();
        }
    }

    /// <summary>识字 / 翻译 / 贴图热键：选定后直接执行；普通截图热键：弹出工具栏。</summary>
    private void Commit()
    {
        if (_defaultAction is CaptureAction.Ocr or CaptureAction.Translate or CaptureAction.Pin) Finish(_defaultAction);
        else ShowToolbar();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (_penMode) return;   // 画笔模式下快速连点不应触发“完成”
        if (!_sel.IsEmpty && _sel.Contains(e.Location)) Finish(_defaultAction);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) CloseNow();
        else if (e.Control && e.KeyCode == Keys.Z) UndoStroke();
        else if (e.KeyCode == Keys.Enter && !_sel.IsEmpty) Finish(_defaultAction);
        else base.OnKeyDown(e);
    }

    // ---------- 工具栏 ----------
    private void ShowToolbar()
    {
        HideToolbar();
        var bar = new OverlayToolbar();
        bar.Hovered += k => { if (k is OverlayToolbar.Kind.Ocr or OverlayToolbar.Kind.Translate) OcrService.Prewarm(); };
        bar.Add(OverlayToolbar.Kind.Pen, "画笔", TogglePen);
        bar.Add(OverlayToolbar.Kind.Copy, "复制", () => Finish(CaptureAction.Copy));
        bar.Add(OverlayToolbar.Kind.Save, "保存", () => Finish(CaptureAction.Save));
        bar.Add(OverlayToolbar.Kind.Ocr, "OCR", () => Finish(CaptureAction.Ocr));
        bar.Add(OverlayToolbar.Kind.Translate, "翻译", () => Finish(CaptureAction.Translate));
        bar.Add(OverlayToolbar.Kind.Pin, "贴图", () => Finish(CaptureAction.Pin));
        bar.Add(OverlayToolbar.Kind.Close, "取消", CloseNow);

        Controls.Add(bar);
        var size = bar.Size;
        int x = Math.Max(0, Math.Min(_sel.Right - size.Width, ClientSize.Width - size.Width));
        int y = _sel.Bottom + 10;
        if (y + size.Height > ClientSize.Height) y = _sel.Top - size.Height - 10;
        if (y < 0) y = Math.Max(0, _sel.Bottom - size.Height - 10);
        bar.Location = new Point(x, y);
        bar.BringToFront();
        _toolbar = bar;
    }

    private void HideToolbar()
    {
        if (_penBar != null)
        {
            Controls.Remove(_penBar);
            _penBar.Dispose();
            _penBar = null;
        }
        if (_toolbar == null) return;
        Controls.Remove(_toolbar);
        _toolbar.Dispose();
        _toolbar = null;
    }

    // ---------- 画笔：开关 / 撤销 / 绘制 ----------
    private void TogglePen()
    {
        if (_toolbar == null) return;
        _penMode = !_penMode;
        _toolbar.SetActive(OverlayToolbar.Kind.Pen, _penMode);
        if (!_penMode)
        {
            if (_penBar != null) { Controls.Remove(_penBar); _penBar.Dispose(); _penBar = null; }
            return;   // 关闭画笔只是不再绘制，已画的笔迹保留
        }

        var pb = new PenBar();
        pb.Select(_penColorIdx, _penWidthIdx);
        pb.Changed += () =>
        {
            _penColorIdx = pb.ColorIndex; _penWidthIdx = pb.WidthIndex;
            _penColor = PenBar.Palette[_penColorIdx]; _penWidth = PenBar.Widths[_penWidthIdx];
        };
        pb.UndoClicked += UndoStroke;
        _penColor = PenBar.Palette[_penColorIdx];
        _penWidth = PenBar.Widths[_penWidthIdx];

        Controls.Add(pb);
        int x = Math.Clamp(_toolbar.Right - pb.Width, 0, Math.Max(0, ClientSize.Width - pb.Width));
        int y = _toolbar.Bottom + 6;                       // 默认放在工具栏下方，放不下就放上方
        if (y + pb.Height > ClientSize.Height) y = _toolbar.Top - pb.Height - 6;
        pb.Location = new Point(x, Math.Max(0, y));
        pb.BringToFront();
        _penBar = pb;
    }

    private void UndoStroke()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        Invalidate(_sel);
    }

    private void ResetPen()
    {
        _penMode = false;
        _current = null;
        _strokes.Clear();
    }

    private Point ClampToSel(Point p) => new(Math.Clamp(p.X, _sel.Left, _sel.Right - 1), Math.Clamp(p.Y, _sel.Top, _sel.Bottom - 1));

    private void InvalidateStroke(Point a, Point b, float width)
    {
        int pad = (int)Math.Ceiling(width) + 3;
        Invalidate(Rectangle.Inflate(Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), pad, pad));
    }

    private static void DrawStroke(Graphics g, Stroke s)
    {
        if (s.Points.Count == 1)
        {
            using var b = new SolidBrush(s.Color);
            float r = s.Width / 2f;
            var p = s.Points[0];
            g.FillEllipse(b, p.X - r, p.Y - r, s.Width, s.Width);
            return;
        }
        using var pen = new Pen(s.Color, s.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, s.Cached ?? s.Points.ToArray());
    }

    /// <summary>把所有笔迹画到裁剪出的图片上（offset = 选区左上角在遮罩客户区里的位置）。</summary>
    private void BurnStrokes(Bitmap bmp, Point offset)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(-offset.X, -offset.Y);
        foreach (var s in _strokes) DrawStroke(g, s);
    }

    /// <summary>先立刻隐藏再关闭：不让一个“已决定退出、却还在等 FormClosed 里的重活”的全屏窗口停留在屏幕上。</summary>
    private void CloseNow()
    {
        Hide();
        Close();
    }

    private void Finish(CaptureAction action)
    {
        var r = Rectangle.Intersect(_sel, new Rectangle(0, 0, _shot.Width, _shot.Height));
        if (r.Width < 1 || r.Height < 1) { CloseNow(); return; }
        Cropped = _shot.Clone(r, _shot.PixelFormat);   // 独立副本，遮罩关闭后仍有效
        // 笔迹画进复制 / 保存 / 贴图的图片；识字 / 翻译用干净原图（笔迹会干扰识别）
        if (_strokes.Count > 0 && action is not (CaptureAction.Ocr or CaptureAction.Translate))
            BurnStrokes(Cropped, r.Location);
        ScreenSelection = new Rectangle(_virtualBounds.X + r.X, _virtualBounds.Y + r.Y, r.Width, r.Height);
        ChosenAction = action;
        CloseNow();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _shot.Dispose();
        _dimmed.Dispose();
        _labelFont.Dispose(); _hintFont.Dispose();
        _borderPen.Dispose(); _labelBg.Dispose(); _hintBg.Dispose();
    }
}
