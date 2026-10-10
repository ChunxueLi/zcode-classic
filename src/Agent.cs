// ZCode7 - agent loop: streaming model + toolchain + MCP + auto compaction
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZCode7
{
    public class Agent
    {
        private readonly Settings _cfg;
        private readonly ApiClient _api;
        public readonly McpHub Mcp = new McpHub();
        private List<Dictionary<string, object>> _messages = new List<Dictionary<string, object>>();
        private CancellationTokenSource _cts;
        private volatile bool _busy;
        private int _compactFailures;

        public string SessionId = "";
        public string Title = "";

        public event Action<string> OnTextDelta;          // streaming assistant text
        public event Action<string> OnThinkingDelta;      // streaming reasoning text
        public event Action<string> OnAssistantText;      // full assistant text (post-turn)
        public event Action<string, string> OnToolStart;  // toolName, argsJson
        public event Action<string, string> OnToolDone;   // toolName, result
        public event Action<string> OnError;
        public event Action<string> OnStatus;             // status line
        public event Action OnTurnDone;
        public event Action OnTodosChanged;
        public event Action<ApprovalRequest> OnApprovalNeeded;   // permission gate (UI must answer Tcs)

        public class ApprovalRequest
        {
            public string ToolName;
            public string ArgsJson;
            public TaskCompletionSource<bool> Tcs;
        }

        public Agent(Settings cfg)
        {
            _cfg = cfg;
            _api = new ApiClient(cfg);
            _api.OnThinkingDelta += t => { if (OnThinkingDelta != null) OnThinkingDelta(t); };
        }

        public bool IsBusy { get { return _busy; } }

        /// <summary>子代理模式：只暴露只读工具，且不重复派生 Task。</summary>
        public bool ReadOnlyTools;

        private volatile bool _subagentRunning;
        public List<Dictionary<string, object>> Messages { get { return _messages; } }
        public List<Dictionary<string, object>> Todos { get { return Tools.Todos; } }

        public string SystemPrompt()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("你是 ZCode7，运行在用户 Windows 本机上的 AI 编程助手。\n");
            sb.Append("通过调用工具直接完成用户在本机上的任务：执行命令、读写文件、检索、维护任务清单、网页抓取。\n");
            sb.Append("原则：\n");
            sb.Append("1. 先用工具获取事实再回答，不要凭空猜测系统状态。\n");
            sb.Append("2. 危险操作（删除、格式化、改注册表、停服务）必须先征得用户同意。\n");
            sb.Append("3. 命令输出为 GBK 编码，中文乱码时考虑编码问题。\n");
            sb.Append("4. 长任务用 Todo 工具维护进度清单。\n");
            sb.Append("5. 回答用中文，简洁直接，先结论后细节。\n");
            sb.Append("6. 操作原生应用界面：优先 Window.list/UIA tree 摸清结构，再用 UIA 的 invoke/click 等动作执行；");
            sb.Append("UIA 不可用时才用 Mouse/Key 兜底。坐标为物理像素。操作前先看清楚目标，避免点到删除/发送类危险控件。\n");
            sb.Append("当前时间: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("系统: ").Append(Environment.OSVersion.VersionString)
              .Append(" / ").Append(Environment.MachineName)
              .Append(" / ").Append(Environment.UserName).Append('\n');
            sb.Append(Skills.BuildSection());
            return sb.ToString();
        }

        private void EnsureSystemMessage()
        {
            if (_messages.Count == 0 || MiniJson.GetStr(_messages[0], "role", "") != "system")
            {
                var m = MiniJson.Obj();
                m["role"] = "system";
                m["content"] = SystemPrompt();
                _messages.Insert(0, m);
            }
            else _messages[0]["content"] = SystemPrompt();
        }

        public void NewSession()
        {
            _messages = new List<Dictionary<string, object>>();
            Tools.Todos = new List<Dictionary<string, object>>();
            SessionId = "";
            Title = "";
            if (OnTodosChanged != null) OnTodosChanged();
        }

        public void LoadSession(List<Dictionary<string, object>> messages,
            List<Dictionary<string, object>> todos, string sessionId, string title)
        {
            _messages = messages;
            Tools.Todos = todos ?? new List<Dictionary<string, object>>();
            SessionId = sessionId;
            Title = title ?? "";
            if (OnTodosChanged != null) OnTodosChanged();
        }

        public void Cancel()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            // a running Bash command must die too, or "stop" appears to do nothing
            try
            {
                var p = Tools.CurrentProcess;
                if (p != null && !p.HasExited) Tools.KillTree(p);
            }
            catch { }
        }

        private bool Approve(string name, string argsJson)
        {
            string mode = _cfg.PermissionMode;
            if (mode == "yolo") return true;
            bool sensitive = name == "Bash" || name == "Write" || name == "Edit"
                || Mcp.IsMcpTool(name, out _, out _);
            // 电脑操作类：只读动作（看树/列表/截屏）放行，改状态动作（点击/键盘/关窗）需审批
            if (ComputerUse.NeedsApproval(name, ParseArgsSafe(argsJson))) sensitive = true;
            if (mode == "auto" && !sensitive) return true;
            if (OnApprovalNeeded == null) return true;   // headless (apitest/e2e): fail-open
            var req = new ApprovalRequest { ToolName = name, ArgsJson = argsJson };
            req.Tcs = new TaskCompletionSource<bool>();
            try { _cts.Token.Register(() => req.Tcs.TrySetResult(false)); } catch { }
            OnApprovalNeeded(req);
            return req.Tcs.Task.GetAwaiter().GetResult();
        }

        private static Dictionary<string, object> ParseArgsSafe(string json)
        {
            try { return MiniJson.ParseObject(json ?? ""); } catch { return null; }
        }

        public async Task SendAsync(string userText)
        {
            if (_busy) return;
            _busy = true;
            _cts = new CancellationTokenSource();
            Tools.CancelRequested = () => _cts != null && _cts.IsCancellationRequested;
            try
            {
                // 每回合按当前模型解析端点：切到自定义供应商模型后 ApiBase/ApiKey 才会真正路由过去
                RpcServicesRegistry.ResolveCustomApi(_cfg, _cfg.Model);
                EnsureSystemMessage();
                var um = MiniJson.Obj();
                um["role"] = "user";
                um["content"] = userText;
                _messages.Add(um);
                // 回合开始即落盘：官方 UI 模式下会话此前只在原生路径保存，重启即丢
                if (SessionId.Length > 0)
                {
                    try { SessionStore.Save(_messages, SessionId, Title, Tools.Todos); } catch { }
                }

                var tools = Tools.Definitions();
                if (ReadOnlyTools)
                {
                    var ro = new List<string> { "Read", "Glob", "Grep", "WebFetch", "SystemInfo" };
                    tools = tools.Where(t =>
                    {
                        var td = t as Dictionary<string, object>;
                        var fn = td != null ? MiniJson.GetDict(td, "function") : null;
                        return fn != null && ro.Contains(MiniJson.GetStr(fn, "name", ""));
                    }).ToList();
                }
                else if (!_subagentRunning)
                {
                    // Task（子代理）：父回合进行中不再嵌套提供
                    var props = new Dictionary<string, object>();
                    var promptSpec = new Dictionary<string, object>();
                    promptSpec["type"] = "string";
                    promptSpec["description"] = "Full task instructions for the subagent";
                    props["prompt"] = promptSpec;
                    var stSpec = new Dictionary<string, object>();
                    stSpec["type"] = "string";
                    stSpec["enum"] = new List<object> { "general-purpose", "Explore" };
                    stSpec["description"] = "general-purpose=full analysis, Explore=read-only research";
                    props["subagent_type"] = stSpec;
                    var parameters = new Dictionary<string, object>();
                    parameters["type"] = "object";
                    parameters["properties"] = props;
                    parameters["required"] = new List<object> { "prompt" };
                    var fn = new Dictionary<string, object>();
                    fn["name"] = "Task";
                    fn["description"] = "Launch a subagent to handle a self-contained subtask. The subagent reads files and fetches web pages, then returns a final report.";
                    fn["parameters"] = parameters;
                    var tool = new Dictionary<string, object>();
                    tool["type"] = "function";
                    tool["function"] = fn;
                    tools.Add(tool);
                }
                Mcp.MergeTools(tools);

                // titleDone 判定须参考磁盘现有标题：用户通过 UI 手动重命名后
                // agent.Title 内存值仍为空，若不读盘会再次自动生成并覆盖手动标题
                bool titleDone = !string.IsNullOrEmpty(Title);
                if (!titleDone && SessionId.Length > 0)
                {
                    try
                    {
                        foreach (var m0 in SessionStore.List())
                        {
                            if (m0.Id == SessionId)
                            {
                                if (!string.IsNullOrEmpty(m0.Title))
                                {
                                    Title = m0.Title;
                                    titleDone = true;
                                }
                                break;
                            }
                        }
                    }
                    catch { }
                }
                bool gotText = false;

                for (int step = 0; step < 40; step++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    await MaybeAutoCompact();

                    var msg = await _api.StreamChatAsync(_messages, tools,
                        t => { if (OnTextDelta != null) OnTextDelta(t); }, _cts.Token);
                    _messages.Add(msg);

                    var toolCalls = MiniJson.GetArr(msg, "tool_calls");
                    string content = MiniJson.GetStr(msg, "content", "");

                    if (toolCalls == null || toolCalls.Count == 0)
                    {
                        if (content.Length > 0 && OnAssistantText != null) OnAssistantText(content);
                        if (!gotText && content.Length > 0) gotText = true;
                        break;
                    }
                    if (content.Length > 0 && OnAssistantText != null) OnAssistantText(content);
                    gotText = true;

                    foreach (object o in toolCalls)
                    {
                        var tc = o as Dictionary<string, object>;
                        if (tc == null) continue;
                        var fn = MiniJson.GetDict(tc, "function");
                        if (fn == null) continue;
                        string name = MiniJson.GetStr(fn, "name", "");
                        string argsJson = MiniJson.GetStr(fn, "arguments", "{}");
                        string callId = MiniJson.GetStr(tc, "id", "");
                        Dictionary<string, object> args;
                        try { args = MiniJson.ParseObject(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson); }
                        catch (Exception ex)
                        {
                            args = MiniJson.Obj();
                            args["_parse_error"] = ex.Message;
                        }

                        if (OnToolStart != null) OnToolStart(name, argsJson);

                        string result;
                        string server, realName;
                        if (!Approve(name, argsJson))
                        {
                            result = "[denied] The user rejected this tool call. Ask the user how to proceed, or use a safer approach.";
                        }
                        else if (Mcp.IsMcpTool(name, out server, out realName))
                        {
                            result = Mcp.CallTool(server, realName, args);
                        }
                        else if (name == "Task" && !ReadOnlyTools)
                        {
                            result = RunSubagent(argsJson);
                        }
                        else
                        {
                            try { result = Tools.Execute(name, args, _cfg); }
                            catch (Exception ex) { result = "tool exception: " + ex.Message; }
                        }
                        if (OnToolDone != null) OnToolDone(name, result);
                        if (name == "Todo" && OnTodosChanged != null) OnTodosChanged();

                        var tr = MiniJson.Obj();
                        tr["role"] = "tool";
                        tr["tool_call_id"] = callId;
                        tr["content"] = result;
                        _messages.Add(tr);
                    }
                }

                // auto title from first exchange
                if (!titleDone && gotText)
                {
                    try
                    {
                        string t = await _api.CompleteAsync(
                            "为这段对话生成一个简短标题（6-12 个字，中文，不加引号句号）。只输出标题本身。",
                            userText, CancellationToken.None);
                        t = t.Trim().Trim('"', '。', '.');
                        if (t.Length > 0 && t.Length <= 40)
                        {
                            Title = t;
                            SessionStore.SetTitle(SessionId, Title);
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                if (OnError != null) OnError("(已取消)");
            }
            catch (Exception ex)
            {
                ZLog.Log("AGENT: " + ex);
                if (OnError != null) OnError(ex.Message);
            }
            finally
            {
                _busy = false;
                if (SessionId.Length > 0)
                {
                    try { SessionStore.Save(_messages, SessionId, Title, Tools.Todos); } catch { }
                }
                if (OnTurnDone != null) OnTurnDone();
            }
        }

        // ---- compaction ----
        private async Task MaybeAutoCompact()
        {
            if (!_cfg.AutoCompactEnabled) return;
            if (!Compact.HasEnoughMessages(_messages)) return;

            int tokens = Compact.EstimateMessagesTokens(_messages);
            int threshold = Compact.ThresholdTokens(_cfg);
            ZLog.Log("COMPACT-CHECK tokens=" + tokens + " buffer=" + _cfg.BufferTokens + " threshold=" + threshold);
            if (tokens + _cfg.BufferTokens < threshold) return;
            if (_compactFailures >= Compact.MaxConsecutiveFailures) return;

            if (OnStatus != null) OnStatus("上下文接近上限，正在压缩...");
            try
            {
                // microcompact first: truncate old tool results
                int changed = Compact.Microcompact(_messages);
                tokens = Compact.EstimateMessagesTokens(_messages);
                if (tokens + _cfg.BufferTokens < threshold && changed > 0)
                {
                    if (OnStatus != null) OnStatus("已 microcompact " + changed + " 条旧工具结果");
                    return;
                }
                await CompactNow();
                _compactFailures = 0;
            }
            catch (Exception ex)
            {
                _compactFailures++;
                ZLog.Log("COMPACT: " + ex);
                if (OnStatus != null) OnStatus("自动压缩失败 (" + _compactFailures + "): " + ex.Message);
            }
        }

        /// <summary>运行子代理：独立消息上下文 + 只读工具，返回最终报告文本。</summary>
        private string RunSubagent(string argsJson)
        {
            var args = MiniJson.ParseObject(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
            string prompt = MiniJson.GetStr(args, "prompt", "");
            if (prompt.Length == 0) return "[error] Task requires a prompt.";
            _subagentRunning = true;
            try
            {
                var child = new Agent(_cfg) { ReadOnlyTools = true };
                var report = new System.Text.StringBuilder();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                child.OnTextDelta += t => { if (report.Length < 4000) report.Append(t); };
                child.OnAssistantText += t => { report.Clear(); report.Append(t); };
                child.OnError += e => { if (report.Length == 0) report.Append("[subagent error] " + e); };
                child.OnToolStart += (tool, a) => ZLog.Log("subagent +" + (int)sw.Elapsed.TotalSeconds + "s tool=" + tool);
                child.OnTurnDone += () => ZLog.Log("subagent +" + (int)sw.Elapsed.TotalSeconds + "s child-turn-done, report=" + report.Length + " chars");
                child.NewSession();
                // 后台跑子代理，父取消令牌触发时级联取消（否则 stop 停不掉子代理）
                var run = Task.Run(() => child.SendAsync(prompt));
                while (!run.Wait(400))
                {
                    if (_cts != null && _cts.Token.IsCancellationRequested)
                    {
                        child.Cancel();
                    }
                }
                run.GetAwaiter().GetResult();
                string text = report.ToString();
                return text.Length > 0 ? text : "[subagent] finished with no output";
            }
            catch (Exception ex)
            {
                return "[subagent error] " + ex.Message;
            }
            finally
            {
                _subagentRunning = false;
            }
        }

        public async Task<string> CompactNow()
        {
            if (_messages.Count < 4) return "(会话很短，无需压缩)";
            if (OnStatus != null) OnStatus("正在压缩上下文...");
            string transcript = Compact.BuildTranscript(_messages);
            string raw = await _api.CompleteAsync(Compact.NoToolsPreamble, Compact.BuildSummaryPrompt(transcript), CancellationToken.None);
            string summary = Compact.ExtractSummary(raw);
            string before = Compact.EstimateMessagesTokens(_messages) + " tokens";

            var sys = MiniJson.Obj();
            sys["role"] = "system";
            sys["content"] = SystemPrompt();
            _messages = new List<Dictionary<string, object>>();
            _messages.Add(sys);
            var sm = MiniJson.Obj();
            sm["role"] = "assistant";
            sm["content"] = "[会话已压缩。以下是此前对话的摘要，据此继续工作。]\n\n" + summary;
            _messages.Add(sm);
            if (OnStatus != null) OnStatus("上下文已压缩: " + before + " → " + Compact.EstimateMessagesTokens(_messages) + " tokens");
            return "已压缩 (" + before + " → " + Compact.EstimateMessagesTokens(_messages) + " tokens)";
        }
    }
}
