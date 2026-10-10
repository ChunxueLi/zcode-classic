// ZCode7 - toolchain, names and behavior ported from ZCode core tools
// Bash / Read / Write / Edit / Glob / Grep / Todo / Skill / WebFetch / SystemInfo
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZCode7
{
    public static class Tools
    {
        // current todo list (set via Todo tool, rendered by UI)
        public static List<Dictionary<string, object>> Todos = new List<Dictionary<string, object>>();

        // running Bash process, killable when the user presses stop
        public static Process CurrentProcess;
        public static Func<bool> CancelRequested;

        public static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/T /F /PID " + p.Id,
                    CreateNoWindow = true,
                    UseShellExecute = false
                })) { k.WaitForExit(3000); }
            }
            catch { try { p.Kill(); } catch { } }
        }

        // ---- schema helpers ----
        private static Dictionary<string, object> Obj() { return new Dictionary<string, object>(); }

        private static Dictionary<string, object> Prop(string type, string desc)
        {
            var p = Obj(); p["type"] = type; p["description"] = desc; return p;
        }

        private static Dictionary<string, object> Props(params object[] kv)
        {
            var d = Obj();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static List<object> Required(params string[] names)
        {
            var a = new List<object>();
            foreach (var n in names) a.Add(n);
            return a;
        }

        private static Dictionary<string, object> ArrOf(string type)
        {
            var p = Obj(); p["type"] = "array";
            var it = Obj(); it["type"] = type;
            p["items"] = it;
            return p;
        }

        private static Dictionary<string, object> Func(string name, string desc, Dictionary<string, object> parameters)
        {
            var fn = Obj(); fn["name"] = name; fn["description"] = desc; fn["parameters"] = parameters;
            var wrap = Obj(); wrap["type"] = "function"; wrap["function"] = fn;
            return wrap;
        }

        public static List<object> Definitions()
        {
            var defs = new List<object>();

            defs.Add(Func("Bash",
                "Executes the given cmd command on this Windows machine and returns stdout/stderr/exit code. " +
                "Executes cmd commands on this Windows machine. Avoid interactive commands. Output is GBK-decoded.",
                Props("type", "object",
                      "properties", Props(
                          "command", Prop("string", "The cmd command to execute"),
                          "timeout", Prop("integer", "Optional timeout in seconds (max by settings, default 60)")),
                      "required", Required("command"))));

            defs.Add(Func("Read",
                "Reads a local text file, returns numbered lines (cat -n style). " +
                "Supports offset/limit. UTF-8/GBK auto-detected. Reads up to 2000 lines by default.",
                Props("type", "object",
                      "properties", Props(
                          "file_path", Prop("string", "Absolute or workspace-relative file path"),
                          "offset", Prop("integer", "1-based line number to start from"),
                          "limit", Prop("integer", "Number of lines to read (default 2000)")),
                      "required", Required("file_path"))));

            defs.Add(Func("Write",
                "Writes a text file, overwriting if it exists. Creates parent directories. UTF-8 output.",
                Props("type", "object",
                      "properties", Props(
                          "file_path", Prop("string", "Target file path"),
                          "content", Prop("string", "Content to write")),
                      "required", Required("file_path", "content"))));

            defs.Add(Func("Edit",
                "Exact string replacement inside a text file. old_string must match file content uniquely unless replace_all is true.",
                Props("type", "object",
                      "properties", Props(
                          "file_path", Prop("string", "File to edit"),
                          "old_string", Prop("string", "Text to replace (must match exactly)"),
                          "new_string", Prop("string", "Replacement text"),
                          "replace_all", Prop("boolean", "Replace all occurrences (default false)")),
                      "required", Required("file_path", "old_string", "new_string"))));

            defs.Add(Func("Glob",
                "Finds files by wildcard pattern (e.g. **/*.cs, *.log), searched recursively under path.",
                Props("type", "object",
                      "properties", Props(
                          "pattern", Prop("string", "Wildcard pattern"),
                          "path", Prop("string", "Directory to search (default current)")),
                      "required", Required("pattern"))));

            defs.Add(Func("Grep",
                "Searches text files under a directory for lines containing the keyword (case-insensitive), returns file:line matches (max 100).",
                Props("type", "object",
                      "properties", Props(
                          "pattern", Prop("string", "Keyword to search"),
                          "path", Prop("string", "Directory to search"),
                          "include", Prop("string", "File wildcard filter, e.g. *.cs (default all files)")),
                      "required", Required("pattern"))));

            defs.Add(Func("Todo",
                "Maintains the task todo list shown in the UI. Call with the full list each time: " +
                "each item {content, status: pending|in_progress|completed}. Exactly one item may be in_progress.",
                Props("type", "object",
                      "properties", Props(
                          "todos", new Dictionary<string, object>
                          {
                              { "type", "array" },
                              { "description", "Full todo list" },
                              { "items", Props("type", "object",
                                  "properties", Props(
                                      "content", Prop("string", "Task description"),
                                      "status", Prop("string", "pending | in_progress | completed"))) }
                          }),
                      "required", Required("todos"))));

            defs.Add(Func("Skill",
                "Loads the full instructions of an installed skill by name. Use when the user invokes /skill-name " +
                "or when the request matches a skill's description.",
                Props("type", "object",
                      "properties", Props("name", Prop("string", "Skill name")),
                      "required", Required("name"))));

            defs.Add(Func("WebFetch",
                "Fetches a URL over HTTP(S) from this machine and returns the raw text content (HTML tags stripped, truncated).",
                Props("type", "object",
                      "properties", Props(
                          "url", Prop("string", "The URL to fetch"),
                          "max_chars", Prop("integer", "Max characters to return (default 8000)")),
                      "required", Required("url"))));

            defs.Add(Func("SystemInfo",
                "Returns machine overview: OS, CPU, disks, memory, hostname, current user, time.",
                Props("type", "object", "properties", Obj(), "required", Required())));

            defs.AddRange(ComputerUse.Definitions());
            return defs;
        }

        public static string Execute(string name, Dictionary<string, object> args, Settings cfg)
        {
            switch (name)
            {
                case "Bash": return Bash(args, cfg);
                case "Read": return Read(args, cfg);
                case "Write": return Write(args);
                case "Edit": return Edit(args);
                case "Glob": return Glob(args);
                case "Grep": return Grep(args);
                case "Todo": return Todo(args);
                case "Skill": return SkillTool(args);
                case "WebFetch": return WebFetch(args, cfg);
                case "SystemInfo": return SystemInfo();
                case "UIA":
                case "Mouse":
                case "Key":
                case "Window":
                case "Screen":
                    return ComputerUse.Execute(name, args);
                default: return "Unknown tool: " + name;
            }
        }

        private static string GetStr(Dictionary<string, object> d, string k, string def)
        { return MiniJson.GetStr(d, k, def); }

        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                if (v is long) return checked((int)(long)v);
                if (v is int) return (int)v;
                if (v is double) return (int)Convert.ToDouble(v);
                int i;
                if (int.TryParse(v.ToString(), out i)) return i;
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                bool b;
                if (bool.TryParse(v.ToString(), out b)) return b;
            }
            return def;
        }

        // ---------- Bash ----------
        private static string Bash(Dictionary<string, object> args, Settings cfg)
        {
            string cmd = GetStr(args, "command", "");
            if (cmd.Trim().Length == 0) return "(empty command)";
            int timeout = GetInt(args, "timeout", 60);
            if (timeout <= 0) timeout = 60;
            if (timeout > cfg.MaxCommandTimeoutSec) timeout = cfg.MaxCommandTimeoutSec;
            var psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c " + cmd;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.GetEncoding(936);
            psi.StandardErrorEncoding = Encoding.GetEncoding(936);
            var sb = new StringBuilder();
            try
            {
                using (var p = Process.Start(psi))
                {
                    CurrentProcess = p;
                    // async pipe reads: a cancel-kill must not deadlock on full pipes
                    var soTask = p.StandardOutput.ReadToEndAsync();
                    var seTask = p.StandardError.ReadToEndAsync();
                    bool exited = false, cancelled = false, timedOut = false;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        if (CancelRequested != null && CancelRequested())
                        {
                            KillTree(p);
                            cancelled = true;
                            break;
                        }
                        // 硬超时：GUI 孙进程继承管道时命令可能永远不退出
                        if (sw.ElapsedMilliseconds >= timeout * 1000L)
                        {
                            KillTree(p);
                            timedOut = true;
                            break;
                        }
                        if (p.WaitForExit(200)) { exited = true; break; }
                    }
                    // cmd 退出 ≠ 管道关闭：`start` 出去的 GUI 进程可能继承写句柄，EOF 永远不来。
                    // 限时等待读完成，超时则杀树并放弃输出，绝不阻塞调用线程。
                    bool pipesClosed = System.Threading.Tasks.Task.WhenAll(soTask, seTask).Wait(5000);
                    if (!pipesClosed) { try { KillTree(p); } catch { } }
                    string so = pipesClosed ? soTask.GetAwaiter().GetResult()
                        : "(output abandoned: a detached GUI child keeps the pipe open)";
                    string se = pipesClosed ? seTask.GetAwaiter().GetResult() : "";
                    if (cancelled)
                    {
                        sb.Append("(cancelled by user)\n");
                    }
                    else if (timedOut)
                    {
                        sb.Append("(timed out after ").Append(timeout).Append("s, killed)\n");
                    }
                    sb.Append("exit=").Append(exited ? p.ExitCode.ToString() : "?").Append('\n');
                    if (so.Length > 0) sb.Append(so).Append('\n');
                    if (se.Length > 0) sb.Append("--- stderr ---\n").Append(se).Append('\n');
                }
            }
            catch (Exception ex) { sb.Append("failed: ").Append(ex.Message); }
            return sb.ToString();
        }

        // ---------- Read (cat -n style) ----------
        private static string Read(Dictionary<string, object> args, Settings cfg)
        {
            string path = GetStr(args, "file_path", "");
            int offset = GetInt(args, "offset", 1);
            if (offset < 1) offset = 1;
            int limit = GetInt(args, "limit", 2000);
            try
            {
                if (!File.Exists(path)) return "File does not exist: " + path;
                string text = DetectRead(path);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var sb = new StringBuilder();
                int last = Math.Min(lines.Length, offset - 1 + limit);
                for (int i = offset; i <= last; i++)
                    sb.Append(i.ToString()).Append('\t').Append(lines[i - 1]).Append('\n');
                string outp = sb.ToString();
                if (outp.Length > cfg.MaxToolResultChars)
                    outp = outp.Substring(0, cfg.MaxToolResultChars) + "\n...(truncated)";
                if (last < lines.Length) outp += "\n(showing lines " + offset + "-" + last + " of " + lines.Length + ")";
                return outp.Length == 0 ? "(empty file)" : outp;
            }
            catch (Exception ex) { return "read failed: " + ex.Message; }
        }

        private static string DetectRead(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try
            {
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch { return Encoding.GetEncoding(936).GetString(bytes); }
        }

        // ---------- Write ----------
        private static string Write(Dictionary<string, object> args)
        {
            string path = GetStr(args, "file_path", "");
            string content = GetStr(args, "content", "");
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content, Encoding.UTF8);
                return "Wrote " + path + " (" + content.Length + " chars)";
            }
            catch (Exception ex) { return "write failed: " + ex.Message; }
        }

        // ---------- Edit ----------
        private static string Edit(Dictionary<string, object> args)
        {
            string path = GetStr(args, "file_path", "");
            string oldS = GetStr(args, "old_string", "");
            string newS = GetStr(args, "new_string", "");
            bool replaceAll = GetBool(args, "replace_all", false);
            try
            {
                if (!File.Exists(path)) return "File does not exist: " + path;
                string text = DetectRead(path);
                if (oldS.Length == 0) return "old_string is empty";
                int count = CountOccurrences(text, oldS);
                if (count == 0) return "old_string not found in file (must match exactly)";
                if (count > 1 && !replaceAll)
                    return "old_string appears " + count + " times; provide more surrounding text or set replace_all=true";
                string outText = replaceAll
                    ? text.Replace(oldS, newS)
                    : text.Substring(0, text.IndexOf(oldS, StringComparison.Ordinal)) + newS
                      + text.Substring(text.IndexOf(oldS, StringComparison.Ordinal) + oldS.Length);
                File.WriteAllText(path, outText, Encoding.UTF8);
                return "Edited " + path + " (" + (replaceAll ? count : 1) + " replacement(s))";
            }
            catch (Exception ex) { return "edit failed: " + ex.Message; }
        }

        private static int CountOccurrences(string text, string sub)
        {
            int c = 0, idx = 0;
            while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0) { c++; idx += sub.Length; }
            return c;
        }

        private static string DecodeHtmlEntities(string s)
        {
            s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
            return Regex.Replace(s, "&#(\\d+);", m =>
            {
                try { return ((char)int.Parse(m.Groups[1].Value)).ToString(); }
                catch { return m.Value; }
            });
        }

        // ---------- Glob ----------
        private static string Glob(Dictionary<string, object> args)
        {
            string pattern = GetStr(args, "pattern", "");
            string path = NormalizeToolPath(GetStr(args, "path", "."));
            try
            {
                var results = new List<string>();
                string dirPart = Path.GetDirectoryName(pattern);
                string filePart = Path.GetFileName(pattern);
                string searchDir = string.IsNullOrEmpty(dirPart) ? path : Path.Combine(path, dirPart);
                if (filePart.IndexOf('*') < 0 && filePart.IndexOf('?') < 0)
                {
                    // literal file
                    if (File.Exists(Path.Combine(searchDir, filePart))) results.Add(Path.Combine(searchDir, filePart));
                }
                else
                {
                    foreach (var f in Directory.GetFiles(searchDir, filePart, SearchOption.AllDirectories))
                        results.Add(f);
                }
                results.Sort();
                if (results.Count == 0) return "(no matches)";
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(200, results.Count); i++) sb.Append(results[i]).Append('\n');
                if (results.Count > 200) sb.Append("...(").Append(results.Count).Append(" total)");
                return sb.ToString();
            }
            catch (Exception ex) { return "glob failed: " + ex.Message; }
        }

        /// <summary>
        /// 归一化模型传入的工具路径：正斜杠→反斜杠、剥掉引号、展开开头的通配符目录，
        /// 相对路径落到工作区根。Glob/Grep 的"路径非法字符"错误多源于此。
        /// </summary>
        private static string NormalizeToolPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.Trim().Trim('"');
            p = p.Replace('/', System.IO.Path.DirectorySeparatorChar);
            // 通配符只允许出现在文件名段；目录段含 * ? 时截断到通配符之前
            int wild = p.IndexOfAny(new[] { '*', '?' });
            if (wild >= 0)
            {
                int sep = p.LastIndexOf(System.IO.Path.DirectorySeparatorChar, wild);
                p = sep >= 0 ? p.Substring(0, sep + 1) : "";
            }
            if (p.Length == 0) p = ".";
            return p;
        }

        // ---------- Grep ----------
        private static string Grep(Dictionary<string, object> args)
        {
            string kw = GetStr(args, "pattern", "");
            string path = NormalizeToolPath(GetStr(args, "path", "."));
            string include = GetStr(args, "include", "*");
            if (kw.Length == 0) return "empty pattern";
            if (!Directory.Exists(path)) { try { path = Directory.GetCurrentDirectory(); } catch { } }
            var sb = new StringBuilder();
            int hits = 0;
            try
            {
                IEnumerable<string> files;
                try { files = Directory.GetFiles(path, include, SearchOption.AllDirectories); }
                catch (ArgumentException) { files = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly); }
                foreach (var f in files)
                {
                    // skip huge binaries
                    try { if (new FileInfo(f).Length > 2 * 1024 * 1024) continue; } catch { continue; }
                    string line;
                    int lineNo = 0;
                    try
                    {
                        using (var sr = new StreamReader(f, true))
                        {
                            while ((line = sr.ReadLine()) != null)
                            {
                                lineNo++;
                                if (line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    sb.Append(f).Append(':').Append(lineNo).Append(": ").Append(line.Trim()).Append('\n');
                                    hits++;
                                    if (hits >= 100) { sb.Append("...(max 100 matches)"); return sb.ToString(); }
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (hits == 0) sb.Append("(no matches)");
            }
            catch (Exception ex) { return "grep failed: " + ex.Message; }
            return sb.ToString();
        }

        // ---------- Todo ----------
        private static string Todo(Dictionary<string, object> args)
        {
            var list = MiniJson.GetArr(args, "todos");
            var newTodos = new List<Dictionary<string, object>>();
            if (list != null)
            {
                foreach (object o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var item = MiniJson.Obj();
                    item["content"] = MiniJson.GetStr(d, "content", "");
                    item["status"] = MiniJson.GetStr(d, "status", "pending");
                    newTodos.Add(item);
                }
            }
            Todos = newTodos;
            if (newTodos.Count == 0) return "Todo list cleared.";
            int done = 0, inprog = 0;
            foreach (var t in newTodos)
            {
                string st = MiniJson.GetStr(t, "status", "");
                if (st == "completed") done++;
                if (st == "in_progress") inprog++;
            }
            return "Todo list updated: " + newTodos.Count + " item(s), " + done + " completed, " + inprog + " in progress.";
        }

        // ---------- Skill ----------
        private static string SkillTool(Dictionary<string, object> args)
        {
            string name = GetStr(args, "name", "");
            string body = Skills.GetBody(name);
            if (body == null)
            {
                var names = new List<string>();
                foreach (var s in Skills.LoadAll()) names.Add(s.Name);
                return "Skill not found: " + name + ". Available: " + string.Join(", ", names.ToArray());
            }
            return "Skill loaded: " + name + "\n\n" + body + "\n\nFollow these instructions to complete the user's request.";
        }

        // ---------- WebFetch ----------
        private static string WebFetch(Dictionary<string, object> args, Settings cfg)
        {
            string url = GetStr(args, "url", "");
            int max = GetInt(args, "max_chars", 8000);
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls;
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.1) ZCode7");
                    string html = wc.DownloadString(url);
                    // strip scripts/styles/tags
                    html = Regex.Replace(html, "(?s)<script.*?</script>", " ");
                    html = Regex.Replace(html, "(?s)<style.*?</style>", " ");
                    html = Regex.Replace(html, "<[^>]+>", " ");
                    html = DecodeHtmlEntities(html);
                    html = Regex.Replace(html, "\\s{3,}", "\n");
                    string outp = ToolsHelper.Trunc(html.Trim(), max);
                    return outp.Length == 0 ? "(empty response)" : outp;
                }
            }
            catch (Exception ex) { return "fetch failed: " + ex.Message; }
        }

        // ---------- SystemInfo ----------
        private static string SystemInfo()
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append("hostname: ").Append(Environment.MachineName).Append('\n');
                sb.Append("user: ").Append(Environment.UserName).Append('\n');
                sb.Append("os: ").Append(Environment.OSVersion.VersionString).Append('\n');
                sb.Append("64bit: ").Append(Environment.Is64BitOperatingSystem).Append('\n');
                sb.Append("cpus: ").Append(Environment.ProcessorCount).Append('\n');
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.IsReady)
                            sb.Append("disk ").Append(d.Name).Append(" free ")
                              .Append((d.AvailableFreeSpace / 1024 / 1024 / 1024)).Append("GB / ")
                              .Append((d.TotalSize / 1024 / 1024 / 1024)).Append("GB\n");
                    }
                    catch { }
                }
                sb.Append("time: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            }
            catch (Exception ex) { return "failed: " + ex.Message; }
            return sb.ToString();
        }
    }

    public static class ToolsHelper
    {
        public static string Trunc(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n...(truncated, " + s.Length + " chars total)";
        }
    }
}
