using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>所有自绘控件的基类：开启双缓冲、统一高质量渲染参数。</summary>
    internal abstract class SkinnedControl : Control
    {
        protected SkinnedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected static GraphicsConfig Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 灰度抗锯齿，不用 ClearType：ClearType 的子像素条纹会在近白底上产生
            // 橙/蓝/青彩边（详见 Theme.TextHint 的说明）。
            g.TextRenderingHint = Theme.TextHint;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            return default(GraphicsConfig);
        }

        /// <summary>
        /// 解析「真正可以拿去填充」的背景色。
        ///
        /// 绝不能把 Parent.BackColor 直接交给 Graphics.Clear()：本类构造时把自身设为
        /// Color.Transparent（ARGB 00FFFFFF），子控件读到父控件的透明色后再 Clear，
        /// GDI+ 会忽略 alpha 通道只取 RGB，于是整块被填成黑色。开关胶囊四周留 2px
        /// 边距、四角又是圆角，露出的那圈黑底就成了「黑色方框」。
        ///
        /// 这里沿父链向上找第一个不透明的颜色；全透明就退到画布色。
        ///
        /// 还要先看父控件的 SurfaceColor：Card 之类的自绘控件本身也是
        /// BackColor = Transparent（A=0），靠 OnPaint 自己填 FillColor。
        /// 只看 BackColor 会跳过它们、一路走到窗体拿到画布灰，于是放在白卡片上的
        /// 开关（46x28 矩形）会被清成画布灰 —— 胶囊四角露出灰底，就是「灰框」。
        /// </summary>
        protected Color ResolveBackColor()
        {
            if (_backOverride.HasValue) { return _backOverride.Value; }
            return ResolveBackColorFrom(Parent);
        }

        Color? _backOverride;

        /// <summary>
        /// 显式指定「我背后是什么颜色」，用来覆盖沿父链解析的结果。
        ///
        /// 为什么必须有这一条：父链只能反映**控件**层级，反映不了**手绘**内容。
        /// 设置面板（SettingsPanel）的白色卡片是它自己在 OnPaint 里画的矩形，
        /// 不是 Card 控件 —— 开关的 Parent 直接就是 Form，父链一路解析到 Form.BackColor
        /// = Theme.Canvas(246,247,249)。于是开关那 46x28 的范围被 Clear 成画布灰，
        /// 而胶囊是圆角的、四角盖不住，白卡片上就露出一圈灰框（用户截图报的就是这个）。
        ///
        /// 这和 Bug 7（黑框）、Bug 14（灰框）是同一个坑的第三次发作：
        /// **只要控件不在父控件的同色矩形里，父链就答不上来，必须显式告诉它。**
        /// </summary>
        public Color? BackOverride
        {
            get { return _backOverride; }
            set
            {
                if (_backOverride == value) { return; }
                _backOverride = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 从指定控件开始沿父链解析背景色（逻辑同 <see cref="ResolveBackColor"/>）。
        ///
        /// 需要「从任意位置开始」的唯一理由：铺满父控件的子控件要自己画出父控件的
        /// 圆角，就必须知道**父控件背后**是什么颜色，用来填四角露出来的那一小块。
        /// 那种情况传 <c>Parent.Parent</c> 即可 —— 传 Parent 会拿到父控件自己的
        /// 表面色，正好是要画的那一层，填出来等于没画。
        /// </summary>
        protected static Color ResolveBackColorFrom(Control start)
        {
            for (Control p = start; p != null; p = p.Parent)
            {
                var skin = p as SkinnedControl;
                if (skin != null)
                {
                    Color? fill = skin.SurfaceColor;
                    if (fill.HasValue) { return fill.Value; }
                }
                Color c = p.BackColor;
                if (c.A != 0) { return c; }
            }
            return Theme.Canvas;
        }

        /// <summary>
        /// 本控件自己绘制的不透明底色；null 表示「透明、跟随父控件」。
        /// 供子控件（以及 ResolveBackColor 的父链查找）当作真实底色使用。
        /// </summary>
        protected virtual Color? SurfaceColor { get { return null; } }

        /// <summary>SurfaceColor 的外部只读入口：非 SkinnedControl 的控件（如 SkinLabel）也能查。</summary>
        internal Color? SurfaceFillColor { get { return SurfaceColor; } }

        protected struct GraphicsConfig { }
    }

    /// <summary>
    /// iOS 风格胶囊/圆角按钮。三态配色 + 可选危险色 + 禁用态。
    /// </summary>
    internal sealed class PillButton : SkinnedControl
    {
        public enum Kind { Primary, Secondary, Danger, Ghost, Tinted }

        Kind _kind = Kind.Secondary;
        bool _hover, _down;
        float _anim;             // 0=常态 1=悬停（用于平滑过渡）
        readonly Timer _tick;

        public Kind Variant
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        /// <summary>紧凑模式：更小的水平内边距（用于"全选/反选"这类小按钮）。</summary>
        public bool Compact { get; set; }

        float _glowPad;
        /// <summary>
        /// 外发光预留的内边距（像素）。&gt;0 时控件四周让出这么多空白，
        /// 胶囊本体画在里圈，光晕画在外圈。Owner 负责把 Location 向内缩同样的
        /// 数值、把 Size 向外扩两倍，这样「视觉上的按钮」位置和大小都不变。
        ///
        /// 之所以由外部来扩：控件自己没法知道父容器的布局意图，
        /// 悄悄把自己变大一圈会破坏 LayoutContent() 里的手工布局。
        /// </summary>
        public float GlowPad
        {
            get { return _glowPad; }
            set
            {
                float v = Math.Max(0f, value);
                if (Math.Abs(v - _glowPad) < 0.01f) { return; }
                _glowPad = v;
                Invalidate();
            }
        }

        /// <summary>胶囊本体的矩形（已扣掉四周的 GlowPad）。</summary>
        RectangleF ButtonRect()
        {
            float p = _glowPad;
            return new RectangleF(0.5f + p, 0.5f + p,
                                  Math.Max(1f, Width - 1f - p * 2f),
                                  Math.Max(1f, Height - 1f - p * 2f));
        }

        /// <summary>
        /// 在 rect 外侧画一圈柔和光晕。用「逐级放大 + 逐级降 alpha 的描边」
        /// 近似高斯衰减：在 8~14px 的尺度上 7 层就足够平滑，不依赖位图或模糊 API。
        /// </summary>
        static void DrawGlow(Graphics g, RectangleF rect, Color color, float strength)
        {
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                float pad = Math.Min(12f, Math.Max(3f, Math.Min(rect.Width, rect.Height) * 0.05f));
                // 从外到内，先画淡的再画浓的：后画的会盖住先画的，内层因此更实。
                for (int i = 5; i >= 1; i--)
                {
                    float t = i / 5f;                       // 1=最外圈 0.2=最内圈
                    float grow = pad * t;
                    // alpha 随外扩二次衰减，视觉上更接近真实阴影
                    int a = (int)Math.Round(26.0 * (1.0 - t) * (1.0 - t) * strength + 3.0 * (1.0 - t));
                    if (a <= 0) { continue; }
                    var r = new RectangleF(rect.X - grow, rect.Y - grow,
                                           rect.Width + grow * 2f, rect.Height + grow * 2f);
                    float rad = Theme.RadiusBtn + grow;
                    using (var p = Squircle.Create(r.X, r.Y, r.Width, r.Height, rad))
                    using (var pn = new Pen(Color.FromArgb(Math.Min(255, a), color), Math.Max(1.4f, grow * 0.55f)))
                    {
                        g.DrawPath(pn, p);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = sm;
            }
        }

        public PillButton()
        {
            Cursor = Cursors.Hand;
            Font = Theme.FBtn;
            Height = Theme.S(38);
            SetStyle(ControlStyles.Selectable, false);
            _tick = new Timer { Interval = 16 };
            _tick.Tick += (_, __) =>
            {
                float target = (_hover || _down) ? 1f : 0f;
                float delta = target - _anim;
                if (Math.Abs(delta) < 0.02f)
                {
                    _anim = target;
                    _tick.Stop();
                }
                else
                {
                    _anim += delta * 0.35f;
                }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; _tick.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; _tick.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; _tick.Start(); base.OnMouseUp(e); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { _hover = false; _down = false; _anim = 0f; }
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            bool on = Enabled;
            Color fill, text;

            switch (_kind)
            {
                case Kind.Primary:
                    fill = !on ? Theme.DisabledBg : Theme.Mix(Theme.Accent, Theme.AccentDark, _anim * 0.55f);
                    text = !on ? Theme.DisabledText : Theme.White;
                    break;
                case Kind.Danger:
                    fill = !on ? Theme.DisabledBg : Theme.Mix(Theme.Danger, Theme.Mix(Theme.Danger, Color.Black, 0.18f), _anim * 0.6f);
                    text = !on ? Theme.DisabledText : Theme.White;
                    break;
                case Kind.Tinted:
                    fill = !on ? Theme.DisabledBg : Theme.Mix(Theme.AccentSoft, Theme.Mix(Theme.AccentSoft, Theme.Accent, 0.14f), _anim);
                    text = !on ? Theme.DisabledText : Theme.AccentDark;
                    break;
                case Kind.Ghost:
                    fill = !on ? Color.Transparent
                         : Theme.Mix(Color.FromArgb(0, Theme.SurfaceAlt), Theme.SurfaceAlt, _anim);
                    text = !on ? Theme.DisabledText : Theme.InkMid;
                    break;
                default: // Secondary
                    fill = !on ? Theme.DisabledBg : Theme.Mix(Theme.Surface, Theme.Mix(Theme.Surface, Theme.Ink, 0.055f), _anim);
                    text = !on ? Theme.DisabledText : Theme.Ink;
                    break;
            }

            if (_down && on) { fill = Theme.Mix(fill, Color.Black, 0.10f); }

            // 主按钮的柔和外发光。画在胶囊之外的「留白」里，所以按钮要把四周
            // 让出一圈给 GlowPad（MainForm 把开始按钮的矩形向外扩同样的像素，
            // 视觉尺寸不变）。Default 0 时下面整段不执行，其他 4 种按钮零开销。
            if (_kind == Kind.Primary && on && _glowPad > 0.5f)
            {
                DrawGlow(g, ButtonRect(), Theme.Accent, 1f + _anim * 0.35f);
            }

            float r = Theme.RadiusBtn;
            var rect = ButtonRect();
            using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, r))
            {
                if (fill.A > 0)
                {
                    using (var br = new SolidBrush(fill)) { g.FillPath(br, path); }
                }
                // 次级按钮加一条极淡描边，保证在浅底上仍有形
                if (_kind == Kind.Secondary || _kind == Kind.Ghost)
                {
                    Color edge = on ? Theme.Mix(Theme.Line, Theme.Ink, 0.14f * _anim) : Theme.LineSoft;
                    using (var pn = new Pen(edge, 1f)) { g.DrawPath(pn, path); }
                }
            }

            // 注意：这里的画刷必须显式释放。
            // DrawString 不会接管画刷所有权，直接 new 而不 Dispose
            // 会在每次重绘时泄漏一个 GDI 句柄，长时间使用后耗尽 GDI 配额。
            using (var tb = new SolidBrush(text))
            {
                g.DrawString(Text, Font, tb,
                    new RectangleF(0, 0, Width, Height), Theme.Center);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tick != null) { _tick.Stop(); _tick.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// iOS 风格开关（替代 Windows CheckBox）。带滑动动画。
    /// </summary>
    internal sealed class ToggleSwitch : SkinnedControl
    {
        bool _checked;
        float _anim;         // 0=关 1=开
        bool _hover, _down;
        readonly Timer _tick;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) { return; }
                _checked = value;
                _tick.Start();
                Invalidate();
                var h = CheckedChanged;
                if (h != null) { h(this, EventArgs.Empty); }
            }
        }

        /// <summary>不触发事件的静默赋值（用于程序化恢复状态）。</summary>
        public void SetCheckedSilent(bool value)
        {
            if (_checked == value) { return; }
            _checked = value;
            _anim = value ? 1f : 0f;
            Invalidate();
        }

        public ToggleSwitch()
        {
            Cursor = Cursors.Hand;
            Size = new Size(Theme.S(46), Theme.S(28));
            SetStyle(ControlStyles.Selectable, false);
            _tick = new Timer { Interval = 16 };
            _tick.Tick += (_, __) =>
            {
                float target = _checked ? 1f : 0f;
                float delta = target - _anim;
                if (Math.Abs(delta) < 0.03f) { _anim = target; _tick.Stop(); }
                else { _anim += delta * 0.38f; }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _down && ClientRectangle.Contains(e.Location) && Enabled)
            {
                Checked = !Checked;
            }
            _down = false; Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            float h = Height;
            float w = Width;
            float pad = Theme.S(2f);
            float knob = h - pad * 2f;

            Color trackOn = Theme.Mix(Theme.Accent, Theme.AccentDark, _hover ? 1f : 0f);
            Color trackOff = Theme.Mix(Theme.Track, Theme.Mix(Theme.Track, Theme.Ink, 0.10f), _hover ? 1f : 0f);

            // 运行期间开关会被禁用，但「已选 / 未选」必须仍然看得出来。
            // 这里曾写成「只要 !Enabled 就一律涂 Theme.DisabledBg(238,240,243)」，旋钮又是 (245,246,248)，
            // 两者几乎同色 —— 于是在白色卡片上，整排开关看起来就是一块空白方块，用户会以为设备根本没勾选。
            // 现在：禁用但已选 = 淡主色（保留选中语义，仅降低饱和与动画）；禁用且未选 = 灰。
            Color track;
            float pos;
            if (Enabled)
            {
                track = Theme.Mix(trackOff, trackOn, _anim);
                pos = _anim;
            }
            else
            {
                track = _checked ? Theme.Mix(Theme.Accent, Theme.White, 0.45f) : Theme.DisabledBg;
                pos = _checked ? 1f : 0f;   // 禁用时不做动画，直接落在正确的一端
            }

            var rect = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
            using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, h / 2f))
            using (var br = new SolidBrush(track)) { g.FillPath(br, path); }

            float kx = pad + (w - knob - pad * 2f) * pos;
            var knobRect = new RectangleF(kx, pad, knob, knob);
            // 旋钮阴影，增强立体感（禁用的「开」也留一点，好让白旋钮从淡绿轨道上分出来）
            int shadowA = Enabled ? 38 : (_checked ? 24 : 0);
            using (var sp = Squircle.Create(knobRect.X, knobRect.Y + 1.2f, knobRect.Width, knobRect.Height, knob / 2f))
            using (var sb = new SolidBrush(Color.FromArgb(shadowA, 0, 0, 0)))
            { g.FillPath(sb, sp); }
            // 旋钮一律白色：禁用的「关」轨道是浅灰、禁用的「开」轨道是淡绿，白旋钮在两者之上都看得见。
            using (var kp = Squircle.Create(knobRect.X, knobRect.Y, knobRect.Width, knobRect.Height, knob / 2f))
            using (var kb = new SolidBrush(Theme.White))
            { g.FillPath(kb, kp); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tick != null) { _tick.Stop(); _tick.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 实时电平条。数值来自音频引擎（0..1 峰值），带快升慢降的视觉平滑。
    /// </summary>
    internal sealed class LevelMeter : SkinnedControl
    {
        float _value;      // 当前显示值
        float _peak;       // 保持峰值（缓慢回落）
        int _hold;

        /// <summary>纯横向条形模式（用于设备行内的小电平）。</summary>
        public bool Thin { get; set; }

        public LevelMeter()
        {
            SetStyle(ControlStyles.Selectable, false);
            Height = Theme.S(8);
        }

        /// <summary>由引擎在 UI 线程调用，传入 0..1。</summary>
        public void Push(float v)
        {
            if (v < 0f) { v = 0f; }
            if (v > 1f) { v = 1f; }
            if (v > _value) { _value = v; }               // 瞬间上升
            else { _value = _value * 0.72f + v * 0.28f; } // 平滑回落
            if (v >= _peak) { _peak = v; _hold = 24; }
            else if (_hold > 0) { _hold--; }
            else { _peak = _peak * 0.85f + v * 0.15f; }
            Invalidate();
        }

        public void Clear() { _value = 0f; _peak = 0f; _hold = 0; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            float h = Height, w = Width;
            float r = h / 2f;

            using (var bg = Squircle.Create(0.5f, 0.5f, w - 1f, h - 1f, r))
            using (var br = new SolidBrush(Theme.LineSoft)) { g.FillPath(br, bg); }

            float filled = (w - 2f) * _value;
            if (filled > 1f)
            {
                using (var fg = Squircle.Create(1f, 1f, Math.Max(filled, 2f), h - 2f, (h - 2f) / 2f))
                using (var br = new SolidBrush(Theme.Accent)) { g.FillPath(br, fg); }
            }

            // 峰值竖线
            float px = 1f + (w - 2f) * _peak - 1.5f;
            if (_peak > 0.01f && px > 1f)
            {
                using (var pen = new Pen(Theme.Mix(Theme.Accent, Color.Black, 0.25f), 1.6f))
                {
                    g.DrawLine(pen, px, 1f, px, h - 1f);
                }
            }
        }
    }

    /// <summary>圆角卡片容器，自绘底色+描边。</summary>
    internal sealed class Card : SkinnedControl
    {
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public float Radius { get; set; }
        public bool DrawBorder { get; set; }

        /// <summary>
        /// 是否由自己画出卡片外形。设为 false 时本控件退化成纯位置容器，
        /// 只把背景清成父控件的颜色，卡片外观交给铺满它的子控件去画。
        ///
        /// 存在这个开关的原因：设备列表是铺满整张卡片的单个自绘控件，
        /// 它必须自己画出圆角（否则四角被它填成直角）。若两层都画，
        /// 后画的子控件会把卡片 1px 描边盖掉，四角还会出现双圆角错位的灰缝。
        /// 所以只能有一层画 —— 由能裁剪内容的子控件来画最省事。
        /// </summary>
        public bool DrawSelf { get; set; }

        public Card()
        {
            FillColor = Theme.Surface;
            BorderColor = Theme.Line;
            Radius = Theme.RadiusCard;
            DrawBorder = true;
            DrawSelf = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            if (!DrawSelf) { return; }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, Radius))
            {
                using (var br = new SolidBrush(FillColor)) { g.FillPath(br, path); }
                if (DrawBorder)
                {
                    using (var pen = new Pen(BorderColor, 1f)) { g.DrawPath(pen, path); }
                }
            }
        }

        /// <summary>卡片自己填了不透明底色，子控件应当以它为准，而不是一路找到画布灰。</summary>
        protected override Color? SurfaceColor { get { return DrawSelf ? FillColor : (Color?)null; } }
    }

    /// <summary>纯色圆点（状态指示）。</summary>
    internal sealed class Dot : SkinnedControl
    {
        Color _color = Theme.InkFaint;
        bool _pulse;
        float _phase;
        readonly Timer _tick;

        public Color DotColor
        {
            get { return _color; }
            set { _color = value; Invalidate(); }
        }

        /// <summary>呼吸效果：运行中时使用。</summary>
        public bool Pulse
        {
            get { return _pulse; }
            set
            {
                _pulse = value;
                if (value) { _tick.Start(); } else { _tick.Stop(); _phase = 0f; }
                Invalidate();
            }
        }

        public Dot()
        {
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(Theme.S(8), Theme.S(8));
            _tick = new Timer { Interval = 40 };
            _tick.Tick += (_, __) => { _phase += 0.13f; if (_phase > 6.2831855f) { _phase -= 6.2831855f; } Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            float size = Math.Min(Width, Height) - 1f;
            float alpha = _pulse ? 0.55f + 0.45f * (float)((Math.Sin(_phase) + 1) / 2) : 1f;
            int a = (int)Math.Round(255 * alpha);
            using (var br = new SolidBrush(Color.FromArgb(a, _color)))
            {
                g.FillEllipse(br, 0.5f, 0.5f, size, size);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tick != null) { _tick.Stop(); _tick.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 自定义外观滑块（替代 TrackBar）。支持整数步进、拖动、点击跳转、键盘方向键。
    /// </summary>
    internal sealed class SlimSlider : SkinnedControl
    {
        int _min, _max = 100, _value = 50, _step = 1;
        bool _drag, _hover;
        float _anim;

        public event EventHandler ValueChanged;

        public int Minimum { get { return _min; } set { _min = value; if (_value < _min) { _value = _min; } Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = value; if (_value > _max) { _value = _max; } Invalidate(); } }
        public int Step { get { return _step; } set { _step = Math.Max(1, value); } }

        public int Value
        {
            get { return _value; }
            set
            {
                int nv = Clamp(value);
                if (nv == _value) { return; }
                _value = nv;
                Invalidate();
                var h = ValueChanged;
                if (h != null) { h(this, EventArgs.Empty); }
            }
        }

        public void SetValueSilent(int value)
        {
            int nv = Clamp(value);
            if (nv == _value) { return; }
            _value = nv;
            Invalidate();
        }

        int Clamp(int v)
        {
            if (_max < _min) { return _min; }
            if (v < _min) { v = _min; }
            if (v > _max) { v = _max; }
            if (_step > 1) { v = _min + (int)Math.Round((v - _min) / (double)_step) * _step; if (v > _max) { v = _max; } }
            return v;
        }

        public SlimSlider()
        {
            Height = Theme.S(26);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        float TrackX(float t) { return Theme.S(9f) + (Width - Theme.S(18f)) * t; }
        float Ratio { get { return _max > _min ? (_value - _min) / (float)(_max - _min) : 0f; } }

        void SetFromX(int x)
        {
            float t = (x - Theme.S(9f)) / Math.Max(1f, Width - Theme.S(18f));
            if (t < 0f) { t = 0f; }
            if (t > 1f) { t = 1f; }
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _drag = true; Focus(); SetFromX(e.X); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) { SetFromX(e.X); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; _anim = 1f; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _anim = 0f; Invalidate(); base.OnMouseLeave(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) { return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int delta = 0;
            if (e.KeyCode == Keys.Left) { delta = -Math.Max(_step, 1); }
            else if (e.KeyCode == Keys.Right) { delta = Math.Max(_step, 1); }
            else if (e.KeyCode == Keys.Home) { Value = _min; e.Handled = true; return; }
            else if (e.KeyCode == Keys.End) { Value = _max; e.Handled = true; return; }
            if (delta != 0) { Value = _value + delta; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            float cy = Height / 2f;
            float x0 = Theme.S(9f), x1 = Width - Theme.S(9f);

            using (var pen = new Pen(Theme.Track, Theme.S(4f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x0, cy, x1, cy);
            }

            float kx = TrackX(Ratio);
            using (var pen = new Pen(Enabled ? Theme.Accent : Theme.Disabled, Theme.S(4f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x0, cy, kx, cy);
            }

            float kr = _drag ? Theme.S(9.5f) : (_hover ? Theme.S(9f) + Theme.S(0.6f) * _anim : Theme.S(8f));
            using (var sh = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
            { g.FillEllipse(sh, kx - kr, cy - kr + Theme.S(1.5f), kr * 2f, kr * 2f); }
            using (var br = new SolidBrush(Enabled ? Theme.White : Color.FromArgb(245, 246, 248)))
            { g.FillEllipse(br, kx - kr, cy - kr, kr * 2f, kr * 2f); }
            using (var pen = new Pen(Enabled ? Theme.Line : Theme.LineSoft, 1f))
            { g.DrawEllipse(pen, kx - kr, cy - kr, kr * 2f, kr * 2f); }

            // 旋钮中心的克莱因蓝实心小圆：白色旋钮在近白底上只是一个「浅灰圈」，
            // 加一个 45% 直径的主色圆点后，一眼能看出「拖这里」，也让整条滑轨
            // 的蓝（已走过部分 + 圆点）形成同一条视觉轴。禁用时改浅灰，不抢焦点。
            float dr = kr * 0.45f;
            using (var br = new SolidBrush(Enabled ? Theme.Accent : Theme.Disabled))
            { g.FillEllipse(br, kx - dr, cy - dr, dr * 2f, dr * 2f); }
        }
    }

    /// <summary>
    /// 自绘下拉框（替代 ComboBox），弹出一个无边框列表。
    /// </summary>
    internal sealed class SlimCombo : SkinnedControl
    {
        readonly List<string> _items = new List<string>();
        int _selected = -1;
        bool _hover, _open;
        PopupList _popup;

        public event EventHandler SelectedIndexChanged;

        public IList<string> Items { get { return _items; } }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < -1 || value >= _items.Count) { value = _items.Count > 0 ? 0 : -1; }
                if (_selected == value) { return; }
                _selected = value;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) { h(this, EventArgs.Empty); }
            }
        }

        public string SelectedText
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : string.Empty; }
        }

        public void SetItems(IEnumerable<string> items, int keepIndex)
        {
            _items.Clear();
            if (items != null) { foreach (string s in items) { _items.Add(s); } }
            if (keepIndex < 0 || keepIndex >= _items.Count) { keepIndex = _items.Count > 0 ? 0 : -1; }
            _selected = keepIndex;
            Invalidate();
        }

        public SlimCombo()
        {
            Cursor = Cursors.Hand;
            Font = Theme.FName;
            Height = Theme.S(36);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && _items.Count > 0) { TogglePopup(); }
            base.OnMouseDown(e);
        }

        void TogglePopup()
        {
            if (_open) { ClosePopup(); return; }
            _popup = new PopupList(_items, _selected, Width);
            _popup.Picked += idx =>
            {
                _open = false;
                Invalidate();
                if (idx >= 0) { SelectedIndex = idx; }
            };
            Point screen = PointToScreen(new Point(0, Height + 4));
            _open = true;
            Invalidate();
            _popup.ShowAt(screen);
        }

        void ClosePopup()
        {
            if (_popup != null) { _popup.CloseSelf(); _popup = null; }
            _open = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e.Graphics);
            Graphics g = e.Graphics;
            g.Clear(ResolveBackColor());

            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color fill = !Enabled ? Theme.DisabledBg : Theme.Mix(Theme.Surface, Theme.Mix(Theme.Surface, Theme.Ink, 0.04f), _hover ? 1f : 0f);
            using (var path = Squircle.Create(rect.X, rect.Y, rect.Width, rect.Height, Theme.S(9f)))
            {
                using (var br = new SolidBrush(fill)) { g.FillPath(br, path); }
                using (var pen = new Pen(Enabled ? (_open ? Theme.Accent : Theme.Line) : Theme.LineSoft, _open ? Theme.S(1.6f) : 1f))
                { g.DrawPath(pen, path); }
            }

            // 左侧主色竖条：像 iOS 里「带色标的分组行」，一眼能认出这是「可选项」而不是
            // 一个只读的显示框。高度取 52%、两端圆头，避免贴满上下边而显得像滚动条。
            if (Enabled)
            {
                float barH = Height * 0.52f;
                float barY = (Height - barH) * 0.5f;
                using (var pen = new Pen(Theme.Accent, Theme.S(3f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, Theme.S(8f), barY, Theme.S(8f), barY + barH);
                }
            }

            string text = SelectedText;
            var textRect = new RectangleF(Theme.S(18f), 0f, Width - Theme.S(46f), Height);
            using (var br = new SolidBrush(Enabled ? Theme.Ink : Theme.DisabledText))
            { g.DrawString(text, Font, br, textRect, Theme.Left); }

            // 展开箭头
            float ax = Width - Theme.S(22f), ay = Height / 2f - Theme.S(1f);
            using (var pen = new Pen(Enabled ? Theme.InkMid : Theme.DisabledText, Theme.S(1.6f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float d = Theme.S(4.5f), e2 = Theme.S(2f);
                if (_open)
                {
                    g.DrawLine(pen, ax - d, ay + e2, ax, ay - e2);
                    g.DrawLine(pen, ax, ay - e2, ax + d, ay + e2);
                }
                else
                {
                    g.DrawLine(pen, ax - d, ay - e2, ax, ay + e2);
                    g.DrawLine(pen, ax, ay + e2, ax + d, ay - e2);
                }
            }
        }

        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) { ClosePopup(); } Invalidate(); base.OnEnabledChanged(e); }
    }

    /// <summary>下拉弹出层：无边框、可点击外部关闭、键盘可操作。</summary>
    internal sealed class PopupList : Form
    {
        readonly List<string> _items;
        readonly int _initial;
        int _hoverIndex = -1;
        /// <summary>选中或取消时回调：索引 &gt;= 0 为选中项，-1 表示未选择即关闭。
        /// 注意：不能命名为 Closed，会遮蔽 Form.Closed 导致事件绑定歧义。</summary>
        public event Action<int> Picked;
        bool _closing;

        static int RowH { get { return Theme.S(32); } }

        public PopupList(List<string> items, int selected, int width)
        {
            _items = items;
            _initial = selected;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            int h = Math.Min(_items.Count, 9) * RowH + Theme.S(12);
            ClientSize = new Size(Math.Max(width, Theme.S(200)), h);
            Font = Theme.FName;
            KeyPreview = true;
            TopMost = true;
        }

        public void ShowAt(Point screen)
        {
            Location = screen;
            // 防止超出屏幕底部
            Screen sc = Screen.FromPoint(screen);
            if (Location.Y + Height > sc.WorkingArea.Bottom)
            {
                Location = new Point(screen.X, Math.Max(sc.WorkingArea.Top, sc.WorkingArea.Bottom - Height - 4));
            }
            Show();
            Activate();
            WndProcHook.MakeShadow(this);
        }

        public void CloseSelf()
        {
            if (_closing) { return; }
            _closing = true;
            var h = Picked;
            if (h != null) { h(-1); }
            Close();
        }

        void Commit(int idx)
        {
            if (_closing) { return; }
            _closing = true;
            var h = Picked;
            if (h != null) { h(idx); }
            Close();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CloseSelf();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = (e.Y - Theme.S(6)) / RowH;
            if (idx < 0 || idx >= _items.Count) { idx = -1; }
            if (idx != _hoverIndex) { _hoverIndex = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = (e.Y - Theme.S(6)) / RowH;
            if (idx >= 0 && idx < _items.Count) { Commit(idx); }
            else { CloseSelf(); }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { CloseSelf(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { _hoverIndex = Math.Min(_hoverIndex + 1, _items.Count - 1); Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { _hoverIndex = Math.Max(_hoverIndex - 1, 0); Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _hoverIndex >= 0) { Commit(_hoverIndex); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = Theme.TextHint;

            using (var path = Squircle.Create(0.5f, 0.5f, Width - 1f, Height - 1f, Theme.S(10f)))
            {
                using (var br = new SolidBrush(Theme.Surface)) { g.FillPath(br, path); }
                using (var pen = new Pen(Theme.Line, 1f)) { g.DrawPath(pen, path); }
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var row = new RectangleF(Theme.S(6f), Theme.S(6f) + i * RowH, Width - Theme.S(12f), RowH);
                bool hot = i == _hoverIndex;
                bool sel = i == _initial;
                if (hot)
                {
                    using (var p = Squircle.Create(row.X, row.Y, row.Width, row.Height, Theme.S(7f)))
                    using (var br = new SolidBrush(Theme.AccentSoft)) { g.FillPath(br, p); }
                }
                using (var br = new SolidBrush(sel ? Theme.AccentDark : Theme.Ink))
                {
                    var tr = new RectangleF(row.X + Theme.S(8f), row.Y, row.Width - (sel ? Theme.S(30f) : Theme.S(16f)), row.Height);
                    g.DrawString(_items[i], Font, br, tr, Theme.Left);
                }
                if (sel)
                {
                    // 打勾
                    using (var pen = new Pen(Theme.AccentDark, Theme.S(1.9f)))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        float cx = row.Right - Theme.S(16f), cy = row.Y + row.Height / 2f;
                        float d = Theme.S(4.5f), d2 = Theme.S(1.5f), d3 = Theme.S(3.5f);
                        g.DrawLine(pen, cx - d, cy, cx - d2, cy + d3);
                        g.DrawLine(pen, cx - d2, cy + d3, cx + d, cy - d3);
                    }
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;

                // 不要 CS_DROPSHADOW：矩形硬边阴影不认圆角、且只贴在右/下两侧。
                // 这里用 DWM 阴影（ShowAt 里的 WndProcHook.MakeShadow）+ 自绘圆角，
                // 定量依据见 tests\shadowab，详见 SettingsPanel.CreateParams 的注释。
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW 不占任务栏
                return cp;
            }
        }
    }

    /// <summary>
    /// 灰度抗锯齿文字标签（用来替代原生 Label）。
    ///
    /// 为什么不用原生 Label：Label 的文字由 GDI（TextRenderer）绘制，渲染方式跟随系统
    /// ClearType 设置，Graphics.TextRenderingHint 管不到它。于是「30 ms」这类粗体数字
    /// 在近白底上会拖出橙/蓝/青的彩色描边（实测 21 种颜色里 19 种是彩色，见
    /// tests/ui5/u1_runs.txt 与 Theme.TextHint 的说明）。
    ///
    /// 这里保留 Label 基类（AutoSize / TextAlign / MaximumSize / ForeColor / Text 全部照旧，
    /// 布局代码一个字都不用改），只是打开 UserPaint 把绘制接管过来，固定用 Theme.TextHint。
    /// 实测同一段文字：ClearType 21 色含 11 种彩边；AntiAliasGridFit 16 色全灰阶，
    /// 实心墨点从 107 增到 120，既干净也更实。
    /// </summary>
    internal sealed class SkinLabel : Label
    {
        public SkinLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        /// <summary>
        /// 显式底色（例如放在白卡片上的文字给 Theme.Surface）。
        /// 未设置时按父链解析，和 SkinnedControl 同一套规则。
        /// </summary>
        public Color? BackOverride { get; set; }

        /// <summary>背景自己画，所以不能让基类再画一遍（否则透明父底色会先铺进来，闪一下）。</summary>
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            Color back = BackOverride ?? ResolveBackColor();
            // Graphics.Clear 只取 RGB、忽略 alpha，所以这里必须是实色（上面两种来源都是）。
            g.Clear(back);

            if (string.IsNullOrEmpty(Text)) { return; }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = Theme.TextHint;

            using (var br = new SolidBrush(Enabled ? ForeColor : Theme.DisabledText))
            {
                g.DrawString(Text, Font, br, ClientRectangle, AlignFormat());
            }
        }

        /// <summary>同上：沿父链找第一个不透明的底色，跳过自绘控件的透明 BackColor。</summary>
        Color ResolveBackColor()
        {
            for (Control p = Parent; p != null; p = p.Parent)
            {
                var skin = p as SkinnedControl;
                if (skin != null)
                {
                    Color? fill = skin.SurfaceFillColor;
                    if (fill.HasValue) { return fill.Value; }
                }
                Color c = p.BackColor;
                if (c.A != 0) { return c; }
            }
            return Theme.Canvas;
        }

        /// <summary>把 Label 的 TextAlign 映射成 StringFormat（只用得到中/左/右三种）。</summary>
        StringFormat AlignFormat()
        {
            switch (TextAlign)
            {
                case ContentAlignment.TopRight:
                case ContentAlignment.MiddleRight:
                case ContentAlignment.BottomRight:
                    return Theme.Right;
                case ContentAlignment.TopCenter:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.BottomCenter:
                    return Theme.Center;
                default:
                    return Theme.Left;
            }
        }
    }
}
