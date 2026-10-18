#nullable disable
// 悬浮球模块入口（v3.0：作为 LST 进程内的一个模块运行，不再有自己的 Main / 托盘 / 自启动）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FloatBall
{
    // 宿主（LST 的 TrayApp）提供给悬浮球的能力
    interface IBallHost
    {
        void Capture(string kind);                                // "copy" | "ocr" | "translate" | "pin"
        void ShowSettings();                                      // 统一设置面板（API、截图、悬浮球都在里面）
        void Notify(string title, string text, bool warn);
        void ExitAll();
        ContextMenuStrip TrayMenu { get; }                        // 托盘右键菜单：悬浮球右键直接弹同一个菜单
    }

    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed((int)m.WParam);
            base.WndProc(ref m);
        }
        public bool Register(int id, string hk)
        {
            Native.UnregisterHotKey(Handle, id);
            uint mod, vk;
            if (!Cfg.ParseHotkey(hk, out mod, out vk)) return false;
            return Native.RegisterHotKey(Handle, id, mod | 0x4000, vk); // MOD_NOREPEAT
        }
        public void Dispose()
        {
            for (int i = 1; i <= 3; i++) Native.UnregisterHotKey(Handle, i);
            DestroyHandle();
        }
    }

    class AppCtx
    {
        readonly IBallHost host;
        readonly Cfg cfg;
        readonly List<Msg> chatHist = new List<Msg>();    // 问答会话
        readonly List<Msg> transHist = new List<Msg>();   // 翻译会话（两者互相独立）
        SelBar selBar;
        readonly BallForm ball;
        ChatPanel panel;
        readonly HotkeyWindow hk;
        readonly Control ui;
        readonly System.Windows.Forms.Timer warm = new System.Windows.Forms.Timer();
        ToolStripMenuItem miTrayBall;
        Form anchor;
        DateTime lastAutoHide = DateTime.MinValue;
        bool hiddenForCapture;

        public AppCtx(IBallHost host)
        {
            this.host = host;
            cfg = Cfg.Load();
            Theme.Apply(cfg.Dark);
            LoadSession();
            ui = new Control(); IntPtr h = ui.Handle;

            ball = new BallForm(this, cfg);

            SetupSelBar();
            hk = new HotkeyWindow();
            hk.Pressed += delegate (int id)
            {
                if (id == 1) TogglePanel();
                else if (id == 2) Grab(0, false);
                else if (id == 3) Grab(1, true);
            };
            RegisterHotkeys();
            if (cfg.BallVisible) ball.Show();

            // 启动后空闲时预热对话面板：第一次打开不必再承担控件创建 / 布局 / 首帧绘制
            warm.Interval = 600;
            warm.Tick += delegate
            {
                warm.Stop(); warm.Dispose();
                long t = LST.Perf.Now();
                try { EnsurePanel(); panel.Prewarm(); } catch { /* 预热失败不影响正常打开 */ }
                if (LST.Perf.Enabled) LST.Perf.Log($"chat panel prewarmed: {LST.Perf.Ms(t):F0} ms after startup");
            };
            warm.Start();
        }

        public bool PanelVisible { get { return panel != null && !panel.IsDisposed && panel.Visible; } }
        public bool BallVisible { get { return ball.Visible; } }

        public Cfg Cfg { get { return cfg; } }

        // ───────── 菜单：悬浮球右键 = 托盘右键（同一个菜单）─────────
        // 把悬浮球相关项加到 LST 的托盘菜单里（单托盘）
        public void FillTrayMenu(ContextMenuStrip m, int index)
        {
            m.Items.Insert(index++, new ToolStripSeparator());
            m.Items.Insert(index++, new ToolStripMenuItem("AI 对话（打开 / 隐藏）", null, delegate { TogglePanel(true); }));
            m.Items.Insert(index++, new ToolStripMenuItem("翻译剪贴板", null, delegate { TranslateClipboard(); }));
            miTrayBall = new ToolStripMenuItem("显示悬浮球", null, delegate { ToggleBall(); });
            m.Items.Insert(index++, miTrayBall);
            m.Opening += delegate { miTrayBall.Checked = ball.Visible; };
        }

        void RegisterHotkeys()
        {
            string bad = "";
            if (!hk.Register(1, cfg.HotkeyToggle)) bad += "「" + cfg.HotkeyToggle + "」";
            if (!hk.Register(2, cfg.HotkeyGrab)) bad += "「" + cfg.HotkeyGrab + "」";
            if (!hk.Register(3, cfg.HotkeyTranslate)) bad += "「" + cfg.HotkeyTranslate + "」";
            if (bad.Length > 0)
                host.Notify("悬浮球快捷键注册失败", bad + " 可能被其他软件占用，请在设置里更换。", true);
        }

        // ───────── 面板控制 ─────────
        void EnsurePanel()
        {
            if (panel == null || panel.IsDisposed) panel = new ChatPanel(this, cfg, chatHist, transHist);
        }

        // 通用入口（悬浮球 / 热键 / 托盘）默认打开问答页；翻译流程通过 ShowPanelCore(1) 或 Prefill 进入翻译页
        public void ShowPanel() { ShowPanelCore(0, null); }

        void ShowPanelCore(int mode, Point? at)
        {
            long t = LST.Perf.Now();
            EnsurePanel();
            if (panel.Visible) panel.FocusInput();
            else if (at.HasValue) panel.ShowNear(new Rectangle(at.Value.X, at.Value.Y + Dpi.S(14), 1, 1), false, mode);
            else
            {
                bool hasBall = ball.Visible;
                Rectangle anchor;
                if (hasBall) anchor = ball.CircleRest();
                else
                {
                    Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                    anchor = new Rectangle(wa.Left + wa.Width / 2, wa.Top + wa.Height / 4, 1, 1);
                }
                panel.ShowNear(anchor, hasBall, mode);
            }
            ball.SetUnread(false); ball.Redraw();
            if (LST.Perf.Enabled) LST.Perf.Log($"chat panel shown: {LST.Perf.Ms(t):F0} ms");
        }

        // 取词 / 划词时：面板直接出现在鼠标旁边（而不是远处的悬浮球旁）
        public void ShowPanelAt(Point pt, int mode) { ShowPanelCore(mode, pt); }

        public bool AutoHiddenSince(DateTime t) { return lastAutoHide >= t.AddMilliseconds(-120); }

        public void HidePanelAuto() { if (PanelVisible) lastAutoHide = DateTime.Now; HidePanel(); }

        public void HidePanel()
        {
            if (panel != null && !panel.IsDisposed && panel.Visible) panel.Hide();
            ball.Redraw();
        }

        // fromClick=true（点悬浮球/托盘）：面板已显示则直接收起；热键：在前台则收起，否则先激活
        public void TogglePanel() { TogglePanel(false); }
        public void TogglePanel(bool fromClick)
        {
            if (PanelVisible)
            {
                if (fromClick || Form.ActiveForm == panel) HidePanel(); else panel.FocusInput();
            }
            else ShowPanel();
        }

        public void NewChat() { EnsurePanel(); panel.NewChat(); ShowPanel(); }

        // 悬浮球是不抢焦点的窗口：先激活一个隐形锚点窗口，菜单才能在点击别处/再点悬浮球时自动收起
        public void ShowBallMenu()
        {
            if (anchor == null || anchor.IsDisposed)
            {
                anchor = new Form();
                anchor.FormBorderStyle = FormBorderStyle.None; anchor.ShowInTaskbar = false; anchor.StartPosition = FormStartPosition.Manual;
                anchor.Size = new Size(1, 1); anchor.Opacity = 0.01; anchor.TopMost = true;
                host.TrayMenu.Closed += delegate { if (anchor != null && !anchor.IsDisposed) anchor.Hide(); };
            }
            Point p = Cursor.Position;
            anchor.Location = p; anchor.Show(); anchor.Activate(); Native.SetForegroundWindow(anchor.Handle);
            host.TrayMenu.Show(p);
        }

        public void AskWithText(string text)
        {
            ShowPanel(); panel.Prefill(text, 0, false);
        }

        public void AskClipboard()
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (!string.IsNullOrEmpty(t)) AskWithText(t); else ShowPanel();
        }

        // 直接翻译一段文字（供宿主 / 菜单调用）：切到翻译模式并自动发送
        public void TranslateText(string text)
        {
            if (string.IsNullOrEmpty(text)) { ShowPanel(); return; }
            ShowPanelCore(1, null); panel.Prefill(text, 1, true);
        }

        public void TranslateClipboard()
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (!string.IsNullOrEmpty(t)) TranslateText(t);
            else host.Notify("AI 悬浮球", "剪贴板里没有文字。", false);
        }

        public void SetThinking(bool v) { ball.SetThinking(v); }
        public void SetUnread(bool v) { ball.SetUnread(v); }

        void ToggleBall()
        {
            cfg.BallVisible = !ball.Visible;
            if (cfg.BallVisible) ball.Show(); else ball.Hide();
            cfg.Save();
        }

        // ───────── 从悬浮球发起截图：先让面板和悬浮球退场，避免被截进去 ─────────
        void CaptureFromBall(string kind)
        {
            HidePanel();
            if (ball.Visible) { hiddenForCapture = true; ball.Hide(); }
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 180;
            t.Tick += delegate { t.Stop(); t.Dispose(); host.Capture(kind); };
            t.Start();
        }

        // 宿主在截图结束（遮罩关闭 / 失败）时调用
        public void CaptureEnded()
        {
            if (!hiddenForCapture) return;
            hiddenForCapture = false;
            if (cfg.BallVisible) ball.Show();
        }

        // ───────── 取词：只在用户按下快捷键时读取选中文字 ─────────
        // 不清空剪贴板：先备份所有格式 → 模拟 Ctrl+C → 仅当剪贴板序号变化才读取并还原，原来的图片/文件/富文本不会丢
        static DataObject CloneClipboard(out bool hadData)
        {
            hadData = false;
            IDataObject src = Clipboard.GetDataObject();
            if (src == null) return null;
            string[] fmts = src.GetFormats(false);
            hadData = fmts.Length > 0;
            DataObject d = new DataObject(); bool any = false;
            foreach (string f in fmts)
            {
                try { object o = src.GetData(f, false); if (o != null) { d.SetData(f, false, o); any = true; } } catch { }
            }
            return any ? d : null;
        }

        void Grab(int mode, bool autoSend)
        {
            Point at = Cursor.Position;
            bool had = false; DataObject backup = null;
            try { backup = CloneClipboard(out had); } catch { }
            uint seq = Native.GetClipboardSequenceNumber();
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < 40 && Native.AnyModifierDown(); i++) Thread.Sleep(25);
                Native.SendCopy();
                bool changed = false;
                for (int i = 0; i < 24; i++) { Thread.Sleep(25); if (Native.GetClipboardSequenceNumber() != seq) { changed = true; break; } }
                if (changed) Thread.Sleep(40);
                try
                {
                    ui.BeginInvoke((Action)delegate
                    {
                        string t = null;
                        if (changed)
                        {
                            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
                            try
                            {
                                if (backup != null) Clipboard.SetDataObject(backup, true);
                                else if (!had) Clipboard.Clear();
                            }
                            catch { }
                        }
                        if (string.IsNullOrEmpty(t))
                        {
                            host.Notify("AI 悬浮球", "没有读取到选中的文字。", false);
                            return;
                        }
                        ShowPanelAt(at, mode); panel.Prefill(t, mode, autoSend);
                    });
                }
                catch { }
            });
        }

        // ───────── 划词工具栏 ─────────
        void SetupSelBar()
        {
            if (cfg.SelBar && selBar == null)
            {
                try { selBar = new SelBar(ui, OnSelPick); }
                catch { selBar = null; }
            }
            else if (!cfg.SelBar && selBar != null) { selBar.Dispose(); selBar = null; }
        }

        // 划词弹出条：0 翻译、1 提问、2 复制
        void OnSelPick(int i)
        {
            if (i == 0) Grab(1, false);
            else if (i == 1) Grab(0, false);
            else CopySelection();
        }

        // 复制：把选中的文字写入剪贴板，不还原剪贴板（复制本身就是目的）
        void CopySelection()
        {
            uint seq = Native.GetClipboardSequenceNumber();
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < 40 && Native.AnyModifierDown(); i++) Thread.Sleep(25);
                Native.SendCopy();
                bool changed = false;
                for (int i = 0; i < 24; i++) { Thread.Sleep(25); if (Native.GetClipboardSequenceNumber() != seq) { changed = true; break; } }
                if (changed) { Thread.Sleep(40); return; }
                try
                {
                    ui.BeginInvoke((Action)delegate
                    {
                        host.Notify("AI 悬浮球", "没有读取到选中的文字。", true);
                    });
                }
                catch { }
            });
        }

        // ───────── 设置（统一设置面板保存后由宿主调用）─────────
        public void OpenSettings() { host.ShowSettings(); }

        public void ApplySettings()
        {
            bool wasVisible = PanelVisible;
            cfg.Save();
            Theme.Apply(cfg.Dark);
            ball.ApplyConfig(); ball.SnapToEdge();
            if (cfg.BallVisible) { if (!ball.Visible) ball.Show(); } else if (ball.Visible) ball.Hide();
            RegisterHotkeys();
            SetupSelBar();
            if (!cfg.SaveHistory) { try { if (File.Exists(Cfg.SessionPath)) File.Delete(Cfg.SessionPath); } catch { } }
            if (panel != null && !panel.IsDisposed) { panel.Dispose(); panel = null; }
            if (wasVisible) ShowPanel();
        }

        // ───────── 会话持久化（只保存最近 60 条，原子写入）─────────
        void LoadSession()
        {
            try
            {
                if (File.Exists(Cfg.SessionPath))
                {
                    List<Msg> l = new JavaScriptSerializer().Deserialize<List<Msg>>(File.ReadAllText(Cfg.SessionPath, Encoding.UTF8));
                    if (l != null) foreach (Msg m in l) if (m != null && m.Content != null && (m.Role == "user" || m.Role == "assistant")) (m.Mode == 1 ? transHist : chatHist).Add(m);
                }
            }
            catch { }
        }

        public void ClearHistory()
        {
            if (panel != null && !panel.IsDisposed) panel.ClearAll();
            else { chatHist.Clear(); transHist.Clear(); }
            try { if (File.Exists(Cfg.SessionPath)) File.Delete(Cfg.SessionPath); } catch { }
        }

        // 设置面板的“查看 / 导出记录”用：返回副本，避免查看时列表变化
        public int ChatCount { get { return chatHist.Count; } }
        public int TranslateCount { get { return transHist.Count; } }
        public List<Msg> SnapshotChat() { return new List<Msg>(chatHist); }
        public List<Msg> SnapshotTranslate() { return new List<Msg>(transHist); }

        public void SaveSession()
        {
            if (!cfg.SaveHistory) return;
            try
            {
                List<Msg> l = new List<Msg>();
                l.AddRange(chatHist.GetRange(Math.Max(0, chatHist.Count - 60), Math.Min(60, chatHist.Count)));
                l.AddRange(transHist.GetRange(Math.Max(0, transHist.Count - 60), Math.Min(60, transHist.Count)));
                JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
                Cfg.WriteAtomic(Cfg.SessionPath, js.Serialize(l));
            }
            catch { }
        }

        // 宿主退出时调用：保存配置与会话，释放窗口和热键
        public void Shutdown()
        {
            try { cfg.Save(); SaveSession(); } catch { }
            try { warm.Stop(); warm.Dispose(); } catch { }
            try { hk.Dispose(); } catch { }
            try { if (selBar != null) selBar.Dispose(); } catch { }
            if (anchor != null && !anchor.IsDisposed) anchor.Dispose();
            if (panel != null && !panel.IsDisposed) panel.Dispose();
            try { ball.Close(); } catch { }
            try { ui.Dispose(); } catch { }
        }
    }
}
