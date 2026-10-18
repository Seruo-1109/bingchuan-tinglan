using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace MultiAudioOutput
{
    /// <summary>
    /// 主窗口：iOS 风格无边框界面。
    ///
    /// 布局策略：所有位置由 Layout() 按当前客户区尺寸计算，不使用绝对坐标常量，
    /// 因此窗口任意缩放、任意 DPI 下都不会错位或被裁切。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ───────── 常量（96 DPI 基准，运行时乘 Theme.Scale）─────────
        //
        // 这些原本是 const int，改成静态只读属性是为了支持 DPI 缩放：app.manifest
        // 声明 permonitorv2 之后，Windows 不再替我们做位图拉伸，所有盒子尺寸都必须
        // 自己按缩放系数放大，否则文字（Point 单位，GDI+ 自动放大）会溢出盒子。
        //
        // 保留 46/56/96/420/520 这些原始数值作为 96 DPI 基准，改它们等于改设计。
        static int TitleH { get { return Theme.S(46); } }
        static int RowH { get { return Theme.S(56); } }
        const int MaxVisibleRows = 5;
        /// <summary>设备列表下方为状态行与按钮预留的高度。EnsureListFits() 也用这个值反推窗口高度。</summary>
        static int BottomBlock { get { return Theme.S(96); } }
        static int MinW { get { return Theme.S(420); } }
        static int MinH { get { return Theme.S(520); } }

        // ───────── 数据 ─────────
        readonly AudioEngine _engine = new AudioEngine();
        DeviceWatcher _watcher;
        Settings _settings;
        readonly Timer _levelTimer;
        bool _running;
        bool _closing;
        Size _clientBeforeMin = Size.Empty;
        readonly List<string> _startupWarnings = new List<string>();

        // ───────── 托盘 / 热键 ─────────
        TrayHost _tray;
        HotKey.Binding _hotKey;
        /// <summary>热键注册失败的原因（成功时为 null）。启动时作为提示显示一次。</summary>
        string _hotKeyError;
        /// <summary>
        /// 真正退出（而不是收进托盘）。
        ///
        /// 为什么必须显式区分：OnFormClosing 的现有实现会先 e.Cancel 再异步清理，
        /// 清理完才 Close()。如果「收进托盘」也走同一条路，托盘菜单的「退出」会被
        /// 当成一次普通的关窗，于是用户点退出、窗口消失、程序还在后台跑 —— 只能
        /// 去任务管理器杀进程。这个标志是唯一的区别点。
        /// </summary>
        bool _reallyExit;
        /// <summary>启动时是否直接隐藏到托盘（--minimized / --autostart）。</summary>
        readonly bool _startHidden;

        // ───────── 控件 ─────────
        Panel _content;
        Card _deviceCard;
        DeviceListView _deviceList;
        Dot _statusDot;
        SkinLabel _statusText;
        SlimCombo _captureCombo;
        SlimSlider _latencySlider;
        SkinLabel _latencyValue;
        PillButton _startBtn, _stopBtn, _selectAllBtn, _invertBtn;
        /// <summary>「方案」下拉：保存/切换设备组合预设。</summary>
        PillButton _profileBtn;
        LevelMeter _captureMeter;
        SkinLabel _latHint;
        Panel _titleBar;
        /// <summary>标题栏主名与副标题（头像徽章由 PaintTitleBar 直接画，不是控件）。</summary>
        SkinLabel _brandTitle, _brandSub;
        // ───────── 标题栏头像的「状态表情」 ─────────
        //
        // 三个表情就是三个运行状态，这是本次二次元改造的核心信息设计：
        //   就绪（未输出）  = Smile，不带环
        //   输出中          = Focus，不带环
        //   出错（引擎故障）= Calm，降饱和 + 红色圆环
        //
        // 为什么用表情而不是加一个「状态」标签：窗口只有 480x632，状态行已经
        // 用文字说清楚了（「正在输出到 2 个设备」）。头像换表情是**冗余编码**，
        // 不占版面，扫一眼就知道现在是什么状态；配文字仍然可读，不依赖颜色。
        int _avatarMood = AvatarArt.Smile;
        Color? _avatarRing;
        // 本次运行是否出过故障。Fault 事件会把头像切成「平静 + 红环」，
        // 但紧接着引擎还会广播 Stopped，而 Stopped → StopEngine() 会把一切
        // 复位成「就绪」。没有这个标志的话，红环只存在一个消息循环的瞬间，
        // 用户永远看不到 —— 实测截图里 ui_error 与 ui_idle 完全一致。
        // 语义：故障态一直保留到下一次成功启动为止（见 StartEngine）。
        bool _engineFaulted;
        PillButton _minBtn, _closeBtn;
        // 设置入口放在标题栏，和最小化/关闭并排。齿轮而不是「设置」两个字：
        // 标题栏右侧空间按 DPI 缩放后只够三个 30px 按钮，文字按钮会把标题挤掉。
        PillButton _gearBtn;
        // 设置面板同一时刻只允许一个。它自己会在失焦时关闭并置 null（见 OnFormClosed）。
        SettingsPanel _settingsPanel;
        SkinLabel _emptyHint;
        // 三个分组标题必须是字段：LayoutContent() 要给它们定位。
        // 若留成局部变量，它们会永远停在 (0,0)，被后面的下拉框盖住。
        SkinLabel _capLabel, _latLabel, _devLabel;

        // ───────── 防呆：系统默认播放设备 ─────────
        //
        // 为什么要单独记这两个值：本程序用 WASAPI loopback 采集「系统默认播放设备」
        // 的声音，再分别输出到用户勾选的设备。如果允许把同一台设备既当采集源又当
        // 输出目标，声音就会「采集→输出→再被采集」，一按开始就是刺耳的自激啸叫。
        // 所以默认播放设备必须在输出列表里被剔除，并且启动时再做一次兜底检查。
        /// <summary>系统默认播放设备（Multimedia 角色）的 ID，空串表示取不到。</summary>
        string _defaultRenderId = string.Empty;
        /// <summary>同上设备的显示名，用于界面提示。</summary>
        string _defaultRenderName = string.Empty;
        /// <summary>卡片下方那行「已隐藏默认播放设备」的说明。</summary>
        SkinLabel _devHint;
        /// <summary>
        /// 捕获源下拉框的数据源：_captureIds[i] 是下拉框第 i+1 项对应的设备 ID
        /// （第 0 项固定是「系统默认播放设备」）。
        ///
        /// 必须缓存，不能再用「重新枚举 + 按序号取」的方式反查：输出列表现在会剔掉
        /// 默认设备，序号与枚举结果不再一一对应，按序号取会静默换到另一台设备。
        /// </summary>
        readonly List<string> _captureIds = new List<string>();

        public MainForm() : this(false) { }

        /// <param name="startHidden">
        /// true 时启动后不显示主窗口，只在托盘里待着（开机自启、--minimized）。
        /// 注意不能只写 WindowState = Minimized：那样任务栏上会留一个最小化的窗口，
        /// 用户点它还得等一次「从最小化恢复」；直接 Hide 才是真的没有窗口。
        /// </param>
        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            _settings = Settings.Load();
            // 必须在 BuildUi 之前把 DPI 告诉 Theme：BuildUi 里的每一个尺寸常量
            // 都要经过 Theme.S()，此时若还是默认的 96，界面会按 100% 排一遍再重排。
            //
            // 注意这里用的是 WndProcHook.SystemDpi() 而不是 this.DeviceDpi：
            // DeviceDpi 是 Control 属性，在窗口句柄创建之前恒为 96 —— 若用户在
            // 125%/150% 缩放下运行，会先按 100% 建一遍全部控件，等句柄建好才发现
            // 排错了，那时控件尺寸已经定死。实测 tests\dpiprobe：未感知进程里
            // 这个值永远是 96，声明 permonitorv2 之后它也要等句柄才准。
            Theme.SetDpi(WndProcHook.SystemDpi());
            // 必须在 BuildUi 之前设好：Form.Icon 只在窗口句柄创建时读取一次，
            // 拖到句柄建好之后再设，标题栏和任务栏不会更新。
            // 另外 /win32icon 只写 exe 资源节，不设 Form.Icon 的话窗口用的是
            // .NET 内置默认图标（实测与 v1 的像素差 119.29，与默认图标差 0.00）。
            AppIcon.Apply(this);
            BuildUi();
            ApplySettings();
            ReloadDevices(true);
            HookWatcher();
            HookEngine();
            InitTrayAndHotKey();

            _levelTimer = new Timer { Interval = 33 }; // ≈30fps，电平刷新足够
            _levelTimer.Tick += (_, __) =>
            {
                // 热插拔：只在未运行时消费（运行中不允许重建列表，停止后会自动补一次刷新）
                if (!_running && _watcher != null) { _watcher.ConsumeIfDue(); }
                TickLevels();
            };
            _levelTimer.Start();
        }

        // ═════════════════════════ 构建界面 ═════════════════════════

        void BuildUi()
        {
            // 显示名（Alt+Tab / 任务栏 / 任务管理器里看到的名字）。
            // 注意：**不要**顺手把 exe 文件名也改掉 —— %APPDATA%\MultiAudioOutput 配置目录、
            // HKCU Run 的自启值名、单实例互斥体名都还叫 MultiAudioOutput，改文件名会
            // 让老用户的配置和自启项失配。显示名与内部标识是两回事。
            Text = "并川听澜";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
            MinimizeBox = true;
            MaximizeBox = false;
            KeyPreview = true;
            // 启动即隐藏时先设成完全透明：OnShown 里再 Hide。
            // 详见 OnShown 的说明 —— 不能直接拒绝显示，否则 OnLoad 的整套
            // 初始化（阴影/圆角/布局/预热）会被跳过。
            //
            // 不要在这里动 ShowInTaskbar：改这个属性会**重建窗口句柄**，
            // 而全局热键是绑在句柄上的，句柄一换热键就失效（且不会报错）。
            // 隐藏的窗体本来就不在任务栏上，没必要动它。
            if (_startHidden) { Opacity = 0d; }
            // 缩放权必须完全收到 Theme.S() 手里。
            //
            // WinForms 默认 AutoScaleMode = Font：manifest 声明 DPI 感知之后，如果
            // 还让 WinForms 自己按字体/DPI 再缩一遍，就会和 Theme.S() 双重叠加
            // （150% 缩放下界面会变成 225%）。设成 None 明确表示"布局由我算"。
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(480), Theme.S(632));
            MinimumSize = new Size(MinW, MinH);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.FName;

            // ── 标题栏 ──
            _titleBar = new Panel { Dock = DockStyle.Top, Height = TitleH, BackColor = Theme.Canvas };
            _titleBar.Paint += PaintTitleBar;
            _titleBar.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { WndProcHook.DragWindow(Handle); } };
            _titleBar.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; } };

            // 标题左边留出 32px 的品牌徽章（角色头像，见 PaintTitleBar），文字从 x=60 起。
            // 标题与副标题分成两行：单个 46px 高的标题栏里，12.5pt 的字居中会显得空，
            // 两行（主名 + 功能说明）把高度用满，也更像 iOS 的大标题栏。
            _brandTitle = new SkinLabel
            {
                Text = "并川听澜",
                Font = Theme.FTitle,
                ForeColor = Theme.Ink,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Theme.S(60), Theme.S(4))
            };
            _brandTitle.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { WndProcHook.DragWindow(Handle); } };
            _titleBar.Controls.Add(_brandTitle);

            _brandSub = new SkinLabel
            {
                Text = "多路音频输出",
                Font = Theme.FSmall,
                ForeColor = Theme.InkFaint,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(Theme.S(60), Theme.S(25))
            };
            _brandSub.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { WndProcHook.DragWindow(Handle); } };
            _titleBar.Controls.Add(_brandSub);

            _minBtn = new PillButton { Text = "—", Variant = PillButton.Kind.Ghost, Size = new Size(Theme.S(30), Theme.S(26)), Font = Theme.FSmall };
            // 最小化 = 真正的任务栏最小化，与关闭（收进托盘）语义分开。
            //
            // 这里曾经写成 HideToTray()，理由是「这个程序的价值在于后台持续分路输出，
            // 最小化到任务栏会留一个占位窗口」。但那是替用户做决定：任务栏上的「—」
            // 在任何一个 Windows 程序里都表示「缩到任务栏」，用户点了发现窗口整个
            // 消失、任务栏也没有图标，就会认为程序被关掉了（实际是躲进托盘）。
            // 想要「界面消失、声音继续」有专门的入口：关闭按钮 + 托盘菜单。
            // 音频链路在最小化时照常运行，所以这个改动不影响后台输出。
            _minBtn.Click += (s, e) => MinimizeToTaskbar();
            _closeBtn = new PillButton { Text = "✕", Variant = PillButton.Kind.Ghost, Size = new Size(Theme.S(30), Theme.S(26)), Font = Theme.FSmall };
            // 关闭的语义由 Settings.CloseToTray 决定（默认 true → 收进托盘）。
            // 真正的退出走托盘菜单的「退出」，那里会把 _reallyExit 置 true。
            _closeBtn.Click += (s, e) =>
            {
                if (_settings.CloseToTray && _tray != null) { HideToTray(); }
                else { Close(); }
            };
            // 齿轮：开机自启 + 全局快捷键都收进这里（原来散在托盘右键菜单里）。
            // 用 "⚙" 而不是画一个矢量齿轮：这个字符在雅黑里有，且随字号自动缩放，
            // 比自己 DrawPath 一组齿形省事且在小尺寸下更清晰。
            _gearBtn = new PillButton { Text = "⚙", Variant = PillButton.Kind.Ghost, Size = new Size(Theme.S(30), Theme.S(26)), Font = Theme.FSmall };
            _gearBtn.Click += (s, e) => OpenSettingsPanel();
            _titleBar.Controls.Add(_minBtn);
            _titleBar.Controls.Add(_closeBtn);
            _titleBar.Controls.Add(_gearBtn);

            // ── 内容区（自适应）──
            _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
            _content.Resize += (s, e) => LayoutContent();

            // 采集源
            _capLabel = SectionLabel("音频来源");
            _captureCombo = new SlimCombo();
            _captureCombo.SelectedIndexChanged += (s, e) => SaveSettings();

            // 采集电平
            _captureMeter = new LevelMeter { Height = 6 };

            // 延迟
            _latLabel = SectionLabel("输出延迟");
            _latencySlider = new SlimSlider { Minimum = Settings.MinLatency, Maximum = Settings.MaxLatency, Step = 5 };
            _latencyValue = new SkinLabel
            {
                Font = Theme.FVal,
                ForeColor = Theme.Ink,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent
            };
            _latencySlider.ValueChanged += (s, e) =>
            {
                _latencyValue.Text = _latencySlider.Value + " ms";
                _latencyValue.ForeColor = _latencySlider.Value <= 15 ? Theme.Warn : Theme.Ink;
                SaveSettings();
            };

            _latHint = new SkinLabel
            {
                Text = "数值越小延迟越低；低于 15ms 时部分声卡可能不稳定",
                Font = Theme.FSmall,
                ForeColor = Theme.InkFaint,
                BackColor = Color.Transparent,
                AutoSize = true
            };

            // 设备区
            _devLabel = SectionLabel("输出设备");
            _selectAllBtn = new PillButton { Text = "全选", Variant = PillButton.Kind.Tinted, Compact = true, Size = new Size(Theme.S(58), Theme.S(26)), Font = Theme.FSmall };
            _invertBtn = new PillButton { Text = "反选", Variant = PillButton.Kind.Ghost, Compact = true, Size = new Size(Theme.S(58), Theme.S(26)), Font = Theme.FSmall };
            _selectAllBtn.Click += (s, e) => SetAllToggles(true);
            _invertBtn.Click += (s, e) => InvertToggles();

            // 「方案」：设备组合预设。放在「全选/反选」左侧 —— 那两颗是高频的
            // 临时操作，方案是低频的整套切换，主次关系靠位置就能读出来。
            _profileBtn = new PillButton { Text = "方案", Variant = PillButton.Kind.Ghost, Compact = true, Size = new Size(Theme.S(62), Theme.S(26)), Font = Theme.FSmall };
            _profileBtn.Click += (s, e) => ShowProfileMenu();

            _deviceCard = new Card();
            // 卡片外观（白色圆角 + 1px 描边）改由铺满它的 _deviceList 自己画。
            // 原因见 DeviceListView.OnPaint：列表铺满卡片，若仍由 Card 画，
            // 列表的整块填充会把四角圆角盖成直角。
            _deviceCard.DrawSelf = false;
            // 整张设备列表只有这一个控件（内部全部自绘）。
            // 旧实现是「每行一个 Panel + ToggleSwitch + LevelMeter」，5 台设备就有
            // 15 个子 HWND，窗口合计 36 个子窗口；每个子窗口都要各自处理一次
            // WM_PAINT 才有内容，于是窗口可见后有一段时间是一片没画过的空窗口
            // （启动黑块 / 从任务栏恢复的骨架帧，见 DeviceListView 类注释）。
            _deviceList = new DeviceListView();
            _deviceList.CheckedChanged += (s, e) => { UpdateStartButtonState(); SaveSettings(); };
            _deviceList.VolumeChanged += (s, e) =>
            {
                // 拖动时连续触发：立刻应用到正在输出的那一路，让用户听到变化。
                // 引擎按设备 ID 定位，热插拔后序号变了也不会调错设备。
                if (_running)
                {
                    try { _engine.SetVolume(_deviceList.VolumeRowId, _deviceList.GetVolume(_deviceList.VolumeRowIndex)); }
                    catch { }
                }
                // 这里**不落盘**：拖动一次会触发几十上百次（每个像素一次），
                // 每次都 SaveSettings 就是几十次序列化 + 临时文件 + 原子改名。
                // 落盘挂在 VolumeChangeCommitted 上，一次拖动只写一次。
            };
            _deviceList.VolumeChangeCommitted += (s, e) => { SaveSettings(); };
            _deviceCard.Controls.Add(_deviceList);

            _emptyHint = new SkinLabel
            {
                Text = "未检测到可用的输出设备",
                Font = Theme.FName,
                ForeColor = Theme.InkFaint,
                BackColor = Color.Transparent,
                // 这个标签压在白色卡片上，但它是 _content 的子控件（不是卡片的子控件），
                // 父链解析会一路走到窗体拿到画布灰 —— 必须显式给卡片白，否则会露出灰底。
                BackOverride = Theme.Surface,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                Visible = false
            };

            // 卡片下方的一行说明：系统默认播放设备已被剔除，以及原因。
            // 不能只悄悄删掉设备 —— 用户会以为程序少认了一台设备，反复折腾驱动。
            _devHint = new SkinLabel
            {
                Font = Theme.FSmall,
                ForeColor = Theme.InkFaint,
                BackColor = Color.Transparent,
                AutoSize = false,
                Visible = false
            };

            // 底部
            _statusDot = new Dot { DotColor = Theme.Success, Size = new Size(Theme.S(8), Theme.S(8)) };
            _statusText = new SkinLabel
            {
                Text = "就绪",
                Font = Theme.FStatus,
                ForeColor = Theme.InkMid,
                AutoSize = true,
                BackColor = Color.Transparent
            };

            _startBtn = new PillButton { Text = "开始", Variant = PillButton.Kind.Primary };
            _stopBtn = new PillButton { Text = "停止", Variant = PillButton.Kind.Danger, Enabled = false };
            _startBtn.Click += (s, e) => StartEngine();
            _stopBtn.Click += (s, e) => StopEngine("已停止");

            _content.Controls.AddRange(new Control[]
            {
                _capLabel, _captureCombo, _captureMeter,
                _latLabel, _latencySlider, _latencyValue, _latHint,
                _devLabel, _profileBtn, _selectAllBtn, _invertBtn, _deviceCard, _emptyHint,
                _devHint,
                _statusDot, _statusText, _startBtn, _stopBtn
            });

            Controls.Add(_content);
            Controls.Add(_titleBar);

            // 事件
            FormClosing += OnFormClosing;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { Close(); }
                else if (e.KeyCode == Keys.Space && !_running) { StartEngine(); e.Handled = true; }
            };
        }

        /// <summary>
        /// 窗口被拖到另一块「不同缩放」的显示器时，重算所有盒子尺寸。
        ///
        /// 声明 permonitorv2 之后，Windows 不再替我们拉伸位图，会在跨屏时发
        /// WM_DPICHANGED。WinForms 更新 DeviceDpi 并触发本方法，但因为我们把
        /// AutoScaleMode 设成了 None（避免和 Theme.S() 双重缩放），它不会自动
        /// 重排任何控件 —— 必须自己把 Theme 的系数换掉再重设一遍尺寸。
        ///
        /// 字体不用管：Theme 的字体全部是 GraphicsUnit.Point，GDI+ 按当前
        /// 绘制表面的 DPI 自动放大。只重设「盒子」即可。
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            try
            {
                if (e.DeviceDpiNew == Theme.CurrentDpi) { return; }
                Theme.SetDpi(e.DeviceDpiNew);
                ApplyDpiMetrics();
                // 跨屏时窗口尺寸要按 DPI 比例一起换，否则 480x632 在 150% 屏上
                // 只剩 320x421 逻辑像素，内容全挤在一起。
                RescaleForDpi(e.DeviceDpiOld, e.DeviceDpiNew, e.SuggestedRectangle);
                LayoutContent();
            }
            catch { }
        }

        /// <summary>
        /// 跨屏 DPI 变化时把窗口本身换成合适的物理尺寸。
        ///
        /// e.SuggestedRectangle 是 Windows 按新 DPI 给的建议外框（保持逻辑尺寸不变），
        /// 直接用它是官方推荐做法。但它没有考虑我们的 MinimumSize（最小尺寸是按
        /// 96 基准乘出来的），也没有检查是否跑出屏幕，所以补两道夹逼。
        /// </summary>
        void RescaleForDpi(int oldDpi, int newDpi, Rectangle suggested)
        {
            try
            {
                int w = suggested.Width;
                int h = suggested.Height;
                if (w < MinimumSize.Width) { w = MinimumSize.Width; }
                if (h < MinimumSize.Height) { h = MinimumSize.Height; }

                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (h > wa.Height) { h = wa.Height; }
                if (w > wa.Width) { w = wa.Width; }

                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(suggested.X, suggested.Y, w, h);
            }
            catch { }
        }

        /// <summary>
        /// 把「构造时算死」的固定尺寸按当前 DPI 重设一遍。
        /// LayoutContent 只负责位置与拉伸型尺寸，这些不会跟着变，必须单独处理。
        /// </summary>
        void ApplyDpiMetrics()
        {
            try
            {
                if (_titleBar != null) { _titleBar.Height = TitleH; }
                if (_brandTitle != null) { _brandTitle.Location = new Point(Theme.S(60), Theme.S(4)); }
                if (_brandSub != null) { _brandSub.Location = new Point(Theme.S(60), Theme.S(25)); }
                if (_minBtn != null) { _minBtn.Size = new Size(Theme.S(30), Theme.S(26)); }
                if (_closeBtn != null) { _closeBtn.Size = new Size(Theme.S(30), Theme.S(26)); }
                if (_gearBtn != null) { _gearBtn.Size = new Size(Theme.S(30), Theme.S(26)); }
                if (_selectAllBtn != null) { _selectAllBtn.Size = new Size(Theme.S(58), Theme.S(26)); }
                if (_invertBtn != null) { _invertBtn.Size = new Size(Theme.S(58), Theme.S(26)); }
                if (_profileBtn != null) { _profileBtn.Size = new Size(Theme.S(62), Theme.S(26)); }
                if (_statusDot != null) { _statusDot.Size = new Size(Theme.S(8), Theme.S(8)); }
                if (_captureCombo != null) { _captureCombo.Height = Theme.S(36); }
                if (_latencySlider != null) { _latencySlider.Height = Theme.S(26); }
                if (_captureMeter != null) { _captureMeter.Height = Theme.S(6); }
                _deviceList.Invalidate();

                MinimumSize = new Size(MinW, MinH);
            }
            catch { }
        }

        static SkinLabel SectionLabel(string text)
        {
            return new SkinLabel
            {
                Text = text,
                Font = Theme.FSec,
                ForeColor = Theme.InkMid,
                AutoSize = true,
                BackColor = Color.Transparent
            };
        }

        void PaintTitleBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Theme.Canvas);

            // 品牌徽章：角色头像裁成圆形，直径 32（96 DPI），垂直居中于标题栏。
            // 头像同时承担**运行状态指示**：就绪=微笑、输出中=专注、出错=平静+红环。
            // 状态在 _avatarMood / _avatarRing 里，由 SetAvatarMood() 维护。
            //
            // 为什么画在这里而不是放一个 PictureBox：头像是静态的，一次绘制就够；
            // 放控件还要处理透明背景、DPI 缩放、和拖窗事件的冒泡，纯绘制更省事。
            // 头像下面加一层极淡的同心投影，让它在近白底上「浮」起来 —— 直接用
            // 白色画底色时头像边缘会和背景糊在一起（头像本身带白色耳机与白外套）。
            float d = Theme.S(32f);
            float bx = Theme.S(20f);
            float by = (_titleBar.Height - d) * 0.5f;
            using (var br = new SolidBrush(Color.FromArgb(22, 12, 32, 62)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(br, bx - Theme.S(1.5f), by + Theme.S(1f), d + Theme.S(3f), d + Theme.S(3f));
                g.SmoothingMode = SmoothingMode.None;
            }
            AvatarArt.Draw(g, new RectangleF(bx, by, d, d), _avatarMood, _avatarRing,
                           _avatarRing.HasValue ? Theme.S(2f) : 0f,
                           _avatarRing.HasValue ? 0.72f : 0f);

            // 1px 发丝线用「填充整数像素行」而不是 DrawLine：
            // DrawLine 画在 y = Height-1 时会跨在像素边界上，抗锯齿把 1px 摊成两行
            // 各 50% 透明（实测 DrawLine 的 1px Theme.LineSoft 会被渲染成 #F7F8FA，
            // 比设计值还浅，且上下各多一行过渡灰）。FillRectangle 落在整数行上是精确
            // 1px 纯色，见 tests/sepvariants 的 A/B/C 三方案像素对照。
            using (var br = new SolidBrush(Theme.Line))
            {
                g.FillRectangle(br, Theme.S(20), _titleBar.Height - 1, Math.Max(0, _titleBar.Width - Theme.S(40)), 1);
            }
        }

        /// <summary>
        /// 切换标题栏头像的表情 = 切换运行状态指示。
        ///
        /// 三种状态：
        ///   就绪  → Smile，无环（默认）
        ///   输出中 → Focus，无环
        ///   出错  → Calm，降饱和 0.72 + 2px 红环
        ///
        /// 只在 UI 线程调用；故障消息来自音频线程时走的是 SetStatus 的编组路径。
        /// </summary>
        void SetAvatarMood(int mood, Color? ring)
        {
            if (_avatarMood == mood && Nullable.Equals(_avatarRing, ring)) { return; }
            _avatarMood = mood;
            _avatarRing = ring;
            if (_titleBar != null) { _titleBar.Invalidate(); }
        }

        // ═════════════════════════ 布局（全自适应）═════════════════════════

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 只要窗口处于「正常」状态就记下客户区尺寸。最小化那一刻 WindowState 已经
            // 变了、ClientSize 也可能已被改写，所以不能等到恢复时再读，必须提前记。
            if (WindowState == FormWindowState.Normal) { _clientBeforeMin = ClientSize; }
            LayoutContent();
        }

        // ═════════════════════ 从最小化恢复（消除黑帧）═════════════════════
        //
        // 症状：最小化后点任务栏图标恢复，画面会先黑一下（全部文字与控件的包围盒
        // 变成纯黑实心块），约 200ms 后才恢复正常。
        //
        // 根因（tests\restoreprobe 逐帧实测）：恢复时 Windows 先把窗口显示出来，
        // 而 MainForm 在 WindowState 从 Minimized 变 Normal 的过程中有 200~260ms
        // 的同步重排（无边框窗口的 DWM 圆角/阴影重算 + 布局），这段时间 UI 线程
        // 不泵消息，38 个子控件（各自独立 HWND）连第一个 WM_PAINT 都还没处理，
        // 未绘制的子窗口按默认深色呈现 —— 与「启动瞬间黑块」（Bug 12）同一机制。
        // 实测消息线：恢复请求 1995ms，窗口 2009ms 已可见且是黑的，首个 WM_PAINT
        // 直到 2261ms 才到，中间 266ms 一条消息都没派发。
        //
        // 修法（restore-hide，tests\restoreprobe 对照实验：3/3 轮坏帧 0，其余
        // 8 种干预全部无效或只减轻）：在窗口可见之前先把它撤下屏幕，把那 200ms
        // 的重排放在看不见的时间段里做完并强制重画一次，最后再显示。用户看到的
        // 就只有重排完成后的完整界面。

        const int WM_SYSCOMMAND = 0x0112;
        const int SC_RESTORE = 0xF120;
        bool _restoring;

        protected override void WndProc(ref Message m)
        {
            // 全局热键：RegisterHotKey 把 WM_HOTKEY 投递到注册时指定的窗口，
            // 只有主窗体这一个句柄，所以不需要再校验 wParam 里的 id 之外的来源。
            // 处理完直接 return，不能让消息继续往下走（否则 Form 的默认处理
            // 会把它当未知消息忽略，虽然无害，但会多绕一圈）。
            if (m.Msg == HotKey.WmHotKey)
            {
                try
                {
                    if (m.WParam.ToInt32() == HotKey.Id) { ToggleFromHotKey(); }
                }
                catch { }
                return;
            }

            // 第二个实例启动时广播这条消息，让已有的这一份亮出来。
            // 已收进托盘时窗口只是 Hide()，HWND 还在，能正常收到广播。
            if (SingleInstance.ShowMessage != 0 && m.Msg == SingleInstance.ShowMessage)
            {
                try { RestoreFromTray(); } catch { }
                return;
            }

            // 任务栏图标点击恢复 = WM_SYSCOMMAND / SC_RESTORE（tests\restoreprobe
            // 的 systask 模式发同一条消息，能 3/3 复现完全相同的黑帧与阻塞时长）。
            if (m.Msg == WM_SYSCOMMAND
                && (m.WParam.ToInt32() & 0xFFF0) == SC_RESTORE
                && WindowState == FormWindowState.Minimized
                && !_restoring
                // 仅用于自动化测试：MAB_NO_RESTORE_HOOK=1 时完全不做干预，
                // 让 tests\restoreverify 能在同一份二进制上跑「不处理」的对照组，
                // 判断骨架帧是产品代码引入的还是 Windows/WinForms 本身的行为。
                && Environment.GetEnvironmentVariable("MAB_NO_RESTORE_HOOK") != "1")
            {
                RestoreWithoutBlackout();
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// 热键行为：切换输出开关。
        ///
        /// 为什么是「切换」而不是「开始」：热键的使用场景几乎都是「临时静音/恢复」
        /// 或者「听一半要切设备」，按一次开、再按一次关最符合直觉；如果只做「开始」，
        /// 用户在运行中按热键会毫无反应，会以为热键坏了。
        ///
        /// 窗口不可见时也要能工作 —— 这正是热键存在的意义（在托盘里按一下就能开/关）。
        /// 但状态变化要能看到：所以窗口隐藏时用托盘气泡提示一次结果。
        /// </summary>
        void ToggleFromHotKey()
        {
            if (_running)
            {
                StopEngine("已停止");
                if (!Visible) { NotifyTray("已停止输出"); }
                return;
            }

            if (!_deviceList.AnyChecked())
            {
                // 没有勾选设备时不要静默失败：用户按热键一定期待有反应。
                SetStatus("请至少选择一个输出设备", Theme.Danger);
                if (!Visible) { NotifyTray("请先打开主界面并勾选输出设备"); }
                else { Activate(); }
                return;
            }

            StartEngine();
            if (!Visible) { NotifyTray(_running ? ("正在输出到 " + _engine.OutputCount + " 个设备") : "启动失败，请打开主界面查看"); }
        }

        /// <summary>窗口隐藏时用气泡告知一次结果（托盘气泡不占焦点）。</summary>
        void NotifyTray(string text)
        {
            if (_tray == null) { return; }
            try { _tray.ShowBalloon(text); } catch { }
        }

        /// <summary>诊断：只在 MAB_RESTORE_LOG 有值时写一行（记录该步之后窗口表面的状态）。</summary>
        void Probe(string step)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAB_RESTORE_LOG"))) { return; }
            int colors;
            double dark;
            bool ok = WndProcHook.SampleSurface(Handle, out colors, out dark);
            WndProcHook.DiagLog(step + " → 表面 " + (ok ? ("色数=" + colors + " dark=" + dark.ToString("0.00") + "%") : "读失败")
                + " 可见=" + Visible + " 状态=" + WindowState
                + " cloak=" + WndProcHook.GetCloak(Handle));
        }

        /// <summary>
        /// 从最小化恢复窗口，让用户第一眼看到的就是成品界面。
        ///
        /// 用分层窗口 alpha 隐身（不是 DWM cloak、不是 SW_HIDE、不是 WM_SETREDRAW）：
        ///   · DWM cloak 只对其他进程的窗口有效，同进程 DwmGetWindowAttribute 恒返回 -1，
        ///     实测窗口一直可见，完全无效；
        ///   · SW_HIDE 之后窗口收不到 WM_PAINT，任何「隐藏期间重画」都是空转，亮出来是空白；
        ///   · WM_SETREDRAW(FALSE) 会让表面变成未初始化的纯黑（dark=100%），比不处理更糟。
        /// 而 alpha=0 时窗口仍是「可见窗口」，WM_PAINT 照常派发、绘制照常进行，只是
        /// DWM 合成时乘 0，用户什么也看不到 —— 这才是唯一能真正「先画完再亮出来」的路。
        /// </summary>
        void RestoreWithoutBlackout()
        {
            _restoring = true;
            try
            {
                // 最小化之前是不是最大化的？必须还原成原样，否则用户从最大化最小化、
                // 再点任务栏，窗口会莫名变成普通大小。
                bool wasMax = WndProcHook.WasMaximizedBeforeMinimize(this);

                // 把 WindowState 改回 Normal 时，WinForms 会按当前窗口样式
                // （OnLoad 里 KeepResizable 补过 WS_THICKFRAME，带一圈它自己不知道的
                // 调整边框）重新推算尺寸，每恢复一次客户区就少掉那圈边框 —— 实测
                // 466x674 → 452x660 → 438x646，每恢复一次两个方向各缩 14px。
                // 注意不能只写 Bounds：WinForms 会拿它自己以为的边框再把 Bounds 折算
                // 成客户区，结果还是少 14px。必须按「客户区」做差值收敛。
                Size wantClient = _clientBeforeMin;

                // ① 先把窗口设成全透明（alpha=0）。窗口仍然是「可见窗口」，所以
                //    WM_PAINT 照常派发、绘制照常进行，只是 DWM 合成时乘上 alpha=0，
                //    用户屏幕上什么都看不到。这一步必须在任何状态切换之前完成。
                WndProcHook.SetWindowAlpha(this, 0);
                Probe("已设为全透明");

                WindowState = wasMax ? FormWindowState.Maximized : FormWindowState.Normal;
                Probe("WindowState 已切换");
                if (!wasMax && wantClient.Width > 0 && wantClient.Height > 0)
                {
                    // 只动尺寸、不动位置；最多 4 轮，够把那圈不可见调整边框收敛掉。
                    for (int i = 0; i < 4; i++)
                    {
                        Size now = ClientSize;
                        int dw = wantClient.Width - now.Width;
                        int dh = wantClient.Height - now.Height;
                        if (dw == 0 && dh == 0) { break; }
                        Size = new Size(Width + dw, Height + dh);
                    }
                }
                Probe("尺寸已收敛");
                PerformLayout();
                LayoutContent();
                Probe("布局已完成");

                // ② 同步重画：21 个子控件各自的表面在同一次调用里被填成成品。
                //    窗口是全透明的，所以这期间用户什么也看不到。
                WndProcHook.ForceRepaint(this);
                Probe("ForceRepaint 已调");

                // ③ 再补一次：ForceRepaint 只保证「已失效的区域重画」，而恢复不透明
                //    的那一刻 DWM 会重新取一次表面。实测少了这一步会留下单帧的中间态
                //    （412ms 处 dark=53%、色数=2，正好是 alpha 刚回到 255 的瞬间）。
                //    这里对整棵控件树逐个 Update，确保每个子窗口的绘制都已完成。
                UpdateChildrenNow();
                Probe("子控件已逐个 Update");

                // ③b 自验证：表面色数够了才认为是成品。少这一步时实测约 1/6 的
                //     恢复会留下「标题/设备列表/按钮仍是纯黑」的半成品帧。
                WaitUntilPainted();
                Probe("表面已确认为成品");

                // ④ 画完了才恢复不透明：用户第一眼看到的就是成品界面。
                WndProcHook.SetWindowAlpha(this, 255);
                Probe("已恢复不透明");
                Activate();
            }
            catch (Exception ex)
            {
                // 不能只是静默吞掉：中途抛异常时 finally 会立刻把 alpha 设回 255，
                // 窗口亮出来但表面还没画完 —— 表现就和「没做任何处理」一样。
                // 这条日志是排查这类「看起来没生效」的唯一线索。
                WndProcHook.DiagLog("恢复过程抛异常: " + ex.GetType().Name + ": " + ex.Message
                    + " @ " + (ex.StackTrace ?? "").Replace("\r\n", " | "));
            }
            finally
            {
                // 兜底：万一上面中途抛异常，绝不能让窗口停在透明状态
                // （那等于软件消失了，用户只能去任务管理器结束进程）。
                try { WndProcHook.SetWindowAlpha(this, 255); } catch { }
                _restoring = false;
            }
        }

        void LayoutContent()
        {
            if (_content == null) { return; }
            int W = _content.ClientSize.Width;
            int H = _content.ClientSize.Height;
            if (W <= 0 || H <= 0) { return; }

            float pad = Theme.S(Theme.PadX);
            int innerW = W - (int)(pad * 2);
            int y = Theme.S(14);

            // 标题按钮靠右：齿轮在最左（离标题最远、离内容最近），然后是最小化、关闭。
            // 顺序沿用的是 Windows 标题栏习惯：破坏性最强的 ✕ 永远在最右角。
            if (_minBtn != null && _titleBar != null)
            {
                _closeBtn.Location = new Point(_titleBar.Width - Theme.S(22) - _closeBtn.Width, Theme.S(10));
                _minBtn.Location = new Point(_closeBtn.Left - Theme.S(6) - _minBtn.Width, Theme.S(10));
                if (_gearBtn != null)
                {
                    _gearBtn.Location = new Point(_minBtn.Left - Theme.S(6) - _gearBtn.Width, Theme.S(10));
                }
            }

            // ── 分组 1：采集源 ──
            // 分组标题必须显式定位并占用纵向空间。原先这三个 Label 从未被定位，
            // 全部停在 (0,0) 相互重叠，又被 y=14 起的下拉框压住，只剩最后一行可见。
            _capLabel.Location = new Point((int)pad, y);
            y += Theme.S(20);
            SetRow(_captureCombo, pad, ref y, innerW, Theme.S(36));
            y += Theme.S(8);
            SetRow(_captureMeter, pad, ref y, innerW, Theme.S(6));
            y += Theme.S(18);

            // ── 分组 2：输出延迟 ──
            _latLabel.Location = new Point((int)pad, y);
            y += Theme.S(20);
            int valW = Theme.S(74);
            _latencyValue.Width = valW;
            _latencyValue.Location = new Point((int)pad + innerW - valW, y - Theme.S(4));
            _latencySlider.Location = new Point((int)pad - Theme.S(4), y);
            _latencySlider.Width = innerW - Theme.S(78);
            _latencySlider.Height = Theme.S(26);
            y += Theme.S(28);
            _latHint.Location = new Point((int)pad, y);
            y += Theme.S(22);

            y += Theme.S(12);

            // ── 分组 3：输出设备 ──
            // 标题与「全选/反选」按钮同处一行，按钮靠右、标题左对齐。
            _devLabel.Location = new Point((int)pad, y + Theme.S(3));
            _invertBtn.Location = new Point((int)pad + innerW - _invertBtn.Width, y);
            _selectAllBtn.Location = new Point(_invertBtn.Left - Theme.S(8) - _selectAllBtn.Width, y);
            _profileBtn.Location = new Point(_selectAllBtn.Left - Theme.S(8) - _profileBtn.Width, y);
            y += _selectAllBtn.Height + Theme.S(8);

            // 设备列表：iOS 分组列表的卡片应当贴合内容高度。
            // 设备少时卡片收缩（不留一大片空卡），设备多时封顶为可用高度并滚动。
            int listTop = y;
            // 卡片下方可能有一行「已隐藏系统默认播放设备」的说明。
            // 必须从设备区可用高度里先扣掉它，否则设备一多，说明文字会压到状态行上。
            int hintReserve = _devHint != null && _devHint.Visible ? Theme.S(20) : 0;
            int maxListH = H - listTop - BottomBlock - hintReserve;
            if (maxListH < RowH + Theme.S(8)) { maxListH = RowH + Theme.S(8); }

            int wantH = _deviceList.Count * RowH + Theme.S(2);
            int capH = Math.Min(MaxVisibleRows, Math.Max(1, _deviceList.Count)) * RowH + Theme.S(2);
            if (wantH > capH) { wantH = capH; }
            int listH = Math.Min(wantH, maxListH);
            if (listH < RowH + Theme.S(8)) { listH = RowH + Theme.S(8); }

            // 高度必须是整数行，否则最后一行会被切成半截——看起来像渲染坏了，
            // 而不是像「下面还有内容可以滚」。宁可少显示一行，也不要露半行。
            if (listH < wantH)
            {
                int fullRows = Math.Max(1, (listH - Theme.S(2)) / RowH);
                listH = fullRows * RowH + Theme.S(2);
            }

            _deviceCard.Location = new Point((int)pad, listTop);
            _deviceCard.Size = new Size(innerW, listH);
            // 列表铺满整张卡片：卡片外观（白色圆角 + 描边）现在由列表自己画
            // （_deviceCard.DrawSelf = false），所以不能再内缩 1px 留边框，
            // 否则四周会露出一圈 1px 的画布灰缝。
            _deviceList.Location = new Point(0, 0);
            _deviceList.Size = new Size(innerW, listH);

            _emptyHint.Location = new Point((int)pad, listTop + Theme.S(30));
            _emptyHint.Size = new Size(innerW, Theme.S(40));

            // 说明行贴在卡片正下方。hintReserve 已经把它从 maxListH 里扣掉了，
            // 所以这里最坏情况是 H-BottomBlock-20 起，仍高于状态行的 H-74。
            if (_devHint != null)
            {
                _devHint.Location = new Point((int)pad, listTop + listH + Theme.S(4));
                _devHint.Size = new Size(innerW, Theme.S(16));
            }

            int by = H - Theme.S(74);
            _statusDot.Location = new Point((int)pad + Theme.S(2), by + Theme.S(2));
            _statusText.Location = new Point((int)pad + Theme.S(18), by - Theme.S(1));

            int gap = Theme.S(10);
            int btnW = (innerW - gap) / 2;
            // 开始按钮带外发光：控件四周向外扩 GlowPad，胶囊本体仍占原矩形，
            // 多出来的一圈只用来承载光晕。外扩后不会压到「停止」按钮（左侧
            // 间距 10，两侧各扩 5 还剩 0），也不会压到状态行（见下方注释）。
            int glow = Theme.S(5);
            _startBtn.GlowPad = glow;
            _startBtn.Location = new Point((int)pad - glow, H - Theme.S(48) - glow);
            _startBtn.Size = new Size(btnW + glow * 2, Theme.S(40) + glow * 2);
            _stopBtn.Location = new Point((int)pad + btnW + gap, H - Theme.S(48));
            _stopBtn.Size = new Size(innerW - btnW - gap, Theme.S(40));

            _statusText.MaximumSize = new Size(innerW - Theme.S(20), Theme.S(20));

            // 设备行宽由 DeviceListView 自己按客户区算，这里只需让列表跟着卡片重画。
            // 行高两侧都是 Theme.S(56)（MainForm.RowH 与 DeviceListView.RowHeight），
            // 不需要同步，DPI 变化时 Theme 的系数已换过。
            _deviceList.Invalidate();
        }

        /// <summary>把标签放到指定 y 并下移游标。</summary>
        void SetRow(Control c, float pad, ref int y, int w, int h)
        {
            c.Location = new Point((int)pad, y);
            c.Size = new Size(w, h);
            y += h;
        }

        // ═════════════════════════ 设备列表 ═════════════════════════

        void HookWatcher()
        {
            try
            {
                _watcher = new DeviceWatcher(_engine.Enumerator);
                _watcher.Changed += () => { if (!_closing) { ReloadDevices(false); } };
                _watcher.Start();
            }
            catch { }
        }

        /// <summary>
        /// 订阅引擎的故障/断开事件。
        /// 这些回调来自音频线程，因此只做线程安全的状态更新：
        /// SetStatus 自带 InvokeRequired 守卫，StopEngine 也加了同样的守卫。
        /// 没有订阅时，采集中断会导致界面状态卡死（灯常绿、开始按钮永久禁用）。
        /// </summary>
        void HookEngine()
        {
            // Stopped 自带中断原因（引擎在采集线程死亡时把具体原因一并广播），
            // 因此不需要在这里暂存状态，也就没有跨线程共享可变字段的问题。
            _engine.Fault += msg =>
            {
                SetStatus(msg, Theme.Danger);
                // 记下「本次运行出过故障」：紧随其后的 Stopped → StopEngine 会做
                // 整体复位，没有这个标志会把故障头像和红色状态文字一起冲掉。
                _engineFaulted = true;
                // 头像换成「平静 + 红环」：这是真正的故障（采集中断、设备丢失），
                // 与「请先选一个设备」这类用户操作提示区分开，后者不换表情。
                SetAvatarMoodOnUi(AvatarArt.Calm, Theme.Danger);
            };
            _engine.Stopped += reason =>
                StopEngine(string.IsNullOrEmpty(reason) ? "音频采集中断，请检查采集源" : reason);
            _engine.OutputLost += name => SetStatus(name + " 已断开，请检查设备", Theme.Warn);
        }

        /// <summary>
        /// 跨线程安全的 SetAvatarMood：Fault 来自音频线程，控件只能在 UI 线程碰。
        /// </summary>
        void SetAvatarMoodOnUi(int mood, Color? ring)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<int, Color?>(SetAvatarMood), mood, ring); }
                catch (InvalidOperationException) { /* 窗口已销毁 */ }
                return;
            }
            SetAvatarMood(mood, ring);
        }

        // ═════════════════════════ 托盘 / 热键 ═════════════════════════

        /// <summary>
        /// 建立托盘图标与全局热键。
        ///
        /// 放在构造函数里而不是 OnLoad：OnLoad 里已经堆了「从最小化恢复不闪黑」那一整套
        /// 同步重排逻辑（撤下屏幕 → 重排 → 强制绘制 → 等绘制完成 → 恢复 alpha），
        /// 中间夹一个托盘初始化会让那段时序更难推理。托盘本身不依赖窗口可见性，
        /// 构造期建立完全安全 —— NotifyIcon 自己会创建隐藏的消息窗口。
        ///
        /// 热键注册需要窗口句柄（RegisterHotKey 投递 WM_HOTKEY 给指定 hwnd），
        /// 因此这里先强制建句柄。构造期 CreateHandle 是安全的：Form 尚未 Show，
        /// 建句柄不会触发 OnShown / 布局 / 可见性变化。
        /// </summary>
        void InitTrayAndHotKey()
        {
            try
            {
                _tray = new TrayHost();
                _tray.ShowRequested += (s, e) => RestoreFromTray();
                _tray.StartRequested += (s, e) => { if (!_running) { StartEngine(); } };
                _tray.StopRequested += (s, e) => { if (_running) { StopEngine("已停止"); } };
                _tray.ExitRequested += (s, e) => ExitForReal();
                // 自启项已从托盘菜单移除（统一挪到设置面板），这里不再订阅 AutoStartToggled。
            }
            catch (Exception ex)
            {
                _tray = null;
                _startupWarnings.Add("托盘图标初始化失败：" + ex.Message);
            }

            RegisterHotKeyFromSettings();
            SyncTray();
        }

        /// <summary>按当前设置注册（或注销）全局热键，失败时把原因记进 _hotKeyError。</summary>
        void RegisterHotKeyFromSettings()
        {
            _hotKeyError = null;
            _hotKey = null;
            try
            {
                string text = _settings == null ? null : _settings.HotKeyText;
                if (string.IsNullOrEmpty(text)) { return; }   // 用户关掉了热键

                HotKey.Binding b = HotKey.Parse(text);
                if (b == null)
                {
                    _hotKeyError = "热键格式无法识别：" + text;
                    return;
                }

                // RegisterHotKey 要求调用方有消息循环；构造期窗口还没 Show，
                // 但消息循环是 Application.Run 提供的，句柄建好就能收消息。
                if (!IsHandleCreated) { var _ = Handle; }

                string err;
                if (HotKey.Register(Handle, b, out err))
                {
                    _hotKey = b;
                }
                else
                {
                    // 最常见的原因是别的程序已经占用了同一个组合键（错误 1409）。
                    // 这不是致命错误：界面照常可用，只是热键无效，所以只提示不阻断。
                    _hotKeyError = string.IsNullOrEmpty(err) ? ("热键 " + text + " 注册失败") : err;
                }
            }
            catch (Exception ex)
            {
                _hotKeyError = "热键注册异常：" + ex.Message;
            }
        }

        /// <summary>把运行状态推给托盘（菜单可用性、提示文本）。</summary>
        void SyncTray()
        {
            if (_tray == null) { return; }
            try
            {
                string hot = (_hotKey != null) ? _hotKey.Text : string.Empty;
                string status = _running ? ("正在输出到 " + _engine.OutputCount + " 个设备") : null;
                _tray.Sync(_running, _deviceList != null && _deviceList.AnyChecked(), hot, status);
            }
            catch { }
        }

        /// <summary>
        /// 任务栏最小化（标题栏「—」按钮的行为）。
        ///
        /// 为什么单独写一个方法而不是直接 <c>WindowState = Minimized;</c>：
        /// 走 <see cref="RestoreWithoutBlackout"/> 那条恢复路径时，要求
        /// <c>_clientBeforeMin</c> 里存的是「最小化之前的客户区尺寸」——它由
        /// OnResize 在 WindowState == Normal 时记下。直接赋值 WindowState 也会触发
        /// OnResize，但那时 WindowState 已经变成 Minimized，记不进去；所以这里先
        /// 显式存一次，保证最小化后再恢复不会丢尺寸（也不会把最小化后的
        /// 极小客户区当成目标尺寸）。
        ///
        /// 另外要确认 ShowInTaskbar 是 true：它是 Form 的默认值，而 BuildUi 里
        /// 刻意没有动它（改这个属性会重建窗口句柄，全局热键绑在句柄上，一换就失效）。
        /// 所以这里只读不写，仅用一次诊断日志留痕，方便日后排查「最小化后任务栏没图标」。
        /// </summary>
        void MinimizeToTaskbar()
        {
            try
            {
                if (WindowState == FormWindowState.Normal) { _clientBeforeMin = ClientSize; }
                if (!ShowInTaskbar)
                {
                    // 理论上不会走到这里（MainForm 从不改 ShowInTaskbar）。
                    // 真走到了说明有人动过它，那最小化后任务栏不会有图标，
                    // 用户会以为程序退出了 —— 记一条日志，别静默。
                    WndProcHook.DiagLog("最小化时 ShowInTaskbar=false，窗口不会出现在任务栏上");
                }
                WindowState = FormWindowState.Minimized;
            }
            catch (Exception ex)
            {
                WndProcHook.DiagLog("最小化失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>收进托盘：隐藏窗口，音频链路完全不受影响。</summary>
        void HideToTray()
        {
            if (_tray == null) { WindowState = FormWindowState.Minimized; return; }
            try
            {
                // 先记住尺寸：隐藏再显示时 WinForms 会保留 Bounds，但如果是
                // 「最小化 → 隐藏」的顺序，恢复时 WindowState 仍是 Minimized。
                if (WindowState == FormWindowState.Normal) { _clientBeforeMin = ClientSize; }
                Hide();
                _tray.ShowMinimizedHint();
            }
            catch { }
        }

        /// <summary>从托盘恢复窗口。走与「任务栏恢复」相同的防黑块路径。</summary>
        void RestoreFromTray()
        {
            try
            {
                if (!Visible) { Show(); }
                if (WindowState == FormWindowState.Minimized)
                {
                    RestoreWithoutBlackout();
                }
                else
                {
                    Activate();
                    BringToFront();
                }
            }
            catch { }
        }

        /// <summary>托盘菜单的「退出」：唯一真正结束进程的入口。</summary>
        void ExitForReal()
        {
            _reallyExit = true;
            try
            {
                if (!Visible) { Show(); }   // 让关闭动画/清理在一个可见窗口上完成
            }
            catch { }
            Close();
        }

        /// <summary>
        /// 把开机自启设成指定状态（由设置面板调用）。返回 null 表示成功，否则返回失败原因。
        ///
        /// 这里刻意不用「切换」语义：面板里是一个开关，用户明确要求「开」或「关」，
        /// 如果内部再取反一次，一旦注册表里已有脏值（比如用户手工改过 Run 键），
        /// 点一下开关可能反而把它设成用户不想要的状态。
        ///
        /// 写完必须回读 AutoStart.IsEnabled 而不是相信 SetEnabled 的返回值 ——
        /// 组策略可以静默忽略写入而不报错，只有回读才知道是否真的生效。
        /// </summary>
        string ApplyAutoStart(bool want)
        {
            string error = null;
            bool now;
            try
            {
                AutoStart.SetEnabled(want, out error);
                now = AutoStart.IsEnabled;
            }
            catch (Exception ex)
            {
                now = AutoStart.IsEnabled;
                error = ex.Message;
            }

            if (now != want)
            {
                return string.IsNullOrEmpty(error) ? "写注册表失败，设置未生效" : ("设置失败：" + error);
            }

            SetStatus(now ? "已设置为开机自动启动" : "已取消开机自动启动", Theme.InkMid);
            SyncTray();
            return null;
        }

        // ═════════════════════════ 设置面板 ═════════════════════════

        /// <summary>
        /// 打开设置面板（标题栏齿轮）。已打开则只把它拉到前面，不叠第二个。
        ///
        /// 面板显示的是「当前已生效」的状态而不是「配置里写的」：如果上次热键被别的
        /// 程序占用了，注册根本没成功，用户看到的必须是真实的空态 + 原因，
        /// 而不是配置文件里那个看起来正常、实际不工作的字符串。
        /// </summary>
        void OpenSettingsPanel()
        {
            try
            {
                if (_settingsPanel != null && !_settingsPanel.IsDisposed)
                {
                    _settingsPanel.Activate();
                    return;
                }

                bool autoOn = false;
                try { autoOn = AutoStart.IsEnabled; } catch { }

                // 热键显示的是 _hotKey（真实注册成功的那一个），不是 _settings.HotKeyText。
                string hotText = (_hotKey != null) ? _hotKey.Text : string.Empty;

                var panel = new SettingsPanel(hotText, autoOn);
                panel.ApplyHotKey = ApplyHotKey;
                panel.ApplyAutoStart = ApplyAutoStart;
                panel.FormClosed += (s, e) =>
                {
                    if (ReferenceEquals(_settingsPanel, panel)) { _settingsPanel = null; }
                };
                _settingsPanel = panel;

                // 面板自己会 Close()，这里不能用 ShowDialog —— 它需要在失焦时自动收起，
                // 而模态窗口的 Deactivate 事件时序在 WinForms 里不可靠。
                panel.ShowAt(this);
            }
            catch (Exception ex)
            {
                SetStatus("无法打开设置：" + ex.Message, Theme.Danger);
            }
        }

        /// <summary>
        /// 应用新热键（由设置面板调用）。binding 为 null 表示「禁用热键」。
        /// 返回 null 表示成功，否则返回失败原因。
        ///
        /// 顺序要求很硬：**必须先注销旧的，再注册新的**。这里踩过一个只用探针
        /// 才抓得到的坑（tests\hotkeyswap）：
        ///   · RegisterHotKey(hwnd, id) 允许同一 (hwnd, id) 用不同组合重复注册，
        ///     多个注册会**并存**（两个组合都能触发）；
        ///   · UnregisterHotKey(hwnd, id) 注销的是该 (hwnd, id) 下的**全部**注册，
        ///     不是最近一次。
        /// 所以「先注册新的、成功后再注销旧的」看似安全，实际那一次 Unregister
        /// 会把刚注册的新组合一起清掉：界面显示改好了、settings.json 也存了、
        /// 系统里却一个热键都不剩 —— 用户按新组合毫无反应，得重启程序才恢复。
        /// 先注销再注册则天然没有这个问题，且新旧同组合时也不会撞 1409。
        /// 代价是注册失败时旧组合已经没了，因此失败分支必须把它注册回来。
        /// </summary>
        string ApplyHotKey(HotKey.Binding binding)
        {
            // 记住旧状态，失败时用来回滚：用户点了新组合但系统不给，不能让他
            // 既丢掉旧热键、又没拿到新的。
            HotKey.Binding previous = _hotKey;

            try
            {
                if (!IsHandleCreated) { var _ = Handle; }

                // 没变化：保持原注册，什么也不用做。
                if (binding != null && previous != null && previous.Text == binding.Text)
                {
                    _hotKey = binding;
                    _hotKeyError = null;
                    _settings.HotKeyText = binding.Text;
                    SaveSettings();
                    SyncTray();
                    return null;
                }

                // ① 先把旧的（连同该 id 下所有残留注册）清干净。
                HotKey.Unregister(Handle);

                // ② 再注册新的。binding == null 表示用户要禁用，不注册任何东西。
                if (binding != null)
                {
                    string err;
                    if (!HotKey.Register(Handle, binding, out err))
                    {
                        // ③ 失败回滚：把旧组合注册回去，界面/配置一概不动。
                        if (previous != null)
                        {
                            try
                            {
                                string ignore;
                                HotKey.Register(Handle, previous, out ignore);
                            }
                            catch { }
                        }
                        _hotKeyError = string.IsNullOrEmpty(err)
                            ? ("热键 " + binding.Text + " 注册失败")
                            : err;
                        return _hotKeyError;
                    }
                }

                _hotKey = binding;
                _hotKeyError = null;
                _settings.HotKeyText = (binding == null) ? string.Empty : binding.Text;
                SaveSettings();
                SyncTray();
                return null;
            }
            catch (Exception ex)
            {
                // 出异常时尽力恢复到旧状态
                try
                {
                    string ignore;
                    HotKey.Unregister(Handle);
                    if (previous != null) { HotKey.Register(Handle, previous, out ignore); }
                    _hotKey = previous;
                }
                catch { }
                _hotKeyError = "热键应用异常：" + ex.Message;
                return _hotKeyError;
            }
        }

        // ═════════════════════════ 设备方案（预设） ═════════════════════════

        /// <summary>
        /// 弹出「方案」菜单：已有方案 + 「保存当前组合为方案…」。
        /// 没有方案时菜单里只有保存项和一行灰色说明。
        /// </summary>
        void ShowProfileMenu()
        {
            if (_running)
            {
                SetStatus("请先停止输出再切换方案", Theme.Warn);
                return;
            }
            try
            {
                var menu = new ProfileMenu();
                var items = new List<ProfileMenu.Item>();

                List<Settings.Profile> list = _settings.Profiles;
                if (list == null || list.Count == 0)
                {
                    items.Add(new ProfileMenu.Item { Kind = ProfileMenu.ItemKind.Empty, Text = "还没有保存过方案" });
                }
                else
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        items.Add(new ProfileMenu.Item { Kind = ProfileMenu.ItemKind.Profile, ProfileIndex = i, Text = list[i].Name });
                    }
                }
                items.Add(new ProfileMenu.Item { Kind = ProfileMenu.ItemKind.SaveNew, Text = "保存当前组合为方案…" });

                menu.SetItems(items, _profileBtn.Width);
                menu.ProfilePicked += idx => ApplyProfileAt(idx);
                menu.SaveRequested += () => SaveCurrentAsProfile();
                menu.DeleteRequested += idx => DeleteProfileAt(idx);

                Point p = _profileBtn.PointToScreen(new Point(0, _profileBtn.Height + Theme.S(4)));
                menu.ShowAt(p);
            }
            catch (Exception ex)
            {
                SetStatus("打开方案菜单失败：" + ex.Message, Theme.Danger);
            }
        }

        /// <summary>把当前勾选与音量存成一个新方案。</summary>
        void SaveCurrentAsProfile()
        {
            try
            {
                List<string> ids = _deviceList.CheckedIds();
                if (ids.Count == 0)
                {
                    SetStatus("请先勾选至少一个设备，再保存为方案", Theme.Warn);
                    return;
                }

                if (_settings.Profiles == null) { _settings.Profiles = new List<Settings.Profile>(); }
                if (_settings.Profiles.Count >= Settings.MaxProfiles)
                {
                    SetStatus("方案数量已达上限（" + Settings.MaxProfiles + " 个），请先删除一个", Theme.Warn);
                    return;
                }

                string def = "方案 " + (_settings.Profiles.Count + 1);
                using (var dlg = new TextPrompt("方案名称", def, "例如：耳机 + 音箱"))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                    string name = dlg.Value;
                    if (name.Length == 0) { SetStatus("方案名称不能为空", Theme.Warn); return; }
                    if (name.Length > Settings.MaxProfileName) { name = name.Substring(0, Settings.MaxProfileName); }

                    // 同名覆盖而不是新增：用户十有八九是想「更新这个方案」。
                    int exist = -1;
                    for (int i = 0; i < _settings.Profiles.Count; i++)
                    {
                        if (string.Equals(_settings.Profiles[i].Name, name, StringComparison.OrdinalIgnoreCase)) { exist = i; break; }
                    }

                    var prof = new Settings.Profile { Name = name };
                    prof.DeviceIds.AddRange(ids);
                    Dictionary<string, int> vols = _deviceList.EnabledVolumes();
                    foreach (var kv in vols) { prof.Volumes[kv.Key] = kv.Value; }

                    if (exist >= 0)
                    {
                        _settings.Profiles[exist] = prof;
                        SetStatus("已更新方案「" + name + "」", Theme.InkMid);
                    }
                    else
                    {
                        _settings.Profiles.Add(prof);
                        SetStatus("已保存方案「" + name + "」", Theme.InkMid);
                    }
                    _settings.Save();
                }
            }
            catch (Exception ex)
            {
                SetStatus("保存方案失败：" + ex.Message, Theme.Danger);
            }
        }

        /// <summary>应用某个方案：勾选与音量一起还原。</summary>
        void ApplyProfileAt(int index)
        {
            try
            {
                // 运行中禁止切换：引擎的通道与设备是 Start() 时定下的，此时改勾选
                // 只会让界面显示「A、B 已选」而实际还在输出到 A、C —— 这种不一致
                // 用户无法从任何一处发现。
                //
                // 守卫必须放在这里而不是只放在 ShowProfileMenu()：菜单弹出后用户
                // 完全可以用全局热键把输出开起来（菜单不是模态的），再点选方案就
                // 绕过了菜单那次检查。实测 newfeatprobe 就是这样抓到的。
                if (_running)
                {
                    SetStatus("请先停止输出再切换方案", Theme.Warn);
                    return;
                }

                if (_settings.Profiles == null || index < 0 || index >= _settings.Profiles.Count) { return; }
                Settings.Profile p = _settings.Profiles[index];

                _deviceList.ApplyProfile(p.DeviceIds, p.Volumes, false);
                UpdateStartButtonState();
                SaveSettings();

                // 统计有多少台方案里的设备当前没插着 —— 静默少一台比「什么都没发生」好懂。
                int missing = 0;
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _deviceList.Count; i++) { present.Add(_deviceList[i].Id); }
                if (p.DeviceIds != null)
                {
                    foreach (string id in p.DeviceIds)
                    {
                        if (!present.Contains(id)) { missing++; }
                    }
                }

                if (missing > 0)
                {
                    SetStatus("已应用方案「" + p.Name + "」，其中 " + missing + " 台设备当前未连接", Theme.Warn);
                }
                else
                {
                    SetStatus("已应用方案「" + p.Name + "」", Theme.InkMid);
                }
            }
            catch (Exception ex)
            {
                SetStatus("应用方案失败：" + ex.Message, Theme.Danger);
            }
        }

        void DeleteProfileAt(int index)
        {
            try
            {
                if (_settings.Profiles == null || index < 0 || index >= _settings.Profiles.Count) { return; }
                string name = _settings.Profiles[index].Name;
                _settings.Profiles.RemoveAt(index);
                _settings.Save();
                SetStatus("已删除方案「" + name + "」", Theme.InkMid);
            }
            catch (Exception ex)
            {
                SetStatus("删除方案失败：" + ex.Message, Theme.Danger);
            }
        }

        /// <summary>
        /// 重新枚举设备。保留用户已勾选状态（按设备 ID 匹配，而非索引）。
        /// </summary>
        void ReloadDevices(bool firstTime)
        {
            if (_closing) { return; }

            // 记录当前勾选
            var checkedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in _deviceList.CheckedIds()) { checkedIds.Add(id); }
            if (firstTime)
            {
                foreach (string id in _settings.SelectedDeviceIds) { checkedIds.Add(id); }
            }

            // 运行中禁止改动列表（否则正在播放的设备会被清掉）
            if (_running)
            {
                return;
            }

            // all = 系统上全部在用的渲染设备，采集源下拉框用它（默认设备也要能当采集源）。
            List<MMDevice> all = _engine.ListOutputDevices();

            // ───────── 防呆：查系统默认播放设备 ─────────
            //
            // 只有 Multimedia 角色算数：本程序采集的就是 Role.Multimedia 这条流
            // （AudioEngine.Start 里 captureDevice == null 时取的就是它），
            // 按 Communications 角色去查会在「通话设备」和「默认设备」不是同一台时
            // 漏掉真正要防的那台。
            //
            // 查不到不是错误（系统可能一个活动端点都没有），那就只跳过剔除逻辑。
            string defId = string.Empty;
            string defName = string.Empty;
            try
            {
                using (MMDevice def = _engine.Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    defId = SafeId(def);
                    defName = SafeName(def);
                }
            }
            catch { defId = string.Empty; defName = string.Empty; }
            _defaultRenderId = defId ?? string.Empty;
            _defaultRenderName = defId != null && defId.Length > 0 ? defName : string.Empty;

            // 释放旧一批 MMDevice 的 COM 引用，否则每次刷新都会累积句柄。
            // 现在列表是纯数据，没有子控件需要 Dispose —— 只释放设备引用本身。
            ReleaseRowDevices();
            _deviceList.SetDevices(new List<MMDevice>(), checkedIds);   // 先清空，避免列表里留着已释放的设备

            // ───────── 采集源下拉框：用全部设备，不剔除默认设备 ─────────
            //
            // 采集源本来就允许是「系统默认播放设备」（第 0 项），用户也可以显式挑一台
            // 具体的设备来抓。默认设备在这份列表里必须保留，否则想抓它却没有入口。
            //
            // 第 0 项的文案写成「桌面音频（跟随系统默认播放设备）」而不是原来的
            // 「系统默认播放设备」：后者是 Windows 的控制面板术语，普通用户看不出
            // 它抓的是「电脑里正在放的所有声音」。新文案先说结果（桌面音频），
            // 括号里再说它怎么来的（跟随默认设备），与用户的心智模型对齐。
            _captureIds.Clear();
            var capItems = new List<string> { "桌面音频（跟随系统默认播放设备）" };
            foreach (MMDevice d in all)
            {
                _captureIds.Add(SafeId(d));
                capItems.Add(SafeName(d));
            }
            string keepCapture = _settings.CaptureDeviceId;
            int keepIdx = 0;
            for (int i = 0; i < _captureIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(keepCapture) &&
                    string.Equals(_captureIds[i], keepCapture, StringComparison.OrdinalIgnoreCase))
                {
                    keepIdx = i + 1;
                    break;
                }
            }
            _captureCombo.SetItems(capItems, keepIdx);

            // ───────── 输出设备列表：剔除系统默认播放设备 ─────────
            //
            // 为什么要剔：本程序用环回捕获采集默认设备正在播放的声音，再输出到勾选的
            // 设备上。同一台设备既当采集源又当输出目标，声音「采集→输出→再采集」
            // 就形成正反馈，一按开始就是刺耳的啸叫。默认设备恰恰是最容易被误勾的
            // 那台（往往就是用户此刻正在听歌的那台），所以直接从列表里拿掉。
            var devices = new List<MMDevice>(all.Count);
            MMDevice excluded = null;
            foreach (MMDevice d in all)
            {
                if (_defaultRenderId.Length > 0 &&
                    string.Equals(SafeId(d), _defaultRenderId, StringComparison.OrdinalIgnoreCase))
                {
                    excluded = d;   // 不进列表，下面单独释放
                    continue;
                }
                devices.Add(d);
            }
            if (excluded != null) { try { excluded.Dispose(); } catch { } }

            bool any = devices.Count > 0;
            _emptyHint.Visible = !any;
            _deviceCard.Visible = any;
            if (!any)
            {
                // 一台都没有：可能是系统真的没有可用输出，也可能是「只有默认那台，
                // 已被防呆剔除」。两种情况提示不同，否则用户会去查驱动。
                _emptyHint.Text = all.Count > 0
                    ? "除系统默认播放设备外，没有其他可用输出设备"
                    : "未检测到可用的输出设备";
                if (_devHint != null) { _devHint.Visible = false; }
                if (_running) { StopEngine("输出设备已断开"); }
                // devices 为空说明 all 里唯一的那台就是被剔除的默认设备，上面已经
                // Dispose 过了；all.Count == 0 则本来就没有东西要释放。
                // 这里不能再 ReleaseDevices(all)：那会把已释放的引用再释放一次。
                LayoutContent();
                UpdateStartButtonState();
                return;
            }

            // 说明行：让用户知道少了一台设备，以及为什么。
            if (_devHint != null)
            {
                bool hasDefault = _defaultRenderId.Length > 0;
                // 原因写在前面、设备名写在后面：这一行宽度有限，SkinLabel 会用
                // 省略号截尾。设备名可长可短（「耳机 (NICEHCK NK1 MAX)」已经很占
                // 位置），要是放在前面，被截掉的正好是「为什么少了一台」。
                _devHint.Text = hasDefault
                    ? "防止自激啸叫，已隐藏系统默认播放设备「" + _defaultRenderName + "」"
                    : string.Empty;
                _devHint.Visible = hasDefault;
            }

            // 列表接管这批设备的引用；由 ReleaseRowDevices() 负责释放。
            // 音量字典按设备 ID 传入，设备重插后音量不会丢。
            _deviceList.SetDevices(devices, checkedIds, _settings.DeviceVolumes);

            // all 里的设备对象和 devices 是同一批 COM 包装：被列表接管的那些由
            // ReleaseRowDevices() 释放，我们不能在这里再 Dispose 一遍；这里只把
            // all 这个 List 丢掉，不释放其中对象。
            LayoutContent();   // 卡片高度随设备数变化，需要重算
            UpdateStartButtonState();
        }

        /// <summary>释放当前设备列表持有的 MMDevice COM 引用。</summary>
        void ReleaseRowDevices()
        {
            for (int i = 0; i < _deviceList.Count; i++)
            {
                MMDevice d = _deviceList[i].Device;
                if (d == null) { continue; }
                try { d.Dispose(); } catch { }
                _deviceList[i].Device = null;
            }
        }

        static string SafeName(MMDevice d)
        {
            try
            {
                string n = d.FriendlyName;
                if (string.IsNullOrEmpty(n)) { n = d.DeviceFriendlyName; }
                if (string.IsNullOrEmpty(n)) { n = "(未命名设备)"; }
                return n;
            }
            catch { return "(未命名设备)"; }
        }

        static string SafeId(MMDevice d)
        {
            try { return d.ID ?? string.Empty; } catch { return string.Empty; }
        }

        // ═════════════════════════ 交互 ═════════════════════════

        void SetAllToggles(bool on)
        {
            _deviceList.SetAllChecked(on, false);
            UpdateStartButtonState();
            SaveSettings();
        }

        void InvertToggles()
        {
            _deviceList.InvertChecked(false);
            UpdateStartButtonState();
            SaveSettings();
        }

        void UpdateStartButtonState()
        {
            bool any = AnyChecked();
            _startBtn.Enabled = !_running && any;
        }

        bool AnyChecked()
        {
            return _deviceList.AnyChecked();
        }

        // ═════════════════════════ 启停 ═════════════════════════

        void StartEngine()
        {
            if (_running) { return; }
            var targets = new List<MMDevice>();
            for (int i = 0; i < _deviceList.Count; i++)
            {
                DeviceListView.Row it = _deviceList[i];
                if (it.Checked && it.Device != null) { targets.Add(it.Device); }
            }
            if (targets.Count == 0) { SetStatus("请至少选择一个输出设备", Theme.Danger); return; }

            // ───────── 采集源：按缓存的设备 ID 反查，不再依赖索引对齐 ─────────
            //
            // 以前这里重新枚举一遍再用 idx-1 取下标，隐含假设「枚举顺序与列表顺序
            // 完全一致」。现在输出列表会把系统默认播放设备剔掉，而采集源下拉框仍然
            // 列全部设备，两边的下标已经对不上了 —— 必须用 ReloadDevices() 里缓存的
            // _captureIds 按 ID 反查，否则会抓错设备（抓到 A 的声音却输出到 B）。
            MMDevice capture = null;
            int idx = _captureCombo.SelectedIndex;
            string captureId = (idx > 0 && idx - 1 < _captureIds.Count) ? _captureIds[idx - 1] : string.Empty;
            if (captureId.Length > 0)
            {
                List<MMDevice> devs = _engine.ListOutputDevices();
                for (int i = 0; i < devs.Count; i++)
                {
                    if (capture == null &&
                        string.Equals(SafeId(devs[i]), captureId, StringComparison.OrdinalIgnoreCase))
                    {
                        capture = devs[i];
                        continue;   // 这个留给引擎用，下面的循环别释放它
                    }
                    try { devs[i].Dispose(); } catch { }
                }
            }

            // ───────── 防呆兜底：绝不把「采集源」或「系统默认播放设备」当输出 ─────────
            //
            // 列表已经把系统默认播放设备剔掉了，正常情况下 targets 里不可能有它；
            // 但设备热插拔、默认设备切换、或枚举恰好失败时，列表可能残留旧的一行。
            // 更常见的一路是：用户把采集源显式选成某台非默认设备，而同一台设备
            // 又勾在了输出列表里 —— 声音「采集→输出→再采集」就是正反馈啸叫。
            // 所以启动前再挡一道，宁可少一路输出，也不能让用户耳朵受罪。
            string avoidId = captureId.Length > 0 ? captureId : _defaultRenderId;
            var safeTargets = new List<MMDevice>(targets.Count);
            string skippedDefaultName = string.Empty;
            string skippedCaptureName = string.Empty;
            for (int i = 0; i < targets.Count; i++)
            {
                string tid = SafeId(targets[i]);
                bool isDefault = _defaultRenderId.Length > 0 &&
                                 string.Equals(tid, _defaultRenderId, StringComparison.OrdinalIgnoreCase);
                bool isCapture = avoidId.Length > 0 &&
                                 string.Equals(tid, avoidId, StringComparison.OrdinalIgnoreCase);
                if (isDefault || isCapture)
                {
                    if (isDefault) { skippedDefaultName = SafeName(targets[i]); }
                    else { skippedCaptureName = SafeName(targets[i]); }
                    continue;
                }
                safeTargets.Add(targets[i]);
            }

            if (safeTargets.Count == 0)
            {
                // 勾了的全被挡下来了：直接说清原因，不要让用户以为是设备坏了。
                string why = skippedCaptureName.Length > 0
                    ? "采集源「" + skippedCaptureName + "」不能同时作为输出目标（会自激啸叫）"
                    : "系统默认播放设备「" + skippedDefaultName + "」不能作为输出目标（会自激啸叫）";
                SetStatus(why, Theme.Warn);
                return;
            }

            int latency = _latencySlider.Value;
            _startBtn.Enabled = false;
            SetStatus("正在启动…", Theme.InkMid);
            Cursor = Cursors.WaitCursor;
            StartReport report;
            try
            {
                report = _engine.Start(capture, safeTargets, latency);
            }
            finally
            {
                Cursor = Cursors.Default;
                // 归还上面借来的采集源设备（引擎已不再持有它）
                if (capture != null) { try { capture.Dispose(); } catch { } }
            }

            if (!report.Started || report.FatalError != null)
            {
                SetStatus("启动失败：" + (report.FatalError ?? "未知原因"), Theme.Danger);
                _startBtn.Enabled = true;
                return;
            }

            _running = true;
            // 成功启动 = 故障态结束，头像可以正常回到「专注」。
            _engineFaulted = false;
            _startBtn.Enabled = false;
            _startBtn.Variant = PillButton.Kind.Secondary;
            _stopBtn.Enabled = true;
            _captureCombo.Enabled = false;
            _latencySlider.Enabled = false;
            _selectAllBtn.Enabled = false;
            _invertBtn.Enabled = false;
            _deviceList.SetTogglesEnabled(false);

            // 把列表里的音量应用到刚建立的各路输出。
            // 必须在引擎 Start 之后：Start 之前不存在输出通道，SetVolume 找不到设备。
            ApplyVolumesToEngine();
            _statusDot.DotColor = Theme.Accent;
            _statusDot.Pulse = true;
            // 输出中：头像换成「专注」。这是三态指示里的中间态，不需要环。
            SetAvatarMood(AvatarArt.Focus, null);

            string msg = "正在输出到 " + report.Attached.Count + " 个设备";
            if (report.Failed.Count > 0) { msg += "，失败 " + report.Failed.Count + " 个"; }
            // 被防呆挡下来的那几台要说清楚，否则用户会以为勾选没生效。
            if (skippedCaptureName.Length > 0) { msg += "；已跳过采集源 " + skippedCaptureName + "（防止自激）"; }
            if (skippedDefaultName.Length > 0) { msg += "；已跳过系统默认播放设备 " + skippedDefaultName + "（防止自激）"; }
            SetStatus(msg, (report.Failed.Count > 0 || skippedCaptureName.Length > 0 || skippedDefaultName.Length > 0) ? Theme.Warn : Theme.InkMid);

            if (report.Failed.Count > 0) { ShowProblems(report); }

            // 让主窗口获得焦点，避免下拉框抢焦点
            Activate();
        }

        /// <summary>
        /// 把设备列表里的音量逐个应用到引擎的对应输出通道。
        ///
        /// 引擎按设备 ID 匹配；列表里那些没被勾选、因而没有输出的设备当然找不到，
        /// SetVolume 会返回 false，属正常情况，不报错。
        /// </summary>
        void ApplyVolumesToEngine()
        {
            if (_engine == null) { return; }
            for (int i = 0; i < _deviceList.Count; i++)
            {
                DeviceListView.Row it = _deviceList[i];
                if (!it.Checked || it.Device == null) { continue; }
                try { _engine.SetVolume(it.Id, it.Volume); } catch { }
            }
        }

        void ShowProblems(StartReport report)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("以下设备未能启动：");
            foreach (string f in report.Failed) { text.AppendLine("· " + f); }
            text.AppendLine();
            text.AppendLine("其余 " + report.Attached.Count + " 个设备已正常工作。");
            MessageBox.Show(this, text.ToString(), "部分设备启用失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void StopEngine(string status)
        {
            // Stopped 事件可能来自音频线程；控件只能在 UI 线程操作，此处统一编组。
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(StopEngine), status); }
                catch (InvalidOperationException) { /* 窗口已销毁 */ }
                return;
            }

            if (!_running && !_engine.IsRunning) { return; }
            Cursor = Cursors.WaitCursor;
            try { _engine.Stop(); }
            catch { }
            finally { Cursor = Cursors.Default; }

            _running = false;
            ClearLevels();
            _startBtn.Variant = PillButton.Kind.Primary;
            _stopBtn.Enabled = false;
            _captureCombo.Enabled = true;
            _latencySlider.Enabled = true;
            _selectAllBtn.Enabled = true;
            _invertBtn.Enabled = true;
            _deviceList.SetTogglesEnabled(true);
            // 就绪 = 绿点（与 design\UI二次元改造方案.md 的状态表一致：
            // 绿 #229E42 表示「一切正常、只是还没开始」，蓝表示输出中，红表示出错）。
            // 之前这里用 InkFaint 灰点，是改造前「状态点只做装饰」的遗留，
            // 灰点在浅色底上几乎看不见，白丢了「一眼看出状态」的冗余编码。
            _statusDot.DotColor = _engineFaulted ? Theme.Danger : Theme.Success;
            _statusDot.Pulse = false;
            // 回到就绪：头像恢复「微笑」。停止是正常操作，不出错，所以不带红环。
            // 例外：如果本次运行刚出过故障（_engineFaulted），Stopped → StopEngine
            // 会紧跟在 Fault 后面执行；此时必须保留「平静 + 红环」，否则故障态
            // 只闪一帧就被复位，用户根本看不到（见字段注释）。
            if (!_engineFaulted) { SetAvatarMood(AvatarArt.Smile, null); }
            UpdateStartButtonState();
            // 故障态的收尾文字同样要保留红色：Stopped 带的原因和 Fault 说的是同一件事，
            // 但颜色若被复位成 InkMid，「中断」看起来就像一次正常停止。
            SetStatus(status, _engineFaulted ? Theme.Danger : Theme.InkMid);
        }

        void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetStatus), text, color); return; }
            _statusText.Text = text;
            _statusText.ForeColor = color;
        }

        void ClearLevels()
        {
            _captureMeter.Clear();
            _deviceList.ClearLevels();
        }

        // ═════════════════════════ 电平刷新 ═════════════════════════

        void TickLevels()
        {
            if (_closing) { return; }
            if (!_running)
            {
                // 未运行时保持静默，避免无意义的 UI 刷新
                return;
            }

            float[] levels = _engine.ReadLevels();
            // 按设备 ID 匹配，而不是按设备名：
            // 两块同型号显示器/USB 声卡会产生完全同名的端点，用名字匹配会串台。
            List<string> ids = _engine.OutputIds;

            float max = 0f;
            for (int i = 0; i < _deviceList.Count; i++)
            {
                DeviceListView.Row it = _deviceList[i];
                float v = 0f;
                if (it.Id.Length > 0 && ids != null)
                {
                    int idx = ids.IndexOf(it.Id);
                    if (idx >= 0 && idx < levels.Length) { v = levels[idx]; }
                }
                _deviceList.PushLevel(i, v);
                if (v > max) { max = v; }
            }
            _captureMeter.Push(max);

            long dropped = _engine.DroppedBytes;
            if (dropped > 0)
            {
                SetStatus("检测到音频缓冲溢出（已丢弃 " + (dropped / 1024) + " KB），建议提高延迟值", Theme.Warn);
            }
        }

        // ═════════════════════════ 设置持久化 ═════════════════════════

        void ApplySettings()
        {
            _latencySlider.SetValueSilent(_settings.LatencyMs);
            _latencyValue.Text = _settings.LatencyMs + " ms";
            _latencyValue.ForeColor = _settings.LatencyMs <= 15 ? Theme.Warn : Theme.Ink;
        }

        void SaveSettings()
        {
            try
            {
                _settings.LatencyMs = _latencySlider.Value;
                _settings.CaptureDeviceId = string.Empty;
                int ci = _captureCombo.SelectedIndex;
                // 用 ReloadDevices() 缓存的 _captureIds 反查，不再重新枚举再按下标取。
                // 输出列表已经剔掉系统默认播放设备，而下拉框列的是全部设备，两边的
                // 下标不再一一对应；重新枚举还会泄漏一次 COM 引用。
                if (ci > 0 && ci - 1 < _captureIds.Count) { _settings.CaptureDeviceId = _captureIds[ci - 1]; }
                _settings.SelectedDeviceIds.Clear();
                foreach (string id in _deviceList.CheckedIds()) { if (id.Length > 0) { _settings.SelectedDeviceIds.Add(id); } }
                // 音量：只保存当前枚举到的设备，拔掉的设备保留原值不动
                // （否则用户临时拔一次声卡，回来时音量就丢了）。
                if (_settings.DeviceVolumes == null) { _settings.DeviceVolumes = new Dictionary<string, int>(); }
                Dictionary<string, int> live = _deviceList.EnabledVolumes();
                foreach (KeyValuePair<string, int> kv in live)
                {
                    if (kv.Key.Length > 0) { _settings.DeviceVolumes[kv.Key] = kv.Value; }
                }
                if (WindowState == FormWindowState.Normal)
                {
                    _settings.WindowWidth = Width;
                    _settings.WindowHeight = Height;
                    _settings.WindowX = Location.X;
                    _settings.WindowY = Location.Y;
                    _settings.WindowDpi = Theme.CurrentDpi;
                    _settings.WindowPlaced = true;
                }
                _settings.Save();
            }
            catch { }
        }

        // ═════════════════════════ 恢复窗口位置 ═════════════════════════

        /// <summary>
        /// 窗口「句柄已建好、但还没显示」时把启动期重活全部干完。
        ///
        /// 为什么必须放在 OnLoad 而不是 OnShown（「启动瞬间黑块」的根因修复）：
        /// 逐帧抓图实测（tests\paintline2）显示，窗口 600 ms 就被显示出来，
        /// 而各子控件第一次 WM_PAINT 迟至 872 ms 才发生——中间 272 ms 里父窗体
        /// 已经把浅灰画布与白卡片画好，子控件却仍是「从未绘制过」的空窗口，
        /// 未绘制区域按默认深色呈现，于是看起来就是一堆黑方块。
        /// OnShown 在窗口已经可见之后才触发，而这里的 DWM 阴影、圆角、
        /// KeepResizable、窗口尺寸迁移每一项都要走窗口消息往返，正好把那 272 ms 占满。
        /// 搬到 OnLoad 后这些工作全部发生在窗口可见之前，首帧即为画好的界面。
        /// （OnLoad 里 Handle 已存在，DWM/P-Invoke 调用与 Screen.FromControl 都可用。）
        /// </summary>
        /// <summary>
        /// 开机自启 / --minimized 时，窗口显示完成后立刻收进托盘。
        ///
        /// 为什么不在 SetVisibleCore 里直接拒绝显示：OnLoad 里那一整套初始化
        /// （DWM 阴影、圆角、KeepResizable、恢复 Bounds、LayoutContent、
        /// EnsureListFits、WarmUpPaint）只有在窗体真正走完「显示」流程时才会执行，
        /// 提前拒绝显示会让这些全部跳过，用户之后从托盘打开时拿到的是一个没排过版、
        /// 没有圆角、尺寸也不对的窗口。
        ///
        /// 那怎么避免闪一下？构造期把 Opacity 设成 0（分层窗口 alpha=0，仍然
        /// 参与正常的显示/布局/绘制流程，只是完全透明），OnShown 之后再 Hide。
        /// 用户从头到尾看不到任何中间态 —— 既没有黑块，也没有一闪而过的界面。
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_startHidden) { return; }
            try
            {
                Hide();
                Opacity = 1d;
                if (_tray != null) { _tray.ShowMinimizedHint(); }
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                WndProcHook.EnableShadow(this);
                if (!WndProcHook.TryNativeRounded(this, 0))
                {
                    // 老系统回退：GDI Region 圆角
                    ApplyFallbackRegion();
                }
                WndProcHook.KeepResizable(this);

                if (_settings.WindowPlaced && _settings.WindowWidth >= 200 && _settings.WindowHeight >= 200)
                {
                    // 尺寸是物理像素，必须按「保存时的 DPI → 当前 DPI」等比换算。
                    // 否则用户从 100% 换到 150% 后，480x632 的物理像素只剩 320x421 逻辑像素，
                    // 设备列表被挤成 4 行、文字全贴在一起（tests\dpilayout 实测）。
                    int savedDpi = (_settings.WindowDpi >= 48 && _settings.WindowDpi <= 480) ? _settings.WindowDpi : 96;
                    double k = Theme.CurrentDpi / (double)savedDpi;
                    int w = (int)Math.Round(_settings.WindowWidth * k, MidpointRounding.AwayFromZero);
                    int h = (int)Math.Round(_settings.WindowHeight * k, MidpointRounding.AwayFromZero);
                    if (w < MinW) { w = MinW; }
                    if (h < MinH) { h = MinH; }
                    var want = new Rectangle(_settings.WindowX, _settings.WindowY, w, h);
                    // 仅当仍落在某个屏幕内才恢复，否则居中（避免显示器拔出后窗口跑到屏幕外）
                    bool visible = false;
                    foreach (Screen sc in Screen.AllScreens)
                    {
                        if (sc.WorkingArea.IntersectsWith(want)) { visible = true; break; }
                    }
                    if (visible)
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = want;
                    }
                }
                LayoutContent();
                EnsureListFits();
                ReportStartupWarnings();
                WarmUpPaint();
            }
            catch { }
        }

        /// <summary>
        /// 在窗口仍不可见时把整套绘制代码先跑一遍，让 JIT 与 GDI+ 的字体/画刷付掉首次成本。
        ///
        /// 为什么需要：38 个子控件的第一次绘制本身要 ~185 ms（paintline3 base 模式实测：
        /// 坏帧区间 537 → 722 ms）。只把重活搬到 OnLoad 并不能消掉这段时间——它发生在
        /// 窗口可见之后，用户照样看得见黑块。DrawToBitmap 走的是 WM_PRINT，
        /// 不要求窗口可见，所以可以在显示前把所有 OnPaint 预热一遍。
        /// 它只用于预热、不参与实际显示，因此个别控件的 WM_PRINT 结果与真实绘制
        /// 有细微差别也无影响（真正显示时仍走正常 WM_PAINT）。
        /// </summary>
        void WarmUpPaint()
        {
            try
            {
                Size s = ClientSize;
                if (s.Width <= 0 || s.Height <= 0) { return; }
                using (Bitmap bmp = new Bitmap(s.Width, s.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, s.Width, s.Height));
                }
            }
            catch { }
        }

        /// <summary>
        /// 让整棵控件树立刻完成各自的绘制，不留待消息队列。
        ///
        /// 与 ForceRepaint 的区别：ForceRepaint 走 RedrawWindow(RDW_UPDATENOW)，它只
        /// 处理「已标记失效」的区域，而且对自绘控件（SkinnedControl 覆写了 OnPaint
        /// 却没调 base.OnPaint）不一定触达。这里逐个控件 Invalidate + Update，是同步
        /// 调用，返回时每个子窗口的表面都已是成品。
        ///
        /// 用途：从最小化恢复时，在把窗口 alpha 从 0 恢复到 255 之前必须调一次 ——
        /// 实测少了这一步会留下单帧的中间态（dark=53%、色数=2）。
        /// </summary>
        void UpdateChildrenNow()
        {
            try
            {
                UpdateChildrenRecursive(this);
                Invalidate(true);
                Update();
            }
            catch { }
        }

        /// <summary>
        /// 采样自己的窗口表面，等它真的画成成品界面后再返回。
        ///
        /// 为什么需要「等」而不是「调一次 Update 就假定画完了」：
        /// Update() 只保证「本线程此刻已处理的绘制请求」完成。子控件的 WM_PAINT
        /// 是各自独立的窗口消息，某些路径下（首次显示、控件刚重建、GPU 合成
        /// 排队）它们会在 Update() 返回之后才陆续到达。实测概率约 1/6：窗口已经
        /// 恢复不透明，但标题、设备列表、按钮还是纯黑 —— 正是「点开一瞬间还是
        /// 半成品」的残余形态。
        ///
        /// 判据用「唯一色数」而不是像素差异：成品界面是浅灰画布 + 白卡片 + 抗锯齿
        /// 文字，实测 67~76 色；只有画布和卡片、控件全黑时是 2 色；画了一半是
        /// 十几到四十色。阈值取 50 能干净分开这三档，又不会因为设备数量变化而误判。
        ///
        /// 上限 250ms：正常情况下第一轮就通过（子控件已画完）。真的等超时也照样
        /// 放行 —— 宁可让用户看到一帧中间态，也不能让窗口一直隐形。
        /// </summary>
        void WaitUntilPainted()
        {
            try
            {
                const int MinColors = 50;
                for (int i = 0; i < 25; i++)
                {
                    int colors;
                    double dark;
                    if (WndProcHook.SampleSurface(Handle, out colors, out dark) && colors >= MinColors)
                    {
                        if (i > 0) { WndProcHook.DiagLog("WaitUntilPainted: 第 " + i + " 轮才画完（色数=" + colors + "）"); }
                        return;
                    }

                    // 再催一次：单独调 Update() 对没收到 WM_PAINT 的子控件无效，
                    // 必须逐个 Invalidate + Update 才能把它们逼出来。
                    UpdateChildrenNow();
                    System.Threading.Thread.Sleep(10);
                }
                WndProcHook.DiagLog("WaitUntilPainted: 等满 250ms 仍未达标，放行（避免窗口一直隐形）");
            }
            catch (Exception ex)
            {
                WndProcHook.DiagLog("WaitUntilPainted 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void UpdateChildrenRecursive(Control parent)
        {
            if (parent == null) { return; }
            foreach (Control c in parent.Controls)
            {
                try
                {
                    if (!c.IsHandleCreated) { continue; }
                    c.Invalidate();
                    c.Update();
                    UpdateChildrenRecursive(c);
                }
                catch { }
            }
        }

        /// <summary>
        /// 首次显示时按需把窗口长高，保证设备列表能完整显示 MaxVisibleRows 行。
        ///
        /// 起因：补上三个分组标题后内容整体变高了，而用户配置里保存的是旧窗口高度，
        /// 恢复后列表最后一行会被切掉——看起来像渲染坏了，而不是像可以滚动。
        /// 这里只在启动时调整一次（由 OnLoad 调用，见上文「启动瞬间黑块」的说明：
        /// 调整必须在窗口可见之前完成，否则用户会看到窗口先弹出来、再被撑高的跳动），
        /// 且不超过屏幕工作区；用户之后手动缩放不受影响。
        ///
        /// 为什么用「量差值再补」而不是一次解方程：
        /// 窗口本身是无边框的（FormBorderStyle.None），但 OnLoad 里 KeepResizable()
        /// 会补上 WS_THICKFRAME 让窗口仍可缩放，Windows 随之加上一圈不可见的调整边框。
        /// WinForms 并不知道这圈边框（它按无边框计算窗口矩形），于是「设 ClientSize」
        /// 与实际得到的客户区总是差一个边框厚度——一次算准永远差最后一行。
        /// 这里每轮只补「还差多少」，用实测的 _deviceCard.Height 收敛，最多三轮。
        ///
        /// 注意只动高度（Height += ...），不要写 ClientSize：WinForms 认为无边框窗口
        /// 没有边框，会把 ClientSize.Width 当成外框宽度写下去，窗口会凭空变窄十几像素。
        ///
        /// 只在 _settings.ListFitted 为 false 时执行一次，执行完置 true 并保存。
        /// 否则用户特意拉小的窗口每次启动都会被顶回去。
        /// </summary>
        void EnsureListFits()
        {
            try
            {
                if (_settings.ListFitted || _content == null) { return; }

                if (_deviceList.Count > 0 && _deviceCard != null)
                {
                    int wantH = Math.Min(_deviceList.Count, MaxVisibleRows) * RowH + Theme.S(2);
                    int limit = Screen.FromControl(this).WorkingArea.Height;

                    for (int i = 0; i < 3; i++)
                    {
                        LayoutContent();
                        int deficit = wantH - _deviceCard.Height;
                        if (deficit <= 0) { break; }
                        if (Height + deficit > limit) { break; }
                        Height += deficit;
                    }
                    LayoutContent();
                }

                // 迁移完成，记下来，之后每次启动都不再自动改窗口尺寸。
                // 没有设备时也要落这个标记：此时没有内容要适配，用户当前的窗口大小就是最终答案，
                // 不能等到某天插上设备再突然把窗口顶大。
                _settings.ListFitted = true;
                _settings.Save();
            }
            catch { }
        }

        void ApplyFallbackRegion()
        {
            try
            {
                WndProcHook.ApplyRegion(this, (int)Theme.RadiusWindow);
            }
            catch { }
        }

        void ReportStartupWarnings()
        {
            if (_startupWarnings.Count == 0) { return; }
            _statusText.Text = _startupWarnings[0];
            _statusText.ForeColor = Theme.Warn;
            _startupWarnings.Clear();
        }

        // ═════════════════════════ 关闭 ═════════════════════════

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closing) { return; }

            // 用户点关闭 / Alt+F4 / 托盘「退出」都会走到这里。
            // 前两者在 CloseToTray 打开时应该只收进托盘；只有 _reallyExit 才是真退出。
            //
            // 注意必须放在 _closing = true 之前判断并 return：一旦设了 _closing，
            // 后续任何 Close() 都会在方法开头被吞掉，窗口再也关不掉。
            if (!_reallyExit && _settings.CloseToTray && _tray != null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            e.Cancel = true;   // 先取消，异步清理完成后再真正关闭
            _closing = true;

            if (_levelTimer != null) { _levelTimer.Stop(); }
            if (_watcher != null) { _watcher.Dispose(); _watcher = null; }

            // 热键与托盘必须在主线程释放：UnregisterHotKey 要求同一线程，
            // NotifyIcon 的句柄也归属创建它的线程。
            try { HotKey.Unregister(Handle); } catch { }
            _hotKey = null;
            try { if (_tray != null) { _tray.Dispose(); _tray = null; } } catch { }

            SetStatus("正在停止音频…", Theme.InkMid);
            Cursor = Cursors.WaitCursor;

            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, a) =>
            {
                try { _engine.Dispose(); } catch { }
            };
            worker.RunWorkerCompleted += (s, a) =>
            {
                try { SaveSettings(); } catch { }

                // 这里必须全程防御：DoWork 在后台线程跑，完成回调也在后台线程。
                // 如果这段时间里窗体已经被销毁（任务管理器结束进程、会话注销、
                // 或者调用方直接 Dispose 了窗体），BeginInvoke 会抛
                // InvalidOperationException「在创建窗口句柄之前，不能在控件上调用
                // Invoke 或 BeginInvoke」。那是线程池线程，没有人 catch，整个进程
                // 会以 0xE0434352 崩掉 —— 而用户看到的只是「关窗口时闪一下错误框」。
                // 实测：tests\dpilayout 里先 Close() 再 Dispose() 就必现。
                try
                {
                    if (IsDisposed || Disposing) { return; }
                    Cursor = Cursors.Default;
                }
                catch { }

                try
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) { return; }
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            FormClosing -= OnFormClosing;
                            Close();
                        }
                        catch { }
                    }));
                }
                catch { }
            };
            worker.RunWorkerAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _engine.Dispose();
                // 释放设备行持有的 MMDevice COM 引用
                try { ReleaseRowDevices(); } catch { }
                if (_levelTimer != null) { _levelTimer.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
