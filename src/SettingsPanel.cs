using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 设置面板：开机自启 + 全局快捷键。
    ///
    /// 为什么单独做成一个弹出窗，而不是塞进主界面：
    /// 主界面每一寸纵向空间都很紧张（设备多时要靠 EnsureListFits 长高），
    /// 而自启和热键都是「设置一次、几个月不再动」的选项。放进主界面等于
    /// 用常驻空间换一个低频操作，不值。
    ///
    /// 为什么不复用 TrayHost 的右键菜单管这两项（之前就是那样）：
    /// 托盘菜单里改热键只能用「预设几种组合」的笨办法 —— 用户想用
    /// Ctrl+Shift+F9 就没辙。真正的自定义需要一块能接收键盘事件的界面，
    /// 而托盘菜单是系统原生 Popup，拿不到稳定的 KeyDown 序列。
    ///
    /// 视觉上与 ProfileMenu / TextPrompt 完全一致：无边框 + 自绘圆角 + 1px 描边，
    /// 用户看到的是同一套语言，而不是一个系统灰窗口拼接进来。
    /// </summary>
    internal sealed class SettingsPanel : Form
    {
        // ───────────────────────── 回调（由 MainForm 注入）─────────────────────────
        //
        // 这两个都是「应用」而不是「记录」：面板只管收集用户意图，
        // 真正写注册表 / 注销并重新注册热键都由 MainForm 做 —— 因为那里才有
        // 窗口句柄（RegisterHotKey 需要）和 _settings 实例。
        // 返回 null 表示成功，返回字符串表示失败原因（面板负责显示）。

        /// <summary>试着应用一个新的热键。null = 清除（禁用热键）。返回 null 表示成功。</summary>
        public Func<HotKey.Binding, string> ApplyHotKey;
        /// <summary>试着应用开机自启状态。返回 null 表示成功。</summary>
        public Func<bool, string> ApplyAutoStart;

        readonly ToggleSwitch _autoToggle;
        readonly PillButton _closeBtn;

        /// <summary>当前已生效的热键文本；空串表示用户主动关掉了热键。</summary>
        string _hotKeyText;
        /// <summary>正在录制时，实时显示的预览（例如按住 Ctrl 时只显示 "Ctrl+"）。</summary>
        string _recordPreview;
        bool _recording;
        bool _closing;

        string _message;
        Color _messageColor = Theme.InkFaint;

        // 绘制与命中测试共用同一批矩形，避免两处各算一遍导致错位。
        Rectangle _cardAuto;
        Rectangle _cardHotKey;
        Rectangle _fieldRect;

        /// <summary>最近一次应用是否失败（供主机判断是否需要提示）。</summary>
        public bool LastApplyFailed { get; private set; }

        public SettingsPanel(string hotKeyText, bool autoStartEnabled)
        {
            _hotKeyText = hotKeyText ?? string.Empty;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Canvas;
            Font = Theme.FName;
            KeyPreview = true;
            Text = "设置";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            LayoutRects();

            _autoToggle = new ToggleSwitch();
            // 开关坐在**面板自己手绘的白色卡片**上，父链解析不出来（Parent 是 Form，
            // Form.BackColor = Canvas 画布灰），必须显式告诉它背后是白色。
            // 不设这一条，46x28 的开关矩形会被清成 #F6F7F9，圆角四角在白色卡片上
            // 露出一圈灰框 —— 用户报的「按钮底部有灰色的框」就是这个。
            _autoToggle.BackOverride = Theme.Surface;
            _autoToggle.SetCheckedSilent(autoStartEnabled);
            _autoToggle.Location = new Point(
                _cardAuto.Right - Theme.S(16) - _autoToggle.Width,
                _cardAuto.Y + (_cardAuto.Height - _autoToggle.Height) / 2);
            _autoToggle.CheckedChanged += (s, e) => OnAutoStartToggled();
            Controls.Add(_autoToggle);

            _closeBtn = new PillButton
            {
                Text = "完成",
                Variant = PillButton.Kind.Primary,
                Font = Theme.FSmall,
                Size = new Size(Theme.S(88), Theme.S(34))
            };
            _closeBtn.Location = new Point(
                ClientSize.Width - Theme.S(22) - _closeBtn.Width,
                ClientSize.Height - Theme.S(16) - _closeBtn.Height);
            _closeBtn.Click += (s, e) => CloseSelf();
            Controls.Add(_closeBtn);
        }

        // ───────────────────────── 布局 ─────────────────────────

        void LayoutRects()
        {
            float pad = Theme.S(22f);

            // 纵向：标题 → 分隔 → 「启动」小标 → 自启卡片 →「全局快捷键」小标 → 快捷键卡片 → 消息行 → 完成按钮
            int w = Theme.S(424);
            int y = Theme.S(16);

            int titleH = Theme.S(30);
            y += titleH;
            y += Theme.S(12);            // 分隔线下方留白

            int secH = Theme.S(16);
            y += secH;                   // 「启动」小标
            int cardAutoH = Theme.S(74);
            _cardAuto = new Rectangle((int)pad, y, w - (int)(pad * 2), cardAutoH);
            y += cardAutoH + Theme.S(20);

            y += secH;                   // 「全局快捷键」小标
            int cardHotH = Theme.S(96);
            _cardHotKey = new Rectangle((int)pad, y, w - (int)(pad * 2), cardHotH);
            _fieldRect = new Rectangle(
                _cardHotKey.X + Theme.S(14),
                _cardHotKey.Y + Theme.S(34),
                _cardHotKey.Width - Theme.S(28),
                Theme.S(36));
            y += cardHotH + Theme.S(14);

            int msgH = Theme.S(18);
            y += msgH + Theme.S(10);     // 消息行

            int btnH = Theme.S(34);
            y += btnH + Theme.S(16);     // 完成按钮 + 底部留白

            ClientSize = new Size(w, y);
        }

        /// <summary>标题、小标、卡片底、消息行的绘制。子控件（开关/按钮）自己画。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = Theme.TextHint;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 画布底色。用 Clear 而不是 FillRectangle：Graphics.Clear 一定铺满整块，
            // 不会因为 Width/Height 少 1 像素而留下上一帧的残影。
            g.Clear(Theme.Canvas);

            float pad = Theme.S(22f);

            // ── 标题 ──
            using (var br = new SolidBrush(Theme.Ink))
            {
                g.DrawString("设置", Theme.FTitle, br,
                    new RectangleF(pad, Theme.S(14f), ClientSize.Width - pad * 2, Theme.S(28f)), Theme.Left);
            }

            // 标题下的分隔线
            using (var pen = new Pen(Theme.Line, 1f))
            {
                float ly = Theme.S(50f);
                g.DrawLine(pen, pad, ly, ClientSize.Width - pad, ly);
            }

            // ── 「启动」小标 ──
            using (var br = new SolidBrush(Theme.InkMid))
            {
                g.DrawString("启动", Theme.FSec, br,
                    new RectangleF(pad + Theme.S(2f), _cardAuto.Y - Theme.S(20f),
                        ClientSize.Width - pad * 2, Theme.S(16f)), Theme.Left);
            }

            // ── 自启卡片 ──
            DrawCard(g, _cardAuto);
            using (var br = new SolidBrush(Theme.Ink))
            {
                g.DrawString("开机自动启动", Theme.FName, br,
                    new RectangleF(_cardAuto.X + Theme.S(16f), _cardAuto.Y + Theme.S(14f),
                        _cardAuto.Width - Theme.S(80f), Theme.S(22f)), Theme.Left);
            }
            using (var br = new SolidBrush(Theme.InkFaint))
            {
                g.DrawString("登录 Windows 后自动在后台运行", Theme.FSmall, br,
                    new RectangleF(_cardAuto.X + Theme.S(16f), _cardAuto.Y + Theme.S(40f),
                        _cardAuto.Width - Theme.S(80f), Theme.S(18f)), Theme.Left);
            }

            // ── 「全局快捷键」小标 ──
            using (var br = new SolidBrush(Theme.InkMid))
            {
                g.DrawString("全局快捷键", Theme.FSec, br,
                    new RectangleF(pad + Theme.S(2f), _cardHotKey.Y - Theme.S(20f),
                        ClientSize.Width - pad * 2, Theme.S(16f)), Theme.Left);
            }

            // ── 快捷键卡片 ──
            DrawCard(g, _cardHotKey);
            using (var br = new SolidBrush(Theme.Ink))
            {
                g.DrawString("切换所有输出开关", Theme.FName, br,
                    new RectangleF(_cardHotKey.X + Theme.S(16f), _cardHotKey.Y + Theme.S(10f),
                        _cardHotKey.Width - Theme.S(32f), Theme.S(20f)), Theme.Left);
            }

            DrawHotKeyField(g);

            // 操作提示（录制中换成另一句）
            string hint = _recording
                ? "按下组合键即可绑定 · Esc 取消"
                : "点击上方区域可重新录制 · Delete 清除";
            using (var br = new SolidBrush(_recording ? Theme.AccentDark : Theme.InkFaint))
            {
                g.DrawString(hint, Theme.FSmall, br,
                    new RectangleF(_cardHotKey.X + Theme.S(16f), _cardHotKey.Y + Theme.S(74f),
                        _cardHotKey.Width - Theme.S(32f), Theme.S(16f)), Theme.Left);
            }

            // ── 消息行（错误 / 成功提示）──
            if (!string.IsNullOrEmpty(_message))
            {
                using (var br = new SolidBrush(_messageColor))
                {
                    g.DrawString(_message, Theme.FSmall, br,
                        new RectangleF(pad, _cardHotKey.Bottom + Theme.S(10f),
                            ClientSize.Width - pad * 2, Theme.S(18f)), Theme.Left);
                }
            }
        }

        void DrawCard(Graphics g, Rectangle r)
        {
            using (GraphicsPath p = Squircle.Create(r.X, r.Y, r.Width, r.Height, Theme.S(14f)))
            using (var br = new SolidBrush(Theme.Surface)) { g.FillPath(br, p); }
            using (GraphicsPath p = Squircle.Create(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f, Theme.S(14f)))
            using (var pen = new Pen(Theme.Line, 1f)) { g.DrawPath(pen, p); }
        }

        /// <summary>
        /// 快捷键显示区。录制中变成强调色边框 + 淡蓝底，并且实时显示按下的修饰键。
        /// 这个「边按边显示」很关键：用户按住 Ctrl 时若界面毫无反应，会以为没录上。
        /// </summary>
        void DrawHotKeyField(Graphics g)
        {
            bool empty = string.IsNullOrEmpty(_hotKeyText);
            Color back = _recording ? Theme.AccentSoft : Theme.SurfaceAlt;
            Color border = _recording ? Theme.Accent : Theme.Line;

            using (GraphicsPath p = Squircle.Create(_fieldRect.X, _fieldRect.Y, _fieldRect.Width, _fieldRect.Height, Theme.S(9f)))
            using (var br = new SolidBrush(back)) { g.FillPath(br, p); }
            using (GraphicsPath p = Squircle.Create(_fieldRect.X + 0.5f, _fieldRect.Y + 0.5f,
                       _fieldRect.Width - 1f, _fieldRect.Height - 1f, Theme.S(9f)))
            using (var pen = new Pen(border, _recording ? Theme.S(1.6f) : 1f)) { g.DrawPath(pen, p); }

            string text;
            Color fg;
            if (_recording)
            {
                // 一个修饰键都没按下时给占位提示，否则显示已按下的部分组合
                text = string.IsNullOrEmpty(_recordPreview) ? "按下组合键…" : _recordPreview;
                fg = string.IsNullOrEmpty(_recordPreview) ? Theme.InkFaint : Theme.AccentDark;
            }
            else if (empty)
            {
                text = "未启用";
                fg = Theme.InkFaint;
            }
            else
            {
                text = _hotKeyText;
                fg = Theme.Ink;
            }

            using (var br = new SolidBrush(fg))
            {
                g.DrawString(text, Theme.FBtn, br,
                    new RectangleF(_fieldRect.X + Theme.S(12f), _fieldRect.Y,
                        _fieldRect.Width - Theme.S(24f), _fieldRect.Height), Theme.Left);
            }
        }

        // ───────────────────────── 交互 ─────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 整张快捷键卡片都可点：比只点输入框命中率高得多，
                // 也和 iOS 里「点一整行进入设置项」的手感一致。
                if (_cardHotKey.Contains(e.Location) && !_recording)
                {
                    StartRecording();
                    return;
                }
                if (_recording && _cardHotKey.Contains(e.Location))
                {
                    return;   // 录制中再点不做事，避免误关
                }
            }
            base.OnMouseDown(e);
        }

        void StartRecording()
        {
            _recording = true;
            _recordPreview = null;
            SetMessage(null, Theme.InkFaint);
            Invalidate();
        }

        void StopRecording()
        {
            _recording = false;
            _recordPreview = null;
            Invalidate();
        }

        /// <summary>
        /// 录制统一走 ProcessCmdKey，而不是 KeyDown。
        ///
        /// 原因：Alt+字母 在 WinForms 里会被当成菜单助记符，可能根本不投递到 KeyDown；
        /// 而 ProcessCmdKey 在 PreProcessMessage 阶段就拿到所有 WM_KEYDOWN / WM_SYSKEYDOWN，
        /// 是唯一能稳定捕获「任意修饰键组合」的地方。录制期间返回 true 拦下所有键，
        /// 防止空格/回车之类触发按钮的默认行为。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_recording) { return base.ProcessCmdKey(ref msg, keyData); }

            Keys code = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0;
            bool alt = (keyData & Keys.Alt) != 0;
            bool shift = (keyData & Keys.Shift) != 0;
            bool win = IsWinKeyDown();

            // Esc 取消录制（回到原来的快捷键，不改动任何设置）
            if (code == Keys.Escape) { StopRecording(); return true; }

            // Delete / Backspace 清除：明确禁用热键，而不是保留旧值
            if (code == Keys.Delete || code == Keys.Back)
            {
                ApplyBinding(null);
                return true;
            }

            // 只按了修饰键：更新预览，继续等主键
            if (IsModifierKey(code))
            {
                _recordPreview = BuildPreview(ctrl, alt, shift, win);
                Invalidate();
                return true;
            }

            // msg.WParam 才是真实虚拟键码：KeyCode 对数字键盘/ OEM 键会给出
            // Keys.NumPad5 / Keys.OemMinus 这类枚举，虚拟键码没有这种歧义。
            int vk = msg.WParam.ToInt32();

            HotKey.Binding b;
            string err;
            if (!HotKey.TryFromKeyEvent(ctrl, alt, shift, win, vk, out b, out err))
            {
                SetMessage(err, Theme.Warn);
                // 不退出录制：用户多半是想按 Ctrl+Shift+9 却先按了 Shift，
                // 直接踢出录制会让他重来一遍。
                _recordPreview = null;
                Invalidate();
                return true;
            }

            ApplyBinding(b);
            return true;
        }

        static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey
                || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu
                || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey
                || k == Keys.LWin || k == Keys.RWin;
        }

        static string BuildPreview(bool ctrl, bool alt, bool shift, bool win)
        {
            var sb = new System.Text.StringBuilder();
            if (ctrl) { sb.Append("Ctrl+"); }
            if (alt) { sb.Append("Alt+"); }
            if (shift) { sb.Append("Shift+"); }
            if (win) { sb.Append("Win+"); }
            return sb.ToString();
        }

        /// <summary>
        /// Windows 键不像其它修饰键那样能可靠地出现在 keyData 里（很多 Win+X 被系统
        /// 优先吃掉），所以直接用 GetAsyncKeyState 轮询物理状态。
        /// 代价是：系统级保留的 Win 组合可能录不到 —— 那种情况下让用户换一个组合。
        /// </summary>
        static bool IsWinKeyDown()
        {
            try
            {
                return (GetAsyncKeyState(VkLWin) & 0x8000) != 0
                    || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;
            }
            catch { return false; }
        }

        const int VkLWin = 0x5B;
        const int VkRWin = 0x5C;

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        /// <summary>把新选择应用到主程序；失败则保留原值并显示原因。</summary>
        void ApplyBinding(HotKey.Binding b)
        {
            string newText = (b == null) ? string.Empty : b.Text;
            string err = null;
            if (ApplyHotKey != null)
            {
                try { err = ApplyHotKey(b); }
                catch (Exception ex) { err = "应用失败：" + ex.Message; }
            }

            _recording = false;
            _recordPreview = null;
            LastApplyFailed = !string.IsNullOrEmpty(err);

            if (LastApplyFailed)
            {
                // 关键：注册失败（比如被别的程序占用）时绝不能让界面显示新组合，
                // 否则用户以为改成功了，实际热键根本不会触发。
                SetMessage(err, Theme.Danger);
            }
            else
            {
                _hotKeyText = newText;
                SetMessage(string.IsNullOrEmpty(newText) ? "已关闭全局快捷键" : ("已设为 " + newText),
                    Theme.InkMid);
            }
            Invalidate();
        }

        void OnAutoStartToggled()
        {
            bool want = _autoToggle.Checked;
            string err = null;
            if (ApplyAutoStart != null)
            {
                try { err = ApplyAutoStart(want); }
                catch (Exception ex) { err = "应用失败：" + ex.Message; }
            }

            if (!string.IsNullOrEmpty(err))
            {
                // 写注册表可能被组策略拦下。失败必须把开关拨回去，
                // 否则界面显示「已开启」而注册表里什么都没有。
                _autoToggle.SetCheckedSilent(!want);
                SetMessage(err, Theme.Danger);
                Invalidate();
            }
            else
            {
                SetMessage(want ? "已开启开机自动启动" : "已关闭开机自动启动", Theme.InkMid);
                Invalidate();
            }
        }

        void SetMessage(string text, Color color)
        {
            _message = text;
            _messageColor = color;
        }

        // ───────────────────────── 生命周期 ─────────────────────────

        public void ShowAt(Form owner)
        {
            if (owner != null && !owner.IsDisposed)
            {
                // 居中于主窗口（而不是屏幕居中）：设置面板属于主窗口，
                // 跟着它走才符合「模态子面板」的直觉。
                Rectangle ob = owner.Bounds;
                Location = new Point(
                    ob.X + (ob.Width - Width) / 2,
                    ob.Y + (ob.Height - Height) / 2);
            }
            Show();
            Activate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                WndProcHook.EnableShadow(this);
                if (!WndProcHook.TryNativeRounded(this, 0)) { WndProcHook.ApplyRegion(this, (int)Theme.RadiusWindow); }
            }
            catch { }
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
            // 点面板外面就收起 —— 与 ProfileMenu / PopupList 行为一致。
            // 但录制中不收：用户可能要点别的窗口去确认某个组合键是否被占用，
            // 一失焦就关掉会让录制永远做不完。
            if (!_recording) { CloseSelf(); }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;

                // 这里刻意**不加** CS_DROPSHADOW(0x00020000)。
                //
                // 它是 Windows 给「有边框窗口」准备的系统投影：由窗口类在窗口矩形之外
                // 直接贴一圈**位图**阴影，因此有两个硬伤：
                //   ① 它不认圆角，永远是矩形 → 圆角窗口的四角外面会露出一块直角暗区，
                //      正好把右下角的倒角在视觉上吃掉（用户报的「右下角没有倒角」）；
                //   ② 系统只在右/下两侧贴（上/左是光照方向），实测左右压暗深度
                //      14 vs 65、上下 8 vs 73 → 用户报的「右侧框的阴影比左边重」。
                // tests\shadowab 四组对照（同尺寸无边框窗口 + 灰底抓屏）定量结论：
                //   A 只有 CS_DROPSHADOW   → 阴影 左0 右57 上0 下57，相邻跳变 20（硬边）
                //   B 现行（CS + DWM）     → 阴影 左14 右65 上8 下73，跳变 19
                //   C 只留 DWM             → 阴影 左14 右14 上8 下28，跳变 2（柔和）
                //   D 只留 GDI Region 圆角 → 阴影全 0（Region 会把 DWM 阴影一起裁掉）
                // 所以正确组合是「DWM 阴影 + DWM 原生圆角」，两者都不要 CS_DROPSHADOW。
                cp.ExStyle |= 0x00000080;     // WS_EX_TOOLWINDOW（不占任务栏/Alt+Tab）
                return cp;
            }
        }
    }
}
