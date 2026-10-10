// ZCode7 - skills management page (list / create / open folder / refresh / invoke)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ZCode7
{
    public class SkillsPage : Panel
    {
        public event Action<string> InvokeSkill;   // "/name" requested
        public event Action BackRequested;

        private List<SkillInfo> _skills = new List<SkillInfo>();
        private readonly List<Rectangle> _rowRects = new List<Rectangle>();
        private readonly Rectangle[] _btnRects = new Rectangle[4];
        private int _hoverRow = -1, _hoverBtn = -1;
        private float _scroll;
        private float _contentH = 1;
        private bool _drag;
        private int _dragY;
        private float _dragStart;
        private DateTime _lastClick;
        private int _lastIndex = -1;

        private static readonly string[] BtnIcons = { "Plus", "Folder", "RotateCcw", "ArrowLeft" };
        private static readonly string[] BtnTips = { "新建技能", "打开技能文件夹", "刷新", "返回对话" };

        public SkillsPage()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            ResizeRedraw = true;
        }

        public void Reload()
        {
            try { _skills = new List<SkillInfo>(Skills.LoadAll(true)); }
            catch { _skills.Clear(); }
            _scroll = 0;
            Invalidate();
        }

        private int ListTop() { return 56; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // header 56px
            TextRenderer.DrawText(g, "技能", new Font(Theme.BaseFont, FontStyle.Bold),
                new Point(16, 16), Theme.TextPrimary, TextFormatFlags.NoPadding);
            int x = Width - 16;
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle(x - 84, 14, 80, 28);
                _btnRects[i] = r;
                x = r.X - 6;
                if (_hoverBtn == i)
                    using (var path = Theme.Rounded(r, 8))
                    using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
                Icons.Draw(g, BtnIcons[i], new Rectangle(r.X + 8, r.Y + 6, 16, 16), Theme.TextPrimary);
                TextRenderer.DrawText(g, BtnTips[i], Theme.SmallFont,
                    new Point(r.X + 30, r.Y + 7), Theme.TextPrimary, TextFormatFlags.NoPadding);
            }
            using (var p = new Pen(Color.FromArgb(13, Theme.Border)))
                g.DrawLine(p, 0, 55, Width, 55);

            // rows
            _rowRects.Clear();
            float y = ListTop() + 8 - _scroll;
            if (_skills.Count == 0)
                TextRenderer.DrawText(g, "还没有技能。点右上\"新建技能\"创建，或把 SKILL.md 放进技能文件夹。",
                    Theme.BaseFont, new Point(24, (int)y + 8), Theme.TextSubtlest, TextFormatFlags.NoPadding);
            foreach (var sk in _skills)
            {
                _rowRects.Add(new Rectangle(12, (int)y, Width - 24, 52));
                if (y + 52 > ListTop() && y < Height)
                {
                    var rect = new Rectangle(12, (int)y, Width - 24, 52);
                    if (_hoverRow == _rowRects.Count - 1)
                        using (var path = Theme.Rounded(rect, 10))
                        using (var b = new SolidBrush(Theme.Hover))
                            g.FillPath(b, path);
                    Icons.Draw(g, "Sparkles", new Rectangle(rect.X + 14, (int)y + 10, 16, 16), Theme.TextSubtle);
                    TextRenderer.DrawText(g, "/" + sk.Name, new Font(Theme.BaseFont, FontStyle.Bold),
                        new Point(rect.X + 42, (int)y + 8), Theme.TextPrimary,
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, sk.Description, Theme.SmallFont,
                        new Point(rect.X + 42, (int)y + 28), Theme.TextSubtle,
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, "双击调用", Theme.XsFont,
                        new Point(rect.Right - 60, (int)y + 18), Theme.TextSubtlest, TextFormatFlags.NoPadding);
                }
                y += 52 + 6;
            }
            _contentH = Math.Max(1, (float)y + 8);

            if (_contentH > Height - ListTop())
            {
                float viewH = Height - ListTop();
                float thumbH = Math.Max(28, viewH * viewH / _contentH);
                float maxS = _contentH - viewH;
                float ty = ListTop() + (viewH - thumbH) * (_scroll / Math.Max(1, maxS));
                using (var path = Theme.Rounded(new Rectangle(Width - 5, (int)ty, 3, (int)thumbH), 2))
                using (var b = new SolidBrush(Color.FromArgb(40, 13, 13, 13)))
                    g.FillPath(b, path);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float viewH = Height - ListTop();
            float max = Math.Max(0, _contentH - viewH);
            _scroll = Math.Max(0, Math.Min(max, _scroll - e.Delta * 0.6f));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag)
            {
                float viewH = Height - ListTop();
                float max = Math.Max(0, _contentH - viewH);
                _scroll = Math.Max(0, Math.Min(max, _dragStart - (e.Y - _dragY) * (_contentH / Math.Max(1, viewH))));
                Invalidate();
                return;
            }
            int nb = -1;
            for (int i = 0; i < 4; i++) if (_btnRects[i].Contains(e.Location)) { nb = i; break; }
            int nr = _btnRects[0].Contains(e.Location) ? -1 : HitRow(e.Location);
            if (nb != _hoverBtn || nr != _hoverRow)
            {
                _hoverBtn = nb; _hoverRow = nr;
                Cursor = nb >= 0 || nr >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private int HitRow(Point p)
        {
            for (int i = 0; i < _rowRects.Count; i++)
                if (_rowRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_contentH > Height - ListTop() && e.X > Width - 10)
            {
                _drag = true; _dragY = e.Y; _dragStart = _scroll;
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                if (_btnRects[i].Contains(e.Location))
                {
                    if (i == 0) NewSkillDialog();
                    else if (i == 1) { try { System.Diagnostics.Process.Start("explorer.exe", Settings.SkillsDir); } catch { } }
                    else if (i == 2) Reload();
                    else if (i == 3 && BackRequested != null) BackRequested();
                    return;
                }
            }
            int r = HitRow(e.Location);
            if (r >= 0)
            {
                // double-click invokes
                if (_lastIndex == r && (DateTime.Now - _lastClick).TotalMilliseconds < 600)
                {
                    if (r < _skills.Count && InvokeSkill != null) InvokeSkill(_skills[r].Name);
                    _lastIndex = -1;
                    return;
                }
                _lastIndex = r;
                _lastClick = DateTime.Now;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false;
        }

        private void NewSkillDialog()
        {
            using (var f = new Form())
            {
                f.Text = "新建技能";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(420, 220);
                f.BackColor = Theme.Bg;
                f.Font = Theme.BaseFont;
                f.MaximizeBox = false; f.MinimizeBox = false;

                var l1 = new Label { Text = "技能名（英文，用作 /name 调用）：", Location = new Point(14, 14), AutoSize = true, ForeColor = Theme.TextSubtle };
                var name = new TextBox { Location = new Point(14, 36), Width = 370, BackColor = Theme.Input, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                var l2 = new Label { Text = "描述（一行，会注入系统提示词）：", Location = new Point(14, 70), AutoSize = true, ForeColor = Theme.TextSubtle };
                var desc = new TextBox { Location = new Point(14, 92), Width = 370, BackColor = Theme.Input, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "创建", Location = new Point(216, 136), Size = new Size(80, 30), BackColor = Theme.Brand, ForeColor = Theme.TextInverse, FlatStyle = FlatStyle.Flat };
                var cancel = new Button { Text = "取消", Location = new Point(304, 136), Size = new Size(80, 30), BackColor = Theme.Panel, ForeColor = Theme.TextPrimary, FlatStyle = FlatStyle.Flat };
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; };
                cancel.Click += (s, e) => f.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l1); f.Controls.Add(name); f.Controls.Add(l2); f.Controls.Add(desc); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok;

                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    string n = name.Text.Trim();
                    if (n.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-zA-Z0-9_-]+$"))
                    {
                        MessageBox.Show("技能名只能用英文字母/数字/下划线/连字符。");
                        return;
                    }
                    try
                    {
                        var dir = Path.Combine(Settings.SkillsDir, n);
                        Directory.CreateDirectory(dir);
                        var sb = new System.Text.StringBuilder();
                        sb.Append("---\n");
                        sb.Append("name: ").Append(n).Append('\n');
                        sb.Append("description: ").Append(desc.Text.Trim().Replace("\n", " ")).Append('\n');
                        sb.Append("---\n\n");
                        sb.Append("# ").Append(n).Append("\n\n在这里写技能的具体指导内容（步骤、约定、示例）。\n");
                        File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString(), System.Text.Encoding.UTF8);
                        Reload();
                        MessageBox.Show("技能已创建：" + n);
                    }
                    catch (Exception ex) { MessageBox.Show("创建失败: " + ex.Message); }
                }
            }
        }
    }
}
