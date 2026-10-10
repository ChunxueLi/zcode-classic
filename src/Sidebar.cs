// ZCode7 - workspace sidebar (264px, zai-light, owner-drawn; all rows functional)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZCode7
{
    public class SessionItem
    {
        public string Id;
        public string Title;
        public string Time;
        public bool Selected;
    }

    public class Sidebar : Panel
    {
        public event Action NewTask;
        public event Action<string> OpenSession;
        public event Action<string> RenameSession;
        public event Action<string> DeleteSession;
        public event Action ShowSkills;
        public event Action Settings;

        public List<SessionItem> Items = new List<SessionItem>();
        public string UsageText = "";
        public Color UsageColor = Theme.TextSubtle;
        private string _filter = "";
        private bool _searching;
        private readonly TextBox _searchBox;
        private int _hover = -2;          // -1 none, 0..2 top rows, -2 list handled separately, 100 footer settings
        private int _hoverList = -1;
        private float _scroll;
        private float _contentH = 1;
        private bool _drag;
        private int _dragY;
        private float _dragStart;
        private readonly List<Rectangle> _itemRects = new List<Rectangle>();
        private DateTime _lastClick;
        private int _lastIndex = -1;

        private static readonly string[] TopIcons = { "MessageCirclePlus", "Search", "Blocks" };
        private static readonly string[] TopLabels = { "新建任务", "搜索", "技能" };
        private static readonly string[] TopKeys = { "Ctrl+N", "Ctrl+K", "" };

        public Sidebar()
        {
            DoubleBuffered = true;
            BackColor = Theme.Sidebar;
            ResizeRedraw = true;

            _searchBox = new TextBox();
            _searchBox.BorderStyle = BorderStyle.FixedSingle;
            _searchBox.BackColor = Theme.Input;
            _searchBox.ForeColor = Theme.TextPrimary;
            _searchBox.Font = Theme.BaseFont;
            _searchBox.Visible = false;
            Controls.Add(_searchBox);
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Enter && _searchBox.Text.Trim().Length == 0))
                {
                    EndSearch();
                    e.SuppressKeyPress = true;
                }
            };
            _searchBox.TextChanged += (s, e) => { _filter = _searchBox.Text.Trim(); _scroll = 0; Invalidate(); };
        }

        private int TopRows() { return _searching ? 0 : 3; }
        private int RowAreaTop() { return 48; }
        private int ListTop() { return 48 + (_searching ? 38 : 3 * 36 + 8); }

        private IEnumerable<SessionItem> VisibleItems()
        {
            if (_filter.Length == 0) return Items;
            return Items.FindAll(x => x.Title.ToLowerInvariant().Contains(_filter.ToLowerInvariant()));
        }

        private void StartSearch()
        {
            _searching = true;
            _searchBox.Location = new Point(8, RowAreaTop() + 4);
            _searchBox.Size = new Size(Width - 16, 28);
            _searchBox.Visible = true;
            _searchBox.Focus();
            Invalidate();
        }

        private void EndSearch()
        {
            _searching = false;
            _filter = "";
            _searchBox.Text = "";
            _searchBox.Visible = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Sidebar);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // logo strip
            var logoRect = new Rectangle(12, 14, 20, 20);
            try
            {
                using (var img = Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + "zlogo.png"))
                    g.DrawImage(img, logoRect);
            }
            catch
            {
                using (var b = new SolidBrush(Color.FromArgb(13, 13, 13)))
                using (var path = Theme.Rounded(logoRect, 5))
                    g.FillPath(b, path);
                using (var f = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                {
                    var sz = g.MeasureString("Z", f);
                    g.DrawString("Z", f, b, logoRect.X + (20 - sz.Width) / 2f, logoRect.Y + 3);
                }
            }

            // top rows (hidden while searching)
            int y = RowAreaTop();
            for (int i = 0; i < TopRows(); i++)
            {
                var rect = new Rectangle(8, y, Width - 16, 32);
                using (var path = Theme.Rounded(rect, 8))
                    if (_hover == i)
                        using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
                Icons.Draw(g, TopIcons[i], new Rectangle(rect.X + 10, y + 8, 16, 16), Theme.TextPrimary);
                TextRenderer.DrawText(g, TopLabels[i], Theme.BaseFont,
                    new Point(rect.X + 34, y + 8), Theme.TextPrimary, TextFormatFlags.NoPadding);
                if (TopKeys[i].Length > 0)
                {
                    var kw = TextRenderer.MeasureText(TopKeys[i], Theme.XsFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, TopKeys[i], Theme.XsFont,
                        new Point(rect.Right - kw - 10, y + 11), Theme.TextSubtlest, TextFormatFlags.NoPadding);
                }
                y += 32 + 4;
            }

            // session list
            _itemRects.Clear();
            float ly = ListTop() - _scroll;
            int visIdx = 0;
            foreach (var it in VisibleItems())
            {
                int h = 32;
                _itemRects.Add(new Rectangle(8, (int)ly, Width - 16, h));
                if (ly + h < ListTop() - 40 || ly > Height) { ly += h + 2; visIdx++; continue; }
                var rect = new Rectangle(8, (int)ly, Width - 16, h);
                if (it.Selected)
                    using (var path = Theme.Rounded(rect, 8))
                    using (var b = new SolidBrush(Theme.Selected))
                        g.FillPath(b, path);
                else if (_hoverList == visIdx)
                    using (var path = Theme.Rounded(rect, 8))
                    using (var b = new SolidBrush(Theme.Hover))
                        g.FillPath(b, path);
                Icons.Draw(g, "MessageCirclePlus", new Rectangle(rect.X + 10, (int)ly + 8, 16, 16), Theme.TextSubtlest);
                int timeW = 0;
                if (it.Time.Length > 0)
                    timeW = TextRenderer.MeasureText(it.Time, Theme.SmallFont, Size.Empty, TextFormatFlags.NoPadding).Width + 14;
                int titleMax = rect.Width - 34 - timeW - 8;
                TextRenderer.DrawText(g, it.Title, Theme.BaseFont,
                    new Rectangle(rect.X + 34, (int)ly + 8, Math.Max(40, titleMax), 18),
                    it.Selected ? Theme.TextPrimary : Theme.TextSubtle,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (it.Time.Length > 0)
                {
                    TextRenderer.DrawText(g, it.Time, Theme.SmallFont,
                        new Point(rect.Right - timeW + 2, (int)ly + 10), Theme.TextSubtlest, TextFormatFlags.NoPadding);
                }
                ly += h + 2;
                visIdx++;
            }
            _contentH = Math.Max(1, (float)ly + 8);

            // quota usage line (fetched from Zhipu monitor API)
            if (UsageText.Length > 0)
            {
                TextRenderer.DrawText(g, "额度  " + UsageText, Theme.SmallFont,
                    new Point(14, Height - 80), UsageColor, TextFormatFlags.NoPadding);
            }

            // footer: account row
            using (var p = new Pen(Theme.Border))
                g.DrawLine(p, 0, Height - 57, Width, Height - 57);
            var av = new Rectangle(12, Height - 44, 24, 24);
            using (var path = Theme.Rounded(av, 12))
            using (var b = new SolidBrush(Color.FromArgb(13, 13, 13)))
                g.FillPath(b, path);
            using (var b = new SolidBrush(Color.White))
            {
                var f = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString("Z", f);
                g.DrawString("Z", f, b, av.X + (24 - sz.Width) / 2f, av.Y + 5);
            }
            TextRenderer.DrawText(g, "Administrator", Theme.BaseFont,
                new Point(44, Height - 38), Theme.TextPrimary, TextFormatFlags.NoPadding);
            bool setHov = _hover == 100;
            if (setHov)
                using (var path = Theme.Rounded(new Rectangle(Width - 40, Height - 44, 28, 28), 8))
                using (var b = new SolidBrush(Theme.Hover))
                    g.FillPath(b, path);
            Icons.Draw(g, "Settings", new Rectangle(Width - 34, Height - 38, 16, 16), Theme.TextSubtle);

            // scrollbar
            float viewH = Height - ListTop() - 60;
            if (_contentH > viewH && viewH > 40)
            {
                float thumbH = Math.Max(28, viewH * viewH / _contentH);
                float maxS = _contentH - viewH;
                float ty = ListTop() + (viewH - thumbH) * (_scroll / Math.Max(1, maxS));
                using (var path = Theme.Rounded(new Rectangle(Width - 5, (int)ty, 3, (int)thumbH), 2))
                using (var b = new SolidBrush(Color.FromArgb(40, 13, 13, 13)))
                    g.FillPath(b, path);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float viewH = Height - ListTop() - 60;
            float max = Math.Max(0, _contentH - viewH);
            _scroll = Math.Max(0, Math.Min(max, _scroll - e.Delta * 0.6f));
            Invalidate();
        }

        private int HitTop(Point p)
        {
            if (_searching) return -1;
            int y = RowAreaTop();
            for (int i = 0; i < 3; i++)
            {
                if (p.Y >= y && p.Y < y + 32) return i;
                y += 36;
            }
            return -1;
        }

        private int HitListItem(Point p)
        {
            for (int i = 0; i < _itemRects.Count; i++)
                if (_itemRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag)
            {
                float viewH = Height - ListTop() - 60;
                float max = Math.Max(0, _contentH - viewH);
                _scroll = Math.Max(0, Math.Min(max, _dragStart - (e.Y - _dragY) * (_contentH / Math.Max(1, viewH))));
                Invalidate();
                return;
            }
            int t = HitTop(e.Location);
            int h;
            if (t >= 0) h = t;
            else if (e.X >= Width - 44 && e.Y >= Height - 50) h = 100;
            else { h = -2; _hoverList = HitListItem(e.Location); }
            if (h == -2 && _hoverList >= 0) h = -3; // list hover keeps repaint
            if (h != _hover)
            {
                _hover = h;
                Cursor = h >= -1 && h != -2 ? Cursors.Hand : (h == -3 ? Cursors.Hand : Cursors.Default);
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            // scrollbar drag
            float viewH = Height - ListTop() - 60;
            if (_contentH > viewH && e.X > Width - 10)
            {
                _drag = true; _dragY = e.Y; _dragStart = _scroll;
                return;
            }

            int t = HitTop(e.Location);
            if (t >= 0)
            {
                if (t == 0 && NewTask != null) NewTask();
                else if (t == 1) StartSearch();
                else if (t == 2 && ShowSkills != null) ShowSkills();
                return;
            }
            if (e.X >= Width - 44 && e.Y >= Height - 50)
            {
                if (Settings != null) Settings();
                return;
            }
            int li = HitListItem(e.Location);
            if (li >= 0)
            {
                var vis = new List<SessionItem>(VisibleItems());
                if (li < vis.Count)
                {
                    var it = vis[li];
                    if (!it.IsProjectGroupSafe() && OpenSession != null) OpenSession(it.Id);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false;
            if (e.Button == MouseButtons.Right)
            {
                int li = HitListItem(e.Location);
                if (li >= 0)
                {
                    var vis = new List<SessionItem>(VisibleItems());
                    if (li < vis.Count)
                    {
                        string id = vis[li].Id;
                        var items = new List<Popover.Item>
                        {
                            new Popover.Item
                            {
                                Text = "重命名", Icon = "Pencil",
                                OnPick = () => { if (RenameSession != null) RenameSession(id); }
                            },
                            new Popover.Item
                            {
                                Text = "删除会话", Icon = "X",
                                IconColor = Theme.Destructive,
                                OnPick = () => { if (DeleteSession != null) DeleteSession(id); }
                            }
                        };
                        var anchor = new Rectangle(PointToScreen(_itemRects[li].Location), _itemRects[li].Size);
                        Popover.Show(this, anchor, items);
                    }
                }
            }
        }
    }

    internal static class SessionItemExt
    {
        public static bool IsProjectGroupSafe(this SessionItem it) { return false; }
    }
}
