#nullable disable
// 自绘小控件：圆角按钮、分段选择器、图钉（带旋转动效）、输入框占位符、Logo
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace FloatBall
{
    class RBtn : Control
    {
        public Color Bg = Color.Transparent, BgHover = Color.Transparent, Fg = Color.Gray, FgHover = Color.White;
        public int Radius = 8;
        bool over, down;

        public RBtn()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = false;
        }
        public void Style(Color bg, Color bgh, Color fg, Color fgh) { Bg = bg; BgHover = bgh; Fg = fg; FgHover = fgh; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = (over || down) ? BgHover : Bg;
            if (c.A > 0)
            {
                using (GraphicsPath p = Gfx.RR(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Radius))
                using (SolidBrush b = new SolidBrush(down ? Gfx.Mix(c, Color.Black, 0.08f) : c)) g.FillPath(b, p);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, over ? FgHover : Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // 圆角输入框外壳：聚焦时显示强调色描边
    class RoundBox : Panel
    {
        public bool Focus2;
        public Color Fill;
        public int Radius = 14;
        public RoundBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Gfx.RR(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Radius))
            {
                using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(Focus2 ? Theme.Accent : Theme.Border, Focus2 ? 1.6f : 1f)) g.DrawPath(pen, p);
            }
        }
    }

    // 带占位提示的多行输入框（提示由自身绘制，不遮挡光标）
    class PlaceholderBox : TextBox
    {
        public string Placeholder = "";
        public Color PhColor = Color.Gray;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0xF && TextLength == 0 && !Focused && Placeholder.Length > 0)
            {
                using (Graphics g = CreateGraphics())
                    TextRenderer.DrawText(g, Placeholder, Font, new Rectangle(1, 1, Width - 2, Height - 2), PhColor, BackColor,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // 分段选择器：高亮滑块平滑移动
    class Segmented : Control
    {
        public string[] Items = new string[0];
        int sel; float hx = -1; readonly WinTimer anim = new WinTimer();
        public event EventHandler Changed;

        public Segmented()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
            anim.Interval = 16; anim.Tick += delegate { Step(); };
        }
        public int Selected
        {
            get { return sel; }
            set { if (value == sel) return; sel = value; anim.Start(); Invalidate(); }
        }
        // 构造时控件还没布局（Width=0），此时算出的 hx 会一直是 0，
        // 导致 mode=1 打开时“翻译”页却高亮“问答”。先置为 -1，等真正有宽度后再对准。
        public void SetInstant(int v) { sel = v; hx = -1; anim.Stop(); Invalidate(); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!anim.Enabled) { hx = Target(); Invalidate(); }
        }
        float SegW() { return Items.Length == 0 ? 1 : (Width - 6f) / Items.Length; }
        float Target() { return 3f + sel * SegW(); }
        void Step()
        {
            float t = Target();
            if (hx < 0) hx = t;
            hx += (t - hx) * 0.32f;
            if (Math.Abs(t - hx) < 0.4f) { hx = t; anim.Stop(); }
            Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Items.Length == 0) return;
            int i = (int)((e.X - 3) / SegW());
            i = Math.Max(0, Math.Min(Items.Length - 1, i));
            if (i != sel) { Selected = i; if (Changed != null) Changed(this, EventArgs.Empty); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hx < 0) hx = Target();
            using (GraphicsPath p = Gfx.RR(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            using (SolidBrush b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
            float sw = SegW();
            using (GraphicsPath p = Gfx.RR(new RectangleF(hx, 3, sw, Height - 7), (Height - 7) / 2f))
            using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            for (int i = 0; i < Items.Length; i++)
            {
                float dist = Math.Min(1f, Math.Abs((3f + i * sw) - hx) / sw);
                Color c = Gfx.Mix(Theme.OnAccent, Theme.Sub, dist);
                TextRenderer.DrawText(g, Items[i], Font, new Rectangle((int)(3 + i * sw), 0, (int)sw, Height), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
    }

    // 图钉按钮：固定时轻轻“按下”摆正（小幅回弹），取消时倾斜回去；固定后变强调色 + 淡淡的底色圆
    class PinButton : Control
    {
        public bool On;
        float angle = 40f, from, to, t;
        readonly WinTimer anim = new WinTimer();
        DateTime t0; bool over, rippling;

        public PinButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
            anim.Interval = 16; anim.Tick += delegate { Step(); };
        }
        public void SetOn(bool on, bool animate)
        {
            On = on; to = on ? 0f : 40f;
            if (!animate) { angle = to; Invalidate(); return; }
            from = angle; t0 = DateTime.Now; t = 0; rippling = on; anim.Start();
        }
        static float EaseOutBack(float x)
        {
            const float c1 = 1.1f; float y = x - 1f;
            return 1f + (c1 + 1f) * y * y * y + c1 * y * y;
        }
        void Step()
        {
            t = (float)((DateTime.Now - t0).TotalMilliseconds / 260.0);
            if (t >= 1f) { t = 1f; angle = to; anim.Stop(); rippling = false; Invalidate(); return; }
            angle = from + (to - from) * EaseOutBack(t);
            Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f;
            if (On || over)
                using (GraphicsPath p = Gfx.RR(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Width / 2f))
                using (SolidBrush b = new SolidBrush(On ? Color.FromArgb(52, Theme.Accent) : Theme.Hover)) g.FillPath(b, p);
            if (rippling)
            {
                float rr = Width * (0.42f + 0.18f * t);
                using (Pen p = new Pen(Color.FromArgb((int)((1f - t) * 150), Theme.Accent), 1.6f)) g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
            }
            float s = Height / 44f;
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy + 0.5f * s);
            g.RotateTransform(angle);
            g.ScaleTransform(s, s);
            PointF[] head = new PointF[] { new PointF(-3f, -8f), new PointF(3f, -8f), new PointF(3f, -3.4f), new PointF(6.2f, 0f),
                                           new PointF(-6.2f, 0f), new PointF(-3f, -3.4f) };
            Color c = On ? Theme.Accent : (over ? Theme.Fg : Theme.Sub);
            if (On) using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, head);
            else using (Pen p = new Pen(c, 1.5f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, head); }
            using (Pen p = new Pen(c, 1.7f)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, 0, 0, 0, 6.5f); }
            g.Restore(st);
        }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
    }

    // 发送按钮：渐变圆形 + 手绘向上箭头；生成中变成“停止”方块并带旋转光圈
    class SendButton : Control
    {
        public bool Busy, Ready;
        bool over, down; float spin;
        readonly WinTimer t = new WinTimer();
        public SendButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
            t.Interval = 30; t.Tick += delegate { spin = (spin + 14f) % 360f; Invalidate(); };
        }
        public void SetBusy(bool b) { Busy = b; if (b) t.Start(); else t.Stop(); Invalidate(); }
        public void SetReady(bool r) { if (Ready != r) { Ready = r; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Math.Min(Width, Height) - 2f; float x = (Width - d) / 2f, y = (Height - d) / 2f;
            RectangleF r = new RectangleF(x, y, d, d);
            bool active = Ready || Busy;
            Color c1 = over ? Theme.AccentHi : Theme.Accent;
            if (!active) c1 = Gfx.Mix(Theme.Accent, Theme.Surface, 0.62f);
            if (down) c1 = Gfx.Mix(c1, Color.Black, 0.1f);
            using (GraphicsPath p = Gfx.RR(r, d * 0.3f)) using (SolidBrush b = new SolidBrush(c1)) g.FillPath(b, p);
            Color ink = Theme.OnAccent;
            float cx = r.X + d / 2f, cy = r.Y + d / 2f, u = d / 40f;
            if (Busy)
            {
                using (SolidBrush b = new SolidBrush(ink)) using (GraphicsPath p = Gfx.RR(new RectangleF(cx - 5f * u, cy - 5f * u, 10f * u, 10f * u), 2f * u)) g.FillPath(b, p);
                using (Pen p = new Pen(Color.FromArgb(190, ink), 1.6f)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawArc(p, r.X + 3.5f, r.Y + 3.5f, d - 7f, d - 7f, spin, 80f); }
            }
            else
            {
                using (Pen p = new Pen(ink, 2.2f * u + 0.3f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLine(p, cx, cy + 5f * u, cx, cy - 5.5f * u);
                    g.DrawLines(p, new PointF[] { new PointF(cx - 5f * u, cy - 0.5f * u), new PointF(cx, cy - 5.8f * u), new PointF(cx + 5f * u, cy - 0.5f * u) });
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }
    }

    // 开关：圆角轨道 + 滑动圆点，文字在右侧（避免系统复选框被裁切）
    class Toggle : Control
    {
        public bool Checked;
        public Toggle()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { Checked = !Checked; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int tw = Dpi.S(38), th = Dpi.S(20), ty = (Height - th) / 2;
            using (GraphicsPath p = Gfx.RR(new RectangleF(0.5f, ty + 0.5f, tw - 1, th - 1), th / 2f))
            {
                using (SolidBrush b = new SolidBrush(Checked ? Theme.Accent : Gfx.Mix(Theme.Border, Theme.Sub, 0.25f))) g.FillPath(b, p);
            }
            float kd = th - 6f, kx = Checked ? tw - kd - 3f : 3f;
            using (SolidBrush b = new SolidBrush(Checked ? Theme.OnAccent : Color.White)) g.FillEllipse(b, kx, ty + 3f, kd, kd);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + Dpi.S(8), 0, Width - tw - Dpi.S(8), Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // 标题栏里的小行星 logo，生成回答时会加速公转
    class LogoBox : Control
    {
        readonly BallVis v = new BallVis();
        readonly WinTimer t = new WinTimer();
        DateTime last = DateTime.Now;
        public LogoBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent; v.Time = 0.6f; v.Orbit = 2.3f;
            t.Interval = 40;
            t.Tick += delegate
            {
                DateTime n = DateTime.Now; float dt = (float)(n - last).TotalSeconds; last = n;
                v.Time += dt; v.Orbit += dt * 6.5f; Invalidate();
            };
        }
        public void SetThinking(bool on)
        {
            v.Thinking = on;
            if (on) { last = DateTime.Now; t.Start(); } else { t.Stop(); }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) { BallArt.Draw(e.Graphics, Width, (int)(Width * 0.98f), v); }
        protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }
    }
}
