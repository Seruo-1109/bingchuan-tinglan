using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;

namespace MultiAudioOutput
{
    /// <summary>
    /// 并川听澜 —— 应用图标设计工作台。
    ///
    /// 一次生成 10 版黑白极简图标：
    ///   · 512×512 PNG（预览）  · 256×256 PNG  · 多尺寸 .ico（16/24/32/48/64/128/256，PNG 压缩）
    /// 外加：_overview.png 总览对比图、_legibility.png 小尺寸可辨识度表、_designs.md 设计说明。
    ///
    /// 外形一律复用 <see cref="Squircle.CreateIosIcon"/>（iOS 连续曲率超椭圆，半径比 0.2237），
    /// 因此圆角在任意尺寸下都是平滑过渡，而不是生硬的直角或正圆。
    ///
    /// 渲染管线：按目标尺寸做 2×/4× 超采样光栅化，再逐级折半降采样（HighQualityBicubic），
    /// 既保留 GDI+ 抗锯齿质量，又允许每个设计按目标尺寸做「光学补偿」（细线在小尺寸加粗）。
    ///
    /// 编译（.NET Framework 4.0 csc / C# 5）：
    ///   csc /nologo /target:exe /out:IconStudio.exe /reference:System.dll /reference:System.Drawing.dll
    ///       src\Squircle.cs src\IconStudio.cs
    /// 用法：
    ///   IconStudio.exe [输出目录]            生成全部图标
    ///   IconStudio.exe verify [输出目录]     校验产物的 ICO 结构与 PNG 内容
    /// </summary>
    internal static class IconStudio
    {
        // ═══════════════════════ 设计令牌 ═══════════════════════

        /// <summary>设计坐标系边长。所有几何都按 1024 归一化书写，渲染时统一缩放。</summary>
        const float U = 1024f;

        static readonly Color Ink      = Color.FromArgb(255, 17, 20, 26);    // 主黑（近黑，同 Theme.Ink）
        static readonly Color InkSoft  = Color.FromArgb(255, 46, 52, 62);    // 次黑
        // 灰阶一律 R=G=B（纯中性灰）。若给灰加蓝偏，图标的「带色像素」占比会飙升，
        // 违反「白黑为主」的硬要求，也会在小尺寸下显出脏蓝。
        static readonly Color GrayDeep = Color.FromArgb(255, 85, 85, 85);    // 深灰（层叠最远层）
        static readonly Color GrayMid  = Color.FromArgb(255, 152, 152, 152); // 中灰
        static readonly Color GrayLite = Color.FromArgb(255, 208, 208, 208); // 浅灰
        static readonly Color White    = Color.FromArgb(255, 255, 255);
        static readonly Color Sheet    = Color.FromArgb(255, 237, 239, 242); // 对比图底板
        static readonly Color SheetInk = Color.FromArgb(255, 26, 30, 38);    // 对比图文字
        static readonly Color SheetDim = Color.FromArgb(255, 122, 130, 142); // 对比图次要文字

        static readonly int[] IcoSizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

        // ═══════════════════════ 数据结构 ═══════════════════════

        /// <summary>单个设计方向的绘制回调。s 固定为 1024；target 是最终像素尺寸，用于光学补偿。</summary>
        delegate void DrawProc(Graphics g, float s, int target);

        sealed class Design
        {
            public string Slug;    // 文件名前缀
            public string Name;    // 英文名
            public string NameCn;  // 中文名
            public string Idea;    // 设计理念
            public string Scene;   // 适用场景
            public DrawProc Draw;
        }

        // ═══════════════════════ 入口 ═══════════════════════

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { /* 控制台不支持 UTF-8 时忽略，日志文件仍然是 UTF-8 */ }

            string mode = "build";
            string outDir = Path.Combine("design", "icons");
            if (args.Length >= 1 && string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
            {
                mode = "verify";
                if (args.Length >= 2) { outDir = args[1]; }
            }
            else if (args.Length >= 1 && string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase))
            {
                mode = "preview";
                if (args.Length >= 2) { outDir = args[1]; }
            }
            else if (args.Length >= 1)
            {
                outDir = args[0];
            }

            outDir = Path.GetFullPath(outDir);
            try
            {
                if (mode == "verify") { return Verify(outDir); }
                if (mode == "preview") { return Preview(outDir); }
                return Build(outDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex.ToString());
                return 1;
            }
        }

        // ═══════════════════════ 生成 ═══════════════════════

        static int Build(string outDir)
        {
            Directory.CreateDirectory(outDir);
            List<string> log = new List<string>();
            List<Design> designs = BuildDesigns();

            log.Add("并川听澜 — IconStudio");
            log.Add("输出目录: " + outDir);
            log.Add("设计数: " + designs.Count.ToString());
            log.Add("");

            List<Bitmap> thumbs = new List<Bitmap>();

            foreach (Design d in designs)
            {
                Bitmap b512 = RenderAt(d, 512);
                Bitmap b256 = RenderAt(d, 256);

                string p512 = Path.Combine(outDir, d.Slug + ".png");
                string p256 = Path.Combine(outDir, d.Slug + "_256.png");
                string pico = Path.Combine(outDir, d.Slug + ".ico");

                SavePng(b512, p512);
                SavePng(b256, p256);

                List<Bitmap> frames = new List<Bitmap>();
                for (int i = 0; i < IcoSizes.Length; i++) { frames.Add(RenderAt(d, IcoSizes[i])); }
                WriteIco(pico, frames);
                for (int i = 0; i < frames.Count; i++) { frames[i].Dispose(); }

                thumbs.Add(b256);   // 交给总览图使用，不释放

                log.Add(string.Format("{0,-16} {1,-10} 512={2,7} B   256={3,7} B   ico={4,7} B",
                    d.Slug,
                    "[" + d.NameCn + "]",
                    new FileInfo(p512).Length,
                    new FileInfo(p256).Length,
                    new FileInfo(pico).Length));

                b512.Dispose();
            }

            log.Add("");
            string overview = Path.Combine(outDir, "_overview.png");
            Bitmap ov = MakeOverview(designs, thumbs);
            SavePng(ov, overview);
            ov.Dispose();
            log.Add(string.Format("{0,-16} {1,-10} {2}", "_overview.png", "[总览对比]", new FileInfo(overview).Length + " B"));

            string legib = Path.Combine(outDir, "_legibility.png");
            Bitmap lg = MakeLegibility(designs);
            SavePng(lg, legib);
            lg.Dispose();
            log.Add(string.Format("{0,-16} {1,-10} {2}", "_legibility.png", "[小尺寸表]", new FileInfo(legib).Length + " B"));

            for (int i = 0; i < thumbs.Count; i++) { thumbs[i].Dispose(); }

            string md = Path.Combine(outDir, "_designs.md");
            File.WriteAllText(md, MakeDesignDoc(designs), new UTF8Encoding(false));
            log.Add(string.Format("{0,-16} {1,-10} {2}", "_designs.md", "[设计说明]", new FileInfo(md).Length + " B"));

            log.Add("");
            log.Add("OK — 全部产物已生成。");

            string logPath = Path.Combine(outDir, "_build.log");
            File.WriteAllText(logPath, string.Join(Environment.NewLine, log.ToArray()) + Environment.NewLine, new UTF8Encoding(false));

            for (int i = 0; i < log.Count; i++) { Console.WriteLine(log[i]); }
            return 0;
        }

