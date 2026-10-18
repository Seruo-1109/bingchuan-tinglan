using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiAudioOutput
{
    /// <summary>
    /// 设备热插拔监听。实现 NAudio 的 IMMNotificationClient。
    ///
    /// 线程模型（关键，勿改）：
    /// 回调来自系统 COM 线程，而控件只能在 UI 线程上碰。这里**不使用 Timer**——
    /// 实测证明 WinForms Timer 在无消息泵的 COM 线程上 Start() 会静默失效
    /// （Tick 永不触发），在有消息泵的线程上 Start() 又会让 Tick 跑在错误的线程上。
    ///
    /// 改用「置脏标志 + UI 线程轮询消费」：
    ///   - 回调只做一件事：Interlocked.Exchange(ref _dirty, 1)（无锁、无分配、不会抛）
    ///   - UI 线程按固定间隔调用 ConsumeIfDue()，用 Environment.TickCount 做 500ms 防抖
    /// 两个线程之间只有 int 字段，天然线程安全。
    /// </summary>
    internal sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        /// <summary>防抖窗口（毫秒）：最后一次设备变化后静默这么久才通知 UI。</summary>
        const int DebounceMs = 500;

        readonly MMDeviceEnumerator _enumerator;
        MMDeviceEnumerator _registered;
        int _dirty;                 // 0 = 干净，1 = 有变化待消费（跨线程共享）
        int _lastChangeTick;        // 最后一次变化的 TickCount（跨线程共享）
        bool _disposed;

        /// <summary>设备列表需要刷新时触发（**UI 线程**）。</summary>
        public event Action Changed;

        public DeviceWatcher(MMDeviceEnumerator enumerator)
        {
            _enumerator = enumerator;
        }

        public void Start()
        {
            if (_registered != null) { return; }
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
                _registered = _enumerator;
            }
            catch
            {
                // 注册失败不影响主功能，只是没有自动刷新
                _registered = null;
            }
        }

        /// <summary>
        /// COM 回调线程调用：只置脏标志并记录时间，绝不碰 UI、绝不分配。
        /// </summary>
        void Poke()
        {
            if (_disposed) { return; }
            try
            {
                _lastChangeTick = Environment.TickCount;
                System.Threading.Interlocked.Exchange(ref _dirty, 1);
            }
            catch { }
        }

        /// <summary>
        /// 由 UI 线程定期调用。若在防抖窗口内没有新的变化，则消费脏标志并触发 Changed。
        /// </summary>
        /// <returns>本次是否触发了 Changed。</returns>
        public bool ConsumeIfDue()
        {
            if (_disposed) { return false; }
            if (System.Threading.Interlocked.CompareExchange(ref _dirty, 0, 1) != 1) { return false; }

            // 防抖：距最后一次变化不足 DebounceMs 就重新置脏，下轮再看
            int elapsed = unchecked(Environment.TickCount - _lastChangeTick);
            if (elapsed < DebounceMs)
            {
                System.Threading.Interlocked.Exchange(ref _dirty, 1);
                return false;
            }

            var h = Changed;
            if (h != null)
            {
                try { h(); }
                catch { }
            }
            return true;
        }

        // ── IMMNotificationClient 实现 ──
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { Poke(); }
        public void OnDeviceAdded(string pwstrDeviceId) { Poke(); }
        public void OnDeviceRemoved(string deviceId) { Poke(); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { Poke(); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { Poke(); }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_registered != null)
                {
                    _registered.UnregisterEndpointNotificationCallback(this);
                }
            }
            catch { }
            _registered = null;
        }
    }
}
