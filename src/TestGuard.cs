using System;
using System.IO;

namespace MultiAudioOutput
{
    /// <summary>
    /// 测试入口的配置沙箱护栏。
    ///
    /// 背景（真实事故）：--shot 会把窗口移到 (-4000,-4000) 截图，--stress 会写入
    /// 损坏 JSON 与越界延迟来验证归一化，两者都会调用 SaveSettings()，于是把
    /// 「全选设备」「窗口在屏幕外」这类测试态写进了用户的真实 settings.json。
    /// 下次用户正常启动程序，窗口就会出现在屏幕外，且所有设备被默认勾选。
    ///
    /// 因此所有测试入口必须包在 Begin/End 之间：运行前把真实配置挪走，
    /// 结束后原样放回；若用户原本没有配置文件，结束时删除测试产生的文件。
    /// </summary>
    internal static class TestGuard
    {
        static readonly string BakPath = Settings.FilePath + ".testbak";
        static bool _active;
        static bool _hadOriginal;

        /// <summary>备份用户真实配置，让测试在干净状态下运行。</summary>
        internal static void Begin()
        {
            if (_active) { return; }
            try
            {
                Directory.CreateDirectory(Settings.DirPath);
                _hadOriginal = File.Exists(Settings.FilePath);
                if (_hadOriginal)
                {
                    File.Copy(Settings.FilePath, BakPath, true);
                    File.Delete(Settings.FilePath);
                }
                _active = true;
            }
            catch { _active = false; }
        }

        /// <summary>还原用户真实配置，并清掉测试留下的痕迹。</summary>
        internal static void End()
        {
            if (!_active) { return; }
            try
            {
                if (File.Exists(Settings.FilePath)) { File.Delete(Settings.FilePath); }
                if (_hadOriginal && File.Exists(BakPath))
                {
                    File.Copy(BakPath, Settings.FilePath, true);
                    File.Delete(BakPath);
                }
            }
            catch { }
            try { if (File.Exists(BakPath)) { File.Delete(BakPath); } }
            catch { }
            _active = false;
        }
    }
}
