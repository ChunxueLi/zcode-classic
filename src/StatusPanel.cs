// ZCode7 - floating status panel as layered window (true ARGB: AA corners + soft shadow, no jaggies)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZCode7
{
    public class TodoItemView
    {
        public string Text;
        public string Status;   // pending / in_progress / completed
    }

    public class StatusPanel : Form
    {
        public List<TodoItemView> Items = new List<TodoItemView>();
        public string DurationText = "";

        private const int Pad = 10;   // subtle shadow margin
        private Rectangle _card;      // card rect in window coords

        public StatusPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = false;
        }

        protected override bool ShowWithoutActivation
        { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000;    // WS_EX_LAYERED
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW (no alt-tab)
                cp.ExStyle |= 0x8000000;  // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { }

        public void SetLayout(Point screenPos, int cardW, int cardH)
        {
            if (!IsHandleCreated) { _ = Handle; }
            _card = new Rectangle(Pad, Pad, cardW, cardH);
            Location = screenPos;
            Size = new Size(cardW + Pad * 2, cardH + Pad * 2);
            Repaint();
        }

        public void Repaint()
        {
            if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    // subtle shadow
                    for (int i = 5; i >= 2; i -= 1)
                    {
                        var sr = new Rectangle(_card.X - i + 1, _card.Y - i + 2, _card.Width + 2 * i, _card.Height + 2 * i);
                        using (var path = Theme.Rounded(sr, 16 + i))
                        using (var b = new SolidBrush(Color.FromArgb(i * 2, 13, 13, 13)))
                            g.FillPath(b, path);
                    }
                    // card
                    using (var path = Theme.Rounded(_card, 16))
                    {
                        using (var b = new SolidBrush(Theme.Popover)) g.FillPath(b, path);
                        using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                    }

                    RenderContent(g);
                }
                ApplyBitmap(bmp);
            }
        }

        private void RenderContent(Graphics g)
        {
            int x = _card.X;
            int y = _card.Y + 10;
            int width = _card.Width;

            int done = 0;
            foreach (var it in Items) if (it.Status == "completed") done++;
            using (var b = new SolidBrush(Theme.TextSubtle))
                g.DrawString("进程", Theme.BaseFont, b, new PointF(x + 12, y - 1));
            string count = done + "/" + Items.Count;
            var cw = g.MeasureString(count, Theme.BaseFont).Width;
            using (var b = new SolidBrush(done == Items.Count && Items.Count > 0 ? Theme.Success : Theme.TextSubtle))
                g.DrawString(count, Theme.BaseFont, b, new PointF(x + width - cw - 14, y - 1));
            y += 28;

            foreach (var it in Items)
            {
                if (y > _card.Bottom - 24) break;
                string icon = it.Status == "completed" ? "CheckCircle2"
                    : (it.Status == "in_progress" ? "ArrowRight" : "Circle");
                Color ic = it.Status == "completed" ? Theme.Success
                    : (it.Status == "in_progress" ? Theme.TextPrimary : Theme.TextSubtlest);
                Icons.Draw(g, icon, new Rectangle(x + 12, y + 3, 14, 14), ic);
                int maxW = width - 36 - 20;
                var lines = Wrap(it.Text, maxW, 2);
                foreach (var ln in lines)
                {
                    Color tc = it.Status == "completed" ? Theme.TextSubtlest
                        : (it.Status == "in_progress" ? Theme.TextPrimary : Theme.TextSubtle);
                    Font f = it.Status == "completed"
                        ? new Font(Theme.BaseFont, FontStyle.Strikeout)
                        : Theme.BaseFont;
                    using (var b = new SolidBrush(tc))
                        g.DrawString(ln, f, b, new PointF(x + 36, y - 1));
                    if (f != Theme.BaseFont) f.Dispose();
                    y += 20;
                }
                y += 6;
            }

            if (DurationText.Length > 0)
            {
                using (var b = new SolidBrush(Theme.TextSubtle))
                    g.DrawString(DurationText, Theme.BaseFont, b, new PointF(x + 12, _card.Bottom - 26));
            }
        }

        private List<string> Wrap(string text, int width, int maxLines)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text)) return res;
            string cur = "";
            foreach (var word in text.Split(' '))
            {
                string w = word + " ";
                string test = cur + w;
                if (TextRenderer.MeasureText(test, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width > width && cur.Length > 0)
                {
                    res.Add(cur);
                    cur = w;
                    if (res.Count == maxLines - 1) break;
                }
                else cur = test;
            }
            if (res.Count < maxLines && cur.Length > 0) res.Add(cur);
            if (res.Count == maxLines && cur.Length > 0 && res[res.Count - 1].Length < text.Length)
            {
                var last = res[res.Count - 1];
                while (last.Length > 1 && TextRenderer.MeasureText(last + "…", Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width > width)
                    last = last.Substring(0, last.Length - 1);
                res[res.Count - 1] = last + "…";
            }
            return res;
        }

        // ---- layered window plumbing ----

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);

        /// <summary>Insert right after the owner in z-order: visually above it, but covered
        /// together with it by other apps (stays "part of" the main window).</summary>
        public void RaiseAbove(IntPtr ownerHwnd)
        {
            SetWindowPos(Handle, ownerHwnd, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW
        }

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        private void ApplyBitmap(Bitmap bmp)
        {
            var size = new SIZE { X = bmp.Width, Y = bmp.Height };
            var src = new POINT { X = 0, Y = 0 };
            var dst = new POINT { X = Location.X, Y = Location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                IntPtr memDc = IntPtr.Zero, oldBmp = IntPtr.Zero, hBmp = IntPtr.Zero;
                try
                {
                    memDc = CreateCompatibleDC(screenDc);
                    // pre-multiply alpha into a DIB
                    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    IntPtr bits = data.Scan0;
                    int len = data.Stride * bmp.Height;
                    var buf = new byte[len];
                    Marshal.Copy(bits, buf, 0, len);
                    bmp.UnlockBits(data);
                    for (int i = 0; i < len; i += 4)
                    {
                        byte a = buf[i + 3];
                        buf[i] = (byte)(buf[i] * a / 255);
                        buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                        buf[i + 2] = (byte)(buf[i + 2] * a / 255);
                    }
                    var bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                    bmi.bmiHeader.biWidth = bmp.Width;
                    bmi.bmiHeader.biHeight = -bmp.Height;
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0;
                    hBmp = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                    Marshal.Copy(buf, 0, bits, len);
                    oldBmp = SelectObject(memDc, hBmp);
                    bool ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
                    if (!ok) ZLog.Log("ULW failed ec=" + Marshal.GetLastWin32Error() + " dst=" + dst.X + "," + dst.Y + " size=" + size.X + "x" + size.Y);

                }
                finally
                {
                    if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    if (memDc != IntPtr.Zero) DeleteDC(memDc);
                }
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        { public uint biSize; public int biWidth, biHeight; public short biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        { public BITMAPINFOHEADER bmiHeader; }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
    }
}
