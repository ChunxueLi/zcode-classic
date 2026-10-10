using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace ZCode7
{
    // 内置浏览器侧栏：独立 WebView2 实例（独立 user data folder），
    // 支持网址/本地文件（md/html/txt/图片/pdf 经 /preview 路由）。
    // 由 ChatHost 注入的悬浮按钮 postMessage 或 Ctrl+U 呼出。
    public class BrowserPane : Panel
    {
        private readonly WebView2 _wv;
        private readonly TextBox _addr;
        private readonly Panel _toolbar;
        private bool _envReady;
        private bool _navInternal; // 程序导航时地址栏由导航事件回填，不重复处理

        public event Action CloseRequested;
        public event Action<string> StatusMessage;

        public BrowserPane()
        {
            Dock = DockStyle.Right;
            Width = 640;
            BackColor = Theme.Bg;

            _toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
            _addr = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.BaseFont,
            };
            _addr.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    NavigateTo(_addr.Text);
                    Focus(); // 焦点交给面板，让后续按键进页面
                }
            };

            var back = Tbb("\u25C0", () => { if (_envReady) _wv.GoBack(); });
            var fwd = Tbb("\u25B6", () => { if (_envReady) _wv.GoForward(); });
            var rel = Tbb("\u21BB", () => { if (_envReady) _wv.Reload(); });
            var ext = Tbb("\u2197", OpenExternal);
            var close = Tbb("\u2715", () => { if (CloseRequested != null) CloseRequested(); });

            _toolbar.Controls.Add(_addr);
            // 从右往左：关闭 | 外部打开 | 刷新 | 前进 | 后退，地址栏吃剩余宽度
            _toolbar.Controls.Add(close);
            _toolbar.Controls.Add(ext);
            _toolbar.Controls.Add(rel);
            _toolbar.Controls.Add(fwd);
            _toolbar.Controls.Add(back);
            close.Dock = DockStyle.Right; close.Width = 36;
            ext.Dock = DockStyle.Right; ext.Width = 36;
            rel.Dock = DockStyle.Right; rel.Width = 36;
            fwd.Dock = DockStyle.Right; fwd.Width = 36;
            back.Dock = DockStyle.Right; back.Width = 36;
            _addr.Padding = new Padding(4);

            _wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_wv);
            Controls.Add(_toolbar);

            InitAsync();
        }

        private Button Tbb(string glyph, Action onClick)
        {
            var b = new Button
            {
                Text = glyph,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 11F),
                BackColor = Theme.Bg,
                ForeColor = Theme.TextPrimary,
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => onClick();
            return b;
        }

        private async void InitAsync()
        {
            try
            {
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ZCode7", "WebView2-Browser");
                // 不开 remote-debugging 端口，避免和聊天面板 9222 冲突
                var env = Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    null, dataDir).GetAwaiter().GetResult();
                await _wv.EnsureCoreWebView2Async(env);
                _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _wv.CoreWebView2.Settings.IsZoomControlEnabled = true;
                _wv.CoreWebView2.DocumentTitleChanged += (s, e) =>
                {
                    try { ZLog.Log("browser title: " + _wv.CoreWebView2.DocumentTitle); } catch { }
                };
                _wv.CoreWebView2.NavigationStarting += (s, e) => { try { _addr.Text = e.Uri; } catch { } };
                _wv.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    try { _addr.Text = _wv.Source != null ? _wv.Source.ToString() : _addr.Text; } catch { }
                };
                // target=_blank 之类的新窗口：在同一面板内继续导航，不弹系统浏览器
                _wv.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    try { _wv.Source = new Uri(e.Uri); } catch { }
                    e.Handled = true;
                };
                _envReady = true;
                if (_pendingUrl != null) NavigateTo(_pendingUrl);
            }
            catch (Exception ex)
            {
                if (StatusMessage != null) StatusMessage("内置浏览器初始化失败: " + ex.Message);
                ZLog.Log("BrowserPane init fail: " + ex);
            }
        }

        private string _pendingUrl;

        // 入口：网址 / 本地路径 / 关键词，统一规整后导航
        public void NavigateTo(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;
            // 剪贴板/跨进程文本常混入控制字符（TAB 等），会让 Path.GetFullPath 抛异常
            var sb = new StringBuilder(input.Length);
            foreach (char c in input.Trim())
            {
                if (!char.IsControl(c)) sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length == 0) return;
            string url = NormalizeInput(s);
            _addr.Text = url;
            if (!_envReady) { _pendingUrl = url; return; }
            try { _wv.Source = new Uri(url); }
            catch (Exception ex) { if (StatusMessage != null) StatusMessage("导航失败: " + ex.Message); }
        }

        public static string NormalizeInput(string s)
        {
            if (File.Exists(s))
                return PreviewUrl(s);
            // 带盘符或以 \\ 开头的路径（文件可能尚未生成）也走预览路由
            if ((s.Length >= 2 && s[1] == ':') || s.StartsWith("\\\\"))
                return PreviewUrl(s);
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return s;
            if (s.StartsWith("127.0.0.1") || s.StartsWith("localhost"))
                return "http://" + s;
            // 像域名的直接补 https；否则当搜索词
            if (s.IndexOf(' ') < 0 && s.IndexOf('.') > 0 && IsAscii(s))
                return "https://" + s;
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(s);
        }

        private static bool IsAscii(string s)
        {
            foreach (char c in s) if (c > 126) return false;
            return true;
        }

        public static string PreviewUrl(string localPath)
        {
            string full;
            try { full = Path.GetFullPath(localPath); }
            catch { full = localPath; }
            return "http://127.0.0.1:8347/preview?path=" + Uri.EscapeDataString(full);
        }

        private void OpenExternal()
        {
            try
            {
                string u = _addr.Text;
                if (string.IsNullOrEmpty(u)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
            }
            catch (Exception ex) { ZLog.Log("browser open external fail: " + ex.Message); }
        }
    }
}
