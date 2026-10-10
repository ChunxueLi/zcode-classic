using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ZCode7
{
    // 电脑操作（CUA）原语：UIA 控件树为主、鼠标键盘 SendInput、GDI 截屏。
    // 设计原则：原生应用优先走 UIA（比像素点击可靠），鼠标/键盘兜底；
    // 坐标一律物理像素（Program.Main 已 SetProcessDPIAware）。
    public static class ComputerUse
    {
        // ---------- P/Invoke ----------
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        private struct POINT { public int X, Y; }
        private struct RECT { public int L, T, R, B; }

        private const int SM_XVIRTUAL = 76; private const int SM_YVIRTUAL = 77;
        private const int SM_WVIRTUAL = 78; private const int SM_HVIRTUAL = 79;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;      // 0=mouse 1=keyboard
            public InputUnion U;
            public static INPUT Create() { var i = new INPUT(); return i; }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        // 供 Program.Main 启动时调用：坐标物理化，避免 DPI 虚拟化错位
        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        // 前台/目标窗口是否属于本进程——是则拒绝键盘/关窗，防止 agent 把宿主打关了
        private static bool IsSelfWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try { return pid == (uint)Process.GetCurrentProcess().Id; }
            catch { return false; }
        }

        // ---------- 工具入口 ----------
        public static List<object> Definitions()
        {
            var defs = new List<object>();
            defs.Add(CuSchema.Func("UIA",
                "Operate native Windows app UI via UI Automation (UIA). Preferred over raw mouse/keyboard. " +
                "Actions: tree (dump element tree of a window), find (list elements matching query), " +
                "invoke|toggle|expand|collapse|select (activate element), value (set text value), " +
                "click|dblclick (mouse click at element center), focus. " +
                "window: title substring, empty/'foreground' = current foreground window. " +
                "query: substring of element Name or AutomationId. index: 0-based match index.",
                CuSchema.Props("type", "object",
                    "properties", CuSchema.Props(
                        "action", CuSchema.Prop("string", "tree|find|invoke|toggle|expand|collapse|select|value|click|dblclick|focus"),
                        "window", CuSchema.Prop("string", "Window title substring; empty or 'foreground' for foreground window"),
                        "query", CuSchema.Prop("string", "Substring of element Name or AutomationId to locate"),
                        "type", CuSchema.Prop("string", "Optional control type filter, e.g. Button/Edit/CheckBox"),
                        "index", CuSchema.Prop("integer", "0-based index among matches (default 0)"),
                        "value", CuSchema.Prop("string", "New value for 'value' action"),
                        "depth", CuSchema.Prop("integer", "Tree depth for 'tree' action (default 5, max 8)")),
                    "required", CuSchema.Required("action"))));

            defs.Add(CuSchema.Func("Mouse",
                "Move/click the real mouse. Actions: move, click, dblclick, rightclick, down, up, drag (from x,y to x2,y2), " +
                "scroll (delta>0 up), pos (read current position). Coordinates are physical pixels of the virtual screen.",
                CuSchema.Props("type", "object",
                    "properties", CuSchema.Props(
                        "action", CuSchema.Prop("string", "move|click|dblclick|rightclick|down|up|drag|scroll|pos"),
                        "x", CuSchema.Prop("integer", "X in physical pixels"), "y", CuSchema.Prop("integer", "Y in physical pixels"),
                        "x2", CuSchema.Prop("integer", "drag target X"), "y2", CuSchema.Prop("integer", "drag target Y"),
                        "delta", CuSchema.Prop("integer", "wheel delta for scroll, positive = up")),
                    "required", CuSchema.Required("action"))));

            defs.Add(CuSchema.Func("Key",
                "Send keyboard input to the foreground window. Either a key combo like 'ctrl+s', 'alt+f4', 'enter', " +
                "'win+d', 'shift+tab', or unicode text via text arg (typed character by character).",
                CuSchema.Props("type", "object",
                    "properties", CuSchema.Props(
                        "keys", CuSchema.Prop("string", "Combo: modifiers+key, e.g. ctrl+shift+esc, enter, f5"),
                        "text", CuSchema.Prop("string", "Unicode text to type")),
                    "required", CuSchema.Required())));

            defs.Add(CuSchema.Func("Window",
                "List top-level windows or manipulate one. Actions: list, focus, minimize, maximize, restore, close, rect. " +
                "match: case-insensitive title substring.",
                CuSchema.Props("type", "object",
                    "properties", CuSchema.Props(
                        "action", CuSchema.Prop("string", "list|focus|minimize|maximize|restore|close|rect"),
                        "match", CuSchema.Prop("string", "Title substring to select the window (list needs none)")),
                    "required", CuSchema.Required("action"))));

            defs.Add(CuSchema.Func("Screen",
                "Capture screen (full virtual screen, or a window by title) to a PNG file. " +
                "Returns the saved path which can be opened with the built-in browser (BrowserPane) or /preview route.",
                CuSchema.Props("type", "object",
                    "properties", CuSchema.Props(
                        "window", CuSchema.Prop("string", "Optional window title substring; empty = whole virtual screen"),
                        "path", CuSchema.Prop("string", "Optional output PNG path; default %APPDATA%\\ZCode7\\screens\\")),
                    "required", CuSchema.Required())));

            return defs;
        }

        public static string Execute(string name, Dictionary<string, object> args)
        {
            switch (name)
            {
                case "UIA": return Uia(args);
                case "Mouse": return Mouse(args);
                case "Key": return Key(args);
                case "Window": return Window(args);
                case "Screen": return ScreenTool(args);
                default: return "Unknown tool: " + name;
            }
        }

        // 该工具调用是否需要用户审批（走 Agent.Approve 之外的补充判断：
        // 只读动作不审批，改状态动作审批）
        public static bool NeedsApproval(string name, Dictionary<string, object> args)
        {
            string action = args != null && args.ContainsKey("action")
                ? Convert.ToString(args["action"]) ?? "" : "";
            switch (name)
            {
                case "Mouse": return action != "pos";
                case "Key": return true;
                case "Window": return action != "list" && action != "rect";
                case "Screen": return false;
                case "UIA": return !(action == "tree" || action == "find" || action == "");
                default: return true;
            }
        }

        private static string GetStr(Dictionary<string, object> d, string k, string def)
        { return MiniJson.GetStr(d, k, def); }

        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return def;
        }

        // ================= UIA =================

        private static string Uia(Dictionary<string, object> args)
        {
            string action = GetStr(args, "action", "");
            string winSpec = GetStr(args, "window", "");
            try
            {
                AutomationElement root = ResolveWindow(winSpec);
                if (root == null)
                    return "window not found: '" + winSpec + "'. Use Window.list to see candidates.";

                if (action == "tree")
                {
                    int depth = GetInt(args, "depth", 5);
                    if (depth > 8) depth = 8; if (depth < 1) depth = 1;
                    var sb = new StringBuilder();
                    int count = 0;
                    string title = SafeName(root);
                    sb.AppendLine("WINDOW " + title);
                    Walk(root, 1, depth, sb, ref count);
                    if (count >= 150) sb.AppendLine("...(truncated at 150 nodes, increase query precision or reduce depth)");
                    return sb.ToString();
                }

                if (action == "find")
                {
                    var els = FindMatches(root, GetStr(args, "query", ""), GetStr(args, "type", ""));
                    if (els.Count == 0) return "no matches. Consider UIA tree first.";
                    var sb = new StringBuilder();
                    for (int i = 0; i < els.Count && i < 40; i++)
                        sb.AppendLine(i + ": " + Describe(els[i]));
                    return sb.ToString();
                }

                // 以下动作需要定位到元素
                var target = FindMatches(root, GetStr(args, "query", ""), GetStr(args, "type", ""));
                if (target.Count == 0)
                    return "no element matches '" + GetStr(args, "query", "") + "'. Run UIA tree to inspect.";
                int idx = GetInt(args, "index", 0);
                if (idx < 0 || idx >= target.Count)
                    return "index " + idx + " out of range, matches=" + target.Count;
                AutomationElement el = target[idx];

                switch (action)
                {
                    case "invoke": return Act(el, () => { ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return "invoked"; });
                    case "toggle": return Act(el, () => { ((TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); return "toggled"; });
                    case "expand": return Act(el, () => { ((ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); return "expanded"; });
                    case "collapse": return Act(el, () => { ((ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); return "collapsed"; });
                    case "select": return Act(el, () => { ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); return "selected"; });
                    case "focus": return Act(el, () => { el.SetFocus(); return "focused"; });
                    case "value":
                        string val = GetStr(args, "value", null);
                        if (val == null) return "missing 'value' argument";
                        return Act(el, () => { ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(val); return "value set"; });
                    case "click": return ClickElement(el, false);
                    case "dblclick": return ClickElement(el, true);
                    default: return "unknown UIA action: " + action;
                }
            }
            catch (Exception ex)
            {
                return "UIA " + action + " failed: " + ex.Message;
            }
        }

        private static string Act(AutomationElement el, Func<string> a)
        {
            try { return a() + " :: " + Describe(el); }
            catch (Exception ex) { return "pattern not supported or failed: " + ex.Message + " :: " + Describe(el); }
        }

        private static AutomationElement ResolveWindow(string spec)
        {
            if (string.IsNullOrEmpty(spec) || spec == "foreground")
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    var el = AutomationElement.FromHandle(fg);
                    if (el != null) return el;
                }
                return AutomationElement.RootElement;
            }
            foreach (var w in TopWindows())
            {
                if (w.Title.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0)
                    return AutomationElement.FromHandle(w.Hwnd);
            }
            // 再从 UIA 根的子窗口里按 Name 找（有些窗口 EnumWindows 拿不到标题）
            return AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, spec));
        }

        private static string Describe(AutomationElement e)
        {
            try
            {
                var c = e.Current;
                string name = c.Name ?? "";
                if (name.Length > 40) name = name.Substring(0, 40);
                var r = c.BoundingRectangle;
                return "[" + (c.ControlType != null ? c.ControlType.ProgrammaticName.Replace("ControlType.", "") : "?") + "]"
                    + " '" + name + "' #" + c.AutomationId
                    + " (" + (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height + ")"
                    + (c.IsEnabled ? "" : " [disabled]");
            }
            catch (Exception ex) { return "[element desc fail: " + ex.Message + "]"; }
        }

        private static string SafeName(AutomationElement e)
        {
            try { return e.Current.Name ?? "(unnamed)"; } catch { return "(unreadable)"; }
        }

        private static void Walk(AutomationElement e, int depth, int maxDepth, StringBuilder sb, ref int count)
        {
            if (count >= 150 || depth > maxDepth) return;
            sb.Append(new string(' ', (depth - 1) * 2));
            sb.AppendLine(Describe(e));
            count++;
            try
            {
                var kids = e.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement k in kids) Walk(k, depth + 1, maxDepth, sb, ref count);
            }
            catch { }
        }

        private static List<AutomationElement> FindMatches(AutomationElement root, string query, string type)
        {
            var result = new List<AutomationElement>();
            if (string.IsNullOrEmpty(query) && string.IsNullOrEmpty(type))
            {
                result.Add(root);
                return result;
            }
            Collect(root, query, type, result, 0);
            return result;
        }

        private static void Collect(AutomationElement e, string query, string type, List<AutomationElement> acc, int depth)
        {
            if (depth > 12 || acc.Count >= 200) return;
            try
            {
                var kids = e.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement k in kids)
                {
                    try
                    {
                        var c = k.Current;
                        bool typeOk = string.IsNullOrEmpty(type)
                            || (c.ControlType != null && c.ControlType.ProgrammaticName.Replace("ControlType.", "")
                                .IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0);
                        bool qOk = string.IsNullOrEmpty(query);
                        if (!qOk)
                        {
                            string name = c.Name ?? "";
                            string aid = c.AutomationId ?? "";
                            qOk = name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                || aid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        if (typeOk && qOk && c.IsEnabled) acc.Add(k);
                        Collect(k, query, type, acc, depth + 1);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string ClickElement(AutomationElement el, bool dbl)
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty) return "element has empty bounds";
                int x = (int)(r.X + r.Width / 2), y = (int)(r.Y + r.Height / 2);
                ClickRaw(x, y, false);
                if (dbl) ClickRaw(x, y, false);
                string d = Describe(el);
                return (dbl ? "dblclick" : "click") + " at (" + x + "," + y + ") :: " + d;
            }
            catch (Exception ex) { return "click fail: " + ex.Message; }
        }

        // ================= Mouse =================

        private static string Mouse(Dictionary<string, object> args)
        {
            string action = GetStr(args, "action", "");
            try
            {
                switch (action)
                {
                    case "pos":
                        POINT p;
                        GetCursorPos(out p);
                        return "cursor at (" + p.X + "," + p.Y + ")";
                    case "move":
                        MoveTo(GetInt(args, "x", 0), GetInt(args, "y", 0));
                        return "moved";
                    case "click":
                        MoveTo(GetInt(args, "x", 0), GetInt(args, "y", 0));
                        ClickRaw(GetInt(args, "x", 0), GetInt(args, "y", 0), false);
                        return "left clicked at (" + GetInt(args, "x", 0) + "," + GetInt(args, "y", 0) + ")";
                    case "dblclick":
                        ClickRaw(GetInt(args, "x", 0), GetInt(args, "y", 0), false);
                        ClickRaw(GetInt(args, "x", 0), GetInt(args, "y", 0), true);
                        return "double clicked";
                    case "rightclick":
                        ClickRaw(GetInt(args, "x", 0), GetInt(args, "y", 0), true);
                        return "right clicked";
                    case "down":
                        SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                        return "left button down";
                    case "up":
                        SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                        return "left button up";
                    case "drag":
                        MoveTo(GetInt(args, "x", 0), GetInt(args, "y", 0));
                        SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                        System.Threading.Thread.Sleep(120);
                        MoveTo(GetInt(args, "x2", GetInt(args, "x", 0)), GetInt(args, "y2", GetInt(args, "y", 0)));
                        System.Threading.Thread.Sleep(80);
                        SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                        return "dragged to (" + GetInt(args, "x2", 0) + "," + GetInt(args, "y2", 0) + ")";
                    case "scroll":
                        SendMouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)GetInt(args, "delta", -120));
                        return "scrolled " + GetInt(args, "delta", -120);
                    default: return "unknown Mouse action: " + action;
                }
            }
            catch (Exception ex) { return "Mouse " + action + " failed: " + ex.Message; }
        }

        private static void MoveTo(int x, int y)
        {
            // 绝对坐标归一化到 0..65535
            int vx = GetSystemMetrics(SM_XVIRTUAL), vy = GetSystemMetrics(SM_YVIRTUAL);
            int vw = GetSystemMetrics(SM_WVIRTUAL), vh = GetSystemMetrics(SM_HVIRTUAL);
            int ax = (x - vx) * 65535 / Math.Max(1, vw - 1);
            int ay = (y - vy) * 65535 / Math.Max(1, vh - 1);
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay, 0);
        }

        private static void ClickRaw(int x, int y, bool right)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(40);
            if (right)
            {
                SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0);
                SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0);
            }
            else
            {
                SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
            }
        }

        private static void SendMouse(uint flags, int dx, int dy, uint data)
        {
            var inp = new INPUT { type = 0 };
            inp.U.mi = new MOUSEINPUT { dwFlags = flags, dx = dx, dy = dy, mouseData = data };
            SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        // ================= Key =================

        private static readonly Dictionary<string, ushort> VkMap = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"enter",0x0D},{"return",0x0D},{"tab",0x09},{"esc",0x1B},{"escape",0x1B},{"space",0x20},{"backspace",0x08},
            {"delete",0x2E},{"del",0x2E},{"insert",0x2D},{"home",0x24},{"end",0x23},{"pgup",0x21},{"pgdn",0x22},
            {"left",0x25},{"up",0x26},{"right",0x27},{"down",0x28},{"win",0x5B},{"lwin",0x5B},{"rwin",0x5C},
            {"shift",0x10},{"ctrl",0x11},{"control",0x11},{"alt",0x12},{"menu",0x12},{"printscreen",0x2C},{"capslock",0x14},
            {"f1",0x70},{"f2",0x71},{"f3",0x72},{"f4",0x73},{"f5",0x74},{"f6",0x75},{"f7",0x76},{"f8",0x77},
            {"f9",0x78},{"f10",0x79},{"f11",0x7A},{"f12",0x7B},
            {"a",0x41},{"b",0x42},{"c",0x43},{"d",0x44},{"e",0x45},{"f",0x46},{"g",0x47},{"h",0x48},{"i",0x49},
            {"j",0x4A},{"k",0x4B},{"l",0x4C},{"m",0x4D},{"n",0x4E},{"o",0x4F},{"p",0x50},{"q",0x51},{"r",0x52},
            {"s",0x53},{"t",0x54},{"u",0x55},{"v",0x56},{"w",0x57},{"x",0x58},{"y",0x59},{"z",0x5A},
            {"0",0x30},{"1",0x31},{"2",0x32},{"3",0x33},{"4",0x34},{"5",0x35},{"6",0x36},{"7",0x37},{"8",0x38},{"9",0x39},
            {"-",0xBD},{"=",0xBB},{"[",0xDB},{"]",0xDD},{"\\",0xDC},{";",0xBA},{"'",0xDE},{",",0xBC},{".",0xBE},{"/",0xBF},{"`",0xC0}
        };

        private static string Key(Dictionary<string, object> args)
        {
            try
            {
                if (IsSelfWindow(GetForegroundWindow()))
                    return "refused: foreground window is ZCode Classic itself. " +
                           "Use Window focus to bring the target app forward first.";
                string text = GetStr(args, "text", null);
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (char ch in text) SendChar(ch);
                    return "typed " + text.Length + " chars";
                }
                string combo = GetStr(args, "keys", "");
                if (combo.Length == 0) return "provide 'keys' or 'text'";
                string[] parts = combo.Split('+');
                var mods = new List<ushort>();
                string main = "";
                foreach (string raw in parts)
                {
                    string p = raw.Trim();
                    if (p.Length == 0) continue;
                    ushort vk;
                    if (VkMap.TryGetValue(p, out vk))
                    {
                        if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B) mods.Add(vk);
                        else main = p;
                    }
                    else return "unknown key: " + p;
                }
                ushort mainVk = main.Length > 0 ? VkMap[main] : (ushort)0;
                if (mainVk == 0 && mods.Count > 0) mainVk = mods[mods.Count - 1];

                foreach (ushort m in mods) SendVk(m, false);
                SendVk(mainVk, false);
                SendVk(mainVk, true);
                for (int i = mods.Count - 1; i >= 0; i--) SendVk(mods[i], true);
                return "sent " + combo;
            }
            catch (Exception ex) { return "Key failed: " + ex.Message; }
        }

        private static void SendVk(ushort vk, bool up)
        {
            var inp = new INPUT { type = 1 };
            inp.U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
            SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendChar(char ch)
        {
            var down = new INPUT { type = 1 };
            down.U.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE };
            var up = new INPUT { type = 1 };
            up.U.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
        }

        // ================= Window =================

        private class WinInfo { public IntPtr Hwnd; public string Title; public RECT Rect; public uint Pid; }

        private static List<WinInfo> TopWindows()
        {
            var list = new List<WinInfo>();
            EnumWindows((EnumProc)((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    int len = GetWindowTextLength(h);
                    if (len <= 0) return true;
                    var sb = new StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    var w = new WinInfo { Hwnd = h, Title = sb.ToString() };
                    GetWindowRect(h, out w.Rect);
                    GetWindowThreadProcessId(h, out w.Pid);
                    list.Add(w);
                }
                catch { }
                return true;
            }), IntPtr.Zero);
            return list;
        }

        private static string Window(Dictionary<string, object> args)
        {
            string action = GetStr(args, "action", "");
            string match = GetStr(args, "match", "");
            try
            {
                if (action == "list")
                {
                    var sb = new StringBuilder();
                    string fgH = "";
                    IntPtr fg = GetForegroundWindow();
                    foreach (var w in TopWindows())
                    {
                        string proc = "?";
                        try { proc = Process.GetProcessById((int)w.Pid).ProcessName; } catch { }
                        sb.Append("'").Append(w.Title).Append("'")
                          .Append(" pid=").Append(w.Pid).Append(" proc=").Append(proc)
                          .Append(" rect=(").Append(w.Rect.L).Append(",").Append(w.Rect.T)
                          .Append(" ").Append(w.Rect.R - w.Rect.L).Append("x").Append(w.Rect.B - w.Rect.T).Append(")")
                          .Append(IsIconic(w.Hwnd) ? " [min]" : "")
                          .Append(w.Hwnd == fg ? " [foreground]" : "")
                          .AppendLine();
                    }
                    return sb.ToString();
                }

                WinInfo target = null;
                foreach (var w in TopWindows())
                    if (w.Title.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) { target = w; break; }
                if (target == null) return "no window title contains '" + match + "'. Use Window.list.";
                if (action == "close" && IsSelfWindow(target.Hwnd))
                    return "refused: that window belongs to ZCode Classic itself.";

                int cmd = action == "focus" ? 9 : action == "minimize" ? 6 : action == "maximize" ? 3
                    : action == "restore" ? 9 : action == "close" ? 0x10 : -1;
                if (cmd == -1 && action == "rect")
                    return "'" + target.Title + "' rect=(" + target.Rect.L + "," + target.Rect.T + " "
                        + (target.Rect.R - target.Rect.L) + "x" + (target.Rect.B - target.Rect.T) + ")";

                if (action == "focus" || action == "restore")
                {
                    if (IsIconic(target.Hwnd)) ShowWindow(target.Hwnd, 9);
                    SetForegroundWindow(target.Hwnd);
                    return (IsIconic(target.Hwnd) ? "restored+focused" : "focused") + " '" + target.Title + "'";
                }
                ShowWindow(target.Hwnd, cmd);
                return action + " '" + target.Title + "' ok";
            }
            catch (Exception ex) { return "Window " + action + " failed: " + ex.Message; }
        }

        // ================= Screen =================

        private static string ScreenTool(Dictionary<string, object> args)
        {
            string winSpec = GetStr(args, "window", "");
            string path = GetStr(args, "path", "");
            try
            {
                Rectangle bounds;
                if (!string.IsNullOrEmpty(winSpec) && winSpec != "foreground")
                {
                    WinInfo target = null;
                    foreach (var w in TopWindows())
                        if (w.Title.IndexOf(winSpec, StringComparison.OrdinalIgnoreCase) >= 0) { target = w; break; }
                    if (target == null) return "no window title contains '" + winSpec + "'";
                    if (IsIconic(target.Hwnd)) return "window is minimized; restore it first (Window restore)";
                    bounds = Rectangle.FromLTRB(target.Rect.L, target.Rect.T, target.Rect.R, target.Rect.B);
                }
                else
                {
                    bounds = Rectangle.FromLTRB(
                        GetSystemMetrics(SM_XVIRTUAL), GetSystemMetrics(SM_YVIRTUAL),
                        GetSystemMetrics(SM_XVIRTUAL) + GetSystemMetrics(SM_WVIRTUAL),
                        GetSystemMetrics(SM_YVIRTUAL) + GetSystemMetrics(SM_HVIRTUAL));
                }

                if (string.IsNullOrEmpty(path))
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ZCode7", "screens");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "screen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                using (var bmp = new Bitmap(bounds.Width, bounds.Height))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
                    bmp.Save(path, ImageFormat.Png);
                }
                var fi = new FileInfo(path);
                return "saved " + path + " (" + bounds.Width + "x" + bounds.Height + ", " + (fi.Length / 1024) + " KB). " +
                       "Preview: open this path in the built-in browser.";
            }
            catch (Exception ex) { return "Screen failed: " + ex.Message; }
        }
    }

    // Tools.cs 的 schema 帮助方法包一层公开入口（避免把 Tools 的私有方法改公开）
    internal static class CuSchema
    {
        internal static Dictionary<string, object> Func(string name, string desc, Dictionary<string, object> parameters)
        {
            var fn = new Dictionary<string, object>();
            fn["name"] = name;
            fn["description"] = desc;
            fn["parameters"] = parameters;
            var wrap = new Dictionary<string, object>();
            wrap["type"] = "function";
            wrap["function"] = fn;
            return wrap;
        }
        internal static Dictionary<string, object> Props(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        internal static Dictionary<string, object> Prop(string type, string desc)
        {
            var p = new Dictionary<string, object>();
            p["type"] = type; p["description"] = desc; return p;
        }
        internal static List<object> Required(params string[] names)
        {
            var l = new List<object>();
            foreach (var n in names) l.Add(n);
            return l;
        }
    }
}
