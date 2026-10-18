# 生成 NSIS Modern UI 2 需要的两张位图
# 欢迎页侧栏: 164 x 314 (bmp, 24bit)
# 页眉图    : 150 x 57  (bmp, 24bit)
#
# 视觉来源：应用自身的新图标（design\app_icon_256.png，黑底圆角方块 + 白色线稿耳机少女）。
# 之前这两张图是「白卡片 + 五根黑音量条」，那是旧图标的构成；换图标后必须同步，
# 否则安装向导还在展示一个用户装完之后根本看不到的旧标志。
#
# 注意：本文件必须带 UTF-8 BOM。Windows PowerShell 5.1 会把无 BOM 的 .ps1 按 ANSI
# 解析，文件里的中文字符串会直接让解析器报「字符串缺少终止符」。
Add-Type -AssemblyName System.Drawing

$root   = $PSScriptRoot
$outDir = $root
$iconPath = Join-Path $root '..\..\design\app_icon_256.png'
$iconPath = [System.IO.Path]::GetFullPath($iconPath)
if (-not (Test-Path $iconPath)) {
    throw "找不到新图标：$iconPath（先运行 design\make_app_icon.py）"
}

$INK   = [System.Drawing.Color]::FromArgb(17, 20, 26)      # #11141A 应用主 Ink
$WHITE = [System.Drawing.Color]::FromArgb(255, 255, 255)
$ACCENT= [System.Drawing.Color]::FromArgb(0, 112, 236)     # #0070EC 应用 Accent

$srcIcon = [System.Drawing.Image]::FromFile($iconPath)

# 高质量缩放的默认参数：这几档设置（双三次 + 高质量合成 + 像素偏移半格）
# 是把 256px 图标缩到 100px 左右时边缘不发糊的关键。
#
# 文字渲染用 AntiAliasGridFit 而**不是** ClearTypeGridFit：
# ClearType 是次像素渲染，靠让 R/G/B 三个子像素分别亮起来提升锐度，
# 在深色渐变背景上的白色小字会因此出现明显的红/蓝彩色镶边
# （用户实测反馈「安装包字显示的有问题」即此）。这两张图是位图，
# 一旦生成就固定下来，不像屏幕文字还能随系统设置变，所以必须用灰度抗锯齿。
function Set-HiQuality($g) {
    $g.SmoothingMode        = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode    = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode      = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality   = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.TextRenderingHint    = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
}

function Save-Bmp($bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
}

# ─────────────── 小字号中文的 4 倍超采样渲染 ───────────────
# 问题：这两张图上的中文只有 9~15px。CJK 字形在 12px 以下每个字只分到
# 12x12 个像素，而「频」「输」「路」这类字有十几笔，直接 DrawString 必然
# 糊成一团（实测「多路音频输出」在 11px 下笔画全部粘连，「并」字丢顶部两点）。
# 解法：按 4 倍字号画到透明画布，再盒式降采样回 1:1。4 倍空间里每个字有
# 48x48 像素，笔画完全展开；盒式平均正好等于覆盖率，缩回来锐利且不丢笔。
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class TextDownsampler
{
    // 颜色必须按 alpha 加权求平均。超采样画布是透明的，全透明像素 RGB 为 0，
    // 若直接平均 RGB，字形边缘会被压黑一圈（深色底上看不出来，浅色底上是脏边）。
    public static Bitmap BoxDown(Bitmap src, int factor)
    {
        int w = src.Width / factor;
        int h = src.Height / factor;
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var sr = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dr = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int sstride = sr.Stride, dstride = dr.Stride;
        byte[] sdata = new byte[sstride * src.Height];
        byte[] ddata = new byte[dstride * h];
        Marshal.Copy(sr.Scan0, sdata, 0, sdata.Length);

        int n = factor * factor;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long sa = 0, sr2 = 0, sg2 = 0, sb2 = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * sstride;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = row + (x * factor + dx) * 4;
                        int a = sdata[i + 3];
                        sa += a;
                        sb2 += a * sdata[i + 0];
                        sg2 += a * sdata[i + 1];
                        sr2 += a * sdata[i + 2];
                    }
                }
                int di = y * dstride + x * 4;
                if (sa <= 0)
                {
                    ddata[di] = 0; ddata[di + 1] = 0; ddata[di + 2] = 0; ddata[di + 3] = 0;
                }
                else
                {
                    ddata[di + 3] = (byte)(sa / n);
                    ddata[di + 2] = (byte)(sr2 / sa);
                    ddata[di + 1] = (byte)(sg2 / sa);
                    ddata[di + 0] = (byte)(sb2 / sa);
                }
            }
        }

        Marshal.Copy(ddata, 0, dr.Scan0, ddata.Length);
        dst.UnlockBits(dr);
        src.UnlockBits(sr);
        return dst;
    }
}
"@

