using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiAudioOutput
{
    /// <summary>
    /// Win32 互操作：无边框窗口圆角、阴影、点击穿透命中测试、拖拽。
    /// 全部通过 P/Invoke，失败时静默降级（不影响功能）。
    /// </summary>
    internal static class WndProcHook
    {
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        // 起名 ShowWindowNative 而不是 ShowWindow：Control 上有同名的实例方法，
        // 直接叫 ShowWindow 会让调用点产生歧义，编译器也会报 CS0108。
        [DllImport("user32.dll")] static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);
        const int SW_HIDE = 0;
        const int SW_SHOWNOACTIVATE = 4;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_NCHITTEST = 0x0084;
        public const int HTCAPTION = 2;
        public const int HTCLIENT = 1;
        public const int HTBOTTOMRIGHT = 17;

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_SYSMENU = 0x00080000;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CLOAK = 13;

        /// <summary>给无边框窗口加 DWM 阴影（Visible 时才有效）。</summary>
        public static void EnableShadow(Form f)
        {
            try
            {
                var margins = new MARGINS { Left = 0, Right = 0, Top = 1, Bottom = 0 };
                DwmExtendFrameIntoClientArea(f.Handle, ref margins);
            }
            catch { }
        }

        /// <summary>Win11+ 让 DWM 自己画系统级圆角（最平滑），失败则回退 GDI Region。</summary>
        public static bool TryNativeRounded(Form f, int radius)
        {
            try
            {
                int pref = DWMWCP_ROUND;
                int hr = DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                if (hr == 0)
                {
                    // 边框颜色跟随主题，避免 1px 高亮描边
                    int border = ColorTranslator.ToWin32(Theme.Line);
                    DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>无 DWM 时的圆角回退：GDI Region（边缘为硬锯齿，但形状正确）。</summary>
        public static void ApplyRegion(Form f, int radius)
        {
            try
            {
                IntPtr rgn = CreateRoundRectRgn(0, 0, f.Width + 1, f.Height + 1, radius * 2, radius * 2);
                if (rgn != IntPtr.Zero)
                {
                    f.Region = Region.FromHrgn(rgn);
                    DeleteObject(rgn);
                }
            }
            catch { }
        }

        /// <summary>让整个窗口可拖动（点击非交互区域时）。</summary>
        public static void DragWindow(IntPtr handle)
        {
            try
            {
                ReleaseCapture();
                SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>保留窗口的调整大小能力（无边框窗口需要声明厚边框样式）。</summary>
        public static void KeepResizable(Form f)
        {
            try
            {
                int style = GetWindowLong(f.Handle, GWL_STYLE);
                SetWindowLong(f.Handle, GWL_STYLE, style | WS_THICKFRAME | WS_MINIMIZEBOX | WS_SYSMENU);
            }
            catch { }
        }

        /// <summary>给弹出层加 DWM 阴影。</summary>
        public static void MakeShadow(Form f)
        {
            try
            {
                // 1 = CS_DROPSHADOW 已由 CreateParams 提供，这里补 DWM 阴影
                var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 1 };
                DwmExtendFrameIntoClientArea(f.Handle, ref margins);
            }
            catch { }
        }

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_HIDEWINDOW = 0x0080;

        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rcUpdate, IntPtr rcUpdateRgn, uint flags);
        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ERASE = 0x0004;
        const uint RDW_ALLCHILDREN = 0x0080;
        const uint RDW_UPDATENOW = 0x0100;
        const uint RDW_FRAME = 0x0400;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        const int WPF_RESTORETOMAXIMIZED = 0x0002;

        /// <summary>
        /// 窗口在被最小化之前是不是「最大化」状态。
        /// 恢复时必须把它还原成原样：若一律设成 Normal，用户从最大化最小化后再点
        /// 任务栏，窗口就会莫名变成普通大小。WINDOWPLACEMENT.flags 的
        /// WPF_RESTORETOMAXIMIZED 位就是记录这件事的。
        /// </summary>
        public static bool WasMaximizedBeforeMinimize(Form f)
        {
            try
            {
                var wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (GetWindowPlacement(f.Handle, ref wp))
                {
                    return (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 同步重画整棵控件树：RedrawWindow 直接给窗口与全部子窗口派发 WM_PAINT，
        /// 不需要走 Application.DoEvents（后者会重入消息泵，在 WndProc 内部不安全）。
        /// 恢复窗口时用它把所有子控件的第一帧画出来，然后再显示窗口。
        /// </summary>
        public static void ForceRepaint(Form f)
        {
            try
            {
                RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch { }
        }

        /// <summary>
        /// 采样某个窗口自身绘制表面的统计量（唯一色数 + 深色像素占比）。
        /// 仅用于自动化诊断：GetDC+BitBlt 读的是窗口自己维护的表面，既不触发重画
        /// 也不受遮挡影响，是判断「这一帧画好了没有」的唯一可信口径
        /// （CopyFromScreen 会抓到别的窗口，PrintWindow 会强制重画把问题掩盖掉）。
        /// </summary>
        public static bool SampleSurface(IntPtr hwnd, out int colors, out double darkPct)
        {
            colors = 0;
            darkPct = 0;
            if (hwnd == IntPtr.Zero) { return false; }
            IntPtr hdc = IntPtr.Zero;
            IntPtr mem = IntPtr.Zero;
            IntPtr bmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                RECT rc;
                if (!GetClientRect(hwnd, out rc)) { return false; }
                int w = rc.Right - rc.Left;
                int h = rc.Bottom - rc.Top;
                if (w <= 0 || h <= 0) { return false; }

                hdc = GetDC(hwnd);
                if (hdc == IntPtr.Zero) { return false; }
                mem = CreateCompatibleDC(hdc);
                bmp = CreateCompatibleBitmap(hdc, w, h);
                old = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, w, h, hdc, 0, 0, SRCCOPY)) { return false; }

                var bmi = new BITMAPINFO();
                bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFO));
                bmi.biWidth = w;
                bmi.biHeight = -h;
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                bmi.biCompression = 0;
                var bits = new byte[w * h * 4];
                if (GetDIBits(mem, bmp, 0, (uint)h, bits, ref bmi, 0) == 0) { return false; }

                var set = new System.Collections.Generic.HashSet<int>();
                long dark = 0;
                long total = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int o = row + x * 4;
                        int b = bits[o], g = bits[o + 1], r = bits[o + 2];
                        set.Add(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                        if ((r * 299 + g * 587 + b * 114) / 1000 < 64) { dark++; }
                        total++;
                    }
                }
                colors = set.Count;
                darkPct = total == 0 ? 0 : (dark * 100.0 / total);
                return true;
            }
            catch { return false; }
            finally
            {
                if (mem != IntPtr.Zero && old != IntPtr.Zero) { try { SelectObject(mem, old); } catch { } }
                if (bmp != IntPtr.Zero) { try { DeleteObject(bmp); } catch { } }
                if (mem != IntPtr.Zero) { try { DeleteDC(mem); } catch { } }
                if (hdc != IntPtr.Zero) { try { ReleaseDC(hwnd, hdc); } catch { } }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
            public int colors;
        }

        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
        const int SRCCOPY = 0x00CC0020;

        /// <summary>给诊断日志写一行（仅当 MAB_RESTORE_LOG 指向文件路径时生效）。</summary>
        public static void DiagLog(string line)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("MAB_RESTORE_LOG");
                if (string.IsNullOrEmpty(path)) { return; }
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\r\n");
            }
            catch { }
        }

        /// <summary>
        /// 把窗口整体设成指定的不透明度（0 = 完全透明，255 = 不透明）。
        ///
        /// 这是「恢复瞬间不露未绘制画面」的最终方案，也是 tests\paintline3 里唯一
        /// 做到零坏帧的一种。为什么其它路子都不行：
        ///   · SW_HIDE：隐藏窗口收不到 WM_PAINT，重画空转；
        ///   · WM_SETREDRAW(FALSE)：冻结后表面变纯黑；
        ///   · DWMWA_CLOAK：只对其他进程的窗口有效，同进程读回 -1；
        ///   · SetWindowPos 挪到屏幕外 / 改 WINDOWPLACEMENT：WinForms 切
        ///     WindowState 时自己会按内部缓存的还原矩形显示窗口，拦不住。
        /// 分层窗口（WS_EX_LAYERED + LWA_ALPHA）则是「窗口照常可见、照常收 WM_PAINT、
        /// 照常绘制」，只是 DWM 合成时把 alpha 乘上去 —— alpha=0 时用户屏幕上什么都
        /// 没有，而窗口自身的绘制表面一切正常。
        ///
        /// 刻意不用 Form.Opacity：它在值回到 1.0 时会摘掉 WS_EX_LAYERED，触发一次
        /// 边框重算，正好又把窗口闪一下。这里始终保留分层样式，只改 alpha。
        /// </summary>
        public static void SetWindowAlpha(Form f, byte alpha)
        {
            try
            {
                if (f == null || !f.IsHandleCreated) { return; }
                int ex = GetWindowLong(f.Handle, GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0)
                {
                    SetWindowLong(f.Handle, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                }
                SetLayeredWindowAttributes(f.Handle, 0, alpha, LWA_ALPHA);
            }
            catch { }
        }

        /// <summary>
        /// 查询窗口当前的不透明度。返回 -1 表示窗口不是分层窗口（即完全不透明）。
        /// 用途：探针要区分「用户看得见的帧」与「产品故意设成全透明的过渡帧」。
        /// </summary>
        public static int GetWindowAlpha(IntPtr hwnd)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_LAYERED) == 0) { return -1; }
                byte a;
                int flags;
                uint key;
                if (!GetLayeredWindowAttributes(hwnd, out key, out a, out flags)) { return -1; }
                if ((flags & LWA_ALPHA) == 0) { return -1; }
                return a;
            }
            catch { return -1; }
        }

        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, int flags);
        [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint colorKey, out byte alpha, out int flags);
        // GWL_EXSTYLE 已在文件顶部定义（:41），这里不能重复声明。
        const int WS_EX_LAYERED = 0x00080000;
        const int LWA_ALPHA = 0x00000002;

        /// <summary>查询窗口当前的 cloak 状态。返回 -1 表示查询失败（DWM 不可用），否则 0/1。
        /// 用途：确认 SetCloak 真的生效了 —— DwmSetWindowAttribute 返回 0 只代表调用
        /// 被接受，不代表状态已改；只有读回来是 1 才算数。
        /// </summary>
        public static int GetCloak(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) { return -1; }
                int value;
                int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAK, out value, sizeof(int));
                if (hr != 0) { return -1; }
                return value != 0 ? 1 : 0;
            }
            catch { return -1; }
        }

        // ───────────── 启动前的 DPI 探测 ─────────────
        //
        // 为什么不能直接用 Form.DeviceDpi：
        //   它是 Control 的属性，在窗口句柄创建之前读到的永远是 96（默认值）。
        //   MainForm 必须在 BuildUi 之前就知道缩放系数，否则会先按 100% 排一遍布局
        //   （尺寸、圆角、字体间距全部偏小），等句柄建好、DeviceDpi 变成 144 时才
        //   发现自己排错了 —— 而那时控件已经按错误尺寸创建完毕。
        //
        // 这里改问主显示器：进程已声明 permonitorv2，屏幕 DC 的 LOGPIXELSX 就是
        // 主显示器当前缩放对应的 DPI（96=100%，120=125%，144=150%，192=200%）。
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
        const int LOGPIXELSX = 88;

        /// <summary>主显示器的 DPI（96 = 100%）。失败时回退 96。</summary>
        public static int SystemDpi()
        {
            // 仅用于自动化测试：MAB_TEST_DPI=144 可强制按 150% 排布局。
            // 开发机通常是 100% 缩放，没有这个开关就无法在 CI/回归里验证
            // 高分屏下盒子是否够大、文字是否溢出（tests\dpilayout.exe 用它）。
            // 不设这个环境变量时行为完全不变。
            try
            {
                string forced = Environment.GetEnvironmentVariable("MAB_TEST_DPI");
                int v;
                if (!string.IsNullOrEmpty(forced)
                    && int.TryParse(forced, out v)
                    && v >= 48 && v <= 480)
                {
                    return v;
                }
            }
            catch { }

            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) { return 96; }
                int dpi = GetDeviceCaps(dc, LOGPIXELSX);
                return (dpi >= 48 && dpi <= 480) ? dpi : 96;
            }
            catch { return 96; }
            finally
            {
                if (dc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, dc); } catch { } }
            }
        }
    }
}
