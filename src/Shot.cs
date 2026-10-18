using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace MultiAudioOutput
{
    /// <summary>
    /// 界面截图工具：把 MainForm 渲染成 PNG，用于离线审查视觉效果。
    /// 用法: --shot &lt;输出目录&gt; [宽] [高]
    ///
    /// 输出 4 张：ui_idle / ui_checked / ui_hover / ui_running。
    /// 所有反射调用与失败都写入 shot_log.txt，避免"截图看起来一样但其实是没点到"这类静默失败。
    /// </summary>
    internal static class Shot
    {
        static string _dir;
        static readonly StringBuilder _log = new StringBuilder();

        internal static int Run(string outDir, int w, int h)
        {
            _dir = outDir;
            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm f = new MainForm();
            if (w > 0 && h > 0) { f.Size = new Size(w, h); }

            // 移到屏幕外显示，保证控件完成真实布局与首次绘制
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            Pump(900);

            Save(f, "ui_idle.png");

            // ① 全选设备：展示开关选中态、电平条、主按钮启用态
            Call(f, "SetAllToggles", new object[] { true });
            Pump(400);
            Save(f, "ui_checked.png");

            // ② 悬停态：离线无法投递真实鼠标消息，直接置位动画字段
            SetField(f, "_startBtn", "_hover", true);
            SetField(f, "_startBtn", "_anim", 1f);
            RefreshControl(f, "_startBtn");
            Pump(250);
            Save(f, "ui_hover.png");
            SetField(f, "_startBtn", "_hover", false);
            SetField(f, "_startBtn", "_anim", 0f);
            RefreshControl(f, "_startBtn");

            // ③ 运行态：真实启动引擎。先确认当前没有音频在播放，避免夜间打扰。
            float peak = CurrentPeak();
            _log.AppendLine("启动前峰值 = " + peak.ToString("F4"));
            if (peak < 0.05f)
            {
                Call(f, "StartEngine", null);
                Pump(1400);
                Save(f, "ui_running.png");
                Call(f, "StopEngine", new object[] { "已停止" });
                Pump(300);
            }
            else
            {
                _log.AppendLine("检测到正在播放音频，跳过 ui_running.png（避免打扰）");
            }

            // ④ 出错态：标题栏头像切到 Calm + 红环（阶段3 的第三个状态）。
            //
            // 这里刻意走**真实的故障路径**而不是直接调 SetAvatarMood：
            // Fault 事件之后引擎还会广播 Stopped，而 Stopped → StopEngine() 会把
            // 界面整体复位。早先的版本在 StopEngine 里无条件恢复 Smile，导致
            // 红环只存在一个消息循环的瞬间 —— ui_error.png 与 ui_idle.png 像素
            // 完全一样（本截图模式正是这样发现的）。现在由 _engineFaulted 守住。
            //
            // 为了让 StopEngine 真的走完复位分支（它开头有 !_running 的早退），
            // 先把 _running 置真，模拟「引擎正在跑然后崩了」的那一刻。
            // 顺序严格照抄真实事件链：Fault（置标志 + 换表情）→ Stopped（复位其余）。
            SetMainField(f, "_engineFaulted", true);
            Call(f, "SetAvatarMood", new object[] { AvatarArt.Calm, (Color?)Theme.Danger });
            SetMainField(f, "_running", true);
            Call(f, "StopEngine", new object[] { "采集已中断：设备已拔出" });
            Pump(250);
            Save(f, "ui_error.png");

            // 复原成就绪态，避免影响后续（如果有）的检查
            SetMainField(f, "_engineFaulted", false);
            Call(f, "SetAvatarMood", new object[] { AvatarArt.Smile, (Color?)null });
            Pump(120);

            _log.AppendLine("完成");
            try { File.WriteAllText(Path.Combine(_dir, "shot_log.txt"), _log.ToString(), new UTF8Encoding(false)); }
            catch { }

            try { f.Hide(); } catch { }
            // 注意：这里不能调用 Environment.Exit —— 那会跳过 Program.Main 里的
            // TestGuard.End()，导致用户的真实 settings.json 永远无法还原。
            return 0;
        }

        // ───────── 工具 ─────────

        static void Pump(int ms)
        {
            const int step = 50;
            for (int t = 0; t < ms; t += step)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(step);
            }
        }

        /// <summary>调用 MainForm 的私有方法；失败写入日志而不是静默吞掉。</summary>
        static void Call(MainForm f, string method, object[] args)
        {
            try
            {
                MethodInfo mi = typeof(MainForm).GetMethod(method,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi == null) { _log.AppendLine("[MISS] 方法不存在: " + method); return; }
                mi.Invoke(f, args);
                _log.AppendLine("[OK] " + method + "()");
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                _log.AppendLine("[FAIL] " + method + "() -> " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        /// <summary>给 _ownerField 指向的控件的 _field 赋值。</summary>
        static void SetField(MainForm f, string ownerField, string field, object value)
        {
            try
            {
                FieldInfo of = typeof(MainForm).GetField(ownerField,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (of == null) { _log.AppendLine("[MISS] 字段不存在: " + ownerField); return; }
                object owner = of.GetValue(f);
                if (owner == null) { _log.AppendLine("[MISS] 字段为 null: " + ownerField); return; }

                FieldInfo ff = owner.GetType().GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (ff == null) { _log.AppendLine("[MISS] 子字段不存在: " + ownerField + "." + field); return; }
                ff.SetValue(owner, value);
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] " + ownerField + "." + field + " -> " + ex.Message);
            }
        }

        /// <summary>直接给 MainForm 自己的私有字段赋值（不经过子控件）。</summary>
        static void SetMainField(MainForm f, string field, object value)
        {
            try
            {
                FieldInfo ff = typeof(MainForm).GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (ff == null) { _log.AppendLine("[MISS] MainForm." + field + " 不存在"); return; }
                ff.SetValue(f, value);
                _log.AppendLine("[OK] set MainForm." + field + " = " + value);
            }
            catch (Exception ex)
            {
                _log.AppendLine("[FAIL] MainForm." + field + " -> " + ex.Message);
            }
        }

        static void RefreshControl(MainForm f, string ownerField)
        {
            try
            {
                FieldInfo of = typeof(MainForm).GetField(ownerField,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (of == null) { return; }
                Control c = of.GetValue(f) as Control;
                if (c != null) { c.Invalidate(); }
            }
            catch { }
        }

        /// <summary>读默认输出设备的峰值，用于判断此刻是否有声音在放。</summary>
        static float CurrentPeak()
        {
            var engine = new AudioEngine();
            try
            {
                List<MMDevice> devs = engine.ListOutputDevices();
                try
                {
                    for (int i = 0; i < devs.Count; i++)
                    {
                        try { return devs[i].AudioMeterInformation.MasterPeakValue; }
                        catch { }
                    }
                }
                finally { AudioEngine.ReleaseDevices(devs); }
            }
            catch (Exception ex) { _log.AppendLine("CurrentPeak 失败: " + ex.Message); }
            finally { try { engine.Dispose(); } catch { } }
            return 0f;
        }

        static void Save(Form f, string name)
        {
            string path = Path.Combine(_dir, name);
            try
            {
                f.Refresh();
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                _log.AppendLine("[SAVE] " + name + " " + new FileInfo(path).Length + " B");
            }
            catch (Exception ex)
            {
                _log.AppendLine("[SAVE-FAIL] " + name + " -> " + ex.Message);
            }
        }
    }
}
