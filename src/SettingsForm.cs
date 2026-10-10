// ZCode7 - settings dialog (dark, grid layout, model list fetch)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZCode7
{
    public class SettingsForm : Form
    {
        public Settings Result;

        private TextBox _apiBase;
        private TextBox _apiKey;
        private ComboBox _model;
        private ComboBox _thinking;
        private TextBox _temperature;
        private TextBox _maxCmdTimeout;
        private TextBox _maxToolResult;
        private CheckBox _autoCompact;
        private TextBox _contextWindow;
        private TextBox _outputReserve;
        private TextBox _bufferTokens;
        private TextBox _thresholdPercent;
        private TextBox _mcpJson;
        private Button _fetchBtn;
        private bool _autoFetching;

        private static readonly string[] ThinkKeys = { "off", "low", "high", "max" };
        private static readonly string[] ThinkNames = { "关", "低", "高", "最大" };

        public SettingsForm(Settings cfg)
        {
            Text = "设置";
            Width = 780; Height = 688;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.BaseFont;
            BackColor = Theme.Bg;
            ForeColor = Theme.TextPrimary;

            // ---- bottom bar (buttons + hint) ----
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;
            bottom.BackColor = Theme.Bg;
            var ok = DarkButton("保存", Theme.Accent);
            ok.Size = new Size(84, 30);
            ok.Location = new Point(Width - 210, 9);
            ok.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            ok.Click += (s, e) => { if (Save()) DialogResult = DialogResult.OK; };
            var cancel = DarkButton("取消", Theme.Surface);
            cancel.Size = new Size(84, 30);
            cancel.Location = new Point(Width - 115, 9);
            cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);
            AcceptButton = ok;

            // ---- root grid ----
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 14, 18, 6);
            root.BackColor = Theme.Bg;
            root.AutoScroll = true;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);
            root.BringToFront();

            Section(root, "接口");
            var g1 = Grid(root, 130, 1.0);
            RowLabel(g1, 0, "API 地址");
            _apiBase = Input();
            _apiBase.Text = cfg.ApiBase;
            g1.Controls.Add(_apiBase, 1, 0);
            RowLabel(g1, 1, "API Key");
            _apiKey = Input();
            _apiKey.Text = cfg.ApiKey;
            g1.Controls.Add(_apiKey, 1, 1);

            Section(root, "模型与生成");
            var g2 = Grid(root, 92, 0.55, 115, 0.45);

            RowLabel(g2, 0, "模型");
            _model = new ComboBox();
            _model.Dock = DockStyle.Fill;
            _model.BackColor = Theme.Input;
            _model.ForeColor = Theme.TextPrimary;
            _model.DropDownStyle = ComboBoxStyle.DropDown; // editable: list pick or manual id
            _model.Text = cfg.Model;
            g2.Controls.Add(_model, 1, 0);
            _fetchBtn = DarkButton("获取模型列表", Theme.Card);
            _fetchBtn.Height = 26;
            _fetchBtn.Dock = DockStyle.Fill;
            _fetchBtn.Click += (s, e) => FetchModels(false);
            g2.Controls.Add(_fetchBtn, 2, 0);
            g2.SetColumnSpan(_fetchBtn, 2);

            RowLabel(g2, 1, "思考强度");
            _thinking = new ComboBox();
            _thinking.DropDownStyle = ComboBoxStyle.DropDownList;
            _thinking.Dock = DockStyle.Fill;
            _thinking.BackColor = Theme.Input;
            _thinking.ForeColor = Theme.TextPrimary;
            _thinking.Items.AddRange(ThinkNames);
            int ti = Array.IndexOf(ThinkKeys, cfg.ThinkingLevel);
            _thinking.SelectedIndex = ti >= 0 ? ti : 3;
            g2.Controls.Add(_thinking, 1, 1);
            RowLabel(g2, 1, "Temperature", 2);
            _temperature = Input();
            _temperature.Text = cfg.Temperature.ToString();
            g2.Controls.Add(_temperature, 3, 1);

            Section(root, "工具");
            var g3 = Grid(root, 120, 0.5, 130, 0.5);
            RowLabel(g3, 0, "命令超时(秒)");
            _maxCmdTimeout = Input();
            _maxCmdTimeout.Text = cfg.MaxCommandTimeoutSec.ToString();
            g3.Controls.Add(_maxCmdTimeout, 1, 0);
            RowLabel(g3, 0, "工具结果上限", 2);
            _maxToolResult = Input();
            _maxToolResult.Text = cfg.MaxToolResultChars.ToString();
            g3.Controls.Add(_maxToolResult, 3, 0);

            Section(root, "上下文压缩");
            _autoCompact = new CheckBox();
            _autoCompact.Text = "自动压缩上下文（对齐 ZCode compact 策略）";
            _autoCompact.Checked = cfg.AutoCompactEnabled;
            _autoCompact.ForeColor = Theme.TextPrimary;
            _autoCompact.Dock = DockStyle.Fill;
            _autoCompact.Height = 26;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.Controls.Add(_autoCompact);

            var g4 = Grid(root, 120, 0.34, 90, 0.33);
            RowLabel(g4, 0, "上下文窗口");
            _contextWindow = Input();
            _contextWindow.Text = cfg.ContextWindowTokens.ToString();
            g4.Controls.Add(_contextWindow, 1, 0);
            RowLabel(g4, 0, "输出预留", 2);
            _outputReserve = Input();
            _outputReserve.Text = cfg.OutputReserveTokens.ToString();
            g4.Controls.Add(_outputReserve, 3, 0);
            RowLabel(g4, 1, "缓冲");
            _bufferTokens = Input();
            _bufferTokens.Text = cfg.BufferTokens.ToString();
            g4.Controls.Add(_bufferTokens, 1, 1);
            RowLabel(g4, 1, "阈值(%)", 2);
            _thresholdPercent = Input();
            _thresholdPercent.Text = cfg.ThresholdPercent.ToString();
            g4.Controls.Add(_thresholdPercent, 3, 1);

            Section(root, "MCP 服务器配置（JSON，留空为无）");
            _mcpJson = new TextBox();
            _mcpJson.Multiline = true;
            _mcpJson.ScrollBars = ScrollBars.Both;
            _mcpJson.Text = cfg.McpServers.Count > 0 ? MiniJson.Serialize(cfg.McpServers) : "{}";
            _mcpJson.Height = 96;
            _mcpJson.Dock = DockStyle.Fill;
            _mcpJson.BackColor = Theme.Input;
            _mcpJson.ForeColor = Theme.TextPrimary;
            _mcpJson.BorderStyle = BorderStyle.FixedSingle;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.Controls.Add(_mcpJson);

            var hint = new Label();
            hint.Text = "提示：模型、思考强度也可在主界面状态栏随时切换；保存后立即生效。";
            hint.ForeColor = Theme.TextSubtlest;
            hint.Font = Theme.SmallFont;
            hint.AutoSize = true;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.Controls.Add(hint);

            // auto-fetch on open for Zhipu endpoints (silent on failure)
            if (ApiClient.LooksLikeZhipu(cfg.ApiBase) && !string.IsNullOrEmpty(cfg.ApiKey))
                FetchModels(true);
        }

        private void FetchModels(bool silent)
        {
            if (_fetchBtn == null || _autoFetching) return;
            string ab = _apiBase.Text.Trim(), ak = _apiKey.Text.Trim();
            if (string.IsNullOrEmpty(ab)) { if (!silent) MessageBox.Show(this, "请先填 API 地址"); return; }
            _autoFetching = true;
            _fetchBtn.Enabled = false;
            _fetchBtn.Text = "获取中…";
            Task.Run((Action)(() =>
            {
                List<ApiClient.ModelInfo> list = null;
                string err = null;
                try { list = ApiClient.FetchModels(ab, ak); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _autoFetching = false;
                        _fetchBtn.Enabled = true;
                        _fetchBtn.Text = "获取模型列表";
                        if (err != null)
                        {
                            if (!silent) MessageBox.Show(this, "获取失败: " + err, "模型列表");
                            return;
                        }
                        if (list == null || list.Count == 0)
                        {
                            if (!silent) MessageBox.Show(this, "端点未返回模型。", "模型列表");
                            return;
                        }
                        string cur = _model.Text.Trim();
                        _model.Items.Clear();
                        foreach (var m in list) _model.Items.Add(m);
                        int best = -1;
                        for (int i = 0; i < list.Count; i++)
                            if (string.Equals(list[i].Id, cur, StringComparison.OrdinalIgnoreCase)) best = i;
                        if (best < 0)
                            for (int i = 0; i < list.Count; i++)
                                if (string.Equals(list[i].Id, cur.Replace("_", "-"), StringComparison.OrdinalIgnoreCase)) best = i;
                        if (best >= 0) _model.SelectedIndex = best;
                        else _model.Text = cur;
                        if (!silent) MessageBox.Show(this, "已获取 " + list.Count + " 个模型。", "模型列表");
                    }));
                }
                catch { }
            }));
        }

        private Label Section(TableLayoutPanel root, string text)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Accent;
            l.Font = new Font(Theme.BaseFont, FontStyle.Bold);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            l.AutoSize = false;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.Controls.Add(l);
            return l;
        }

        // widths: >=1 absolute px; 0<x<1 percent of remaining space
        private TableLayoutPanel Grid(TableLayoutPanel root, params double[] widths)
        {
            var g = new TableLayoutPanel();
            g.ColumnCount = widths.Length;
            g.Dock = DockStyle.Fill;
            g.Height = 64;
            g.Margin = new Padding(0, 0, 0, 8);
            g.BackColor = Theme.Bg;
            foreach (var w in widths)
                g.ColumnStyles.Add(w >= 1
                    ? new ColumnStyle(SizeType.Absolute, (float)w)
                    : new ColumnStyle(SizeType.Percent, (float)(w * 100)));
            for (int r = 0; r < 2; r++)
                g.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(g);
            return g;
        }

        private void RowLabel(TableLayoutPanel g, int row, string text, int col = 0)
        {
            var l = new Label();
            l.Text = text + "：";
            l.ForeColor = Theme.TextSubtle;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.Dock = DockStyle.Fill;
            g.Controls.Add(l, col, row);
        }

        private TextBox Input()
        {
            var t = new TextBox();
            t.Dock = DockStyle.Fill;
            t.BackColor = Theme.Input;
            t.ForeColor = Theme.TextPrimary;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        private Button DarkButton(string text, Color back)
        {
            var b = new Button();
            b.Text = text;
            b.BackColor = back;
            b.ForeColor = Theme.TextPrimary;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.Font = Theme.BaseFont;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private bool Save()
        {
            var s = new Settings();
            s.ApiBase = _apiBase.Text.Trim();
            s.ApiKey = _apiKey.Text.Trim();
            var sel = _model.SelectedItem as ApiClient.ModelInfo;
            s.Model = sel != null ? sel.Id : _model.Text.Trim();
            if (string.IsNullOrEmpty(s.Model)) s.Model = "GLM-5.3-Flash";
            s.ThinkingLevel = ThinkKeys[_thinking.SelectedIndex >= 0 ? _thinking.SelectedIndex : 3];
            s.ThinkingEnabled = s.ThinkingLevel != "off";
            double t;
            if (!double.TryParse(_temperature.Text.Trim(), out t)) t = 0.6;
            s.Temperature = t;
            int c;
            if (!int.TryParse(_maxCmdTimeout.Text.Trim(), out c) || c < 10) c = 120;
            s.MaxCommandTimeoutSec = c;
            int m;
            if (!int.TryParse(_maxToolResult.Text.Trim(), out m) || m < 1000) m = 20000;
            s.MaxToolResultChars = m;
            s.AutoCompactEnabled = _autoCompact.Checked;
            int cw;
            if (!int.TryParse(_contextWindow.Text.Trim(), out cw) || cw < 16000) cw = 200000;
            s.ContextWindowTokens = cw;
            int or2;
            if (!int.TryParse(_outputReserve.Text.Trim(), out or2) || or2 < 0) or2 = 32000;
            s.OutputReserveTokens = or2;
            int bf;
            if (!int.TryParse(_bufferTokens.Text.Trim(), out bf) || bf < 0) bf = 13000;
            s.BufferTokens = bf;
            int tp;
            if (!int.TryParse(_thresholdPercent.Text.Trim(), out tp) || tp < 20) tp = 100;
            s.ThresholdPercent = tp;
            try
            {
                var mcps = MiniJson.ParseObject(string.IsNullOrWhiteSpace(_mcpJson.Text) ? "{}" : _mcpJson.Text);
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
            }
            catch (Exception ex)
            {
                MessageBox.Show("MCP 配置 JSON 解析失败: " + ex.Message);
                return false;
            }
            Result = s;
            return true;
        }
    }
}
