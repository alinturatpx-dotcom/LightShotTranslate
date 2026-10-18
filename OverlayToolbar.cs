using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LST;

/// <summary>自绘图标工具栏（深蓝底、图标在上文字在下，悬停高亮）。截图遮罩与贴图窗口共用。</summary>
internal sealed class OverlayToolbar : Control
{
    public enum Kind { Pen, Copy, Save, Ocr, Translate, Pin, Swap, Close }

    private sealed record Item(Kind Kind, string Label, Action Click);

    private static readonly Color Normal = Color.FromArgb(43, 56, 77);
    private static readonly Color Hot = Color.FromArgb(66, 86, 116);

    private readonly List<Item> _items = new();
    private readonly bool _compact;
    private readonly int _itemW, _itemH, _pad;
    private int _hot = -1;

    /// <summary>鼠标移到某个按钮上（用于“识字/翻译”悬停时提前预热 OCR 子进程）。</summary>
    public event Action<Kind>? Hovered;
    private Kind? _active;   // 处于开启状态的开关项（如“画笔”）

    /// <param name="compact">紧凑模式：只显示图标，用于贴图窗口。</param>
    public OverlayToolbar(bool compact = false)
    {
        _compact = compact;
        _itemW = compact ? 40 : 62;
        _itemH = compact ? 34 : 56;
        _pad = compact ? 4 : 6;
        // 透明背景 + 抗锯齿圆角（不使用 Region 裁剪，否则圆角边缘会有锯齿）
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    public void Add(Kind kind, string label, Action click)
    {
        _items.Add(new Item(kind, label, click));
        Size = new Size(_pad * 2 + _items.Count * _itemW, _itemH + _pad * 2);
    }

    /// <summary>设置某个开关项（如画笔）的高亮状态。</summary>
    public void SetActive(Kind kind, bool on)
    {
        var next = on ? kind : (Kind?)null;
        if (_active == next) return;
        _active = next;
        Invalidate();
    }

    // ---------- 绘制 ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var bgPath = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
        using (var bgBrush = new SolidBrush(Normal))
            g.FillPath(bgBrush, bgPath);

        using var labelFont = new Font("Microsoft YaHei UI", 9f);
        using var sf = new StringFormat { Alignment = StringAlignment.Center };

        for (int i = 0; i < _items.Count; i++)
        {
            var rc = new Rectangle(_pad + i * _itemW, _pad, _itemW, _itemH);
            bool hot = i == _hot || _active == _items[i].Kind;
            if (hot)
            {
                using var path = Rounded(Rectangle.Inflate(rc, -2, -2), 8);
                using var hb = new SolidBrush(Hot);
                g.FillPath(hb, path);
            }

            var color = hot ? Color.White : Color.FromArgb(205, 214, 228);
            float cx = rc.X + rc.Width / 2f;
            float cy = _compact ? rc.Y + rc.Height / 2f : rc.Y + 19f;
            DrawIcon(g, _items[i].Kind, cx, cy, color, hot ? Hot : Normal);

            if (!_compact)
            {
                using var br = new SolidBrush(color);
                g.DrawString(_items[i].Label, labelFont, br, new RectangleF(rc.X, rc.Y + 33, rc.Width, 20), sf);
            }
        }
    }

    private static void DrawIcon(Graphics g, Kind kind, float cx, float cy, Color color, Color bg)
    {
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case Kind.Pen:
                var sp = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(45);
                g.DrawRectangle(pen, -3, -9, 6, 12);      // 笔杆
                g.DrawLine(pen, -3, 3, 0, 9);             // 笔尖
                g.DrawLine(pen, 3, 3, 0, 9);
                g.DrawLine(pen, -3, -5, 3, -5);           // 笔帽分隔线
                g.Restore(sp);
                break;

            case Kind.Copy:
                g.DrawRectangle(pen, cx - 3, cy - 8, 10, 10);
                using (var occ = new SolidBrush(bg)) g.FillRectangle(occ, cx - 8, cy - 3, 10, 10);
                g.DrawRectangle(pen, cx - 8, cy - 3, 10, 10);
                break;

            case Kind.Save:
                g.DrawRectangle(pen, cx - 8, cy - 8, 16, 16);
                g.DrawRectangle(pen, cx - 4, cy - 8, 8, 5);
                g.DrawRectangle(pen, cx - 5, cy + 1, 10, 7);
                break;

            case Kind.Ocr:
                // 四个取景角 + 中间两行文字线
                foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                {
                    float x = cx + sx * 8, y = cy + sy * 8;
                    g.DrawLine(pen, x, y, x - sx * 5, y);
                    g.DrawLine(pen, x, y, x, y - sy * 5);
                }
                g.DrawLine(pen, cx - 4, cy - 2, cx + 4, cy - 2);
                g.DrawLine(pen, cx - 4, cy + 3, cx + 1, cy + 3);
                break;

            case Kind.Translate:
                using (var f = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(color))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("文A", f, br, new RectangleF(cx - 14, cy - 10, 28, 20), sf);
                break;

            case Kind.Pin:
                var st = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(40);
                g.DrawRectangle(pen, -3, -9, 6, 8);
                g.DrawLine(pen, -6, -1, 6, -1);
                g.DrawLine(pen, 0, -1, 0, 8);
                g.Restore(st);
                break;

            case Kind.Swap:
                g.DrawLine(pen, cx - 8, cy - 4, cx + 8, cy - 4);
                g.DrawLine(pen, cx + 8, cy - 4, cx + 4, cy - 8);
                g.DrawLine(pen, cx + 8, cy - 4, cx + 4, cy);
                g.DrawLine(pen, cx + 8, cy + 5, cx - 8, cy + 5);
                g.DrawLine(pen, cx - 8, cy + 5, cx - 4, cy + 1);
                g.DrawLine(pen, cx - 8, cy + 5, cx - 4, cy + 9);
                break;

            case Kind.Close:
                g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                g.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
                break;
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- 交互 ----------
    private int HitTest(Point p)
    {
        if (p.X < _pad || p.Y < _pad || p.Y > _pad + _itemH) return -1;
        int i = (p.X - _pad) / _itemW;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); if (h >= 0) Hovered?.Invoke(_items[h].Kind); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int i = HitTest(e.Location);
            if (i >= 0) _items[i].Click();
        }
        base.OnMouseUp(e);
    }
}
