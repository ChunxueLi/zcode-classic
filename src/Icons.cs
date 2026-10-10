// ZCode7 - lucide icon renderer (24x24 viewBox, stroke 2, round caps/joins)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace ZCode7
{
    public static class Icons
    {
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            { "MessageCirclePlus", "M7.9 20A9 9 0 1 0 4 16.1L2 22Z M8 12h8 M12 8v8" },
            { "Search", "m21 21-4.34-4.34 M11 11m-8 0a8 8 0 1 0 8 0a8 8 0 1 0 -8 0" },
            { "CalendarClock", "M16 14v2.2l1.6 1 M16 2v4 M21 7.5V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h3.5 M3 10h5 M8 2v4 M16 16m-6 0a6 6 0 1 0 6 0a6 6 0 1 0 -6 0" },
            { "Blocks", "M10 22V7a1 1 0 0 0-1-1H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-5a1 1 0 0 0-1-1H2 M14.0 3.0a1.0 1.0 0 0 1 1.0 -1.0h6.0a1.0 1.0 0 0 1 1.0 1.0v6.0a1.0 1.0 0 0 1 -1.0 1.0h-6.0a1.0 1.0 0 0 1 -1.0 -1.0z" },
            { "Folder", "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z" },
            { "FolderOpen", "m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2" },
            { "House", "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8 M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" },
            { "Cloud", "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z" },
            { "Settings", "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 12m-3 0a3 3 0 1 0 3 0a3 3 0 1 0 -3 0" },
            { "User", "M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2 M12 7m-4 0a4 4 0 1 0 4 0a4 4 0 1 0 -4 0" },
            { "Brain", "M12 5a3 3 0 1 0-5.997.125 4 4 0 0 0-2.526 5.77 4 4 0 0 0 .556 6.588A4 4 0 1 0 12 18Z M12 5a3 3 0 1 1 5.997.125 4 4 0 0 1 2.526 5.77 4 4 0 0 1-.556 6.588A4 4 0 1 1 12 18Z M15 13a4.5 4.5 0 0 1-3-4 4.5 4.5 0 0 1-3 4 M17.599 6.5a3 3 0 0 0 .399-1.375 M6.003 5.125A3 3 0 0 0 6.401 6.5 M3.477 10.896a4 4 0 0 1 .585-.396 M19.938 10.5a4 4 0 0 1 .585.396 M6 18a4 4 0 0 1-1.967-.516 M19.967 17.484A4 4 0 0 1 18 18" },
            { "ChevronRight", "m9 18 6-6-6-6" },
            { "ChevronDown", "m6 9 6 6 6-6" },
            { "ChevronUp", "m18 15-6-6-6 6" },
            { "ArrowUp", "m5 12 7-7 7 7 M12 19V5" },
            { "ArrowLeft", "m12 19-7-7 7-7 M19 12H5" },
            { "ArrowRight", "M5 12h14 m12 5 7 7-7 7" },
            { "Plus", "M5 12h14 M12 5v14" },
            { "Ellipsis", "M12 12m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M19 12m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M5 12m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0" },
            { "MonitorCog", "M12 17v4 m14.305 7.53.923-.382 m15.228 4.852-.923-.383 m16.852 3.228-.383-.924 m16.852 8.772-.383.923 m19.148 3.228.383-.924 m19.53 9.696-.382-.924 m20.772 4.852.924-.383 m20.772 7.148.924.383 M22 13v2a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h7 M8 21h8 M18 6m-3 0a3 3 0 1 0 3 0a3 3 0 1 0 -3 0" },
            { "ShieldCheck", "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z m9 12 2 2 4-4" },
            { "ShieldAlert", "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z M12 8v4 M12 16h.01" },
            { "Hand", "M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2 M14 10V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2 M10 10.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8 M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15" },
            { "Package", "M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z M12 22V12 M3.29L7L12L12L20.71L7 m7.5 4.27 9 5.15" },
            { "SquareTerminal", "m7 11 2-2-2-2 M11 13h4 M3.0 5.0a2.0 2.0 0 0 1 2.0 -2.0h14.0a2.0 2.0 0 0 1 2.0 2.0v14.0a2.0 2.0 0 0 1 -2.0 2.0h-14.0a2.0 2.0 0 0 1 -2.0 -2.0z" },
            { "Circle", "M12 12m-10 0a10 10 0 1 0 10 0a10 10 0 1 0 -10 0" },
            { "Undo2", "M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11" },
            { "Pencil", "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z m15 5 4 4" },
            { "X", "M18 6 6 18 m6 6 12 12" },
            { "Minimize2", "m14 10 7-7 M20 10h-6V4 m3 21 7-7 M4 14h6v6" },
            { "Maximize2", "M15 3h6v6 m21 3-7 7 m3 21 7-7 M9 21H3v-6" },
            { "FileDiff", "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M9 10h6 M12 13V7 M9 17h6" },
            { "Square", "M3.0 5.0a2.0 2.0 0 0 1 2.0 -2.0h14.0a2.0 2.0 0 0 1 2.0 2.0v14.0a2.0 2.0 0 0 1 -2.0 2.0h-14.0a2.0 2.0 0 0 1 -2.0 -2.0z" },
            { "LoaderCircle", "M21 12a9 9 0 1 1-6.219-8.56" },
            { "Clock", "M12 6v6l4 2 M12 12m-10 0a10 10 0 1 0 10 0a10 10 0 1 0 -10 0" },
            { "Moon", "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z" },
            { "Pin", "M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z" },
            { "ListTree", "M21 12h-8 M21 6H8 M21 18h-8 M3 6v4c0 1.1.9 2 2 2h3 M3 10v6c0 1.1.9 2 2 2h3" },
            { "Archive", "M2.0 4.0a1.0 1.0 0 0 1 1.0 -1.0h18.0a1.0 1.0 0 0 1 1.0 1.0v3.0a1.0 1.0 0 0 1 -1.0 1.0h-18.0a1.0 1.0 0 0 1 -1.0 -1.0z M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8 M10 12h4" },
            { "GitBranch", "M6 3L6 15 M18 6m-3 0a3 3 0 1 0 3 0a3 3 0 1 0 -3 0 M6 18m-3 0a3 3 0 1 0 3 0a3 3 0 1 0 -3 0 M18 9a9 9 0 0 1-9 9" },
            { "Check", "M20 6 9 17l-5-5" },
            { "Copy", "M8.0 10.0a2.0 2.0 0 0 1 2.0 -2.0h10.0a2.0 2.0 0 0 1 2.0 2.0v10.0a2.0 2.0 0 0 1 -2.0 2.0h-10.0a2.0 2.0 0 0 1 -2.0 -2.0z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2" },
            { "Bot", "M12 8V4H8 M4.0 10.0a2.0 2.0 0 0 1 2.0 -2.0h12.0a2.0 2.0 0 0 1 2.0 2.0v8.0a2.0 2.0 0 0 1 -2.0 2.0h-12.0a2.0 2.0 0 0 1 -2.0 -2.0z M2 14h2 M20 14h2 M15 13v2 M9 13v2" },
            { "FileText", "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M10 9H8 M16 13H8 M16 17H8" },
            { "Wrench", "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z" },
            { "Sparkles", "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z M20 3v4 M22 5h-4 M4 17v2 M5 18H3" },
            { "Paperclip", "m16 6-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551" },
            { "Activity", "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2" },
            { "Send", "M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11z m21.854 2.147-10.94 10.939" },
            { "File", "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4" },
            { "Terminal", "M12 19h8 m4 17 6-6-6-6" },
            { "Hash", "M4 9L20 9 M4 15L20 15 M10 3L8 21 M16 3L14 21" },
            { "ListFilter", "M3 6h18 M7 12h10 M10 18h4" },
            { "GripVertical", "M9 12m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M9 5m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M9 19m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M15 12m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M15 5m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0 M15 19m-1 0a1 1 0 1 0 1 0a1 1 0 1 0 -1 0" },
            { "PencilRuler", "M13 7 8.7 2.7a2.41 2.41 0 0 0-3.4 0L2.7 5.3a2.41 2.41 0 0 0 0 3.4L7 13 m8 6 2-2 m18 16 2-2 m17 11 4.3 4.3c.94.94.94 2.46 0 3.4l-2.6 2.6c-.94.94-2.46.94-3.4 0L11 17 M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z m15 5 4 4" },
            { "Globe", "M12 12m-10 0a10 10 0 1 0 10 0a10 10 0 1 0 -10 0 M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20 M2 12h20" },
            { "RefreshCw", "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5" },
            { "ChevronLeft", "m15 18-6-6 6-6" },
            { "RotateCcw", "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5" },
            { "CheckCircle2", "M21.801 10A10 10 0 1 1 17 3.335 m9 11 3 3L22 4" },
        };

        private static readonly HashSet<string> Missing = new HashSet<string>();

        public static bool Has(string name)
        {
            return Paths.ContainsKey(name);
        }

        private static readonly Dictionary<string, Bitmap> PngCache = new Dictionary<string, Bitmap>();

        private static readonly Dictionary<string, Tuple<int, int>> SpriteIndex = new Dictionary<string, Tuple<int, int>>

        {

            { "Activity", Tuple.Create(0, 0) },

            { "Archive", Tuple.Create(1, 0) },

            { "ArrowLeft", Tuple.Create(2, 0) },

            { "ArrowRight", Tuple.Create(3, 0) },

            { "ArrowUp", Tuple.Create(4, 0) },

            { "Blocks", Tuple.Create(5, 0) },

            { "Bot", Tuple.Create(6, 0) },

            { "Brain", Tuple.Create(7, 0) },

            { "CalendarClock", Tuple.Create(0, 1) },

            { "Check", Tuple.Create(1, 1) },

            { "ChevronDown", Tuple.Create(2, 1) },

            { "ChevronLeft", Tuple.Create(3, 1) },

            { "ChevronRight", Tuple.Create(4, 1) },

            { "ChevronUp", Tuple.Create(5, 1) },

            { "Circle", Tuple.Create(6, 1) },

            { "Clock", Tuple.Create(7, 1) },

            { "Cloud", Tuple.Create(0, 2) },

            { "Copy", Tuple.Create(1, 2) },

            { "Ellipsis", Tuple.Create(2, 2) },

            { "File", Tuple.Create(3, 2) },

            { "FileDiff", Tuple.Create(4, 2) },

            { "FileText", Tuple.Create(5, 2) },

            { "Folder", Tuple.Create(6, 2) },

            { "FolderOpen", Tuple.Create(7, 2) },

            { "GitBranch", Tuple.Create(0, 3) },

            { "Globe", Tuple.Create(1, 3) },

            { "GripVertical", Tuple.Create(2, 3) },

            { "Hand", Tuple.Create(3, 3) },

            { "Hash", Tuple.Create(4, 3) },

            { "House", Tuple.Create(5, 3) },

            { "ListFilter", Tuple.Create(6, 3) },

            { "ListTree", Tuple.Create(7, 3) },

            { "LoaderCircle", Tuple.Create(0, 4) },

            { "Maximize2", Tuple.Create(1, 4) },

            { "MessageCirclePlus", Tuple.Create(2, 4) },

            { "Minimize2", Tuple.Create(3, 4) },

            { "MonitorCog", Tuple.Create(4, 4) },

            { "Moon", Tuple.Create(5, 4) },

            { "Package", Tuple.Create(6, 4) },

            { "Paperclip", Tuple.Create(7, 4) },

            { "Pencil", Tuple.Create(0, 5) },

            { "PencilRuler", Tuple.Create(1, 5) },

            { "Pin", Tuple.Create(2, 5) },

            { "Plus", Tuple.Create(3, 5) },

            { "RefreshCw", Tuple.Create(4, 5) },

            { "RotateCcw", Tuple.Create(5, 5) },

            { "Search", Tuple.Create(6, 5) },

            { "Send", Tuple.Create(7, 5) },

            { "Settings", Tuple.Create(0, 6) },

            { "ShieldAlert", Tuple.Create(1, 6) },

            { "ShieldCheck", Tuple.Create(2, 6) },

            { "Sparkles", Tuple.Create(3, 6) },

            { "Square", Tuple.Create(4, 6) },

            { "SquareTerminal", Tuple.Create(5, 6) },

            { "Terminal", Tuple.Create(6, 6) },

            { "Undo2", Tuple.Create(7, 6) },

            { "User", Tuple.Create(0, 7) },

            { "Wrench", Tuple.Create(1, 7) },

            { "X", Tuple.Create(2, 7) },

        };

        private const int SpriteCols = 8;
        private const int SpriteCell = 64;

        private static Bitmap LoadPng(string name, out Rectangle srcRect)
        {
            srcRect = Rectangle.Empty;
            int col = -1, row = -1;
            foreach (var kv in SpriteIndex)
            {
                if (kv.Key == name) { col = kv.Value.Item1; row = kv.Value.Item2; break; }
            }
            if (col < 0) return null;
            Bitmap b;
            if (!PngCache.TryGetValue("sheet", out b) || b == null)
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var st = asm.GetManifestResourceStream("icons.sheet.png"))
                    {
                        if (st != null) b = new Bitmap(st);
                    }
                }
                catch { }
                PngCache["sheet"] = b;
            }
            if (b == null) return null;
            srcRect = new Rectangle(col * SpriteCell, row * SpriteCell, SpriteCell, SpriteCell);
            return b;
        }

        /// <summary>Draw a lucide icon centered in rect (browser-rendered PNG preferred).</summary>
        public static void Draw(Graphics g, string name, Rectangle rect, Color color)
        {
            Rectangle srcRect;
            Bitmap png = LoadPng(name, out srcRect);
            if (png != null)
            {
                if (rect.Width <= 0 || rect.Height <= 0) return;
                var st2 = g.Save();
                try
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float scale = Math.Min(rect.Width, rect.Height) / 24f;
                    int sz = (int)Math.Round(24 * scale);
                    var dest = new Rectangle(rect.X + (rect.Width - sz) / 2, rect.Y + (rect.Height - sz) / 2, sz, sz);
                    using (var attrs = new ImageAttributes())
                    {
                        // ignore source RGB (black), keep alpha, output target color
                        var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
                        {
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 1, 0 },
                            new float[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 },
                        });
                        attrs.SetColorMatrix(cm);
                        g.DrawImage(png, dest, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, GraphicsUnit.Pixel, attrs);
                    }
                }
                catch { }
                finally { g.Restore(st2); }
                return;
            }

            string d;
            if (!Paths.TryGetValue(name, out d))
            {
                if (Missing.Add(name)) ZLog.Log("icon missing: " + name);
                return;
            }
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = Math.Min(rect.Width, rect.Height) / 24f;
                g.TranslateTransform(rect.X + (rect.Width - 24 * scale) / 2f,
                                     rect.Y + (rect.Height - 24 * scale) / 2f);
                g.ScaleTransform(scale, scale);
                using (var pen = new Pen(color))
                {
                    pen.Width = 2f;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    using (var gp = BuildPath(d))
                        g.DrawPath(pen, gp);
                }
            }
            catch { }
            finally { g.Restore(state); }
        }

        // ---- SVG path -> GraphicsPath ----
        private static GraphicsPath BuildPath(string d)
        {
            var gp = new GraphicsPath();
            var toks = Tokenize(d);
            int i = 0;
            float cx = 0, cy = 0, sx = 0, sy = 0;
            float lastC2x = 0, lastC2y = 0, lastQx = 0, lastQy = 0;
            char lastCmd = ' ';
            PointF P(float x, float y) { return new PointF(x, y); }

            while (i < toks.Count)
            {
                char cmd;
                if (toks[i] is char)
                {
                    cmd = (char)toks[i];
                    i++;
                }
                else
                {
                    cmd = lastCmd; // implicit repeat
                    if (cmd == 'M') cmd = 'L';
                    else if (cmd == 'm') cmd = 'l';
                }
                lastCmd = cmd;
                bool rel = char.IsLower(cmd);
                try
                {
                    switch (char.ToUpper(cmd))
                    {
                        case 'M':
                            {
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x += cx; y += cy; }
                                gp.StartFigure();
                                cx = sx = x; cy = sy = y;
                                break;
                            }
                        case 'L':
                            {
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x += cx; y += cy; }
                                gp.AddLine(P(cx, cy), P(x, y));
                                cx = x; cy = y;
                                break;
                            }
                        case 'H':
                            {
                                float x = Num(toks, ref i);
                                if (rel) x += cx;
                                gp.AddLine(P(cx, cy), P(x, cy));
                                cx = x;
                                break;
                            }
                        case 'V':
                            {
                                float y = Num(toks, ref i);
                                if (rel) y += cy;
                                gp.AddLine(P(cx, cy), P(cx, y));
                                cy = y;
                                break;
                            }
                        case 'C':
                            {
                                float x1 = Num(toks, ref i), y1 = Num(toks, ref i);
                                float x2 = Num(toks, ref i), y2 = Num(toks, ref i);
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                                gp.AddBezier(P(cx, cy), P(x1, y1), P(x2, y2), P(x, y));
                                lastC2x = x2; lastC2y = y2;
                                cx = x; cy = y;
                                break;
                            }
                        case 'S':
                            {
                                float x1r = 2 * cx - lastC2x, y1r = 2 * cy - lastC2y;
                                float x2 = Num(toks, ref i), y2 = Num(toks, ref i);
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                                gp.AddBezier(P(cx, cy), P(x1r, y1r), P(x2, y2), P(x, y));
                                lastC2x = x2; lastC2y = y2;
                                cx = x; cy = y;
                                break;
                            }
                        case 'Q':
                            {
                                float qx = Num(toks, ref i), qy = Num(toks, ref i);
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                                float c1x = cx + 2f / 3f * (qx - cx), c1y = cy + 2f / 3f * (qy - cy);
                                float c2x = x + 2f / 3f * (qx - x), c2y = y + 2f / 3f * (qy - y);
                                gp.AddBezier(P(cx, cy), P(c1x, c1y), P(c2x, c2y), P(x, y));
                                lastQx = qx; lastQy = qy;
                                cx = x; cy = y;
                                break;
                            }
                        case 'T':
                            {
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x += cx; y += cy; }
                                float qx = 2 * cx - lastQx, qy = 2 * cy - lastQy;
                                float c1x = cx + 2f / 3f * (qx - cx), c1y = cy + 2f / 3f * (qy - cy);
                                float c2x = x + 2f / 3f * (qx - x), c2y = y + 2f / 3f * (qy - y);
                                gp.AddBezier(P(cx, cy), P(c1x, c1y), P(c2x, c2y), P(x, y));
                                lastQx = qx; lastQy = qy;
                                cx = x; cy = y;
                                break;
                            }
                        case 'A':
                            {
                                float rx = Num(toks, ref i), ry = Num(toks, ref i);
                                float rot = Num(toks, ref i);
                                float laf = Num(toks, ref i), sf = Num(toks, ref i);
                                float x = Num(toks, ref i), y = Num(toks, ref i);
                                if (rel) { x += cx; y += cy; }
                                AddArcTo(gp, cx, cy, rx, ry, rot, laf > 0.5f, sf > 0.5f, x, y);
                                cx = x; cy = y;
                                break;
                            }
                        case 'Z':
                            gp.CloseFigure();
                            cx = sx; cy = sy;
                            break;
                    }
                }
                catch { break; }
            }
            return gp;
        }

        private static void AddArcTo(GraphicsPath gp, float x0, float y0, float rx, float ry,
            float degRot, bool largeArc, bool sweep, float x1, float y1)
        {
            if (rx == 0 || ry == 0) { gp.AddLine(new PointF(x0, y0), new PointF(x1, y1)); return; }
            double phi = degRot * Math.PI / 180.0;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            double dx = (x0 - x1) / 2.0, dy = (y0 - y1) / 2.0;
            double x1p = cosP * dx + sinP * dy;
            double y1p = -sinP * dx + cosP * dy;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lam > 1)
            {
                double s = Math.Sqrt(lam);
                rx = (float)(rx * s); ry = (float)(ry * s);
            }
            double sign = (largeArc != sweep) ? 1 : -1;
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = sign * Math.Sqrt(Math.Max(0, num / den));
            double cxp = co * rx * y1p / ry;
            double cyp = -co * ry * x1p / rx;
            double ccx = cosP * cxp - sinP * cyp + (x0 + x1) / 2.0;
            double ccy = sinP * cxp + cosP * cyp + (y0 + y1) / 2.0;
            double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
            double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
            double n1 = Math.Sqrt(ux * ux + uy * uy), n2 = Math.Sqrt(vx * vx + vy * vy);
            double p1 = Math.Acos(Math.Max(-1, Math.Min(1, ux / n1)));
            double theta1 = (uy < 0) ? -p1 : p1;
            double dt = Math.Acos(Math.Max(-1, Math.Min(1, (ux * vx + uy * vy) / (n1 * n2))));
            if (ux * vy - uy * vx < 0) dt = -dt;
            if (!sweep && dt > 0) dt -= 2 * Math.PI;
            if (sweep && dt < 0) dt += 2 * Math.PI;

            // sample the arc as a polyline (robust; no GDI angle-convention pitfalls)
            float startAngle = (float)(theta1 * 180.0 / Math.PI);
            double steps = Math.Max(8, Math.Ceiling(Math.Abs(dt) / (Math.PI / 12.0)));   // <=15 deg per segment
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
            PointF? prev = null;
            for (int k = 0; k <= steps; k++)
            {
                double t = theta1 + dt * k / steps;
                double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
                float px = (float)(ccx + cosPhi * ex - sinPhi * ey);
                float py = (float)(ccx * 0 + ccy + sinPhi * ex + cosPhi * ey);
                if (prev.HasValue) gp.AddLine(prev.Value, new PointF(px, py));
                else gp.StartFigure();
                prev = new PointF(px, py);
            }
        }

        private static List<object> Tokenize(string d)
        {
            var toks = new List<object>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsLetter(c)) { toks.Add(c); i++; }
                else if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
                {
                    int j = i + 1;
                    while (j < d.Length && (char.IsDigit(d[j]) || d[j] == '.')) j++;
                    float v;
                    if (float.TryParse(d.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        toks.Add(v);
                    i = j;
                }
                else i++;
            }
            return toks;
        }

        private static float Num(List<object> toks, ref int i)
        {
            if (i < toks.Count && toks[i] is float) { var v = (float)toks[i]; i++; return v; }
            throw new FormatException("bad path");
        }
    }
}
