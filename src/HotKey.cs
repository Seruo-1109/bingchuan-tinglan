using System;
using System.Runtime.InteropServices;

namespace MultiAudioOutput
{
    /// <summary>
    /// 全局热键：字符串 ⇄ (修饰键, 虚拟键) 的解析、格式化与注册/注销。
    ///
    /// 为什么把解析单独抽成一个类型：热键写在 settings.json 里，用户手改一个错字
    /// （"Ctrl+Mmm"）绝不能让程序启动失败，也不能静默地「按了没反应」。TryParse 失败
    /// 一律由调用方回落到默认值，并在状态栏给一行提示 —— 配置写错必须看得见。
    ///
    /// 为什么只接受 Ctrl / Alt / Win 之一：只有 Shift 的组合（或干脆没有修饰键）
    /// 等于在全系统范围内吃掉一个普通按键，任何程序里打字都会被它截走。
    /// </summary>
    internal static class HotKey
    {
        /// <summary>WM_HOTKEY 消息号，由 MainForm.WndProc 分派。</summary>
        public const int WmHotKey = 0x0312;

        /// <summary>注册用的热键 id。整个进程只有一个热键，固定值即可。</summary>
        public const int Id = 0xA117;

        const uint ModAlt = 0x0001;
        const uint ModControl = 0x0002;
        const uint ModShift = 0x0004;
        const uint ModWin = 0x0008;

        /// <summary>
        /// MOD_NOREPEAT：按住不放时只触发一次。
        /// 不加这个标志，用户按住 M 键不放，系统会按键盘重复率（约每秒 30 次）
        /// 反复投递 WM_HOTKEY —— 引擎会被开关三十次，每次都要重新枚举设备。
        /// </summary>
        const uint ModNoRepeat = 0x4000;

        /// <summary>注册失败时 GetLastError 的取值：该热键已被别的程序占用。</summary>
        public const int ErrorHotKeyAlreadyRegistered = 1409;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const string DefaultText = "Ctrl+Alt+M";

        /// <summary>一个已解析的热键。Modifiers 已含 MOD_NOREPEAT。</summary>
        internal sealed class Binding
        {
            public uint Modifiers;
            public uint Key;
            public string Text;
            public override string ToString() { return Text; }
        }

        /// <summary>解析配置字符串；失败返回 null（调用方回落默认值）。</summary>
        public static Binding Parse(string text)
        {
            Binding b;
            return TryParse(text, out b) ? b : null;
        }

        /// <summary>
        /// 解析 "Ctrl+Alt+M" 这类写法。
        ///
        /// 规则：用 '+' 分隔，最后一段是主键，前面若干段是修饰键。
        /// 主键只接受 A-Z / 0-9 / F1-F24 —— 这些在任何键盘布局下都有确定的虚拟键码，
        /// 而标点符号（如 "Ctrl+,"）在不同布局上会落到不同键上，存进配置里换台机器就错位。
        /// </summary>
        public static bool TryParse(string text, out Binding binding)
        {
            binding = null;
            if (string.IsNullOrEmpty(text)) { return false; }

            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            // 至少要「一个修饰键 + 一个主键」，否则就是裸键全局热键
            if (parts.Length < 2) { return false; }

            uint mods = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") { mods |= ModControl; }
                else if (m == "alt") { mods |= ModAlt; }
                else if (m == "shift") { mods |= ModShift; }
                else if (m == "win" || m == "windows") { mods |= ModWin; }
                else { return false; }
            }

            // 必须含 Ctrl / Alt / Win 之一（Shift 单独存在不算修饰，见类注释）
            if ((mods & (ModControl | ModAlt | ModWin)) == 0) { return false; }

            uint vk;
            if (!TryParseKey(parts[parts.Length - 1].Trim().ToUpperInvariant(), out vk)) { return false; }

            binding = new Binding { Modifiers = mods | ModNoRepeat, Key = vk, Text = Format(mods, vk) };
            return true;
        }