# 在 $g 的 $rect 区域画一行文字（4 倍超采样 + 盒式降采样）
function Draw-TextSmooth {
    param($g, [string]$text, [int]$px, [string]$style,
          [System.Drawing.Color]$color, [System.Drawing.RectangleF]$rect, $sf, [int]$super = 4)

    $w = [int][Math]::Ceiling($rect.Width)
    $h = [int][Math]::Ceiling($rect.Height)
    if ($w -le 0 -or $h -le 0) { return }

    $fs = [System.Drawing.FontStyle]::Regular
    if ($style -eq 'Bold') { $fs = [System.Drawing.FontStyle]::Bold }

    $big = New-Object System.Drawing.Bitmap(($w * $super), ($h * $super), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bg = [System.Drawing.Graphics]::FromImage($big)
    $bg.Clear([System.Drawing.Color]::Transparent)
    $bg.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $bg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $fBig = New-Object System.Drawing.Font("Microsoft YaHei UI", ($px * $super), $fs, [System.Drawing.GraphicsUnit]::Pixel)
    $br   = New-Object System.Drawing.SolidBrush($color)
    $rBig = New-Object System.Drawing.RectangleF(0, 0, ($w * $super), ($h * $super))
    $bg.DrawString($text, $fBig, $br, $rBig, $sf)
    $bg.Dispose(); $fBig.Dispose(); $br.Dispose()

    $small = [TextDownsampler]::BoxDown($big, $super)
    $big.Dispose()
    $g.DrawImage($small, [int]$rect.X, [int]$rect.Y)
    $small.Dispose()
}

# ───────────────────────── 1) 欢迎页侧栏 164x314 ─────────────────────────
$w = 164; $h = 314
$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
Set-HiQuality $g

# 竖向渐变：上方近黑，下方略抬亮，避免一整块死黑
$rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(28, 32, 41),
    [System.Drawing.Color]::FromArgb(8, 10, 14),
    [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
$g.FillRectangle($bg, $rect)
$bg.Dispose()

# 顶部一抹极淡的光晕，模拟图标的高光质感
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(-70, -110, 310, 240)
$gp = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$gp.CenterColor = [System.Drawing.Color]::FromArgb(30, 255, 255, 255)
$gp.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$g.FillPath($gp, $glow)
$gp.Dispose(); $glow.Dispose()

# 应用图标本体（图自带透明圆角，叠在深色渐变上即可，不需要再画卡片底）
$iconSize = 104
$iconX = [int](($w - $iconSize) / 2)
$iconY = 62
# 先垫一层柔和投影，让图标从背景里浮起来。用圆角矩形而不是椭圆：
# 图标的轮廓是圆角方块，椭圆的暗影会在四角露出与形状不符的黑边。
$shadowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 26; $d = $r * 2
$sx = $iconX + 3; $sy = $iconY + 6; $sw = $iconSize; $sh = $iconSize
$shadowPath.AddArc($sx, $sy, $d, $d, 180, 90)
$shadowPath.AddArc($sx + $sw - $d, $sy, $d, $d, 270, 90)
$shadowPath.AddArc($sx + $sw - $d, $sy + $sh - $d, $d, $d, 0, 90)
$shadowPath.AddArc($sx, $sy + $sh - $d, $d, $d, 90, 90)
$shadowPath.CloseFigure()
$shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
$g.FillPath($shadow, $shadowPath)
$shadow.Dispose(); $shadowPath.Dispose()

$g.DrawImage($srcIcon, $iconX, $iconY, $iconSize, $iconSize)

# 文案
# 字号从 15/11 提到 18/13：这两个尺寸是实测出来的下限。
# 「并」字在 15px 时顶部两点会并成一条横杠，「多路音频输出」在 11px 时
# 「频」「输」的笔画互相粘连，读不出字。18/13 配上 4 倍超采样后笔画完整。
$cTitle = [System.Drawing.Color]::FromArgb(255, 255, 255)
$cSub   = [System.Drawing.Color]::FromArgb(168, 255, 255, 255)
$cFaint = [System.Drawing.Color]::FromArgb(110, 255, 255, 255)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center

Draw-TextSmooth -g $g -text "并川听澜" -px 22 -style 'Bold' -color $cTitle `
    -rect (New-Object System.Drawing.RectangleF(0, 185, $w, 34)) -sf $sf
Draw-TextSmooth -g $g -text "多路音频输出" -px 15 -style 'Regular' -color $cSub `
    -rect (New-Object System.Drawing.RectangleF(0, 219, $w, 24)) -sf $sf

# 版本胶囊
$pill = New-Object System.Drawing.Drawing2D.GraphicsPath
$pill.AddArc(50, 258, 20, 20, 180, 90)
$pill.AddArc(94, 258, 20, 20, 270, 90)
$pill.AddArc(94, 258, 20, 20, 0, 90)
$pill.AddArc(50, 258, 20, 20, 90, 90)
$pill.CloseFigure()
$pillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38, 255, 255, 255))
$g.FillPath($pillBrush, $pill)
$pillBrush.Dispose(); $pill.Dispose()
Draw-TextSmooth -g $g -text "v1.2" -px 11 -style 'Regular' -color $cFaint `
    -rect (New-Object System.Drawing.RectangleF(50, 259, 64, 18)) -sf $sf

# 底部品牌线（9px 太小，「一键安装」四个字会糊；提到 10px 并用超采样）
Draw-TextSmooth -g $g -text "一键安装 · Windows 10 / 11" -px 10 -style 'Regular' -color $cFaint `
    -rect (New-Object System.Drawing.RectangleF(0, 289, $w, 16)) -sf $sf

