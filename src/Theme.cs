using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// 设计令牌：颜色 / 字体 / 圆角 / 间距。
    /// 全部集中在这里，改一处即可整体换肤。画笔字体静态缓存，全程不释放（进程级）。
    /// </summary>
    internal static class Theme
    {
        // ───────────────────────── 色板（黑白灰为主，蓝仅作点缀）─────────────────────────
        public static readonly Color White = Color.FromArgb(255, 255, 255);
        public static readonly Color Canvas = Color.FromArgb(246, 247, 249);  // 窗口底
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);  // 卡片
        public static readonly Color SurfaceAlt = Color.FromArgb(241, 243, 246);  // 次级底面

        // 对比度基线（WCAG 2.1，最不利底色 = Canvas #F6F7F9）：
        //   正文类文字 ≥ 4.5:1；图形/指示器 ≥ 3.0:1。
        //   每个令牌后面的数字是最差底色下的实测对比度，改动请同步更新 tests/audit_contrast.ps1。
        public static readonly Color Ink = Color.FromArgb(17, 20, 26);   // 主文字（近黑）      17.2:1
        public static readonly Color InkMid = Color.FromArgb(95, 103, 117);  // 次要文字        5.32:1
        public static readonly Color InkFaint = Color.FromArgb(108, 114, 123);  // 弱化文字     4.52:1
        public static readonly Color Line = Color.FromArgb(228, 231, 236);  // 分隔线（图形，1.16:1 仅装饰）
        public static readonly Color LineSoft = Color.FromArgb(240, 242, 245);  // 浅分隔（装饰）

        // 主色 = 角色设定图上的「克莱因蓝」#0174FD。原样拿来白字只有 4.27:1，
        // 达不到 4.5，所以压深一档到 #0069F0：肉眼几乎看不出差别，但白字 4.91:1、
        // 落在 Canvas 上 4.58:1，填充和描边两种用法都合规。
        public static readonly Color Accent = Color.FromArgb(0, 105, 240);  // 克莱因蓝，填充用（白字 4.91:1）
        public static readonly Color AccentDark = Color.FromArgb(0, 84, 204);  // 强调色文字（最差 5.92:1）
        public static readonly Color AccentSoft = Color.FromArgb(232, 242, 255);

        public static readonly Color Danger = Color.FromArgb(214, 48, 38);  // 危险色（填充+文字）4.55:1
        public static readonly Color DangerSoft = Color.FromArgb(255, 235, 233);
        public static readonly Color Success = Color.FromArgb(34, 158, 66);  // 成功色（图形）3.10:1
        public static readonly Color Warn = Color.FromArgb(166, 90, 0);  // 警告色（文字+图形）4.80:1

        public static readonly Color Track = Color.FromArgb(229, 231, 235);  // 开关关闭轨道
        public static readonly Color Disabled = Color.FromArgb(196, 202, 210);  // 禁用图形（WCAG 豁免）
        public static readonly Color DisabledBg = Color.FromArgb(238, 240, 243);
        public static readonly Color DisabledText = Color.FromArgb(150, 158, 170);  // 禁用文字（WCAG 豁免）

        // ───────────────────────── 几何（96 DPI 基准，运行时乘 Scale）─────────────────────────
        //
        // 这些原本是 const float。改成静态属性是为了让它们跟随 DPI：
        // 声明 permonitorv2 之后 Windows 不再替我们放大任何东西，圆角、边距也必须自己乘。
        // 注意它们是「调用时求值」，所以调用点必须在 Theme.SetDpi() 之后才读它们
        // （MainForm 的 BuildUi 正是如此）。
        public static float RadiusWindow { get { return S(14f); } }   // 窗口整体圆角
        // 卡片圆角从 14 提到 18：卡片宽 436、高 280~336，14 在这么宽的矩形上
        // 显得「只磨了个小角」，与 iOS 分组列表的圆润感差一档。
        // 18 在 96 DPI 下约占短边的 6.4%，接近 iOS Inset Grouped 列表的观感。
        public static float RadiusCard { get { return S(18f); } }     // 卡片
        public static float RadiusBtn { get { return S(11f); } }      // 按钮
        public static float PadX { get { return S(22f); } }           // 左右安全边距
        public static float GapSec { get { return S(14f); } }         // 分组间距

        // ───────────────────────── 字体 ─────────────────────────
        /// <summary>中文字体优先级：雅黑 → 思源 → 苹方 → 系统默认。</summary>
        public static readonly string Family = PickFamily();

        public static readonly Font FTitle = new Font(Family, 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FSec = new Font(Family, 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FName = new Font(Family, 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font FSmall = new Font(Family, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font FBtn = new Font(Family, 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FVal = new Font(Family, 11f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FStatus = new Font(Family, 9f, FontStyle.Regular, GraphicsUnit.Point);

        /// <summary>
        /// 全局文字渲染方式：灰度抗锯齿（AntiAliasGridFit），刻意不用 ClearType。
        ///
        /// ClearType 靠 RGB 子像素条纹提高「感知清晰度」，代价是笔画边缘出现橙/蓝/青的
        /// 彩色描边。本界面底色是近白（#F6F7F9 / #FFFFFF），彩边对比度被放大得特别明显：
        /// 实测「30 ms」区域 21 种颜色里 19 种是彩色（#B2F7F9 青 / #11146E 深蓝 /
        /// #B26B19 橙 / #691419 暗红 …），设备名与分组标题同样如此。
        ///
        /// 换灰度 AA 后同一段文字只剩 16 种颜色、全部是灰阶，且「实心墨点」从 107 增到
        /// 120（更实、更干净），见 tests/sepvariants 的对照。iOS / macOS 从不用子像素
        /// 彩色抗锯齿，这也是「类 iOS 简约风」应有的观感。
        ///
        /// 注意：这个令牌只管 GDI+ 绘制（Graphics.DrawString）。标准 WinForms Label 走
        /// GDI（TextRenderer），不受它影响 —— 所以 MainForm 里的 Label 已全部换成
        /// UiKit.SkinLabel，那条路径也统一到灰度 AA。
        /// </summary>
        public static readonly TextRenderingHint TextHint = TextRenderingHint.AntiAliasGridFit;

        public static readonly StringFormat Center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        public static readonly StringFormat Left = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        public static readonly StringFormat Right = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        static string PickFamily()
        {
            // 逐个探测已安装字体族，避免在不存在的字体上回退成宋体（很难看）
            string[] prefer = { "Microsoft YaHei UI", "Microsoft YaHei", "Source Han Sans SC",
                                "PingFang SC", "Noto Sans SC", "Segoe UI" };
            foreach (string name in prefer)
            {
                try
                {
                    using (var f = new FontFamily(name)) { return name; }
                }
                catch { /* 未安装，继续 */ }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        /// <summary>混色，t=0 取 a，t=1 取 b。</summary>
        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ───────────────────────── DPI 缩放 ─────────────────────────
        //
        // 设计基准是 96 DPI（100% 缩放）。app.manifest 声明了 permonitorv2，
        // 进程不再被 Windows 当位图拉伸，因此所有"盒子"尺寸必须自己乘缩放系数。
        //
        // 为什么不用 Graphics.DpiX：
        //   tests\dpiprobe 实测 g.DpiX 恒为 96 —— 即使进程已声明 DPI 感知，
        //   在 100% 缩放的机器上它当然也是 96，但更关键的是它反映的是"绘制表面"
        //   的 DPI，而不是"窗口所在的显示器"的 DPI。窗口被拖到另一块不同缩放的
        //   显示器时，它不会及时更新。真正可靠的来源是 Control.DeviceDpi
        //   （WinForms 在 WM_DPICHANGED 时更新），由 MainForm 推给 SetDpi。
        //
        // 字体不需要在这里缩放：Theme 的字体全部用 GraphicsUnit.Point，
        // GDI+ 会按 DPI 自动放大。所以只需缩放盒子，文字会自己跟上。
        // 反过来说，盒子不缩放就会导致文字溢出 —— 这才是 A 步的核心。

        static int _dpi = 96;

        /// <summary>当前生效的 DPI（设计基准 96）。</summary>
        public static int CurrentDpi { get { return _dpi; } }

        /// <summary>当前缩放系数，96 DPI 时为 1.0。</summary>
        public static float Scale { get { return _dpi / 96f; } }

        /// <summary>设置当前 DPI。超出合理范围的值被忽略，避免布局被脏数据搞崩。</summary>
        public static void SetDpi(int dpi)
        {
            if (dpi >= 48 && dpi <= 480) { _dpi = dpi; }
        }

        /// <summary>按当前 DPI 缩放一个整数值（尺寸、间距、圆角）。</summary>
        public static int S(int v) { return (int)Math.Round(v * Scale, MidpointRounding.AwayFromZero); }

        /// <summary>按当前 DPI 缩放一个浮点值。</summary>
        public static float S(float v) { return v * Scale; }

        /// <summary>
        /// 已废弃：依赖 Graphics.DpiX 做缩放。
        ///
        /// 保留它只是为了让老调用点编译得过，新代码一律用 S()。
        /// 它的问题在于 g.DpiX 反映的是绘制表面而不是显示器，窗口跨屏拖动时不会更新；
        /// 而且全项目调用点本来就是 0 处（实测），一直是死代码。
        /// </summary>
        [Obsolete("Use Theme.S() instead: Graphics.DpiX does not track the window's monitor.")]
        public static float Dpi(Graphics g, float v)
        {
            try { return v * (g.DpiX / 96f); }
            catch { return v; }
        }
    }
}
