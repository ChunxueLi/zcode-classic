// ZCode7 - owner-drawn dropdown popover (replaces ContextMenuStrip: deterministic close semantics)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZCode7
{
    public class Popover : Form
    {
        public class Item
        {
            public string Text;
            public string Icon;     // lucide icon name (optional)
            public Color IconColor = Color.Transparent;
            public bool Checked;
            public Action OnPick;
        }

        private readonly List<Item> _items;
        private readonly int _rowH = 34;
        private int _hover = -1;
        public static DateTime LastClosedUtc = DateTime.MinValue;

        public static void Show(Control owner, Rectangle anchorScreen, List<Item> items)
        {
            int width = 190;
            foreach (var it in items)
            {
                int w = TextRenderer.MeasureText(it.Text, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width + 64;
                width = Math.Max(width, w);
            }
            foreach (var it in items)
                if (!string.IsNullOrEmpty(it.Icon)) { width = Math.Max(width, 230); break; }
            width = Math.Min(width, 420);
            int height = items.Count * 34 + 12;
            var wa = Screen.FromControl(owner).WorkingArea;
            int x = Math.Min(anchorScreen.X, wa.Right - width - 4);
            int y = anchorScreen.Y - height - 8;          // open upward (composer is at screen bottom)
            if (y < wa.Top + 4) y = anchorScreen.Bottom + 6;
            if (y + height > wa.Bottom - 4) y = wa.Bottom - 4 - height;

            var f = new Popover(items, width, height);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(x, y);
            f.Show(owner);
            f.Activate();
        }

        private Popover(List<Item> items, int width, int height)
        {
            _items = items;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Theme.Popover;
            Size = new Size(width, height);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) CloseSilent(); };
        }

        private void CloseSilent()
        {
            LastClosedUtc = DateTime.UtcNow;
            Close();
        }

        private int HitRow(Point p)
        {
            int y = 6;
            for (int i = 0; i < _items.Count; i++)
            {
                if (p.Y >= y && p.Y < y + _rowH) return i;
                y += _rowH;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(rect, 10))
            {
                using (var b = new SolidBrush(Theme.Popover)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            int y = 6;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (i == _hover)
                {
                    var hr = new Rectangle(4, y, Width - 9, _rowH);
                    using (var path = Theme.Rounded(hr, 6))
                    using (var b = new SolidBrush(Theme.Hover))
                        g.FillPath(b, path);
                }
                int tx = 12;
                if (!string.IsNullOrEmpty(it.Icon))
                {
                    Color ic = it.IconColor != Color.Transparent ? it.IconColor : Theme.TextPrimary;
                    Icons.Draw(g, it.Icon, new Rectangle(10, y + 9, 16, 16), ic);
                    tx = 34;
                }
                TextRenderer.DrawText(g, it.Text, Theme.BaseFont, new Point(tx, y + 9),
                    Theme.TextPrimary, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (it.Checked)
                    Icons.Draw(g, "Check", new Rectangle(Width - 28, y + 9, 16, 16), Theme.TextPrimary);
                y += _rowH;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitRow(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = HitRow(e.Location);
            if (h >= 0)
            {
                var act = _items[h].OnPick;
                LastClosedUtc = DateTime.UtcNow;
                Close();
                if (act != null) act();
            }
            else CloseSilent();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CloseSilent();
        }
    }
}
