#nullable disable
// 划词工具栏：在文字上拖选 / 双击之后，鼠标旁弹出「翻译 · 提问 · 复制」小条，点一下即可，不用再按快捷键。
// 隐私与安全：
//  - 弹出条时【不读取】选中的文字；只有你点按钮的那一刻才读取（并且会还原剪贴板）。
//  - 只在鼠标指针是“文字输入光标（I 形）”的地方触发；终端 / 命令行 / 桌面等窗口一律忽略（避免误发 Ctrl+C 打断程序）。
//  - 可在设置里整体关闭。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace FloatBall
{
    class SelBar : IDisposable
    {
        const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A;
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] struct MSLL { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        static readonly string[] DenyClass = { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "VirtualConsoleClass", "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        static readonly string[] DenyProc = { "windowsterminal", "wt", "cmd", "powershell", "pwsh", "conhost", "openconsole", "wezterm-gui", "alacritty", "mintty", "putty", "kitty", "mstsc" };

        readonly Control ui; readonly Action<int> picked;
        readonly HookProc proc; IntPtr hook = IntPtr.Zero;
        SelPop pop; readonly WinTimer hideT = new WinTimer();
        IntPtr ibeam;
        Point downPt, lastDownPt; int downTick, lastDownTick; bool downIbeam, downDbl;
        readonly uint myPid;
        bool disposed;

        public SelBar(Control ui, Action<int> picked)
        {
            this.ui = ui; this.picked = picked;
            myPid = (uint)Process.GetCurrentProcess().Id;
            ibeam = LoadCursor(IntPtr.Zero, (IntPtr)32513);
            proc = HookCb;
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            hideT.Interval = 6000; hideT.Tick += delegate { HidePop(); };
        }

        public bool Active { get { return hook != IntPtr.Zero; } }

        bool IsIBeam()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                return GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && ci.hCursor == ibeam;
            }
            catch { return false; }
        }

        IntPtr HookCb(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && !disposed)
                {
                    int msg = (int)wParam;
                    if (msg == WM_LBUTTONDOWN)
                    {
                        MSLL ms = (MSLL)Marshal.PtrToStructure(lParam, typeof(MSLL));
                        Point pt = new Point(ms.X, ms.Y);
                        if (pop == null || !pop.Visible || !pop.Bounds.Contains(pt))
                        {
                            if (pop != null && pop.Visible) ui.BeginInvoke((Action)HidePop);
                            int now = Environment.TickCount;
                            downDbl = (now - lastDownTick) <= SystemInformation.DoubleClickTime
                                      && Math.Abs(pt.X - lastDownPt.X) <= SystemInformation.DoubleClickSize.Width
                                      && Math.Abs(pt.Y - lastDownPt.Y) <= SystemInformation.DoubleClickSize.Height;
                            downPt = pt; downTick = now; lastDownPt = pt; lastDownTick = now;
                            downIbeam = IsIBeam();
                        }
                        else downIbeam = false;
                    }
                    else if (msg == WM_LBUTTONUP)
                    {
                        MSLL ms = (MSLL)Marshal.PtrToStructure(lParam, typeof(MSLL));
                        Point pt = new Point(ms.X, ms.Y);
                        int dist = Math.Max(Math.Abs(pt.X - downPt.X), Math.Abs(pt.Y - downPt.Y));
                        int held = Environment.TickCount - downTick;
                        bool g = downIbeam && ((dist >= 12 && held >= 100) || (downDbl && dist < 6));
                        downIbeam = false;
                        if (g) ui.BeginInvoke((Action)delegate { OnGesture(pt); });
                    }
                    else if (msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_MOUSEWHEEL)
                    {
                        if (pop != null && pop.Visible) ui.BeginInvoke((Action)HidePop);
                    }
                }
            }
            catch { }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        bool Allowed()
        {
            try
            {
                IntPtr fg = GetForegroundWindow(); if (fg == IntPtr.Zero) return false;
                uint pid; GetWindowThreadProcessId(fg, out pid);
                if (pid == myPid) return false;
                StringBuilder sb = new StringBuilder(128); GetClassName(fg, sb, 128);
                string cls = sb.ToString();
                foreach (string c in DenyClass) if (string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)) return false;
                string name = "";
                try { using (Process p = Process.GetProcessById((int)pid)) name = p.ProcessName.ToLowerInvariant(); } catch { }
                foreach (string n in DenyProc) if (name == n) return false;
                return true;
            }
            catch { return false; }
        }

        void OnGesture(Point pt)
        {
            if (disposed || !Allowed()) return;
            if (pop == null || pop.IsDisposed)
            {
                pop = new SelPop();
                pop.Picked += delegate (int i) { HidePop(); picked(i); };
            }
            pop.ShowAt(pt);
            hideT.Stop(); hideT.Start();
        }

        public void HidePop()
        {
            hideT.Stop();
            if (pop != null && !pop.IsDisposed && pop.Visible) pop.Hide();
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); } catch { }
            hook = IntPtr.Zero;
            try { hideT.Stop(); hideT.Dispose(); } catch { }
            try { if (pop != null) pop.Dispose(); } catch { }
        }
    }

    // 不抢焦点的小条：点击时目标程序仍保持焦点和选区
    class SelPop : Form
    {
        static readonly string[] Labels = { "翻译", "提问", "复制" };
        public event Action<int> Picked;
        int hot = -1; bool useRegion; readonly Font f;
        int Bw { get { return Dpi.S(54); } }
        int Pad { get { return Dpi.S(4); } }

        public SelPop()
        {
            Dpi.Freeze(this);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
            f = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(Bw * Labels.Length + 2 * Pad, Dpi.S(34));
            BackColor = Theme.Bg;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;                  // 阴影
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8;    // NOACTIVATE | TOOLWINDOW | TOPMOST
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE → MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            useRegion = !Native.TryRoundCorners(Handle);
            ApplyRegion();
        }

        void ApplyRegion()
        {
            if (!useRegion || !IsHandleCreated) return;
            int r = Dpi.S(8) * 2;
            Native.SetWindowRgn(Handle, Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r), true);
        }

        public void ShowAt(Point pt)
        {
            Theme.Apply(Theme.Dark); BackColor = Theme.Bg;
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            int x = pt.X - Width / 2, y = pt.Y - Height - Dpi.S(18);
            if (y < wa.Top + 2) y = pt.Y + Dpi.S(22);
            x = Math.Max(wa.Left + 2, Math.Min(x, wa.Right - Width - 2));
            Location = new Point(x, y);
            hot = -1;
            if (!Visible) Show(); else Invalidate();
            ApplyRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            for (int i = 0; i < Labels.Length; i++)
            {
                Rectangle r = new Rectangle(Pad + i * Bw, Pad, Bw, Height - 2 * Pad);
                if (i == hot)
                    using (GraphicsPath gp = Gfx.RR(r, Dpi.S(6)))
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillPath(b, gp);
                TextRenderer.DrawText(g, Labels[i], f, r, i == hot ? Theme.OnAccent : Theme.Fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        int IndexAt(Point p)
        {
            if (p.Y < Pad || p.Y > Height - Pad) return -1;
            int i = (p.X - Pad) / Bw;
            return (p.X >= Pad && i >= 0 && i < Labels.Length) ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i != hot) { hot = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int i = IndexAt(e.Location);
            if (i >= 0 && Picked != null) Picked(i);
        }

        protected override void Dispose(bool disposing) { if (disposing) f.Dispose(); base.Dispose(disposing); }
    }
}
