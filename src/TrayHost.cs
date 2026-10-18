using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 托盘图标 + 右键菜单。
    ///
    /// 这个类只负责「显示与收集意图」，不直接操作引擎和窗口：
    /// 所有动作都以事件形式抛给 MainForm，由它决定怎么做。原因很简单——
    /// 托盘菜单里点「开始」和窗口里点「开始」必须走**同一条**代码路径，
    /// 否则两处的状态机（按钮可用性、设备行开关、状态文案）迟早会不一致。
    ///
    /// 图标：托盘只能用 16px 图标，直接复用 AppIcon 里已编译进 exe 的那份
    /// （见 AppIcon.cs）。这里 Clone 一份给 NotifyIcon：NotifyIcon.Dispose 会
    /// 连带 Dispose 掉传给它的 Icon，而 AppIcon.Load 的返回值是**静态缓存**，
    /// 被 Dispose 之后同一个进程里再取就会拿到已释放的句柄。
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _showItem;
        readonly ToolStripMenuItem _startItem;
        readonly ToolStripMenuItem _stopItem;
        readonly ToolStripMenuItem _exitItem;
        readonly ToolStripMenuItem _hintItem;
        readonly Icon _trayIcon;
        bool _balloonShown;
        string _lastBalloon;
        int _lastBalloonTick;
        bool _disposed;

        public event EventHandler ShowRequested;
        public event EventHandler StartRequested;
        public event EventHandler StopRequested;
        public event EventHandler ExitRequested;

        public TrayHost()
        {
            _menu = new ContextMenuStrip
            {
                // 默认的 ContextMenuStrip 是 Windows 老式灰底描边，和整套 iOS 风格不搭。
                // 换成自绘配色后视觉上是白底、圆角、无边框阴影的菜单。
                Renderer = new TrayMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                BackColor = Theme.Surface,
                ForeColor = Theme.Ink,
                Font = Theme.FSmall,
                // 菜单不应该在托盘图标上按 WinForms 默认的「贴着屏幕边缘」定位，
                // 交给系统按图标位置弹出即可（ShowBelow)。
                DropShadowEnabled = true,
                Padding = new Padding(0, Theme.S(4), 0, Theme.S(4))
            };
            // 无边框 + 自绘圆角：ContextMenuStrip 是原生窗口，默认有 1px 边框。
            _menu.DropShadowEnabled = true;

            _hintItem = new ToolStripMenuItem("热键 —") { Enabled = false };
            _showItem = new ToolStripMenuItem("打开主界面");
            _startItem = new ToolStripMenuItem("开始输出");
            _stopItem = new ToolStripMenuItem("停止输出") { Enabled = false };
            _exitItem = new ToolStripMenuItem("退出");

            _showItem.Click += (s, e) => Raise(ShowRequested);
            _startItem.Click += (s, e) => Raise(StartRequested);
            _stopItem.Click += (s, e) => Raise(StopRequested);
            _exitItem.Click += (s, e) => Raise(ExitRequested);

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _hintItem,
                new ToolStripSeparator(),
                _showItem,
                _startItem,
                _stopItem,
                new ToolStripSeparator(),
                _exitItem
            });

            Icon src = AppIcon.Load(16);
            _trayIcon = (src != null) ? (Icon)src.Clone() : SystemIcons.Application;

            _icon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "并川听澜",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.DoubleClick += (s, e) => Raise(ShowRequested);
            // 单击左键也打开：托盘图标上「点一下就出来」比「必须双击」更符合直觉，
            // 右键仍然弹菜单（WinForms 会自动区分 MouseClick 的按键）。
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { Raise(ShowRequested); }
            };
        }

        static void Raise(EventHandler h)
        {
            if (h == null) { return; }
            try { h(null, EventArgs.Empty); } catch { }
        }

        /// <summary>
        /// 同步菜单与提示文本。
        ///
        /// NotifyIcon.Text 在 .NET Framework 里**硬上限 63 个字符**，超了直接抛
        /// ArgumentException（不是截断）。中文设备名 + 状态很容易超，所以这里必须
        /// 自己截断，否则某个设备名一长，托盘一刷新就崩。
        /// </summary>
        public void Sync(bool running, bool canStart, string hotKeyText, string status)
        {
            if (_disposed) { return; }
            try
            {
                _startItem.Enabled = !running && canStart;
                _stopItem.Enabled = running;
                _showItem.Text = "打开主界面";
                _hintItem.Text = string.IsNullOrEmpty(hotKeyText) ? "热键未启用" : ("热键 " + hotKeyText + " 开始/停止");

                string tip = "并川听澜";
                if (!string.IsNullOrEmpty(status)) { tip += " · " + status; }
                _icon.Text = Clip(tip, 62);
            }
            catch { }
        }

        /// <summary>
        /// 第一次收进托盘时弹一次气泡，否则用户点了关闭就「程序不见了」，
        /// 会以为被关掉了。只弹一次：之后每次关闭都弹就成了骚扰。
        /// </summary>
        public void ShowMinimizedHint()
        {
            if (_disposed || _balloonShown) { return; }
            _balloonShown = true;
            try
            {
                _icon.BalloonTipTitle = "并川听澜";
                _icon.BalloonTipText = "程序仍在后台运行。双击托盘图标可以重新打开，右键可退出。";
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(3000);
            }
            catch { }
        }

        /// <summary>
        /// 弹一条临时气泡（热键操作结果等）。
        ///
        /// 与 ShowMinimizedHint 的区别：那个有「只弹一次」的门闩，用来解释「程序去哪了」；
        /// 这个是每次动作都要反馈，因此不设门闩，但同样的文案在 5 秒内不重复弹 ——
        /// 用户连按热键时会看到一串气泡堆在通知中心，很烦。
        /// </summary>
        public void ShowBalloon(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text)) { return; }
            try
            {
                if (text == _lastBalloon && Environment.TickCount - _lastBalloonTick < 5000) { return; }
                _lastBalloon = text;
                _lastBalloonTick = Environment.TickCount;
                _icon.BalloonTipTitle = "并川听澜";
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(2000);
            }
            catch { }
        }

        static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) { return string.Empty; }
            if (s.Length <= max) { return s; }
            return s.Substring(0, max - 1) + "…";
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
            try { _menu.Dispose(); } catch { }
            // _trayIcon 是 Clone 出来的独立句柄，NotifyIcon.Dispose 可能已经释放它，
            // 这里再 Dispose 一次是幂等的（Icon.Dispose 内部有 disposed 标志）。
            try { if (_trayIcon != null && !ReferenceEquals(_trayIcon, SystemIcons.Application)) { _trayIcon.Dispose(); } } catch { }
        }

        /// <summary>
        /// 托盘菜单的配色表：把 WinForms 默认的灰蓝渐变换成与主界面一致的白/浅灰。
        /// 只覆写真正会被用到的几项，其余沿用基类默认值。
        /// </summary>
        sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled) { e.TextColor = Theme.DisabledText; }
                else if (e.Item.Selected) { e.TextColor = Theme.Ink; }
                else { e.TextColor = Theme.Ink; }
                base.OnRenderItemText(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // 不画默认的 1px 深色边框：托盘菜单在小尺寸下有一圈深框会显得很脏。
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(Theme.Surface)) { e.Graphics.FillRectangle(b, e.AffectedBounds); }
            }
        }

        sealed class TrayColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.AccentSoft; } }
            public override Color MenuItemBorder { get { return Theme.AccentSoft; } }
            public override Color MenuBorder { get { return Theme.Line; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color SeparatorDark { get { return Theme.LineSoft; } }
            public override Color SeparatorLight { get { return Theme.Surface; } }
            public override Color CheckBackground { get { return Theme.AccentSoft; } }
            public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
            public override Color CheckPressedBackground { get { return Theme.AccentSoft; } }
        }
    }
}
