// ZCode7 - design tokens ported from ZCode zai-light theme (packages/ui/src/styles.css)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZCode7
{
    public static class Theme
    {
        // ---- zai-light backgrounds (styles.css .theme-zai-light) ----
        public static readonly Color Bg = FromHex("#f8f8f8");            // --color-background
        public static readonly Color BackgroundWinAlt = FromHex("#ececee");
        public static readonly Color Sidebar = FromHex("#f0f0f0");       // --color-sidebar
        public static readonly Color Panel = FromHex("#ffffff");         // --color-panel / header
        public static readonly Color Card = FromHex("#ffffff");          // --color-card
        public static readonly Color Popover = FromHex("#ffffff");       // --color-popover
        public static readonly Color Input = FromHex("#ffffff");         // --color-input
        public static readonly Color Header = FromHex("#ffffff");

        // translucent overlays (over #f8f8f8/#ffffff)
        public static readonly Color Border = Color.FromArgb(26, 13, 13, 13);      // rgba(13,13,13,.1)
        public static readonly Color BorderHover = Color.FromArgb(38, 13, 13, 13); // .15
        public static readonly Color Hover = Color.FromArgb(13, 13, 13, 13);       // .05
        public static readonly Color Selected = Color.FromArgb(13, 13, 13, 13);    // .05
        public static readonly Color Surface = Color.FromArgb(8, 13, 13, 13);      // .03
        public static readonly Color SurfaceHover = Color.FromArgb(13, 13, 13, 13);// .05

        // ---- text (zai-light: foreground neutral-800) ----
        public static readonly Color TextPrimary = FromHex("#262626");   // neutral-800
        public static readonly Color TextSubtle = Color.FromArgb(153, 38, 38, 38);   // 800 @60%
        public static readonly Color TextSubtlest = Color.FromArgb(102, 38, 38, 38); // 800 @40%
        public static readonly Color TextInverse = FromHex("#ffffff");

        // ---- feedback (zai-light) ----
        public static readonly Color Success = FromHex("#1e8a3e");
        public static readonly Color Destructive = FromHex("#dc2626");
        public static readonly Color Warning = FromHex("#e07b00");
        public static readonly Color DiffAdded = FromHex("#16a34a");     // green-600
        public static readonly Color DiffRemoved = FromHex("#dc2626");   // red-600
        public static readonly Color Brand = FromHex("#000000");         // zai brand = black

        // legacy aliases (SettingsForm etc.)
        public static readonly Color Accent = Brand;
        public static readonly Color AccentSoft = Color.FromArgb(13, 13, 13, 13);

        // markdown inline code: --color-tag neutral-200 #e5e5e5 (drawn at /50 on white ≈ #f2f2f2)
        public static readonly Color Tag = FromHex("#e5e5e5");
        public static readonly Color InlineCodeBg = FromHex("#f2f2f2");

        // ---- fonts (pixel sizes, --ui-font-size: 14px) ----
        public static Font BaseFont = Pixel("Microsoft YaHei UI", 14, FontStyle.Regular);
        public static Font BaseFontMedium = Pixel("Microsoft YaHei UI", 14, FontStyle.Bold);
        public static Font BaseFontLg = Pixel("Microsoft YaHei UI", 16, FontStyle.Regular);
        public static Font SmallFont = Pixel("Microsoft YaHei UI", 12, FontStyle.Regular);
        public static Font CaptionFont = Pixel("Microsoft YaHei UI", 13, FontStyle.Regular);
        public static Font XsFont = Pixel("Microsoft YaHei UI", 10, FontStyle.Regular);
        public static Font MonoFont = Pixel("Consolas", 12, FontStyle.Regular);
        public static Font SidebarFont = Pixel("Microsoft YaHei UI", 14, FontStyle.Regular);

        public static Font Pixel(string family, int px, FontStyle style)
        {
            return new Font(family, px, style, GraphicsUnit.Pixel);
        }

        public static Color FromHex(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(
                Convert.ToInt32(hex.Substring(0, 2), 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            return Rounded(r, radius, radius, radius, radius);
        }

        public static GraphicsPath Rounded(Rectangle r, int tl, int tr, int br, int bl)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new Rectangle(r.X, r.Y, 1, 1)); return p; }
            int t = Math.Min(tl, Math.Min(Math.Min(tr, br), Math.Min(bl, Math.Min(r.Width / 2, r.Height / 2))));
            int d = t * 2;
            if (t <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
