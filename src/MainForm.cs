// ZCode7 - main window: faithful zai-light layout (sidebar 264 + header 48 + chat 896 + composer)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZCode7
{
    public class MainForm : Form
    {
        private Settings _cfg;
        private Agent _agent;
        public bool DemoMode;

        private Sidebar _sidebar;
        private Panel _mainArea;
        private Panel _chatPage;
        private BrowserPane _browserPane;
        private HeaderBar _header;
        private ChatHost _chat;
        private Composer _composer;
        private SkillsPage _skillsPage;
        private StatusPanel _statusPanel;
        private Timer _tick;

        private int _streamRow = -1;
        private readonly System.Text.StringBuilder _streamText = new System.Text.StringBuilder();
        private int _thinkRow = -1;
        private readonly System.Text.StringBuilder _thinkBuf = new System.Text.StringBuilder();
        private DateTime _thinkStart;
        private int _workRow = -1;
        private int _lastToolRow = -1;
        private DateTime _workStart;
        private string _currentSessionId = "";
        private readonly Dictionary<string, Agent.ApprovalRequest> _approvals
            = new Dictionary<string, Agent.ApprovalRequest>();
        private readonly Dictionary<ChatRow, string> _approvalRowIds
            = new Dictionary<ChatRow, string>();
        private static ulong _iaCounter;

        public MainForm() : this(false) { }

        public MainForm(bool demo)
        {
            DemoMode = demo;
            _cfg = Settings.Load();
            _agent = new Agent(_cfg);
            if (!demo) WireEvents();
            BuildUi();
            if (!demo)
            {
                RefreshSessions();
                RefreshQuota();
                AppendInfo("ZCode7 已就绪。模型 " + _cfg.Model + "。输入任务开始，或从左侧选择历史会话。");
                Task.Run(() =>
                {
                    _agent.Mcp.ConnectAll(_cfg);
                    if (_agent.Mcp.Errors.Count > 0)
                        BeginInvoke((Action)(() => AppendInfo("MCP 连接问题: " + string.Join("; ", _agent.Mcp.Errors.ToArray()))));
                });
            }
            else SeedDemo();
        }

        private void WireEvents()
        {
            _agent.OnTextDelta += t => Safe(() =>
            {
                EndThinkRow();
                if (_streamRow < 0) _streamRow = _chat.AddRow("assistant");
                _streamText.Append(t);
                _chat.SetAssistantText(_streamRow, _streamText.ToString());
                _chat.ScrollToBottom();
            });
            _agent.OnThinkingDelta += t => Safe(() =>
            {
                if (_thinkRow < 0)
                {
                    _thinkRow = _chat.AddRow("thinking");
                    _thinkStart = DateTime.Now;
                }
                _thinkBuf.Append(t);
                _chat.SetThinking(_thinkRow, _thinkBuf.ToString(), "", true);
                _chat.ScrollToBottom();
            });
            _agent.OnAssistantText += t => Safe(() =>
            {
                if (_streamRow >= 0)
                {
                    _chat.SetAssistantText(_streamRow, t);
                    _streamRow = -1;
                    _streamText.Length = 0;
                }
            });
            _agent.OnToolStart += (name, argsJson) => Safe(() =>
            {
                EndThinkRow();
                if (_streamRow >= 0) { _chat.SetAssistantText(_streamRow, _streamText.ToString()); _streamRow = -1; _streamText.Length = 0; }
                _lastToolRow = _chat.AddRow("tool");
                FillToolMeta(_lastToolRow, name, argsJson);
                _chat.ScrollToBottom();
            });
            _agent.OnToolDone += (name, result) => Safe(() =>
            {
                if (_lastToolRow >= 0)
                {
                    _chat.SetToolResult(_lastToolRow, result == null ? "" : result);
                    _chat.ScrollToBottom();
                }
                _lastToolRow = -1;
            });
            _agent.OnStatus += s => Safe(() =>
            {
                if (_workRow >= 0 && !string.IsNullOrEmpty(s))
                    _chat.SetWorkStatus(_workRow, s, true);
            });
            _agent.OnError += s => Safe(() =>
            {
                AppendInfo("错误: " + s);
                EndThinkRow();
                EndWorkRow();
                _composer.SetRunning(false);
            });
            _agent.OnTurnDone += () => Safe(() =>
            {
                EndThinkRow();
                if (_streamRow >= 0) { _chat.SetAssistantText(_streamRow, _streamText.ToString()); _streamRow = -1; _streamText.Length = 0; }
                if (_workRow >= 0)
                {
                    _chat.SetWorkStatus(_workRow, Elapsed(DateTime.Now - _workStart), false);
                    _workRow = -1;
                }
                _composer.SetRunning(false);
                UpdateComposerUsage();
                RefreshQuota();
                if (!string.IsNullOrEmpty(_agent.SessionId) || _agent.Messages.Count > 0)
                    Persist();
                RefreshSessions();
            });
            _agent.OnTodosChanged += () => Safe(RenderTodos);
            _agent.OnApprovalNeeded += req => Safe(() =>
            {
                string interactionId = "ia-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _approvals[interactionId] = req;
                AppendInfo("工具待确认: " + req.ToolName + " (interactionId=" + interactionId + ")");
            });
        }

        private void EndThinkRow()
        {
            if (_thinkRow >= 0)
            {
                _chat.SetThinking(_thinkRow, _thinkBuf.ToString(),
                    Math.Max(1, (int)(DateTime.Now - _thinkStart).TotalSeconds) + " 秒", false);
                _thinkRow = -1;
                _thinkBuf.Length = 0;
            }
        }

        private void EndWorkRow()
        {
            if (_workRow >= 0)
            {
                _chat.SetWorkStatus(_workRow, "", false);
                _workRow = -1;
            }
        }

        private void FillToolMeta(int rowId, string toolName, string argsJson)
        {
            string args = argsJson == null ? "" : argsJson;
            int addLines = -1;
            try
            {
                var d = MiniJson.ParseObject(argsJson);
                if (d != null)
                {
                    if (toolName == "Bash") args = MiniJson.GetStr(d, "command", "");
                    else if (toolName == "Write" || toolName == "Edit" || toolName == "Read")
                    {
                        args = MiniJson.GetStr(d, "path", "");
                        if (args.Length == 0) args = MiniJson.GetStr(d, "file_path", "");
                        if (toolName == "Write")
                        {
                            var content = MiniJson.GetStr(d, "content", "");
                            if (content.Length > 0) addLines = content.Split('\n').Length;
                        }
                    }
                    else if (toolName == "Glob" || toolName == "Grep") args = MiniJson.GetStr(d, "pattern", "");
                    else if (toolName == "WebFetch") args = MiniJson.GetStr(d, "url", "");
                    else if (toolName == "Skill") args = MiniJson.GetStr(d, "name", "");
                    else if (toolName == "Todo") args = argsJson;
                }
            }
            catch { }
            _chat.SetToolArgs(rowId, toolName, args, addLines);
        }

        private void Safe(Action a)
        {
            try
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)(() =>
                    {
                        try { a(); }
                        catch (Exception ex) { ZLog.Log("UI-OP: " + ex); }
                    }));
            }
            catch (Exception ex) { ZLog.Log("UI-BEGIN: " + ex); }
        }

        // ---------------- UI ----------------

        private void BuildUi()
        {
            Text = "ZCode Classic";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Width = 1440; Height = 900;
            StartPosition = FormStartPosition.Manual;
            if (DemoMode) Location = new Point(0, 0); else StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.BaseFont;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;


            _mainArea = new Panel();
            _mainArea.Dock = DockStyle.Fill;
            _mainArea.BackColor = Theme.Bg;

            // ---- chat page (header + chat + composer + status panel) ----
            _chatPage = new Panel();
            _chatPage.Dock = DockStyle.Fill;
            _chatPage.BackColor = Theme.Bg;

            _chat = new ChatHost(_agent, _cfg);
            _chat.Dock = DockStyle.Fill;
            _chat.BrowserToggleRequested += url => BeginInvoke((Action)(() => ToggleBrowser(url)));

            _composer = new Composer();
            _composer.Dock = DockStyle.Bottom;
            _composer.Height = 110;
            _composer.Send += t => SendInput(t);
            _composer.Stop += () => _agent.Cancel();
            _composer.PermissionSelected += mode =>
            {
                _cfg.PermissionMode = mode;
                try { _cfg.Save(); } catch { }
                _composer.UpdateFromConfig(_cfg);
                string zh = mode == "yolo" ? "完全访问" : (mode == "build" ? "手动确认" : "智能确认");
                AppendInfo("权限模式已切换: " + zh + (mode == "yolo" ? "（所有工具自动执行）" : mode == "build" ? "（所有工具执行前需确认）" : "（读取类自动执行，写入/命令需确认）"));
            };
            _chat.ApprovalDecided += (interactionId, allow) =>
            {
                if (interactionId != null && _approvals.TryGetValue(interactionId, out var req))
                {
                    _approvals.Remove(interactionId);
                    req.Tcs.TrySetResult(allow);
                }
            };
            _composer.ModelSelected += id =>
            {
                _cfg.Model = id;
                try { _cfg.Save(); } catch { }
                _composer.UpdateFromConfig(_cfg);
                AppendInfo("模型已切换: " + id);
            };
            _composer.ThinkSelected += key =>
            {
                _cfg.ThinkingLevel = key;
                _cfg.ThinkingEnabled = key != "off";
                try { _cfg.Save(); } catch { }
                _composer.UpdateFromConfig(_cfg);
            };

            _header = new HeaderBar();
            _header.Dock = DockStyle.Top;
            _header.Height = 48;
            _header.SettingsClick += (s, e) => ShowSettings();
            _header.CompactClick += (s, e) => DoCompact();
            _header.TodoClick += (s, e) => { _statusPanel.Visible = !_statusPanel.Visible; };

            _statusPanel = new StatusPanel();
            _statusPanel.Visible = false;

            // 官方 UI 模式：WebView2 占满整窗（侧栏/输入框/设置都在官方页面里，
            // 数据由 C# RPC 后端供给）。原生侧栏/头部/输入框保留实例但不加入窗口，
            // 相关引用保持可用；旧版纯原生界面可在 Gitee 历史里找到。
            _chatPage.Controls.Add(_chat);

            // ---- skills page ----
            _skillsPage = new SkillsPage();
            _skillsPage.Dock = DockStyle.Fill;
            _skillsPage.Visible = false;
            _skillsPage.InvokeSkill += name => ShowChatPage();
            _skillsPage.InvokeSkill += name => SendInput("/" + name);
            _skillsPage.BackRequested += () => ShowChatPage();

            _mainArea.Controls.Add(_chatPage);
            _mainArea.Controls.Add(_skillsPage);

            _sidebar = new Sidebar();
            _sidebar.Dock = DockStyle.Left;
            _sidebar.Width = 264;
            _sidebar.Visible = false;   // 原生侧栏下线：官方 UI 自带侧栏
            _sidebar.NewTask += () => { ShowChatPage(); NewSession(); };
            _sidebar.OpenSession += id => { ShowChatPage(); LoadSessionById(id); };
            _sidebar.ShowSkills += () => ShowSkillsPage();
            _sidebar.Settings += () => ShowSettings();
            _sidebar.RenameSession += id => RenameSession(id);
            _sidebar.DeleteSession += id => DeleteSession(id);

            Controls.Add(_mainArea);
            _sidebar.Hide();

            _tick = new Timer();
            _tick.Interval = 1000;
            _tick.Tick += (s, e) =>
            {
                if (_workRow >= 0)
                {
                    _chat.SetWorkStatus(_workRow, Elapsed(DateTime.Now - _workStart), true);
                }
            };
            _tick.Start();

            _mainArea.Resize += (s, e) => LayoutStatusPanel();
            LocationChanged += (s, e) => LayoutStatusPanel();
            // dump diagnostics after UI settles
            var diagTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            diagTimer.Tick += (s, e) => { diagTimer.Stop(); try { _chat.DumpDiag(); } catch { } };
            diagTimer.Start();
            Activated += (s, e) => { if (_statusPanel != null && _statusPanel.Visible) _statusPanel.RaiseAbove(Handle); };
            Shown += (s, e) => { if (_statusPanel != null && _statusPanel.Visible) _statusPanel.RaiseAbove(Handle); };

            // composer usage text (session tokens / context window)
            UpdateComposerUsage();

            // prefetch real model list for the composer dropdown
            Task.Run(() =>
            {
                try
                {
                    var models = ApiClient.FetchModels(_cfg.ApiBase, _cfg.ApiKey);
                    Safe(() => _composer.SetModels(models));
                }
                catch { }
            });
            _composer.UpdateFromConfig(_cfg);
        }

        private void ShowChatPage()
        {
            _skillsPage.Visible = false;
            _chatPage.Visible = true;
            _chatPage.BringToFront();
            _composer.FocusInput();
        }

        private void ShowSkillsPage()
        {
            _chatPage.Visible = false;
            _skillsPage.Visible = true;
            _skillsPage.BringToFront();
            _skillsPage.Reload();
        }

        private void UpdateComposerUsage()
        {
            int used = Compact.EstimateMessagesTokens(_agent.Messages);
            _composer.UsageText = used > 0
                ? (used >= 1000 ? (used / 1000.0).ToString("0.#") + "k" : used.ToString()) + " / " + (_cfg.ContextWindowTokens / 1000) + "k"
                : "";
            _composer.Invalidate();
        }

        private void RefreshQuota()
        {
            string key = _cfg.ApiKey;
            Task.Run(() =>
            {
                var u = ZhipuUsage.Fetch(key);
                if (u == null) return;
                string text = u.Summary;
                Color c = u.PctWeek >= 0 ? ZhipuUsage.UsageColor(u.PctWeek) : Theme.TextSubtle;
                Safe(() => { _sidebar.UsageText = text; _sidebar.UsageColor = c; _sidebar.Invalidate(); });
            });
        }

        private void LayoutStatusPanel()
        {
            if (_statusPanel == null) return;
            try { BeginInvoke((Action)(() => DoLayoutStatusPanel())); } catch { }
        }

        private void DoLayoutStatusPanel()
        {
            if (_statusPanel == null) return;
            int cardH = _statusPanel.Items.Count == 0 ? 56 : Math.Min(420, 40 + _statusPanel.Items.Count * 26 + 24);
            var p = _chatPage.PointToScreen(new Point(_chatPage.Width - 320 - 16 - 18, 12));
            _statusPanel.SetLayout(p, 320, cardH);
        }

        private static string Elapsed(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + " 秒";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " 分";
            if (t.TotalDays < 1) return (int)t.TotalHours + " 小时";
            return (int)t.TotalDays + " 天";
        }

        private static string Relative(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "刚刚";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "分钟";
            if (d.TotalDays < 1) return (int)d.TotalHours + "小时";
            return (int)d.TotalDays + "天";
        }

        // ---------------- sessions ----------------

        private void RefreshSessions()
        {
            _sidebar.Items.Clear();
            try
            {
                foreach (var m in SessionStore.List())
                {
                    _sidebar.Items.Add(new SessionItem
                    {
                        Id = m.Id,
                        Title = string.IsNullOrEmpty(m.Title) ? m.Id : m.Title,
                        Time = Relative(m.Updated),
                        Selected = m.Id == _agent.SessionId
                    });
                }
            }
            catch { }
            _sidebar.Invalidate();
        }

        private void LoadSessionById(string id)
        {
            if (_agent.IsBusy) { AppendInfo("正在处理任务"); return; }
            try
            {
                var m = SessionStore.List().FirstOrDefault(x => x.Id == id);
                if (m == null) return;
                _agent.LoadSession(SessionStore.Load(m.Id), SessionStore.LoadTodos(m.Id), m.Id, m.Title);
                _chat.ClearRows();
                _streamRow = -1;
                foreach (var msg in _agent.Messages)
                {
                    string role = MiniJson.GetStr(msg, "role", "");
                    string c = MiniJson.GetStr(msg, "content", "");
                    if (role == "user" && c.Length > 0)
                    {
                        var uid = _chat.AddRow("user");
                        _chat.SetUserText(uid, c);
                    }
                    else if (role == "assistant" && c.Length > 0)
                    {
                        var aid = _chat.AddRow("assistant");
                        _chat.SetAssistantText(aid, c);
                    }
                }
                _chat.ScrollToBottom();
                _header.Title = string.IsNullOrEmpty(m.Title) ? m.Id : m.Title;
                RenderTodos();
                RefreshSessions();
            }
            catch (Exception ex) { MessageBox.Show("加载失败: " + ex.Message); }
        }

        private void RenameSession(string id)
        {
            var m = SessionStore.List().FirstOrDefault(x => x.Id == id);
            string cur = m != null && !string.IsNullOrEmpty(m.Title) ? m.Title : id;
            string renamed = PromptText("重命名会话", "新的会话标题：", cur);
            if (renamed == null || renamed.Trim().Length == 0 || renamed == cur) return;
            SessionStore.SetTitle(id, renamed.Trim());
            if (id == _agent.SessionId) _agent.Title = renamed.Trim();
            RefreshSessions();
        }

        private void DeleteSession(string id)
        {
            if (MessageBox.Show("删除这个会话？不可恢复。", "删除会话",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (id == _agent.SessionId)
            {
                _agent.NewSession();
                _chat.ClearRows();
                _header.Title = "新任务";
                RenderTodos();
            }
            SessionStore.Delete(id);
            RefreshSessions();
            AppendInfo("会话已删除。");
        }

        private string PromptText(string title, string label, string initial)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(420, 160);
                f.BackColor = Theme.Bg;
                f.MaximizeBox = false; f.MinimizeBox = false;
                var l = new Label { Text = label, Location = new Point(14, 14), AutoSize = true, ForeColor = Theme.TextSubtle };
                var tb = new TextBox { Location = new Point(14, 38), Width = 370, BackColor = Theme.Input, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle, Text = initial };
                var ok = new Button { Text = "确定", Location = new Point(216, 74), Size = new Size(80, 30), BackColor = Theme.Brand, ForeColor = Theme.TextInverse, FlatStyle = FlatStyle.Flat };
                var cancel = new Button { Text = "取消", Location = new Point(304, 74), Size = new Size(80, 30), BackColor = Theme.Panel, ForeColor = Theme.TextPrimary, FlatStyle = FlatStyle.Flat };
                ok.Click += (s2, e2) => { f.DialogResult = DialogResult.OK; };
                cancel.Click += (s2, e2) => { f.DialogResult = DialogResult.Cancel; };
                f.Controls.Add(l); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok;
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        private void NewSession()
        {
            if (_agent.IsBusy) { AppendInfo("正在处理任务"); return; }
            Persist();
            _agent.NewSession();
            _currentSessionId = "";
            _streamRow = -1; _workRow = -1; _lastToolRow = -1; _streamText.Length = 0; _thinkBuf.Length = 0;
            _chat.ClearRows();
            _header.Title = "新任务";
            RenderTodos();
            AppendInfo("新任务已创建。");
            RefreshSessions();
        }

        // ---------------- sending ----------------

        private void SendInput(string text)
        {
            if (_agent.IsBusy) { AppendInfo("正在处理上一条消息，请稍候"); return; }

            if (text == "/new") { NewSession(); return; }
            if (text == "/compact") { DoCompact(); return; }
            if (text == "/help")
            {
                AppendInfo("可用命令：/new 新建对话 · /compact 压缩上下文 · /help 帮助 · /技能名 调用技能（如 /explore）。技能列表见侧栏\"技能\"。");
                return;
            }
            if (text.StartsWith("/") && text.Length > 1)
            {
                string skillName = text.Substring(1).Split(new[] { ' ' }, 2)[0];
                if (Skills.Find(skillName) != null)
                {
                    var body = Skills.GetBody(skillName);
                    _agent.Messages.Add(MkMsg("user",
                        "调用技能 " + skillName + "：\n" + body + "\n\n请遵循以上技能指导处理接下来的请求。"));
                    AppendInfo("技能 " + skillName + " 已加载，请输入具体请求。");
                    return;
                }
            }

            if (string.IsNullOrEmpty(_agent.SessionId))
                _agent.SessionId = SessionStore.NewId();

            var ur = _chat.AddRow("user");
            _chat.SetUserText(ur, text);
            StartWorkRow();
            _composer.SetRunning(true);
            UpdateComposerUsage();
            Task.Run(() => _agent.SendAsync(text));
        }

        private void StartWorkRow()
        {
            _workRow = _chat.AddRow("work");
            _workStart = DateTime.Now;
            _chat.SetWorkStatus(_workRow, "", true);
            _chat.ScrollToBottom();
        }

        private Dictionary<string, object> MkMsg(string role, string content)
        {
            var m = MiniJson.Obj();
            m["role"] = role;
            m["content"] = content;
            _agent.Messages.Add(m);
            return m;
        }

        private void DoCompact()
        {
            if (_agent.IsBusy) return;
            Task.Run(async () =>
            {
                try
                {
                    string msg = await _agent.CompactNow();
                    Safe(() => AppendInfo(msg));
                }
                catch (Exception ex)
                {
                    Safe(() => AppendInfo("压缩失败: " + ex.Message));
                }
            });
        }

        private void Persist()
        {
            try
            {
                if (_agent.Messages.Count == 0) return;
                if (string.IsNullOrEmpty(_agent.SessionId))
                    _agent.SessionId = SessionStore.NewId();
                SessionStore.Save(_agent.Messages, _agent.SessionId, _agent.Title, Tools.Todos);
            }
            catch { }
        }

        // ---------------- todos ----------------

        private void RenderTodos()
        {
            _statusPanel.Items.Clear();
            var todos = _agent.Todos;
            foreach (var t in todos)
            {
                _statusPanel.Items.Add(new TodoItemView
                {
                    Status = MiniJson.GetStr(t, "status", "pending"),
                    Text = MiniJson.GetStr(t, "content", "")
                });
            }
            bool show = todos.Count > 0;
            if (show && !_statusPanel.Visible) _statusPanel.Show(this);
            if (!show && _statusPanel.Visible && !_agent.IsBusy) _statusPanel.Hide();
            LayoutStatusPanel();
            _statusPanel.Repaint();
        }

        // ---------------- misc ----------------

        private void AppendInfo(string text)
        {
            var id = _chat.AddRow("info");
            _chat.SetInfo(id, text);
            _chat.ScrollToBottom();
        }

        private void ShowSettings()
        {
            using (var f = new SettingsForm(_cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    _cfg = f.Result;
                    _cfg.Save();
                    Task.Run(() => _agent.Mcp.ConnectAll(_cfg));
                    _composer.ModelName = _cfg.Model;
                    AppendInfo("设置已保存。");
                }
            }
        }

        // 官方桌面壳负责的应用级快捷键：web 页只显示标签不派发，由宿主转发给页面按钮
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.N))
            {
                _chat?.Js("document.querySelector('[data-testid=conversation-new-task]')?.click()");
                return true;
            }
            if (keyData == (Keys.Control | Keys.K))
            {
                _chat?.Js("Array.from(document.querySelectorAll('button')).find(function(b){return (b.innerText||'').indexOf('搜索')>=0&&b.innerText.indexOf('Ctrl')>=0;})?.click()");
                return true;
            }
            if (keyData == (Keys.Control | Keys.U))
            {
                ToggleBrowser(null);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 内置浏览器侧栏：Ctrl+U 或官方 UI 悬浮按钮呼出；url 非空时定位到该地址
        private void ToggleBrowser(string url)
        {
            if (_browserPane == null)
            {
                _browserPane = new BrowserPane();
                _browserPane.CloseRequested += () => ToggleBrowser(null);
                _browserPane.StatusMessage += m => BeginInvoke((Action)(() => AppendInfo(m)));
                _chatPage.Controls.Add(_browserPane);
                _chat.BringToFront(); // Fill 控件保持 index 0，Right 停靠吃掉右侧剩余空间
            }
            if (_browserPane.Visible && string.IsNullOrEmpty(url))
            {
                _browserPane.Visible = false;
                _browserPane.Hide();
            }
            else
            {
                _browserPane.Show();
                _browserPane.Visible = true;
                if (!string.IsNullOrEmpty(url)) _browserPane.NavigateTo(url);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { Persist(); } catch { }
            try { _agent.Mcp.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        // ---------------- demo seed (--uitest) ----------------

        private void SeedDemo()
        {
            _header.Title = "ZCode7 UI 复刻预览";
            var u1 = _chat.AddRow("user");
            _chat.SetUserText(u1, "其实你还是你按照zcode的ui来实现，可以100%或者95%复刻吗？只是功能精简，但是UI是不是可以别精简？");

            var a1 = _chat.AddRow("assistant");
            _chat.SetAssistantText(a1, "可以，而且这正是\"忠实复刻\"的正路。先说结论：**静态观感（配色/布局/卡片/图标/字体）95% 复刻是现实的**——因为 `ZCode` 桌面版本质上就是 HTML/CSS，所有颜色、间距、图标都是确定的资源，可以照抄；动态效果（动画、markdown 逐字渲染的细微行为）在 WinForms 里是近似。功能精简、UI 不精简，没问题。");

            var t1 = _chat.AddRow("tool");
            FillToolMeta(t1, "Read", "{\"path\":\"packages/ui/src/styles.css\"}");
            _chat.SetToolResult(t1, "2053 行样式表，@theme 块定义了全部语义 token。");

            var t2 = _chat.AddRow("tool");
            FillToolMeta(t2, "Bash", "{\"command\":\"git clone --depth 1 https://github.com/zai-org/ZCode.git zcode-src\"}");
            _chat.SetToolResult(t2, "Cloning into 'zcode-src'...\nUpdating files: 100% (7060/7060), done.");

            var a2 = _chat.AddRow("assistant");
            _chat.SetAssistantText(a2, "关键发现：你用的 ZCode 是**浅色主题**（白底 + 浅灰侧栏），而 ZCode7 v2 是暗色的，方向要掰过来。\n\n- 侧栏 264px、`bg #f0f0f0`\n- 气泡圆角 12、右上角 4px\n- 发送按钮 28×28 黑色圆角方块\n\n映射实现分三步：\n\n1. 提取 `.theme-zai-light` 全部语义 token\n2. 换算 tailwind 单位（1 单位 = 4px）\n3. 写入 `Theme.cs` 静态字段\n\n```\npublic static readonly Color Bg = FromHex(\"#f8f8f8\");\n```\n\n全部来自 `styles.css` 的 `.theme-zai-light` 块。");

            _statusPanel.Items.Add(new TodoItemView { Text = "Theme.cs 换 zai-light 色板", Status = "completed" });
            _statusPanel.Items.Add(new TodoItemView { Text = "Icons.cs lucide 渲染器", Status = "completed" });
            _statusPanel.Items.Add(new TodoItemView { Text = "WebView2 聊天区", Status = "in_progress" });
            _statusPanel.Items.Add(new TodoItemView { Text = "截图对比迭代", Status = "pending" });
            LayoutStatusPanel();
            if (!_statusPanel.Visible) _statusPanel.Show(this);
            _chat.ScrollToBottom();
        }
}

    /// <summary>48px session header: folder + title + ellipsis menu (ZCode-faithful).</summary>
    public class HeaderBar : Panel
    {
        public string Title = "";
        public event EventHandler SettingsClick;
        public event EventHandler CompactClick;
        public event EventHandler TodoClick;

        private readonly Rectangle _rFolder = new Rectangle(10, 10, 28, 28);
        private Rectangle _rMenu;
        private int _hover = -1;

        public HeaderBar()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover == 0)
                using (var path = Theme.Rounded(_rFolder, 8))
                using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            Icons.Draw(g, "Folder", new Rectangle(_rFolder.X + 6, _rFolder.Y + 6, 16, 16), Theme.TextSubtle);

            TextRenderer.DrawText(g, Title, new Font(Theme.BaseFont, FontStyle.Bold),
                new Point(_rFolder.Right + 8, 15), Theme.TextPrimary,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            int titleW = TextRenderer.MeasureText(Title, new Font(Theme.BaseFont, FontStyle.Bold),
                Size.Empty, TextFormatFlags.NoPadding).Width;
            _rMenu = new Rectangle(_rFolder.Right + 8 + Math.Min(titleW, 400) + 8, 10, 28, 28);
            DrawBtn(g, _rMenu, "Ellipsis", 1);

            using (var p = new Pen(Color.FromArgb(13, Theme.Border)))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        public Rectangle MenuScreenRect()
        {
            var pt = PointToScreen(_rMenu.Location);
            return new Rectangle(pt, _rMenu.Size);
        }

        private void DrawBtn(Graphics g, Rectangle r, string icon, int idx)
        {
            if (_hover == idx + 1)
                using (var path = Theme.Rounded(r, 8))
                using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            Icons.Draw(g, icon, new Rectangle(r.X + 6, r.Y + 6, 16, 16), Theme.TextSubtle);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            if (_rMenu.Contains(e.Location)) h = 1;
            else if (_rFolder.Contains(e.Location)) h = 0;
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_hover == 1)
            {
                var items = new List<Popover.Item>
                {
                    new Popover.Item
                    {
                        Text = "任务清单", Icon = "ListTree",
                        OnPick = () => { if (TodoClick != null) TodoClick(this, EventArgs.Empty); }
                    },
                    new Popover.Item
                    {
                        Text = "压缩上下文", Icon = "Wrench",
                        OnPick = () => { if (CompactClick != null) CompactClick(this, EventArgs.Empty); }
                    },
                    new Popover.Item
                    {
                        Text = "设置", Icon = "Settings",
                        OnPick = () => { if (SettingsClick != null) SettingsClick(this, EventArgs.Empty); }
                    }
                };
                Popover.Show(this, MenuScreenRect(), items);
            }
        }
    }
}
