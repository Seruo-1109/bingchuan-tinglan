using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiAudioOutput
{
    /// <summary>
    /// 句柄归因探针：分别重复调用可疑 API，定位真正的泄漏源。
    /// 用法: --leakprobe &lt;输出目录&gt;
    /// </summary>
    internal static class LeakProbe
    {
        static StringBuilder _log = new StringBuilder();
        static Process _self = Process.GetCurrentProcess();

        static void L(string s) { _log.AppendLine(s); Console.WriteLine(s); }
        static int H() { _self.Refresh(); return _self.HandleCount; }

        static void Section(string title, int iterations, Action body)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            System.Threading.Thread.Sleep(200);
            int h0 = H();
            for (int i = 0; i < iterations; i++)
            {
                try { body(); } catch { }
                if ((i + 1) % 50 == 0) { System.Threading.Thread.Sleep(30); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            System.Threading.Thread.Sleep(300);
            int h1 = H();
            L(string.Format("{0,-42} iters={1,-5} handles {2,5} -> {3,5}  ({4:+#;-#;0}, {5:+0.000;-0.000;0}/iter)",
                title, iterations, h0, h1, h1 - h0, (h1 - h0) / (double)iterations));
        }

        internal static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            L("=== 句柄归因探针 ===");
            L("PID=" + _self.Id + "  64bit=" + Environment.Is64BitProcess);
            L("基线句柄数=" + H());
            L("");

            var en = new MMDeviceEnumerator();

            // 1. 枚举设备（每次都会新建 MMDevice 集合）
            Section("EnumerateAudioEndPoints(Render,Active)", 100, delegate
            {
                MMDeviceCollection c = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                int n = c.Count;
                if (n > 0) { string s = c[0].FriendlyName; }
            });

            // 2. 枚举 + 显式 Dispose 每个 MMDevice
            Section("Enumerate + Dispose each MMDevice", 100, delegate
            {
                MMDeviceCollection c = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < c.Count; i++) { try { c[i].Dispose(); } catch { } }
            });

            // 3. AudioMeterInformation 读取（同一 device 反复取）
            MMDevice dev = null;
            try
            {
                MMDeviceCollection c = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (c.Count > 0) { dev = c[0]; }
            }
            catch { }

            if (dev != null)
            {
                Section("dev.AudioMeterInformation.MasterPeakValue", 500, delegate
                {
                    float f = dev.AudioMeterInformation.MasterPeakValue;
                    if (f < -1) { L("unreachable"); }
                });

                // 4. 缓存 AudioMeterInformation 后反复读
                AudioMeterInformation mi = null;
                try { mi = dev.AudioMeterInformation; } catch { }
                if (mi != null)
                {
                    Section("cached AudioMeterInformation.MasterPeakValue", 500, delegate
                    {
                        float f = mi.MasterPeakValue;
                        if (f < -1) { L("unreachable"); }
                    });
                }

                // 5. GetDefaultAudioEndpoint
                Section("GetDefaultAudioEndpoint", 100, delegate
                {
                    MMDevice d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (d == null) { L("unreachable"); }
                });

                // 6. GetDefaultAudioEndpoint + Dispose
                Section("GetDefaultAudioEndpoint + Dispose", 100, delegate
                {
                    MMDevice d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    try { d.Dispose(); } catch { }
                });

                // 7. 完整 WasapiOut 生命周期
                Section("WasapiOut create/Init/Play/Stop/Dispose", 30, delegate
                {
                    var wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
                    var buf = new BufferedWaveProvider(wo.OutputWaveFormat);
                    buf.DiscardOnBufferOverflow = true; buf.ReadFully = true;
                    wo.Init(buf);
                    wo.Play();
                    System.Threading.Thread.Sleep(40);
                    wo.Stop();
                    wo.Dispose();
                    buf.ClearBuffer();
                });

                // 8. WasapiLoopbackCapture 完整生命周期
                Section("WasapiLoopbackCapture start/stop/dispose", 20, delegate
                {
                    var cap = new WasapiLoopbackCapture(dev);
                    cap.StartRecording();
                    System.Threading.Thread.Sleep(60);
                    cap.StopRecording();
                    cap.Dispose();
                });
            }

            L("");
            L("最终句柄数=" + H());
            L("按调用顺序阅读；增长 ≈ 0/iter 的 API 是干净的。");
            try { File.WriteAllText(Path.Combine(outDir, "leakprobe.txt"), _log.ToString(), Encoding.UTF8); }
            catch { }
            return 0;
        }
    }
}
