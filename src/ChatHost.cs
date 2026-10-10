// ZCode7 - WebView2 chat host: browser-rendered timeline (native selection/copy/md)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZCode7
{
    /// <summary>Chat timeline hosted in WebView2 (Chromium). Markdown via marked + DOMPurify,
    /// native selection/copy/scroll. C# pushes JSON; JS posts back button actions.</summary>
    public class ChatHost : Panel
    {
        private System.Windows.Forms.Timer _dbgExecTimer;
        private System.Windows.Forms.Timer _quotaTimer;
        private int _dbgTicks;
        private Microsoft.Web.WebView2.WinForms.WebView2 _wv;
        private bool _ready;
        private readonly Queue<string> _pending = new Queue<string>();
        private int _nextRowId;

        public void DumpDiag()
        {
            // 必须 await：GetResult 会在 UI 线程同步阻塞，而 ExecuteScriptAsync 的完成回调
            // 也要回到 UI 线程 → 自死锁，此后所有排队消息（拖动/最大化/关闭/计时器）全部饿死
            _ = DumpDiagAsync();
        }

        private async Task DumpDiagAsync()
        {
            try
            {
                var result = await _wv.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({"
                    + "url:location.href,"
                    + "hasErr:!!document.querySelector('[role=alert]'),"
                    + "errText:document.querySelector('[role=alert]')?document.querySelector('[role=alert]').innerText.substring(0,500):'',"
                    + "rootChildren:document.getElementById('root')?document.getElementById('root').children.length:-1"
                    + "})");
                ZLog.Log("page diag: " + result);
            }
            catch (Exception ex) { ZLog.Log("DumpDiag fail: " + ex.Message); }
        }
        public event Action<string, bool> ApprovalDecided;   // interactionId, allowed
        public event Action<string> BrowserToggleRequested;  // url 可空：仅切换面板
        private readonly Agent _agent;                       // null = 静态页模式
        private readonly Settings _cfg;

        public ChatHost() : this(null, null) { }

        public ChatHost(Agent agent) : this(agent, null) { }

        public ChatHost(Agent agent, Settings cfg)
        {
            _agent = agent;
            _cfg = cfg;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            _wv = new Microsoft.Web.WebView2.WinForms.WebView2();
            _wv.Dock = DockStyle.Fill;
            Controls.Add(_wv);
            Init();
        }

        private async void Init()
        {
            try
            {
                var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZCode7", "WebView2");
                var opts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions();
                opts.AdditionalBrowserArguments = "--remote-debugging-port=9222 --remote-allow-origins=*";
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    null, dataDir, opts);
                await _wv.EnsureCoreWebView2Async(env);
                // web2 = 官方 UI 构建产物（out/web2），存在则优先；web/ 是自建 chat.html 回退
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var webRoot = File.Exists(Path.Combine(baseDir, "web2", "index.html"))
                    ? Path.Combine(baseDir, "web2")
                    : Path.Combine(baseDir, "web");
                var server = new ChatServer(webRoot, _agent, _cfg);
                server.Start(8347);
                _wv.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        var msg = MiniJson.ParseObject(e.WebMessageAsJson);
                        if (msg == null) return;
                        string type = MiniJson.GetStr(msg, "type", "");
                        if (type == "approval")
                        {
                            string rowId = MiniJson.GetStr(msg, "row", "");
                            bool allow = MiniJson.GetStr(msg, "action", "") == "allow";
                            if (ApprovalDecided != null) ApprovalDecided(rowId, allow);
                        }
                        else if (type == "jserror")
                        {
                            ZLog.Log("JS-ERROR: " + MiniJson.GetStr(msg, "msg", ""));
                        }
                        else if (type == "ready")
                        {
                            _ready = true;
                            FlushPending();
                        }
                        else if (type == "open-browser")
                        {
                            string u = MiniJson.GetStr(msg, "url", "");
                            if (BrowserToggleRequested != null) BrowserToggleRequested(u);
                        }
                    }
                    catch (Exception ex) { ZLog.Log("chat msg: " + ex.Message); }
                };
                _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                // 权限自动授予：本地单机应用，剪贴板读取等权限弹窗（edge://permission-request-dialog）
                // 会以整页形式遮挡应用且无法交互，统一自动允许并记日志
                _wv.CoreWebView2.PermissionRequested += (s2, e2) =>
                {
                    e2.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
                    e2.Handled = true;
                    ZLog.Log("permission auto-allow: " + e2.PermissionKind);
                };
                // 外链防护：帮助菜单等产品外链会把应用页面整个导航走。非本机来源的导航/新窗口
                // 一律取消并转系统默认浏览器打开
                var selfOrigin = "http://127.0.0.1:8347";
                _wv.CoreWebView2.NewWindowRequested += (s2, e2) =>
                {
                    try
                    {
                        ZLog.Log("external new window -> " + e2.Uri);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri) { UseShellExecute = true });
                    }
                    catch (Exception ex2) { ZLog.Log("open external fail: " + ex2.Message); }
                    e2.Handled = true;
                };
                _wv.CoreWebView2.NavigationStarting += (s2, e2) =>
                {
                    if (e2.Uri != null && !e2.Uri.StartsWith(selfOrigin, StringComparison.OrdinalIgnoreCase)
                        && !e2.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        && e2.Uri != "about:blank")
                    {
                        try
                        {
                            ZLog.Log("external nav -> " + e2.Uri);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri) { UseShellExecute = true });
                        }
                        catch (Exception ex2) { ZLog.Log("open external fail: " + ex2.Message); }
                        e2.Cancel = true;
                    }
                };
                // 非客户区支持必须开启：UI 里任务头部/欢迎页/侧栏/设置页都渲染了 [app-region:drag]
                // 拖动条，且 WebView2 需要它接管缩放边框（原生边框被 WebView2 子窗口覆盖）。
                // 之前"开启后拖不动"实为 DumpDiag UI 线程死锁（已修），与本开关无关。
                _wv.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
                ZLog.Log("IsNonClientRegionSupportEnabled=" + _wv.CoreWebView2.Settings.IsNonClientRegionSupportEnabled
                    + " BrowserVersion=" + _wv.CoreWebView2.Environment.BrowserVersionString);
                _wv.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _wv.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.onerror=function(m,s,l,err){try{window.chrome.webview.postMessage({type:'jserror',msg:m+' @'+l,stack:(err&&err.stack)?err.stack:''});}catch(e){}};" +
                    "['log','warn','error'].forEach(function(k){var o=console[k];console[k]=function(){" +
                    "var args=Array.prototype.map.call(arguments,function(a){try{return (a&&a.stack)?String(a.stack):String(a);}catch(e){return '[unprintable]'}});" +
                    "try{window.chrome.webview.postMessage({type:'console',level:k,msg:args.join(' ')});}catch(e){}" +
                    "o.apply(console,arguments);};});" +
                    // 内置浏览器悬浮按钮：右缘中部，点击 postMessage 给 C# 切换 BrowserPane
                    "(function(){function mk(){" +
                    "if(document.getElementById('zcode7-browser-btn'))return;" +
                    "var b=document.createElement('div');b.id='zcode7-browser-btn';b.title='\\u5185\\u7f6e\\u6d4f\\u89c8\\u5668 (Ctrl+U)';" +
                    "b.style.cssText='position:fixed;right:0;top:45%;z-index:9998;width:26px;height:56px;" +
                    "border-radius:8px 0 0 8px;background:rgba(0,0,0,0.10);display:flex;align-items:center;justify-content:center;" +
                    "font-size:15px;cursor:pointer;user-select:none;transition:background .15s;';" +
                    "b.textContent='\\uD83C\\uDF10';" +
                    "b.onmouseenter=function(){b.style.background='rgba(0,0,0,0.2)';};" +
                    "b.onmouseleave=function(){b.style.background='rgba(0,0,0,0.10)';};" +
                    "b.onclick=function(){try{window.chrome.webview.postMessage({type:'open-browser',url:''});}catch(e){}};" +
                    "document.body.appendChild(b);}" +
                    "if(document.body)mk();else document.addEventListener('DOMContentLoaded',mk);})();");
                _wv.CoreWebView2.WebMessageReceived += (s3, e3) =>
                {
                    try
                    {
                        var m3 = MiniJson.ParseObject(e3.WebMessageAsJson);
                        if (m3 != null && MiniJson.GetStr(m3, "type", "") == "console")
                            ZLog.Log("console[" + MiniJson.GetStr(m3, "level", "") + "] " + MiniJson.GetStr(m3, "msg", ""));
                        if (m3 != null && MiniJson.GetStr(m3, "type", "") == "jserror")
                            ZLog.Log("jserror: " + MiniJson.GetStr(m3, "msg", "") + " STACK: " + MiniJson.GetStr(m3, "stack", ""));
                    }
                    catch { }
                };
                _wv.CoreWebView2.NavigationCompleted += async (s2, e2) =>
                {
                    ZLog.Log("chat nav done: success=" + e2.IsSuccess);
                    try
                    {
                        var diag = await _wv.CoreWebView2.ExecuteScriptAsync(
                            "JSON.stringify({"
                            + "url:location.href,"
                            + "hasErrorCard:!!document.querySelector('[role=alert]'),"
                            + "errorText:(document.querySelector('[role=alert] pre')||{}).textContent||'',"
                            + "rootChildren:document.getElementById('root')?document.getElementById('root').children.length:-1,"
                            + "diagKeys:Object.keys(localStorage).filter(k=>/diag|log|error|crash/i.test(k))"
                            + "})");
                        ZLog.Log("chat diag: " + diag);
                        // pull the official UI diagnostic log tail if present
                        var stack = await _wv.CoreWebView2.ExecuteScriptAsync(
                            "(function(){try{var e=document.querySelector('[role=alert] details pre');return e?e.textContent:'';}catch(x){return 'ERR';}})()");
                        if (!string.IsNullOrEmpty(stack) && stack != "''" && stack.Length > 4)
                            ZLog.Log("component stack tail: " + stack.Substring(0, Math.Min(1500, stack.Length)));
                        // delayed polls: react crashes surface AFTER navigation completes
                        _ = Task.Run(async () =>
                        {
                            foreach (var delayMs in new[] { 3000, 8000, 15000 })
                            {
                                await Task.Delay(delayMs);
                                try
                                {
                                    BeginInvoke((Action)(async () =>
                                    {
                                        try
                                        {
                                            var d2 = await _wv.CoreWebView2.ExecuteScriptAsync(
                                                "JSON.stringify({t:'" + delayMs + "',errCard:!!document.querySelector('[role=alert]'),"
                                                + "errText:(document.querySelector('[role=alert] code')||{}).textContent||'',"
                                                + "stack:(document.querySelector('[role=alert] details pre')||{}).textContent||'',"
                                                + "rows:document.querySelectorAll('.row').length,"
                                                + "bodyLen:document.body ? document.body.innerText.length : 0})");
                                            ZLog.Log("delayed diag: " + d2);
                                        }
                                        catch (Exception ex2) { ZLog.Log("delayed diag fail: " + ex2.Message); }
                                    }));
                                }
                                catch (Exception ex3) { ZLog.Log("begininvoke fail: " + ex3.Message); }
                            }
                        });
                    }
                    catch (Exception ex) { ZLog.Log("chat diag fail: " + ex.Message); }
                };
                ZLog.Log("chat nav start -> http://127.0.0.1:8347/index.html  root=" + webRoot);
                // 应用级快捷键：web 页只显示标签不派发（桌面壳职责），这里在页面内注册 Ctrl+N/Ctrl+K
                _ = _wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.addEventListener('keydown',function(e){" +
                    "if(e.ctrlKey&&!e.shiftKey&&!e.altKey&&!e.isComposing){" +
                    "var k=(e.key||'').toLowerCase();" +
                    "if(k==='n'){e.preventDefault();e.stopPropagation();var t=document.querySelector('[data-testid=conversation-new-task]');if(t)t.click();}" +
                    "else if(k==='k'){e.preventDefault();e.stopPropagation();var bs=document.querySelectorAll('button');for(var i=0;i<bs.length;i++){var s=bs[i].innerText||'';if(s.indexOf('\\u641c\\u7d22')>=0&&s.indexOf('Ctrl')>=0){bs[i].click();break;}}}" +
                    "}},true);");
                // debug-exec 通道：%APPDATA%\ZCode7\debug-exec.js 存在则执行并回写日志（不依赖 9222 DevTools）
                _dbgExecTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                _dbgExecTimer.Tick += async (s3, e3) =>
                {
                    try
                    {
                        var dbgFile = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "ZCode7", "debug-exec.js");
                        if (!File.Exists(dbgFile)) { if (++_dbgTicks % 400 == 0) ZLog.Log("DBGTICK alive no-file"); return; }
                        _dbgTicks = 0;
                        var code = File.ReadAllText(dbgFile);
                        File.Delete(dbgFile);
                        var r3 = await _wv.CoreWebView2.ExecuteScriptAsync(code);
                        ZLog.Log("DEBUGEXEC " + (r3.Length > 8000 ? r3.Substring(0, 8000) : r3));
                    }
                    catch (Exception ex4) { ZLog.Log("DEBUGEXEC fail: " + ex4.Message); }
                };
                _dbgExecTimer.Start();
                // ---- 额度显示：周期性获取智谱 5h/周额度并注入 composer 下方 DOM ----
                _quotaTimer = new System.Windows.Forms.Timer { Interval = 300000 }; // 5 min
                _quotaTimer.Tick += async (s5, e5) =>
                {
                    try
                    {
                        string key = _cfg != null ? _cfg.ApiKey : "";
                        if (string.IsNullOrEmpty(key)) return;
                        var u = await Task.Run(() => ZhipuUsage.Fetch(key));
                        if (u == null) return;
                        string pct5h = u.Pct5h >= 0 ? u.Pct5h + "%" : "--";
                        string pctW = u.PctWeek >= 0 ? u.PctWeek + "%" : "--";
                        string color5h = u.Pct5h >= 90 ? "#f87171" : u.Pct5h >= 80 ? "#fbbf24" : "#4ade80";
                        string colorW = u.PctWeek >= 90 ? "#f87171" : u.PctWeek >= 80 ? "#fbbf24" : "#4ade80";
                        string js = "(function(){" +
                            "var el=document.getElementById('zcode7-quota-bar');" +
                            "if(!el){el=document.createElement('div');el.id='zcode7-quota-bar';" +
                            "el.style.cssText='position:fixed;bottom:72px;right:16px;z-index:9999;" +
                            "display:flex;gap:12px;padding:3px 10px;border-radius:6px;" +
                            "background:rgba(0,0,0,0.06);font-size:11px;pointer-events:none;" +
                            "color:var(--foreground-subtle,inherit);user-select:none;';" +
                            "document.body.appendChild(el);}" +
                            "el.innerHTML='<span title=“5小时滚动窗口”>5h <span style=“color:" + color5h + "”>" + pct5h + "</span></span>" +
                            "<span>·</span><span title=“周额度”>周 <span style=“color:" + colorW + "”>" + pctW + "</span></span>';" +
                            "})()";
                        await _wv.CoreWebView2.ExecuteScriptAsync(js);
                        ZLog.Log("quota inject: 5h=" + pct5h + " week=" + pctW);
                    }
                    catch (Exception ex5) { ZLog.Log("quota inject fail: " + ex5.Message); }
                };
                _quotaTimer.Start();
                _wv.Source = new Uri("http://127.0.0.1:8347/index.html");
            }
            catch (Exception ex)
            {
                ZLog.Log("WebView2 init failed: " + ex);
                var lbl = new Label
                {
                    Text = "WebView2 初始化失败：" + ex.Message,
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.TextSubtle,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                Controls.Add(lbl);
            }
        }

        private static double GetNum(Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v)) return -1;
            try { return Convert.ToDouble(v); } catch { return -1; }
        }

        /// <summary>Run a JS snippet (fire and forget, queued until ready).
        /// try/catch 包裹：官方 UI 页面没有 window.chat 全局，推送脚本静默失败即可。</summary>
        public void Js(string script)
        {
            var wrapped = "try{" + script + "}catch(e){}";
            if (_ready)
            {
                try { _wv.ExecuteScriptAsync(wrapped); } catch (Exception ex) { ZLog.Log("js: " + ex.Message); }
            }
            else
            {
                lock (_pending) _pending.Enqueue(wrapped);
            }
        }

        private void FlushPending()
        {
            lock (_pending)
            {
                while (_pending.Count > 0)
                {
                    var js = _pending.Dequeue();
                    try { _wv.ExecuteScriptAsync(js); } catch { }
                }
            }
        }

        // ---------- public message API (JSON-safe payloads) ----------

        private static string J(string s)
        {
            return s == null ? "''" : JsQuote(s);
        }

        private static string JsQuote(string s)
        {
            var sb = new System.Text.StringBuilder("'");
            foreach (var ch in s)
            {
                if (ch == '\'') sb.Append("\\'");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') { }
                else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            sb.Append('\'');
            return sb.ToString();
        }

        public int AddRow(string kind)
        {
            int id = ++_nextRowId;
            Js("chat.add(" + id + ",'" + kind + "')");
            return id;
        }

        public void SetAssistantText(int rowId, string text)
        {
            Js("chat.assistant(" + rowId + "," + J(text) + ")");
        }

        public void SetUserText(int rowId, string text)
        {
            Js("chat.user(" + rowId + "," + J(text) + ")");
        }

        public void SetTool(int rowId, string tool, string args, string result, bool running, int addLines)
        {
            Js("chat.tool(" + rowId + "," + J(tool) + "," + J(args ?? "") + "," + J(result ?? "") + "," +
               (running ? "true" : "false") + "," + addLines + ")");
        }

        public void SetThinking(int rowId, string text, string duration, bool running)
        {
            Js("chat.thinking(" + rowId + "," + J(text ?? "") + "," + J(duration) + "," + (running ? "true" : "false") + ")");
        }

        public void SetToolArgs(int rowId, string tool, string args, int addLines)
        {
            Js("chat.tool(" + rowId + "," + J(tool) + "," + J(args ?? "") + ",'',true," + addLines + ")");
        }

        // browser handles selection/copy natively; kept for compatibility
        public bool HasSelection { get { return false; } }
        public void CopySelection() { }
        public void ClearSelection() { }

        public void SetToolResult(int rowId, string result)
        {
            Js("chat.toolresult(" + rowId + "," + J(result ?? "") + ")");
        }

        public void SetWorkStatus(int rowId, string duration, bool running)
        {
            Js("chat.work(" + rowId + "," + J(duration) + "," + (running ? "true" : "false") + ")");
        }

        public void SetInfo(int rowId, string text)
        {
            Js("chat.info(" + rowId + "," + J(text) + ")");
        }

        public void SetApproval(int rowId, string tool, string args, bool decided, bool allowed)
        {
            Js("chat.approval(" + rowId + "," + J(tool) + "," + J(args ?? "") + "," +
               (decided ? "true" : "false") + "," + (allowed ? "true" : "false") + ")");
        }

        public void ClearRows()
        {
            Js("chat.clear()");
        }

        public void ScrollToBottom()
        {
            Js("chat.bottom()");
        }

        public void UpdateTodos(string itemsJson)
        {
            Js("chat.todos(" + (string.IsNullOrEmpty(itemsJson) ? "[]" : itemsJson) + ")");
        }
    }
}
