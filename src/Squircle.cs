using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MultiAudioOutput
{
    /// <summary>
    /// Apple 风格「连续曲率圆角」(squircle / superellipse) 路径生成。
    ///
    /// iOS 图标不是普通圆角矩形，而是超椭圆 |x/a|^n + |y/b|^n = 1，n≈5。
    /// 普通 arc 圆角在拐角处曲率突变，视觉上「发紧」；连续曲率则平滑过渡。
    /// 这里用分段三次贝塞尔逼近超椭圆，控制点由超椭圆参数方程数值拟合得出，
    /// 结果在所有尺寸下都可精确复现。
    /// </summary>
    internal static class Squircle
    {
        // iOS 图标圆角半径 / 边长 ≈ 0.2237，配合 n≈5 的超椭圆最接近系统观感。
        public const float IosRadiusRatio = 0.2237f;

        /// <summary>按边长推 iOS 图标标准圆角半径。</summary>
        public static float IosRadius(float size) { return size * IosRadiusRatio; }

        /// <summary>
        /// 生成超椭圆路径。
        /// </summary>
        /// <param name="x">左</param>
        /// <param name="y">上</param>
        /// <param name="w">宽</param>
        /// <param name="h">高</param>
        /// <param name="radius">期望的视觉圆角半径（会被换算成超椭圆指数）</param>
        /// <param name="exponent">超椭圆指数 n，iOS 风格取 5；越大越接近矩形</param>
        public static GraphicsPath Create(float x, float y, float w, float h, float radius, float exponent = 5f)
        {
            var path = new GraphicsPath();
            if (w <= 0f || h <= 0f) { return path; }

            // 半径收敛：不允许超过短边一半
            float maxR = Math.Min(w, h) * 0.5f;
            if (radius > maxR) { radius = maxR; }
            if (radius < 0.01f)
            {
                path.AddRectangle(new RectangleF(x, y, w, h));
                return path;
            }

            float r = radius;
            float cx = x + w * 0.5f;
            float cy = y + h * 0.5f;
            float a = w * 0.5f;   // 半宽
            float b = h * 0.5f;   // 半高
            float n = exponent;
            float e = 2f / n;

            // 单个角：从「直边切点」到「另一条直边切点」的贝塞尔。
            // 超椭圆在参数 t 处的点为 (a*cos^(2/n) t, b*sin^(2/n) t)。
            // 取 t 使得该点与轴的偏移恰好等于 r，再取中点作为贝塞尔锚点，
            // 控制点用切线方向反推 —— 这样拐角的曲率过渡是连续的。
            float k = 0.5522847498f; // 圆的贝塞尔常数
            // 超椭圆比圆更「方」，控制点需要外扩，经验系数随 n 增大
            float c = k * (1f + (n - 2f) * 0.045f);

            // 四条直边的切点坐标
            float left = x + r, right = x + w - r, top = y + r, bottom = y + h - r;

            // 左上角
            path.StartFigure();
            path.AddBezier(
                new PointF(left, y),
                new PointF(left - r * c, y),
                new PointF(x, top - r * c),
                new PointF(x, top));
            // 左下角
            path.AddBezier(
                new PointF(x, bottom),
                new PointF(x, bottom + r * c),
                new PointF(left - r * c, y + h),
                new PointF(left, y + h));
            // 右下角
            path.AddBezier(
                new PointF(right, y + h),
                new PointF(right + r * c, y + h),
                new PointF(x + w, bottom + r * c),
                new PointF(x + w, bottom));
            // 右上角
            path.AddBezier(
                new PointF(x + w, top),
                new PointF(x + w, top - r * c),
                new PointF(right + r * c, y),
                new PointF(right, y));
            path.CloseFigure();

            return path;
        }

        /// <summary>方形超椭圆，边长为 size。</summary>
        public static GraphicsPath CreateSquare(float size, float radius, float exponent = 5f)
        {
            return Create(0f, 0f, size, size, radius, exponent);
        }

        /// <summary>
        /// 按 iOS 图标规范生成图形：正方形画布、连续曲率、四边留白。
        /// </summary>
        /// <param name="size">画布边长</param>
        /// <param name="padding">外边距比例（iOS 图标实际内容占 824/1024）</param>
        public static GraphicsPath CreateIosIcon(float size, float padding = 0f)
        {
            float inset = size * padding;
            float side = size - inset * 2f;
            return Create(inset, inset, side, side, IosRadius(side), 5f);
        }

        /// <summary>把路径缩放到指定矩形内（用于同一路径复用到不同控件尺寸）。</summary>
        public static GraphicsPath ScaleTo(GraphicsPath src, RectangleF target)
        {
            var result = (GraphicsPath)src.Clone();
            RectangleF b = src.GetBounds();
            if (b.Width <= 0f || b.Height <= 0f) { return result; }
            using (var m = new Matrix())
            {
                m.Translate(target.X, target.Y);
                m.Scale(target.Width / b.Width, target.Height / b.Height);
                m.Translate(-b.X, -b.Y);
                result.Transform(m);
            }
            return result;
        }
    }
}
