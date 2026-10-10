// ZCode7 - MCP (Model Context Protocol) stdio client
// Connects to local MCP servers (newline-delimited JSON-RPC over stdio),
// merges their tools into the agent toolset.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZCode7
{
    public class McpServerConn : IDisposable
    {
        private Process _p;
        private StreamWriter _stdin;
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending
            = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly object _lock = new object();
        public string LastError = "";
        public List<Dictionary<string, object>> Tools = new List<Dictionary<string, object>>();
        public bool Connected;

        public void Start(string command, string args, Dictionary<string, object> env)
        {
            var psi = new ProcessStartInfo();
            // Windows 上 npx/tsc 等 .cmd 包装器无法被 Process.Start 直接解析，统一走 cmd /c
            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            if (isWindows)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + command + " " + (args ?? "");
                psi.UseShellExecute = false;
            }
            else
            {
                psi.FileName = command;
                psi.Arguments = args ?? "";
                psi.UseShellExecute = false;
            }
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = EncodingHelper.Utf8NoBom;
            if (env != null)
            {
                foreach (var kv in env)
                {
                    try { psi.EnvironmentVariables[kv.Key] = kv.Value == null ? "" : kv.Value.ToString(); }
                    catch { }
                }
            }
            _p = Process.Start(psi);
            _stdin = _p.StandardInput;
            _stdin.AutoFlush = true;

            var reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Start();
            var errT = new Thread(() =>
            {
                try
                {
                    string el;
                    while ((el = _p.StandardError.ReadLine()) != null)
                    {
                        if (el.Length > 0) ZLog.Log("MCP stderr: " + (el.Length > 120 ? el.Substring(0, 120) : el));
                    }
                }
                catch { }
            });
            errT.IsBackground = true;
            errT.Start();
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _p.StandardOutput.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (line.Length > 2) ZLog.Log("MCP << " + (line.Length > 100 ? line.Substring(0, 100) : line));
                    Dictionary<string, object> msg = null;
                    try { msg = MiniJson.ParseObject(line); } catch { continue; }
                    if (msg == null) continue;
                    object idObj;
                    if (msg.TryGetValue("id", out idObj) && idObj != null)
                    {
                        int id;
                        if (int.TryParse(idObj.ToString(), out id))
                        {
                            TaskCompletionSource<Dictionary<string, object>> tcs = null;
                            lock (_lock)
                            {
                                if (_pending.TryGetValue(id, out tcs))
                                {
                                    _pending.Remove(id);
                                    var err = MiniJson.GetDict(msg, "error");
                                    if (err != null)
                                        tcs.TrySetException(new Exception("MCP error: " + MiniJson.Serialize(err)));
                                    else
                                        tcs.TrySetResult(msg);
                                }
                            }
                        }
                    }
                    // notifications (notifications/*) ignored for now
                }
            }
            catch (Exception ex)
            {
                LastError = "read loop: " + ex.Message;
            }
        }

        private Dictionary<string, object> Request(string method, Dictionary<string, object> @params, int timeoutMs)
        {
            int id;
            var tcs = SendInternal(method, @params, out id);
            if (!tcs.Task.Wait(timeoutMs))
                throw new Exception("MCP 请求超时: " + method);
            return tcs.Task.Result;
        }

        private TaskCompletionSource<Dictionary<string, object>> SendInternal(
            string method, Dictionary<string, object> @params, out int idOut)
        {
            int id;
            lock (_lock) { id = ++_idCounter; }
            idOut = id;
            var req = MiniJson.Obj();
            req["jsonrpc"] = "2.0";
            req["id"] = (long)id;
            req["method"] = method;
            if (@params != null) req["params"] = @params;
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            lock (_lock) { _pending[id] = tcs; }
            _stdin.WriteLine(MiniJson.Serialize(req));
            ZLog.Log("MCP >> " + method + " id=" + id);
            return tcs;
        }

        private int _idCounter = 0;

        public void Initialize()
        {
            var p = MiniJson.Obj();
            p["protocolVersion"] = "2024-11-05";
            var caps = MiniJson.Obj();
            p["capabilities"] = caps;
            var info = MiniJson.Obj();
            info["name"] = "ZCode7";
            info["version"] = "1.0.0";
            p["clientInfo"] = info;
            Request("initialize", p, 60000);  // npx 冷启动需下载包，15s 不够
            // notification: initialized (no id, no response expected)
            var n = MiniJson.Obj();
            n["jsonrpc"] = "2.0";
            n["method"] = "notifications/initialized";
            _stdin.WriteLine(MiniJson.Serialize(n));

            var resp = Request("tools/list", MiniJson.Obj(), 30000);
            // JSON-RPC 包一层：工具数组在 result.tools 里
            var resultDict = MiniJson.GetDict(resp, "result");
            var arr = MiniJson.GetArr(resultDict, "tools");
            Tools.Clear();
            if (arr != null)
                foreach (object o in arr)
                {
                    var t = o as Dictionary<string, object>;
                    if (t != null) Tools.Add(t);
                }
            Connected = true;
        }

        // Returns concatenated text content of the tool call result.
        public string CallTool(string toolName, Dictionary<string, object> arguments)
        {
            var p = MiniJson.Obj();
            p["name"] = toolName;
            p["arguments"] = arguments ?? MiniJson.Obj();
            var resp = Request("tools/call", p, 60000);
            var result = MiniJson.GetDict(resp, "result");
            if (result == null) return "(空结果)";
            bool isError = false;
            object errFlag;
            if (result.TryGetValue("isError", out errFlag) && errFlag is bool) isError = (bool)errFlag;
            var content = MiniJson.GetArr(result, "content");
            var sb = new System.Text.StringBuilder();
            if (isError) sb.Append("[MCP 工具报错] ");
            if (content != null)
            {
                bool first = true;
                foreach (object o in content)
                {
                    var c = o as Dictionary<string, object>;
                    if (c == null) continue;
                    string type = MiniJson.GetStr(c, "type", "text");
                    if (!first) sb.Append('\n');
                    first = false;
                    if (type == "text") sb.Append(MiniJson.GetStr(c, "text", ""));
                    else sb.Append("(" + type + " 内容，" + MiniJson.Serialize(c).Length + " 字节，略)");
                }
            }
            string s = sb.ToString();
            return s.Length == 0 ? "(空结果)" : ToolsHelper.Trunc(s, 20000);
        }

        public void Dispose()
        {
            try { if (_stdin != null) _stdin.WriteLine(MiniJson.Serialize(BuildNotify("shutdown"))); } catch { }
            try { if (_p != null && !_p.HasExited) _p.Kill(); } catch { }
            try { if (_p != null) _p.Dispose(); } catch { }
        }

        private Dictionary<string, object> BuildNotify(string method)
        {
            var n = MiniJson.Obj();
            n["jsonrpc"] = "2.0";
            n["method"] = method;
            return n;
        }
    }

    public static class EncodingHelper
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    }

    // Manages all configured MCP servers.
    public class McpHub : IDisposable
    {
        private readonly Dictionary<string, McpServerConn> _conns
            = new Dictionary<string, McpServerConn>();
        public List<string> Errors = new List<string>();

        public void ConnectAll(Settings cfg)
        {
            DisposeAll();
            Errors.Clear();
            foreach (var kv in cfg.McpServers)
            {
                string serverName = kv.Key;
                var conf = kv.Value;
                string cmd = MiniJson.GetStr(conf, "command", "");
                if (cmd.Trim().Length == 0) continue;
                // args 兼容官方数组格式（["-x","y"]）与字符串格式；GetStr 对数组会返回 ToString 垃圾，必须先查数组
                string args = "";
                if (conf.TryGetValue("args", out var av) && av is List<object> al && al.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var a in al)
                    {
                        var t = a != null ? a.ToString() : "";
                        if (t.Length == 0) continue;
                        parts.Add(t.IndexOf(' ') >= 0 ? "\"" + t + "\"" : t);
                    }
                    args = string.Join(" ", parts);
                }
                else
                {
                    args = MiniJson.GetStr(conf, "args", "");
                }
                var env = MiniJson.GetDict(conf, "env");
                try
                {
                    var conn = new McpServerConn();
                    conn.Start(cmd, args, env);
                    conn.Initialize();
                    _conns[serverName] = conn;
                    ZLog.Log("MCP connect ok: " + serverName + " " + cmd + " " + args
                        + " tools=" + conn.Tools.Count);
                }
                catch (Exception ex)
                {
                    Errors.Add(serverName + ": " + ex.Message);
                    ZLog.Log("MCP connect FAIL: " + serverName + " " + ex.Message);
                }
            }
        }

        // merge MCP tools (prefixed) into the tool list
        public void MergeTools(List<object> toolDefs)
        {
            foreach (var kv in _conns)
            {
                foreach (var t in kv.Value.Tools)
                {
                    string name = MiniJson.GetStr(t, "name", "");
                    string desc = MiniJson.GetStr(t, "description", "");
                    var schema = MiniJson.GetDict(t, "inputSchema");
                    if (name.Length == 0) continue;
                    var fn = MiniJson.Obj();
                    fn["name"] = "mcp_" + kv.Key + "_" + name;
                    fn["description"] = "[MCP:" + kv.Key + "] " + desc;
                    fn["parameters"] = schema ?? MiniJson.Obj();
                    var wrap = MiniJson.Obj();
                    wrap["type"] = "function";
                    wrap["function"] = fn;
                    toolDefs.Add(wrap);
                }
            }
        }

        public bool IsMcpTool(string toolName, out string server, out string realName)
        {
            server = null; realName = null;
            if (!toolName.StartsWith("mcp_")) return false;
            int i = toolName.IndexOf('_', 4);
            if (i < 0) return false;
            server = toolName.Substring(4, i - 4);
            realName = toolName.Substring(i + 1);
            return _conns.ContainsKey(server);
        }

        public string CallTool(string server, string toolName, Dictionary<string, object> args)
        {
            McpServerConn conn;
            if (!_conns.TryGetValue(server, out conn)) return "MCP 服务器未连接: " + server;
            try { return conn.CallTool(toolName, args); }
            catch (Exception ex) { return "MCP 调用失败: " + ex.Message; }
        }

        public void DisposeAll()
        {
            foreach (var c in _conns.Values) { try { c.Dispose(); } catch { } }
            _conns.Clear();
        }

        public void Dispose() { DisposeAll(); }
    }
}
