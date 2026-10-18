using System.Drawing.Drawing2D;

namespace LST;

/// <summary>画笔选项条：颜色（6 种）、粗细（3 档）、撤销。点“画笔”后出现在工具栏旁。</summary>
internal sealed class PenBar : Control
{
    public static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48),    // 红
        Color.FromArgb(255, 204, 0),    // 黄
        Color.FromArgb(52, 199, 89),    // 绿
        Color.FromArgb(10, 132, 255),   // 蓝
        Color.White,
        Color.FromArgb(28, 28, 30)      // 黑
    };
    public static readonly float[] Widths = { 3f, 6f, 10f };

    private static readonly Color Normal = Color.FromArgb(43, 56, 77);
    private static readonly Color Hot = Color.FromArgb(66, 86, 116);

    private const int Pad = 6, Cell = 30, CellH = 30, Gap = 12;
    private readonly Rectangle[] _colorRects = new Rectangle[Palette.Length];
    private readonly Rectangle[] _widthRects = new Rectangle[Widths.Length];
    private readonly Rectangle _undoRect;

    public int ColorIndex { get; private set; }
    public int WidthIndex { get; private set; } = 1;

    /// <summary>颜色或粗细改变。</summary>
    public event Action? Changed;
    public event Action? UndoClicked;

    public PenBar()
    {
        int x = Pad;
        for (int i = 0; i < Palette.Length; i++, x += Cell) _colorRects[i] = new Rectangle(x, Pad, Cell, CellH);
        x += Gap - 4;
        for (int i = 0; i < Widths.Length; i++, x += Cell) _widthRects[i] = new Rectangle(x, Pad, Cell, CellH);
        x += Gap - 4;
        _undoRect = new Rectangle(x, Pad, Cell + 6, CellH);
        Size = new Size(_undoRect.Right + Pad, CellH + Pad * 2);

        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    /// <summary>恢复上次选择的颜色/粗细。</summary>
    public void Select(int colorIndex, int widthIndex)
    {
        ColorIndex = Math.Clamp(colorIndex, 0, Palette.Length - 1);
        WidthIndex = Math.Clamp(widthIndex, 0, Widths.Length - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var bg = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
        using (var bb = new SolidBrush(Normal))
            g.FillPath(bb, bg);

        // 颜色
        for (int i = 0; i < Palette.Length; i++)
        {
            var c = Center(_colorRects[i]);
            using (var b = new SolidBrush(Palette[i])) g.FillEllipse(b, c.X - 8, c.Y - 8, 16, 16);
            using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawEllipse(rim, c.X - 8, c.Y - 8, 16, 16);
            if (i == ColorIndex)
                using (var ring = new Pen(Color.White, 2f)) g.DrawEllipse(ring, c.X - 12, c.Y - 12, 24, 24);
        }

        // 粗细：用当前颜色画出对应粗细的圆点
        for (int i = 0; i < Widths.Length; i++)
        {
            var rc = _widthRects[i];
            if (i == WidthIndex)
                using (var p = Rounded(Rectangle.Inflate(rc, -2, -2), 8))
                using (var hb = new SolidBrush(Hot))
                    g.FillPath(hb, p);
            var c = Center(rc);
            float d = Widths[i] + 2;
            using var b = new SolidBrush(Palette[ColorIndex]);
            g.FillEllipse(b, c.X - d / 2, c.Y - d / 2, d, d);
        }

        // 撤销：弧线 + 箭头
        var u = Center(_undoRect);
        using (var pen = new Pen(Color.FromArgb(205, 214, 228), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, u.X - 6, u.Y - 5, 12, 12, 200, 250);
            g.DrawLine(pen, u.X - 7, u.Y - 4, u.X - 7, u.Y + 1);
            g.DrawLine(pen, u.X - 7, u.Y - 4, u.X - 2, u.Y - 4);
        }
    }

    private static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            for (int i = 0; i < _colorRects.Length; i++)
                if (_colorRects[i].Contains(e.Location)) { ColorIndex = i; Invalidate(); Changed?.Invoke(); break; }
            for (int i = 0; i < _widthRects.Length; i++)
                if (_widthRects[i].Contains(e.Location)) { WidthIndex = i; Invalidate(); Changed?.Invoke(); break; }
            if (_undoRect.Contains(e.Location)) UndoClicked?.Invoke();
        }
        base.OnMouseUp(e);
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
}
