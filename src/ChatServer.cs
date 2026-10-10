// ZCode7 - local HTTP + WebSocket server for the official ZCode web UI.
// Serves web2/ static files and accepts /ws (recon mode: log every frame the
// official client sends, so we can implement the RPC protocol against reality).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZCode7
{
    public class ChatServer
    {
        private readonly string _webRoot;
        private readonly Agent _agent;                    // null = 纯静态模式（uitest）
        private readonly Settings _cfg;
        private HttpListener _listener;
        public event Action<string> Log;
        public event Action<byte[]> WsFrameFromClient;   // recon: raw frames from official client
        public int Port { get; private set; }

        public ChatServer(string webRoot) : this(webRoot, null, null) { }

        public ChatServer(string webRoot, Agent agent) : this(webRoot, agent, null) { }

        public ChatServer(string webRoot, Agent agent, Settings cfg)
        {
            _webRoot = webRoot;
            _agent = agent;
            _cfg = cfg;
        }

        private void L(string s)
        {
            ZLog.Log(s);
            if (Log != null) Log(s);
        }

        public void Start(int port)
        {
            Port = port;
            _webRootForPreview = _webRoot;
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Start();
            L("chat server on http://127.0.0.1:" + port + " root=" + _webRoot);
            Task.Run((Action)AcceptLoop);
        }

        private async void AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception ex) { L("accept: " + ex.Message); return; }
                try { Handle(ctx); }
                catch (Exception ex) { L("handle: " + ex.Message); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            if (req.IsWebSocketRequest && req.Url.AbsolutePath == "/ws")
            {
                HandleWs(ctx);
                return;
            }
            if (req.Url.AbsolutePath == "/api/server-info")
            {
                // 返回空 workspaces：官方 web 在无初始工作区时走兜底
                // ensureConversationWorkspace -> 以 conversation 目的建 tab，
                // 侧栏"任务"区才会真正查询 listTaskList（project tab 不查任务）
                var json = "{\"workspaces\":[],\"serverId\":\"zcode7\",\"version\":\"1.0.0\",\"protocolVersion\":1,\"authRequired\":false}";
                var b = System.Text.Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.Close();
                return;
            }
            if (req.Url.AbsolutePath == "/preview")
            {
                ServePreview(ctx);
                return;
            }
            ServeStatic(ctx);
        }

        // /preview?path=<本地文件>：内置浏览器打开本地 md/html/txt/图片/pdf。
        // md 渲染为 HTML（marked.js 从 webRoot/preview/ 读取），html/图片/pdf 原样返回。
        private void ServePreview(HttpListenerContext ctx)
        {
            string raw = ctx.Request.QueryString["path"] ?? "";
            string file;
            try { file = Uri.UnescapeDataString(raw); }
            catch { file = raw; }
            file = file.Trim().Trim('"');

            string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
            if (File.Exists(file) && (ext == ".html" || ext == ".htm" || ext == ".pdf" ||
                ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".ico"))
            {
                // 浏览器原生可渲染的类型直接回原始字节
                var bytes = File.ReadAllBytes(file);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = ext == ".pdf" ? "application/pdf"
                    : ext == ".png" ? "image/png"
                    : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                    : ext == ".gif" ? "image/gif"
                    : ext == ".webp" ? "image/webp"
                    : ext == ".svg" ? "image/svg+xml"
                    : ext == ".ico" ? "image/x-icon"
                    : "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                using (var os = ctx.Response.OutputStream) os.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                return;
            }
            ResponseHtml(ctx, 200, PreviewHtml(file, ext));
        }

        internal static string PreviewHtml(string file, string ext)
        {
            string esc = System.Net.WebUtility.HtmlEncode(file);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return "<html><head><meta charset='utf-8'><style>body{font-family:sans-serif;color:#555;background:#fafafa;display:flex;align-items:center;justify-content:center;height:96vh}</style></head>" +
                       "<body><div style='text-align:center'><div style='font-size:40px'>&#128449;</div><p>文件不存在或无法读取</p><code>" + esc + "</code></div></body></html>";

            string content;
            try { content = File.ReadAllText(file, DetectEncoding(file)); }
            catch (Exception ex)
            {
                return "<html><body><p>读取失败: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</p></body></html>";
            }

            string body;
            if (ext == ".md" || ext == ".markdown")
            {
                // marked.js 内联注入（本地文件，无外网依赖），渲染失败时回落 <pre>
                string markedJs;
                string markedPath = Path.Combine(_webRootForPreview, "preview", "marked.min.js");
                try { markedJs = File.ReadAllText(markedPath); }
                catch { markedJs = ""; }
                string mdJson = MiniJson.Serialize(content ?? "");
                body = markedJs.Length > 0
                    ? "<div id='c'></div><script>" + markedJs + "</script>" +
                      "<script>var SRC=" + mdJson + ";" +
                      "try{document.getElementById('c').innerHTML=marked.parse(SRC);}" +
                      "catch(e){var p=document.createElement('pre');p.textContent=SRC;document.body.appendChild(p);}</script>"
                    : "<pre>" + esc2(content) + "</pre>";
                string css = "<style>body{font-family:'Segoe UI',sans-serif;max-width:860px;margin:24px auto;padding:0 20px;color:#1f2328;line-height:1.65}" +
                    "h1,h2,h3{border-bottom:1px solid #e4e4e4;padding-bottom:6px}code{background:#f2f2f2;border-radius:4px;padding:1px 5px;font-family:Consolas,monospace;font-size:.92em}" +
                    "pre{background:#f6f8fa;border-radius:8px;padding:12px;overflow:auto}pre code{background:none;padding:0}" +
                    "table{border-collapse:collapse}td,th{border:1px solid #d8d8d8;padding:6px 10px}blockquote{border-left:4px solid #d0d7de;margin:0;padding:0 12px;color:#59636e}" +
                    "img{max-width:100%}a{color:#0969da}</style>";
                return "<html><head><meta charset='utf-8'>" + css + "</head><body>" + body + "</body></html>";
            }
            // 其余文本一律 <pre>
            return "<html><head><meta charset='utf-8'><style>body{font-family:Consolas,monospace;font-size:13px;margin:16px;white-space:pre-wrap;color:#1f2328}</style></head><body>"
                + esc2(content) + "</body></html>";
        }

        private static string esc2(string s)
        {
            return System.Net.WebUtility.HtmlEncode(s);
        }

        // 简单编码探测：默认 UTF8；带 BOM 的会自动识别，GB2312/GBK 中文日志常见——
        // UTF8 严格解码失败时回落 GBK（.NET Framework 默认带 GB18030）
        private static Encoding DetectEncoding(string file)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return new UTF8Encoding(true);
                // 严格 UTF-8 校验
                try
                {
                    var strict = new UTF8Encoding(false, true);
                    strict.GetString(bytes);
                    return new UTF8Encoding(false);
                }
                catch
                {
                    try { return Encoding.GetEncoding("GB18030"); }
                    catch { return Encoding.UTF8; }
                }
            }
            catch { return Encoding.UTF8; }
        }

        // ServePreview 需要知道 webRoot（marked.js 位置）；Start 时赋值
        private static string _webRootForPreview;

        private void ResponseHtml(HttpListenerContext ctx, int status, string html)
        {
            var b = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            using (var os = ctx.Response.OutputStream) os.Write(b, 0, b.Length);
            ctx.Response.Close();
        }

        private async Task HandleWs(HttpListenerContext ctx)
        {
            L("WS CONNECT from official UI  path=" + ctx.Request.Url.AbsolutePath +
              "  subprotocol='" + (ctx.Request.Headers["Sec-WebSocket-Protocol"] ?? "") + "'");
            WebSocket ws = null;
            try
            {
                ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                L("WS ACCEPTED state=" + ws.State);
                var rpc = new RpcChannelServer(frameBytes =>
                {
                    if (ws.State == WebSocketState.Open)
                        ws.SendAsync(new ArraySegment<byte>(frameBytes), WebSocketMessageType.Binary, true, CancellationToken.None)
                         .GetAwaiter().GetResult();
                });
                RpcServicesRegistry.RegisterAll(rpc, _cfg, _agent);
                rpc.SendInitialize();
                rpc.onRequestLogged += req =>
                    L("RPC " + req.ChannelName + "." + req.Name + " id=" + req.Id +
                      " type=" + req.Type + " argLen=" + (req.Arg != null ? "set" : "null"));
                var buf = new byte[65536];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        L("WS CLOSE: " + (result.CloseStatusDescription ?? ""));
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }
                    int len = result.Count;
                    // complete frames may span multiple ws messages only if partial; official client sends whole frames
                    var frameBytes = new byte[len];
                    Array.Copy(buf, frameBytes, len);
                    rpc.OnFrame(frameBytes);
                }
            }
            catch (Exception ex) { L("WS error: " + ex.Message); }
            finally { try { ws?.Dispose(); } catch { } }
        }

        private static string Hex(byte[] b, int len, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(len, max); i++) sb.Append(b[i].ToString("x2"));
            if (len > max) sb.Append("…");
            return sb.ToString();
        }

        private static string SafeUtf8(byte[] b, int len, int max)
        {
            try
            {
                var s = Encoding.UTF8.GetString(b, 0, len);
                return s.Length > max ? s.Substring(0, max) : s;
            }
            catch { return ""; }
        }

        private void ServeStatic(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath;
            if (path == "/" || path.Length == 0) path = "/index.html";
            string file = Path.Combine(_webRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                // SPA fallback: unknown paths get index.html
                file = Path.Combine(_webRoot, "index.html");
            }
            ctx.Response.ContentType = Mime(Path.GetExtension(file));
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            var bytes = File.ReadAllBytes(file);
            ctx.Response.ContentLength64 = bytes.Length;
            using (var os = ctx.Response.OutputStream)
            {
                os.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.Close();
        }

        private static string Mime(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript";
                case ".css": return "text/css";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".svg": return "image/svg+xml";
                case ".woff2": return "font/woff2";
                case ".json": return "application/json";
                default: return "application/octet-stream";
            }
        }
    }
}
