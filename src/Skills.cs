// ZCode7 - skills system (SKILL.md frontmatter format, ported from ZCode)
// Layout: <AppData>\ZCode7\skills\<skill-folder>\SKILL.md
// SKILL.md: YAML frontmatter (--- name / description / metadata ---) + markdown body
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZCode7
{
    public class SkillInfo
    {
        public string Folder;      // skill folder name
        public string Name;        // frontmatter name (fallback: folder name)
        public string Description; // frontmatter description
        public string Path;        // full SKILL.md path
        public string Body;        // markdown body (after frontmatter)
        public bool Enabled = true;
        public override string ToString() { return Name + " - " + Description; }
    }

    public static class Skills
    {
        private static List<SkillInfo> _cache;
        private static readonly object _lock = new object();

        public static List<SkillInfo> LoadAll(bool forceReload = false)
        {
            lock (_lock)
            {
                if (_cache != null && !forceReload) return _cache;
                _cache = new List<SkillInfo>();
                try
                {
                    // 双目录合并：应用本地（UI 创建/导入目标）优先，官方 ~/.zcode/skills 共享
                    var roots = new List<string> { Settings.SkillsDir };
                    var official = Settings.OfficialSkillsDir;
                    if (!string.IsNullOrEmpty(official) && !roots.Contains(official)) roots.Add(official);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string root in roots)
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (string dir in Directory.GetDirectories(root))
                        {
                            string folder = Path.GetFileName(dir);
                            if (!seen.Add(folder)) continue;   // 同名以先加入的（应用本地）为准
                            string md = Path.Combine(dir, "SKILL.md");
                            if (!File.Exists(md)) continue;
                            var info = Parse(md, folder);
                            if (info != null)
                            {
                                info.Enabled = !DisabledSkills().Contains(folder);
                                _cache.Add(info);
                            }
                        }
                    }
                }
                catch { }
                return _cache;
            }
        }

        private static List<string> _disabled = null;

        /// <summary>禁用技能名单（config.json "disabledSkills"，跨重启持久）。</summary>
        public static List<string> DisabledSkills()
        {
            if (_disabled != null) return _disabled;
            var l = new List<string>();
            try
            {
                string cfgPath = System.IO.Path.Combine(Settings.AppDir, "config.json");
                if (System.IO.File.Exists(cfgPath))
                {
                    var d = MiniJson.ParseObject(System.IO.File.ReadAllText(cfgPath, System.Text.Encoding.UTF8));
                    if (d != null && d.TryGetValue("disabledSkills", out var dv) && dv is List<object> dl)
                        foreach (var x in dl)
                            if (x != null && x.ToString().Length > 0) l.Add(x.ToString());
                }
            }
            catch { }
            _disabled = l;
            return l;
        }

        public static void SetDisabled(string folder, bool disabled)
        {
            var l = DisabledSkills();
            bool changed = false;
            if (disabled && !l.Contains(folder)) { l.Add(folder); changed = true; }
            if (!disabled && l.Remove(folder)) changed = true;
            if (!changed) return;
            _disabled = l;
            _cache = null;
            try
            {
                string cfgPath = System.IO.Path.Combine(Settings.AppDir, "config.json");
                var d = MiniJson.ParseObject(System.IO.File.ReadAllText(cfgPath, System.Text.Encoding.UTF8)) ?? new Dictionary<string, object>();
                var objs = new List<object>();
                foreach (var x in l) objs.Add(x);
                d["disabledSkills"] = objs;
                System.IO.File.WriteAllText(cfgPath, MiniJson.Serialize(d), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static SkillInfo Parse(string mdPath, string folder)
        {
            try
            {
                string text = File.ReadAllText(mdPath, Encoding.UTF8);
                string name = folder;
                string desc = "";
                string body = text;
                if (text.StartsWith("---"))
                {
                    int end = text.IndexOf("\n---", 3);
                    if (end >= 0)
                    {
                        string front = text.Substring(3, end - 3);
                        body = text.Substring(end + 4);
                        name = MatchFront(front, "name") ?? folder;
                        desc = MatchFront(front, "description") ?? "";
                    }
                }
                return new SkillInfo
                {
                    Folder = folder,
                    Name = name.Trim(),
                    Description = desc.Trim(),
                    Path = mdPath,
                    Body = body.Trim()
                };
            }
            catch { return null; }
        }

        private static string MatchFront(string front, string key)
        {
            var m = Regex.Match(front, @"^" + key + @"\s*:\s*(.+)$", RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value.Trim();
            // multi-line description: "> text" continuation
            var m2 = Regex.Match(front, @"^" + key + @"\s*:\s*\n((?:[ \t]+.+\n?)+)", RegexOptions.Multiline);
            if (m2.Success) return m2.Groups[1].Value.Trim();
            return null;
        }

        public static SkillInfo Find(string name)
        {
            foreach (var s in LoadAll())
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Folder, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        // system prompt section listing available skills (name + description, like ZCode)
        public static string BuildSection()
        {
            var all = LoadAll();
            if (all.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append("\n# Skills\n");
            sb.Append("本机已安装以下技能。用户输入 /技能名 或请求匹配某技能描述时，用 Skill 工具加载其完整指导后遵循执行。\n");
            foreach (var s in all)
            {
                if (!s.Enabled) continue;
                sb.Append("- ").Append(s.Name);
                if (s.Description.Length > 0) sb.Append(": ").Append(s.Description);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string GetBody(string name)
        {
            var s = Find(name);
            return s == null ? null : s.Body;
        }

        // create a starter skill on first run
        public static void Defaults(string root)
        {
            var repair = Path.Combine(root, "computer-repair");
            Directory.CreateDirectory(repair);
            var p = Path.Combine(repair, "SKILL.md");
            if (!File.Exists(p))
                File.WriteAllText(p,
                    "---\n" +
                    "name: computer-repair\n" +
                    "description: 诊断和修复 Windows 电脑问题。当用户报告故障、要求检查系统状态、网络、磁盘、服务或进程时使用。\n" +
                    "metadata:\n  author: zcode7\n  version: 1.0.0\n" +
                    "---\n\n" +
                    "# 电脑维修助手\n\n" +
                    "诊断流程：\n" +
                    "1. 先用 SystemInfo 了解系统概况\n" +
                    "2. 用 Bash 执行诊断命令（netstat / tasklist / dir / reg query / wevtutil 查事件日志）\n" +
                    "3. 每步先说明意图，执行后解读结果\n" +
                    "4. 删除、停服务、改注册表等危险操作必须先征得用户同意\n" +
                    "5. 命令输出为 GBK 编码，乱码时注意编码\n", Encoding.UTF8);
        }
    }
}