$g.Dispose()
Save-Bmp $bmp (Join-Path $outDir "welcome.bmp")
$bmp.Dispose()

# ───────────────────────── 2) 页眉图 150x57 ─────────────────────────
$w2 = 150; $h2 = 57
$bmp2 = New-Object System.Drawing.Bitmap($w2, $h2, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
Set-HiQuality $g2

$rect2 = New-Object System.Drawing.Rectangle(0, 0, $w2, $h2)
$bg2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect2,
    [System.Drawing.Color]::FromArgb(18, 21, 27),
    [System.Drawing.Color]::FromArgb(30, 34, 43),
    [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
$g2.FillRectangle($bg2, $rect2)
$bg2.Dispose()

# 图标全部落在右侧，不和左边标题抢地方
$hs = 42
$g2.DrawImage($srcIcon, ($w2 - $hs - 8), [int](($h2 - $hs) / 2), $hs, $hs)

# 页眉只有 150x57，图标占右侧 42px，文字区实际只剩 x=12..96 共 84px。
# 实测宽度：并川听澜 Bold 18px=80.1 / 多路音频输出 Reg 12px=78.1，
# 是能塞进 84px 的最大可用组合（再大就会撞到图标）。
$cH1 = [System.Drawing.Color]::FromArgb(250, 251, 253)
$cH2 = [System.Drawing.Color]::FromArgb(148, 196, 205, 219)
$sfL = New-Object System.Drawing.StringFormat
$sfL.Alignment = [System.Drawing.StringAlignment]::Near
$sfL.LineAlignment = [System.Drawing.StringAlignment]::Center

Draw-TextSmooth -g $g2 -text "并川听澜" -px 18 -style 'Bold' -color $cH1 `
    -rect (New-Object System.Drawing.RectangleF(12, 5, 84, 26)) -sf $sfL
Draw-TextSmooth -g $g2 -text "多路音频输出" -px 12 -style 'Regular' -color $cH2 `
    -rect (New-Object System.Drawing.RectangleF(12, 31, 84, 18)) -sf $sfL

$g2.Dispose()
Save-Bmp $bmp2 (Join-Path $outDir "header.bmp")
$bmp2.Dispose()

$srcIcon.Dispose()

"OK welcome=$((Get-Item (Join-Path $outDir 'welcome.bmp')).Length) header=$((Get-Item (Join-Path $outDir 'header.bmp')).Length)"
