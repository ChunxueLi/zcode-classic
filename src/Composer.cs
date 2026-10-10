// ZCode7 - composer: rounded input card + toolbar chips + send (zai-light spec)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZCode7
{
    public class Composer : Panel
    {
        public event Action<string> Send;
        public event Action Stop;

        private readonly TextBox _input;
        private Label _placeholderLabel;
        private string _placeholder = "向 ZCode 提问，使用 @ 添加上下文，使用 / 选择命令或能力";
        private bool _running;
        public string ModelName = "GLM-5.3-Flash";
        public string ThinkKey = "high";
        public string PermissionMode = "auto";
        public string UsageText = "";
        public event Action<string> ModelSelected;
        public event Action<string> ThinkSelected;
        public event Action<string> PermissionSelected;
        private List<ApiClient.ModelInfo> _models;

        private static readonly string[] ThinkKeys = { "off", "low", "high", "max" };
        private static readonly string[] ThinkNames = { "关", "低", "高", "最大" };

        private readonly Rectangle[] _chipRects = new Rectangle[8];
        private int _hoverChip = -1;

        public void SetModels(List<ApiClient.ModelInfo> models)
        {
            _models = models;
            Invalidate();
        }

        public void SetText(string t)
        {
            _input.Text = t;
            _input.SelectionStart = t.Length;
            _input.Focus();
        }

        public void UpdateFromConfig(Settings cfg)
        {
            ModelName = cfg.Model;
            ThinkKey = cfg.ThinkingLevel;
            PermissionMode = cfg.PermissionMode;
            Invalidate();
        }

        public string PermissionName()
        {
            if (PermissionMode == "yolo") return "完全访问";
            if (PermissionMode == "build") return "手动确认";
            return "智能确认";
        }

        public Composer()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;

            _input = new TextBox();
            _input.BorderStyle = BorderStyle.None;
            _input.Multiline = true;
            _input.BackColor = Theme.Input;
            _input.ForeColor = Theme.TextPrimary;
            _input.Font = Theme.BaseFont;
            _input.ScrollBars = ScrollBars.Vertical;
            Controls.Add(_input);
            _placeholderLabel = new Label();
            _placeholderLabel.Text = _placeholder;
            _placeholderLabel.ForeColor = Theme.TextSubtlest;
            _placeholderLabel.Font = Theme.BaseFont;
            _placeholderLabel.BackColor = Theme.Input;
            _placeholderLabel.AutoSize = false;
            _placeholderLabel.Cursor = Cursors.IBeam;
            _placeholderLabel.Click += (s, e) => _input.Focus();
            Controls.Add(_placeholderLabel);
            _input.BringToFront();
            _input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    DoSend();
                }
            };
            _input.TextChanged += (s, e) => { _placeholderLabel.Visible = _input.Text.Length == 0; UpdateInputHeight(); };
            Resize += (s, e) => LayoutInput();
        }

        private int CardWidth()
        {
            int avail = Width - 32;
            return Math.Max(320, Math.Min(896, avail));
        }

        private Rectangle CardRect()
        {
            int cw = CardWidth();
            return new Rectangle((Width - cw) / 2, 8, cw, Height - 16);
        }

        private void LayoutInput()
        {
            var card = CardRect();
            _input.Location = new Point(card.X + 12, card.Y + 12);
            _input.Size = new Size(card.Width - 24, InputHeight());
            _placeholderLabel.Location = _input.Location;
            _placeholderLabel.Size = new Size(card.Width - 24, 20);
            _placeholderLabel.Visible = _input.Text.Length == 0;
        }

        private int InputHeight()
        {
            int lines = _input.Lines != null && _input.Lines.Length > 0 ? _input.Lines.Length : 1;
            return Math.Max(20, Math.Min(160, lines * 20));
        }

        private void UpdateInputHeight()
        {
            LayoutInput();
            Height = TotalHeight();
        }

        public int TotalHeight()
        {
            return 8 + 12 + InputHeight() + 6 + 28 + 12 + 8;
        }

        public void SetRunning(bool running)
        {
            _running = running;
            Invalidate();
        }

        public void FocusInput() { _input.Focus(); }

        private void DoSend()
        {
            string t = _input.Text.Trim();
            if (t.Length == 0) return;
            _input.Clear();
            if (Send != null) Send(t);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = CardRect();

            // input card: rounded 16, border, white bg
            using (var path = Theme.Rounded(card, 16))
            {
                using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }

            // toolbar row
            int ty = card.Bottom - 12 - 28;
            int x = card.X + 12;

            string pIcon = PermissionMode == "yolo" ? "ShieldAlert" : (PermissionMode == "build" ? "Hand" : "ShieldCheck");
            Color pColor = PermissionMode == "yolo" ? Theme.Warning : Theme.TextPrimary;
            x = Chip(g, ref _chipRects[0], x, ty, pIcon, PermissionName(), 0, true, pColor) + 6;

            // right side: model, thinking, send
            var modelText = ModelName;
            var mw = TextRenderer.MeasureText(modelText, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var r1 = new Rectangle(card.Right - 12 - 28 - 6 - mw - 40, ty, mw + 40, 28);
            var thinkText = ThinkNames[Math.Max(0, Array.IndexOf(ThinkKeys, ThinkKey))];
            var tw = TextRenderer.MeasureText(thinkText, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var r2 = new Rectangle(r1.X - 6 - tw - 40, ty, tw + 40, 28);
            var sendRect = new Rectangle(card.Right - 12 - 28, ty, 28, 28);
            _chipRects[1] = r1; _chipRects[2] = r2; _chipRects[3] = sendRect;

            if (UsageText.Length > 0)
            {
                var uw = TextRenderer.MeasureText(UsageText, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, UsageText, Theme.BaseFont,
                    new Point(r2.X - uw - 18, ty + 6), Theme.TextSubtlest, TextFormatFlags.NoPadding);
            }
            ChipAt(g, r1, "Package", modelText);
            ChipAt(g, r2, "Brain", thinkText);
            // level bar: 3px wide, fill by level (off 0 / low 1/3 / high 2/3 / max 1)
            int lv = Math.Max(0, Array.IndexOf(ThinkKeys, ThinkKey));
            var bar = new Rectangle(r2.Right - 12, r2.Y + 6, 3, 16);
            using (var path = Theme.Rounded(bar, 2))
            using (var b = new SolidBrush(Color.FromArgb(26, 13, 13, 13)))
                g.FillPath(b, path);
            int fillH = (int)Math.Round(16 * (lv / 3.0));
            if (fillH > 0)
            {
                var fill = new Rectangle(bar.X, bar.Bottom - fillH, 3, fillH);
                using (var path = Theme.Rounded(fill, 2))
                using (var b = new SolidBrush(Theme.Success))
                    g.FillPath(b, path);
            }

            // send button: black rounded square with white arrow up
            if (_hoverChip == 5)
                using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) { }
            using (var path = Theme.Rounded(sendRect, 8))
            using (var b = new SolidBrush(_running ? Color.FromArgb(80, 13, 13, 13) : Theme.Brand))
                g.FillPath(b, path);
            Icons.Draw(g, _running ? "Square" : "ArrowUp",
                new Rectangle(sendRect.X + 6, sendRect.Y + 6, 16, 16), Theme.TextInverse);
        }

        private int Chip(Graphics g, ref Rectangle rect, int x, int y, string icon, string label, int fixedW, bool paint, Color color)
        {
            int w;
            if (fixedW > 0) w = fixedW;
            else
            {
                w = 16;
                if (!string.IsNullOrEmpty(icon)) w += 18;
                if (!string.IsNullOrEmpty(label))
                    w += TextRenderer.MeasureText(label, Theme.BaseFont, Size.Empty, TextFormatFlags.NoPadding).Width + 6;
                w += 10;
            }
            rect = new Rectangle(x, y, w, 28);
            if (paint) ChipAt(g, rect, icon, label, color);
            return x + w;
        }

        private void ChipAt(Graphics g, Rectangle rect, string icon, string label)
        {
            ChipAt(g, rect, icon, label, Theme.TextPrimary);
        }

        private void ChipAt(Graphics g, Rectangle rect, string icon, string label, Color color)
        {
            bool hov = _hoverChip >= 0 && _chipRects[_hoverChip] == rect;
            if (hov)
                using (var path = Theme.Rounded(rect, 8))
                using (var b = new SolidBrush(Theme.Hover))
                    g.FillPath(b, path);
            int ix = rect.X + 5;
            if (!string.IsNullOrEmpty(icon))
            {
                Icons.Draw(g, icon, new Rectangle(ix, rect.Y + 6, 16, 16), color);
                ix += 18;
            }
            if (!string.IsNullOrEmpty(label))
                TextRenderer.DrawText(g, label, Theme.BaseFont, new Point(ix, rect.Y + 6), color, TextFormatFlags.NoPadding);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < 4; i++)
                if (_chipRects[i].Contains(e.Location)) { h = i; break; }
            if (h != _hoverChip)
            {
                _hoverChip = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // swallow the click that just dismissed a popover so it can't re-trigger anything
            if ((DateTime.UtcNow - Popover.LastClosedUtc).TotalMilliseconds < 250)
            {
                _hoverChip = -1;
                Invalidate();
                return;
            }
            Rectangle anchor(Rectangle chip)
            {
                var p = PointToScreen(chip.Location);
                return new Rectangle(p, chip.Size);
            }
            if (_hoverChip == 3) // send / stop
            {
                if (_running && Stop != null) Stop();
                else DoSend();
                return;
            }
            if (_hoverChip == 0) // permission mode (icons per level, faithful to ZCode)
            {
                var items = new List<Popover.Item>
                {
                    new Popover.Item
                    {
                        Text = "智能确认（读写需确认）", Icon = "ShieldCheck",
                        Checked = PermissionMode == "auto",
                        OnPick = () => { if (PermissionSelected != null) PermissionSelected("auto"); }
                    },
                    new Popover.Item
                    {
                        Text = "手动确认（所有工具）", Icon = "Hand",
                        Checked = PermissionMode == "build",
                        OnPick = () => { if (PermissionSelected != null) PermissionSelected("build"); }
                    },
                    new Popover.Item
                    {
                        Text = "完全访问（不确认）", Icon = "ShieldAlert",
                        Checked = PermissionMode == "yolo",
                        OnPick = () => { if (PermissionSelected != null) PermissionSelected("yolo"); }
                    }
                };
                Popover.Show(this, anchor(_chipRects[0]), items);
                return;
            }
            if (_hoverChip == 1) // model select
            {
                var items = new List<Popover.Item>();
                var list = _models;
                if (list == null || list.Count == 0)
                {
                    foreach (var id in new[] { "GLM-5.3-Flash", "GLM-5.3-FlashX", "GLM-5.3", "GLM-5.2", "GLM-5.1", "GLM-5" })
                        items.Add(new Popover.Item
                        {
                            Text = id,
                            Checked = string.Equals(id, ModelName, StringComparison.OrdinalIgnoreCase),
                            OnPick = () => { if (ModelSelected != null) ModelSelected(id); }
                        });
                }
                else
                {
                    foreach (var mi in list)
                    {
                        var id = mi.Id;
                        items.Add(new Popover.Item
                        {
                            Text = id,
                            Checked = string.Equals(id, ModelName, StringComparison.OrdinalIgnoreCase),
                            OnPick = () => { if (ModelSelected != null) ModelSelected(id); }
                        });
                    }
                }
                Popover.Show(this, anchor(_chipRects[1]), items);
                return;
            }
            if (_hoverChip == 2) // thinking level
            {
                var items = new List<Popover.Item>();
                for (int i = 0; i < ThinkKeys.Length; i++)
                {
                    string key = ThinkKeys[i];
                    items.Add(new Popover.Item
                    {
                        Text = ThinkNames[i],
                        Checked = key == ThinkKey,
                        OnPick = () => { if (ThinkSelected != null) ThinkSelected(key); }
                    });
                }
                Popover.Show(this, anchor(_chipRects[2]), items);
            }
        }
    }
}
