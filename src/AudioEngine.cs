using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiAudioOutput
{
    /// <summary>启动结果：哪些设备成功、哪些失败（不再一个失败就全部崩）。</summary>
    internal sealed class StartReport
    {
        public readonly List<string> Attached = new List<string>();
        public readonly List<string> Failed = new List<string>();
        public string CaptureName = string.Empty;
        public WaveFormat Format;
        public bool Started;
        public string FatalError;

        public bool AnyAttached { get { return Attached.Count > 0; } }
    }

    /// <summary>
    /// 音频引擎：WASAPI 环回采集 + 多路输出。
    ///
    /// 线程模型（这是旧版最薄弱的地方，此处显式规定）：
    ///   · UI 线程         —— 调用 Start / Stop；所有字段读写持有 _gate
    ///   · 采集线程(NAudio) —— OnData 回调，只做 AddSamples，绝不碰控件
    ///   · 输出线程(NAudio) —— 各自消费自己的 BufferedWaveProvider
    ///
    /// 关键修复：
    ///   1. 单设备失败不再中断全部输出（逐个 try，失败记入 Failed）
    ///   2. 采集/输出格式不一致时显式拒绝，而不是把数据硬塞进去
    ///   3. 缓冲区溢出计数并上报，不再静默丢音
    ///   4. 停止流程加锁，可重入，不泄漏句柄
    ///   5. 电平读数来自 MMDevice.AudioMeterInformation，不干扰音频链路
    /// </summary>
    internal sealed class AudioEngine : IDisposable
    {
        readonly object _gate = new object();

        MMDeviceEnumerator _enumerator;
        WasapiLoopbackCapture _capture;
        readonly List<WasapiOut> _outs = new List<WasapiOut>();
        readonly List<BufferedWaveProvider> _bufs = new List<BufferedWaveProvider>();
        readonly List<MMDevice> _meterDevices = new List<MMDevice>();
        readonly List<AudioMeterInformation> _meters = new List<AudioMeterInformation>();
        readonly List<string> _outNames = new List<string>();
        readonly List<string> _outIds = new List<string>();

        /// <summary>
        /// WasapiOut → 设备名。用于 OnPlaybackStopped 识别是哪一路断开。
        /// 这个 NAudio 版本的 WasapiOut 没有 OutputDevice 属性，只能在创建时记录。
        /// 用并发字典：读取发生在 WASAPI 播放线程，写入发生在 UI 线程，
        /// 且回调里**不能加 _gate**（StopInternal 持锁等待播放线程退出，会死锁）。
        /// </summary>
        readonly System.Collections.Concurrent.ConcurrentDictionary<WasapiOut, string> _outNameMap =
            new System.Collections.Concurrent.ConcurrentDictionary<WasapiOut, string>();

        volatile bool _running;
        long _droppedBytes;
        int _captureOverflows;

        /// <summary>
        /// 每路输出的音量（0..100 百分比），与 _outs/_bufs 同序。
        ///
        /// 为什么是「每路各自一份字节」而不是共享广播：音量必须逐路不同，
        /// 而 AddSamples 写入的是不可变字节数组。共享同一份缓冲就无法让两路
        /// 输出不同音量。因此采集回调里为每一路复制一份、各自缩放后写入。
        ///
        /// 复制开销可忽略：48kHz/32bit/立体声 ≈ 384 KB/s，5 路合计不到 2 MB/s，
        /// 且 scratch 缓冲复用、稳态下零分配。
        /// </summary>
        readonly List<int> _gains = new List<int>();

        /// <summary>每路独立的分路缓冲（避免每次回调都分配新数组）。与 _bufs 同序。</summary>
        readonly List<byte[]> _laneBuffers = new List<byte[]>();

        /// <summary>
        /// 采集格式。采集回调里做音量缩放必须知道样本位宽与编码，
        /// 而回调运行在 WASAPI 采集线程，不能每次去问 _capture.WaveFormat（COM 属性读取不可靠）。
        /// 因此在 Start 时快照一次。
        /// </summary>
        volatile WaveFormat _format;

        /// <summary>采集端累计丢帧（引擎自行统计，用于状态栏警告）。</summary>
        public long DroppedBytes { get { lock (_gate) { return _droppedBytes; } } }
        public bool IsRunning { get { return _running; } }
        public int OutputCount { get { lock (_gate) { return _outs.Count; } } }

        public MMDeviceEnumerator Enumerator
        {
            get
            {
                if (_enumerator == null) { _enumerator = new MMDeviceEnumerator(); }
                return _enumerator;
            }
        }

        /// <summary>
        /// 枚举正在工作的输出设备（Render + Active）。
        /// 调用方负责在不再使用后调用 ReleaseDevices() 释放，避免 COM 句柄堆积。
        /// </summary>
        public List<MMDevice> ListOutputDevices()
        {
            var list = new List<MMDevice>();
            try
            {
                MMDeviceCollection col = Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < col.Count; i++) { list.Add(col[i]); }
            }
            catch { }
            return list;
        }

        /// <summary>释放一批 MMDevice 的 COM 引用（枚举结果用完后必须调用）。</summary>
        public static void ReleaseDevices(IEnumerable<MMDevice> devices)
        {
            if (devices == null) { return; }
            foreach (MMDevice d in devices)
            {
                if (d == null) { continue; }
                try { d.Dispose(); } catch { }
            }
        }

        /// <summary>枚举可作为采集源的渲染设备（环回捕获）。</summary>
        public List<MMDevice> ListCaptureDevices()
        {
            // 环回捕获的源就是渲染端点本身
            return ListOutputDevices();
        }

        /// <summary>
        /// 开始采集并输出到指定设备。
        /// </summary>
        /// <param name="captureDevice">采集源；null 表示系统默认播放设备</param>
        /// <param name="targets">目标输出设备</param>
        /// <param name="latencyMs">WASAPI 延迟（毫秒）</param>
        public StartReport Start(MMDevice captureDevice, IList<MMDevice> targets, int latencyMs)
        {
            var report = new StartReport();
            lock (_gate)
            {
                if (_running) { report.FatalError = "引擎已在运行"; return report; }

                if (latencyMs < 5) { latencyMs = 5; }
                if (latencyMs > 500) { latencyMs = 500; }

                // ── 1. 建立采集 ──
                MMDevice src = captureDevice;
                // ownSrc 表示 src 是引擎自己从枚举器取来的，用完必须归还；
                // 调用方传进来的设备归调用方所有（它可能同时还是输出目标，引擎绝不能碰）。
                bool ownSrc = false;
                try
                {
                    if (src == null)
                    {
                        try { src = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                        catch { src = null; }
                        ownSrc = (src != null);

                        if (src == null)
                        {
                            // 默认设备取不到时，退化为环回默认构造函数
                            _capture = new WasapiLoopbackCapture();
                        }
                    }
                    if (_capture == null && src != null)
                    {
                        _capture = new WasapiLoopbackCapture(src);
                        report.CaptureName = SafeName(src);
                    }
                    else if (_capture is WasapiLoopbackCapture && report.CaptureName.Length == 0)
                    {
                        // 与界面第 0 项文案保持一致：用户看到的采集源名字应当能对上下拉框里的那一行。
                        report.CaptureName = "桌面音频（跟随系统默认播放设备）";
                    }

                    report.Format = _capture.WaveFormat;
                    _format = report.Format;
                }
                catch (Exception ex)
                {
                    SafeDisposeCapture();
                    report.FatalError = "采集初始化失败：" + ex.Message;
                    return report;
                }
                finally
                {
                    // WasapiLoopbackCapture 不接管传入的 MMDevice（tests\ownprobe 实测：释放后设备仍可访问），
                    // 构造完就可以归还。不归还的话每次启动都会漏一个 COM 引用，只能等 GC 才回收。
                    if (ownSrc && src != null) { try { src.Dispose(); } catch { } }
                }

                // ── 2. 逐个建立输出（失败隔离）──
                WaveFormat wf = report.Format;
                if (wf == null)
                {
                    SafeDisposeCapture();
                    report.FatalError = "采集格式不可用";
                    return report;
                }

                int bufferMs = Math.Max(latencyMs * 3, 120);
                int bufferBytes = wf.AverageBytesPerSecond / 1000 * bufferMs;
                // 至少容纳 4 个完整块，避免极小延迟下缓冲区过小导致持续溢出
                if (bufferBytes < wf.AverageBytesPerSecond / 4) { bufferBytes = wf.AverageBytesPerSecond / 4; }

                foreach (MMDevice dev in targets)
                {
                    if (dev == null) { continue; }
                    string name = SafeName(dev);
                    WasapiOut wo = null;
                    BufferedWaveProvider buf = null;
                    try
                    {
                        wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
                        // 订阅断线：设备中途被拔掉时 WASAPI 会让输出线程带异常退出
                        wo.PlaybackStopped += OnPlaybackStopped;
                        buf = new BufferedWaveProvider(wf)
                        {
                            DiscardOnBufferOverflow = true,
                            ReadFully = true,
                            BufferDuration = TimeSpan.FromMilliseconds(bufferMs)
                        };
                        // BufferLength 需显式对齐块，避免非整块边界
                        buf.BufferLength = AlignUp(bufferBytes, Math.Max(1, wf.BlockAlign));

                        // 关键：AudioMeterInformation 只在启停时取一次并缓存。
                        // 每次访问 MMDevice.AudioMeterInformation 都会新建 COM 对象，
                        // 在 UI 定时器里高频读取会造成句柄缓慢泄漏。
                        AudioMeterInformation meter = null;
                        try { meter = dev.AudioMeterInformation; } catch { meter = null; }

                        wo.Init(buf);
                        wo.Play();

                        _outs.Add(wo);
                        _bufs.Add(buf);
                        _meterDevices.Add(dev);
                        _meters.Add(meter);
                        _outNames.Add(name);
                        // 同时记录设备 ID：同名设备（同型号双显示器/USB 声卡）靠 ID 才能区分
                        _outIds.Add(SafeId(dev));
                        // 分路音量：默认满音量（100 = 不缩放，逐字节原样）
                        _gains.Add(100);
                        // 分路缓冲预分配，采集回调里不再 new
                        _laneBuffers.Add(new byte[Math.Max(wf.AverageBytesPerSecond / 10, 16384)]);
                        // 供 OnPlaybackStopped 反查设备名（该回调运行在播放线程，不能加 _gate）
                        _outNameMap[wo] = name;
                        report.Attached.Add(name);
                    }
                    catch (Exception ex)
                    {
                        // 关键修复：单设备失败不影响其它设备
                        try { if (wo != null) { wo.Stop(); wo.Dispose(); } } catch { }
                        report.Failed.Add(name + "（" + Short(ex.Message) + "）");
                    }
                }

                if (_outs.Count == 0)
                {
                    SafeDisposeCapture();
                    report.FatalError = "没有任何输出设备可用";
                    return report;
                }

                // ── 3. 启动采集 ──
                try
                {
                    _droppedBytes = 0;
                    _captureOverflows = 0;
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _capture.StartRecording();
                    _running = true;
                    report.Started = true;
                }
                catch (Exception ex)
                {
                    report.FatalError = "采集启动失败：" + ex.Message;
                    StopInternal();
                }
            }
            return report;
        }

        static int AlignUp(int value, int align)
        {
            if (align <= 1) { return value; }
            int rem = value % align;
            return rem == 0 ? value : value + (align - rem);
        }

        static string SafeName(MMDevice d)
        {
            try { return d.FriendlyName; } catch { return "(未知设备)"; }
        }

        /// <summary>安全读取端点 ID（用于区分同名设备）。</summary>
        static string SafeId(MMDevice d)
        {
            try { return d.ID; } catch { return string.Empty; }
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) { return "未知错误"; }
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 46 ? s.Substring(0, 46) + "…" : s;
        }

        /// <summary>
        /// 采集线程回调。
        /// 严格遵守：只做内存拷贝，不碰任何 UI，不做可抛异常的复杂逻辑。
        /// </summary>
        void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_running) { return; }
            if (e == null || e.Buffer == null || e.BytesRecorded <= 0) { return; }

            BufferedWaveProvider[] bufs;
            byte[][] lanes;
            int[] gains;
            WaveFormat wf;
            lock (_gate)
            {
                if (_bufs.Count == 0) { return; }
                bufs = _bufs.ToArray();
                lanes = _laneBuffers.ToArray();
                gains = _gains.ToArray();
                wf = _format;
            }

            for (int i = 0; i < bufs.Length; i++)
            {
                try
                {
                    BufferedWaveProvider b = bufs[i];
                    int g = (gains != null && i < gains.Length) ? gains[i] : 100;

                    // 满音量是绝大多数情况：直接写原始字节，不做任何拷贝与舍入，
                    // 保证默认状态下音频流与改动前逐字节相同。
                    byte[] data;
                    int count = e.BytesRecorded;
                    if (g >= 100 || wf == null || wf.Encoding == WaveFormatEncoding.Unknown)
                    {
                        data = e.Buffer;
                    }
                    else
                    {
                        // 分路：复制一份再缩放。lanes 里的缓冲是预分配的，
                        // 容量不足（采集卡突发大块）时才扩容，稳态不发生分配。
                        byte[] lane = (lanes != null && i < lanes.Length) ? lanes[i] : null;
                        if (lane == null || lane.Length < count)
                        {
                            lane = new byte[Math.Max(count, 16384)];
                            lock (_gate) { if (i < _laneBuffers.Count) { _laneBuffers[i] = lane; } }
                        }
                        Buffer.BlockCopy(e.Buffer, 0, lane, 0, count);
                        PcmGain.Apply(lane, 0, count, wf, PcmGain.GainFromPercent(g));
                        data = lane;
                    }

                    // 溢出检测：写入前若剩余空间不足，说明即将丢音
                    if (b.BufferLength - b.BufferedBytes < count)
                    {
                        Interlocked.Add(ref _droppedBytes, count);
                    }
                    b.AddSamples(data, 0, count);
                }
                catch
                {
                    // 单个输出通道异常不应终止采集
                    Interlocked.Increment(ref _captureOverflows);
                }
            }
        }

        void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            // 采集线程自己停了（通常是设备被拔出或被独占）
            string reason = "音频采集中断，请检查采集源";
            if (e != null && e.Exception != null)
            {
                reason = "采集已中断：" + Short(e.Exception.Message);
                RaiseFault(reason);
            }
            if (_running)
            {
                _running = false;
                // 把具体原因一并广播出去，避免 UI 只能显示泛化文案
                RaiseStopped(reason);
            }
        }

        public event Action<string> Fault;

        /// <summary>采集线程意外停止。参数为中断原因，可直接显示给用户。</summary>
        public event Action<string> Stopped;

        /// <summary>某个输出端点在播放中途断开（拔线/禁用/被独占）。参数为设备名。</summary>
        public event Action<string> OutputLost;

        void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // 正常 Stop() 时我们已先退订，不会走到这里；
            // 能走到这里说明是 WASAPI 自己让输出线程退出的（设备被拔掉/被禁用）。
            if (!_running) { return; }
            string name = "(未知设备)";
            try
            {
                var wo = sender as WasapiOut;
                string mapped;
                if (wo != null && _outNameMap.TryGetValue(wo, out mapped) && !string.IsNullOrEmpty(mapped))
                {
                    name = mapped;
                }
            }
            catch { }
            RaiseOutputLost(name);
        }

        void RaiseOutputLost(string name)
        {
            var h = OutputLost;
            if (h != null) { h(name); }
        }

        void RaiseFault(string msg)
        {
            var h = Fault;
            if (h != null) { h(msg); }
        }

        void RaiseStopped(string reason)
        {
            var h = Stopped;
            if (h != null) { h(reason); }
        }

        /// <summary>停止并释放所有资源。可重复调用。</summary>
        public void Stop()
        {
            lock (_gate) { StopInternal(); }
        }

        void StopInternal()
        {
            _running = false;

            if (_capture != null)
            {
                try { _capture.DataAvailable -= OnDataAvailable; } catch { }
                try { _capture.RecordingStopped -= OnRecordingStopped; } catch { }
                try
                {
                    if (_capture.CaptureState == CaptureState.Capturing || _capture.CaptureState == CaptureState.Starting)
                    {
                        _capture.StopRecording();
                    }
                }
                catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }

            for (int i = 0; i < _outs.Count; i++)
            {
                // 先退订再停止：正常停止不应触发「输出断开」提示
                try { _outs[i].PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { _outs[i].Stop(); } catch { }
            }
            for (int i = 0; i < _outs.Count; i++)
            {
                try { _outs[i].Dispose(); } catch { }
            }
            for (int i = 0; i < _bufs.Count; i++)
            {
                try { _bufs[i].ClearBuffer(); } catch { }
            }

            _outs.Clear();
            _bufs.Clear();
            _meters.Clear();
            _meterDevices.Clear();
            _outNames.Clear();
            _outIds.Clear();
            _gains.Clear();
            _laneBuffers.Clear();
            _outNameMap.Clear();
            _format = null;
        }

        void SafeDisposeCapture()
        {
            if (_capture != null)
            {
                try { _capture.DataAvailable -= OnDataAvailable; } catch { }
                try { _capture.RecordingStopped -= OnRecordingStopped; } catch { }
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
        }

        /// <summary>
        /// 读取各输出通道的实时峰值（0..1）。由 UI 定时器调用。
        /// 注意：AudioMeterInformation 是该端点的「会话峰值」，反映真实输出电平。
        /// 使用启动时缓存的 AudioMeterInformation 实例，避免高频访问产生 COM 句柄堆积。
        /// </summary>
        public float[] ReadLevels()
        {
            AudioMeterInformation[] meters;
            lock (_gate) { meters = _meters.ToArray(); }
            var result = new float[meters.Length];
            for (int i = 0; i < meters.Length; i++)
            {
                AudioMeterInformation m = meters[i];
                if (m == null) { result[i] = 0f; continue; }
                try { result[i] = m.MasterPeakValue; }
                catch { result[i] = 0f; }
            }
            return result;
        }

        public List<string> OutputNames { get { lock (_gate) { return new List<string>(_outNames); } } }

        /// <summary>当前已连接输出端点的设备 ID 列表，与 OutputNames 一一对应。</summary>
        public List<string> OutputIds { get { lock (_gate) { return new List<string>(_outIds); } } }

        // ═════════════════════════ 分路音量 ═════════════════════════

        /// <summary>
        /// 设置某一路输出的音量（0..100）。返回 true 表示已生效。
        ///
        /// 按设备 ID 定位而不是序号：设备热插拔后序号会变，
        /// 用 ID 才能保证「调的是那台设备」。
        ///
        /// 运行中调用是安全的：采集线程每帧都会重新快照 _gains，
        /// 这里只改一个 int，不需要停流。
        /// </summary>
        public bool SetVolume(string deviceId, int percent)
        {
            if (percent < 0) { percent = 0; }
            if (percent > 100) { percent = 100; }
            lock (_gate)
            {
                int idx = IndexOfId(deviceId);
                if (idx < 0) { return false; }
                _gains[idx] = percent;
                return true;
            }
        }

        /// <summary>读取某一路输出的音量（0..100）；找不到返回 -1。</summary>
        public int GetVolume(string deviceId)
        {
            lock (_gate)
            {
                int idx = IndexOfId(deviceId);
                if (idx < 0) { return -1; }
                return _gains[idx];
            }
        }

        /// <summary>批量读取当前所有输出的音量，与 OutputIds 同序。</summary>
        public List<int> OutputVolumes { get { lock (_gate) { return new List<int>(_gains); } } }

        int IndexOfId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) { return -1; }
            for (int i = 0; i < _outIds.Count; i++)
            {
                if (string.Equals(_outIds[i], deviceId, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        public void Dispose()
        {
            Stop();
            if (_enumerator != null)
            {
                try { _enumerator.Dispose(); } catch { }
                _enumerator = null;
            }
        }
    }
}
