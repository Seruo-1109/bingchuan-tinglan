using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 界面重绘压力探针。
    ///
    /// 为什么需要它：--stress 是无界面运行的（只驱动音频引擎，从不绘制），
    /// 因此完全覆盖不到 OnPaint 路径。真实的 GDI 泄漏（例如画刷创建后未释放）
    /// 只在界面持续重绘时暴露 —— 无界面压力测试永远测不出来。
    /// 本探针就是补上这个盲区，并顺带覆盖每一种控件与每一种状态分支。
    ///
    /// 用法: --paintprobe &lt;输出目录&gt; [重绘轮数]
    /// </summary>
    internal static class PaintProbe
    {
        [DllImport("user32.dll")]
        static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        // 用于灵敏度自检：创建真正的 GDI 对象（占用 GDI 句柄表），
        // 与 GDI+ 的 SolidBrush/Pen 不同 —— 后者分配在 GDI+ 自己的堆上。
        [DllImport("gdi32.dll")]
        static extern IntPtr CreatePen(int fnPenStyle, int nWidth, int crColor);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        const int GR_GDIOBJECTS = 0;
        const int GR_USEROBJECTS = 1;
        const int PS_SOLID = 0;

        static readonly StringBuilder _log = new StringBuilder();
        static readonly Process _self = Process.GetCurrentProcess();

        static void L(string s) { _log.AppendLine(s); Console.WriteLine(s); }

        static int Gdi() { return GetGuiResources(_self.Handle, GR_GDIOBJECTS); }
        static int User() { return GetGuiResources(_self.Handle, GR_USEROBJECTS); }
        static int Handles() { _self.Refresh(); return _self.HandleCount; }
        static long WsMb() { _self.Refresh(); return _self.WorkingSet64 / (1024L * 1024L); }

        internal static int Run(string outDir, int rounds)
        {
            Directory.CreateDirectory(outDir);
            if (rounds < 20) { rounds = 20; }

            L("=== 界面重绘压力探针 ===");
            L("PID=" + _self.Id + "  64bit=" + Environment.Is64BitProcess);
            L("CLR=" + Environment.Version);
            L("重绘轮数=" + rounds);
            L("基线: GDI=" + Gdi() + "  USER=" + User() + "  句柄=" + Handles() + "  内存=" + WsMb() + "MB");
            L("");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int failures = 0;

            // ══════════ 第 1 节：真实 MainForm 全量重绘 ══════════
            L("── 第 1 节：真实窗口全量重绘 ──");
            MainForm f = null;
            try
            {
                f = new MainForm();
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Pump(700);

                // 勾选全部设备：覆盖开关选中配色、电平条填充、主按钮启用态
                Call(f, "SetAllToggles", new object[] { true });
                Pump(400);

                // 预热：GDI+ 内部会缓存字体/画刷，必须先稳定下来再取基线
                int warm = Math.Max(30, rounds / 4);
                for (int i = 0; i < warm; i++) { RepaintRound(f, i); }
                Settle();

                int g0 = Gdi(), u0 = User(), h0 = Handles();
                long w0 = WsMb();
                L("预热 " + warm + " 轮后基线: GDI=" + g0 + "  USER=" + u0 + "  句柄=" + h0 + "  内存=" + w0 + "MB");

                for (int i = 0; i < rounds; i++) { RepaintRound(f, i); }
                Settle();

                int g1 = Gdi(), u1 = User(), h1 = Handles();
                long w1 = WsMb();
                int dg = g1 - g0, du = u1 - u0, dh = h1 - h0;

                L("重绘 " + rounds + " 轮后:      GDI=" + g1 + "  USER=" + u1 + "  句柄=" + h1 + "  内存=" + w1 + "MB");
                L(string.Format("净增: GDI {0:+#;-#;0}   USER {1:+#;-#;0}   句柄 {2:+#;-#;0}   内存 {3:+#;-#;0}MB",
                    dg, du, dh, w1 - w0));
                L(string.Format("每轮: GDI {0:+0.0000;-0.0000;0}   USER {1:+0.0000;-0.0000;0}",
                    dg / (double)rounds, du / (double)rounds));

                if (dg <= 20 && du <= 20)
                {
                    L("[PASS] 界面重绘无 GDI/USER 泄漏（净增 <= 20）");
                }
                else
                {
                    L("[FAIL] 界面重绘存在 GDI/USER 泄漏 — GDI " + dg + "  USER " + du);
                    failures++;
                }
            }
            catch (Exception ex)
            {
                L("[FAIL] 第 1 节异常: " + ex);
                failures++;
            }
            finally
            {
                if (f != null) { try { f.Hide(); f.Dispose(); } catch { } }
                Settle();
            }

            // ══════════ 第 2 节：每种控件 × 每种状态分支 ══════════
            L("");
            L("── 第 2 节：控件与状态分支覆盖 ──");
            Form host = null;
            try
            {
                host = new Form();
                host.FormBorderStyle = FormBorderStyle.None;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-4000, -4000);
                host.Size = new Size(600, 900);

                var pills = new List<PillButton>();
                Array kinds = Enum.GetValues(typeof(PillButton.Kind));
                int px = 8, py = 8;
                foreach (PillButton.Kind k in kinds)
                {
                    var b = new PillButton();
                    b.Variant = k;
                    b.Text = k.ToString();
                    b.Size = new Size(110, 38);
                    b.Location = new Point(px, py);
                    host.Controls.Add(b);
                    pills.Add(b);
                    px += 118;
                    if (px > 480) { px = 8; py += 46; }

                    // 同一变体的禁用态（走 !Enabled 分支）
                    var bd = new PillButton();
                    bd.Variant = k;
                    bd.Text = k.ToString();
                    bd.Enabled = false;
                    bd.Size = new Size(110, 38);
                    bd.Location = new Point(px, py);
                    host.Controls.Add(bd);
                    pills.Add(bd);
                    px += 118;
                    if (px > 480) { px = 8; py += 46; }

                    // 紧凑态
                    var bc = new PillButton();
                    bc.Variant = k;
                    bc.Compact = true;
                    bc.Text = "紧凑";
                    bc.Size = new Size(70, 30);
                    bc.Location = new Point(px, py);
                    host.Controls.Add(bc);
                    pills.Add(bc);
                    px += 78;
                    if (px > 480) { px = 8; py += 46; }
                }

                py += 56;
                var toggles = new List<ToggleSwitch>();
                for (int i = 0; i < 8; i++)
                {
                    var t = new ToggleSwitch();
                    t.Location = new Point(8 + i * 60, py);
                    t.SetCheckedSilent((i % 2) == 0);
                    host.Controls.Add(t);
                    toggles.Add(t);
                }

                py += 44;
                var meters = new List<LevelMeter>();
                for (int i = 0; i < 8; i++)
                {
                    var m = new LevelMeter();
                    m.Thin = (i % 2) == 0;
                    m.Location = new Point(8, py + i * 16);
                    m.Size = new Size(280, m.Thin ? 4 : 8);
                    host.Controls.Add(m);
                    meters.Add(m);
                }

                py += 140;
                var slider = new SlimSlider();
                slider.Minimum = 5;
                slider.Maximum = 300;
                slider.Location = new Point(8, py);
                slider.Size = new Size(280, 26);
                host.Controls.Add(slider);

                var combo = new SlimCombo();
                combo.Location = new Point(300, py);
                combo.Size = new Size(200, 34);
                combo.SetItems(new string[] { "桌面音频（跟随系统默认播放设备）", "扬声器 (Realtek)", "耳机 (USB Audio)" }, 0);
                host.Controls.Add(combo);

                py += 44;
                var cards = new List<Card>();
                for (int i = 0; i < 4; i++)
                {
                    var c = new Card();
                    c.FillColor = Theme.Surface;
                    c.BorderColor = Theme.Line;
                    c.Radius = 12f;
                    c.DrawBorder = (i % 2) == 0;
                    c.Location = new Point(8 + i * 140, py);
                    c.Size = new Size(130, 40);
                    host.Controls.Add(c);
                    cards.Add(c);
                }

                var dots = new List<Dot>();
                for (int i = 0; i < 4; i++)
                {
                    var d = new Dot();
                    d.Location = new Point(8 + i * 24, py + 50);
                    d.DotColor = i == 0 ? Theme.Success : Theme.InkFaint;
                    host.Controls.Add(d);
                    dots.Add(d);
                }

                host.Show();
                Pump(400);

                int warm2 = Math.Max(30, rounds / 4);
                for (int i = 0; i < warm2; i++) { RepaintRound(host, i); }
                Settle();

                int g0 = Gdi(), u0 = User();
                L("预热 " + warm2 + " 轮后基线: GDI=" + g0 + "  USER=" + u0);

                for (int i = 0; i < rounds; i++)
                {
                    // 每轮切换状态，确保两条分支都被走到
                    bool on = (i % 2) == 0;
                    for (int k = 0; k < pills.Count; k++)
                    {
                        SetPrivate(pills[k], "_hover", on);
                        SetPrivate(pills[k], "_down", !on && (k % 3) == 0);
                        SetPrivate(pills[k], "_anim", on ? 1f : 0f);
                    }
                    for (int k = 0; k < toggles.Count; k++)
                    {
                        toggles[k].SetCheckedSilent(((k + i) % 2) == 0);
                        SetPrivate(toggles[k], "_hover", on);
                        SetPrivate(toggles[k], "_down", !on);
                    }
                    for (int k = 0; k < meters.Count; k++)
                    {
                        float v = (float)((Math.Sin(i * 0.31 + k) + 1.0) / 2.0);
                        meters[k].Push(v);
                    }
                    slider.SetValueSilent(5 + (i * 7) % 296);
                    SetPrivate(slider, "_hover", on);
                    SetPrivate(slider, "_drag", !on);
                    for (int k = 0; k < cards.Count; k++) { cards[k].DrawBorder = ((k + i) % 2) == 0; }
                    for (int k = 0; k < dots.Count; k++) { dots[k].Pulse = ((k + i) % 2) == 0; }

                    RepaintRound(host, i);
                }
                Settle();

                int g1 = Gdi(), u1 = User();
                int dg = g1 - g0, du = u1 - u0;
                L("重绘 " + rounds + " 轮后:      GDI=" + g1 + "  USER=" + u1);
                L(string.Format("净增: GDI {0:+#;-#;0}   USER {1:+#;-#;0}", dg, du));
                L(string.Format("每轮: GDI {0:+0.0000;-0.0000;0}   USER {1:+0.0000;-0.0000;0}",
                    dg / (double)rounds, du / (double)rounds));

                if (dg <= 20 && du <= 20)
                {
                    L("[PASS] 全部控件与状态分支无 GDI/USER 泄漏（净增 <= 20）");
                }
                else
                {
                    L("[FAIL] 控件重绘存在 GDI/USER 泄漏 — GDI " + dg + "  USER " + du);
                    failures++;
                }
            }
            catch (Exception ex)
            {
                L("[FAIL] 第 2 节异常: " + ex);
                failures++;
            }
            finally
            {
                if (host != null) { try { host.Hide(); host.Dispose(); } catch { } }
                Settle();
            }

            // ══════════ 第 3 节：探针灵敏度自检 ══════════
            // 关键：如果探针本身测不出泄漏，那么前面所有「没测到增长」都毫无意义。
            // 这里故意泄漏一批真正的 GDI 对象，验证探针确实能看见。
            //
            // 注意（实测得出的能力边界）：GDI+ 的 SolidBrush/Pen 分配在 GDI+ 私有堆上，
            // 不占用 GDI 句柄表，GetGuiResources 看不见它们 —— 曾用 200 个 SolidBrush
            // 做自检，GDI 计数纹丝不动（29 -> 29）。因此本节用 gdi32!CreatePen
            // 创建货真价实的 GDI 对象来验证灵敏度；GDI+ 堆泄漏由第 4 节单独用
            // 内存/句柄趋势界定，不能混为一谈。
            L("");
            L("── 第 3 节：探针灵敏度自检（真 GDI 对象）──");
            try
            {
                const int n = 200;
                int before = Gdi();
                var pens = new IntPtr[n];
                for (int i = 0; i < n; i++) { pens[i] = CreatePen(PS_SOLID, 1, 0); }
                int after = Gdi();
                int delta = after - before;
                L("故意创建 " + n + " 个 GDI 画笔: GDI " + before + " -> " + after + " (净增 " + delta + ")");
                if (delta >= n * 9 / 10)
                {
                    L("[PASS] 探针灵敏度正常 —— 确实能测出 GDI 句柄泄漏");
                }
                else
                {
                    L("[FAIL] 探针灵敏度不足（预期净增 >= " + (n * 9 / 10) + "），本次测量结果不可信");
                    failures++;
                }
                for (int i = 0; i < n; i++) { if (pens[i] != IntPtr.Zero) { DeleteObject(pens[i]); } }
                Settle();
                L("已释放全部故意泄漏的画笔，GDI 回到 " + Gdi());
            }
            catch (Exception ex)
            {
                L("[FAIL] 第 3 节异常: " + ex);
                failures++;
            }

            // ══════════ 第 4 节：GDI+ 堆资源趋势（句柄探针的盲区）══════════
            // GDI+ 的 Brush/Pen/Graphics 若未 Dispose，不会增加 GDI 句柄，
            // 但会持续占用非托管内存。这里用「私有内存 + 托管堆」趋势来间接界定：
            // 若每轮都在泄漏 GDI+ 对象，非托管内存会随轮数单调上升。
            L("");
            L("── 第 4 节：GDI+ 堆资源趋势 ──");
            try
            {
                Form h2 = new Form();
                h2.FormBorderStyle = FormBorderStyle.None;
                h2.StartPosition = FormStartPosition.Manual;
                h2.Location = new Point(-4000, -4000);
                h2.Size = new Size(400, 300);

                var btn = new PillButton();
                btn.Variant = PillButton.Kind.Primary;
                btn.Text = "GDI+ 趋势";
                btn.Size = new Size(160, 40);
                btn.Location = new Point(20, 20);
                h2.Controls.Add(btn);

                var met = new LevelMeter();
                met.Location = new Point(20, 80);
                met.Size = new Size(300, 8);
                h2.Controls.Add(met);

                var tg = new ToggleSwitch();
                tg.Location = new Point(20, 110);
                h2.Controls.Add(tg);

                h2.Show();
                Pump(300);

                int warm4 = Math.Max(50, rounds / 2);
                for (int i = 0; i < warm4; i++)
                {
                    met.Push((float)((Math.Sin(i * 0.2) + 1.0) / 2.0));
                    RepaintTree(h2);
                }
                Settle();
                _self.Refresh();
                long pm0 = _self.PrivateMemorySize64;
                int gh0 = Gdi(), uh0 = User();
                L("预热 " + warm4 + " 轮后基线: 私有内存=" + (pm0 / 1048576L) + "MB  GDI=" + gh0 + "  USER=" + uh0);

                for (int i = 0; i < rounds; i++)
                {
                    met.Push((float)((Math.Sin(i * 0.2) + 1.0) / 2.0));
                    tg.SetCheckedSilent((i % 2) == 0);
                    SetPrivate(btn, "_hover", (i % 2) == 0);
                    SetPrivate(btn, "_anim", (i % 2) == 0 ? 1f : 0f);
                    RepaintTree(h2);
                }
                Settle();
                _self.Refresh();
                long pm1 = _self.PrivateMemorySize64;
                int gh1 = Gdi(), uh1 = User();
                long dpm = pm1 - pm0;
                L("重绘 " + rounds + " 轮后:      私有内存=" + (pm1 / 1048576L) + "MB  GDI=" + gh1 + "  USER=" + uh1);
                L(string.Format("净增: 私有内存 {0:+#;-#;0}MB  GDI {1:+#;-#;0}  USER {2:+#;-#;0}",
                    dpm / 1048576L, gh1 - gh0, uh1 - uh0));
                L(string.Format("每轮: 私有内存 {0:+0.000;-0.000;0}KB", dpm / 1024.0 / rounds));

                // 判据：GDI+ 对象每轮泄漏几个的话，200 轮会稳定累积；
                // 这里放宽到「每轮 < 1KB 且 GDI/USER 无增长」视为无泄漏。
                double kbPerRound = dpm / 1024.0 / rounds;
                if (gh1 - gh0 <= 20 && uh1 - uh0 <= 20 && kbPerRound < 1.0)
                {
                    L("[PASS] GDI+ 堆资源无泄漏趋势（每轮 < 1KB）");
                }
                else
                {
                    L("[FAIL] GDI+ 堆资源存在泄漏趋势 — 每轮 " + kbPerRound.ToString("F3") + "KB");
                    failures++;
                }

                try { h2.Hide(); h2.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                L("[FAIL] 第 4 节异常: " + ex);
                failures++;
            }
            Settle();

            // ══════════ 结论 ══════════
            L("");
            L("最终: GDI=" + Gdi() + "  USER=" + User() + "  句柄=" + Handles() + "  内存=" + WsMb() + "MB");
            L("失败项: " + failures);
            if (failures == 0) { L("=== 全部检查通过 ==="); } else { L("=== 存在失败项 ==="); }

            try
            {
                File.WriteAllText(Path.Combine(outDir, "paintprobe_report.txt"), _log.ToString(),
                    new UTF8Encoding(false));
            }
            catch { }

            return failures == 0 ? 0 : 1;
        }

        // ───────── 驱动与工具 ─────────

        /// <summary>一轮重绘：递归重绘整棵控件树，并额外走一次 WM_PRINTCLIENT。</summary>
        static void RepaintRound(Control root, int index)
        {
            RepaintTree(root);

            // 每 4 轮做一次 DrawToBitmap：它走 WM_PRINTCLIENT，
            // 能覆盖「控件不在屏幕上时」的绘制分支（截图工具用的就是这条路径）。
            if ((index % 4) == 0)
            {
                try
                {
                    using (var bmp = new Bitmap(root.Width, root.Height))
                    {
                        root.DrawToBitmap(bmp, new Rectangle(0, 0, root.Width, root.Height));
                    }
                }
                catch { }
            }
        }

        static void RepaintTree(Control c)
        {
            try
            {
                c.Invalidate();
                c.Update();
            }
            catch { }

            // 动画类控件（按钮/开关/状态点）由内部 Timer 推进，跑一下消息泵让它们真的走帧
            Application.DoEvents();

            Control.ControlCollection kids = c.Controls;
            for (int i = 0; i < kids.Count; i++) { RepaintTree(kids[i]); }
        }

        static void Pump(int ms)
        {
            const int step = 50;
            for (int t = 0; t < ms; t += step)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(step);
            }
        }

        /// <summary>等 GDI 计数稳定下来：GC + 消息泵 + 短暂休眠。</summary>
        static void Settle()
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Application.DoEvents();
                System.Threading.Thread.Sleep(80);
            }
            Application.DoEvents();
        }

        static void SetPrivate(object target, string field, object value)
        {
            if (target == null) { return; }
            try
            {
                FieldInfo fi = target.GetType().GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi != null) { fi.SetValue(target, value); }
            }
            catch { }
        }

        static void Call(MainForm f, string method, object[] args)
        {
            try
            {
                MethodInfo mi = typeof(MainForm).GetMethod(method,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi == null) { L("[MISS] 方法不存在: " + method); return; }
                mi.Invoke(f, args);
                L("[OK] " + method + "()");
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                L("[FAIL] " + method + "() -> " + inner.GetType().Name + ": " + inner.Message);
            }
        }
    }
}
