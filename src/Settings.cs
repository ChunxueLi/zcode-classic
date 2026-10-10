// ZCode7 - settings (config.json in %APPDATA%\ZCode7)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace ZCode7
{
    public class Settings
    {
        public string ApiBase = "";  // 每回合由 ResolveCustomApi 按配置的自定义供应商解析，无内置写死端点
        public string ApiKey = "";
        public string Model = "";  // 由配置 "model" 驱动（UI 设置页/选择器写入）
        public List<string> Models = new List<string>();
        public bool ThinkingEnabled = true;
        // ZCode zcode-builtin.json: GLM-5.3 family reasoningLevel values = off|low|high|max
        // (anthropic map: off->thinking.type=disabled, else thinking.type=adaptive + output_config.effort)
        public string ThinkingLevel = "max";
        // permission modes like ZCode composer: auto (smart confirm) / build (manual confirm) / yolo (full access)
        public string PermissionMode = "auto";
        public double Temperature = 0.6;
        public int MaxCommandTimeoutSec = 120;
        public int MaxToolResultChars = 20000;
        // compaction (ported from ZCode compact/policy.ts defaults)
        public bool AutoCompactEnabled = true;
        public int ContextWindowTokens = 200000;
        public int OutputReserveTokens = 32000;
        public int BufferTokens = 13000;
        public int ThresholdPercent = 100;
        public Dictionary<string, Dictionary<string, object>> McpServers
            = new Dictionary<string, Dictionary<string, object>>();

        public static string AppDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ZCode7");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string SessionsDir
        {
            get
            {
                string d = Path.Combine(AppDir, "sessions");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string SkillsDir
        {
            get
            {
                string d = Path.Combine(AppDir, "skills");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>官方 ZCode 的技能目录（~/.zcode/skills），与官方 CLI 共享。</summary>
        public static string OfficialSkillsDir
        {
            get
            {
                try
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string d = Path.Combine(home, ".zcode", "skills");
                    if (Directory.Exists(d)) return d;
                }
                catch { }
                return null;
            }
        }

        /// <summary>官方 ZCode 的 MCP 配置（~/.zcode/cli/config.json 的 mcp.servers），官方格式兼容。</summary>
        public static Dictionary<string, Dictionary<string, object>> OfficialMcpServers()
        {
            var r = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string p = Path.Combine(home, ".zcode", "cli", "config.json");
                if (!File.Exists(p)) return r;
                var root = MiniJson.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                var mcp = root != null && root.TryGetValue("mcp", out var mv) && mv is Dictionary<string, object> md ? md : null;
                var servers = mcp != null && mcp.TryGetValue("servers", out var sv) && sv is Dictionary<string, object> sd ? sd : null;
                if (servers == null) return r;
                foreach (var kv in servers)
                {
                    if (kv.Value is Dictionary<string, object> v) r[kv.Key] = v;
                }
            }
            catch { }
            return r;
        }

        private static string ConfigPath
        { get { return Path.Combine(AppDir, "config.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var d = MiniJson.ParseObject(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (d != null)
                    {
                        s.ApiBase = MiniJson.GetStr(d, "apiBase", s.ApiBase);
                        s.ApiKey = MiniJson.GetStr(d, "apiKey", s.ApiKey);
                        s.Model = MiniJson.GetStr(d, "model", s.Model);
                        if (d.TryGetValue("models", out var ml) && ml is List<object> mlist)
                        {
                            var ids = mlist.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                            if (ids.Count > 0) s.Models = ids;
                        }
                        s.ThinkingLevel = MiniJson.GetStr(d, "thinkingLevel", s.ThinkingLevel);
                        if (s.ThinkingLevel != "off" && s.ThinkingLevel != "low"
                            && s.ThinkingLevel != "high" && s.ThinkingLevel != "max")
                            s.ThinkingLevel = "max";
                        s.PermissionMode = MiniJson.GetStr(d, "permissionMode", s.PermissionMode);
                        if (s.PermissionMode != "auto" && s.PermissionMode != "build" && s.PermissionMode != "yolo")
                            s.PermissionMode = "auto";
                        double t;
                        if (double.TryParse(MiniJson.GetStr(d, "temperature", ""), out t)) s.Temperature = t;
                        int c;
                        if (int.TryParse(MiniJson.GetStr(d, "maxCommandTimeoutSec", ""), out c)) s.MaxCommandTimeoutSec = c;
                        int m;
                        if (int.TryParse(MiniJson.GetStr(d, "maxToolResultChars", ""), out m)) s.MaxToolResultChars = m;
                        bool ac;
                        if (bool.TryParse(MiniJson.GetStr(d, "autoCompactEnabled", ""), out ac)) s.AutoCompactEnabled = ac;
                        int cw;
                        if (int.TryParse(MiniJson.GetStr(d, "contextWindowTokens", ""), out cw)) s.ContextWindowTokens = cw;
                        int or2;
                        if (int.TryParse(MiniJson.GetStr(d, "outputReserveTokens", ""), out or2)) s.OutputReserveTokens = or2;
                        int bf;
                        if (int.TryParse(MiniJson.GetStr(d, "bufferTokens", ""), out bf)) s.BufferTokens = bf;
                        int tp;
                        if (int.TryParse(MiniJson.GetStr(d, "thresholdPercent", ""), out tp)) s.ThresholdPercent = tp;
                        var mcps = MiniJson.GetDict(d, "mcpServers");
                        if (mcps != null)
                        {
                            var conv = new Dictionary<string, Dictionary<string, object>>();
                            foreach (var kv in mcps)
                            {
                                var v = kv.Value as Dictionary<string, object>;
                                if (v != null) conv[kv.Key] = v;
                            }
                            s.McpServers = conv;
                        }
                        // 合并官方 ~/.zcode/cli/config.json 的 mcp.servers（应用配置优先；无条件执行）
                        foreach (var okv in OfficialMcpServers())
                        {
                            if (!s.McpServers.ContainsKey(okv.Key)) s.McpServers[okv.Key] = okv.Value;
                        }
                    }
                }
                else s.Save();
            }
            catch { }
            return s;
        }

        public void CopyFrom(Settings o)
        {
            ApiBase = o.ApiBase;
            ApiKey = o.ApiKey;
            Model = o.Model;
            ThinkingEnabled = o.ThinkingEnabled;
            ThinkingLevel = o.ThinkingLevel;
            PermissionMode = o.PermissionMode;
            Temperature = o.Temperature;
            MaxCommandTimeoutSec = o.MaxCommandTimeoutSec;
            MaxToolResultChars = o.MaxToolResultChars;
            AutoCompactEnabled = o.AutoCompactEnabled;
            ContextWindowTokens = o.ContextWindowTokens;
            OutputReserveTokens = o.OutputReserveTokens;
            BufferTokens = o.BufferTokens;
            ThresholdPercent = o.ThresholdPercent;
            McpServers = o.McpServers;
        }

        public void Save()
        {
            // Preserve unknown keys (customProviders etc.) or saves wipe them
            var d = MiniJson.Obj();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var prev = MiniJson.ParseObject(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (prev != null)
                        foreach (var kv in prev)
                            if (kv.Key != "mcpServers") d[kv.Key] = kv.Value;
                }
            }
            catch { }
            d["apiBase"] = ApiBase;
            d["apiKey"] = ApiKey;
            d["model"] = Model;
            if (Models.Count > 0) d["models"] = Models.Select(x => (object)x).ToList();
            d["thinkingLevel"] = ThinkingLevel;
            d["permissionMode"] = PermissionMode;
            d["temperature"] = Temperature;
            d["maxCommandTimeoutSec"] = (long)MaxCommandTimeoutSec;
            d["maxToolResultChars"] = (long)MaxToolResultChars;
            d["autoCompactEnabled"] = AutoCompactEnabled;
            d["contextWindowTokens"] = (long)ContextWindowTokens;
            d["outputReserveTokens"] = (long)OutputReserveTokens;
            d["bufferTokens"] = (long)BufferTokens;
            d["thresholdPercent"] = (long)ThresholdPercent;
            d["mcpServers"] = McpServers;
            File.WriteAllText(ConfigPath, MiniJson.Serialize(d), Encoding.UTF8);
        }
    }
}
