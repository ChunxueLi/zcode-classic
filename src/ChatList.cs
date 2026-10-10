// ZCode7 - conversation timeline, owner-drawn (zai-light spec from packages/ui v4)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ZCode7
{
    public enum RowKind { Info, User, Assistant, Thinking, Tool, WorkStatus, Approval }

    public class Seg
    {
        public string Text;
        public bool Bold;
        public bool Code;
    }

    public class SegPos
    {
        public Seg Seg;
        public int Start, Len;
        public float X, W;
    }

    public class LayoutLine
    {
        public List<SegPos> Parts = new List<SegPos>();
        public int Height;
        public string ListMarker;   // "•" or "1." etc; indents content
        public bool InCodeBlock;    // fenced code line: mono + shared bg
        public float Indent;        // content x-offset
    }

    public class ChatRow
    {
        public RowKind Kind;
        public string Text = "";
        public string ToolName = "";
        public string Args = "";
        public string Result = "";
        public string Duration = "";
        public string DetailLabel = "";   // expanded card arg label (命令/参数)
        public bool Running;
        public bool Expanded;
        public int AddLines = -1, DelLines = -1;
        public bool Decided;
        public Rectangle CopyRect;        // hover copy button (row-local)
        public bool Allowed;
        public Rectangle AllowRect;   // row-local coords
        public Rectangle DenyRect;
        public string InteractionId;  // approval interaction link
        public int Height;

        // measured
        public List<LayoutLine> Lines = new List<LayoutLine>();
        public List<LayoutLine> DetailLines = new List<LayoutLine>();
        public int BodyTop;           // y offset of expandable body inside row
        public int HeaderH;
    }

    public class ChatList : Panel
    {
        private List<ChatRow> _rows = new List<ChatRow>();
        private float _scroll = 0f;
        private int _layoutH = 1;         // deterministic content height (computed on layout, not paint)
        private bool _stickBottom = true; // auto-follow while streaming; off when user scrolls up
        private bool _dragScroll;
        private int _dragY;
        private float _dragScrollStart;
        private int _hoverRow = -1;
        public bool StatusPanelVisible;
        public int Tick; // incremented for spinner frames

        public event Action<ChatRow, bool> ApprovalDecided;

        public ChatList()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            ResizeRedraw = true;
        }

        public IList<ChatRow> Rows { get { return _rows; } }

        public void RemoveRow(ChatRow r)
        {
            _rows.Remove(r);
            RecalcHeight();
            Invalidate();
        }

        public void Clear()
        {
            _rows.Clear();
            _scroll = 0;
            _stickBottom = true;
            Relayout();
        }

        public ChatRow Add(RowKind kind)
        {
            var r = new ChatRow { Kind = kind };
            _rows.Add(r);
            return r;
        }


        public void Changed(ChatRow r)
        {
            RelayoutRow(r);
            RecalcHeight();
            if (_stickBottom) { _scroll = Math.Max(0, _layoutH - Height); }
            Invalidate();
        }

        private void RecalcHeight()
        {
            int h = 8;
            foreach (var r in _rows) h += r.Height + 20;
            _layoutH = h + 60;
        }

        public void ScrollToBottom()
        {
            _stickBottom = true;
            _scroll = Math.Max(0, _layoutH - Height);
            Invalidate();
        }

        private int ContentWidth()
        {
            int avail = Width - 48;                       // px-4*2 + slack
            if (StatusPanelVisible) avail = Width - 384 - 24;
            return Math.Max(320, Math.Min(896, avail));   // max-w-4xl 896
        }

        private int ContentX()
        {
            int avail = Width - 48;
            if (StatusPanelVisible) avail = Width - 384 - 24;
            int w = Math.Max(320, Math.Min(896, avail));
            return (Width - w) / 2;
        }

        private static readonly Dictionary<string, string> KindLabel = new Dictionary<string, string>
        {
            { "Bash", "终端" }, { "Read", "读取" }, { "Write", "写入" }, { "Edit", "编辑" },
            { "Glob", "搜索" }, { "Grep", "搜索" }, { "WebFetch", "查阅" }, { "Skill", "技能" },
            { "Todo", "计划" }, { "SystemInfo", "系统" }, { "Mcp", "工具" }
        };

        private static readonly Dictionary<string, string> ToolIcon = new Dictionary<string, string>
        {
            { "Bash", "SquareTerminal" }, { "Read", "FileText" }, { "Write", "Pencil" },
            { "Edit", "Pencil" }, { "Glob", "Search" }, { "Grep", "Search" },
            { "WebFetch", "Globe" }, { "Skill", "Sparkles" }, { "Todo", "ListTree" },
            { "SystemInfo", "Wrench" }, { "Mcp", "Wrench" }
        };

        private string IconFor(ChatRow r)
        {
            string ic;
            if (ToolIcon.TryGetValue(r.ToolName, out ic)) return ic;
            return "Wrench";
        }

        private string KindFor(ChatRow r)
        {
            string k;
            if (KindLabel.TryGetValue(r.ToolName, out k)) return k;
            return r.ToolName;
        }

        private string ArgsPreview(ChatRow r)
        {
            string a = r.Args == null ? "" : r.Args;
            if (a.Length > 120) a = a.Substring(0, 120) + "…";
            return a.Replace("\n", " ").Replace("\r", "");
        }

        public void Relayout()
        {
            foreach (var r in _rows) RelayoutRow(r);
            RecalcHeight();
            if (_stickBottom) _scroll = Math.Max(0, _layoutH - Height);
            Invalidate();
        }

        private void RelayoutRow(ChatRow r)
        {
            int w = ContentWidth();
            int inner;
            switch (r.Kind)
            {
                case RowKind.Info:
                    r.Lines = WrapPlain(r.Text, Theme.SmallFont, w - 32);
                    r.Height = LineHeight(r.Lines, 16) + 8;
                    break;
                case RowKind.WorkStatus:
                    r.Height = 20 + 10; // text + border-b padding
                    break;
                case RowKind.User:
                    inner = Math.Min(576, w) - 32; // px-4 py-3
                    r.Lines = WrapPlain(r.Text, Theme.BaseFont, inner);
                    r.Height = 12 + LineHeight(r.Lines, 20) + 12;
                    break;
                case RowKind.Assistant:
                    r.Lines = WrapMarkdown(r.Text, Theme.BaseFont, w);
                    r.Height = Math.Max(20, LineHeight(r.Lines, 20));
                    break;
                case RowKind.Thinking:
                    r.HeaderH = 20;
                    if (r.Expanded && !string.IsNullOrEmpty(r.Text))
                    {
                        r.Lines = WrapPlain(r.Text, Theme.BaseFont, w - 32);
                        int body = Math.Min(240, LineHeight(r.Lines, 20));
                        r.Height = r.HeaderH + 12 + body;
                    }
                    else r.Height = r.HeaderH;
                    break;
                case RowKind.Approval:
                    r.HeaderH = 22;
                    if (!r.Decided)
                    {
                        r.AllowRect = new Rectangle(12, 40, 88, 30);
                        r.DenyRect = new Rectangle(108, 40, 88, 30);
                        r.Height = 40 + 30 + 12;
                    }
                    else
                    {
                        r.AllowRect = Rectangle.Empty;
                        r.DenyRect = Rectangle.Empty;
                        r.Height = 40 + 26;
                    }
                    break;

                case RowKind.Tool:
                    r.HeaderH = 20;
                    if (r.Expanded)
                    {
                        string argText = r.DetailLabel.Length > 0 ? r.DetailLabel + "  " + ArgsPreview(r) : ArgsPreview(r);
                        var argLines = WrapMono(argText, Theme.MonoFont, w - 32 - 24);
                        int argH = LineHeight(argLines, 16);
                        int resH = 0;
                        if (!string.IsNullOrEmpty(r.Result))
                        {
                            r.DetailLines = WrapMono(r.Result, Theme.MonoFont, w - 32 - 24);
                            resH = Math.Min(300, LineHeight(r.DetailLines, 16));
                        }
                        r.BodyTop = r.HeaderH + 10;
                        r.Height = r.HeaderH + 10 + 12 + argH + (resH > 0 ? 8 + resH : 0) + 12 + 2;
                    }
                    else r.Height = r.HeaderH;
                    break;
            }
        }

        private int LineHeight(List<LayoutLine> lines, int lh)
        {
            int h = 0;
            foreach (var l in lines) h += l.Height > 0 ? l.Height : lh;
            return h;
        }

        // ---------- measuring ----------

        private List<Seg> Parse(string text)
        {
            var segs = new List<Seg>();
            if (text == null) return segs;
            var sb = new StringBuilder();
            bool bold = false, code = false;
            Action flush = () =>
            {
                if (sb.Length > 0) { segs.Add(new Seg { Text = sb.ToString(), Bold = bold, Code = code }); sb.Length = 0; }
            };
            int i = 0;
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    flush(); bold = !bold; i += 2;
                }
                else if (text[i] == '`')
                {
                    flush(); code = !code; i++;
                }
                else { sb.Append(text[i]); i++; }
            }
            flush();
            return segs;
        }

        private List<LayoutLine> WrapMarkdown(string text, Font f, int width)
        {
            var lines = new List<LayoutLine>();
            bool inCode = false;
            foreach (var para in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var t = para.TrimEnd();
                if (t.StartsWith("```"))
                {
                    inCode = !inCode;
                    continue;
                }
                if (inCode)
                {
                    var csegs = new List<Seg>();
                    csegs.Add(new Seg { Text = para.Length == 0 ? " " : para, Code = true });
                    int before = lines.Count;
                    WrapSegs(csegs, Theme.MonoFont, Math.Max(200, width - 48), lines, false, true);
                    for (int i = before; i < lines.Count; i++) { lines[i].InCodeBlock = true; lines[i].Indent = 16; }
                    continue;
                }
                string marker = null;
                var body = para;
                if (body.StartsWith("- ") || body.StartsWith("* "))
                {
                    marker = "\u2022";
                    body = body.Substring(2);
                }
                else
                {
                    int dot = body.IndexOf(". ");
                    if (dot > 0 && dot <= 3)
                    {
                        string num = body.Substring(0, dot);
                        bool digits = true;
                        foreach (var ch in num) if (ch < '0' || ch > '9') { digits = false; break; }
                        if (digits) { marker = num + "."; body = body.Substring(dot + 2); }
                    }
                }
                var cont = new List<LayoutLine>();
                var segs2 = Parse(body);
                WrapSegs(segs2, f, width - (marker != null ? 22 : 0), cont, body.Length == 0);
                if (marker != null && cont.Count > 0) cont[0].ListMarker = marker;
                foreach (var cl in cont) cl.Indent = marker != null ? 22 : 0;
                lines.AddRange(cont);
            }
            return lines;
        }

        private List<LayoutLine> WrapPlain(string text, Font f, int width)
        {
            var lines = new List<LayoutLine>();
            foreach (var para in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var segs = new List<Seg>();
                if (para.Length > 0) segs.Add(new Seg { Text = para });
                WrapSegs(segs, f, width, lines, para.Length == 0);
            }
            return lines;
        }

        private List<LayoutLine> WrapMono(string text, Font f, int width)
        {
            var lines = new List<LayoutLine>();
            foreach (var para in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var segs = new List<Seg>();
                if (para.Length > 0) segs.Add(new Seg { Text = para, Code = true });
                WrapSegs(segs, f, width, lines, para.Length == 0, true);
            }
            return lines;
        }

        private void WrapSegs(List<Seg> segs, Font f, int width, List<LayoutLine> lines, bool blank, bool mono = false)
        {
            if (blank) { lines.Add(new LayoutLine { Height = mono ? 8 : 10 }); return; }
            var cur = new LayoutLine { Height = mono ? 16 : 20 };
            float x = 0;
            var font = mono ? Theme.MonoFont : f;
            foreach (var seg in segs)
            {
                var sfont = seg.Bold ? Theme.BaseFontMedium : font;
                int start = 0;
                while (start < seg.Text.Length)
                {
                    int remain = seg.Text.Length - start;
                    // fit as many chars as possible
                    int lo = 1, hi = remain, fit = 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        var sz = TextRenderer.MeasureText(seg.Text.Substring(start, mid), sfont,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                        if (sz.Width <= width - x) { fit = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }
                    bool breakLine = fit < remain;
                    int take = breakLine ? fit : remain;
                    var w = TextRenderer.MeasureText(seg.Text.Substring(start, take), sfont,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    cur.Parts.Add(new SegPos { Seg = seg, Start = start, Len = take, X = x, W = w });
                    x += w;
                    start += take;
                    if (breakLine || start >= seg.Text.Length)
                    {
                        if (breakLine)
                        {
                            lines.Add(cur);
                            cur = new LayoutLine { Height = mono ? 16 : 20 };
                            x = 0;
                        }
                    }
                }
            }
            lines.Add(cur);
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            int w = ContentWidth();
            int x0 = ContentX();
            float y = 8 - _scroll;
            int rowIdx = 0;
            var rowRects = _rowRects;
            _rowRects.Clear();
            foreach (var r in _rows)
            {
                int top = (int)y;
                PaintRow(g, r, x0, w, y, rowIdx == _hoverRow, rowIdx);
                _rowRects.Add(new Rectangle(x0, top, w, r.Height));
                y += r.Height + 20; // gap-5
                rowIdx++;
            }
            // scrollbar (thin, subtle)
            if (_layoutH > Height)
            {
                float trackH = Height - 8;
                float thumbH = Math.Max(28, trackH * Height / _layoutH);
                float maxScroll = _layoutH - Height;
                float ty = 4 + (trackH - thumbH) * (_scroll / maxScroll);
                using (var p = Theme.Rounded(new Rectangle(Width - 6, (int)ty, 4, (int)thumbH), 2))
                using (var b = new SolidBrush(Color.FromArgb(40, 13, 13, 13)))
                    g.FillPath(b, p);
            }
        }

        private List<Rectangle> _rowRects = new List<Rectangle>();

        // ---- owner-draw text selection (assistant rows) ----
        private int[] _selA;              // {rowIdx, lineIdx, charOff}
        private int[] _selB;
        private bool _selecting;

        private static int Cmp(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++)
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }

        public bool HasSelection
        { get { return _selA != null && _selB != null && Cmp(_selA, _selB) != 0; } }

        public void ClearSelection()
        {
            if (_selA != null || _selB != null) { _selA = null; _selB = null; Invalidate(); }
        }

        private int[] FindPos(float x, float y)
        {
            for (int i = 0; i < _rowRects.Count; i++)
            {
                if (_rows[i].Kind != RowKind.Assistant) continue;
                var rr = _rowRects[i];
                if (y >= rr.Y && y <= rr.Bottom) return FindPosInRow(i, x, y);
            }
            return null;
        }

        private int[] FindPosInRow(int rowIdx, float x, float y)
        {
            var r = _rows[rowIdx];
            int x0 = ContentX();
            float localY = y + _scroll - _rowRects[rowIdx].Y;
            float cy = 0;
            for (int li = 0; li < r.Lines.Count; li++)
            {
                var line = r.Lines[li];
                if (localY < cy + line.Height || li == r.Lines.Count - 1)
                {
                    if (line.Parts.Count == 0) return new[] { rowIdx, li, 0 };
                    float relX = x - x0 - line.Indent;
                    foreach (var p in line.Parts)
                    {
                        if (relX <= p.X + p.W || p == line.Parts[line.Parts.Count - 1])
                        {
                            float frac = p.W > 0 ? (relX - p.X) / p.W : 1f;
                            frac = Math.Max(0f, Math.Min(1f, frac));
                            int off = (int)Math.Round(frac * p.Len);
                            return new[] { rowIdx, li, p.Start + Math.Max(0, Math.Min(p.Len, off)) };
                        }
                    }
                    var lastP = line.Parts[line.Parts.Count - 1];
                    return new[] { rowIdx, li, lastP.Start + lastP.Len };
                }
                cy += line.Height;
            }
            return new[] { rowIdx, 0, 0 };
        }

        private float CharX(LayoutLine line, int chOff)
        {
            foreach (var p in line.Parts)
            {
                if (chOff <= p.Start) return p.X;
                if (chOff >= p.Start + p.Len) continue;
                float frac = p.Len > 0 ? (float)(chOff - p.Start) / p.Len : 1f;
                return p.X + frac * p.W;
            }
            var lp = line.Parts.Count > 0 ? line.Parts[line.Parts.Count - 1] : null;
            return lp != null ? lp.X + lp.W : 0;
        }

        private static float LineExtent(LayoutLine line)
        {
            float maxw = 40;
            foreach (var p in line.Parts) maxw = Math.Max(maxw, p.X + p.W);
            return maxw;
        }

        private string ExtractSelection()
        {
            if (!HasSelection) return "";
            var a = NormSel(_selA, _selB);
            var b = Cmp(a, _selB) == 0 ? _selA : _selB;
            var sb = new System.Text.StringBuilder();
            for (int row = a[0]; row <= b[0]; row++)
            {
                var r = _rows[row];
                int li0 = row == a[0] ? a[1] : 0;
                int li1 = row == b[0] ? b[1] : r.Lines.Count - 1;
                for (int li = li0; li <= li1; li++)
                {
                    if (li >= r.Lines.Count) break;
                    var line = r.Lines[li];
                    if (line.Parts.Count == 0) { if (li < li1) sb.Append('\n'); continue; }
                    int cs = int.MinValue, ce = int.MaxValue;
                    if (row == a[0] && li == a[1]) cs = a[2];
                    if (row == b[0] && li == b[1]) ce = b[2];
                    foreach (var pt in line.Parts)
                    {
                        int ps = pt.Start, pe = pt.Start + pt.Len;
                        int s0 = Math.Max(ps, cs), e0 = Math.Min(pe, ce);
                        if (e0 > s0) sb.Append(pt.Seg.Text.Substring(s0, e0 - s0));
                    }
                    if (li < li1) sb.Append('\n');
                }
                if (row < b[0]) sb.Append('\n');
            }
            return sb.ToString();
        }

        private static int[] NormSel(int[] a, int[] b)
        {
            return Cmp(a, b) < 0 ? (int[])a.Clone() : (int[])b.Clone();
        }

        private void DrawSelection(Graphics g, int rowIdx, float x0, float y)
        {
            if (!HasSelection) return;
            var a = NormSel(_selA, _selB);
            var b = Cmp(a, _selB) == 0 ? _selA : _selB;
            if (rowIdx < a[0] || rowIdx > b[0]) return;
            var r = _rows[rowIdx];
            float cy = y;
            for (int li = 0; li < r.Lines.Count; li++)
            {
                var line = r.Lines[li];
                bool first = rowIdx == a[0] && li == a[1];
                bool last = rowIdx == b[0] && li == b[1];
                bool middle = SelInRange(a, b, rowIdx, li) && !first && !last;
                if (!first && !last && !middle) { cy += line.Height; continue; }
                float sx = x0 + line.Indent, sw;
                if (first && last)
                {
                    sx = x0 + line.Indent + CharX(line, a[2]);
                    sw = (x0 + line.Indent + CharX(line, b[2])) - sx;
                }
                else if (first)
                {
                    sx = x0 + line.Indent + CharX(line, a[2]);
                    sw = Math.Max(40, LineExtent(line)) - (sx - x0 - line.Indent);
                }
                else if (last)
                {
                    sw = x0 + line.Indent + CharX(line, b[2]) - sx;
                }
                else
                {
                    sw = Math.Max(40, LineExtent(line));
                }
                using (var br = new SolidBrush(Color.FromArgb(70, 25, 118, 210)))
                    g.FillRectangle(br, sx, cy, sw, line.Height);
                cy += line.Height;
            }
        }

        private static bool SelInRange(int[] a, int[] b, int row, int li)
        {
            if (row > a[0] && row < b[0]) return true;
            if (row == a[0] && row == b[0]) return li >= a[1] && li <= b[1];
            if (row == a[0]) return li >= a[1];
            if (row == b[0]) return li <= b[1];
            return false;
        }

        public void CopySelection()
        {
            var t = ExtractSelection();
            if (t.Length > 0) try { Clipboard.SetText(t); } catch { }
        }

        private void PaintRow(Graphics g, ChatRow r, int x0, int w, float y, bool hover, int rowIdx0)
        {
            switch (r.Kind)
            {
                case RowKind.Info:
                    DrawLines(g, r.Lines, x0 + 16, y + 4, Theme.TextSubtle, false);
                    break;

                case RowKind.WorkStatus:
                    using (var b = new SolidBrush(Theme.TextSubtle))
                        g.DrawString((r.Running ? "工作中" : "工作了") + (string.IsNullOrEmpty(r.Duration) ? "" : " " + r.Duration),
                            Theme.BaseFont, b, x0, y);
                    using (var p = new Pen(Color.FromArgb(13, Theme.Border)))
                        g.DrawLine(p, x0, y + 26, x0 + w, y + 26);
                    break;

                case RowKind.User:
                    {
                        int bw = Math.Min(576, w);
                        // bubble width = widest line + padding, right aligned
                        float maxLine = 0;
                        foreach (var l in r.Lines) { float lw = 0; foreach (var p in l.Parts) lw += p.W; maxLine = Math.Max(maxLine, lw); }
                        bw = (int)Math.Min(576, Math.Max(80, maxLine + 32));
                        var rect = new Rectangle(x0 + w - bw, (int)y, bw, r.Height);
                        using (var path = Theme.Rounded(rect, 12, 4, 12, 12))
                        {
                            using (var b = new SolidBrush(Color.FromArgb(13, 13, 13, 13)))
                                g.FillPath(b, path);
                            using (var p = new Pen(Theme.Border))
                                g.DrawPath(p, path);
                        }
                        DrawLines(g, r.Lines, rect.X + 16, y + 12, Theme.TextPrimary, false);
                        break;
                    }

                case RowKind.Assistant:
                    DrawSelection(g, rowIdx0, x0, y);
                    DrawLines(g, r.Lines, x0, y, Theme.TextPrimary, true);
                    if (hover)
                    {
                        // copy button top-right of the message
                        r.CopyRect = new Rectangle(x0 + w - 76, Math.Max(0, (int)y - 2), 76, 22);
                        bool copied = r.Decided;   // reuse Decided as "just copied" flash
                        using (var path = Theme.Rounded(r.CopyRect, 6))
                        {
                            using (var b = new SolidBrush(Color.FromArgb(18, 13, 13, 13)))
                                g.FillPath(b, path);
                            using (var p2 = new Pen(Theme.Border))
                                g.DrawPath(p2, path);
                        }
                        Icons.Draw(g, copied ? "Check" : "Copy",
                            new Rectangle(r.CopyRect.X + 6, r.CopyRect.Y + 3, 16, 16),
                            copied ? Theme.Success : Theme.TextSubtle);
                        TextRenderer.DrawText(g, copied ? "已复制" : "复制", Theme.SmallFont,
                            new Point(r.CopyRect.X + 26, r.CopyRect.Y + 4),
                            copied ? Theme.Success : Theme.TextSubtle, TextFormatFlags.NoPadding);
                    }
                    else r.CopyRect = Rectangle.Empty;
                    break;

                case RowKind.Thinking:
                    {
                        Icons.Draw(g, "Brain", new Rectangle(x0, (int)y + 2, 16, 16), Theme.TextSubtlest);
                        int tx = x0 + 16 + 8;
                        using (var b = new SolidBrush(Theme.TextSubtlest))
                        {
                            var flags = TextFormatFlags.NoPadding;
                            var sz1 = TextRenderer.MeasureText("思考", Theme.BaseFontMedium, Size.Empty, flags);
                            TextRenderer.DrawText(g, "思考", Theme.BaseFontMedium, new Point(tx, (int)y), Theme.TextSubtlest, flags);
                            TextRenderer.DrawText(g, "·", Theme.BaseFont, new Point(tx + sz1.Width + 8, (int)y), Theme.TextSubtlest, flags);
                            TextRenderer.DrawText(g, r.Running ? "正在思考" : (r.Duration.Length > 0 ? "持续了 " + r.Duration : ""),
                                Theme.BaseFont, new Point(tx + sz1.Width + 8 + 10 + 8, (int)y), Theme.TextSubtlest, flags);
                        }
                        if (r.Expanded)
                        {
                            DrawLines(g, r.Lines, x0 + 16, y + r.HeaderH + 12, Theme.TextSubtlest, false);
                        }
                        break;
                    }

                case RowKind.Approval:
                    {
                        var card = new Rectangle(x0, (int)y, w, r.Height);
                        using (var path = Theme.Rounded(card, 12))
                        {
                            using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                            using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                        }
                        string kind;
                        if (!KindLabel.TryGetValue(r.ToolName, out kind)) kind = r.ToolName;
                        string icon;
                        if (!ToolIcon.TryGetValue(r.ToolName, out icon)) icon = "Wrench";
                        Icons.Draw(g, icon, new Rectangle(x0 + 12, (int)y + 14, 16, 16), Theme.TextSubtlest);
                        TextRenderer.DrawText(g, kind, Theme.BaseFontMedium, new Point(x0 + 36, (int)y + 12),
                            Theme.TextSubtlest, TextFormatFlags.NoPadding);
                        var avail = x0 + w - x0 - 36 - 12;
                        string clipped = ClipToWidth(ArgsPreview(r), Theme.BaseFont, avail);
                        TextRenderer.DrawText(g, clipped, Theme.BaseFont, new Point(x0 + 36 + TextRenderer.MeasureText(kind, Theme.BaseFontMedium, Size.Empty, TextFormatFlags.NoPadding).Width + 8, (int)y + 12),
                            Theme.TextSubtle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        if (!r.Decided)
                        {
                            var ar = new Rectangle(card.X + r.AllowRect.X, (int)y + r.AllowRect.Y, r.AllowRect.Width, r.AllowRect.Height);
                            var dr = new Rectangle(card.X + r.DenyRect.X, (int)y + r.DenyRect.Y, r.DenyRect.Width, r.DenyRect.Height);
                            using (var path = Theme.Rounded(ar, 8))
                            using (var b = new SolidBrush(Theme.Brand))
                                g.FillPath(b, path);
                            TextRenderer.DrawText(g, "允许执行", Theme.BaseFont, ar, Theme.TextInverse,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                            using (var path = Theme.Rounded(dr, 8))
                            {
                                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                            }
                            TextRenderer.DrawText(g, "拒绝", Theme.BaseFont, dr, Theme.TextPrimary,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        }
                        else
                        {
                            if (r.Allowed)
                            {
                                Icons.Draw(g, "CheckCircle2", new Rectangle(x0 + 12, (int)y + 42, 16, 16), Theme.Success);
                                TextRenderer.DrawText(g, "已允许执行", Theme.BaseFont, new Point(x0 + 36, (int)y + 40), Theme.Success, TextFormatFlags.NoPadding);
                            }
                            else
                            {
                                Icons.Draw(g, "X", new Rectangle(x0 + 12, (int)y + 42, 16, 16), Theme.Destructive);
                                TextRenderer.DrawText(g, "已拒绝", Theme.BaseFont, new Point(x0 + 36, (int)y + 40), Theme.Destructive, TextFormatFlags.NoPadding);
                            }
                        }
                        break;
                    }

                case RowKind.Tool:
                    {
                        int iy = (int)y + 2;
                        Icons.Draw(g, IconFor(r), new Rectangle(x0, iy, 16, 16), Theme.TextSubtlest);
                        int tx = x0 + 16 + 8;
                        var flags = TextFormatFlags.NoPadding;
                        using (var b = new SolidBrush(Theme.TextSubtlest))
                        {
                            var kind = KindFor(r);
                            TextRenderer.DrawText(g, kind, Theme.BaseFontMedium, new Point(tx, (int)y), Theme.TextSubtlest, flags);
                            var kw = TextRenderer.MeasureText(kind, Theme.BaseFontMedium, Size.Empty, flags).Width;
                            int cx = tx + kw + 8;
                            string preview = ArgsPreview(r);
                            if (preview.Length > 0)
                            {
                                TextRenderer.DrawText(g, "·", Theme.BaseFont, new Point(cx, (int)y), Theme.TextSubtlest, flags);
                                cx += 10 + 8;
                                var avail = x0 + w - cx - 40;
                                string clipped = ClipToWidth(preview, Theme.BaseFont, avail);
                                TextRenderer.DrawText(g, clipped, Theme.BaseFont, new Point(cx, (int)y), Theme.TextSubtlest, flags);
                                cx += TextRenderer.MeasureText(clipped, Theme.BaseFont, Size.Empty, flags).Width + 8;
                            }
                            if (r.AddLines >= 0)
                            {
                                string s = "+" + r.AddLines;
                                TextRenderer.DrawText(g, s, Theme.MonoFont, new Point(cx, (int)y), Theme.DiffAdded, flags);
                                cx += TextRenderer.MeasureText(s, Theme.MonoFont, Size.Empty, flags).Width + 4;
                            }
                            if (r.DelLines >= 0)
                            {
                                string s = "-" + r.DelLines;
                                TextRenderer.DrawText(g, s, Theme.MonoFont, new Point(cx, (int)y), Theme.DiffRemoved, flags);
                            }
                        }
                        if (!r.Running)
                            Icons.Draw(g, "ChevronRight", new Rectangle(x0 + w - 16, iy, 16, 16), Theme.TextSubtlest);
                        if (r.Expanded)
                        {
                            var card = new Rectangle(x0 + 16, (int)y + r.BodyTop, w - 32, r.Height - r.BodyTop - 2);
                            using (var path = Theme.Rounded(card, 12))
                            {
                                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                            }
                            float ty = card.Y + 12;
                            var argText = r.DetailLabel.Length > 0 ? r.DetailLabel + "  " + ArgsPreview(r) : ArgsPreview(r);
                            var argLines = WrapMono(argText, Theme.MonoFont, card.Width - 24);
                            DrawMonoLines(g, argLines, card.X + 12, ref ty, Theme.TextSubtle, card.Bottom - 4);
                            if (r.DetailLines.Count > 0 && !string.IsNullOrEmpty(r.Result))
                            {
                                ty += 6;
                                DrawMonoLines(g, r.DetailLines, card.X + 12, ref ty, Theme.TextSubtle, Math.Min(card.Bottom - 4, ty + 300));
                            }
                        }
                        break;
                    }
            }
        }

        private static string StripMarkdown(string md)
        {
            if (md == null) return "";
            var sb = new System.Text.StringBuilder();
            bool inCode = false;
            foreach (var line in md.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.TrimEnd();
                if (t.StartsWith("```")) { inCode = !inCode; continue; }
                if (inCode) { sb.Append(t).Append('\n'); continue; }
                var hm = System.Text.RegularExpressions.Regex.Match(t, "^#{1,4}\\s+(.*)$");
                if (hm.Success) { sb.Append(hm.Groups[1].Value).Append('\n'); continue; }
                if (t.StartsWith("> ")) { sb.Append(t.Substring(2)).Append('\n'); continue; }
                if (t.StartsWith("- ") || t.StartsWith("* ")) t = "• " + t.Substring(2);
                else
                {
                    var om = System.Text.RegularExpressions.Regex.Match(t, "^(\\d+)\\.\\s+(.*)$");
                    if (om.Success) t = om.Groups[1].Value + ". " + om.Groups[2].Value;
                }
                t = t.Replace("**", "").Replace("`", "");
                sb.Append(t).Append('\n');
            }
            return sb.ToString();
        }

        private string ClipToWidth(string text, Font f, int width)
        {
            if (width <= 0) return "";
            if (TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding).Width <= width) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (TextRenderer.MeasureText(text.Substring(0, mid) + "…", f, Size.Empty, TextFormatFlags.NoPadding).Width <= width) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo) + "…";
        }

        private void DrawLines(Graphics g, List<LayoutLine> lines, float x, float y, Color color, bool markdown)
        {
            float cy = y;
            var flags = TextFormatFlags.NoPadding;

            if (markdown)
            {
                int i = 0;
                while (i < lines.Count)
                {
                    if (!lines[i].InCodeBlock) { i++; continue; }
                    int j = i;
                    while (j < lines.Count && lines[j].InCodeBlock) j++;
                    float by = y;
                    for (int k = 0; k < i; k++) by += lines[k].Height;
                    float bh = 0;
                    for (int k = i; k < j; k++) bh += lines[k].Height;
                    float maxw = 200;
                    for (int k = i; k < j; k++)
                    {
                        float lw = 24;
                        foreach (var pp in lines[k].Parts) lw += pp.W;
                        maxw = Math.Max(maxw, lw);
                    }
                    var rect = new Rectangle((int)x + 4, (int)by - 2, (int)maxw + 8, (int)bh + 4);
                    using (var path = Theme.Rounded(rect, 8))
                    using (var b = new SolidBrush(Color.FromArgb(246, 246, 246)))
                        g.FillPath(b, path);
                    i = j;
                }
            }

            foreach (var line in lines)
            {
                if (line.ListMarker != null)
                {
                    TextRenderer.DrawText(g, line.ListMarker, Theme.BaseFont,
                        new Point((int)x, (int)cy), Theme.TextSubtle, flags);
                }
                float indent = (line.ListMarker != null ? 22 : 0) + (line.InCodeBlock ? 16 : 0);
                if (line.Parts.Count == 0) { cy += line.Height; continue; }
                foreach (var p in line.Parts)
                {
                    string t = p.Seg.Text.Substring(p.Start, p.Len);
                    if (p.Seg.Code && !line.InCodeBlock)
                    {
                        var bgRect = new RectangleF(x + indent + p.X - 3, cy + 2, p.W + 6, 17);
                        using (var path = Theme.Rounded(new Rectangle((int)bgRect.X, (int)bgRect.Y, (int)bgRect.Width, (int)bgRect.Height), 4))
                        using (var b = new SolidBrush(Theme.InlineCodeBg))
                            g.FillPath(b, path);
                        TextRenderer.DrawText(g, t, Theme.MonoFont, new Point((int)(x + indent + p.X), (int)cy + 2), color, flags);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, t, p.Seg.Bold ? Theme.BaseFontMedium : (p.Seg.Code ? Theme.MonoFont : Theme.BaseFont),
                            new Point((int)(x + indent + p.X), (int)cy), color, flags);
                    }
                }
                cy += line.Height;
            }
        }

        private void DrawMonoLines(Graphics g, List<LayoutLine> lines, float x, ref float y, Color color, float clipBottom)
        {
            var flags = TextFormatFlags.NoPadding;
            foreach (var line in lines)
            {
                if (y > clipBottom) { g.DrawString("…", Theme.BaseFont, SystemBrushes.ControlDarkDark, x, clipBottom - 16); break; }
                if (line.Parts.Count == 0) { y += line.Height; continue; }
                foreach (var p in line.Parts)
                    TextRenderer.DrawText(g, p.Seg.Text.Substring(p.Start, p.Len), Theme.MonoFont,
                        new Point((int)(x + p.X), (int)y), color, flags);
                y += line.Height;
            }
        }

        // ---------- interaction ----------

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta > 0) _stickBottom = false;   // user scrolled up: stop following
            _scroll = Math.Max(0, Math.Min(Math.Max(0, _layoutH - Height), _scroll - e.Delta * 0.6f));
            Invalidate();
        }

        private int Hit(float x, float y)
        {
            for (int i = 0; i < _rowRects.Count; i++)
                if (_rowRects[i].Contains((int)x, (int)y)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragScroll)
            {
                _scroll = Math.Max(0, Math.Min(Math.Max(0, _layoutH - Height), _dragScrollStart - (e.Y - _dragY) * (_layoutH / (float)Math.Max(1, Height))));
                Invalidate();
                return;
            }
            if (_selecting)
            {
                var p = FindPos(e.X, e.Y);
                if (p != null) { _selB = p; Invalidate(); }
                return;
            }
            int h = Hit(e.X, e.Y + (int)_scroll);
            if (h != _hoverRow) { _hoverRow = h; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.X > Width - 12 && _layoutH > Height)
            {
                _dragScroll = true; _dragY = e.Y; _dragScrollStart = _scroll;
                return;
            }
            // start drag-select on assistant text
            var selPos = FindPos(e.X, e.Y);
            if (selPos != null && _rows[selPos[0]].Kind == RowKind.Assistant)
            {
                _selA = selPos; _selB = selPos; _selecting = true;
                Invalidate();
                return;
            }
            ClearSelection();

            int h = Hit(e.X, e.Y + (int)_scroll);
            if (h >= 0)
            {
                var r = _rows[h];
                if (r.CopyRect.Contains(e.X - _rowRects[h].X, e.Y + (int)_scroll - _rowRects[h].Y))
                {
                    try { Clipboard.SetText(StripMarkdown(r.Text)); } catch { }
                    r.Decided = true;   // flash 已复制
                    Invalidate();
                    var tmr = new Timer { Interval = 1200 };
                    tmr.Tick += (s2, e2) => { tmr.Stop(); tmr.Dispose(); r.Decided = false; Invalidate(); };
                    tmr.Start();
                    return;
                }
                if (r.Kind == RowKind.Approval && !r.Decided && ApprovalDecided != null)
                {
                    int lx = e.X - _rowRects[h].X;
                    int ly = e.Y + (int)_scroll - _rowRects[h].Y;
                    if (r.AllowRect.Contains(lx, ly))
                    {
                        r.Decided = true;
                        r.Allowed = true;
                        RelayoutRow(r);
                        Invalidate();
                        ApprovalDecided(r, true);
                        return;
                    }
                    if (r.DenyRect.Contains(lx, ly))
                    {
                        r.Decided = true;
                        r.Allowed = false;
                        RelayoutRow(r);
                        Invalidate();
                        ApprovalDecided(r, false);
                        return;
                    }
                    return;
                }
                if (r.Kind == RowKind.Thinking || r.Kind == RowKind.Tool)
                {
                    r.Expanded = !r.Expanded;
                    RelayoutRow(r);
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragScroll = false;
            if (_selecting)
            {
                var p = FindPos(e.X, e.Y);
                if (p != null) _selB = p;
                _selecting = false;
                if (!HasSelection) { _selA = null; _selB = null; }
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }
    }

}
