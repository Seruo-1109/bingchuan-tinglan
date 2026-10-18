using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        static readonly string LogPath = Path.Combine(Settings.DirPath, "crash.log");

        [STAThread]
        static void Main(string[] args)
        {
            // ── GUI 子系统程序默认没有控制台，测试入口需要 attach 才能回显 ──
            try { AttachConsole(-1); } catch { }

            // ── 开发期自检入口（不影响正常启动）──
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                Environment.Exit(SelfTest.Run(dir));
            }

            if (args != null && args.Length > 0 && args[0] == "--leakprobe")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                Environment.Exit(LeakProbe.Run(dir));
            }

            if (args != null && args.Length > 0 && args[0] == "--shot")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                int w = 0, h = 0;
                if (args.Length > 2) { int.TryParse(args[2], out w); }
                if (args.Length > 3) { int.TryParse(args[3], out h); }
                // 截图会把窗口移到屏幕外并全选设备，必须隔离用户真实配置
                TestGuard.Begin();
                int shotRc;
                try { shotRc = Shot.Run(dir, w, h); }
                finally { TestGuard.End(); }
                Environment.Exit(shotRc);
            }

            if (args != null && args.Length > 0 && args[0] == "--paintprobe")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                int rounds = 200;
                if (args.Length > 2) { int.TryParse(args[2], out rounds); }
                if (rounds < 20) { rounds = 20; }
                int prc;
                // 探针会创建真实窗口并调用 SetAllToggles（会写配置），必须隔离用户真实配置
                TestGuard.Begin();
                try { prc = PaintProbe.Run(dir, rounds); }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "paintprobe_report.txt"),
                            "重绘探针自身异常:\r\n" + ex, Encoding.UTF8);
                    }
                    catch { }
                    prc = 99;
                }
                finally { TestGuard.End(); }
                Environment.Exit(prc);
            }

            if (args != null && args.Length > 0 && args[0] == "--stress")
            {
                string dir = args.Length > 1 ? args[1] : ".";
                int rounds = 20;
                if (args.Length > 2) { int.TryParse(args[2], out rounds); }
                if (rounds < 1) { rounds = 1; }
                int rc;
                // 压力测试会故意写坏配置来验证归一化，必须隔离用户真实配置
                TestGuard.Begin();
                try { rc = StressTest.Run(dir, rounds); }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "stress_report.txt"),
                            "压力测试自身异常:\r\n" + ex, Encoding.UTF8);
                    }
                    catch { }
                    rc = 99;
                }
                finally { TestGuard.End(); }
                Environment.Exit(rc);
            }

            // ── DPI：必须在任何窗口创建之前 ──
            try { Application.EnableVisualStyles(); } catch { }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }

            // ── 全局异常兜底：绝不允许静默退出 ──
            Application.ThreadException += (s, e) => HandleFatal(e.Exception, "UI 线程异常");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                HandleFatal(e.ExceptionObject as Exception ?? new Exception("未知异常"), "后台线程异常");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // ── 启动形态 ──
            // --autostart：由注册表 Run 项拉起（开机自启），不显示主窗口。
            // --minimized：用户手动加的参数，同样只留托盘。
            //
            // 两者都必须在 Application.Run 之前解析：窗口一旦显示再隐藏就会闪。
            // 注意这里**不要**把参数当成「未知参数 → 回落 GUI」的老路 ——
            // 那条路会让带错参数时静默开出一个窗口挂在那里（见交付说明的已知行为）。
            bool startHidden = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase))
                {
                    startHidden = true;
                }
            }

            // ── 单实例 ──
            // 必须在 GUI 分支里做，且必须在测试入口之后：--selftest / --shot 等
            // 探针常常在已有实例运行时被调用，如果它们也被挡住，整轮回归会集体
            // 静默退出（退出码 0、报告文件不生成，看起来像「跑过了」）。
            //
            // 托盘化之后这条守卫是必需品：关窗不再退出程序，用户会再双击快捷方式。
            if (!SingleInstance.TryAcquire(!startHidden))
            {
                // 已有实例在跑，且已经收到唤起消息。安静退出，不弹窗 ——
                // 用户的本意就是「打开它」，而它已经打开了。
                return;
            }

            try
            {
                Application.Run(new MainForm(startHidden));
            }
            catch (Exception ex)
            {
                HandleFatal(ex, "启动失败");
            }
            finally
            {
                SingleInstance.Release();
            }
        }

        /// <summary>记录崩溃信息并告知用户，而不是无声消失。</summary>
        static void HandleFatal(Exception ex, string stage)
        {
            string text = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n{2}\r\n\r\n",
                DateTime.Now, stage, ex == null ? "(null)" : ex.ToString());
            try
            {
                Directory.CreateDirectory(Settings.DirPath);
                File.AppendAllText(LogPath, text, Encoding.UTF8);
            }
            catch { }

            try
            {
                MessageBox.Show(
                    "程序遇到问题：" + Environment.NewLine + Environment.NewLine +
                    (ex == null ? "未知错误" : ex.Message) + Environment.NewLine + Environment.NewLine +
                    "详细信息已记录到：" + Environment.NewLine + LogPath,
                    "并川听澜",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
