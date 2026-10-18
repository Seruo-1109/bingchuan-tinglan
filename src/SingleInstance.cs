using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MultiAudioOutput
{
    /// <summary>
    /// 单实例守卫。
    ///
    /// 为什么托盘化之后必须有它：以前关窗就是退出，用户想再打开只会有一个实例。
    /// 现在关窗只是收进托盘，程序还在后台输出，用户很容易再双击一次快捷方式 ——
    /// 于是第二个实例起来了，两套引擎抢同一批 WASAPI 端点（第二套 Start 会因
    /// 独占失败报错，或者两路同时往同一台设备灌数据），任务栏/托盘出现两个图标，
    /// 两份配置互相覆盖。这类症状用户完全无法自己定位。
    ///
    /// 做法：命名互斥体判重 + 注册窗口消息唤起已有实例。
    /// 用注册消息（RegisterWindowMessage）而不是 WM_COPYDATA，是因为它不需要
    /// 事先知道对方窗口句柄，广播即可送达；而且已收进托盘的主窗口只是 Hide()，
    /// HWND 仍然存在，能正常收到广播。
    /// </summary>
    internal static class SingleInstance
    {
        // Local\ 前缀 = 仅本登录会话。同一台机器上多个用户各开一份是合理的。
        const string DefaultMutexName = @"Local\MultiAudioOutput.SingleInstance";

        /// <summary>
        /// 实际使用的互斥体名。
        ///
        /// 为什么允许环境变量覆盖：回归里有几个探针（restorestate / restoreverify /
        /// windowicon / singleinstance / hotkeyprobe）需要**启动真实的 exe** 再观察它。
        /// 而这些探针经常在「用户已经开着一份程序」时被调用 —— 那时新进程会命中单实例
        /// 守卫，安静 return（退出码 0），探针拿不到窗口句柄，于是报
        /// 「进程已退出，因此无法获得所请求的信息」。
        ///
        /// 这类失败是环境噪声，不是产品缺陷，但也不能靠「先把用户的程序杀掉」来解决：
        /// 用户可能正开着它在输出音频。所以给测试留一个显式的隔离开关。
        ///
        /// 生产路径永远走 DefaultMutexName：正常启动的进程读不到这个环境变量，
        /// 单实例语义与之前逐字节一致。
        /// </summary>
        static string CurrentMutexName
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("MULTIAUDIO_TEST_MUTEX");
                if (!string.IsNullOrEmpty(over)) { return @"Local\" + over; }
                return DefaultMutexName;
            }
        }

        const int HwndBroadcast = 0xFFFF;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        static readonly uint _showMsg = RegisterWindowMessage("MultiAudioOutput.ShowExistingInstance");

        static Mutex _mutex;
        static bool _owned;

        /// <summary>唤起已有实例用的窗口消息号。0 表示注册失败（极罕见），此时忽略。</summary>
        public static int ShowMessage { get { return unchecked((int)_showMsg); } }

        /// <summary>
        /// 尝试成为唯一实例。返回 false 表示已有一个在跑。
        /// </summary>
        /// <param name="signalExisting">
        /// true（用户手动启动）→ 把已有实例叫到前台；
        /// false（开机自启/静默启动）→ 只安静退出。开机自启那次绝不能把
        /// 用户已经收进托盘的窗口弹出来，否则每次登录都会莫名多一个窗口。
        /// </param>
        public static bool TryAcquire(bool signalExisting)
        {
            // 本进程已经拿到了就不要再碰 —— 见下方「不覆盖已持有的句柄」。
            if (_owned) { return false; }

            try
            {
                bool createdNew;
                // 用带初始所有权的构造 + createdNew，而不是 WaitOne(0)：
                // 后者在上一份进程被强杀时会抛 AbandonedMutexException。
                //
                // 注意 initiallyOwned:true 只在「对象是本次新建」时生效。
                // 如果互斥体已存在，这个参数被内核忽略，拿到的 Mutex 对象
                // 并不持有所有权 —— 所以绝不能把它当成「我也持有」存起来。
                Mutex m = new Mutex(true, CurrentMutexName, out createdNew);
                if (createdNew)
                {
                    _mutex = m;
                    _owned = true;
                    return true;
                }

                // 没抢到：立刻丢掉这个句柄。曾经这里直接写 _mutex = m，
                // 把「真正持有所有权的那个对象」的引用覆盖掉了 —— 之后
                // Release() 对不持有的对象调 ReleaseMutex 抛异常被静默吞掉，
                // 而原来那个对象既没释放也没 Dispose，互斥体永远被占住，
                // 程序重启后自己都进不去。singleinstance 探针第 4/6 条抓到的
                // 就是这个（Release 之后再也抢不回来）。
                try { m.Dispose(); } catch { }
            }
            catch
            {
                // 互斥体创建本身失败（权限/资源）时宁可放行，也不要让用户打不开程序。
                return true;
            }

            if (signalExisting) { SignalExisting(); }
            return false;
        }

        /// <summary>把已有实例叫出来。对方没响应就算了，绝不能卡住自己。</summary>
        static void SignalExisting()
        {
            if (_showMsg == 0) { return; }
            try
            {
                // 允许对方抢前台：否则窗口会闪一下任务栏却不真的到最前。
                AllowSetForegroundWindow(-1); // ASFW_ANY
                // 对方可能正处在启动中（窗口还没建好），多试几次覆盖那几百毫秒。
                for (int i = 0; i < 12; i++)
                {
                    PostMessage(new IntPtr(HwndBroadcast), _showMsg, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(120);
                }
            }
            catch { }
        }

        /// <summary>正常退出时调用。进程结束时系统也会释放，这里只是让释放更确定。</summary>
        public static void Release()
        {
            // 只对「确实持有所有权」的那个对象释放。对不持有的互斥体调
            // ReleaseMutex 会抛 ApplicationException，而调用方一律吞异常，
            // 于是所有权永远留在那个对象上、进程内再也抢不回来。
            if (_owned && _mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            if (_mutex != null)
            {
                try { _mutex.Dispose(); } catch { }
            }
            _mutex = null;
            _owned = false;
        }
    }
}
