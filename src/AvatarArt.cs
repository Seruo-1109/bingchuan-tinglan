using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace MultiAudioOutput
{
    /// <summary>
    /// 二次元角色头像（三个表情）的加载与绘制。
    ///
    /// 素材来自「赛博少女角色设定图」底排的三个头像，已用泛洪法抠掉白底
    /// （见 design\anime_cut_heads.py），再在 design\make_badges.py 里按 alpha
    /// 包围盒裁紧、等比装进正方形、用椭圆遮罩裁圆，输出 192x192 的 PNG。
    ///
    /// 资源文件放在项目根的 res\ 下，由 build.ps1 用 /resource: 嵌进程序集，
    /// 名字分别是 Avatar0.png / Avatar1.png / Avatar2.png，与此处的常量一一对应。
    ///
    /// 为什么 PNG 里已经裁好圆形了、这里还要不要 Clip？
    /// 不要。PNG 的圆形边缘是抗锯齿的 alpha（0..255 过渡），再套一层抗锯齿
    /// 的椭圆 ClipPath 会把边缘像素的 alpha 乘两次（0.5 x 0.5 = 0.25），
    /// 圆形轮廓反而被削细一圈，在 32px 这种小尺寸上看得出来。所以这里直接
    /// 把 PNG 缩放着画上去，只依赖 PNG 自带的 alpha。
    /// </summary>
    internal static class AvatarArt
    {
        // ── 三个表情的编号，与 /resource 里的 Avatar{0,1,2}.png 一一对应 ──
        /// <summary>平静中性（用于出错态，配降饱和 + 红环）。</summary>
        public const int Calm = 0;
        /// <summary>严肃专注（用于输出中）。</summary>
        public const int Focus = 1;
        /// <summary>柔和微笑（用于就绪 / 标题栏品牌徽章）。</summary>
        public const int Smile = 2;

        const int MoodCount = 3;
        static readonly string[] ResourceNames = { "Avatar0.png", "Avatar1.png", "Avatar2.png" };

        // 原图 192x192，命中率高且只需加载一次；懒加载避免程序启动时读三次流。
        static readonly Bitmap[] _cache = new Bitmap[MoodCount];
        static readonly bool[] _tried = new bool[MoodCount];

        /// <summary>取原始方形头像（192x192，圆外透明）。取不到返回 null。</summary>
        static Bitmap Load(int mood)
        {
            if (mood < 0 || mood >= MoodCount) { return null; }
            if (_tried[mood]) { return _cache[mood]; }
            _tried[mood] = true;

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(ResourceNames[mood]))
                {
                    if (s == null) { return null; }
                    // Bitmap 直接持有流会要求流存活，先拷贝到内存再解码。
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        ms.Position = 0;
                        // 从 MemoryStream 解码出来的 Bitmap 不依赖流本身，可以安全关闭。
                        _cache[mood] = new Bitmap(ms);
                    }
                }
            }
            catch
            {
                _cache[mood] = null;
            }
            return _cache[mood];
        }

        /// <summary>
        /// 把头像画进 rect（取短边为直径，居中）。
        /// </summary>
        /// <param name="ring">外圈颜色，null 表示不画环。</param>
        /// <param name="ringWidth">环宽（像素）。&lt;=0 表示不画。</param>
        /// <param name="desat">0=原色，1=完全去色。出错态用 0.75。</param>
        public static void Draw(Graphics g, RectangleF rect, int mood,
                                Color? ring, float ringWidth, float desat)
        {
            float d = Math.Min(rect.Width, rect.Height);
            if (d <= 0f) { return; }
            float x = rect.X + (rect.Width - d) * 0.5f;
            float y = rect.Y + (rect.Height - d) * 0.5f;

            SmoothingMode sm = g.SmoothingMode;
            InterpolationMode im2 = g.InterpolationMode;
            PixelOffsetMode po = g.PixelOffsetMode;
            CompositingQuality cq = g.CompositingQuality;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            try
            {
                Bitmap bmp = Load(mood);
                if (bmp == null)
                {
                    // 资源缺失时不要画空：给一个同色调的圆垫底，至少位置和尺寸对。
                    using (var br = new SolidBrush(Theme.AccentSoft))
                    {
                        g.FillEllipse(br, x, y, d, d);
                    }
                }
                else if (desat <= 0.001f)
                {
                    g.DrawImage(bmp, x, y, d, d);
                }
                else
                {
                    // 降饱和用 ColorMatrix 做，alpha 列原样透传，所以圆形边缘不受影响。
                    float s = 1f - Math.Max(0f, Math.Min(1f, desat));
                    float lr = 0.2126f * (1f - s), lg = 0.7152f * (1f - s), lb = 0.0722f * (1f - s);
                    ColorMatrix cm;
                    cm = new ColorMatrix(new float[][]
                    {
                        new float[] { lr + s, lr,      lr,      0f, 0f },
                        new float[] { lg,     lg + s,  lg,      0f, 0f },
                        new float[] { lb,     lb,      lb + s,  0f, 0f },
                        new float[] { 0f,     0f,      0f,      1f, 0f },
                        new float[] { 0f,     0f,      0f,      0f, 1f }
                    });
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(bmp,
                            new Rectangle((int)Math.Round(x), (int)Math.Round(y),
                                          (int)Math.Round(d), (int)Math.Round(d)),
                            0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                    }
                }

                if (ring.HasValue && ringWidth > 0.01f)
                {
                    // 环画在圆的边界上（线宽居中），外侧占一半、内侧占一半。
                    float inset = ringWidth * 0.5f;
                    using (var pen = new Pen(ring.Value, ringWidth))
                    {
                        g.DrawEllipse(pen, x + inset, y + inset, d - ringWidth, d - ringWidth);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = sm;
                g.InterpolationMode = im2;
                g.PixelOffsetMode = po;
                g.CompositingQuality = cq;
            }
        }

        /// <summary>品牌徽章：固定微笑表情、不带环。</summary>
        public static void DrawBrand(Graphics g, RectangleF rect)
        {
            Draw(g, rect, Smile, null, 0f, 0f);
        }
    }
}
