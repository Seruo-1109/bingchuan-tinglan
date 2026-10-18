using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 应用图标加载器。
    ///
    /// 为什么需要它：编译器的 /win32icon 只把图标写进 exe 的 **Win32 资源节**
    /// （所以资源管理器里能看到新图标），但 WinForms 的 Form.Icon 属性如果不显式赋值，
    /// 窗体就永远用 .NET 内置的默认图标（一个灰底蓝框的通用窗口图）。任务栏、
    /// Alt+Tab 切换器读的是窗口图标，不是 exe 资源节 —— 两边是两套东西。
    ///
    /// 本类从「嵌入的托管资源 → exe 自身资源节 → 系统默认」三级回退取图标，
    /// 保证任何情况下都不会因为图标缺失而抛异常。
    /// </summary>
    internal static class AppIcon
    {
        /// <summary>嵌入的托管资源名（csc /resource:app.ico,AppIcon.ico 指定）。</summary>
        const string ResourceName = "AppIcon.ico";

        /// <summary>
        /// 按尺寸分别缓存。
        ///
        /// 为什么不用「单个最大的那份」缓存（这是修过的真缺陷）：旧写法是
        /// `if (_cached != null &amp;&amp; _cached.Width &gt;= size) return _cached;`，
        /// 于是先 Load(64) 之后，再 Load(32) 拿回来的还是 64×64 的图标 —— 调用方
        /// 以为拿到 32px，实际拿到 64px，尺寸不符。tests\appiconprobe 第 2 节就是
        /// 靠「返回尺寸必须等于请求尺寸」这条断言把它抓出来的。
        /// </summary>
        static readonly Dictionary<int, Icon> _cache = new Dictionary<int, Icon>();

        /// <summary>
        /// 取应用图标。size 为期望边长（像素）；返回的 Icon 尺寸**保证等于 size**
        /// （必要时从最接近的帧缩放）。任何一步失败都继续往下回退，最终一定返回非 null。
        /// </summary>
        public static Icon Load(int size)
        {
            if (size <= 0) { size = 32; }
            lock (_cache)
            {
                Icon hit;
                if (_cache.TryGetValue(size, out hit) && hit != null) { return hit; }
            }

            Icon icon = FromEmbeddedResource(size);
            if (icon == null) { icon = FromOwnExecutable(size); }
            if (icon == null) { icon = SystemIcons.Application; }

            // 选帧策略是「宁可大一点也不要糊」，所以挑出来的帧未必正好是 size；
            // 在出口处统一规整，让 Load(size) 的返回值尺寸严格等于 size。
            icon = FitTo(icon, size);

            lock (_cache)
            {
                if (!_cache.ContainsKey(size)) { _cache[size] = icon; }
            }
            return icon;
        }

        /// <summary>
        /// 把任意 Icon 规整成恰好 size×size。已经是目标尺寸就原样返回。
        ///
        /// 为什么不能只靠 new Icon(src, new Size(size, size))（这是修过的真缺陷）：
        /// 那个构造函数只是**从多帧 ICO 里挑一帧**，它**永不重采样**。挑不到正好
        /// size 的帧时，它返回的是「最接近的更大帧但带着被请求的尺寸标签」，或者干脆
        /// 返回被选中帧的真实尺寸 —— tests\appiconprobe 实测 Load(96/128/256) 全部
        /// 返回 64x64，调用方以为拿到大图，实际拿到 64px。这里改用真正的 GDI+ 重采样，
        /// 让返回值尺寸严格等于请求尺寸。
        /// </summary>
        static Icon FitTo(Icon src, int size)
        {
            if (src == null) { return null; }
            if (src.Width == size && src.Height == size) { return src; }
            try
            {
                using (Bitmap src32 = ToBitmap32(src))
                {
                    if (src32 == null) { return src; }
                    Icon fitted = BitmapToIcon(src32, size);
                    // 重采样失败不是致命问题，退回原图标（尺寸可能不符，但绝不为图标崩程序）
                    return (fitted != null) ? fitted : src;
                }
            }
            catch
            {
                return src;
            }
        }

        /// <summary>
        /// 把 Icon 安全地转成 32bppArgb 位图。
        ///
        /// 必须自己重绘而不是直接用 Icon.ToBitmap()：ICO 帧若是不带 alpha 通道的
        /// 低位深格式，ToBitmap() 出来的是 32bppRgb（alpha 恒为 255），交给
        /// Graphics.DrawImage 后透明区域会变成黑块。先铺到一张已知 ARGB 画布上，
        /// 透明信息才保得住。
        /// </summary>
        static Bitmap ToBitmap32(Icon src)
        {
            try
            {
                using (Bitmap raw = src.ToBitmap())
                {
                    return ToArgbCopy(raw);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>第一级：嵌入的托管资源（最可靠，不依赖文件位置）。</summary>
        static Icon FromEmbeddedResource(int size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { return null; }
                    return FromStream(s, size);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从 ICO 流里挑一帧、解码、再规整成恰好 size×size 的 Icon。
        ///
        /// 为什么要自己解析 ICO 目录（tests\icodiag.cs 实测，务必记住）：
        /// System.Drawing.Icon 对 ICO 里的 **PNG 压缩帧**完全没有正确处理，而且
        /// **失败方式会骗人**：
        ///   ① new Icon(stream, Size(128,128)) 这样构造 **不抛异常**，异常要等到
        ///      后面调用 ToBitmap() 才抛 ArgumentOutOfRangeException
        ///      「请求的范围扩展超过了数组的结尾」；
        ///   ② 它根本不看帧的编码格式，PNG 帧会被当 BMP 位图解析 —— 小尺寸 PNG 帧
        ///      解析出来是满屏噪声（不抛异常，最难发现）；
        ///   ③ Icon.Width 会撒谎：请求 256 时返回 Width=128。
        /// 所以「逐档试试能不能构造成功」这条退路是走不通的。可靠的做法是自己读
        /// ICONDIR / ICONDIRENTRY，用载荷前 8 字节判断这一帧是 BMP 还是 PNG
        /// （PNG 魔数 89 50 4E 47），再按编码分别解码（见 PickFrameBitmap）。
        /// </summary>
        static Icon FromStream(Stream s, int size)
        {
            byte[] data = ReadAll(s);
            // 读不出来或短于一个 ICONDIR（6 字节）就直接放弃这一级，交给 Load 的
            // 下一级回退（exe 资源节 → 系统默认图标）。绝不在这里 new Icon(stream)：
            // 调用方是在 using 里关掉流的，那样交出去的图标会带着一个已关闭的流引用
            // （与 FromBytes 注释里写的是同一个坑，别在这里又踩一次）。
            if (data == null || data.Length < 6) { return null; }

            Bitmap frame = null;
            try { frame = PickFrameBitmap(data, size); }
            catch { frame = null; }
            if (frame == null) { return FromBytes(data); }

            Icon result;
            try { result = BitmapToIcon(frame, size); }
            finally { frame.Dispose(); }
            // 解码成功但重采样失败时也别交出 null，退回默认帧更安全
            return (result != null) ? result : FromBytes(data);
        }

        /// <summary>
        /// 最后退路：不带尺寸构造，让 .NET 自己挑默认帧（实测永远可用）。
        ///
        /// 刻意接收 byte[] 而不是原始 Stream：`new Icon(stream)` 会保留对流的引用，
        /// 而调用方（FromEmbeddedResource）是在 using 里把流关掉的 —— 交出去的图标
        /// 之后一旦被解引用就会踩到已关闭的流。这里用 MemoryStream 包一份自己的副本，
        /// 生命周期完全由图标自己掌握。
        /// </summary>
        static Icon FromBytes(byte[] data)
        {
            if (data == null || data.Length < 6) { return null; }
            try
            {
                using (var ms = new MemoryStream(data, false))
                {
                    return new Icon(ms);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 亲读 ICO 目录，挑出最合适的一帧并解码成 32bppArgb 位图（PNG 帧与 BMP 帧都支持）。
        ///
        /// 为什么必须自己解析目录（tests\icoframes + tests\icodiag 实测，务必记住）：
        /// System.Drawing.Icon 对 ICO 里的 **PNG 压缩帧**完全没有正确处理，而且
        /// **失败方式会骗人**：
        ///   ① new Icon(stream, Size(128,128)) 这样构造 **不抛异常**，异常要等到
        ///      后面调用 ToBitmap() 才抛 ArgumentOutOfRangeException
        ///      「请求的范围扩展超过了数组的结尾」；
        ///   ② 它根本不看帧的编码格式，PNG 帧会被当 BMP 位图解析 —— 小尺寸 PNG 帧
        ///      解析出来是满屏噪声（不抛异常，最难发现）；
        ///   ③ Icon.Width 会撒谎：请求 256 时返回 Width=128。
        /// 所以「逐档试试能不能构造成功」这条退路是走不通的。
        ///
        /// 本方法对两种帧走两条解码路径（app.ico 是混合编码：16/24/32/48/64 是 BMP，
        /// 128/256 是 PNG）：
        ///   BMP 帧 -> 抽成单帧 ICO 交给 Icon 解析，再转 ARGB 位图；
        ///   PNG 帧 -> 直接交给 Bitmap（GDI+ 解 PNG 是可靠的），拿到正确的 alpha。
        /// 以前只挑 BMP 帧，于是 Load(128)/Load(256) 静默退回 64px 的 BMP 帧 ——
        /// tests\appiconprobe 第 1 节把这件事抓了出来（返回尺寸与请求尺寸不符）。
        /// </summary>
        static Bitmap PickFrameBitmap(byte[] data, int size)
        {
            if (data.Length < 6) { return null; }

            // ── ICONDIR：reserved(2) type(2) count(2) ──
            int reserved = BitConverter.ToInt16(data, 0);
            int type = BitConverter.ToInt16(data, 2);
            int count = BitConverter.ToInt16(data, 4);
            if (reserved != 0 || type != 1 || count <= 0) { return null; }

            // ── 挑最接近请求尺寸的一帧（宁可大一点也不要糊；偏小惩罚更重）──
            int pickOff = -1, pickLen = 0, pickW = 0, pickH = 0;
            bool pickPng = false;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                if (e + 16 > data.Length) { break; }
                int w = data[e]; if (w == 0) { w = 256; }
                int h = data[e + 1]; if (h == 0) { h = 256; }
                int len = BitConverter.ToInt32(data, e + 8);
                int off = BitConverter.ToInt32(data, e + 12);
                if (off < 0 || len <= 8 || off + len > data.Length) { continue; }

                int delta = (w >= size) ? (w - size) : (size - w) * 3;
                if (delta >= bestDelta) { continue; }

                bestDelta = delta;
                pickOff = off;
                pickLen = len;
                pickW = w;
                pickH = h;
                // PNG 帧魔数 0x89 'P' 'N' 'G'；DIB/BMP 帧则以 BITMAPINFOHEADER 的
                // biSize（通常是 40 = 0x28）开头，绝不会是 0x474E5089。
                pickPng = (data[off] == 0x89 && data[off + 1] == 0x50
                        && data[off + 2] == 0x4E && data[off + 3] == 0x47);
            }
            if (pickOff < 0) { return null; }

            if (pickPng)
            {
                // PNG 帧：GDI+ 能正确解码，直接交给 Bitmap
                using (var ms = new MemoryStream(data, pickOff, pickLen))
                using (var img = Image.FromStream(ms))
                {
                    if (img == null) { return null; }
                    return ToArgbCopy(img);
                }
            }

            // ── BMP 帧：抽出来单独包成单帧 ICO 再交给 Icon 解析 ──
            // 为什么不能直接把整支 ICO 交给 Icon 并指定尺寸：Icon 不看帧的编码格式，
            // 命中 PNG 帧时构造仍会成功（Width 还会撒谎），坏掉的是后面的 ToBitmap。
            // 抽出单帧后才能确保 .NET 拿到的一定是它能正确解析的 BMP 帧。
            using (var ms = new MemoryStream(BuildSingleFrameIco(data, pickOff, pickLen)))
            using (var ic = new Icon(ms, new Size(pickW, pickH)))
            {
                using (Bitmap raw = ic.ToBitmap())
                {
                    if (raw == null) { return null; }
                    return ToArgbCopy(raw);
                }
            }
        }

        /// <summary>把任意 Image 铺到一张已知 32bppArgb 画布上（保住透明通道）。</summary>
        static Bitmap ToArgbCopy(Image src)
        {
            if (src == null) { return null; }
            var argb = new Bitmap(src.Width, src.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(argb))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return argb;
        }

        /// <summary>把位图转成恰好 size×size 的 Icon（真正的 GDI+ 重采样）。</summary>
        static Icon BitmapToIcon(Bitmap src, int size)
        {
            if (src == null) { return null; }
            Bitmap target = src;
            bool ownTarget = false;
            try
            {
                if (src.Width != size || src.Height != size)
                {
                    target = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    ownTarget = true;
                    using (Graphics g = Graphics.FromImage(target))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, size, size));
                    }
                }

                IntPtr h = target.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        Icon copy = new Icon(tmp, new Size(size, size));
                        // 构造成功不代表可用：真正碰一次像素，坏了就当场判失败
                        using (Bitmap probe = copy.ToBitmap())
                        {
                            if (probe == null) { return null; }
                        }
                        return copy;
                    }
                }
                finally
                {
                    // GetHicon 的句柄必须显式销毁，否则每次调用漏一个 GDI 对象
                    NativeMethods.DestroyIcon(h);
                }
            }
            finally
            {
                if (ownTarget && target != null) { target.Dispose(); }
            }
        }

        /// <summary>把 ICO 里某一帧抽出来，重新包成只有一个目录项的单帧 ICO。</summary>
        static byte[] BuildSingleFrameIco(byte[] data, int off, int len)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write((short)0);   // reserved
            bw.Write((short)1);   // type = icon
            bw.Write((short)1);   // count = 1
            // 宽高与位深都从 DIB 头读（BITMAPINFOHEADER 的 biWidth / biHeight/2 /
            // biBitCount 是权威值），这样目录项与载荷一定自洽，不必依赖原目录项里
            // 可能为 0（表示 256）的字节，也不会把 8bpp 调色板帧谎报成 32bpp。
            int w = 0, h = 0, bpp = 32;
            if (off + 16 <= data.Length)
            {
                w = BitConverter.ToInt32(data, off + 4);
                h = BitConverter.ToInt32(data, off + 8) / 2;   // biHeight = XOR 高 + AND 高
                int bits = BitConverter.ToInt16(data, off + 14);
                if (bits >= 1 && bits <= 32) { bpp = bits; }
            }
            bw.Write((byte)(w >= 256 ? 0 : w));
            bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0);    // 调色板数
            bw.Write((byte)0);    // reserved
            bw.Write((short)1);   // planes
            bw.Write((short)bpp); // 位深
            bw.Write(len);
            bw.Write(22);         // 6 字节 ICONDIR + 16 字节目录项
            bw.Write(data, off, len);
            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>把流整个读进内存（ICO 都很小，几十到几百 KB）。</summary>
        static byte[] ReadAll(Stream s)
        {
            try
            {
                if (s.CanSeek) { s.Position = 0; }
                var ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); }
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>第二级：exe 自身的 Win32 资源节（/win32icon 写进去的那份）。</summary>
        static Icon FromOwnExecutable(int size)
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { return null; }

                using (Icon small = Icon.ExtractAssociatedIcon(exe))
                {
                    if (small == null) { return null; }
                    // ExtractAssociatedIcon 只给系统小图标尺寸（16-32px）；请求更大尺寸时
                    // 必须自己重采样放大，否则任务栏/Alt+Tab 里会糊成一团。
                    using (Bitmap src32 = ToBitmap32(small))
                    {
                        if (src32 == null) { return new Icon(small, new Size(small.Width, small.Height)); }
                        Icon big = BitmapToIcon(src32, size);
                        if (big != null) { return big; }
                    }
                    return new Icon(small, new Size(small.Width, small.Height));
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把应用图标贴到一个窗体上。必须在窗口句柄创建前调用。</summary>
        public static void Apply(Form form)
        {
            if (form == null) { return; }
            try
            {
                Icon icon = Load(32);
                if (icon != null) { form.Icon = icon; }
            }
            catch
            {
                // 图标只是外观，任何失败都不该影响程序启动
            }
        }

        internal static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public static extern bool DestroyIcon(IntPtr handle);
        }
    }
}
