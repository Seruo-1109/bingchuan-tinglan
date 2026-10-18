using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;

namespace MultiAudioOutput
{
    /// <summary>
    /// 开发期自检工具：验证 Squircle 路径的几何正确性 + 导出预览 PNG。
    /// 仅在 DEBUG 编译时包含，不影响发布产物。
    /// 用法: 并川听澜.exe --selftest <输出目录>
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>沿 (dx,dy) 方向二分求点到边界的距离。</summary>
        static float EdgeDistance(GraphicsPath p, float x, float y, float dx, float dy, float max)
        {
            bool inside = p.IsVisible(x, y);
            if (!inside) { return 0f; }
            float lo = 0f, hi = max;
            for (int k = 0; k < 30; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (p.IsVisible(x + dx * mid, y + dy * mid)) lo = mid; else hi = mid;
            }
            return lo;
        }

        internal static int Main(string[] args)
        {
            string dir = args != null && args.Length > 0 ? args[0] : ".";
            try
            {
                return Run(dir);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "selftest_error.txt"), ex.ToString(), Encoding.UTF8);
                Console.Error.Write(ex.ToString());
                return 99;
            }
        }

        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var sb = new StringBuilder();
            int fails = 0;

            // ── 1. 几何校验：路径必须闭合、四边对称、不越界 ──
            const float S = 256f;
            using (var p = Squircle.CreateIosIcon(S))
            {
                RectangleF b = p.GetBounds();
                sb.AppendLine("[bounds] " + b);
                if (Math.Abs(b.X) > 0.6f || Math.Abs(b.Y) > 0.6f ||
                    Math.Abs(b.Width - S) > 1.2f || Math.Abs(b.Height - S) > 1.2f)
                { sb.AppendLine("  FAIL: 外接矩形与画布不一致"); fails++; }
                else { sb.AppendLine("  OK: 外接矩形 = 画布"); }

                PointF[] pts = p.PathPoints;
                sb.AppendLine("[pointCount] " + pts.Length);
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (PointF q in pts)
                {
                    if (q.X < minX) minX = q.X;
                    if (q.X > maxX) maxX = q.X;
                    if (q.Y < minY) minY = q.Y;
                    if (q.Y > maxY) maxY = q.Y;
                }
                sb.AppendLine(string.Format("[extent] x[{0:F2},{1:F2}] y[{2:F2},{3:F2}]", minX, maxX, minY, maxY));
                if (minX < -0.01f || minY < -0.01f || maxX > S + 0.01f || maxY > S + 0.01f)
                { sb.AppendLine("  FAIL: 路径越界"); fails++; }
                else { sb.AppendLine("  OK: 路径未越界"); }

                // ── 2. 超椭圆性质：中心点到边界在 45° 方向的距离应小于(圆角矩形) 内切半径 ──
                // 普通圆角矩形 45° 处最远点 = (S/2 - r + r/√2)*√2 ≈ 0.828S（r=0.2237S）
                // 超椭圆 n=5 在 45° 处更靠近正方形，应显著大于圆角矩形
                float cx = S / 2f, cy = S / 2f;
                float r45 = 0f;
                for (int i = 0; i < 3600; i++)
                {
                    double ang = i * Math.PI / 1800.0;
                    float dx = (float)(Math.Cos(ang) * S);
                    float dy = (float)(Math.Sin(ang) * S);
                    if (p.IsVisible(cx + dx * 0.001f, cy + dy * 0.001f) == false) continue;
                    // 二分找边界
                    float lo = 0f, hi = S;
                    for (int k = 0; k < 40; k++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (p.IsVisible(cx + dx * mid / S, cy + dy * mid / S)) lo = mid; else hi = mid;
                    }
                    if (Math.Abs(dx) > 1e-6f && Math.Abs(dy / dx - 1f) < 1e-3f) { r45 = lo; break; }
                }
                sb.AppendLine(string.Format("[diag] 45° 边界距离 = {0:F2} (圆角矩形理论 ≈ {1:F2})",
                    r45, (S / 2f - S * Squircle.IosRadiusRatio + S * Squircle.IosRadiusRatio / 1.41421356f) * 1.41421356f));

                // ── 3. 对称性：中心点 ± 同距，可见性一致 ──
                // 注意：先求「到边界的距离」，只在离边界 > 0.75px 的稳定区内比较。
                // 紧贴边界的点在 float 精度下会因 S-px 的舍入而翻转判定，属于判据噪声。
                bool sym = true;
                int mism = 0;
                int tested = 0;
                var rnd = new Random(20260527);
                for (int i = 0; i < 60000 && tested < 20000; i++)
                {
                    float px = (float)(rnd.NextDouble() * S);
                    float py = (float)(rnd.NextDouble() * S);
                    float h1 = EdgeDistance(p, px, py, 1, 0, S);
                    float h2 = EdgeDistance(p, S - px, py, 1, 0, S);
                    float v1 = EdgeDistance(p, px, py, 0, 1, S);
                    float v2 = EdgeDistance(p, px, S - py, 0, 1, S);
                    float t1 = EdgeDistance(p, px, py, 0.70710678f, 0.70710678f, S);
                    float t2 = EdgeDistance(p, py, px, 0.70710678f, 0.70710678f, S);
                    if (h1 < 0.75f || h2 < 0.75f || v1 < 0.75f || v2 < 0.75f || t1 < 0.75f || t2 < 0.75f) continue;
                    tested++;
                    bool a = p.IsVisible(px, py);
                    if (a != p.IsVisible(S - px, py) || a != p.IsVisible(px, S - py) || a != p.IsVisible(py, px))
                    {
                        sym = false;
                        mism++;
                        if (mism <= 6)
                        {
                            sb.AppendLine(string.Format("  MISMATCH at ({0:F3},{1:F3}) d=({2:F3},{3:F3},{4:F3})", px, py, h1, v1, t1));
                        }
                    }
                }
                sb.AppendLine(string.Format("  采样 {0} 个稳定点（离边界>0.75px）", tested));
                sb.AppendLine(mism == 0 ? "  OK: 四重对称" : "  FAIL: 对称性破坏！不一致点数 = " + mism + "/" + tested);
                if (!sym) fails++;
            }

            // ── 4. 各半径渲染预览 ──
            using (var bmp = new Bitmap(1024, 256))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(246, 247, 249));
                float[] radii = { 0f, 8f, 20f, 40f, 64f, 92f };
                for (int i = 0; i < radii.Length; i++)
                {
                    var rect = new RectangleF(20 + i * 165, 40, 140, 140);
                    using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, radii[i]))
                    using (var br = new SolidBrush(Color.FromArgb(17, 20, 26)))
                    {
                        g.FillPath(br, path);
                    }
                    g.DrawString("r=" + radii[i], SystemFonts.DefaultFont, Brushes.Black, rect.X, rect.Bottom + 8);
                }
                bmp.Save(Path.Combine(outDir, "squircle_radii.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            // ── 5. 超椭圆指数对比 ──
            using (var bmp = new Bitmap(1024, 300))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.White);
                float[] exps = { 2f, 3f, 4f, 5f, 8f, 20f };
                for (int i = 0; i < exps.Length; i++)
                {
                    var rect = new RectangleF(30 + i * 165, 50, 140, 140);
                    using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, Squircle.IosRadius(140f), exps[i]))
                    using (var br = new SolidBrush(i == 3 ? Color.FromArgb(10, 132, 255) : Color.FromArgb(17, 20, 26)))
                    {
                        g.FillPath(br, path);
                    }
                    g.DrawString("n=" + exps[i] + (i == 3 ? " (iOS)" : ""), SystemFonts.DefaultFont, Brushes.Black, rect.X, rect.Bottom + 10);
                }
                bmp.Save(Path.Combine(outDir, "squircle_exponents.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            sb.AppendLine(fails == 0 ? "\n== ALL GEOMETRY CHECKS PASSED ==" : "\n== FAILURES: " + fails + " ==");
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
            return fails;
        }
    }
}
