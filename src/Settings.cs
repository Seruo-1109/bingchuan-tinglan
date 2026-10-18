using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MultiAudioOutput
{
    /// <summary>
    /// 设置持久化。存储于 %APPDATA%\MultiAudioOutput\settings.json。
    /// 不使用外部依赖；序列化用 System.Web.Extensions（.NET Framework 内置）。
    /// 任何读写异常都静默降级为默认值 —— 设置文件损坏绝不能导致程序打不开。
    /// </summary>
    internal sealed class Settings
    {
        // ── 可持久化字段（名字变动即视为不兼容，需升级版本号）──
        public int Version { get; set; }
        public int LatencyMs { get; set; }
        public string CaptureDeviceId { get; set; }
        public List<string> SelectedDeviceIds { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public bool WindowPlaced { get; set; }

        /// <summary>
        /// 保存 WindowWidth/WindowHeight 时进程所处的 DPI（96 = 100%）。
        ///
        /// 为什么需要：窗口尺寸是物理像素。用户在 100% 下把窗口调成 480x632，
        /// 换成 150% 的显示器后如果照搬这 480x632 物理像素，实际逻辑尺寸就只剩
        /// 320x421 —— 内容全被挤在一起、设备列表被切成 4 行。启动时按
        /// 「当前 DPI / 保存时 DPI」等比放大，才能保持用户看到的窗口大小不变。
        ///
        /// 旧配置（Version &lt; 4）没有这个字段，读出来是 0，按 96 处理：
        /// 因为那之前的版本是 DPI 未感知的，它的像素本来就是 96 基准。
        /// </summary>
        public int WindowDpi { get; set; }

        /// <summary>
        /// 是否已经做过「按设备数把窗口长高到刚好显示 5 行」的一次性迁移。
        /// 只在升级到本版本后的第一次启动为 false，之后就再也不会自动改窗口尺寸——
        /// 否则用户特意拉小的窗口每次启动都会被顶回去。
        /// </summary>
        public bool ListFitted { get; set; }

        public const int CurrentVersion = 6;
        public const int DefaultLatency = 30;
        public const int MinLatency = 5;
        public const int MaxLatency = 300;

        /// <summary>
        /// 每台输出设备的独立音量：设备 ID → 百分比（0..100）。
        ///
        /// 为什么按设备 ID 而不是按序号：USB 声卡重新插拔后，枚举顺序完全可能变化，
        /// 序号会张冠李戴；设备 ID 由端点自己携带，跨插拔稳定。
        ///
        /// 旧配置（Version &lt; 5）没有这个字段，读出来是 null，
        /// 载入时补成空字典——空字典意味着「全部按默认 100%」，与旧行为一致。
        /// </summary>
        public Dictionary<string, int> DeviceVolumes { get; set; }

        /// <summary>
        /// 关闭窗口时收进托盘而不是退出。默认 true：这个程序的价值就在于「一直在后台
        /// 把声音分到几台设备」，关一次窗口就停掉全部输出，等于每次都要重新开一遍。
        ///
        /// 注意「最小化按钮」与「关闭按钮」都走这条路径，真正的退出只有两个入口：
        /// 托盘菜单的「退出」和托盘图标的右键退出。
        /// </summary>
        public bool CloseToTray { get; set; }

        /// <summary>
        /// 全局热键文本（"Ctrl+Alt+M"）。空字符串 = 用户关掉了热键。
        /// 存文本而不是存 (修饰键, 键码) 两个整数：配置文件是人要能读、能手改的。
        /// </summary>
        public string HotKeyText { get; set; }

        /// <summary>设备组合预设（「方案」）。见 Profile 类。</summary>
        public List<Profile> Profiles { get; set; }

        /// <summary>
        /// 一套设备组合：名字 + 勾选了哪些设备 + 各路音量。
        ///
        /// 为什么连音量一起存：用户做「耳机 + 音箱」方案时，通常是耳机要小声、
        /// 音箱要大声；只存勾选列表的话，每次切方案还得再调一遍音量，方案就没意义了。
        ///
        /// 为什么不存延迟：延迟是全局一项（不是每路），且切换方案的场景里几乎不变，
        /// 存进去只会让「方案」和「全局设置」的边界变模糊。
        /// </summary>
        public sealed class Profile
        {
            public string Name { get; set; }
            public List<string> DeviceIds { get; set; }
            public Dictionary<string, int> Volumes { get; set; }

            public Profile()
            {
                Name = string.Empty;
                DeviceIds = new List<string>();
                Volumes = new Dictionary<string, int>();
            }

            public Profile Clone()
            {
                var p = new Profile { Name = Name };
                if (DeviceIds != null) { p.DeviceIds.AddRange(DeviceIds); }
                if (Volumes != null)
                {
                    foreach (var kv in Volumes) { p.Volumes[kv.Key] = kv.Value; }
                }
                return p;
            }
        }

        /// <summary>方案名最大长度：设备区标题行只放得下这么多，超了会被截断显示。</summary>
        public const int MaxProfileName = 24;
        /// <summary>方案数量上限。纯 UI 限制：下拉列表超过 12 项就得滚动，反而更难用。</summary>
        public const int MaxProfiles = 12;

        public Settings()
        {
            Version = CurrentVersion;
            LatencyMs = DefaultLatency;
            CaptureDeviceId = string.Empty;
            SelectedDeviceIds = new List<string>();
            WindowWidth = 0;
            WindowHeight = 0;
            WindowDpi = 96;
            WindowPlaced = false;
            ListFitted = false;
            DeviceVolumes = new Dictionary<string, int>();
            CloseToTray = true;
            HotKeyText = DefaultHotKey;
            Profiles = new List<Profile>();
        }

        /// <summary>默认热键。放成常量而不是字面量：MainForm 与归一化逻辑都要用同一个值。</summary>
        public const string DefaultHotKey = "Ctrl+Alt+M";

        public static string DirPath
        {
            get
            {
                // 测试用的配置目录隔离。
                //
                // 为什么需要：有探针要**启动真实的 exe** 再观察它的行为（窗口恢复、
                // 热键注册、单实例）。这类探针现在只能靠 TestGuard 把用户的
                // settings.json 挪走再放回 —— 但那是「同一个目录里反复改名」，
                // 一旦探针崩在中途，用户的配置就停在备份名上；而且无法让被启动的
                // 子进程使用**不同的热键**（用户那份配置里的 Ctrl+Alt+M 可能正被
                // 已运行的程序占着）。
                //
                // 有了这个开关，探针可以给子进程一个完全独立的目录，各写各的，
                // 互不可见，也不碰用户配置。
                //
                // 生产路径不受影响：正常启动读不到这个环境变量，目录仍是
                // %APPDATA%\MultiAudioOutput。
                string over = Environment.GetEnvironmentVariable("MULTIAUDIO_TEST_APPDATA");
                if (!string.IsNullOrEmpty(over)) { return over; }

                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(baseDir)) { baseDir = Path.GetTempPath(); }
                return Path.Combine(baseDir, "MultiAudioOutput");
            }
        }

        public static string FilePath { get { return Path.Combine(DirPath, "settings.json"); } }

        /// <summary>载入；失败返回全新默认值。</summary>
        public static Settings Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) { return new Settings(); }
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) { return new Settings(); }

                var ser = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                Settings s = ser.Deserialize<Settings>(json);
                if (s == null) { return new Settings(); }

                // 归一化：任何越界值都必须被修正，避免坏设置让程序行为异常
                if (s.LatencyMs < MinLatency || s.LatencyMs > MaxLatency) { s.LatencyMs = DefaultLatency; }
                if (s.SelectedDeviceIds == null) { s.SelectedDeviceIds = new List<string>(); }
                if (s.CaptureDeviceId == null) { s.CaptureDeviceId = string.Empty; }
                if (s.WindowWidth < 200 || s.WindowWidth > 8000) { s.WindowWidth = 0; }
                if (s.WindowHeight < 200 || s.WindowHeight > 8000) { s.WindowHeight = 0; }
                // 版本 2 及更早没有 ListFitted 字段：这些配置里的窗口高度是按旧布局存的，
                // 高度偏小会把设备列表最后一行切掉，所以让它们各做一次自动长高。
                if (s.Version < 3) { s.ListFitted = false; }
                // 版本 3 及更早没有 WindowDpi：那之前的程序不声明 DPI 感知，
                // 存下来的像素天然就是 96 基准，读成 0 时按 96 处理即可。
                if (s.WindowDpi < 48 || s.WindowDpi > 480) { s.WindowDpi = 96; }
                // 旧配置没有 DeviceVolumes；补齐并夹逼到合法区间，
                // 避免手改配置写进 500% 这种值把音量放大到失真。
                if (s.DeviceVolumes == null) { s.DeviceVolumes = new Dictionary<string, int>(); }
                if (s.DeviceVolumes.Count > 0)
                {
                    var keys = new List<string>(s.DeviceVolumes.Keys);
                    for (int i = 0; i < keys.Count; i++)
                    {
                        int v = s.DeviceVolumes[keys[i]];
                        if (v < 0) { s.DeviceVolumes[keys[i]] = 0; }
                        else if (v > 100) { s.DeviceVolumes[keys[i]] = 100; }
                    }
                }

                // ── 版本 6：托盘 / 热键 / 方案 ──
                //
                // CloseToTray 没有「旧配置」问题：bool 反序列化缺失字段时是 false，
                // 但我们要的默认值是 true。靠版本号判断——只有 v6 及以后的配置里
                // 这个字段才可信，之前的都当成「没设置过」，一律给 true。
                if (s.Version < 6) { s.CloseToTray = true; }

                // 热键：null/空 → 默认值；非空但要能解析，解析不了就回落默认。
                // 这里**不能**把解析不了的字符串原样留下：MainForm 会拿它去注册，
                // 每次启动都失败一次，而用户在设置文件里看不出哪里写错了。
                if (s.HotKeyText == null) { s.HotKeyText = DefaultHotKey; }
                else if (s.HotKeyText.Length > 0 && HotKey.Parse(s.HotKeyText) == null) { s.HotKeyText = DefaultHotKey; }

                if (s.Profiles == null) { s.Profiles = new List<Profile>(); }
                NormalizeProfiles(s.Profiles);
                s.Version = CurrentVersion;
                return s;
            }
            catch
            {
                return new Settings();
            }
        }

        /// <summary>
        /// 方案归一化：丢掉坏数据，而不是让一个坏方案把整个配置判废。
        ///
        /// 判据：名字去空白后非空且不超长、设备列表非 null、音量夹逼 0..100。
        /// 数量上限 MaxProfiles，超出部分直接丢弃（用户手改 JSON 塞 200 条时，
        /// 下拉列表会变成一根长条，比丢数据更难用）。
        /// </summary>
        static void NormalizeProfiles(List<Profile> list)
        {
            var keep = new List<Profile>();
            for (int i = 0; i < list.Count && keep.Count < MaxProfiles; i++)
            {
                Profile p = list[i];
                if (p == null) { continue; }
                if (p.Name == null) { p.Name = string.Empty; }
                p.Name = p.Name.Trim();
                if (p.Name.Length == 0) { continue; }
                if (p.Name.Length > MaxProfileName) { p.Name = p.Name.Substring(0, MaxProfileName); }
                if (p.DeviceIds == null) { p.DeviceIds = new List<string>(); }
                // 去掉空 ID 与重复 ID：重复的设备 ID 会让「按方案勾选」时同一行被
                // 处理两次，虽然结果一样，但计数会错，后续「已选 N 台」就显示错了。
                var ids = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int j = 0; j < p.DeviceIds.Count; j++)
                {
                    string id = p.DeviceIds[j];
                    if (string.IsNullOrEmpty(id)) { continue; }
                    if (!seen.Add(id)) { continue; }
                    ids.Add(id);
                }
                p.DeviceIds = ids;

                if (p.Volumes == null) { p.Volumes = new Dictionary<string, int>(); }
                if (p.Volumes.Count > 0)
                {
                    var vk = new List<string>(p.Volumes.Keys);
                    for (int j = 0; j < vk.Count; j++)
                    {
                        int v = p.Volumes[vk[j]];
                        if (v < 0) { p.Volumes[vk[j]] = 0; }
                        else if (v > 100) { p.Volumes[vk[j]] = 100; }
                    }
                }
                keep.Add(p);
            }
            list.Clear();
            list.AddRange(keep);
        }

        /// <summary>保存；失败静默（无权限目录等不应打断使用）。</summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                var ser = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                string json = ser.Serialize(this);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                // 原子替换，避免断电写坏文件
                if (File.Exists(FilePath)) { File.Delete(FilePath); }
                File.Move(tmp, FilePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
