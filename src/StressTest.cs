using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiAudioOutput
{
    /// <summary>
    /// 无界面压力测试：真实驱动 AudioEngine 跑多轮启停，统计资源与异常。
    /// 用法: 并川听澜.exe --stress &lt;输出目录&gt; [轮数]
    /// </summary>
    internal static class StressTest
    {
        static StringBuilder _log = new StringBuilder();
        static int _fail;

        static void L(string s) { _log.AppendLine(s); Console.WriteLine(s); }
        static void Check(string name, bool ok, string detail = "")
        {
            L((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail.Length > 0 ? " — " + detail : ""));
            if (!ok) { _fail++; }
        }

        internal static int Run(string outDir, int rounds)
        {
            Directory.CreateDirectory(outDir);
            var self = Process.GetCurrentProcess();
            L("=== 并川听澜 压力测试 ===");
            L("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            L("轮数: " + rounds);
            L("进程: " + self.ProcessName + " PID=" + self.Id);
            L("OS: " + Environment.OSVersion);
            L("CLR: " + Environment.Version);
            L("64位进程: " + Environment.Is64BitProcess);
            L("");

            // ── 1. 设备枚举 ──
            L("── 1. 设备枚举 ──");
            var engine = new AudioEngine();
            List<MMDevice> devs = engine.ListOutputDevices();
            L("  发现的输出设备数: " + devs.Count);
            for (int i = 0; i < devs.Count; i++)
            {
                string id, nm, st;
                try { id = devs[i].ID; } catch { id = "?"; }
                try { nm = devs[i].FriendlyName; } catch { nm = "?"; }
                try { st = devs[i].State.ToString(); } catch { st = "?"; }
                L("    [" + i + "] " + nm + "  state=" + st);
                L("         id=" + id);
            }
            Check("设备枚举不抛异常", true);
            if (devs.Count == 0)
            {
                L("");
                L("!! 本机没有活动的输出设备，无法进行音频链路测试。");
                Write(outDir);
                return _fail;
            }

            // ── 2. 电平 API 可用性（未运行时也不应崩）──
            L("");
            L("── 2. 电平接口 ──");
            float[] idleLevels = engine.ReadLevels();
            Check("未运行时 ReadLevels 返回空数组", idleLevels.Length == 0, "len=" + idleLevels.Length);
            try
            {
                float peak = devs[0].AudioMeterInformation.MasterPeakValue;
                Check("MasterPeakValue 可读", peak >= 0f && peak <= 1f, "value=" + peak.ToString("F4"));
            }
            catch (Exception ex) { Check("MasterPeakValue 可读", false, ex.Message); }

            // ── 3. 多轮启停 ──
            L("");
            L("── 3. 启停压力测试（" + rounds + " 轮）──");
            int latency = 30;
            var use = new List<MMDevice>();
            use.Add(devs[0]);
            if (devs.Count > 1) { use.Add(devs[1]); }

            int okRounds = 0, failRounds = 0;
            double maxStartMs = 0, sumStartMs = 0;
            double maxStopMs = 0, sumStopMs = 0;
            long maxHandles = 0, maxThreads = 0, maxMem = 0;
            long firstHandles = 0;

            for (int r = 1; r <= rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                StartReport rep = engine.Start(null, use, latency);
                sw.Stop();
                double startMs = sw.Elapsed.TotalMilliseconds;
                sumStartMs += startMs;
                if (startMs > maxStartMs) { maxStartMs = startMs; }

                if (!rep.Started)
                {
                    failRounds++;
                    L("  第 " + r + " 轮启动失败: " + (rep.FatalError ?? "未知") +
                      (rep.Failed.Count > 0 ? " | 失败设备: " + string.Join("; ", rep.Failed.ToArray()) : ""));
                    continue;
                }

                if (r == 1)
                {
                    L("  第 1 轮启动成功:");
                    L("    采集格式: " + Fmt(rep.Format));
                    L("    成功设备: " + rep.Attached.Count + " -> " + string.Join(" | ", rep.Attached.ToArray()));
                    if (rep.Failed.Count > 0) { L("    失败设备: " + string.Join(" | ", rep.Failed.ToArray())); }
                }

                // 跑一小段时间，让音频真正流动并采样电平
                float peakSeen = 0f;
                long droppedBefore = engine.DroppedBytes;
                var sw2 = Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 400)
                {
                    float[] lv = engine.ReadLevels();
                    for (int i = 0; i < lv.Length; i++) { if (lv[i] > peakSeen) { peakSeen = lv[i]; } }
                    Thread.Sleep(20);
                }

                Check("第 " + r + " 轮: IsRunning=true", engine.IsRunning);
                Check("第 " + r + " 轮: 输出通道数=" + rep.Attached.Count, engine.OutputCount == rep.Attached.Count,
                      "actual=" + engine.OutputCount);
                Check("第 " + r + " 轮: ReadLevels 长度匹配", engine.ReadLevels().Length == rep.Attached.Count,
                      "len=" + engine.ReadLevels().Length);

                var sw3 = Stopwatch.StartNew();
                engine.Stop();
                sw3.Stop();
                double stopMs = sw3.Elapsed.TotalMilliseconds;
                sumStopMs += stopMs;
                if (stopMs > maxStopMs) { maxStopMs = stopMs; }

                Check("第 " + r + " 轮: 停止后 IsRunning=false", !engine.IsRunning);
                Check("第 " + r + " 轮: 停止后通道清空", engine.OutputCount == 0, "count=" + engine.OutputCount);

                // 资源快照
                self.Refresh();
                if (r == 1) { firstHandles = self.HandleCount; }
                if (self.HandleCount > maxHandles) { maxHandles = self.HandleCount; }
                if (self.Threads.Count > maxThreads) { maxThreads = self.Threads.Count; }
                long mem = self.WorkingSet64 / (1024 * 1024);
                if (mem > maxMem) { maxMem = mem; }

                okRounds++;
            }

            // 先把枚举出的 MMDevice 释放掉，再统计句柄水位，
            // 否则测到的是「未释放的枚举对象」而不是引擎自身的泄漏。
            AudioEngine.ReleaseDevices(devs);
            devs.Clear();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(300);

            self.Refresh();
            int endHandles = self.HandleCount;
            L("  末轮句柄数: " + endHandles);
            L("  句柄净增: " + (endHandles - firstHandles) + "（" + firstHandles + " -> " + endHandles + "）");

            L("");
            L("  成功轮次: " + okRounds + " / " + rounds + "，失败: " + failRounds);
            L("  启动耗时: 平均 " + (okRounds > 0 ? (sumStartMs / okRounds).ToString("F1") : "-") +
              " ms，最大 " + maxStartMs.ToString("F1") + " ms");
            L("  停止耗时: 平均 " + (okRounds > 0 ? (sumStopMs / okRounds).ToString("F1") : "-") +
              " ms，最大 " + maxStopMs.ToString("F1") + " ms");
            L("  首轮句柄数: " + firstHandles + "，峰值句柄数: " + maxHandles);
            L("  峰值线程数: " + maxThreads + "，峰值内存: " + maxMem + " MB");

            Check("全部 " + rounds + " 轮启动成功", failRounds == 0, "失败 " + failRounds + " 轮");
            // 句柄泄漏判据：释放枚举对象后的净增相对首轮不超过 25%
            double growth = firstHandles > 0 ? (endHandles - firstHandles) / (double)firstHandles : 0;
            Check("无句柄泄漏（净增 < 25%）", growth < 0.25,
                  "净增 " + (growth * 100).ToString("F1") + "% (" + firstHandles + " -> " + endHandles +
                  "，峰值 " + maxHandles + ")");

            // ── 4. 边界条件 ──
            // 注意：use 里的设备引用在此处会被重新枚举出来，因为上一批已在上面释放。
            var use2 = engine.ListOutputDevices();
            if (use2.Count > 0) { use = new List<MMDevice>(); use.Add(use2[0]); if (use2.Count > 1) { use.Add(use2[1]); } }

            L("");
            L("── 4. 边界与异常处理 ──");

            // 4a 空目标列表
            StartReport r1 = engine.Start(null, new List<MMDevice>(), 30);
            Check("空设备列表应失败且不崩", !r1.Started && r1.FatalError != null, r1.FatalError ?? "");

            // 4b 重复 Stop
            engine.Stop(); engine.Stop(); engine.Stop();
            Check("重复 Stop 不抛异常", true);

            // 4c 重复 Start（第二次应被拒绝）
            StartReport a = engine.Start(null, use, 30);
            if (a.Started)
            {
                StartReport b = engine.Start(null, use, 30);
                Check("重复 Start 被拒绝", !b.Started, b.FatalError ?? "");
                engine.Stop();
            }
            else { L("  (跳过重复 Start 检查：首启失败 " + a.FatalError + ")"); }

            // 4d 极端延迟值
            StartReport r4 = engine.Start(null, use, 1);
            if (r4.Started) { engine.Stop(); Check("延迟=1ms 被钳制后可用", true, "采集格式 " + Fmt(r4.Format)); }
            else { Check("延迟=1ms 未崩溃（启动失败也可接受）", true, r4.FatalError ?? ""); }
            StartReport r5 = engine.Start(null, use, 5000);
            if (r5.Started) { engine.Stop(); Check("延迟=5000ms 被钳制后可用", true); }
            else { Check("延迟=5000ms 未崩溃", true, r5.FatalError ?? ""); }

            // 4e 指定具体采集设备
            StartReport r6 = engine.Start(use.Count > 0 ? use[0] : null, use, 30);
            if (r6.Started) { Thread.Sleep(200); engine.Stop(); Check("指定采集源可用", true, r6.CaptureName); }
            else { Check("指定采集源未崩溃", true, r6.FatalError ?? ""); }

            // 4f Dispose 后再调用不应崩
            engine.Dispose();
            engine.Stop();
            try { engine.ReadLevels(); Check("Dispose 后 ReadLevels 不崩", true); }
            catch (Exception ex) { Check("Dispose 后 ReadLevels 不崩", false, ex.Message); }
            AudioEngine.ReleaseDevices(use2);

            // ── 5. 设置读写 ──
            L("");
            L("── 5. 设置持久化 ──");
            var s = new Settings { LatencyMs = 25, CaptureDeviceId = "test-id" };
            s.SelectedDeviceIds.Add("a"); s.SelectedDeviceIds.Add("b");
            bool saved = s.Save();
            Check("设置保存", saved, Settings.FilePath);
            var s2 = Settings.Load();
            Check("设置往返一致（延迟）", s2.LatencyMs == 25, "got=" + s2.LatencyMs);
            Check("设置往返一致（设备数）", s2.SelectedDeviceIds.Count == 2, "got=" + s2.SelectedDeviceIds.Count);
            Check("设置往返一致（采集源）", s2.CaptureDeviceId == "test-id", "got=" + s2.CaptureDeviceId);

            // 坏设置
            try
            {
                File.WriteAllText(Settings.FilePath, "{ this is not json !!!", Encoding.UTF8);
                var s3 = Settings.Load();
                Check("损坏的设置文件回退到默认值", s3.LatencyMs == Settings.DefaultLatency, "got=" + s3.LatencyMs);
            }
            catch (Exception ex) { Check("损坏的设置文件回退", false, ex.Message); }

            // 越界值
            try
            {
                File.WriteAllText(Settings.FilePath, "{\"Version\":2,\"LatencyMs\":99999}", Encoding.UTF8);
                var s4 = Settings.Load();
                Check("越界延迟被归一化", s4.LatencyMs == Settings.DefaultLatency, "got=" + s4.LatencyMs);
            }
            catch (Exception ex) { Check("越界延迟归一化", false, ex.Message); }

            // ── 6. 几何自检 ──
            L("");
            L("── 6. 圆角几何 ──");
            int geo = SelfTest.Run(Path.Combine(outDir, "geometry"));
            Check("Squircle 几何自检通过", geo == 0, "失败项=" + geo);

            L("");
            L(_fail == 0 ? "=== 全部检查通过 ===" : "=== 存在 " + _fail + " 项失败 ===");
            Write(outDir);
            return _fail;
        }

        static string Fmt(WaveFormat wf)
        {
            if (wf == null) { return "(null)"; }
            try
            {
                return wf.SampleRate + "Hz " + wf.BitsPerSample + "bit " + wf.Channels + "ch " +
                       wf.Encoding + " (" + wf.AverageBytesPerSecond + " B/s)";
            }
            catch { return "(unreadable)"; }
        }

        static void Write(string outDir)
        {
            try { File.WriteAllText(Path.Combine(outDir, "stress_report.txt"), _log.ToString(), Encoding.UTF8); }
            catch { }
        }
    }
}
