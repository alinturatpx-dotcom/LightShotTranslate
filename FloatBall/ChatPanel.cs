#nullable disable
// 对话面板：圆角窗口 / 问答·翻译两个相互独立的会话（各自的历史、草稿、请求状态）/ 流式输出
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace FloatBall
{
    class ChatPanel : Form
    {
        // 一个模式 = 一个独立会话：各自的历史、列表视图、输入草稿和请求状态，切换模式互不干扰
        class Sess
        {
            public int Mode; public List<Msg> Hist; public ChatView View; public string Draft = "";
            public bool Busy; public ChatClient Client;
            public readonly object Lock = new object();
            public readonly StringBuilder Buf = new StringBuilder();
            public readonly StringBuilder Answer = new StringBuilder();
        }
        readonly Sess[] ss = new Sess[2];
        Sess Cur { get { return ss[mode]; } }
        bool switching;

        readonly AppCtx app; readonly Cfg cfg;
        LogoBox logo; Label title, sub, keyHint;
        PinButton pin; RBtn bSet, bClose, bPaste, bRetry, bNew; SendButton bSend;
        Segmented seg; RoundBox inputBox; PlaceholderBox input;
        Font fBody, fBold, fSmall, fIcon;
        int mode;
        bool useRegion, warming;
        CornerLayer[] corners;
        readonly WinTimer flush = new WinTimer();
        readonly WinTimer fade = new WinTimer();
        DateTime shownAt = DateTime.MinValue;
        int Rad { get { return useRegion ? Dpi.S(10) : Dpi.S(12); } }

        public ChatPanel(AppCtx a, Cfg c, List<Msg> chatHist, List<Msg> transHist)
        {
            Dpi.Freeze(this);
            app = a; cfg = c;
            ss[0] = new Sess(); ss[0].Mode = 0; ss[0].Hist = chatHist;
            ss[1] = new Sess(); ss[1].Mode = 1; ss[1].Hist = transHist;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; KeyPreview = true;
            ResizeRedraw = true; DoubleBuffered = true;
            BackColor = Theme.Bg; ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9f);
            fBody = new Font("Microsoft YaHei UI", 10f);
            fBold = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            fSmall = new Font("Microsoft YaHei UI", 8f);
            fIcon = new Font("Segoe MDL2 Assets", 10f);
            Size = new Size(Dpi.S(cfg.PanelW), Dpi.S(cfg.PanelH));
            MinimumSize = new Size(Dpi.S(360), Dpi.S(480));
            Build();
            mode = cfg.Mode; seg.SetInstant(mode);
            for (int i = 0; i < 2; i++) ss[i].View.Visible = (i == mode);
            ApplyMode();
            DoLayout();
            RenderAll();
            flush.Interval = 60; flush.Tick += delegate { foreach (Sess x in ss) if (x.Busy) FlushNow(x); };
            fade.Interval = 12; fade.Tick += delegate
            {
                double o = Opacity + 0.2; if (o >= 1.0) { o = 1.0; fade.Stop(); UpdateCorners(); }
                Opacity = o;
            };
            ResizeEnd += delegate { cfg.PanelW = (int)(Width / Dpi.F); cfg.PanelH = (int)(Height / Dpi.F); cfg.Save(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // 阴影
                cp.ExStyle |= 0x80;         // TOOLWINDOW：不出现在 Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            useRegion = !Native.TryRoundCorners(Handle);   // Win11 由系统画圆角，Win10 用 Region 兜底
            ApplyRegion();
        }

        void ApplyRegion()
        {
            if (!useRegion || !IsHandleCreated) return;
            // 区域比绘制圆角切得更深（半径 +4），锯齿边落到四角覆盖层的抗锯齿带里
            int rr = Rad + Dpi.S(4);
            IntPtr rgn = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, rr * 2, rr * 2);
            Native.SetWindowRgn(Handle, rgn, true);
        }

        // 启动后在空闲时调用：建好窗口 / 子控件句柄并跑一遍首帧，第一次打开不再有构建卡顿
        public void Prewarm()
        {
            if (warming || IsDisposed || Visible) return;
            try
            {
                warming = true;
                Opacity = 0;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Show();
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed || !warming) return;   // 用户已在预热完成前打开：不要把它藏回去
                    Hide();
                    warming = false;
                    Opacity = 1;
                });
            }
            catch
            {
                warming = false;
                try { Hide(); } catch { }
                Opacity = 1;
            }
        }

        protected override bool ShowWithoutActivation { get { return warming; } }

        // Win10 的窗口区域是二值位图，圆角必然锯齿。四角各放一个逐像素透明的小覆盖窗，
        // 沿同一个圆角路径画一圈抗锯齿的描边，把区域锯齿盖住，外缘就能和桌面平滑过渡。
        void UpdateCorners()
        {
            if (!useRegion || warming || !Visible || IsDisposed) { HideCorners(); return; }
            int sz = Rad * 2 + Dpi.S(12);
            Rectangle b = Bounds;
            int[] xs = { b.Left, b.Right - sz, b.Left, b.Right - sz };
            int[] ys = { b.Top, b.Top, b.Bottom - sz, b.Bottom - sz };
            if (corners == null)
            {
                corners = new CornerLayer[4];
                for (int i = 0; i < 4; i++) { corners[i] = new CornerLayer { Owner = this, TopMost = true }; }
            }
            Color line = cfg.Pinned ? Color.FromArgb(150, Theme.Accent) : Theme.Border;
            float lw = cfg.Pinned ? 1.6f : 1f;
            for (int i = 0; i < 4; i++)
            {
                corners[i].SetBounds(xs[i], ys[i], sz, sz);
                corners[i].Render(sz, b.Size, Rad, line, lw, xs[i] - b.Left, ys[i] - b.Top);
                if (!corners[i].Visible) corners[i].Show();
            }
        }

        void HideCorners()
        {
            if (corners == null) return;
            foreach (CornerLayer c in corners) if (c.Visible) c.Hide();
        }

        // ───────── 构建界面 ─────────
        RBtn IconBtn(string glyph, string tip)
        {
            RBtn b = new RBtn(); b.Text = glyph; b.Font = fIcon; b.Radius = Dpi.S(9);
            b.Style(Color.Transparent, Theme.Hover, Theme.Sub, Theme.Fg);
            new ToolTip().SetToolTip(b, tip);
            return b;
        }
        RBtn TextBtn(string text)
        {
            RBtn b = new RBtn(); b.Text = text; b.Font = fSmall; b.Radius = Dpi.S(8);
            b.Style(Color.Transparent, Theme.Hover, Theme.Sub, Theme.Fg);
            return b;
        }

        void Build()
        {
            logo = new LogoBox(); Controls.Add(logo);
            title = new Label(); title.Text = "AI 助手"; title.Font = fBold; title.ForeColor = Theme.Fg; title.AutoSize = true; title.BackColor = Color.Transparent;
            sub = new Label(); sub.Font = fSmall; sub.ForeColor = Theme.Sub; sub.AutoSize = true; sub.BackColor = Color.Transparent;
            RefreshModelLabel();
            Controls.Add(title); Controls.Add(sub);
            logo.MouseDown += DragMove; title.MouseDown += DragMove; sub.MouseDown += DragMove;

            pin = new PinButton(); pin.SetOn(cfg.Pinned, false);
            new ToolTip().SetToolTip(pin, "固定窗口（失焦时不自动隐藏）");
            pin.Click += delegate { cfg.Pinned = !cfg.Pinned; pin.SetOn(cfg.Pinned, true); cfg.Save(); Invalidate(); UpdateCorners(); };
            bSet = IconBtn("\uE713", "设置");
            bClose = IconBtn("\uE711", "隐藏（Esc）");
            bSet.Click += delegate { app.OpenSettings(); };
            bClose.Click += delegate { app.HidePanel(); };
            Controls.Add(pin); Controls.Add(bSet); Controls.Add(bClose);

            for (int i = 0; i < 2; i++) { ss[i].View = new ChatView(); Controls.Add(ss[i].View); }

            seg = new Segmented(); seg.Font = Font; seg.Items = new string[] { "问答", "翻译" };
            seg.Changed += delegate { if (!switching) SwitchTo(seg.Selected); };
            Controls.Add(seg);

            inputBox = new RoundBox(); inputBox.Fill = Theme.Input; inputBox.Radius = Dpi.S(16);
            input = new PlaceholderBox();
            input.Multiline = true; input.AcceptsReturn = true; input.BorderStyle = BorderStyle.None;
            input.BackColor = Theme.Input; input.ForeColor = Theme.Fg; input.Font = fBody; input.ScrollBars = ScrollBars.None;
            input.PhColor = Theme.Sub;
            input.GotFocus += delegate { inputBox.Focus2 = true; inputBox.Invalidate(); };
            input.LostFocus += delegate { inputBox.Focus2 = false; inputBox.Invalidate(); };
            input.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control) { e.SuppressKeyPress = true; e.Handled = true; Send(); }
            };
            inputBox.MouseDown += delegate { input.Focus(); };
            inputBox.Controls.Add(input);
            bSend = new SendButton();
            bSend.Click += delegate { Send(); };
            input.TextChanged += delegate { bSend.SetReady(input.TextLength > 0); };
            inputBox.Controls.Add(bSend);
            Controls.Add(inputBox);

            bPaste = TextBtn("粘贴"); bRetry = TextBtn("重试"); bNew = TextBtn("新对话");   // 文字随模式变化，见 ApplyMode
            bPaste.Click += delegate { PasteClipboard(); };
            bRetry.Click += delegate { Retry(); };
            bNew.Click += delegate { ClearSession(Cur); input.Focus(); };
            Controls.Add(bPaste); Controls.Add(bRetry); Controls.Add(bNew);
            keyHint = new Label(); keyHint.Font = fSmall; keyHint.ForeColor = Theme.Sub; keyHint.AutoSize = false;
            keyHint.TextAlign = ContentAlignment.MiddleRight; keyHint.BackColor = Color.Transparent;
            keyHint.Text = "Enter 发送 · Shift+Enter 换行";
            Controls.Add(keyHint);
        }

        void DoLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height, m = Dpi.S(16);
            logo.SetBounds(m - Dpi.S(4), Dpi.S(11), Dpi.S(34), Dpi.S(34));
            title.Location = new Point(m + Dpi.S(34), Dpi.S(11));
            sub.Location = new Point(m + Dpi.S(35), title.Bottom - Dpi.S(1));
            int bs = Dpi.S(30), by = Dpi.S(14);
            bClose.SetBounds(W - m - bs + Dpi.S(4), by, bs, bs);
            bSet.SetBounds(bClose.Left - bs - Dpi.S(2), by, bs, bs);
            pin.SetBounds(bSet.Left - bs - Dpi.S(2), by, bs, bs);

            int segH = Dpi.S(32), inH = Dpi.S(88), toolH = Dpi.S(26);
            int yTool = H - Dpi.S(12) - toolH;
            int yIn = yTool - Dpi.S(6) - inH;
            int ySeg = yIn - Dpi.S(8) - segH;
            int vt = Dpi.S(54);
            foreach (Sess x in ss) if (x.View != null) x.View.SetBounds(Dpi.S(6), vt, W - Dpi.S(12), Math.Max(Dpi.S(80), ySeg - Dpi.S(6) - vt));
            seg.SetBounds(m, ySeg, Dpi.S(150), segH);
                        inputBox.SetBounds(m, yIn, W - 2 * m, inH);
            input.SetBounds(Dpi.S(14), Dpi.S(11), inputBox.Width - Dpi.S(28) - Dpi.S(44), inH - Dpi.S(22));
            bSend.SetBounds(inputBox.Width - Dpi.S(46), inputBox.Height - Dpi.S(46), Dpi.S(34), Dpi.S(34));
            int tx = m - Dpi.S(6);
            RBtn[] tb = { bPaste, bRetry, bNew };
            foreach (RBtn b in tb)
            {
                int bw = TextRenderer.MeasureText(b.Text, b.Font).Width + Dpi.S(18);
                b.SetBounds(tx, yTool, bw, toolH); tx += bw + Dpi.S(2);
            }
            keyHint.SetBounds(tx, yTool, W - m - tx, toolH);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (inputBox != null) DoLayout();
            ApplyRegion();
            UpdateCorners();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            UpdateCorners();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) HideCorners();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Gfx.RR(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Rad))
            using (Pen pen = new Pen(cfg.Pinned ? Color.FromArgb(150, Theme.Accent) : Theme.Border, cfg.Pinned ? 1.6f : 1f))
                g.DrawPath(pen, p);
        }

        // 无边框窗口：四边/四角可拖动调整大小
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    long lp = m.LParam.ToInt64();
                    Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    int g = Dpi.S(6);
                    bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                    if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14;
                    else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
                    else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11;
                    else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
                }
                return;
            }
            base.WndProc(ref m);
        }

        void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Y < Dpi.S(52)) DragMove(this, e); }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (warming || cfg.Pinned || !Visible) return;
            if ((DateTime.Now - shownAt).TotalMilliseconds < 400) return;
            app.HidePanelAuto();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                if (Cur.Busy) Stop(Cur);
                else if (Cur.View.HasSelection) Cur.View.ClearSelection();
                else app.HidePanel();
                return;
            }
            if (e.Control && (e.KeyCode == Keys.D1 || e.KeyCode == Keys.D2))
            {
                e.Handled = true; e.SuppressKeyPress = true;
                SwitchTo(e.KeyCode == Keys.D1 ? 0 : 1);
                return;
            }
            // 输入框里没有选中文字、而对话列表里有选中内容时，Ctrl+C 复制列表里的选中文字
            if (e.Control && e.KeyCode == Keys.C && input.SelectionLength == 0 && Cur.View.HasSelection)
            {
                Cur.View.CopySelection(); e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { flush.Stop(); flush.Dispose(); fade.Stop(); fade.Dispose(); } catch { }
                foreach (Sess x in ss) if (x.Client != null) x.Client.Cancel();
                if (corners != null) foreach (CornerLayer c in corners) try { c.Dispose(); } catch { }
                fBody.Dispose(); fBold.Dispose(); fSmall.Dispose(); fIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        // ───────── 对外接口 ─────────
        public void ShowNear(Rectangle a, bool hasBall, int startMode)
        {
            warming = false;
            SwitchTo(startMode);
            Rectangle wa = Screen.FromRectangle(a).WorkingArea;
            int gap = Dpi.S(12), x, y;
            if (hasBall)
            {
                bool right = (a.Left + a.Width / 2) > (wa.Left + wa.Width / 2);
                x = right ? a.Left - gap - Width : a.Right + gap;
                y = (a.Top + a.Height / 2) > (wa.Top + wa.Height / 2) ? a.Bottom - Height : a.Top;
            }
            else { x = a.Left - Width / 2; y = a.Top; }
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4));
            y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4));
            Location = new Point(x, y);
            shownAt = DateTime.Now;
            Opacity = 0.0; Show(); fade.Start();
            FocusInput();
        }

        public void FocusInput()
        {
            Activate(); Native.SetForegroundWindow(Handle);
            input.Focus(); input.SelectionStart = input.TextLength;
        }

        public void SetInput(string text)
        {
            input.Text = text; input.SelectionStart = input.TextLength; input.Focus();
        }

        // 取词 / 外部调用：
        //  - autoSend（翻译）：切到翻译页并立即翻译；翻译是无状态的，正在翻译上一段就直接顶替，不会变成“停止”
        //  - 否则（提问）：切到问答页，把文字追加到草稿里，等用户补一句问题再发送
        public void Prefill(string text, int newMode, bool autoSend)
        {
            SwitchTo(newMode);
            Sess s = ss[newMode];
            if (autoSend)
            {
                string raw = (text ?? "").Trim();
                if (raw.Length == 0) return;
                if (s.Busy) Abort(s);
                SendText(s, raw);
                return;
            }
            input.Text = input.TextLength > 0 ? input.Text + "\n" + text : text;
            input.SelectionStart = input.TextLength; input.Focus();
        }

        public void RefreshModelLabel() { sub.Text = cfg.Model.Length > 0 ? cfg.Model : "未设置模型"; }

        // ───────── 模式（每个模式一个独立会话）─────────
        void SwitchTo(int m)
        {
            if (m != mode)
            {
                ss[mode].Draft = input.Text;
                ss[mode].View.Visible = false;
                mode = m; cfg.Mode = m;
                switching = true; seg.Selected = m; switching = false;
                ss[m].View.Visible = true;
                input.Text = ss[m].Draft; input.SelectionStart = input.TextLength;
                ApplyMode(); SetSendIcon(); DoLayout();
            }
            input.Focus();
        }

        void ApplyMode()
        {
            input.Placeholder = mode == 0 ? "问点什么…" : "输入或粘贴要翻译的文字（中 ⇄ 英 自动识别）";
            bNew.Text = mode == 0 ? "新对话" : "清空翻译";
            input.Invalidate();
        }

        // ───────── 剪贴板 ─────────
        void PasteClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    input.Text = input.TextLength > 0 ? input.Text + "\n" + t : t;
                    input.SelectionStart = input.TextLength;
                }
            }
            catch { }
            input.Focus();
        }

        // ───────── 发送 / 停止 ─────────
        void SetSendIcon() { bSend.SetBusy(Cur.Busy); }

        void UpdateBusyUi()
        {
            bool any = ss[0].Busy || ss[1].Busy;
            SetSendIcon(); logo.SetThinking(any); app.SetThinking(any);
            if (any) { if (!flush.Enabled) flush.Start(); } else flush.Stop();
        }

        void Send()
        {
            Sess s = Cur;
            if (s.Busy) { Stop(s); return; }
            string raw = input.Text.Trim();
            if (raw.Length == 0) return;
            if (!SendText(s, raw)) return;
            input.Clear(); s.Draft = "";
        }

        bool SendText(Sess s, string raw)
        {
            if (cfg.BaseUrl.Trim().Length == 0 || cfg.Model.Trim().Length == 0)
            {
                s.View.AddNote("请先在设置里填写 API 地址、Key 和模型名称。", true);
                return false;
            }
            Msg um = new Msg("user", raw, s.Mode);
            s.Hist.Add(um);
            s.View.AddUser(raw, s.Mode == 1 ? "翻译 " + Prompts.Direction(raw) : null, um.Time);
            StartRequest(s);
            return true;
        }

        void Stop(Sess s) { if (s.Client != null) s.Client.Cancel(); }

        // 立即终止并收尾（保留已生成的部分，标记“已停止生成”）
        void Abort(Sess s)
        {
            ChatClient c = s.Client; if (c == null) return;
            c.Cancel();
            Finished(s, c, null);
        }

        List<Dictionary<string, object>> BuildMessages(Sess s)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            if (s.Mode == 1)
            {
                l.Add(ChatClient.M("system", Prompts.Translate));
                l.Add(ChatClient.M("user", Prompts.Wrap(s.Hist[s.Hist.Count - 1].Content)));
                return l;
            }
            if (!string.IsNullOrEmpty(cfg.SystemPrompt)) l.Add(ChatClient.M("system", cfg.SystemPrompt));
            List<Msg> ctx = s.Hist;
            int start = Math.Max(0, ctx.Count - cfg.MaxContext);
            while (start < ctx.Count && ctx[start].Role != "user") start++;
            for (int i = start; i < ctx.Count; i++) l.Add(ChatClient.M(ctx[i].Role, ctx[i].Content));
            return l;
        }

        void StartRequest(Sess s)
        {
            s.Busy = true;
            s.View.BeginAssistant(s.Mode == 1 ? "译文" : null);
            s.Answer.Length = 0;
            lock (s.Lock) s.Buf.Length = 0;
            UpdateBusyUi();

            List<Dictionary<string, object>> msgs = BuildMessages(s);
            string url = cfg.BaseUrl, key = cfg.GetKey(), model = cfg.Model; bool st = cfg.Stream;
            ChatClient cl = new ChatClient(); s.Client = cl;
            Thread th = new Thread(delegate ()
            {
                string err = cl.Run(url, key, model, st, msgs, delegate (string t) { lock (s.Lock) s.Buf.Append(t); });
                try { BeginInvoke((Action)delegate { Finished(s, cl, err); }); } catch { }
            });
            th.IsBackground = true; th.Start();
        }

        void FlushNow(Sess s)
        {
            string t;
            lock (s.Lock) { if (s.Buf.Length == 0) return; t = s.Buf.ToString(); s.Buf.Length = 0; }
            s.Answer.Append(t.Replace("\r\n", "\n"));
            s.View.SetLastText(s.Answer.ToString());
        }

        void Finished(Sess s, ChatClient cl, string err)
        {
            if (cl != s.Client) return;
            FlushNow(s);
            s.Busy = false; s.Client = null;
            string text = s.Answer.ToString();
            bool cancelled = cl.Cancelled;
            if (text.Length > 0)
            {
                Msg am = new Msg("assistant", text, s.Mode); s.Hist.Add(am);
                s.View.EndStream(am.Time);
            }
            else s.View.RemoveLastEmpty();
            if (err != null)
            {
                bool net = cl.Status == ReqStatus.Timeout || cl.Status == ReqStatus.NetworkError;
                s.View.AddNote("⚠ " + err + (net ? "\n网络似乎有问题，恢复后可以点“重试”。" : ""), true);
            }
            else if (cl.Warning != null) s.View.AddNote(cl.Warning, false);
            else if (cancelled) s.View.AddNote("已停止生成", false);
            else if (text.Length == 0) s.View.AddNote("（模型返回了空内容）", false);
            app.SaveSession();
            UpdateBusyUi();
            if (!Visible) app.SetUnread(true);
        }

        void Retry()
        {
            Sess s = Cur;
            if (s.Busy || s.Hist.Count == 0) return;
            if (s.Hist[s.Hist.Count - 1].Role == "assistant") s.Hist.RemoveAt(s.Hist.Count - 1);
            if (s.Hist.Count > 0 && s.Hist[s.Hist.Count - 1].Role == "user")
            {
                s.View.SetMessages(s.Hist);
                StartRequest(s);
            }
        }

        // 清空一个会话（问答 = 新对话；翻译 = 清空翻译记录），不影响另一个会话
        void ClearSession(Sess s)
        {
            ChatClient c = s.Client; s.Client = null;
            if (c != null) c.Cancel();
            s.Busy = false;
            s.Hist.Clear(); s.View.Clear();
            app.SaveSession(); UpdateBusyUi();
        }

        public void NewChat() { ClearSession(ss[0]); SwitchTo(0); }
        public void ClearAll() { ClearSession(ss[0]); ClearSession(ss[1]); }

        void RenderAll()
        {
            ss[0].View.WelcomeTitle = "有什么可以帮你？";
            ss[0].View.Welcome = new string[] {
                "选中文字后点出现的「提问」，或按 " + cfg.HotkeyGrab,
                cfg.HotkeyToggle + " 随时呼出 / 隐藏，也可以把文字拖到悬浮球上",
                "Ctrl+1 / 2 切换 问答 · 翻译；对话内容可以直接拖选复制" };
            ss[1].View.WelcomeTitle = "想翻译什么？";
            ss[1].View.Welcome = new string[] {
                "选中文字后点出现的「翻译」，或按 " + cfg.HotkeyTranslate,
                "也可以粘贴到下面的输入框（中 ⇄ 英 自动识别）",
                "每次翻译相互独立，不带上下文" };
            foreach (Sess x in ss) { if (x.Hist.Count == 0) x.View.Clear(); else x.View.SetMessages(x.Hist); }
        }
    }

    // 窗口四角的抗锯齿覆盖层：逐像素透明的小窗口，只画圆角边缘那一圈（含描边），
    // 把 SetWindowRgn 的二值锯齿边藏起来；单击穿透，不影响按钮与拖拽调整大小。
    class CornerLayer : Form
    {
        Bitmap bmp;
        int cachedSz = -1, cachedW = -1, cachedH = -1, cachedLine, cachedRad = -1;
        float cachedLw = -1f;

        public CornerLayer()
        {
            Dpi.Freeze(this);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x20 | 0x80000 | 0x08000000; // TOOLWINDOW | TRANSPARENT | LAYERED | NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void Render(int sz, Size panel, int rad, Color line, float lw, int dx, int dy)
        {
            int argb = line.ToArgb();
            if (sz == cachedSz && panel.Width == cachedW && panel.Height == cachedH &&
                rad == cachedRad && argb == cachedLine && Math.Abs(lw - cachedLw) < 0.01f) return;
            cachedSz = sz; cachedW = panel.Width; cachedH = panel.Height;
            cachedRad = rad; cachedLine = argb; cachedLw = lw;

            if (bmp != null && bmp.Width != sz) { bmp.Dispose(); bmp = null; }
            if (bmp == null) bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TranslateTransform(-dx, -dy);
                RectangleF outer = new RectangleF(0.5f, 0.5f, panel.Width - 1f, panel.Height - 1f);
                float band = Math.Max(3f, Dpi.S(3));
                RectangleF inner = new RectangleF(outer.X + band, outer.Y + band, outer.Width - 2 * band, outer.Height - 2 * band);
                using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
                {
                    using (GraphicsPath po = Gfx.RR(outer, rad))
                    using (GraphicsPath pi = Gfx.RR(inner, Math.Max(1f, rad - band)))
                    {
                        ring.AddPath(po, false); ring.AddPath(pi, false);
                    }
                    using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillPath(b, ring);
                }
                using (GraphicsPath p = Gfx.RR(outer, rad))
                using (Pen pen = new Pen(line, lw)) g.DrawPath(pen, p);
            }
            Native.SetLayeredBitmap(Handle, bmp, 255);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && bmp != null) { bmp.Dispose(); bmp = null; }
            base.Dispose(disposing);
        }
    }
}
