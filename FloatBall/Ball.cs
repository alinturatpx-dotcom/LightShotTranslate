#nullable disable
// 悬浮球：莫兰蒂色系的小行星图标 + 吸附/缩进/呼吸动效
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace FloatBall
{
    class BallVis
    {
        public float Time, Orbit, Hover, Pulse, Think;
        public bool Thinking, Unread;
    }

    static class BallArt
    {
        // 土星常见配色：金黄的星体、奶油色光环、冰蓝色小卫星
        static readonly Color Oat = Color.FromArgb(0xF4, 0xE8, 0xCB);      // 光环
        static readonly Color Rose = Color.FromArgb(0xA9, 0xC4, 0xE4);     // 小卫星（冰蓝）
        static readonly Color Sage = Color.FromArgb(0x7F, 0xC8, 0xA0);     // 未读提示点
        static readonly Color Haze1 = Color.FromArgb(0xF3, 0xDE, 0xA8);    // 星体亮部
        static readonly Color Haze2 = Color.FromArgb(0xC4, 0x93, 0x4F);    // 星体暗部
        static readonly Color Lilac = Color.FromArgb(0xF2, 0xD3, 0x8A);    // 光晕/涟漪（暖金）
        static readonly Color Ink = Color.FromArgb(0x8A, 0x5E, 0x2A);      // 条纹/内环

        const float RingTilt = -20f, MoonTilt = 30f;

        static PointF Orb(float cx, float cy, float rx, float ry, float tiltDeg, float th, out bool front)
        {
            double t = tiltDeg * Math.PI / 180.0;
            double ox = rx * Math.Cos(th), oy = ry * Math.Sin(th);
            double X = ox * Math.Cos(t) - oy * Math.Sin(t), Y = ox * Math.Sin(t) + oy * Math.Cos(t);
            front = Math.Sin(th) > 0;
            return new PointF(cx + (float)X, cy + (float)Y);
        }

        static void RingHalf(Graphics g, float cx, float cy, float rx, float ry, float w, Color c, bool front)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(RingTilt);
            using (Pen p = new Pen(c, w))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, -rx, -ry, 2 * rx, 2 * ry, front ? 0 : 180, 180);
            }
            g.Restore(st);
        }

        static void Sparkle(Graphics g, float cx, float cy, float R, Color c)
        {
            PointF[] pts = new PointF[8];
            double inner = R * 0.28;
            for (int i = 0; i < 8; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 4;
                double rr = (i % 2 == 0) ? R : inner;
                pts[i] = new PointF((float)(cx + rr * Math.Cos(a)), (float)(cy + rr * Math.Sin(a)));
            }
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        // 小月亮：平时慢速公转；思考时加速并拖出彗尾（只有一颗）
        static void DrawMoons(Graphics g, float cx, float cy, float u, float mr, BallVis v, bool pass)
        {
            Moon(g, cx, cy, u * 0.45f, u * 0.27f, MoonTilt, v.Orbit, mr, Rose, v.Think, pass, 1f, 1f);
        }

        static void Moon(Graphics g, float cx, float cy, float rx, float ry, float tilt, float th, float mr, Color col, float think, bool pass, float vis, float dirSign)
        {
            int trail = (int)Math.Round(7 * think);
            for (int k = trail; k >= 0; k--)
            {
                bool front;
                PointF p = Orb(cx, cy, rx, ry, tilt, th - dirSign * k * 0.14f, out front);
                if (front != pass) continue;
                float f = 1f - k / (float)(trail + 1);
                float rr = mr * (k == 0 ? 1f : (0.35f + 0.55f * f));
                int a = (int)((front ? 235 : 140) * (k == 0 ? 1f : f * 0.6f) * vis);
                if (k == 0 && think > 0.02f)
                    using (SolidBrush gl = new SolidBrush(Color.FromArgb((int)(60 * think * vis), col))) g.FillEllipse(gl, p.X - rr * 2f, p.Y - rr * 2f, rr * 4f, rr * 4f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a)), col))) g.FillEllipse(b, p.X - rr, p.Y - rr, rr * 2, rr * 2);
                if (k == 0 && front)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 255, 255, 255))) g.FillEllipse(b, p.X - rr * 0.6f, p.Y - rr * 0.7f, rr * 0.8f, rr * 0.6f);
            }
        }

        public static void Draw(Graphics g, int W, int S, BallVis v)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = W / 2f, cy = W / 2f;
            float breathe = 1f + 0.014f * (float)Math.Sin(v.Time * 1.7);
            float u = S * breathe * (1f + 0.07f * v.Hover);
            float r = u * 0.30f;

            // 近乎透明的底盘：让整个球体区域都能被鼠标命中
            using (SolidBrush hit = new SolidBrush(Color.FromArgb(3, 0, 0, 0)))
                g.FillEllipse(hit, cx - u / 2f, cy - u / 2f, u, u);

            // 悬停光晕
            float glow = Math.Max(v.Hover, v.Think * (0.55f + 0.45f * (float)Math.Sin(v.Time * 6.0)));
            if (glow > 0.01f)
                for (int i = 4; i >= 1; i--)
                {
                    float gr = r * (1.05f + i * 0.2f);
                    int a = (int)(glow * (9 + (5 - i) * 4));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Lilac))) g.FillEllipse(b, cx - gr, cy - gr, gr * 2, gr * 2);
                }

            // 投影
            for (int i = 3; i >= 1; i--)
            {
                float sr = r + i * 1.4f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(15, 20, 22, 34)))
                    g.FillEllipse(b, cx - sr, cy - sr + u * 0.05f, sr * 2, sr * 2);
            }

            float rw = Math.Max(1.8f, u * 0.062f);
            float rx = u * 0.47f, ry = u * 0.14f;

            // 光环后半 + 月亮（背面）
            RingHalf(g, cx, cy, rx, ry, rw, Color.FromArgb(120, Oat), false);
            RingHalf(g, cx, cy, rx * 0.78f, ry * 0.78f, rw * 0.5f, Color.FromArgb(90, Ink), false);
            float mr = Math.Max(2f, u * 0.062f);
            DrawMoons(g, cx, cy, u, mr, v, false);

            // 行星本体
            RectangleF br = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (LinearGradientBrush lg = new LinearGradientBrush(br, Haze1, Haze2, 50f)) g.FillEllipse(lg, br);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(br);
                g.SetClip(gp);
                using (Pen p = new Pen(Color.FromArgb(95, 255, 250, 235), r * 0.17f))
                    g.DrawArc(p, cx - r * 1.5f, cy - r * 0.62f, r * 3f, r * 1.2f, 15, 150);
                using (Pen p = new Pen(Color.FromArgb(120, Ink), r * 0.17f))
                    g.DrawArc(p, cx - r * 1.5f, cy - r * 0.05f, r * 3f, r * 1.2f, 15, 150);
                using (LinearGradientBrush sh = new LinearGradientBrush(br, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(85, 0x5A, 0x38, 0x10), 45f))
                    g.FillEllipse(sh, br);
                using (SolidBrush hi = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                    g.FillEllipse(hi, cx - r * 0.66f, cy - r * 0.74f, r * 0.72f, r * 0.36f);
                g.ResetClip();
            }
            using (Pen rim = new Pen(Color.FromArgb(60, 255, 255, 255), 1f)) g.DrawEllipse(rim, br);

            // 光环前半 + 月亮（正面）
            RingHalf(g, cx, cy, rx, ry, rw, Color.FromArgb(240, Oat), true);
            RingHalf(g, cx, cy, rx * 0.78f, ry * 0.78f, rw * 0.5f, Color.FromArgb(190, Ink), true);
            DrawMoons(g, cx, cy, u, mr, v, true);

            // 星点
            float[][] stars = new float[][] { new float[] { -0.38f, -0.34f, 0f }, new float[] { 0.40f, 0.30f, 2.1f }, new float[] { -0.26f, 0.40f, 4.0f } };
            foreach (float[] s in stars)
            {
                float tw = 0.5f + 0.5f * (float)Math.Sin(v.Time * 2.2 + s[2]);
                Sparkle(g, cx + s[0] * u, cy + s[1] * u, u * 0.095f * (0.6f + 0.4f * tw), Color.FromArgb((int)(120 + 135 * tw), 255, 244, 214));
            }

            // 点击涟漪
            if (v.Pulse > 0f)
            {
                float pr = r * (1.15f + (1f - v.Pulse) * 1.2f);
                using (Pen p = new Pen(Color.FromArgb((int)(v.Pulse * 190), Lilac), 2f)) g.DrawEllipse(p, cx - pr, cy - pr, pr * 2, pr * 2);
            }

            // 未读提示：鼠尾草绿 + 呼吸圈
            if (v.Unread)
            {
                float dr = u * 0.075f, dx = cx + u * 0.36f, dy = cy - u * 0.36f;
                float ph = (v.Time * 1.1f) % 1f;
                using (Pen p = new Pen(Color.FromArgb((int)((1f - ph) * 150), Sage), 1.6f))
                {
                    float pr = dr * (1f + ph * 1.4f);
                    g.DrawEllipse(p, dx - pr, dy - pr, pr * 2, pr * 2);
                }
                using (SolidBrush w = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.FillEllipse(w, dx - dr - 1.5f, dy - dr - 1.5f, (dr + 1.5f) * 2, (dr + 1.5f) * 2);
                using (SolidBrush b = new SolidBrush(Sage)) g.FillEllipse(b, dx - dr, dy - dr, dr * 2, dr * 2);
            }
        }

        // 小头像（对话卡片里用）
        public static void DrawMini(Graphics g, RectangleF rc)
        {
            SmoothingMode old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = rc.X + rc.Width / 2f, cy = rc.Y + rc.Height / 2f, r = rc.Width * 0.30f;
            RingHalf(g, cx, cy, rc.Width * 0.47f, rc.Width * 0.14f, Math.Max(1.2f, rc.Width * 0.07f), Color.FromArgb(120, Oat), false);
            RectangleF br = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (LinearGradientBrush lg = new LinearGradientBrush(br, Haze1, Haze2, 50f)) g.FillEllipse(lg, br);
            RingHalf(g, cx, cy, rc.Width * 0.47f, rc.Width * 0.14f, Math.Max(1.2f, rc.Width * 0.07f), Oat, true);
            g.SmoothingMode = old;
        }

        public static Icon MakeIcon(int px)
        {
            using (Bitmap bmp = RenderFrame(px))
            {
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        public static Bitmap RenderFrame(int px)
        {
            Bitmap bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                BallVis v = new BallVis(); v.Time = 0.5f; v.Orbit = 2.35f;
                Draw(g, px, (int)(px * 1.0f), v);
            }
            return bmp;
        }
    }

    class BallForm : Form
    {
        readonly AppCtx app;
        readonly Cfg cfg;
        int S, Pad, W;
        bool hover, thinking, unread, dragging, moved, peeked;
        int dock; // -1 左 / 0 无 / 1 右
        Point dragCursor, dragLoc;
        DateTime lastActive = DateTime.Now, downAt = DateTime.MinValue;
        readonly WinTimer tick = new WinTimer();
        readonly Stopwatch sw = Stopwatch.StartNew();
        long lastMs;
        readonly BallVis vis = new BallVis();
        Bitmap frame; Graphics fg;   // 复用的绘图缓存，只在尺寸变化时重新分配

        public BallForm(AppCtx a, Cfg c)
        {
            Dpi.Freeze(this);
            app = a; cfg = c;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            AllowDrop = true;
            ApplyConfig();
            PlaceInitial();
            tick.Interval = 66; tick.Tick += OnTick; tick.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            try { if (IsHandleCreated) BeginInvoke((Action)delegate { SnapToEdge(); }); } catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public void ApplyConfig()
        {
            S = Dpi.S(cfg.BallSize); Pad = Dpi.S(10); W = S + 2 * Pad;
            Size = new Size(W, W);
            if (fg != null) { fg.Dispose(); fg = null; }
            if (frame != null) { frame.Dispose(); frame = null; }
            Redraw();
        }

        void PlaceInitial()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            bool ok = false;
            if (cfg.BallX != -99999)
            {
                Rectangle probe = new Rectangle(cfg.BallX, cfg.BallY, W, W);
                foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(probe)) ok = true;
            }
            if (ok) Location = new Point(cfg.BallX, cfg.BallY);
            else Location = new Point(wa.Right - S - Pad, wa.Top + wa.Height / 3);
            SnapToEdge();
        }

        Rectangle WA() { return Screen.FromRectangle(Bounds).WorkingArea; }
        int RestX(Rectangle wa) { return dock < 0 ? wa.Left - Pad : wa.Right - S - Pad; }
        int PeekX(Rectangle wa) { int o = (int)(S * 0.55); return dock < 0 ? RestX(wa) - o : RestX(wa) + o; }
        bool OuterEdge(Rectangle wa)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            return dock < 0 ? wa.Left <= vs.Left : wa.Right >= vs.Right;
        }

        public Rectangle CircleRest()
        {
            Rectangle wa = WA();
            int x = dock != 0 ? RestX(wa) : Left;
            return new Rectangle(x + Pad, Top + Pad, S, S);
        }

        public void SnapToEdge()
        {
            Rectangle wa = WA();
            int thr = Dpi.S(56);
            int cl = Left + Pad, cr = cl + S;
            int dl = cl - wa.Left, dr = wa.Right - cr;
            if (dl <= dr && dl < thr) dock = -1;
            else if (dr < thr) dock = 1;
            else dock = 0;
            int y = Math.Max(wa.Top - Pad, Math.Min(Top, wa.Bottom - S - Pad));
            int x = dock != 0 ? RestX(wa) : Math.Max(wa.Left - Pad, Math.Min(Left, wa.Right - S - Pad));
            Location = new Point(x, y);
            lastActive = DateTime.Now;
        }

        void SavePos() { cfg.BallX = Left; cfg.BallY = Top; cfg.Save(); }

        public void SetThinking(bool v) { thinking = v; vis.Thinking = v; lastActive = DateTime.Now; Redraw(); }
        public void SetUnread(bool v) { unread = v; vis.Unread = v; lastActive = DateTime.Now; Redraw(); }

        public void Redraw()
        {
            if (!IsHandleCreated || !Visible) return;
            bool full = hover || thinking || unread || dragging || app.PanelVisible;
            byte alpha = full ? (byte)255 : (byte)(cfg.IdleOpacity * 255 / 100);
            if (frame == null) { frame = new Bitmap(W, W, PixelFormat.Format32bppPArgb); fg = Graphics.FromImage(frame); }
            fg.Clear(Color.Transparent);
            BallArt.Draw(fg, W, S, vis);
            Native.SetLayeredBitmap(Handle, frame, alpha);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                tick.Stop(); tick.Dispose();
                if (fg != null) fg.Dispose();
                if (frame != null) frame.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) { lastActive = DateTime.Now; Redraw(); } }

        void OnTick(object sender, EventArgs e)
        {
            long now = sw.ElapsedMilliseconds;
            float dt = Math.Min(0.25f, (now - lastMs) / 1000f); lastMs = now;
            vis.Time += dt;
            float tt = thinking ? 1f : 0f;
            bool anim = false, moving = false;
            if (Math.Abs(vis.Think - tt) > 0.01f) { vis.Think += (tt - vis.Think) * Math.Min(1f, dt * 6f); anim = true; } else vis.Think = tt;
            vis.Orbit += dt * (0.8f + 11.5f * vis.Think);
            float ht = (hover || dragging) ? 1f : 0f;
            if (Math.Abs(vis.Hover - ht) > 0.01f) { vis.Hover += (ht - vis.Hover) * Math.Min(1f, dt * 14f); anim = true; } else vis.Hover = ht;
            if (vis.Pulse > 0f) { vis.Pulse -= dt * 2.2f; if (vis.Pulse < 0f) vis.Pulse = 0f; anim = true; }

            peeked = false;
            if (Visible && !dragging && dock != 0)
            {
                Rectangle wa = WA();
                bool want = OuterEdge(wa) && !hover && !app.PanelVisible && !thinking && !unread
                            && (DateTime.Now - lastActive).TotalSeconds > 2.5;
                int tx = want ? PeekX(wa) : RestX(wa);
                if (Left != tx)
                {
                    int d = tx - Left; int step = d / 3;
                    if (step == 0) step = d > 0 ? 1 : -1;
                    Left += step; moving = true;
                }
                peeked = want && Left == tx;
            }
            bool idleAnim = cfg.Animate && Visible && !peeked;
            if (Visible && (anim || thinking || unread || idleAnim || moving)) Redraw();
            int iv = (anim || thinking || moving) ? 33 : ((idleAnim || unread) ? 66 : 250);
            if (tick.Interval != iv) tick.Interval = iv;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; lastActive = DateTime.Now; Redraw(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; lastActive = DateTime.Now; Redraw(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); lastActive = DateTime.Now;
            if (e.Button == MouseButtons.Left) { downAt = DateTime.Now; dragging = true; moved = false; dragCursor = Cursor.Position; dragLoc = Location; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); lastActive = DateTime.Now;
            if (dragging)
            {
                Point c = Cursor.Position;
                int dx = c.X - dragCursor.X, dy = c.Y - dragCursor.Y;
                if (!moved && (Math.Abs(dx) > Dpi.S(4) || Math.Abs(dy) > Dpi.S(4))) moved = true;
                if (moved) Location = new Point(dragLoc.X + dx, dragLoc.Y + dy);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = dragging; dragging = false; lastActive = DateTime.Now;
            if (e.Button == MouseButtons.Left && wasDrag)
            {
                if (moved) { SnapToEdge(); SavePos(); }
                else
                {
                    vis.Pulse = 1f;
                    // 按下的瞬间面板若因失焦被自动收起，这次点击的目的就是“收起”，不要再弹出来
                    if (!app.AutoHiddenSince(downAt)) app.TogglePanel(true);
                }
            }
            else if (e.Button == MouseButtons.Right) app.ShowBallMenu();
            else if (e.Button == MouseButtons.Middle) app.AskClipboard();
        }

        // 把文字拖到悬浮球上 → 打开对话并填入输入框（不会自动发送）
        protected override void OnDragEnter(DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok) { vis.Pulse = 1f; }
        }
        protected override void OnDragDrop(DragEventArgs e)
        {
            string t = e.Data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(t)) t = e.Data.GetData(DataFormats.Text) as string;
            if (!string.IsNullOrEmpty(t)) app.AskWithText(t);
        }
    }
}
