using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace MultiAudioOutput
{
    /// <summary>
    /// 开机自启：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
    ///
    /// 为什么用注册表 Run 而不是启动文件夹快捷方式或计划任务：
    ///   · Run 项是 HKCU，**不需要管理员权限**，也不需要提权提示；
    ///   · 启动文件夹的 .lnk 要处理「快捷方式被安全软件清理」「路径含中文」；
    ///   · 计划任务需要 Task Scheduler COM 接口，代码量大且失败模式更隐蔽。
    /// Run 项唯一的缺点是「登录后才启动、非提权」，对音频转发程序恰好合适。
    ///
    /// 值里必须带 --autostart 参数：程序据此决定「启动后不弹窗、直接进托盘」，
    /// 否则每次开机都会有一个窗口糊在用户脸上。
    /// </summary>
    internal static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MultiAudioOutput";

        /// <summary>开机自启时附加的命令行参数。</summary>
        public const string LaunchArg = "--autostart";

        /// <summary>当前可执行文件的完整路径。单文件部署时就是 exe 自己。</summary>
        public static string ExePath
        {
            get
            {
                try
                {
                    // 优先用主模块路径：Assembly.Location 在单文件/影子复制场景下可能是 dll 路径。
                    string p = Process.GetCurrentProcess().MainModule.FileName;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) { return p; }
                }
                catch { }
                try
                {
                    string p = Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(p)) { return p; }
                }
                catch { }
                return string.Empty;
            }
        }

        /// <summary>期望写入注册表的那条命令行（带引号，路径含空格也不会断）。</summary>
        public static string ExpectedCommand
        {
            get
            {
                string exe = ExePath;
                if (string.IsNullOrEmpty(exe)) { return string.Empty; }
                return "\"" + exe + "\" " + LaunchArg;
            }
        }

        /// <summary>读当前登记的启动命令；没登记返回 null。异常一律当作「没登记」。</summary>
        public static string ReadCommand()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) { return null; }
                    object v = k.GetValue(ValueName, null);
                    return v as string;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 是否已启用**且指向当前这个 exe**。
        ///
        /// 只判断「值存在」是不够的：用户把程序从 D:\Software 挪到别处后，注册表里还留着
        /// 旧路径，登录时 Windows 找不到文件，表现为「自启开关明明开着却不启动」。
        /// 路径不一致时这里返回 false，UI 会显示为关闭状态，用户重新勾选即可修复。
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                string cmd = ReadCommand();
                if (string.IsNullOrEmpty(cmd)) { return false; }
                string want = ExpectedCommand;
                if (string.IsNullOrEmpty(want)) { return true; } // 拿不到自身路径时按「已登记」处理
                return string.Equals(cmd.Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>开启自启；返回是否成功，失败原因写进 error。</summary>
        public static bool Enable(out string error)
        {
            error = null;
            string cmd = ExpectedCommand;
            if (string.IsNullOrEmpty(cmd))
            {
                error = "无法确定程序路径";
                return false;
            }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) { error = "无法打开注册表启动项"; return false; }
                    k.SetValue(ValueName, cmd, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                // 组策略禁止写入 Run 项时会走到这里（UnauthorizedAccessException）。
                // 不抛给调用方：自启只是便利功能，失败不应该让主流程中断。
                error = ex.Message;
                return false;
            }
        }

        /// <summary>关闭自启。本来就没登记也算成功。</summary>
        public static bool Disable(out string error)
        {
            error = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) { return true; }
                    k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool SetEnabled(bool on, out string error)
        {
            return on ? Enable(out error) : Disable(out error);
        }
    }
}
