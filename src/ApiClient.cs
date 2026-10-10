// ZCode7 - chat API client with SSE streaming: OpenAI-compatible + Anthropic-compatible
// (auto-detected by /anthropic in base URL). Non-streaming kept for compaction/titles.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZCode7
{
    public class ApiClient
    {
        public event Action<string> OnThinkingDelta;   // streaming reasoning text

        private readonly Settings _cfg;

        public ApiClient(Settings cfg) { _cfg = cfg; }

        public bool IsAnthropic
        { get { return (_cfg.ApiBase ?? "").ToLowerInvariant().Contains("/anthropic"); } }

        public class ModelInfo
        {
            public string Id;
            public string Name;
            public override string ToString() { return Id; }
        }

        public static bool LooksLikeZhipu(string apiBase)
        {
            string h = (apiBase ?? "").ToLowerInvariant();
            return h.Contains("bigmodel.cn") || h.Contains("zhipu");
        }

        /// <summary>GET {base}/v1/models (anthropic) or /models (openai-style). Throws on failure.</summary>
        public static List<ModelInfo> FetchModels(string apiBase, string apiKey)
        {
            bool anthropic = (apiBase ?? "").ToLowerInvariant().Contains("/anthropic");
            string b = (apiBase ?? "").TrimEnd('/');
            string url = b + (anthropic ? "/v1/models?limit=100" : "/models");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
                | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                if (anthropic)
                {
                    req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                var resp = client.SendAsync(req).GetAwaiter().GetResult();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " " + body.Substring(0, Math.Min(160, body.Length)));
                var d = MiniJson.ParseObject(body);
                if (d == null) throw new Exception("response is not a JSON object");
                var data = d.ContainsKey("data") ? d["data"] as List<object> : null;
                if (data == null) throw new Exception("no 'data' array in response");
                var list = new List<ModelInfo>();
                foreach (var it in data)
                {
                    var m = it as Dictionary<string, object>;
                    if (m == null) continue;
                    var mi = new ModelInfo();
                    mi.Id = MiniJson.GetStr(m, "id", "");
                    mi.Name = MiniJson.GetStr(m, "display_name", "");
                    if (string.IsNullOrEmpty(mi.Name)) mi.Name = MiniJson.GetStr(m, "name", mi.Id);
                    if (!string.IsNullOrEmpty(mi.Id)) list.Add(mi);
                }
                return list;
            }
        }

        public string Endpoint
        {
            get
            {
                string b = (_cfg.ApiBase ?? "").TrimEnd('/');
                if (IsAnthropic)
                {
                    if (b.EndsWith("/messages")) return b;
                    if (b.EndsWith("/v1")) return b + "/messages";
                    return b + "/v1/messages";
                }
                if (b.EndsWith("/chat/completions")) return b;
                return b + "/chat/completions";
            }
        }

        private HttpClient NewClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
                | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.Timeout = TimeSpan.FromSeconds(300);
            return client;
        }

        private HttpRequestMessage NewRequest(string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey);
            if (IsAnthropic)
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        private void RequireKey()
        {
            if (string.IsNullOrEmpty(_cfg.ApiKey))
                throw new Exception("尚未配置 API Key。请点击工具栏【设置】。");
        }

        // ---------- streaming chat ----------
        // onTextDelta receives incremental assistant text as it arrives.
        // Returns the final assistant message in canonical OpenAI format
        // (role/content/tool_calls), suitable for appending to message history.
        public async Task<Dictionary<string, object>> StreamChatAsync(
            List<Dictionary<string, object>> messages,
            List<object> tools,
            Action<string> onTextDelta,
            CancellationToken ct)
        {
            RequireKey();
            string bodyJson = IsAnthropic
                ? BuildAnthropicBody(messages, tools, true)
                : BuildOpenAIBody(messages, tools, true);

            using (var client = NewClient())
            using (var req = NewRequest(bodyJson))
            using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if ((int)resp.StatusCode < 200 || (int)resp.StatusCode >= 300)
                {
                    string errText = await resp.Content.ReadAsStringAsync();
                    ZLog.Log("API-FAIL " + (int)resp.StatusCode + " endpoint=" + Endpoint
                        + " bodyLen=" + (bodyJson == null ? 0 : bodyJson.Length)
                        + " bodyHead=" + (bodyJson == null ? "" : bodyJson.Substring(0, Math.Min(300, bodyJson.Length))));
                    throw new Exception("API " + (int)resp.StatusCode + ": " + ToolsHelper.Trunc(errText, 1500));
                }
                using (var stream = await resp.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return IsAnthropic
                        ? ConsumeAnthropicSse(reader, onTextDelta, ct)
                        : ConsumeOpenAISse(reader, onTextDelta, ct);
                }
            }
        }

        private Dictionary<string, object> ConsumeOpenAISse(StreamReader reader, Action<string> onText, CancellationToken ct)
        {
            var content = new StringBuilder();
            // index -> {id, name, args}
            var callIdx = new Dictionary<int, Dictionary<string, object>>();
            var callOrder = new List<int>();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:")) continue;
                string data = line.Substring(5).Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;
                Dictionary<string, object> evt;
                try { evt = MiniJson.ParseObject(data); } catch { continue; }
                if (evt == null) continue;
                var choices = MiniJson.GetArr(evt, "choices");
                if (choices == null || choices.Count == 0) continue;
                var ch = choices[0] as Dictionary<string, object>;
                var delta = MiniJson.GetDict(ch, "delta");
                if (delta != null)
                {
                    object dc;
                    if (delta.TryGetValue("content", out dc) && dc is string && ((string)dc).Length > 0)
                    {
                        content.Append((string)dc);
                        if (onText != null) onText((string)dc);
                    }
                    object rc;
                    if (delta.TryGetValue("reasoning_content", out rc) && rc is string && ((string)rc).Length > 0
                        && OnThinkingDelta != null)
                        OnThinkingDelta((string)rc);
                    var tcs = MiniJson.GetArr(delta, "tool_calls");
                    if (tcs != null)
                        foreach (object o in tcs)
                        {
                            var tc = o as Dictionary<string, object>;
                            if (tc == null) continue;
                            int idx = GetIntLocal(tc, "index", 0);
                            Dictionary<string, object> slot;
                            if (!callIdx.TryGetValue(idx, out slot))
                            {
                                slot = MiniJson.Obj();
                                slot["id"] = "";
                                slot["name"] = "";
                                slot["args"] = new StringBuilder();
                                callIdx[idx] = slot;
                                callOrder.Add(idx);
                            }
                            string id = MiniJson.GetStr(tc, "id", "");
                            if (id.Length > 0) slot["id"] = id;
                            var fn = MiniJson.GetDict(tc, "function");
                            if (fn != null)
                            {
                                string n = MiniJson.GetStr(fn, "name", "");
                                if (n.Length > 0) slot["name"] = slot["name"] + n;
                                string a = MiniJson.GetStr(fn, "arguments", "");
                                ((StringBuilder)slot["args"]).Append(a);
                            }
                        }
                }
            }
            return BuildResult(content, callIdx, callOrder);
        }

        private Dictionary<string, object> ConsumeAnthropicSse(StreamReader reader, Action<string> onText, CancellationToken ct)
        {
            var content = new StringBuilder();
            var toolCalls = new List<object>();
            int callSeq = 0;
            int currentBlock = -1;
            bool currentIsTool = false;
            string currentToolId = null, currentToolName = null;
            var currentArgs = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:")) continue;
                string data = line.Substring(5).Trim();
                if (data.Length == 0) continue;
                Dictionary<string, object> evt;
                try { evt = MiniJson.ParseObject(data); } catch { continue; }
                if (evt == null) continue;
                string type = MiniJson.GetStr(evt, "type", "");
                if (type == "content_block_start")
                {
                    var cb = MiniJson.GetDict(evt, "content_block");
                    currentBlock = GetIntLocal(evt, "index", 0);
                    string btype = MiniJson.GetStr(cb, "type", "text");
                    currentIsTool = btype == "tool_use";
                    currentToolId = MiniJson.GetStr(cb, "id", "toolu_0");
                    currentToolName = MiniJson.GetStr(cb, "name", "");
                    currentArgs.Clear();
                }
                else if (type == "content_block_delta")
                {
                    var delta = MiniJson.GetDict(evt, "delta");
                    string dtype = MiniJson.GetStr(delta, "type", "");
                    if (dtype == "text_delta")
                    {
                        string t = MiniJson.GetStr(delta, "text", "");
                        content.Append(t);
                        if (onText != null) onText(t);
                    }
                    else if (dtype == "thinking_delta")
                    {
                        string t = MiniJson.GetStr(delta, "thinking", "");
                        if (t.Length > 0 && OnThinkingDelta != null) OnThinkingDelta(t);
                    }
                    else if (dtype == "input_json_delta")
                    {
                        currentArgs.Append(MiniJson.GetStr(delta, "partial_json", ""));
                    }
                }
                else if (type == "content_block_stop")
                {
                    if (currentIsTool)
                    {
                        callSeq++;
                        var fn = MiniJson.Obj();
                        fn["name"] = currentToolName ?? "";
                        fn["arguments"] = currentArgs.Length == 0 ? "{}" : currentArgs.ToString();
                        var tc = MiniJson.Obj();
                        tc["id"] = currentToolId ?? ("toolu_" + callSeq);
                        tc["type"] = "function";
                        tc["function"] = fn;
                        toolCalls.Add(tc);
                    }
                    currentIsTool = false;
                }
                else if (type == "message_stop")
                {
                    break;
                }
                else if (type == "error")
                {
                    var err = MiniJson.GetDict(evt, "error");
                    throw new Exception("Anthropic stream error: " + MiniJson.Serialize(err ?? evt));
                }
            }
            var msg = MiniJson.Obj();
            msg["role"] = "assistant";
            msg["content"] = content.ToString();
            if (toolCalls.Count > 0) msg["tool_calls"] = toolCalls;
            return msg;
        }

        private static int GetIntLocal(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                if (v is long) return checked((int)(long)v);
                if (v is int) return (int)v;
                if (v is double) return (int)Convert.ToDouble(v);
            }
            return def;
        }

        private static Dictionary<string, object> BuildResult(
            StringBuilder content,
            Dictionary<int, Dictionary<string, object>> callIdx,
            List<int> callOrder)
        {
            var msg = MiniJson.Obj();
            msg["role"] = "assistant";
            msg["content"] = content.ToString();
            if (callOrder.Count > 0)
            {
                callOrder.Sort();
                var toolCalls = new List<object>();
                foreach (int idx in callOrder)
                {
                    var slot = callIdx[idx];
                    var fn = MiniJson.Obj();
                    fn["name"] = (string)slot["name"];
                    fn["arguments"] = ((StringBuilder)slot["args"]).ToString();
                    var tc = MiniJson.Obj();
                    tc["id"] = string.IsNullOrEmpty((string)slot["id"]) ? ("call_" + idx) : (string)slot["id"];
                    tc["type"] = "function";
                    tc["function"] = fn;
                    toolCalls.Add(tc);
                }
                msg["tool_calls"] = toolCalls;
            }
            return msg;
        }

        // ---------- non-streaming (compaction, titles) ----------
        public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            RequireKey();
            string bodyJson = IsAnthropic
                ? BuildAnthropicBody(PairMessages(systemPrompt, userPrompt), null, false)
                : BuildOpenAIBody(PairMessages(systemPrompt, userPrompt), null, false);
            using (var client = NewClient())
            using (var req = NewRequest(bodyJson))
            using (var resp = await client.SendAsync(req, ct))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if ((int)resp.StatusCode < 200 || (int)resp.StatusCode >= 300)
                    throw new Exception("API " + (int)resp.StatusCode + ": " + ToolsHelper.Trunc(text, 1500));
                var msg = IsAnthropic ? ParseAnthropicNonStream(text) : ParseOpenAINonStream(text);
                object c;
                msg.TryGetValue("content", out c);
                return c == null ? "" : c.ToString();
            }
        }

        private static List<Dictionary<string, object>> PairMessages(string sys, string user)
        {
            var msgs = new List<Dictionary<string, object>>();
            var m1 = MiniJson.Obj(); m1["role"] = "system"; m1["content"] = sys;
            var m2 = MiniJson.Obj(); m2["role"] = "user"; m2["content"] = user;
            msgs.Add(m1); msgs.Add(m2);
            return msgs;
        }

        private static Dictionary<string, object> ParseOpenAINonStream(string text)
        {
            var root = MiniJson.ParseObject(text);
            var choices = MiniJson.GetArr(root, "choices");
            if (choices == null || choices.Count == 0)
                throw new Exception("API 返回异常(无 choices): " + ToolsHelper.Trunc(text, 800));
            var ch = choices[0] as Dictionary<string, object>;
            var msg = MiniJson.GetDict(ch, "message");
            if (msg == null) throw new Exception("API 返回异常(无 message)");
            var copy = new Dictionary<string, object>(msg);
            copy["role"] = "assistant";
            return copy;
        }

        private static Dictionary<string, object> ParseAnthropicNonStream(string text)
        {
            var root = MiniJson.ParseObject(text);
            var blocks = MiniJson.GetArr(root, "content");
            if (blocks == null)
                throw new Exception("Anthropic 返回异常(无 content): " + ToolsHelper.Trunc(text, 800));
            var sb = new StringBuilder();
            foreach (object o in blocks)
            {
                var b = o as Dictionary<string, object>;
                if (b == null) continue;
                if (MiniJson.GetStr(b, "type", "") == "text")
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(MiniJson.GetStr(b, "text", ""));
                }
            }
            var msg = MiniJson.Obj();
            msg["role"] = "assistant";
            msg["content"] = sb.ToString();
            return msg;
        }

        // ---------- body builders (shared stream/non-stream) ----------
        private string BuildOpenAIBody(List<Dictionary<string, object>> messages, List<object> tools, bool stream)
        {
            var body = MiniJson.Obj();
            body["model"] = _cfg.Model;
            body["messages"] = messages;
            body["temperature"] = _cfg.Temperature;
            body["stream"] = stream;
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }
            return MiniJson.Serialize(body);
        }

        private string BuildAnthropicBody(List<Dictionary<string, object>> messages, List<object> tools, bool stream)
        {
            var wireMsgs = new List<object>();
            var sysParts = new List<string>();

            int i = 0;
            while (i < messages.Count)
            {
                var m = messages[i];
                string role = MiniJson.GetStr(m, "role", "user");
                string content = MiniJson.GetStr(m, "content", "");

                if (role == "system")
                {
                    if (!string.IsNullOrEmpty(content)) sysParts.Add(content);
                    i++; continue;
                }

                var calls = MiniJson.GetArr(m, "tool_calls");
                if (role == "assistant" && calls != null && calls.Count > 0)
                {
                    var blocks = new List<object>();
                    if (!string.IsNullOrEmpty(content))
                    {
                        var tb = MiniJson.Obj(); tb["type"] = "text"; tb["text"] = content;
                        blocks.Add(tb);
                    }
                    foreach (object c in calls)
                    {
                        var tc = c as Dictionary<string, object>;
                        if (tc == null) continue;
                        var fn = MiniJson.GetDict(tc, "function");
                        if (fn == null) continue;
                        string argsJson = MiniJson.GetStr(fn, "arguments", "{}");
                        object input;
                        try { input = MiniJson.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson); }
                        catch { input = MiniJson.Obj(); }
                        var ub = MiniJson.Obj();
                        ub["type"] = "tool_use";
                        ub["id"] = MiniJson.GetStr(tc, "id", "call_0");
                        ub["name"] = MiniJson.GetStr(fn, "name", "");
                        ub["input"] = input;
                        blocks.Add(ub);
                    }
                    var am = MiniJson.Obj();
                    am["role"] = "assistant";
                    am["content"] = blocks;
                    wireMsgs.Add(am);
                    i++; continue;
                }

                if (role == "tool")
                {
                    var blocks = new List<object>();
                    while (i < messages.Count && MiniJson.GetStr(messages[i], "role", "") == "tool")
                    {
                        var tm = messages[i];
                        var rb = MiniJson.Obj();
                        rb["type"] = "tool_result";
                        rb["tool_use_id"] = MiniJson.GetStr(tm, "tool_call_id", "call_0");
                        rb["content"] = MiniJson.GetStr(tm, "content", "");
                        blocks.Add(rb);
                        i++;
                    }
                    var um = MiniJson.Obj();
                    um["role"] = "user";
                    um["content"] = blocks;
                    wireMsgs.Add(um);
                    continue;
                }

                var pm = MiniJson.Obj();
                pm["role"] = role == "assistant" ? "assistant" : "user";
                var pb = MiniJson.Obj(); pb["type"] = "text"; pb["text"] = content;
                var parr = new List<object>(); parr.Add(pb);
                pm["content"] = parr;
                wireMsgs.Add(pm);
                i++;
            }

            var body = MiniJson.Obj();
            body["model"] = _cfg.Model;
            body["max_tokens"] = 8192;
            if (IsAnthropic)
            {
                // faithful to ZCode zcode-builtin.json anthropic-messages reasoningLevel map:
                // off -> {"thinking":{"type":"disabled"}}
                // low/high/max -> {"thinking":{"type":"adaptive"},"output_config":{"effort":level}}
                var lvl = _cfg.ThinkingLevel;
                if (lvl != "low" && lvl != "high" && lvl != "max") lvl = "off";
                var th = MiniJson.Obj();
                if (lvl == "off")
                {
                    th["type"] = "disabled";
                }
                else
                {
                    th["type"] = "adaptive";
                    var oc = MiniJson.Obj();
                    oc["effort"] = lvl;
                    body["output_config"] = oc;
                }
                body["thinking"] = th;
            }
            if (sysParts.Count > 0) body["system"] = string.Join("\n\n", sysParts.ToArray());
            body["messages"] = wireMsgs;
            if (tools != null && tools.Count > 0)
            {
                var at = new List<object>();
                foreach (object t in tools)
                {
                    var w = t as Dictionary<string, object>;
                    if (w == null) continue;
                    var fn = MiniJson.GetDict(w, "function");
                    if (fn == null) continue;
                    string name = MiniJson.GetStr(fn, "name", "");
                    if (name.Length > 64) name = name.Substring(0, 64);
                    var af = MiniJson.Obj();
                    af["name"] = name;
                    af["description"] = MiniJson.GetStr(fn, "description", "");
                    af["input_schema"] = MiniJson.GetDict(fn, "parameters") ?? MiniJson.Obj();
                    at.Add(af);
                }
                body["tools"] = at;
                var tc2 = MiniJson.Obj(); tc2["type"] = "auto";
                body["tool_choice"] = tc2;
            }
            body["stream"] = stream;
            return MiniJson.Serialize(body);
        }
    }
}
