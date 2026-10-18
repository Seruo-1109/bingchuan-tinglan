using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace MultiAudioOutput
{
    /// <summary>
    /// 设备列表：整张列表只用一个 HWND，所有行、分隔线、开关、电平条全部在这里自绘。
    ///
    /// ─────────── 为什么要合并成一个控件 ───────────
    /// 旧实现是「每个设备行一个 Panel，行内再放 ToggleSwitch + LevelMeter」：
    /// 5 台设备 = 5 个 Panel + 5 个 ToggleSwitch + 5 个 LevelMeter = 15 个子 HWND，
    /// 加上卡片、标签、按钮，主窗口一共有 36 个子窗口。
    ///
    /// 每个子窗口都是独立的 HWND，系统要求它们各自处理一次 WM_PAINT 才会有内容。
    /// 实测（tests\paintline2）：窗口 600 ms 就可见了，第一个子控件的 WM_PAINT
    /// 迟至 872 ms 才到 —— 中间 272 ms 里父窗体已经把浅灰画布和白卡片画好，
    /// 子控件却还是「从未绘制过」的空窗口，未绘制区域按默认深色呈现，
    /// 于是用户看到的就是一堆黑色/浅灰色的方块。启动黑块与「从任务栏恢复」
    /// 的骨架帧是同一个机制，只是触发时刻不同。
    ///
    /// 合并成单个控件后，整张列表只有 1 次 WM_PAINT，不再有「N 个子窗口排队首绘」
    /// 的问题；配合 WarmUpPaint 的预热，恢复瞬间就能直接上成品画面。
    ///
    /// ─────────── 交互与无障碍 ───────────
    /// 行内的开关不再是真控件，所以点击、悬停、键盘操作都在这里自己处理：
    ///   · 鼠标：点击开关区域切换；悬停高亮；滚轮滚动
    ///   · 键盘：Tab 进入列表，上下键移动当前行，空格切换开关
    ///   · 无障碍：AccessibleRole = List，每行通过 AccessibleObject 暴露名字与选中状态
    ///
    /// 这是自绘列表相对「一堆真控件」的真实退步（屏幕阅读器拿不到原生控件语义），
    /// 因此这里补了 AccessibleObject 覆盖与完整键盘操作作为最低补偿。
    /// </summary>
    internal sealed class DeviceListView : SkinnedControl
    {
        // ───────── 常量 ─────────

        /// <summary>行高（96 DPI 基准 56，实际值经 Theme.S 缩放）。</summary>
        internal static int RowHeight { get { return Theme.S(56); } }

        const int RowBase = 56;

        // ───────── 数据 ─────────

        /// <summary>
        /// 一行设备的数据。这里只存数据，不再持有任何子控件。
        /// </summary>
        internal sealed class Row
        {
            public MMDevice Device;
            public string Id = string.Empty;
            public string Name = string.Empty;
            public bool Checked;
            public float Level;
            public float Peak;
            public int PeakHold;
            public float Anim;          // 开关动画 0=关 1=开
            public bool ToggleEnabled = true;

            /// <summary>
            /// 本行的输出音量（0..100）。仅在该行被勾选时有意义：
            /// 未启用的设备不参与输出，音量条也不显示。
            /// </summary>
            public int Volume = 100;

            /// <summary>这一行的电平条（就是底部分隔线）在上一帧的矩形，供 Invalidate 局部刷新。</summary>
            public Rectangle MeterRect;
            /// <summary>这一行的开关在上一帧的矩形，用于命中测试。</summary>
            public Rectangle ToggleRect;
            /// <summary>这一行的音量条在上一帧的矩形，用于命中测试与拖动。</summary>
            public Rectangle VolumeRect;
        }

        readonly List<Row> _items = new List<Row>();
        readonly Timer _animTimer;

        int _scrollY;
        int _hotRow = -1;        // 鼠标悬停行
        int _focusRow = -1;      // 键盘焦点行
        bool _downOnToggle;      // 鼠标按在开关上（松开时才切换，与真控件行为一致）
        int _dragVolume = -1;    // 正在拖动音量条的行号，-1 表示没有

        // ───────── 事件 ─────────

        /// <summary>某行的开关被用户切换（只由鼠标/键盘触发，程序化赋值不触发）。</summary>
        public event EventHandler CheckedChanged;

        /// <summary>
        /// 某行的音量被用户改变。发送者是 DeviceListView，
        /// 用 <see cref="VolumeRowIndex"/> 与 <see cref="VolumeRowId"/> 取是哪一行。
        /// 拖动过程中会连续触发，调用方自行决定要不要立即应用。
        ///
        /// 注意：拖动一次会触发几十上百次（每个像素一次）。**不要在这里做磁盘 IO**
        /// —— 需要落盘请订阅 <see cref="VolumeChangeCommitted"/>。
        /// </summary>
        public event EventHandler VolumeChanged;

        /// <summary>
        /// 用户结束调整音量（松开鼠标 / 键盘按键抬手 / 失去捕获）。
        /// 拖动过程中每像素触发一次的 <see cref="VolumeChanged"/> 不适合落盘：
        /// 一次拖动就能写几十次 settings.json，每次都是序列化 + 临时文件 + 原子改名。
        /// 这个方法只在「动作结束」时发一次，落盘挂在这里。
        /// </summary>
        public event EventHandler VolumeChangeCommitted;

        /// <summary>最近一次 VolumeChanged 对应的行号。</summary>
        internal int VolumeRowIndex { get; private set; }

        /// <summary>最近一次 VolumeChanged 对应行的设备 ID。</summary>
        internal string VolumeRowId
        {
            get
            {
                int i = VolumeRowIndex;
                if (i < 0 || i >= _items.Count) { return string.Empty; }
                return _items[i].Id ?? string.Empty;
            }
        }

        // ───────── 对外接口 ─────────

        internal int Count { get { return _items.Count; } }
        internal IList<Row> Items { get { return _items; } }

        internal Row this[int i] { get { return _items[i]; } }

        public DeviceListView()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "输出设备列表";

            // 开关动画：只在有行处于过渡态时运行，静止时自动停表，避免空转耗电
            _animTimer = new Timer { Interval = 16 };
            _animTimer.Tick += (_, __) => TickAnim();
        }

        /// <summary>
        /// 整体替换设备列表。checkedIds 里命中的设备会被勾上；
        /// volumes 是「设备 ID → 音量百分比」的字典，缺失的设备按 100 处理。
        /// </summary>
        internal void SetDevices(List<MMDevice> devices, ICollection<string> checkedIds, IDictionary<string, int> volumes)
        {
            _items.Clear();
            _hotRow = -1;
            _focusRow = devices.Count > 0 ? 0 : -1;
            _scrollY = 0;
            _dragVolume = -1;

            for (int i = 0; i < devices.Count; i++)
            {
                MMDevice d = devices[i];
                string id = SafeId(d);
                bool on = checkedIds != null && checkedIds.Contains(id);
                int vol = 100;
                if (volumes != null && id.Length > 0)
                {
                    int v;
                    if (volumes.TryGetValue(id, out v))
                    {
                        if (v < 0) { v = 0; }
                        if (v > 100) { v = 100; }
                        vol = v;
                    }
                }
                var it = new Row
                {
                    Device = d,
                    Id = id,
                    Name = SafeName(d),
                    Checked = on,
                    Anim = on ? 1f : 0f,
                    Volume = vol
                };
                _items.Add(it);
            }

            UpdateScrollRange();
            Invalidate();
        }

        /// <summary>兼容旧调用：不带音量字典时全部按 100。</summary>
        internal void SetDevices(List<MMDevice> devices, ICollection<string> checkedIds)
        {
            SetDevices(devices, checkedIds, null);
        }

        /// <summary>读取某行的音量（0..100）。</summary>
        internal int GetVolume(int index)
        {
            if (index < 0 || index >= _items.Count) { return 100; }
            return _items[index].Volume;
        }

        /// <summary>
        /// 程序化设置某行音量（不触发 VolumeChanged），供启动时按保存的配置恢复。
        /// </summary>
        internal void SetVolumeSilent(int index, int percent)
        {
            if (index < 0 || index >= _items.Count) { return; }
            if (percent < 0) { percent = 0; }
            if (percent > 100) { percent = 100; }
            if (_items[index].Volume == percent) { return; }
            _items[index].Volume = percent;
            Invalidate();
        }

        /// <summary>当前所有已启用设备的「ID → 音量」，供持久化。</summary>
        internal Dictionary<string, int> EnabledVolumes()
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                if (it.Id.Length > 0) { map[it.Id] = it.Volume; }
            }
            return map;
        }

        /// <summary>读取当前所有被勾选的设备 ID（顺序与列表一致）。</summary>
        internal List<string> CheckedIds()
        {
            var list = new List<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Checked && _items[i].Id.Length > 0) { list.Add(_items[i].Id); }
            }
            return list;
        }

        internal bool AnyChecked()
        {
            for (int i = 0; i < _items.Count; i++) { if (_items[i].Checked) { return true; } }
            return false;
        }

        /// <summary>全部勾选/全部取消/反转。silent=true 表示不触发 CheckedChanged。</summary>
        internal void SetAllChecked(bool on, bool silent)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                if (it.Checked == on) { continue; }
                it.Checked = on;
                if (silent) { it.Anim = on ? 1f : 0f; }
                else { StartAnimIfNeeded(); }
            }
            Invalidate();
            if (!silent) { RaiseCheckedChanged(); }
        }

        internal void InvertChecked(bool silent)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                it.Checked = !it.Checked;
                if (silent) { it.Anim = it.Checked ? 1f : 0f; }
            }
            if (!silent) { StartAnimIfNeeded(); }
            Invalidate();
            if (!silent) { RaiseCheckedChanged(); }
        }

        /// <summary>
        /// 按「方案」一次性设置勾选与音量。
        ///
        /// 与 SetDevices 的区别：这里**不动设备对象本身**，只改每行的状态。
        /// 方案切换不能重新枚举设备 —— 那会让引擎里正在用的 MMDevice 引用失效，
        /// 而且枚举本身要 20~60ms，切个方案卡一下很难看。
        ///
        /// 方案里列出、但当前没插上的设备会被静默跳过（拔了耳机再切回「耳机+音箱」方案，
        /// 不应该报错，只把插着的那台打开）。方案里没列出的设备一律取消勾选 ——
        /// 方案的语义是「这些，且只有这些」，而不是「再把这些加上」。
        ///
        /// silent=true 时不触发 CheckedChanged/VolumeChanged，由调用方统一处理
        /// （切换方案要同时改勾选与音量，逐行触发会让 MainForm 反复落盘）。
        /// </summary>
        internal void ApplyProfile(ICollection<string> ids, IDictionary<string, int> volumes, bool silent)
        {
            var want = new HashSet<string>(StringComparer.Ordinal);
            if (ids != null) { foreach (string id in ids) { if (!string.IsNullOrEmpty(id)) { want.Add(id); } } }

            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                bool on = it.Id.Length > 0 && want.Contains(it.Id);

                if (it.Checked != on)
                {
                    it.Checked = on;
                    if (silent) { it.Anim = on ? 1f : 0f; }
                }

                // 音量只对「方案里真的列出了这台设备」的行生效。
                // 否则切到方案 B（没存音量）时，会把用户刚在方案 A 下调好的音量抹成默认值。
                if (volumes != null && it.Id.Length > 0)
                {
                    int v;
                    if (volumes.TryGetValue(it.Id, out v))
                    {
                        if (v < 0) { v = 0; }
                        if (v > 100) { v = 100; }
                        it.Volume = v;
                    }
                }
            }

            if (!silent) { StartAnimIfNeeded(); }
            Invalidate();
            if (!silent) { RaiseCheckedChanged(); }
        }

        /// <summary>运行中禁用全部开关（视觉上保留选中状态）。</summary>
        internal void SetTogglesEnabled(bool on)
        {
            for (int i = 0; i < _items.Count; i++) { _items[i].ToggleEnabled = on; }
            Cursor = on ? Cursors.Default : Cursors.Default;
            Invalidate();
        }

        /// <summary>推入某行的电平值（0..1）。由 UI 线程的定时器调用。</summary>
        internal void PushLevel(int index, float v)
        {
            if (index < 0 || index >= _items.Count) { return; }
            Row it = _items[index];
            if (v < 0f) { v = 0f; }
            if (v > 1f) { v = 1f; }

            float oldVal = it.Level, oldPeak = it.Peak;

            if (v > it.Level) { it.Level = v; }
            else { it.Level = it.Level * 0.72f + v * 0.28f; }

            if (v >= it.Peak) { it.Peak = v; it.PeakHold = 24; }
            else if (it.PeakHold > 0) { it.PeakHold--; }
            else { it.Peak = it.Peak * 0.85f + v * 0.15f; }

            // 只刷新电平线所在的小矩形，不重画整张列表。
            // 线高只有 2px，但峰值标记会向上凸出 4px，因此上下各留余量。
            if (it.MeterRect.Width > 0)
            {
                int x0 = Math.Min((int)(oldVal * it.MeterRect.Width), (int)(it.Level * it.MeterRect.Width));
                int x1 = Math.Max((int)(oldVal * it.MeterRect.Width), (int)(it.Level * it.MeterRect.Width));
                x1 = Math.Max(x1, (int)(oldPeak * it.MeterRect.Width));
                x1 = Math.Max(x1, (int)(it.Peak * it.MeterRect.Width));
                int pad = Theme.S(3);
                int up = Theme.S(6);
                Rectangle r = new Rectangle(
                    it.MeterRect.X + Math.Max(0, x0 - pad), it.MeterRect.Y - up,
                    Math.Min(it.MeterRect.Width, x1 - x0 + pad * 2), it.MeterRect.Height + up);
                if (r.Width > 0) { Invalidate(r); }
            }
            else
            {
                Invalidate();
            }
        }

        internal void ClearLevels()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Level = 0f;
                _items[i].Peak = 0f;
                _items[i].PeakHold = 0;
            }
            Invalidate();
        }

        // ───────── 滚动 ─────────

        void UpdateScrollRange()
        {
            int content = _items.Count * RowHeight;
            int max = Math.Max(0, content - ClientSize.Height);
            if (_scrollY > max) { _scrollY = max; }
            if (_scrollY < 0) { _scrollY = 0; }
        }

        /// <summary>是否因为内容超出而需要滚动（供 LayoutContent 判断要不要留滚动条宽度）。</summary>
        internal bool NeedsScroll
        {
            get { return _items.Count * RowHeight > ClientSize.Height; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollRange();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, _items.Count * RowHeight - ClientSize.Height);
            if (max <= 0) { base.OnMouseWheel(e); return; }

            int delta = -e.Delta * RowHeight / 120;   // 一格滚一行
            if (delta == 0) { delta = e.Delta > 0 ? -RowHeight : RowHeight; }
            int old = _scrollY;
            _scrollY += delta;
            if (_scrollY < 0) { _scrollY = 0; }
            if (_scrollY > max) { _scrollY = max; }
            if (old != _scrollY) { Invalidate(); }
        }

        // ───────── 命中测试 ─────────

        int RowAt(Point p)
        {
            int y = p.Y + _scrollY;
            int i = y / RowHeight;
            if (i < 0 || i >= _items.Count) { return -1; }
            return i;
        }

        Rectangle RowBounds(int i)
        {
            return new Rectangle(0, i * RowHeight - _scrollY, ClientSize.Width, RowHeight);
        }

        Rectangle ToggleBounds(int i)
        {
            int tw = Theme.S(46), th = Theme.S(28);
            Rectangle row = RowBounds(i);
            return new Rectangle(row.Right - Theme.S(14) - tw, row.Y + (RowHeight - th) / 2, tw, th);
        }

        /// <summary>
        /// 底部那条线的矩形。它同时是「行分隔线」和「电平指示条」——
        /// 原先电平是行内一根独立的浅灰短横条，与底部分隔线并排，视觉上很别扭。
        /// 现在两者合成一条：静止时它就是普通分隔线，有声时从左往右长出绿色。
        /// </summary>
        Rectangle MeterBounds(int i)
        {
            Rectangle row = RowBounds(i);
            int h = Theme.S(2);
            return new Rectangle(row.X + Theme.S(14), row.Bottom - h,
                Math.Max(Theme.S(20), row.Width - Theme.S(28)), h);
        }

        /// <summary>音量条的命中矩形（只在该行已启用时有意义）。</summary>
        Rectangle VolumeBounds(int i)
        {
            Rectangle row = RowBounds(i);
            int right = row.Width - Theme.S(14) - Theme.S(46) - Theme.S(12);
            int left = Theme.S(14);
            int valueW = Theme.S(38);
            int w = right - left - valueW - Theme.S(6);
            if (w < Theme.S(40)) { w = Theme.S(40); }
            return new Rectangle(left, row.Y + Theme.S(30), w, Theme.S(20));
        }

        /// <summary>百分比文字的矩形。</summary>
        Rectangle VolumeValueBounds(int i)
        {
            Rectangle row = RowBounds(i);
            int right = row.Width - Theme.S(14) - Theme.S(46) - Theme.S(12);
            int valueW = Theme.S(38);
            Rectangle vb = VolumeBounds(i);
            return new Rectangle(vb.Right + Theme.S(6), row.Y + Theme.S(30), valueW, Theme.S(20));
        }

        // ───────── 鼠标 ─────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int row = RowAt(e.Location);
            if (row < 0 || row >= _items.Count) { return; }
            _focusRow = row;

            if (e.Button == MouseButtons.Left)
            {
                // 音量条优先判定：它和文字区域在垂直方向上有重叠，
                // 但水平上音量条只占左侧约三分之二，且开关在右侧，不会互相干扰。
                if (_items[row].Checked && VolumeBounds(row).Contains(e.Location))
                {
                    _dragVolume = row;
                    // 捕获鼠标：拖出控件范围时仍能继续收到 Move/Up，
                    // 否则鼠标一离开列表，拖动就断了。
                    try { Capture = true; } catch { }
                    SetVolumeFromX(row, e.X);
                    return;
                }
                if (ToggleBounds(row).Contains(e.Location) && _items[row].ToggleEnabled)
                {
                    _downOnToggle = true;
                }
            }
            Invalidate(RowBounds(row));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragVolume >= 0)
            {
                if (_dragVolume < _items.Count) { SetVolumeFromX(_dragVolume, e.X); }
                return;
            }

            int row = RowAt(e.Location);
            if (row != _hotRow)
            {
                int old = _hotRow;
                _hotRow = row;
                if (old >= 0 && old < _items.Count) { Invalidate(RowBounds(old)); }
                if (_hotRow >= 0 && _hotRow < _items.Count) { Invalidate(RowBounds(_hotRow)); }
            }

            bool clickable = row >= 0 && row < _items.Count
                && (_items[row].ToggleEnabled || (_items[row].Checked && VolumeBounds(row).Contains(e.Location)));
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_dragVolume >= 0)
            {
                _dragVolume = -1;
                try { Capture = false; } catch { }
                Cursor = Cursors.Default;
                // 拖动到这里才算结束 —— 这一次落盘即可，
                // 拖动途中的每一次像素变化都不写磁盘。
                RaiseVolumeCommitted();
                return;
            }

            int row = RowAt(e.Location);
            if (e.Button == MouseButtons.Left && _downOnToggle && row >= 0 && row < _items.Count
                && ToggleBounds(row).Contains(e.Location) && _items[row].ToggleEnabled)
            {
                ToggleRow(row);
            }
            _downOnToggle = false;
            if (row >= 0 && row < _items.Count) { Invalidate(RowBounds(row)); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotRow >= 0 && _hotRow < _items.Count) { Invalidate(RowBounds(_hotRow)); }
            _hotRow = -1;
            _downOnToggle = false;
            // 注意：不在这里清 _dragVolume。拖到控件外再拖回来是常规操作，
            // 系统会把鼠标抬起事件仍然发给捕获窗口；这里若清掉，拖动会中途卡住。
            Cursor = Cursors.Default;
        }

        /// <summary>
        /// 鼠标捕获被系统收走（Alt+Tab 切换窗口、弹出模态框、被别的窗口抢捕获等）。
        /// 这种情况下 OnMouseUp 永远不会到，_dragVolume 会一直停在某个行号上，
        /// 后果是列表再也不会响应悬停高亮，而且下次点任意位置都会被当成「继续拖音量」。
        /// 这里必须兜底清掉，并把这次拖动当作已结束。
        /// </summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragVolume >= 0 && !Capture)
            {
                _dragVolume = -1;
                Cursor = Cursors.Default;
                RaiseVolumeCommitted();
            }
        }

        /// <summary>按鼠标 x 坐标设置某行音量（0..100），并触发 VolumeChanged。</summary>
        void SetVolumeFromX(int row, int x)
        {
            if (row < 0 || row >= _items.Count) { return; }
            Row it = _items[row];
            Rectangle vb = VolumeBounds(row);
            float x0 = vb.X + Theme.S(6f);
            float x1 = vb.Right - Theme.S(6f);
            if (x1 <= x0) { return; }

            float t = (x - x0) / (x1 - x0);
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            int v = (int)Math.Round(t * 100f);
            if (v < 0) { v = 0; }
            if (v > 100) { v = 100; }
            if (v == it.Volume) { return; }

            it.Volume = v;
            Invalidate(RowBounds(row));

            VolumeRowIndex = row;
            var h = VolumeChanged;
            if (h != null) { h(this, EventArgs.Empty); }
        }

        void ToggleRow(int i)
        {
            Row it = _items[i];
            it.Checked = !it.Checked;
            StartAnimIfNeeded();
            // 整行重绘而不是只重绘开关：勾选状态决定副信息行是「未启用」
            // 还是「音量条 + 百分比」，那一整块都要换掉。
            Invalidate(RowBounds(i));
            RaiseCheckedChanged();
        }

        void RaiseCheckedChanged()
        {
            var h = CheckedChanged;
            if (h != null) { h(this, EventArgs.Empty); }
        }

        // ───────── 键盘 ─────────

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.Space:
                case Keys.Left:
                case Keys.Right:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0) { return; }
            if (_focusRow < 0) { _focusRow = 0; }

            switch (e.KeyCode)
            {
                case Keys.Up:
                    SetFocusRow(_focusRow - 1);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    SetFocusRow(_focusRow + 1);
                    e.Handled = true;
                    break;
                case Keys.Home:
                    SetFocusRow(0);
                    e.Handled = true;
                    break;
                case Keys.End:
                    SetFocusRow(_items.Count - 1);
                    e.Handled = true;
                    break;
                case Keys.Space:
                    if (_focusRow >= 0 && _focusRow < _items.Count && _items[_focusRow].ToggleEnabled)
                    {
                        ToggleRow(_focusRow);
                    }
                    e.Handled = true;
                    break;
                // 左右键调音量：只在焦点行已启用时有效。
                // 步进 5%：1% 在 100 段上按一次几乎看不出来，5% 兼顾精细与效率。
                case Keys.Left:
                    NudgeVolume(_focusRow, -5);
                    e.Handled = true;
                    break;
                case Keys.Right:
                    NudgeVolume(_focusRow, +5);
                    e.Handled = true;
                    break;
            }
        }

        void NudgeVolume(int row, int delta)
        {
            if (row < 0 || row >= _items.Count) { return; }
            Row it = _items[row];
            if (!it.Checked) { return; }
            int v = it.Volume + delta;
            if (v < 0) { v = 0; }
            if (v > 100) { v = 100; }
            if (v == it.Volume) { return; }
            it.Volume = v;
            Invalidate(RowBounds(row));

            VolumeRowIndex = row;
            var h = VolumeChanged;
            if (h != null) { h(this, EventArgs.Empty); }

            // 键盘每按一次就是一个完整动作（不像拖动那样有明确的松手时机），
            // 所以每次按键都算一次「已提交」。
            RaiseVolumeCommitted();
        }

        /// <summary>通知订阅者「这次调音量已经结束」，用于落盘等低频动作。</summary>
        void RaiseVolumeCommitted()
        {
            var h = VolumeChangeCommitted;
            if (h != null) { h(this, EventArgs.Empty); }
        }

        void SetFocusRow(int i)
        {
            if (i < 0) { i = 0; }
            if (i >= _items.Count) { i = _items.Count - 1; }
            int old = _focusRow;
            _focusRow = i;

            // 把焦点行滚进可视区
            int top = i * RowHeight;
            int bottom = top + RowHeight;
            if (top < _scrollY) { _scrollY = top; }
            else if (bottom > _scrollY + ClientSize.Height)
            {
                _scrollY = bottom - ClientSize.Height;
            }
            UpdateScrollRange();

            if (old >= 0 && old < _items.Count) { Invalidate(RowBounds(old)); }
            Invalidate(RowBounds(_focusRow));
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (_focusRow < 0 && _items.Count > 0) { _focusRow = 0; }
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        // ───────── 动画 ─────────

        void StartAnimIfNeeded()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                float target = it.Checked ? 1f : 0f;
                if (Math.Abs(target - it.Anim) > 0.001f)
                {
                    if (!_animTimer.Enabled) { _animTimer.Start(); }
                    return;
                }
            }
        }

        void TickAnim()
        {
            bool busy = false;
            for (int i = 0; i < _items.Count; i++)
            {
                Row it = _items[i];
                float target = it.Checked ? 1f : 0f;
                float delta = target - it.Anim;
                if (Math.Abs(delta) < 0.03f)
                {
                    it.Anim = target;
                }
                else
                {
                    it.Anim += delta * 0.38f;
                    busy = true;
                    Invalidate(ToggleBounds(i));
                }
            }
            if (!busy) { _animTimer.Stop(); }
        }

        // ───────── 绘制 ─────────

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;

            // ── 自己画出整张卡片的圆角外形 ──
            //
            // 这个控件是铺满 Card 的（Location=(1,1)，Size=(卡片宽-2, 卡片高-2)）。
            // 之前这里只有一句 g.Clear(ResolveBackColor())，等于用一块不透明白矩形
            // 把 Card 已经画好的四角圆角整个盖住 —— 卡片在屏幕上就是直角，四个角
            // 还有一圈 1px 的边框断头（Card 的描边被列表压掉）。
            //
            // 修法：由列表自己承担卡片的视觉 —— 先铺「卡片背后」的颜色（画布灰），
            // 再用抗锯齿路径填白色圆角本体 + 描边，最后把内容裁剪在圆角内。
            // Card 仍然存在且仍会画，但它被列表完全覆盖，效果上只是一个位置容器。
            g.Clear(ResolveBackColorFrom(Parent != null ? Parent.Parent : null));

            var card = new RectangleF(0f, 0f, Width, Height);
            using (GraphicsPath shell = Squircle.Create(0.5f, 0.5f, Width - 1f, Height - 1f, Theme.RadiusCard))
            {
                using (var br = new SolidBrush(Theme.Surface)) { g.FillPath(br, shell); }
                using (var pen = new Pen(Theme.Line, 1f)) { g.DrawPath(pen, shell); }

                if (_items.Count == 0) { return; }

                // 行内容（悬停底色是整行宽的矩形、首行贴着顶边）必须裁在圆角内，
                // 否则第一行悬停时又会在四角切出直角。
                Region oldClip = g.Clip;
                g.SetClip(shell, CombineMode.Intersect);
                try
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        Rectangle row = RowBounds(i);
                        // 完全在可视区外的行直接跳过（滚动时省一半绘制）
                        if (row.Bottom <= 0 || row.Top >= ClientSize.Height) { continue; }
                        DrawRow(g, i, row);
                    }
                }
                finally
                {
                    g.Clip = oldClip;
                }
            }
        }

        void DrawRow(Graphics g, int i, Rectangle row)
        {
            Row it = _items[i];
            bool hot = (i == _hotRow);
            bool focused = (i == _focusRow) && Focused;

            // 悬停高亮：极淡的底色，iOS 列表的按压反馈
            if (hot || focused)
            {
                using (var br = new SolidBrush(focused ? Theme.SurfaceAlt : Theme.Mix(Theme.Surface, Theme.SurfaceAlt, 0.6f)))
                {
                    g.FillRectangle(br, row);
                }
            }

            // 设备名
            int textW = row.Width - Theme.S(90);
            using (var br = new SolidBrush(it.ToggleEnabled ? Theme.Ink : Theme.DisabledText))
            {
                var rect = new RectangleF(row.X + Theme.S(14f), row.Y + Theme.S(9f),
                    Math.Max(Theme.S(40), textW), Theme.S(20f));
                g.DrawString(it.Name, Theme.FName, br, rect, Theme.Left);
            }

            // 副信息行：启用后这里换成音量条 + 百分比，
            // 未启用时保持「未启用」灰字——不占用额外行高，窗口尺寸无需变化。
            if (it.Checked)
            {
                Rectangle vb = VolumeBounds(i);
                Rectangle vvb = VolumeValueBounds(i);
                it.VolumeRect = vb;
                DrawVolume(g, vb, it);
                using (var br = new SolidBrush(Theme.InkFaint))
                {
                    g.DrawString(it.Volume + "%", Theme.FSmall, br, vvb, Theme.Left);
                }
            }
            else
            {
                it.VolumeRect = Rectangle.Empty;
                using (var br = new SolidBrush(Theme.InkFaint))
                {
                    var rect = new RectangleF(row.X + Theme.S(14f), row.Y + Theme.S(29f),
                        Math.Max(Theme.S(40), textW), Theme.S(16f));
                    g.DrawString("未启用", Theme.FSmall, br, rect, Theme.Left);
                }
            }

            // 底部：分隔线与电平条合成的那一条（最后一行也画，作为列表收尾）
            Rectangle mr = MeterBounds(i);
            it.MeterRect = mr;
            DrawMeterLine(g, mr, it, i == _items.Count - 1);

            // 开关
            Rectangle tr = ToggleBounds(i);
            it.ToggleRect = tr;
            DrawToggle(g, tr, it);
        }

        /// <summary>
        /// 画底部分隔线。没有声音时是一条与卡片同色的浅线；
        /// 有声音时，声压所及的那一段变成绿色，峰值处一根细线。
        /// 因为线高只有 2px，绿色的「生长」看起来是沿线滑动，比独立的一根粗横条克制得多。
        /// </summary>
        void DrawMeterLine(Graphics g, Rectangle r, Row it, bool lastRow)
        {
            g.SmoothingMode = SmoothingMode.None;

            // 底色：分隔线的颜色。最后一行用更淡的一档，避免列表底部收得太硬。
            using (var br = new SolidBrush(lastRow ? Theme.LineSoft : Theme.Line))
            {
                g.FillRectangle(br, r.X, r.Y, r.Width, r.Height);
            }

            float lvl = it.Level;
            if (lvl > 0.001f)
            {
                int fw = (int)Math.Round(r.Width * lvl);
                if (fw < 2) { fw = 2; }
                if (fw > r.Width) { fw = r.Width; }
                using (var br = new SolidBrush(Theme.Accent))
                {
                    g.FillRectangle(br, r.X, r.Y, fw, r.Height);
                }
            }

            // 峰值：一根比线略高的细标记，只在有明显峰值时出现
            if (it.Peak > 0.01f)
            {
                int px = r.X + (int)Math.Round(r.Width * it.Peak);
                int ph = Theme.S(6);
                using (var br = new SolidBrush(Theme.Mix(Theme.Accent, Color.Black, 0.25f)))
                {
                    g.FillRectangle(br, Math.Min(px, r.Right - 1), r.Y - (ph - r.Height), 1, ph);
                }
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        /// <summary>
        /// 行内音量条。比滑块更轻：一条 3px 轨道 + 实心圆点，
        /// 和 iOS 的设置项里那种内嵌滑杆一致。
        /// </summary>
        void DrawVolume(Graphics g, Rectangle r, Row it)
        {
            float cy = r.Y + r.Height / 2f;
            float x0 = r.X + Theme.S(6f);
            float x1 = r.Right - Theme.S(6f);
            if (x1 <= x0) { return; }
            float t = it.Volume / 100f;
            float kx = x0 + (x1 - x0) * t;

            // 轨道
            using (var pen = new Pen(Theme.Track, Theme.S(3f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x0, cy, x1, cy);
            }
            // 已填充
            if (kx > x0 + 0.5f)
            {
                using (var pen = new Pen(Theme.Accent, Theme.S(3f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, x0, cy, kx, cy);
                }
            }
            // 圆点
            float kr = Theme.S(6.5f);
            using (var sh = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
            { g.FillEllipse(sh, kx - kr, cy - kr + Theme.S(1f), kr * 2f, kr * 2f); }
            using (var br = new SolidBrush(Theme.White))
            { g.FillEllipse(br, kx - kr, cy - kr, kr * 2f, kr * 2f); }
            using (var pen = new Pen(Theme.Line, 1f))
            { g.DrawEllipse(pen, kx - kr, cy - kr, kr * 2f, kr * 2f); }
        }

        void DrawToggle(Graphics g, Rectangle r, Row it)
        {
            float w = r.Width, h = r.Height;
            float pad = Theme.S(2f);
            float knob = h - pad * 2f;

            // 悬停判定：鼠标是否正停在这个开关上（只有可点击时才有悬停反馈）
            bool hot = it.ToggleEnabled && r.Contains(PointToClientSafe(SafeMousePosition()));
            Color trackOn = Theme.Mix(Theme.Accent, Theme.AccentDark, hot ? 1f : 0f);
            Color trackOff = Theme.Mix(Theme.Track, Theme.Mix(Theme.Track, Theme.Ink, 0.10f), hot ? 1f : 0f);

            Color track;
            float pos;
            if (it.ToggleEnabled)
            {
                track = Theme.Mix(trackOff, trackOn, it.Anim);
                pos = it.Anim;
            }
            else
            {
                // 运行期间开关被禁用，但「已选/未选」必须仍然看得出来：
                // 禁用但已选 = 淡主色（保留选中语义），禁用且未选 = 灰。
                track = it.Checked ? Theme.Mix(Theme.Accent, Theme.White, 0.45f) : Theme.DisabledBg;
                pos = it.Checked ? 1f : 0f;
            }

            var box = new RectangleF(r.X + 0.5f, r.Y + 0.5f, w - 1f, h - 1f);
            using (var path = Squircle.Create(box.X, box.Y, box.Width, box.Height, h / 2f))
            using (var br = new SolidBrush(track)) { g.FillPath(br, path); }

            float kx = r.X + pad + (w - knob - pad * 2f) * pos;
            var knobRect = new RectangleF(kx, r.Y + pad, knob, knob);

            int shadowA = it.ToggleEnabled ? 38 : (it.Checked ? 24 : 0);
            using (var sp = Squircle.Create(knobRect.X, knobRect.Y + 1.2f, knobRect.Width, knobRect.Height, knob / 2f))
            using (var sb = new SolidBrush(Color.FromArgb(shadowA, 0, 0, 0)))
            { g.FillPath(sb, sp); }

            using (var kp = Squircle.Create(knobRect.X, knobRect.Y, knobRect.Width, knobRect.Height, knob / 2f))
            using (var kb = new SolidBrush(Theme.White))
            { g.FillPath(kb, kp); }
        }

        /// <summary>
        /// 鼠标位置转本控件坐标。取不到时返回一个必然在控件外的点，
        /// 这样「悬停加深」判断在失焦/无鼠标时自动为 false。
        /// </summary>
        Point PointToClientSafe(Point screen)
        {
            try
            {
                if (screen.X == int.MinValue) { return new Point(-9999, -9999); }
                return PointToClient(screen);
            }
            catch { return new Point(-9999, -9999); }
        }

        static Point SafeMousePosition()
        {
            try { return Control.MousePosition; }
            catch { return new Point(int.MinValue, int.MinValue); }
        }

        // ───────── 无障碍 ─────────

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new DeviceListAccessible(this);
        }

        sealed class DeviceListAccessible : Control.ControlAccessibleObject
        {
            readonly DeviceListView _owner;

            public DeviceListAccessible(DeviceListView owner) : base(owner) { _owner = owner; }

            public override int GetChildCount() { return _owner._items.Count; }

            public override AccessibleObject GetChild(int index)
            {
                if (index < 0 || index >= _owner._items.Count) { return null; }
                return new RowAccessible(_owner, index);
            }

            public override AccessibleRole Role { get { return AccessibleRole.List; } }
        }

        sealed class RowAccessible : AccessibleObject
        {
            readonly DeviceListView _owner;
            readonly int _index;

            public RowAccessible(DeviceListView owner, int index) { _owner = owner; _index = index; }

            public override string Name
            {
                get
                {
                    Row it = _owner._items[_index];
                    return it.Name + "，" + (it.Checked ? "已启用" : "未启用");
                }
            }

            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }

            public override AccessibleStates State
            {
                get
                {
                    Row it = _owner._items[_index];
                    return it.Checked ? AccessibleStates.Checked : AccessibleStates.None;
                }
            }

            public override Rectangle Bounds
            {
                get { return _owner.RectangleToScreen(_owner.RowBounds(_index)); }
            }

            public override AccessibleObject Parent { get { return _owner.AccessibilityObject; } }
        }

        // ───────── 清理 ─────────

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); }
            base.Dispose(disposing);
        }

        // ───────── 工具 ─────────

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
    }
}
