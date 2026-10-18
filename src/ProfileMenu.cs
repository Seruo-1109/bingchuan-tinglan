using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 「方案」弹出菜单：列出已保存的设备组合，点击即切换；右侧 × 删除；
    /// 末行是「保存当前组合为方案…」。
    ///
    /// 为什么不复用 UiKit.PopupList：那个类只接受字符串列表、只回报索引，
    /// 没有「删除」这种次级动作，也不支持「末行是另一种颜色/带分隔线的操作项」。
    /// 强行塞进去会把一个 200 行的通用下拉框改成专用控件，得不偿失。
    /// 这里刻意与 PopupList 保持同样的视觉语言（白底圆角 + 1px 描边 + 悬停淡蓝），
    /// 用户看到的是同一套风格，而不是两个拼在一起的控件。
    /// </summary>
    internal sealed class ProfileMenu : Form
    {
        /// <summary>菜单项类型。保存项与方案项的点选语义不同，必须能区分。</summary>
        internal enum ItemKind { Profile, SaveNew, Empty }

        internal sealed class Item
        {
            public ItemKind Kind;
            public int ProfileIndex = -1;
            public string Text;
        }

        readonly List<Item> _items = new List<Item>();
        int _hover = -1;
        int _hoverDelete = -1;
        bool _closing;

        /// <summary>选中某个方案（参数为方案在 Settings.Profiles 里的索引）。</summary>
        public event Action<int> ProfilePicked;
        /// <summary>请求「保存当前组合」。</summary>
        public event Action SaveRequested;
        /// <summary>请求删除某个方案。</summary>
        public event Action<int> DeleteRequested;

        static int RowH { get { return Theme.S(34); } }
        static int Pad { get { return Theme.S(6); } }
        static int DelW { get { return Theme.S(30); } }

        public ProfileMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Font = Theme.FName;
            KeyPreview = true;
            TopMost = true;
        }

        /// <summary>装载内容并按条目数定高。</summary>
        public void SetItems(List<Item> items, int width)
        {
            _items.Clear();
            if (items != null) { _items.AddRange(items); }
            int h = _items.Count * RowH + Pad * 2;
            ClientSize = new Size(Math.Max(width, Theme.S(200)), Math.Max(h, RowH + Pad * 2));
        }

        public void ShowAt(Point screen)
        {
            Screen sc = Screen.FromPoint(screen);
            int x = screen.X, y = screen.Y;
            if (x + Width > sc.WorkingArea.Right) { x = Math.Max(sc.WorkingArea.Left, sc.WorkingArea.Right - Width - Theme.S(4)); }
            if (y + Height > sc.WorkingArea.Bottom) { y = Math.Max(sc.WorkingArea.Top, sc.WorkingArea.Bottom - Height - Theme.S(4)); }
            Location = new Point(x, y);
            Show();
            Activate();
            WndProcHook.MakeShadow(this);
        }

        public void CloseSelf()
        {
            if (_closing) { return; }
            _closing = true;
            Close();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CloseSelf();
        }

        int RowAt(int y)
        {
            int idx = (y - Pad) / RowH;
            if (y < Pad || idx < 0 || idx >= _items.Count) { return -1; }
            return idx;
        }

        Rectangle DeleteRect(int i)
        {
            return new Rectangle(Width - Pad - DelW, Pad + i * RowH + (RowH - DelW) / 2, DelW, DelW);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = RowAt(e.Y);
            int del = -1;
            if (idx >= 0 && _items[idx].Kind == ItemKind.Profile && DeleteRect(idx).Contains(e.Location)) { del = idx; }
            if (idx != _hover || del != _hoverDelete)
            {
                _hover = idx;
                _hoverDelete = del;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            _hoverDelete = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = RowAt(e.Y);
            if (idx >= 0)
            {
                Item it = _items[idx];
                if (it.Kind == ItemKind.Profile)
                {
                    if (DeleteRect(idx).Contains(e.Location))
                    {
                        var h = DeleteRequested;
                        if (h != null) { h(it.ProfileIndex); }
                        CloseSelf();
                        return;
                    }
                    var p = ProfilePicked;
                    if (p != null) { p(it.ProfileIndex); }
                    CloseSelf();
                    return;
                }
                if (it.Kind == ItemKind.SaveNew)
                {
                    var s = SaveRequested;
                    if (s != null) { s(); }
                    CloseSelf();
                    return;
                }
            }
            CloseSelf();
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { CloseSelf(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = Theme.TextHint;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var path = Squircle.Create(0.5f, 0.5f, Width - 1f, Height - 1f, Theme.S(10f)))
            {
                using (var br = new SolidBrush(Theme.Surface)) { g.FillPath(br, path); }
                using (var pen = new Pen(Theme.Line, 1f)) { g.DrawPath(pen, path); }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                Item it = _items[i];
                var row = new Rectangle(Pad, Pad + i * RowH, Width - Pad * 2, RowH);
                bool hot = i == _hover && it.Kind != ItemKind.Empty;

                if (hot)
                {
                    using (var p = Squircle.Create(row.X, row.Y, row.Width, row.Height, Theme.S(7f)))
                    using (var br = new SolidBrush(Theme.AccentSoft)) { g.FillPath(br, p); }
                }

                // 「保存新方案」上方画一条分隔线，和已有方案区分开
                if (it.Kind == ItemKind.SaveNew && i > 0)
                {
                    using (var pen = new Pen(Theme.LineSoft, 1f))
                    {
                        g.DrawLine(pen, row.X + Theme.S(8), row.Y, row.Right - Theme.S(8), row.Y);
                    }
                }

                Color fg;
                if (it.Kind == ItemKind.Empty) { fg = Theme.InkFaint; }
                else if (it.Kind == ItemKind.SaveNew) { fg = Theme.AccentDark; }
                else { fg = Theme.Ink; }

                int textRight = (it.Kind == ItemKind.Profile) ? DelW : 0;
                var tr = new RectangleF(row.X + Theme.S(10), row.Y, row.Width - Theme.S(16) - textRight, row.Height);
                using (var br = new SolidBrush(fg)) { g.DrawString(it.Text ?? string.Empty, Font, br, tr, Theme.Left); }

                if (it.Kind == ItemKind.Profile)
                {
                    // × 删除：悬停时变红，平时是浅灰，避免满屏红叉
                    Rectangle dr = DeleteRect(i);
                    Color xc = (_hoverDelete == i) ? Theme.Danger : Theme.Disabled;
                    using (var pen = new Pen(xc, Theme.S(1.6f)))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        float cx = dr.X + dr.Width / 2f, cy = dr.Y + dr.Height / 2f, d = Theme.S(4f);
                        g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                        g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
                    }
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;

                // 不要 CS_DROPSHADOW：它是矩形的硬边位图阴影，不认圆角，还会被系统
                // 只贴在右/下两侧，导致「右下角没倒角 + 右侧阴影偏重」。
                // 这里改用 DWM 阴影（ShowAt 里的 WndProcHook.MakeShadow）+ GDI 圆角，
                // 定量依据见 tests\shadowab 四组对照，详见 SettingsPanel.CreateParams 的注释。
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    /// <summary>
    /// 单行文本输入弹窗（用于「给方案起个名字」）。
    ///
    /// 不用 Microsoft.VisualBasic.Interaction.InputBox：那会拖进 Microsoft.VisualBasic
    /// 程序集，而本项目是 csc 直编、引用表极简，为一个输入框加一个程序集不值得；
    /// 而且 InputBox 是系统老式灰窗口，和整套 iOS 风格割裂。
    /// </summary>
    internal sealed class TextPrompt : Form
    {
        readonly TextBox _box;
        readonly PillButton _ok;
        readonly PillButton _cancel;
        public string Value { get { return _box.Text.Trim(); } }

        public TextPrompt(string title, string initial, string placeholder)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Surface;
            ClientSize = new Size(Theme.S(320), Theme.S(150));
            KeyPreview = true;
            Font = Theme.FName;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            var lbl = new SkinLabel
            {
                Text = title,
                Font = Theme.FSec,
                ForeColor = Theme.InkMid,
                AutoSize = true,
                BackColor = Color.Transparent,
                BackOverride = Theme.Surface,
                Location = new Point(Theme.S(20), Theme.S(18))
            };
            Controls.Add(lbl);

            _box = new TextBox
            {
                Text = initial ?? string.Empty,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FName,
                Location = new Point(Theme.S(20), Theme.S(44)),
                Width = ClientSize.Width - Theme.S(40)
            };
            // 占位提示：TextBox 没有原生 placeholder，用 Cue Banner 消息。
            try
            {
                if (!string.IsNullOrEmpty(placeholder)) { SendMessageCue(_box, placeholder); }
            }
            catch { }
            Controls.Add(_box);

            _cancel = new PillButton { Text = "取消", Variant = PillButton.Kind.Ghost, Font = Theme.FSmall };
            _ok = new PillButton { Text = "保存", Variant = PillButton.Kind.Primary, Font = Theme.FSmall };
            int bw = Theme.S(84), bh = Theme.S(32), gap = Theme.S(10);
            _ok.Size = new Size(bw, bh);
            _cancel.Size = new Size(bw, bh);
            _ok.Location = new Point(ClientSize.Width - Theme.S(20) - bw, ClientSize.Height - Theme.S(20) - bh);
            _cancel.Location = new Point(_ok.Left - gap - bw, _ok.Top);
            _ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_ok);
            Controls.Add(_cancel);
            AcceptButton = null;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                WndProcHook.EnableShadow(this);
                if (!WndProcHook.TryNativeRounded(this, 0)) { WndProcHook.ApplyRegion(this, (int)Theme.RadiusWindow); }
                _box.Focus();
                _box.SelectAll();
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Line, 1f))
            {
                // 无边框窗体自己补一圈描边，否则纯白面板贴在白背景上分不出边界
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SendMessageCue(TextBox box, string cue)
        {
            const int EM_SETCUEBANNER = 0x1501;
            SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, cue);
        }
    }
}
