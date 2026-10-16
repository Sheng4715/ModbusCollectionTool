using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 最大化时的「溢出补偿」。
    /// <para>
    /// 用 <see cref="WindowChrome"/> 自绘标题栏时，系统会把最大化窗口的四边各外扩一个窗口边框宽度
    /// （本机 150% DPI 实测 11px：窗口 -11,-11 ~ 1931,1219，而工作区 0,0 ~ 1920,1208），
    /// 把窗口底边推到任务栏之下；WindowChrome 下客户区覆盖整个窗口，于是底栏最下一条被任务栏遮住
    /// （表现为「底栏偏下、被切」）。
    /// </para>
    /// <para>
    /// ⚠️ <b>拦 <c>WM_GETMINMAXINFO</c> 改 <c>ptMaxSize</c> 的方案无效</b>（已试过）：钩子确实被
    /// 调用，但系统对带边框的最大化窗口仍按自己的规则外扩，写回值被忽略。<b>不要再走这条路</b>。
    /// </para>
    /// <para>
    /// 故改为确定性补偿：把根元素四边各内缩「外扩量」，内容正好落进工作区。全部用<b>物理像素</b>
    /// 测量再按窗口 DPI 换算，不依赖 WPF 布局时序；多显示器下由 <c>MonitorFromWindow</c>
    /// 取窗口所在那台。
    /// </para>
    /// </summary>
    public static class MaximizedInset
    {
        private const int MonitorDefaultToNearest = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        /// <summary>
        /// 按当前窗口矩形与所在显示器工作区之差，给 <paramref name="root"/> 设/清内缩量。
        /// 非最大化时清空内缩。
        /// </summary>
        public static void Apply(Window window, FrameworkElement root)
        {
            if (root == null)
            {
                return;
            }

            if (window.WindowState != WindowState.Maximized)
            {
                root.Margin = new Thickness(0);
                return;
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;
            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            // MONITORINFO 必须填 cbSize，否则 GetMonitorInfo 失败
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out NativeRect windowRect))
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(window);
            double overX = ((windowRect.Right - windowRect.Left) - (info.Work.Right - info.Work.Left)) / 2.0 / dpi.DpiScaleX;
            double overY = ((windowRect.Bottom - windowRect.Top) - (info.Work.Bottom - info.Work.Top)) / 2.0 / dpi.DpiScaleY;

            root.Margin = overX > 0 || overY > 0
                ? new Thickness(Math.Max(0, overX), Math.Max(0, overY), Math.Max(0, overX), Math.Max(0, overY))
                : new Thickness(0);
        }
    }
}
