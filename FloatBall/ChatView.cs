#nullable disable
// 自绘对话列表：用户气泡 / AI 卡片 / 代码块 / 细滚动条 / 平滑滚动
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace FloatBall
{
    class ChatView : Control
    {
        class Run { public string T; public int St; }
        class Seg { public string T; public int X, W, St; }
        class Line { public List<Seg> Segs = new List<Seg>(); public int Y; public bool Code; public int Brk; }   // Brk: 0=自动换行的续行 1=换行 2=前面有空行
        class CodeBlk { public int Y0, Y1; public StringBuilder Text = new StringBuilder(); public int Flash; }
        class Item
        {
            public string Role, Text, Tag, Time;
            public bool Err, Stream;
            public List<Line> Lines = new List<Line>();
            public List<CodeBlk> Blocks = new List<CodeBlk>();
            public int Ix, Top, H, TextH, BubW, MaxLine, Flash, SnapW;
            public Snap Sn;
        }
        class Snap { public int Si, Y, LinesN, BlocksN, CbY0; public bool Code, LastBlank; public string CbText; }
        class Hit { public Rectangle R; public Item It; public CodeBlk Blk; }
        class Pos { public int I, L, C; public Pos(int i, int l, int c) { I = i; L = l; C = c; } }

        readonly List<Item> items = new List<Item>();
        readonly List<Hit> hits = new List<Hit>();
        Font fBody, fBold, fCode, fHead, fSmall, fSmallB;
        int lh, padX, gap, cardPadX, headH, radius, codePad, codePadV, gutter, tagH, bubPadX, bubPadY, timeH, notePad, charW;
        readonly Dictionary<string, int>[] mcache = new Dictionary<string, int>[4];
        readonly int[] spW = new int[4];
        static readonly Regex NumRe = new Regex(@"^\s*(\d{1,2}[\.\)])\s+(.*)$");
        const TextFormatFlags MF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags DF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoClipping;

        int layW = -1, contentH;
        float scroll, target;
        readonly WinTimer scrollAnim = new WinTimer(), flashT = new WinTimer(), dotT = new WinTimer();
        bool dragBar, barHover; int dragOff;
        Hit hoverHit; Item hoverItem;
        public string[] Welcome = new string[0];
        public string WelcomeTitle = "有什么可以帮你？";
        readonly BallVis wv = new BallVis();

        public ChatView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = false;
            fBody = new Font("Microsoft YaHei UI", 10f);
            fBold = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            fHead = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            fCode = new Font("Consolas", 9.5f);
            fSmall = new Font("Microsoft YaHei UI", 8f);
            fSmallB = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            for (int i = 0; i < 4; i++) mcache[i] = new Dictionary<string, int>();
            EnsureMenu();
            lh = (int)(fBody.Height * 1.42f);
            padX = Dpi.S(12); gap = Dpi.S(14); cardPadX = Dpi.S(14); headH = Dpi.S(30); radius = Dpi.S(14);
            codePad = Dpi.S(10); codePadV = Dpi.S(8); gutter = Dpi.S(10); tagH = Dpi.S(18);
            bubPadX = Dpi.S(14); bubPadY = Dpi.S(10); timeH = Dpi.S(16); notePad = Dpi.S(10);
            charW = Math.Max(1, MW("M", 2));
            for (int i = 0; i < 4; i++) spW[i] = Math.Max(2, MW("a a", i) - MW("aa", i));
            wv.Time = 0.4f; wv.Orbit = 2.2f;
            scrollAnim.Interval = 16; scrollAnim.Tick += delegate { StepScroll(); };
            flashT.Interval = 1350; flashT.Tick += delegate { flashT.Stop(); Invalidate(); };
            dotT.Interval = 350; dotT.Tick += delegate { Invalidate(); };
        }

        Font FontOf(int st) { return st == 0 ? fBody : (st == 1 ? fBold : (st == 2 ? fCode : fHead)); }
        int MW(string s, int st)
        {
            int w; Dictionary<string, int> c = mcache[st];
            if (c.TryGetValue(s, out w)) return w;
            w = TextRenderer.MeasureText(s, FontOf(st), new Size(100000, 100000), MF).Width;
            if (c.Count > 6000) c.Clear();   // 限制缓存容量
            c[s] = w; return w;
        }

        // ───────── 对外接口 ─────────
        public void Clear() { anchor = caret = null; items.Clear(); scroll = target = 0; layW = -1; hoverHit = null; hoverItem = null; UpdateDots(); Invalidate(); }

        public void SetMessages(List<Msg> hist)
        {
            anchor = caret = null; items.Clear();
            int start = Math.Max(0, hist.Count - 40);
            for (int i = start; i < hist.Count; i++)
            {
                Msg m = hist[i];
                Item it = new Item(); it.Text = m.Content; it.Time = m.Time;
                if (m.Role == "user") { it.Role = "user"; if (m.Mode == 1) it.Tag = "翻译 " + Prompts.Direction(m.Content); }
                else { it.Role = "assistant"; if (m.Mode == 1) it.Tag = "译文"; }
                items.Add(it);
            }
            layW = -1; EnsureLayout(); ScrollNow(MaxScroll); UpdateDots(); Invalidate();
        }

        public void AddUser(string text, string tag, string time)
        {
            Item it = new Item(); it.Role = "user"; it.Text = text; it.Tag = tag; it.Time = time;
            AddItem(it, true);
        }
        public void BeginAssistant(string tag)
        {
            Item it = new Item(); it.Role = "assistant"; it.Text = ""; it.Tag = tag; it.Stream = true;
            AddItem(it, true); UpdateDots();
        }
        public void SetLastText(string text)
        {
            if (items.Count == 0) return;
            Item it = items[items.Count - 1];
            bool stick = Stick();
            it.Text = text;
            EnsureLayout(); LayoutItem(it, ContentW, true); Reflow();
            if (stick) ScrollNow(MaxScroll);
            UpdateDots(); Invalidate();
        }
        public void EndStream(string time)
        {
            if (items.Count == 0) return;
            Item it = items[items.Count - 1]; it.Stream = false; it.Time = time;
            UpdateDots(); Invalidate();
        }
        public void RemoveLastEmpty()
        {
            if (items.Count == 0) return;
            Item it = items[items.Count - 1];
            if (it.Role == "assistant" && string.IsNullOrEmpty(it.Text)) { items.RemoveAt(items.Count - 1); Reflow(); ClampScroll(); UpdateDots(); Invalidate(); }
        }
        public void AddNote(string text, bool err)
        {
            Item it = new Item(); it.Role = "note"; it.Text = text; it.Err = err;
            AddItem(it, true);
        }

        void AddItem(Item it, bool smooth)
        {
            bool stick = true;
            EnsureLayout(); LayoutItem(it, ContentW, false); items.Add(it); Reflow();
            if (stick) { target = MaxScroll; if (smooth) StartScroll(); else ScrollNow(target); }
            Invalidate();
        }

        void UpdateDots()
        {
            bool need = false;
            foreach (Item it in items) if (it.Stream && string.IsNullOrEmpty(it.Text)) need = true;
            if (need && !dotT.Enabled) dotT.Start(); else if (!need && dotT.Enabled) dotT.Stop();
        }

        // ───────── 布局 ─────────
        int ContentW { get { return Math.Max(Dpi.S(160), Width - 2 * padX - gutter); } }
        int MaxScroll { get { return Math.Max(0, contentH - Height); } }
        bool Stick() { return target >= MaxScroll - Dpi.S(36); }

        void EnsureLayout()
        {
            if (Width <= 0) return;
            if (layW == Width) return;
            int cw = ContentW;
            foreach (Item it in items) LayoutItem(it, cw, false);
            layW = Width; Reflow();
        }

        void Reflow()
        {
            int y = Dpi.S(10);
            int k = 0; foreach (Item it in items) { it.Ix = k++; it.Top = y; y += it.H + gap; }
            contentH = y - gap + Dpi.S(10);
        }

        void LayoutItem(Item it, int cw, bool resume)
        {
            if (!resume) { it.Lines.Clear(); it.Blocks.Clear(); it.Sn = null; }
            if (it.Role == "user")
            {
                int maxW = (int)(cw * 0.86f) - 2 * bubPadX;
                it.TextH = LayoutText(it, maxW, false);
                int tw = it.MaxLine;
                if (!string.IsNullOrEmpty(it.Tag)) tw = Math.Max(tw, TextRenderer.MeasureText(it.Tag, fSmallB, new Size(9999, 99), MF).Width);
                it.BubW = Math.Max(Dpi.S(40), tw + 2 * bubPadX);
                it.H = (string.IsNullOrEmpty(it.Tag) ? 0 : tagH) + it.TextH + 2 * bubPadY + timeH;
            }
            else if (it.Role == "assistant")
            {
                int maxW = cw - 2 * cardPadX;
                int th = LayoutText(it, maxW, resume);
                if (th < lh) th = lh;
                it.TextH = th;
                it.H = headH + th + Dpi.S(12);
            }
            else
            {
                int maxW = cw - 2 * notePad;
                it.TextH = LayoutText(it, maxW, false);
                it.H = it.Err ? it.TextH + 2 * notePad : it.TextH + Dpi.S(4);
            }
        }

        static bool IsWide(char c) { return c >= 0x2E80; }
        static bool IsClosing(string u) { return u.Length == 1 && "，。、！？；：）】》”’…」』".IndexOf(u[0]) >= 0; }

        static List<string> Tok(string s)
        {
            List<string> l = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t') { l.Add(" "); while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++; }
                else if (IsWide(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { l.Add(s.Substring(i, 2)); i += 2; }
                    else { l.Add(c.ToString()); i++; }
                }
                else
                {
                    int j = i;
                    while (j < s.Length && s[j] != ' ' && s[j] != '\t' && !IsWide(s[j])) j++;
                    l.Add(s.Substring(i, j - i)); i = j;
                }
            }
            return l;
        }

        static List<Run> Inline(string t, bool head)
        {
            List<Run> res = new List<Run>(); bool bold = false, ic = false; StringBuilder sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                char ch = t[i];
                if (!ic && ch == '*' && i + 1 < t.Length && t[i + 1] == '*') { FlushRun(res, sb, bold, ic, head); bold = !bold; i++; continue; }
                if (ch == '`') { FlushRun(res, sb, bold, ic, head); ic = !ic; continue; }
                sb.Append(ch);
            }
            FlushRun(res, sb, bold, ic, head);
            return res;
        }
        static void FlushRun(List<Run> res, StringBuilder sb, bool bold, bool ic, bool head)
        {
            if (sb.Length == 0) return;
            Run r = new Run(); r.T = sb.ToString(); r.St = ic ? 2 : (head ? 3 : (bold ? 1 : 0));
            res.Add(r); sb.Length = 0;
        }

        static void Put(Line ln, string text, int st, int x, int w)
        {
            if (ln.Segs.Count > 0)
            {
                Seg last = ln.Segs[ln.Segs.Count - 1];
                if (last.St == st && last.X + last.W == x) { last.T += text; last.W += w; return; }
            }
            Seg s = new Seg(); s.T = text; s.X = x; s.W = w; s.St = st; ln.Segs.Add(s);
        }

        int WrapLine(Item it, List<Run> runs, string prefix, int maxW, int y, int brk)
        {
            Line cur = new Line(); cur.Y = y; cur.Brk = brk; it.Lines.Add(cur);
            int x = 0, indent = 0;
            if (prefix != null) { int pw = MW(prefix, 0); Put(cur, prefix, 0, 0, pw); x = pw + spW[0] + Dpi.S(2); indent = x; }
            foreach (Run r in runs)
            {
                foreach (string u in Tok(r.T))
                {
                    if (u == " ")
                    {
                        if (x <= indent) continue;
                        int sw = spW[r.St];
                        if (x + sw > maxW) { y += lh; cur = new Line(); cur.Y = y; it.Lines.Add(cur); x = indent; continue; }
                        Put(cur, " ", r.St, x, sw); x += sw; continue;
                    }
                    int w = MW(u, r.St);
                    if (x + w > maxW && x > indent && !IsClosing(u)) { y += lh; cur = new Line(); cur.Y = y; it.Lines.Add(cur); x = indent; }
                    if (x + w > maxW && w > maxW - indent && u.Length > 1)
                    {
                        for (int k = 0; k < u.Length; k++)
                        {
                            string ch = (char.IsHighSurrogate(u[k]) && k + 1 < u.Length) ? u.Substring(k, 2) : u[k].ToString();
                            if (ch.Length == 2) k++;
                            int cw1 = MW(ch, r.St);
                            if (x + cw1 > maxW && x > indent) { y += lh; cur = new Line(); cur.Y = y; it.Lines.Add(cur); x = indent; }
                            Put(cur, ch, r.St, x, cw1); x += cw1;
                        }
                    }
                    else { Put(cur, u, r.St, x, w); x += w; }
                }
            }
            return y + lh;
        }

        // resume=true：流式输出时只重排“最后一行及之后”的内容，之前已完成的行直接复用
        int LayoutText(Item it, int maxW, bool resume)
        {
            it.MaxLine = 0;
            string[] src = (it.Text ?? "").Replace("\r\n", "\n").Split('\n');
            int y = 0; bool code = false, lastBlank = true; CodeBlk cb = null; int startSi = 0;
            if (resume && it.Sn != null && it.SnapW == maxW && it.Sn.Si < src.Length)
            {
                Snap n = it.Sn; startSi = n.Si; y = n.Y; code = n.Code; lastBlank = n.LastBlank;
                it.Lines.RemoveRange(n.LinesN, it.Lines.Count - n.LinesN);
                it.Blocks.RemoveRange(n.BlocksN, it.Blocks.Count - n.BlocksN);
                if (code) { cb = new CodeBlk(); cb.Y0 = n.CbY0; cb.Text.Append(n.CbText); }
            }
            else { it.Lines.Clear(); it.Blocks.Clear(); it.Sn = null; }
            int cols = Math.Max(8, (maxW - 2 * codePad) / charW);
            int gapS = Dpi.S(6);
            for (int si = startSi; si < src.Length; si++)
            {
                string ln = src[si];
                if (si == src.Length - 1)
                {
                    Snap sn = new Snap(); sn.Si = si; sn.Y = y; sn.LinesN = it.Lines.Count; sn.BlocksN = it.Blocks.Count;
                    sn.Code = code; sn.LastBlank = lastBlank; sn.CbY0 = cb != null ? cb.Y0 : 0; sn.CbText = cb != null ? cb.Text.ToString() : "";
                    it.Sn = sn; it.SnapW = maxW;
                }
                if (ln.TrimStart().StartsWith("```"))
                {
                    if (!code) { code = true; if (y > 0 && !lastBlank) y += gapS; cb = new CodeBlk(); cb.Y0 = y; y += codePadV + Dpi.S(14); }
                    else { code = false; y += codePadV; cb.Y1 = y; it.Blocks.Add(cb); cb = null; y += gapS; lastBlank = true; }
                    continue;
                }
                if (code)
                {
                    if (cb.Text.Length > 0) cb.Text.Append('\n');
                    cb.Text.Append(ln);
                    string l2 = ln.Replace("\t", "    ");
                    int pos = 0; bool firstChunk = true;
                    do
                    {
                        StringBuilder chunk = new StringBuilder(); int cc = 0;
                        while (pos < l2.Length)
                        {
                            int add = IsWide(l2[pos]) ? 2 : 1;
                            if (cc + add > cols && cc > 0) break;
                            chunk.Append(l2[pos]); cc += add; pos++;
                        }
                        Line L = new Line(); L.Y = y; L.Code = true; L.Brk = firstChunk ? 1 : 0; firstChunk = false;
                        Seg s = new Seg(); s.T = chunk.ToString(); s.X = codePad; s.W = MW(s.T, 2); s.St = 2; L.Segs.Add(s);
                        it.Lines.Add(L); y += lh;
                    } while (pos < l2.Length);
                    continue;
                }
                if (ln.Trim().Length == 0) { if (!lastBlank) y += lh / 2; lastBlank = true; continue; }
                lastBlank = false;
                bool head = false; string prefix = null, body = ln;
                if (ln.StartsWith("#")) { body = ln.TrimStart('#').Trim(); head = true; if (y > 0) y += lh / 4; }
                else if (ln.StartsWith("- ") || ln.StartsWith("* ")) { prefix = "•"; body = ln.Substring(2); }
                else { Match mm = NumRe.Match(ln); if (mm.Success) { prefix = mm.Groups[1].Value; body = mm.Groups[2].Value; } }
                y = WrapLine(it, Inline(body, head), prefix, maxW, y, (si > 0 && src[si - 1].Trim().Length == 0) ? 2 : 1);
            }
            if (code && cb != null) { y += codePadV; cb.Y1 = y; it.Blocks.Add(cb); }
            if (lastBlank && y > 0 && it.Lines.Count > 0 && !code) { /* 末尾空行不额外占位 */ }
            foreach (Line L in it.Lines)
                foreach (Seg s in L.Segs) it.MaxLine = Math.Max(it.MaxLine, s.X + s.W);
            if (it.Blocks.Count > 0) it.MaxLine = maxW;
            return y;
        }


        // ───────── 自由选择 / 复制 ─────────
        Pos anchor, caret;
        bool selecting; int clickN, lastClickT; Point lastClickP;
        ContextMenuStrip ctx;

        public bool HasSelection { get { return anchor != null && caret != null && (anchor.I != caret.I || anchor.L != caret.L || anchor.C != caret.C); } }
        public void ClearSelection() { if (anchor == null && caret == null) return; anchor = caret = null; selecting = false; Invalidate(); }

        static int CmpPos(int i1, int l1, int c1, int i2, int l2, int c2)
        {
            if (i1 != i2) return i1 < i2 ? -1 : 1;
            if (l1 != l2) return l1 < l2 ? -1 : 1;
            if (c1 != c2) return c1 < c2 ? -1 : 1;
            return 0;
        }
        void Norm(out Pos a, out Pos b)
        {
            if (CmpPos(anchor.I, anchor.L, anchor.C, caret.I, caret.L, caret.C) <= 0) { a = anchor; b = caret; } else { a = caret; b = anchor; }
        }

        static string LineText(Line L)
        {
            if (L.Segs.Count == 1) return L.Segs[0].T;
            StringBuilder sb = new StringBuilder();
            foreach (Seg s in L.Segs) sb.Append(s.T);
            return sb.ToString();
        }

        // 文字起点（与 DrawUser / DrawAssistant / DrawNote 保持一致）
        void Origin(Item it, int top, out int x0, out int y0)
        {
            if (it.Role == "user")
            {
                int bx = Width - gutter - padX - it.BubW;
                x0 = bx + bubPadX; y0 = top + bubPadY + (string.IsNullOrEmpty(it.Tag) ? 0 : tagH);
            }
            else if (it.Role == "assistant") { x0 = padX + cardPadX; y0 = top + headH; }
            else if (it.Err) { x0 = padX + notePad; y0 = top + notePad; }
            else
            {
                int w = 0; foreach (Line L in it.Lines) foreach (Seg s in L.Segs) w = Math.Max(w, s.X + s.W);
                x0 = padX + (ContentW - w) / 2; y0 = top + Dpi.S(2);
            }
        }

        // 屏幕坐标 → 选择位置（行内按字符边界取最近）
        Pos PosAt(Point p)
        {
            if (items.Count == 0) return null;
            int sc = (int)Math.Round(scroll);
            int ii = -1; bool before = false, after = false;
            for (int k = 0; k < items.Count; k++)
            {
                Item it = items[k]; int t = it.Top - sc;
                if (p.Y < t) { ii = k; before = true; break; }
                if (p.Y <= t + it.H) { ii = k; break; }
            }
            if (ii < 0) { ii = items.Count - 1; after = true; }
            Item item = items[ii];
            if (item.Lines.Count == 0) return new Pos(ii, 0, 0);
            if (before) return new Pos(ii, 0, 0);
            if (after) { int last = item.Lines.Count - 1; return new Pos(ii, last, LineText(item.Lines[last]).Length); }
            int x0, y0; Origin(item, item.Top - sc, out x0, out y0);
            int rel = p.Y - y0, li = item.Lines.Count - 1;
            for (int k = 0; k < item.Lines.Count; k++) if (rel < item.Lines[k].Y + lh) { li = k; break; }
            Line L = item.Lines[li];
            if (rel >= L.Y + lh && li == item.Lines.Count - 1 && rel > L.Y + lh * 2) return new Pos(ii, li, LineText(L).Length);
            return new Pos(ii, li, CharAt(L, p.X - x0));
        }

        int CharAt(Line L, int lx)
        {
            int start = 0;
            foreach (Seg s in L.Segs)
            {
                if (lx < s.X) return start;
                if (lx <= s.X + s.W)
                {
                    int rel = lx - s.X, prev = 0;
                    for (int k = 1; k <= s.T.Length; k++)
                    {
                        if (char.IsLowSurrogate(s.T[k - 1]) == false && k < s.T.Length && char.IsLowSurrogate(s.T[k])) continue;
                        int w = MW(s.T.Substring(0, k), s.St);
                        if (rel < (prev + w) / 2) return start + (k - 1 > 0 && char.IsLowSurrogate(s.T[k - 1]) ? k - 2 : k - 1);
                        prev = w;
                    }
                    return start + s.T.Length;
                }
                start += s.T.Length;
            }
            return start;
        }

        int XOf(Seg s, int k) { return k <= 0 ? 0 : (k >= s.T.Length ? s.W : Math.Min(s.W, MW(s.T.Substring(0, k), s.St))); }

        // 该行被选中的字符区间
        bool SelOnLine(Item it, int li, Line L, out int c0, out int c1)
        {
            c0 = c1 = 0;
            if (!HasSelection) return false;
            Pos a, b; Norm(out a, out b);
            int ca = CmpPos(it.Ix, li, 0, a.I, a.L, 0), cb = CmpPos(it.Ix, li, 0, b.I, b.L, 0);
            if (ca < 0 || cb > 0) return false;
            int len = LineText(L).Length;
            c0 = ca == 0 ? Math.Min(a.C, len) : 0;
            c1 = cb == 0 ? Math.Min(b.C, len) : len;
            return c1 > c0;
        }

        static int Cls(char ch)
        {
            if (ch == ' ' || ch == '\t') return 0;
            if (ch >= 0x2E80) return "，。、！？；：（）【】《》“”‘’…「」『』".IndexOf(ch) >= 0 ? 3 : 2;
            if (char.IsLetterOrDigit(ch) || ch == '_') return 1;
            return 3;
        }

        void SelectWord(Pos p)
        {
            Item it = items[p.I]; if (p.L >= it.Lines.Count) return;
            string t = LineText(it.Lines[p.L]); if (t.Length == 0) return;
            int i = Math.Max(0, Math.Min(t.Length - 1, p.C));
            int c = Cls(t[i]), a = i, b = i + 1;
            if (c != 3) { while (a > 0 && Cls(t[a - 1]) == c) a--; while (b < t.Length && Cls(t[b]) == c) b++; }
            anchor = new Pos(p.I, p.L, a); caret = new Pos(p.I, p.L, b);
        }

        void SelectParagraph(Pos p)
        {
            Item it = items[p.I]; if (it.Lines.Count == 0) return;
            int l0 = Math.Min(p.L, it.Lines.Count - 1), l1 = l0;
            while (l0 > 0 && it.Lines[l0].Brk == 0) l0--;
            while (l1 + 1 < it.Lines.Count && it.Lines[l1 + 1].Brk == 0) l1++;
            anchor = new Pos(p.I, l0, 0); caret = new Pos(p.I, l1, LineText(it.Lines[l1]).Length);
        }

        public void SelectAll()
        {
            if (items.Count == 0) return;
            Item last = items[items.Count - 1];
            anchor = new Pos(0, 0, 0);
            int ll = Math.Max(0, last.Lines.Count - 1);
            caret = new Pos(items.Count - 1, ll, last.Lines.Count > 0 ? LineText(last.Lines[ll]).Length : 0);
            Invalidate();
        }

        static bool NeedSpace(char prev, char next)
        {
            return prev < 0x2E80 && next < 0x2E80 && !char.IsWhiteSpace(prev) && !char.IsWhiteSpace(next);
        }

        public string SelectedText()
        {
            if (!HasSelection) return "";
            Pos a, b; Norm(out a, out b);
            StringBuilder sb = new StringBuilder();
            for (int i = a.I; i <= b.I && i < items.Count; i++)
            {
                Item it = items[i];
                int l0 = i == a.I ? a.L : 0, l1 = i == b.I ? Math.Min(b.L, it.Lines.Count - 1) : it.Lines.Count - 1;
                bool started = false;
                for (int l = l0; l <= l1; l++)
                {
                    string t = LineText(it.Lines[l]);
                    int c0 = (i == a.I && l == a.L) ? Math.Min(a.C, t.Length) : 0;
                    int c1 = (i == b.I && l == b.L) ? Math.Min(b.C, t.Length) : t.Length;
                    string part = c1 > c0 ? t.Substring(c0, c1 - c0) : "";
                    if (sb.Length > 0)
                    {
                        if (!started) sb.Append("\n\n");
                        else
                        {
                            int brk = it.Lines[l].Brk;
                            if (brk == 0) { if (part.Length > 0 && NeedSpace(sb[sb.Length - 1], part[0])) sb.Append(' '); }
                            else sb.Append(brk == 2 ? "\n\n" : "\n");
                        }
                    }
                    sb.Append(part); started = true;
                }
            }
            return sb.ToString();
        }

        public bool CopySelection()
        {
            string t = SelectedText();
            if (t.Length == 0) return false;
            try { Clipboard.SetText(t); return true; } catch { return false; }
        }

        void EnsureMenu()
        {
            if (ctx != null) return;
            ctx = new ContextMenuStrip();
            ToolStripMenuItem miCopy = new ToolStripMenuItem("复制", null, delegate { CopySelection(); });
            ToolStripMenuItem miAll = new ToolStripMenuItem("全选", null, delegate { SelectAll(); });
            ctx.Items.Add(miCopy); ctx.Items.Add(miAll);
            ctx.Opening += delegate { miCopy.Enabled = HasSelection; miAll.Enabled = items.Count > 0; };
            ContextMenuStrip = ctx;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { if (CopySelection()) { e.Handled = true; e.SuppressKeyPress = true; } return; }
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        // ───────── 滚动 ─────────
        void ClampScroll() { target = Math.Max(0, Math.Min(MaxScroll, target)); scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); }
        void ScrollNow(float v) { target = scroll = Math.Max(0, Math.Min(MaxScroll, v)); }
        void StartScroll() { if (!scrollAnim.Enabled) scrollAnim.Start(); }
        void StepScroll()
        {
            float d = target - scroll;
            if (Math.Abs(d) < 0.6f) { scroll = target; scrollAnim.Stop(); }
            else scroll += d * 0.28f;
            Invalidate();
        }
        public void ScrollToEnd() { target = MaxScroll; StartScroll(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            target = Math.Max(0, Math.Min(MaxScroll, target - e.Delta / 120f * Dpi.S(72)));
            StartScroll();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bool st = Stick();
            layW = -1; EnsureLayout();
            if (st) ScrollNow(MaxScroll); else ClampScroll();
            Invalidate();
        }

        Rectangle BarRect()
        {
            if (contentH <= Height) return Rectangle.Empty;
            int w = (dragBar || barHover) ? Dpi.S(7) : Dpi.S(4);
            int trackH = Height - Dpi.S(16);
            int th = Math.Max(Dpi.S(30), (int)(trackH * (float)Height / contentH));
            int ty = Dpi.S(8) + (int)((trackH - th) * (MaxScroll == 0 ? 0 : scroll / MaxScroll));
            return new Rectangle(Width - w - Dpi.S(3), ty, w, th);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= Width - Dpi.S(16) && contentH > Height)
            {
                Rectangle br = BarRect();
                if (br.Contains(e.X, e.Y) || (e.X >= br.Left - Dpi.S(8) && e.Y >= br.Top && e.Y <= br.Bottom)) { dragBar = true; dragOff = e.Y - br.Top; }
                else { target = e.Y < br.Top ? Math.Max(0, target - Height * 0.85f) : Math.Min(MaxScroll, target + Height * 0.85f); StartScroll(); }
                Invalidate();
                return;
            }
            foreach (Hit x in hits) if (x.R.Contains(e.Location)) return;   // 点“复制”按钮：交给 MouseUp 处理
            Focus();
            Pos p = PosAt(e.Location);
            if (p == null) return;
            int now = Environment.TickCount; Size ds = SystemInformation.DoubleClickSize;
            if (now - lastClickT <= SystemInformation.DoubleClickTime && Math.Abs(e.X - lastClickP.X) <= ds.Width && Math.Abs(e.Y - lastClickP.Y) <= ds.Height) clickN++; else clickN = 1;
            if (clickN > 3) clickN = 1;
            lastClickT = now; lastClickP = e.Location;
            if (clickN == 2) { SelectWord(p); selecting = false; }
            else if (clickN == 3) { SelectParagraph(p); selecting = false; }
            else { anchor = p; caret = p; selecting = true; }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragBar)
            {
                int trackH = Height - Dpi.S(16);
                Rectangle br = BarRect();
                int range = Math.Max(1, trackH - br.Height);
                float v = (e.Y - dragOff - Dpi.S(8)) / (float)range * MaxScroll;
                ScrollNow(v); Invalidate(); return;
            }
            if (selecting)
            {
                if (e.Y < 0) { target = Math.Max(0, target - Dpi.S(26)); scroll = target; }
                else if (e.Y > Height) { target = Math.Min(MaxScroll, target + Dpi.S(26)); scroll = target; }
                caret = PosAt(new Point(e.X, Math.Max(0, Math.Min(Height, e.Y))));
                Cursor = Cursors.IBeam; Invalidate(); return;
            }
            bool bh = e.X >= Width - Dpi.S(16) && contentH > Height;
            Hit h = null;
            foreach (Hit x in hits) if (x.R.Contains(e.Location)) { h = x; break; }
            Item hi = null;
            foreach (Item it in items) { int t = it.Top - (int)Math.Round(scroll); if (e.Y >= t && e.Y <= t + it.H) { hi = it; break; } }
            if (bh != barHover || h != hoverHit || hi != hoverItem)
            {
                barHover = bh; hoverHit = h; hoverItem = hi;
                Cursor = h != null ? Cursors.Hand : (hi != null && !bh ? Cursors.IBeam : Cursors.Default);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragBar) { barHover = false; hoverHit = null; hoverItem = null; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragBar) { dragBar = false; Invalidate(); return; }
            if (selecting)
            {
                selecting = false;
                if (!HasSelection) { anchor = caret = null; }
                Invalidate(); return;
            }
            if (e.Button == MouseButtons.Left && hoverHit != null && hoverHit.R.Contains(e.Location))
            {
                string txt = hoverHit.Blk != null ? hoverHit.Blk.Text.ToString() : hoverHit.It.Text;
                try { Clipboard.SetText(txt); } catch { return; }
                int tick = Environment.TickCount;
                if (hoverHit.Blk != null) hoverHit.Blk.Flash = tick; else hoverHit.It.Flash = tick;
                flashT.Stop(); flashT.Start(); Invalidate();
            }
        }

        // ───────── 绘制 ─────────
        void DrawLines(Graphics g, Item it, int x0, int y0, Color fg)
        {
            for (int li = 0; li < it.Lines.Count; li++)
            {
                Line L = it.Lines[li];
                int ly = y0 + L.Y;
                if (ly + lh < 0 || ly > Height) continue;
                int sc0, sc1;
                if (SelOnLine(it, li, L, out sc0, out sc1))
                {
                    using (SolidBrush sb = new SolidBrush(it.Role == "user" ? Color.FromArgb(95, 255, 255, 255) : Color.FromArgb(Theme.Dark ? 110 : 80, Theme.Accent)))
                    {
                        int segStart = 0;
                        foreach (Seg s in L.Segs)
                        {
                            int s0 = segStart, s1 = s0 + s.T.Length; segStart = s1;
                            int i0 = Math.Max(sc0, s0), i1 = Math.Min(sc1, s1);
                            if (i1 <= i0) continue;
                            int xa = x0 + s.X + XOf(s, i0 - s0), xb = x0 + s.X + XOf(s, i1 - s0);
                            g.FillRectangle(sb, xa, ly, Math.Max(2, xb - xa), lh);
                        }
                    }
                }
                foreach (Seg s in L.Segs)
                {
                    Font f = FontOf(s.St); int ty = ly + (lh - f.Height) / 2;
                    if (L.Code) TextRenderer.DrawText(g, s.T, f, new Point(x0 + s.X, ty), Theme.CodeFg, DF);
                    else if (s.St == 2)
                    {
                        using (GraphicsPath p = Gfx.RR(new RectangleF(x0 + s.X - 2, ly + Dpi.S(3), s.W + 4, lh - Dpi.S(6)), 4))
                        using (SolidBrush b = new SolidBrush(Theme.CodeBg)) g.FillPath(b, p);
                        TextRenderer.DrawText(g, s.T, f, new Point(x0 + s.X, ty), Theme.CodeFg, DF);
                    }
                    else TextRenderer.DrawText(g, s.T, f, new Point(x0 + s.X, ty), fg, DF);
                }
            }
        }

        void DrawCodeBlocks(Graphics g, Item it, int x0, int y0, int w)
        {
            foreach (CodeBlk b in it.Blocks)
            {
                Rectangle r = new Rectangle(x0, y0 + b.Y0, w, b.Y1 - b.Y0);
                if (r.Bottom < 0 || r.Top > Height) continue;
                using (GraphicsPath p = Gfx.RR(r, Dpi.S(10)))
                using (SolidBrush br = new SolidBrush(Theme.CodeBg)) g.FillPath(br, p);
                bool fl = Environment.TickCount - b.Flash < 1300 && b.Flash != 0;
                string cap = fl ? "已复制 ✓" : "复制代码";
                int cw = TextRenderer.MeasureText(cap, fSmall, new Size(999, 99), MF).Width;
                Rectangle cr = new Rectangle(r.Right - cw - Dpi.S(12), r.Top + Dpi.S(5), cw + Dpi.S(8), Dpi.S(18));
                TextRenderer.DrawText(g, cap, fSmall, new Point(cr.X + Dpi.S(4), cr.Y + Dpi.S(2)), fl ? Theme.Accent : Theme.Sub, DF);
                Hit h = new Hit(); h.R = cr; h.It = it; h.Blk = b; hits.Add(h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            EnsureLayout();
            hits.Clear();
            if (items.Count == 0) { DrawWelcome(g); return; }
            int sc = (int)Math.Round(scroll);
            int cw = ContentW;
            foreach (Item it in items)
            {
                int top = it.Top - sc;
                if (top + it.H < 0) continue;
                if (top > Height) break;
                if (it.Role == "user") DrawUser(g, it, top);
                else if (it.Role == "assistant") DrawAssistant(g, it, top, cw);
                else DrawNote(g, it, top, cw);
            }
            DrawBar(g);
        }

        void DrawCaption(Graphics g, Item it, int right, int y, bool hovered)
        {
            bool fl = it.Flash != 0 && Environment.TickCount - it.Flash < 1300;
            string cap; Color col;
            if (hovered || fl) { cap = fl ? "已复制 ✓" : "复制"; col = Theme.Accent; }
            else { cap = it.Time ?? ""; col = Theme.Sub; }
            if (cap.Length == 0) return;
            int w = TextRenderer.MeasureText(cap, fSmall, new Size(999, 99), MF).Width;
            TextRenderer.DrawText(g, cap, fSmall, new Point(right - w, y), col, DF);
            if (hovered || fl)
            {
                Hit h = new Hit(); h.R = new Rectangle(right - w - Dpi.S(8), y - Dpi.S(4), w + Dpi.S(16), Dpi.S(20)); h.It = it; hits.Add(h);
            }
        }

        void DrawUser(Graphics g, Item it, int top)
        {
            bool hasTag = !string.IsNullOrEmpty(it.Tag);
            int bx = Width - gutter - padX - it.BubW;
            int bh = (hasTag ? tagH : 0) + it.TextH + 2 * bubPadY;
            using (GraphicsPath p = Gfx.RR(new Rectangle(bx, top, it.BubW, bh), radius))
            using (SolidBrush b = new SolidBrush(Theme.UserBg)) g.FillPath(b, p);
            int ty = top + bubPadY;
            if (hasTag)
            {
                TextRenderer.DrawText(g, it.Tag, fSmallB, new Point(bx + bubPadX, ty - Dpi.S(1)), Gfx.Mix(Theme.UserFg, Theme.UserBg, 0.35f), DF);
                ty += tagH;
            }
            DrawLines(g, it, bx + bubPadX, ty, Theme.UserFg);
            DrawCodeBlocks(g, it, bx + bubPadX, ty, it.BubW - 2 * bubPadX);
            DrawCaption(g, it, bx + it.BubW - Dpi.S(2), top + bh + Dpi.S(3), hoverItem == it);
        }

        void DrawAssistant(Graphics g, Item it, int top, int cw)
        {
            Rectangle cr = new Rectangle(padX, top, cw, it.H);
            using (GraphicsPath p = Gfx.RR(new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1), radius))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
            }
            int av = Dpi.S(20);
            BallArt.DrawMini(g, new RectangleF(cr.X + cardPadX - Dpi.S(3), top + (headH - av) / 2f, av, av));
            int nx = cr.X + cardPadX + av;
            TextRenderer.DrawText(g, "AI", fSmallB, new Point(nx, top + Dpi.S(8)), Theme.Fg, DF);
            if (!string.IsNullOrEmpty(it.Tag))
                TextRenderer.DrawText(g, "· " + it.Tag, fSmall, new Point(nx + Dpi.S(20), top + Dpi.S(9)), Theme.Sub, DF);
            if (!it.Stream) DrawCaption(g, it, cr.Right - Dpi.S(12), top + Dpi.S(9), hoverItem == it);
            int x0 = cr.X + cardPadX, y0 = top + headH;
            if (it.Stream && string.IsNullOrEmpty(it.Text))
            {
                int n = (Environment.TickCount / 350) % 4;
                TextRenderer.DrawText(g, new string('·', n == 0 ? 1 : n) + " 思考中", fBody, new Point(x0, y0 + (lh - fBody.Height) / 2), Theme.Sub, DF);
            }
            else
            {
                DrawCodeBlocks(g, it, x0, y0, cw - 2 * cardPadX);
                DrawLines(g, it, x0, y0, Theme.Fg);
            }
        }

        void DrawNote(Graphics g, Item it, int top, int cw)
        {
            if (it.Err)
            {
                using (GraphicsPath p = Gfx.RR(new Rectangle(padX, top, cw, it.H), Dpi.S(12)))
                using (SolidBrush b = new SolidBrush(Theme.ErrBg)) g.FillPath(b, p);
                DrawLines(g, it, padX + notePad, top + notePad, Theme.Err);
            }
            else
            {
                int w = 0; foreach (Line L in it.Lines) foreach (Seg s in L.Segs) w = Math.Max(w, s.X + s.W);
                DrawLines(g, it, padX + (cw - w) / 2, top + Dpi.S(2), Theme.Sub);
            }
        }

        void DrawBar(Graphics g)
        {
            Rectangle br = BarRect();
            if (br.IsEmpty) return;
            int a = (dragBar || barHover) ? 170 : 85;
            using (GraphicsPath p = Gfx.RR(br, br.Width / 2f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Theme.Sub))) g.FillPath(b, p);
        }

        void DrawWelcome(Graphics g)
        {
            int ps = Dpi.S(96);
            int total = ps + Dpi.S(14) + lh + Dpi.S(8) + Welcome.Length * (lh - Dpi.S(2));
            int y = Math.Max(Dpi.S(10), (Height - total) / 2 - Dpi.S(10));
            GraphicsState st = g.Save();
            g.TranslateTransform((Width - ps) / 2f, y);
            BallArt.Draw(g, ps, (int)(ps * 0.9f), wv);
            g.Restore(st);
            y += ps + Dpi.S(10);
            TextRenderer.DrawText(g, WelcomeTitle, fHead, new Rectangle(0, y, Width, lh), Theme.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            y += lh + Dpi.S(8);
            foreach (string s in Welcome)
            {
                TextRenderer.DrawText(g, s, fSmall, new Rectangle(Dpi.S(16), y, Width - Dpi.S(32), lh), Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                y += lh - Dpi.S(2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                scrollAnim.Dispose(); flashT.Dispose(); dotT.Dispose();
                fBody.Dispose(); fBold.Dispose(); fCode.Dispose(); fHead.Dispose(); fSmall.Dispose(); fSmallB.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