        // ═══════════════════════ 渲染管线 ═══════════════════════

        static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;
        }

        /// <summary>按最终像素尺寸渲染：2×/4× 超采样 → 逐级折半降采样。</summary>
        static Bitmap RenderAt(Design d, int size)
        {
            int ss = size <= 128 ? 4 : 2;
            Bitmap cur = RenderMaster(d, size, size * ss);
            while (cur.Width / 2 >= size)
            {
                Bitmap next = HalfSize(cur);
                cur.Dispose();
                cur = next;
            }
            if (cur.Width != size)
            {
                Bitmap fin = ScaleTo(cur, size);
                cur.Dispose();
                cur = fin;
            }
            return cur;
        }

        static Bitmap RenderMaster(Design d, int target, int res)
        {
            Bitmap bmp = new Bitmap(res, res, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Setup(g);
                float k = (float)res / U;
                g.ScaleTransform(k, k);
                d.Draw(g, U, target);
            }
            return bmp;
        }

        static Bitmap HalfSize(Bitmap src)
        {
            int n = src.Width / 2;
            Bitmap dst = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            dst.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(dst))
            {
                Setup(g);
                g.DrawImage(src, new Rectangle(0, 0, n, n), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }
            return dst;
        }

        static Bitmap ScaleTo(Bitmap src, int n)
        {
            Bitmap dst = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            dst.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(dst))
            {
                Setup(g);
                g.DrawImage(src, new Rectangle(0, 0, n, n), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }
            return dst;
        }

        static void SavePng(Bitmap b, string path)
        {
            b.Save(path, ImageFormat.Png);
        }

        // ═══════════════════════ 绘制原语 ═══════════════════════

        /// <summary>填充图标外形（iOS 连续曲率超椭圆）并把后续绘制裁剪在形状内。</summary>
        static void FillBody(Graphics g, float s, Color c)
        {
            GraphicsPath body = Squircle.CreateIosIcon(s);
            using (SolidBrush b = new SolidBrush(c)) { g.FillPath(b, body); }
            // 用 Region 承接裁剪：Region 会复制路径数据，随后即可安全释放 body，
            // 避免依赖 GraphicsPath 在 SetClip 之后的生存期（GDI+ 未对此作保证）。
            using (Region clip = new Region(body))
            {
                g.SetClip(clip, CombineMode.Replace);
            }
            body.Dispose();
        }

        static void FillCircle(Graphics g, float cx, float cy, float r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) { g.FillEllipse(b, cx - r, cy - r, r * 2f, r * 2f); }
        }

        /// <summary>圆角矩形（超椭圆，与外形同一套圆角语言）。</summary>
        static void FillRoundRect(Graphics g, float x, float y, float w, float h, float r, Color c)
        {
            using (GraphicsPath p = Squircle.Create(x, y, w, h, r, 5f))
            using (SolidBrush b = new SolidBrush(c)) { g.FillPath(b, p); }
        }

        /// <summary>胶囊形（真正的半圆端帽），用于波形条与电平条。</summary>
        static void FillCapsule(Graphics g, float x, float y, float w, float h, Color c)
        {
            if (w <= 0f || h <= 0f) { return; }
            using (GraphicsPath p = new GraphicsPath())
            {
                if (w >= h)
                {
                    p.AddArc(x, y, h, h, 90f, 180f);
                    p.AddArc(x + w - h, y, h, h, 270f, 180f);
                }
                else
                {
                    p.AddArc(x, y, w, w, 180f, 180f);
                    p.AddArc(x, y + h - w, w, w, 0f, 180f);
                }
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(c)) { g.FillPath(b, p); }
            }
        }

        static void DrawRing(Graphics g, float cx, float cy, float r, float w, Color c)
        {
            using (Pen p = new Pen(c, w)) { g.DrawEllipse(p, cx - r, cy - r, r * 2f, r * 2f); }
        }

        static Pen RoundPen(float w, Color c)
        {
            Pen p = new Pen(c, w);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        static Color Alpha(Color c, float a)
        {
            int v = (int)Math.Round(a * 255f);
            if (v < 0) { v = 0; }
            if (v > 255) { v = 255; }
            return Color.FromArgb(v, c.R, c.G, c.B);
        }

        /// <summary>光学补偿：同一个设计在小尺寸下需要更粗的笔画，否则线条会消失。</summary>
        static float Hinted(float baseFrac, int target, float tinyFrac, float smallFrac)
        {
            if (target <= 24) { return tinyFrac; }
            if (target <= 48) { return smallFrac; }
            return baseFrac;
        }

        // ═══════════════════════ 十个设计方向 ═══════════════════════

        // ── v1 波形条：五根圆头竖条构成对称振幅包络 ──────────────────────
        static void DrawV1(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float[] hf = { 0.24f, 0.46f, 0.74f, 0.46f, 0.24f };
            float bw = 0.098f * s;
            float gap = 0.062f * s;
            int n = hf.Length;
            float total = n * bw + (n - 1) * gap;
            float x = (s - total) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float bh = hf[i] * s;
                FillCapsule(g, x, (s - bh) * 0.5f, bw, bh, White);
                x += bw + gap;
            }
        }

        // ── v2 一分多：一个音源扇出到三个输出节点 ───────────────────────
        static void DrawV2(Graphics g, float s, int target)
        {
            FillBody(g, s, White);

            float cx = 0.285f * s;
            float cy = 0.500f * s;
            float nx = 0.700f * s;
            float ns = 0.185f * s;
            float[] ny = { 0.255f, 0.500f, 0.745f };
            float pw = Hinted(0.062f, target, 0.085f, 0.072f) * s;

            using (Pen p = RoundPen(pw, Ink))
            {
                for (int i = 0; i < ny.Length; i++)
                {
                    float tx = nx - ns * 0.5f;
                    float ty = ny[i] * s;
                    float dx = tx - cx;
                    float dy = ty - cy;
                    g.DrawBezier(p,
                        cx, cy,
                        cx + dx * 0.42f, cy + dy * 0.02f,
                        cx + dx * 0.72f, cy + dy * 0.98f,
                        tx, ty);
                }
            }

            FillCircle(g, cx, cy, 0.112f * s, Ink);
            for (int i = 0; i < ny.Length; i++)
            {
                FillRoundRect(g, nx - ns * 0.5f, ny[i] * s - ns * 0.5f, ns, ns, ns * 0.30f, Ink);
            }
        }

        // ── v3 声波扩散：同心圆环由内向外灰阶递减 ───────────────────────
        static void DrawV3(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float cx = s * 0.5f;
            float cy = s * 0.5f;
            DrawRing(g, cx, cy, 0.347f * s, 0.042f * s, Alpha(White, 0.32f));
            DrawRing(g, cx, cy, 0.249f * s, 0.050f * s, Alpha(White, 0.60f));
            DrawRing(g, cx, cy, 0.142f * s, 0.060f * s, White);
            FillCircle(g, cx, cy, 0.060f * s, White);
        }

        // ── v4 设备层叠：三张超椭圆卡片沿对角线层叠 ─────────────────────
        static void DrawV4(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float card = 0.380f * s;
            float off = 0.115f * s;
            float r = 0.105f * s;
            // 整组几何跨度 = card + 2*off，居中摆放
            float fx = (s - (card + 2f * off)) * 0.5f + card * 0.5f;   // 最前（左下）
            float fy = s - fx;

            FillRoundRect(g, fx + 2f * off - card * 0.5f, fy - 2f * off - card * 0.5f, card, card, r, GrayDeep);
            FillRoundRect(g, fx + off - card * 0.5f, fy - off - card * 0.5f, card, card, r, GrayMid);
            FillRoundRect(g, fx - card * 0.5f, fy - card * 0.5f, card, card, r, White);
        }

        // ── v5 字母 M：四段圆头粗笔画几何构造 ───────────────────────────
        static void DrawV5(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float w = 0.520f * s;
            float h = 0.460f * s;
            float pw = 0.108f * s;
            float cx = s * 0.5f;
            float xL = cx - w * 0.5f + pw * 0.5f;
            float xR = cx + w * 0.5f - pw * 0.5f;
            float yTop = (s - h) * 0.5f;
            float yBot = yTop + h;
            float yMid = yBot - h * 0.360f;

            using (Pen p = RoundPen(pw, White))
            {
                g.DrawLine(p, xL, yBot, xL, yTop);
                g.DrawLine(p, xL, yTop, cx, yMid);
                g.DrawLine(p, cx, yMid, xR, yTop);
                g.DrawLine(p, xR, yTop, xR, yBot);
            }
        }

        // ── v6 正负形：白色圆盘内挖出黑色声柱 ───────────────────────────
        static void DrawV6(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            FillCircle(g, s * 0.5f, s * 0.5f, 0.345f * s, White);

            float[] hf = { 0.240f, 0.420f, 0.240f };
            float bw = 0.084f * s;
            float gap = 0.054f * s;
            int n = hf.Length;
            float total = n * bw + (n - 1) * gap;
            float x = (s - total) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float bh = hf[i] * s;
                FillCapsule(g, x, (s - bh) * 0.5f, bw, bh, Ink);
                x += bw + gap;
            }
        }

        // ── v7 线条极简：细线信号分叉树 ─────────────────────────────────
        static void DrawV7(Graphics g, float s, int target)
        {
            FillBody(g, s, White);
            float pw = Hinted(0.050f, target, 0.075f, 0.062f) * s;
            float x0 = 0.500f * s;
            float y0 = 0.225f * s;
            float y1 = 0.415f * s;
            float y2 = 0.735f * s;
            float[] ex = { 0.255f, 0.500f, 0.745f };
            float dot = Hinted(0.072f, target, 0.095f, 0.082f) * s;

            using (Pen p = RoundPen(pw, Ink))
            {
                g.DrawLine(p, x0, y0, x0, y1);
                for (int i = 0; i < ex.Length; i++)
                {
                    float tx = ex[i] * s;
                    float dx = tx - x0;
                    float dy = y2 - y1;
                    g.DrawBezier(p,
                        x0, y1,
                        x0 + dx * 0.08f, y1 + dy * 0.52f,
                        tx, y1 + dy * 0.52f - dy * 0.02f,
                        tx, y2);
                }
            }
            for (int i = 0; i < ex.Length; i++)
            {
                FillCircle(g, ex[i] * s, y2, dot, Ink);
            }
        }

        // ── v8 几何分割：实心圆被十字缝隙切成四个扇形 ───────────────────
        static void DrawV8(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float cx = s * 0.5f;
            float cy = s * 0.5f;
            float r = 0.345f * s;
            float gw = 0.086f * s;

            FillCircle(g, cx, cy, r, White);

            using (SolidBrush b = new SolidBrush(Ink))
            {
                g.FillRectangle(b, cx - r, cy - gw * 0.5f, r * 2f, gw);
                g.FillRectangle(b, cx - gw * 0.5f, cy - r, gw, r * 2f);
            }
        }

        // ── v9 电平条：三条水平圆头条逐级递减 ───────────────────────────
        static void DrawV9(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float bh = 0.108f * s;
            float gap = 0.075f * s;
            float[] wf = { 0.660f, 0.460f, 0.270f };
            Color[] cs = { White, White, GrayMid };
            int n = wf.Length;
            float total = n * bh + (n - 1) * gap;
            float y = (s - total) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float bw = wf[i] * s;
                FillCapsule(g, (s - bw) * 0.5f, y, bw, bh, cs[i]);
                y += bh + gap;
            }
        }

        // ── v10 辐射枢纽：中心节点 + 四条对角线 ─────────────────────────
        static void DrawV10(Graphics g, float s, int target)
        {
            FillBody(g, s, Ink);
            float cx = s * 0.5f;
            float cy = s * 0.5f;
            float armW = 0.105f * s;
            float inner = 0.095f * s;
            float outer = 0.305f * s;

            using (Pen p = RoundPen(armW, White))
            {
                for (int i = 0; i < 4; i++)
                {
                    double ang = Math.PI * 0.25 + i * Math.PI * 0.5;
                    float ux = (float)Math.Cos(ang);
                    float uy = (float)Math.Sin(ang);
                    g.DrawLine(p, cx + ux * inner, cy + uy * inner, cx + ux * outer, cy + uy * outer);
                }
            }
            FillCircle(g, cx, cy, 0.095f * s, White);
        }

        // ═══════════════════════ 设计清单 ═══════════════════════

        static List<Design> BuildDesigns()
        {
            List<Design> list = new List<Design>();

            list.Add(new Design
            {
                Slug = "v1_waveform", Name = "Waveform", NameCn = "波形条", Draw = DrawV1,
                Idea = "5 根圆头竖条构成对称振幅包络，中间最高、两侧递减 —— 「声音」最无歧义的几何转译。",
                Scene = "小尺寸场景首选（任务栏 / 开始菜单 / 文件关联图标），任何分辨率下都读得出来。"
            });

            list.Add(new Design
            {
                Slug = "v2_fanout", Name = "Fan-out", NameCn = "一分多分发", Draw = DrawV2,
                Idea = "左侧实心圆是唯一音源，三条 S 形贝塞尔曲线扇出到右侧三个圆角方块，直接画出「一路输入、多路输出」。",
                Scene = "语义最贴合产品本身，适合当主图标；也适合官网首图 / 功能说明插图。"
            });

            list.Add(new Design
            {
                Slug = "v3_ripple", Name = "Ripple", NameCn = "声波扩散", Draw = DrawV3,
                Idea = "同心圆环由内向外以 100% / 60% / 32% 三级灰阶递减，模拟声波在空间中的衰减，中心实心点为声源。",
                Scene = "深色背景下最有氛围感，适合深色主题的启动画面 / 关于页 / 安装向导。"
            });

            list.Add(new Design
            {
                Slug = "v4_layers", Name = "Layers", NameCn = "设备层叠", Draw = DrawV4,
                Idea = "三张超椭圆卡片沿对角线层叠，用 #555555 / #989898 / #FFFFFF 三级明度拉开前后关系，暗示多台输出设备。",
                Scene = "强调「多设备」而非「多路信号」，适合设备管理列表的标题图标。"
            });

            list.Add(new Design
            {
                Slug = "v5_monogram", Name = "Monogram M", NameCn = "字母 M", Draw = DrawV5,
                Idea = "用四段圆头粗笔画几何构造出 M，笔画端点收圆与外形超椭圆呼应，去掉了字体渲染的偶然性。",
                Scene = "品牌化最强，适合作为产品标识长期使用；代价是语义需要用户学习一次。"
            });

            list.Add(new Design
            {
                Slug = "v6_invert", Name = "Inverted Disc", NameCn = "正负形", Draw = DrawV6,
                Idea = "白色圆盘压在黑底上，盘内再挖出三根黑色声柱 —— 黑与白在同一个图形里互相定义，形成强对比。",
                Scene = "对比度最高，缩到 16px 依然清晰；适合高 DPI 屏与深色任务栏。"
            });

            list.Add(new Design
            {
                Slug = "v7_lineart", Name = "Line Split", NameCn = "线条极简", Draw = DrawV7,
                Idea = "极细线条画的信号分叉树：一根主干向下，分成左中右三支，末端收成实心圆点。",
                Scene = "最轻盈、最有工程感，适合浅色界面与文档 / 说明图；16px 下依赖笔画加粗补偿。"
            });

            list.Add(new Design
            {
                Slug = "v8_aperture", Name = "Aperture", NameCn = "几何分割", Draw = DrawV8,
                Idea = "实心圆被一个十字缝隙均分为四个扇形，像相机光圈，也像「一份信号被切成四路」。",
                Scene = "几何感与秩序感最强，对称稳定，适合技术型 / 工具型产品的图标。"
            });

            list.Add(new Design
            {
                Slug = "v9_levels", Name = "Levels", NameCn = "电平条", Draw = DrawV9,
                Idea = "三条水平圆头条自上而下递减长度，最短的一条降为中灰，形成信号电平逐级衰减的节奏。",
                Scene = "横向构图，与常见的竖向波形图标拉开差异；适合列表项 / 设置项里的小图标。"
            });

            list.Add(new Design
            {
                Slug = "v10_hub", Name = "Hub", NameCn = "辐射枢纽", Draw = DrawV10,
                Idea = "中心一个实心节点，四条圆头对角线向四方辐射，构成一个无方向的「分发枢纽」。",
                Scene = "抽象、平衡、通用，适合做系列图标的基础母题；语义偏弱，需配合文字。"
            });

            return list;
        }

        // ═══════════════════════ ICO 容器 ═══════════════════════

        /// <summary>
        /// 写 ICO 容器（Vista+ 支持 PNG 压缩帧）：
        /// ICONDIR = 6 字节 (reserved=0, type=1, count=N)
        /// ICONDIRENTRY = 16 字节 (宽, 高, 调色板, 保留, 平面, 位深, 数据长度, 数据偏移)
        /// 宽/高为 0 表示 256。数据段直接放 PNG 字节。
        /// </summary>
        static void WriteIco(string path, List<Bitmap> images)
        {
            int n = images.Count;
            byte[][] blobs = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    images[i].Save(ms, ImageFormat.Png);
                    blobs[i] = ms.ToArray();
                }
            }

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);      // reserved
                bw.Write((ushort)1);      // type = icon
                bw.Write((ushort)n);      // count

                int offset = 6 + 16 * n;
                for (int i = 0; i < n; i++)
                {
                    int w = images[i].Width;
                    int h = images[i].Height;
                    bw.Write((byte)(w >= 256 ? 0 : w));
                    bw.Write((byte)(h >= 256 ? 0 : h));
                    bw.Write((byte)0);        // 调色板数
                    bw.Write((byte)0);        // 保留
                    bw.Write((ushort)1);      // 色彩平面
                    bw.Write((ushort)32);     // 位深
                    bw.Write((uint)blobs[i].Length);
                    bw.Write((uint)offset);
                    offset += blobs[i].Length;
                }
                for (int i = 0; i < n; i++) { bw.Write(blobs[i]); }
            }
        }

        // ═══════════════════════ 总览 / 小尺寸图 ═══════════════════════

        static readonly string[] FontPrefer = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Noto Sans SC", "Arial" };
        static InstalledFontCollection _fonts = new InstalledFontCollection();
        static string _family = null;

        static string Family()
        {
            if (_family != null) { return _family; }
            for (int i = 0; i < FontPrefer.Length; i++)
            {
                for (int j = 0; j < _fonts.Families.Length; j++)
                {
                    if (string.Equals(_fonts.Families[j].Name, FontPrefer[i], StringComparison.OrdinalIgnoreCase))
                    {
                        _family = FontPrefer[i];
                        return _family;
                    }
                }
            }
            _family = FontFamily.GenericSansSerif.Name;
            return _family;
        }

        static Font F(float px, FontStyle st)
        {
            return new Font(Family(), px, st, GraphicsUnit.Pixel);
        }

        static void ShadowBody(Graphics g, float x, float y, float size, float dy, int alpha)
        {
            using (GraphicsPath p = Squircle.CreateIosIcon(size))
            using (Matrix m = new Matrix())
            {
                m.Translate(x, y + dy);
                p.Transform(m);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0))) { g.FillPath(b, p); }
            }
        }

        static Bitmap MakeOverview(List<Design> designs, List<Bitmap> thumbs)
        {
            const int ICON = 256;
            const int COLS = 5;
            const int GAPX = 44;
            const int GAPY = 48;
            const int LABELH = 54;
            const int PAD = 40;
            const int HEADER = 108;

            int rows = (designs.Count + COLS - 1) / COLS;
            int W = PAD * 2 + COLS * ICON + (COLS - 1) * GAPX;
            int H = HEADER + rows * (ICON + LABELH) + (rows - 1) * GAPY + PAD;

            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (SolidBrush bg = new SolidBrush(Sheet)) { g.FillRectangle(bg, 0, 0, W, H); }

                using (Font ft = F(30f, FontStyle.Bold))
                using (Font fs = F(15f, FontStyle.Regular))
                using (SolidBrush bi = new SolidBrush(SheetInk))
                using (SolidBrush bd = new SolidBrush(SheetDim))
                {
                    g.DrawString("并川听澜 — 图标方案", ft, bi, PAD, 30f);
                    g.DrawString("10 版黑白极简方向 · 外形统一为 iOS 连续曲率超椭圆（圆角半径比 0.2237）", fs, bd, PAD + 2f, 70f);
                }

                using (StringFormat ctr = new StringFormat())
                {
                    ctr.Alignment = StringAlignment.Center;
                    ctr.LineAlignment = StringAlignment.Near;
                    ctr.FormatFlags = StringFormatFlags.NoWrap;

                    for (int i = 0; i < designs.Count; i++)
                    {
                        int c = i % COLS;
                        int r = i / COLS;
                        float x = PAD + c * (ICON + GAPX);
                        float y = HEADER + r * (ICON + LABELH + GAPY);

                        ShadowBody(g, x, y, ICON, 6f, 22);
                        ShadowBody(g, x, y, ICON, 12f, 12);
                        g.DrawImage(thumbs[i], new Rectangle((int)x, (int)y, ICON, ICON));

                        using (Font fl = F(17f, FontStyle.Bold))
                        using (Font fc = F(14f, FontStyle.Regular))
                        using (SolidBrush bi = new SolidBrush(SheetInk))
                        using (SolidBrush bd = new SolidBrush(SheetDim))
                        {
                            string l1 = string.Format("v{0}  {1}", i + 1, designs[i].Name);
                            g.DrawString(l1, fl, bi, x + ICON * 0.5f, y + ICON + 12f, ctr);
                            g.DrawString(designs[i].NameCn, fc, bd, x + ICON * 0.5f, y + ICON + 34f, ctr);
                        }
                    }
                }
            }
            return bmp;
        }

        static Bitmap MakeLegibility(List<Design> designs)
        {
            int[] sizes = { 16, 24, 32, 48, 64 };
            const int ROWH = 88;
            const int LABELW = 250;
            const int PAD = 40;
            const int HEADER = 108;
            const int GAP = 44;

            int colsW = 0;
            for (int i = 0; i < sizes.Length; i++) { colsW += sizes[i] + GAP; }
            int W = LABELW + colsW + PAD;
            int H = HEADER + designs.Count * ROWH + PAD;

            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (SolidBrush bg = new SolidBrush(Sheet)) { g.FillRectangle(bg, 0, 0, W, H); }

                using (Font ft = F(30f, FontStyle.Bold))
                using (Font fs = F(15f, FontStyle.Regular))
                using (Font fh = F(15f, FontStyle.Bold))
                using (Font fn = F(17f, FontStyle.Bold))
                using (Font fc = F(13f, FontStyle.Regular))
                using (SolidBrush bi = new SolidBrush(SheetInk))
                using (SolidBrush bd = new SolidBrush(SheetDim))
                {
                    g.DrawString("小尺寸可辨识度检查", ft, bi, PAD, 30f);
                    g.DrawString("按真实像素尺寸渲染（未放大），用于判断 16 / 24 / 32 px 下形状是否还成立", fs, bd, PAD + 2f, 70f);

                    float hx = LABELW;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        g.DrawString(sizes[i].ToString() + " px", fh, bd, hx + sizes[i] * 0.5f, 82f,
                            new StringFormat { Alignment = StringAlignment.Center });
                        hx += sizes[i] + GAP;
                    }

                    for (int i = 0; i < designs.Count; i++)
                    {
                        float y = HEADER + i * ROWH;
                        g.DrawString(string.Format("v{0}  {1}", i + 1, designs[i].Name), fn, bi, PAD, y + ROWH * 0.5f - 18f);
                        g.DrawString(designs[i].NameCn, fc, bd, PAD, y + ROWH * 0.5f + 2f);

                        float x = LABELW;
                        for (int k = 0; k < sizes.Length; k++)
                        {
                            int sz = sizes[k];
                            Bitmap b = RenderAt(designs[i], sz);
                            float iy = y + (ROWH - sz) * 0.5f;
                            ShadowBody(g, x, iy, sz, sz <= 24 ? 1.5f : 3f, 26);
                            g.DrawImage(b, new Rectangle((int)x, (int)iy, sz, sz));
                            b.Dispose();
                            x += sz + GAP;
                        }
                    }
                }
            }
            return bmp;
        }

        static string MakeDesignDoc(List<Design> designs)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 并川听澜 — 图标设计方案");
            sb.AppendLine();
            sb.AppendLine("共 " + designs.Count + " 版，全部为黑 / 白 / 灰阶，外形统一使用 `Squircle.CreateIosIcon()`");
            sb.AppendLine("（iOS 连续曲率超椭圆，圆角半径比 0.2237，指数 n=5），因此圆角是平滑过渡而非生硬直角。");
            sb.AppendLine();
            sb.AppendLine("每个设计产出三个文件：`<slug>.png`（512×512 预览）、`<slug>_256.png`（256×256）、");
            sb.AppendLine("`<slug>.ico`（16/24/32/48/64/128/256 七尺寸，PNG 压缩帧）。");
            sb.AppendLine();
            sb.AppendLine("| # | 名称 | 中文 | 设计理念 | 适合场景 | 文件 |");
            sb.AppendLine("|---|------|------|----------|----------|------|");
            for (int i = 0; i < designs.Count; i++)
            {
                Design d = designs[i];
                sb.AppendLine(string.Format("| v{0} | {1} | {2} | {3} | {4} | `{5}.png` / `{5}_256.png` / `{5}.ico` |",
                    i + 1, d.Name, d.NameCn, d.Idea, d.Scene, d.Slug));
            }
            sb.AppendLine();
            sb.AppendLine("## 通用设计手法");
            sb.AppendLine();
            sb.AppendLine("- **单一焦点**：每版只有一个视觉母题，1 秒内可辨认，符合 Apple HIG「embrace simplicity」。");
            sb.AppendLine("- **正负形互补**：v1/v3/v4/v5/v6/v8/v9/v10 为黑底白形，v2/v7 为白底黑形，v6 在同一图形内同时使用两者。");
            sb.AppendLine("- **灰阶层次**：只用 #11141A / #555555 / #989898 / #D0D0D0 / #FFFFFF 五级明度做纵深，全部 R=G=B，不引入任何彩色。");
            sb.AppendLine("- **光学补偿**：渲染器按目标尺寸自动加粗细笔画（v2/v7），避免 16px 时线条消失。");
            sb.AppendLine("- **安全区**：所有母题都收在边长 70%–76% 的中心区域内，缩放时不会被圆角切到。");
            sb.AppendLine();
            AppendResearch(sb);
            return sb.ToString();
        }

        /// <summary>把设计调研结论写进说明文档 —— 放在生成器里，重新构建不会丢失。</summary>
        static void AppendResearch(StringBuilder sb)
        {
            sb.AppendLine("## 参考的公开设计规范");
            sb.AppendLine();
            sb.AppendLine("| 来源 | 采纳的具体做法 |");
            sb.AppendLine("|------|----------------|");
            sb.AppendLine("| [Apple HIG — App icons](https://developer.apple.com/design/human-interface-guidelines/app-icons) | 极简、单一焦点、图标内不放文字、按 29×29 级别验证可辨识度 |");
            sb.AppendLine("| [IBM Design Language — App icons usage](https://www.ibm.com/design/language/iconography/app-icons/usage/) | 16/20/24/32px 为核心尺寸，48px 以上才允许表达性细节 → 本套图标把细节全部压在 48px 以下也能成立 |");
            sb.AppendLine("| [SVG Genie — App Icon Design Guide](https://www.svggenie.com/blog/app-icon-design-guide) | 各平台尺寸矩阵与导出规格 |");
            sb.AppendLine("| [Twinr — Apple App Icon Guidelines](https://twinr.dev/blogs/apple-app-icon-design-guidelines) | 连续曲率圆角（continuous corner）与安全区留白比例 |");
            sb.AppendLine("| [ios27-design-system — icon spec](https://github.com/seunghan91/ios27-design-system/blob/main/docs/icon-spec.md) | 圆角半径比 0.2237 的量化取值 |");
            sb.AppendLine("| [App Icon Squircle Mask Previewer](https://owaiskhan.website/tools/app-icon-squircle-mask-previewer/) | 验证超椭圆遮罩下四角裁切量，确认母题不越界 |");
            sb.AppendLine("| [AWC — 7+ Minimalist White & Black App Icons](https://awc.airforce.mil.ng/white-and-black-app-icons) | 单色图标的正负形互换手法与统一笔画粗细 |");
            sb.AppendLine();
            sb.AppendLine("### 从这些规范中提炼的共性手法");
            sb.AppendLine();
            sb.AppendLine("1. **正负形互换**：同一个母题在黑底白形 / 白底黑形下都要成立（v6 在单个图形内同时使用两者）。");
            sb.AppendLine("2. **单一几何符号**，不做插画式描绘；母题数量收敛到一个。");
            sb.AppendLine("3. **大面积留白**：母题只占中心约 70%，四周留呼吸空间。");
            sb.AppendLine("4. **用灰阶代替彩色做纵深**：靠明度分级表达前后关系，而不是加颜色。");
            sb.AppendLine("5. **统一笔画粗细**：同一图标内所有线条等宽，端点统一收圆。");
            sb.AppendLine("6. **光学居中而非数学居中**：视觉重心略高于几何中心，底部留白略多于顶部。");
            sb.AppendLine("7. **按尺寸分级做光学补偿**：小尺寸加粗笔画、减少细节，大尺寸才允许表达性细节。");
            sb.AppendLine();
            sb.AppendLine("> 说明：Apple HIG 建议 iOS 图标**不要**自带圆角（由系统裁切）。本项目的目标是 Windows 桌面");
            sb.AppendLine("> `.ico`，Windows 不会为图标裁切圆角，因此这里**刻意**把超椭圆圆角烘焙进图像本身。");
            sb.AppendLine();
        }

        // ═══════════════════════ 文本化自检（ASCII 预览） ═══════════════════════

        const string Ramp = "@%#*+=-:.";   // 索引 0 = 纯黑，索引 8 = 纯白（不含空格，空格只表示形状之外）

        static char Glyph(int alpha, int lum)
        {
            if (alpha < 40) { return ' '; }
            int i = lum * (Ramp.Length - 1) / 255;
            if (i < 0) { i = 0; }
            if (i > Ramp.Length - 1) { i = Ramp.Length - 1; }
            return Ramp[i];
        }

        /// <summary>把一版图标渲染成 ASCII 文本，用于在没有图像查看能力时人工审阅构图。</summary>
        static int Preview(string outDir)
        {
            List<Design> designs = BuildDesigns();
            List<string> big = new List<string>();
            List<string> small = new List<string>();

            big.Add("ASCII 预览 —— 32×32（用于审阅构图、留白、黑白比例）");
            big.Add("'@' = 纯黑   '.' = 纯白   ' ' = 图标形状之外（完全透明）   中间灰阶按 @ % # * + = - : . 递减");
            big.Add("");

            small.Add("ASCII 预览 —— 16×16（真实小尺寸，用于审阅可辨识度）");
            small.Add("");

            for (int i = 0; i < designs.Count; i++)
            {
                Design d = designs[i];

                big.Add(string.Format("── v{0} {1} / {2} ──────────────────────────────", i + 1, d.Name, d.NameCn));
                big.AddRange(Ascii(d, 32, 2));
                big.Add("   " + Stats(d, 256));
                big.Add("");
                big.Add("");

                small.Add(string.Format("v{0} {1} / {2}", i + 1, d.Name, d.NameCn));
                small.AddRange(Ascii(d, 16, 1));
                small.Add("");
                small.Add("");
            }

            string p1 = Path.Combine(outDir, "_ascii_32.txt");
            string p2 = Path.Combine(outDir, "_ascii_16.txt");
            File.WriteAllText(p1, string.Join(Environment.NewLine, big.ToArray()), new UTF8Encoding(false));
            File.WriteAllText(p2, string.Join(Environment.NewLine, small.ToArray()), new UTF8Encoding(false));
            Console.WriteLine(p1 + "  (" + new FileInfo(p1).Length + " B)");
            Console.WriteLine(p2 + "  (" + new FileInfo(p2).Length + " B)");
            return 0;
        }

        static List<string> Ascii(Design d, int n, int charAspect)
        {
            List<string> lines = new List<string>();
            using (Bitmap b = RenderAt(d, n))
            {
                for (int y = 0; y < n; y++)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(string.Format("{0,2}|", y));   // 固定行号栏，便于逐列核对
                    for (int x = 0; x < n; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        sb.Append(Glyph(c.A, lum));
                        sb.Append(' ', charAspect - 1);
                    }
                    lines.Add(sb.ToString().TrimEnd());
                }
                StringBuilder ruler = new StringBuilder();
                ruler.Append("  +");
                for (int x = 0; x < n; x++)
                {
                    ruler.Append((x % 10).ToString());
                    ruler.Append(' ', charAspect - 1);
                }
                lines.Insert(0, ruler.ToString());
            }
            return lines;
        }

        /// <summary>构图统计：主体覆盖率、内容包围盒、水平/垂直对称性 —— 用于客观判断视觉平衡。</summary>
        static string Stats(Design d, int n)
        {
            using (Bitmap b = RenderAt(d, n))
            {
                // 底色 = 顶部中点的像素（所有设计在该处都是纯底色，没有母题）
                Color bg = b.GetPixel(n / 2, 1);
                int bgLum = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;

                int motif = 0, opaque = 0;
                int minX = n, maxX = -1, minY = n, maxY = -1;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.A < 40) { continue; }
                        opaque++;
                        int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        // 主体 = 与底色差异明显的像素（对黑底 / 白底同样成立，可横向比较）
                        if (Math.Abs(lum - bgLum) > 48)
                        {
                            motif++;
                            if (x < minX) { minX = x; }
                            if (x > maxX) { maxX = x; }
                            if (y < minY) { minY = y; }
                            if (y > maxY) { maxY = y; }
                        }
                    }
                }
                float shapePct = opaque * 100f / (n * n);
                float motifPct = motif * 100f / (n * n);

                long diffLR = 0, diffTB = 0;
                int samples = 0;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        Color a = b.GetPixel(x, y);
                        Color lr = b.GetPixel(n - 1 - x, y);
                        Color tb = b.GetPixel(x, n - 1 - y);
                        diffLR += Math.Abs(a.R - lr.R) + Math.Abs(a.G - lr.G) + Math.Abs(a.B - lr.B);
                        diffTB += Math.Abs(a.R - tb.R) + Math.Abs(a.G - tb.G) + Math.Abs(a.B - tb.B);
                        samples++;
                    }
                }
                float symLR = 100f - (diffLR / (float)(samples * 3 * 255) * 100f);
                float symTB = 100f - (diffTB / (float)(samples * 3 * 255) * 100f);

                string bbox = (maxX < 0)
                    ? "无主体"
                    : string.Format("x[{0}..{1}] y[{2}..{3}] 宽{4}% 高{5}%",
                        minX, maxX, minY, maxY,
                        (maxX - minX + 1) * 100 / n, (maxY - minY + 1) * 100 / n);

                return string.Format(
                    "形状={0:F1}%  主体覆盖率={1:F1}%  包围盒={2}  左右对称={3:F1}%  上下对称={4:F1}%",
                    shapePct, motifPct, bbox, symLR, symTB);
            }
        }

        // ═══════════════════════ 校验 ═══════════════════════

        static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static long U32(byte[] b, int i)
        {
            return (long)(uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        }
        static int BE32(byte[] b, int i)
        {
            return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
        }

        static int Verify(string dir)
        {
            int problems = 0;
            List<string> log = new List<string>();
            log.Add("=== ICO 结构校验 ===");
            log.Add("目录: " + dir);
            log.Add("");

            string[] icos = Directory.GetFiles(dir, "*.ico");
            Array.Sort(icos);
            for (int i = 0; i < icos.Length; i++)
            {
                string f = icos[i];
                byte[] b = File.ReadAllBytes(f);
                string name = Path.GetFileName(f);
                int reserved = U16(b, 0);
                int type = U16(b, 2);
                int count = U16(b, 4);
                bool ok = (reserved == 0) && (type == 1) && (count == IcoSizes.Length);

                StringBuilder detail = new StringBuilder();
                for (int k = 0; k < count; k++)
                {
                    int e = 6 + 16 * k;
                    int ew = b[e];
                    int eh = b[e + 1];
                    long len = U32(b, e + 8);
                    long off = U32(b, e + 12);
                    int px = ew == 0 ? 256 : ew;
                    int py = eh == 0 ? 256 : eh;

                    bool pngMagic = off + 8 <= b.Length
                        && b[off] == 0x89 && b[off + 1] == 0x50 && b[off + 2] == 0x4E && b[off + 3] == 0x47
                        && b[off + 4] == 0x0D && b[off + 5] == 0x0A && b[off + 6] == 0x1A && b[off + 7] == 0x0A;
                    bool inRange = (off + len) <= b.Length && len > 0;
                    bool ihdrOk = false;
                    if (pngMagic && off + 24 <= b.Length)
                    {
                        ihdrOk = (BE32(b, (int)off + 16) == px) && (BE32(b, (int)off + 20) == py);
                    }
                    bool entryOk = pngMagic && inRange && ihdrOk && (px == IcoSizes[k]) && (py == IcoSizes[k]);
                    if (!entryOk) { ok = false; }

                    detail.Append(string.Format(" [{0}x{1} len={2} off={3} {4}]",
                        px, py, len, off, entryOk ? "ok" : "BAD"));
                }

                log.Add(string.Format("{0,-20} bytes={1,-8} reserved={2} type={3} count={4}  {5}",
                    name, b.Length, reserved, type, count, ok ? "PASS" : "FAIL"));
                log.Add("    " + detail.ToString());
                if (!ok) { problems++; }
            }

            log.Add("");
            log.Add("=== PNG 内容校验（非空白 / 非纯色）===");
            log.Add("");

            string[] pngs = Directory.GetFiles(dir, "*.png");
            Array.Sort(pngs);
            for (int i = 0; i < pngs.Length; i++)
            {
                string f = pngs[i];
                using (Bitmap bm = new Bitmap(f))
                {
                    int dark = 0, light = 0, mid = 0, clear = 0, n = 0;
                    HashSet<int> colors = new HashSet<int>();
                    for (int y = 0; y < bm.Height; y += 1)
                    {
                        for (int x = 0; x < bm.Width; x += 1)
                        {
                            Color c = bm.GetPixel(x, y);
                            n++;
                            if (c.A < 24) { clear++; continue; }
                            colors.Add((c.R << 16) | (c.G << 8) | c.B);
                            int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                            if (lum < 64) { dark++; }
                            else if (lum > 200) { light++; }
                            else { mid++; }
                        }
                    }
                    float pd = dark * 100f / n, pl = light * 100f / n, pm = mid * 100f / n, pc = clear * 100f / n;
                    bool blank = colors.Count < 3 || (pd + pl + pm) < 1f;
                    bool single = (dark == 0 && mid == 0) || (light == 0 && mid == 0);
                    bool ok = !blank && !single;
                    log.Add(string.Format("{0,-24} {1}x{2}  colors={3,-5} dark={4,5:F1}% mid={5,5:F1}% light={6,5:F1}% alpha0={7,5:F1}%  {8}",
                        Path.GetFileName(f), bm.Width, bm.Height, colors.Count, pd, pm, pl, pc, ok ? "PASS" : "FAIL"));
                    if (!ok) { problems++; }
                }
            }

            log.Add("");
            log.Add(problems == 0 ? "ALL CHECKS PASSED" : ("PROBLEMS: " + problems));

            string logPath = Path.Combine(dir, "_verify.log");
            File.WriteAllText(logPath, string.Join(Environment.NewLine, log.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
            for (int i = 0; i < log.Count; i++) { Console.WriteLine(log[i]); }
            return problems == 0 ? 0 : 2;
        }
    }
}