        static bool TryParseKey(string token, out uint vk)
        {
            vk = 0;
            if (string.IsNullOrEmpty(token)) { return false; }

            if (token.Length == 1)
            {
                char c = token[0];
                if (c >= 'A' && c <= 'Z') { vk = (uint)('A' + (c - 'A')); return true; }
                if (c >= '0' && c <= '9') { vk = (uint)('0' + (c - '0')); return true; }
                return false;
            }

            if (token[0] == 'F')
            {
                int n;
                if (int.TryParse(token.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    vk = (uint)(0x70 + (n - 1)); // VK_F1 = 0x70
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 从一次真实按键事件构造 Binding —— 设置面板的「录制」用它。
        ///
        /// 为什么不复用 TryParse：录制拿到的是虚拟键码（Keys 枚举），不是字符串。
        /// 若先转成字符串再解析，就得为每个键维护一张 vk→名字 的表，而且
        /// **大小写/别名/数字小键盘** 都会成为新的解析失败来源。直接收 vk 最稳。
        ///
        /// 三条拒绝规则与 TryParse 完全一致（用户从两条路进来必须看到同一套规矩）：
        ///   1) 必须是「修饰键 + 主键」，不能只有主键（裸键全局热键会吃掉普通输入）；
        ///   2) 修饰键必须含 Ctrl / Alt / Win 之一（只有 Shift 不算）；
        ///   3) 主键必须是 A-Z / 0-9 / F1-F24，且不能本身又是修饰键。
        /// </summary>
        public static bool TryFromKeyEvent(bool ctrl, bool alt, bool shift, bool win, int vk, out Binding binding, out string error)
        {
            binding = null;
            error = null;

            uint mods = 0;
            if (ctrl) { mods |= ModControl; }
            if (alt) { mods |= ModAlt; }
            if (shift) { mods |= ModShift; }
            if (win) { mods |= ModWin; }

            if (mods == 0)
            {
                error = "请按住 Ctrl / Alt / Win 再按一个键";
                return false;
            }
            if ((mods & (ModControl | ModAlt | ModWin)) == 0)
            {
                error = "只有 Shift 不行，会和普通按键冲突";
                return false;
            }

            if (!IsSupportedKey(vk))
            {
                error = "只支持字母 A-Z、数字 0-9、功能键 F1-F24";
                return false;
            }

            uint key = (uint)vk;
            binding = new Binding { Modifiers = mods | ModNoRepeat, Key = key, Text = Format(mods, key) };
            return true;
        }

        /// <summary>录制可用作主键的虚拟键码：A-Z / 0-9 / F1-F24。</summary>
        public static bool IsSupportedKey(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) { return true; }   // A-Z
            if (vk >= 0x30 && vk <= 0x39) { return true; }   // 0-9
            if (vk >= 0x70 && vk <= 0x87) { return true; }   // F1-F24
            return false;
        }

        /// <summary>规范化成 "Ctrl+Alt+M" 形式（顺序固定，便于比较与显示）。</summary>
        public static string Format(uint mods, uint vk)
        {
            var sb = new System.Text.StringBuilder();
            if ((mods & ModControl) != 0) { sb.Append("Ctrl+"); }
            if ((mods & ModAlt) != 0) { sb.Append("Alt+"); }
            if ((mods & ModShift) != 0) { sb.Append("Shift+"); }
            if ((mods & ModWin) != 0) { sb.Append("Win+"); }

            if (vk >= 0x41 && vk <= 0x5A) { sb.Append((char)vk); }
            else if (vk >= 0x30 && vk <= 0x39) { sb.Append((char)vk); }
            else if (vk >= 0x70 && vk <= 0x87) { sb.Append('F').Append(vk - 0x70 + 1); }
            else { sb.Append("0x").Append(vk.ToString("X2")); }
            return sb.ToString();
        }

        /// <summary>
        /// 注册热键。失败时通过 error 返回可读原因（冲突 / 无窗口句柄 / 系统拒绝）。
        ///
        /// 注意 RegisterHotKey 的失败不会抛异常，只返回 false，必须读 GetLastError 才知道原因；
        /// 而 .NET 里 Marshal.GetLastWin32Error 只有在 DllImport 标了 SetLastError=true 时才准。
        /// </summary>
        public static bool Register(IntPtr hwnd, Binding binding, out string error)
        {
            error = null;
            if (hwnd == IntPtr.Zero) { error = "窗口尚未创建"; return false; }
            if (binding == null) { error = "热键为空"; return false; }

            try
            {
                if (RegisterHotKey(hwnd, Id, binding.Modifiers, binding.Key)) { return true; }
                int code = Marshal.GetLastWin32Error();
                error = (code == ErrorHotKeyAlreadyRegistered)
                    ? ("已被其它程序占用（" + binding.Text + "）")
                    : ("系统拒绝注册（错误码 " + code + "）");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>注销热键。窗口销毁时系统也会自动注销，这里显式做是为了支持「运行中改热键」。</summary>
        public static void Unregister(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) { return; }
            try { UnregisterHotKey(hwnd, Id); } catch { }
        }
    }
}
