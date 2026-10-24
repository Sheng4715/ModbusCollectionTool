using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 主窗口的圆角与边缘线 —— 两者都交给 DWM 画。
    ///
    /// <para>
    /// **为什么不用 XAML 自绘**：窗口是 <see cref="System.Windows.Shell.WindowChrome"/> 自绘标题栏，
    /// 且 <c>GlassFrameThickness=0</c> + <c>NonClientFrameEdges=None</c>，系统既不画边框也不画圆角。
    /// 最初试过在 <c>RootGrid</c> 上盖一条 <c>Border</c> 当边缘线，**在加圆角后翻车**：
    /// 一旦开启 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>，DWM 会自己在窗口最外层补一圈 1px 边框，
    /// 于是自绘描边被盖住（实测边缘色是 DWM 的 <c>#B5B5B6</c>，而不是设的 <c>#C8C8C8</c>），
    /// 圆角处还会露出两道线。**不要再走自绘那条路。**
    /// </para>
    /// <para>
    /// 现在两件事都由 DWM 做：<c>DWMWA_WINDOW_CORNER_PREFERENCE</c> 管圆角、
    /// <c>DWMWA_BORDER_COLOR</c> 管边缘线颜色。好处是圆角与边框天然同心同半径，
    /// 最大化时 DWM 自动收掉圆角并让边框贴边，**不需要**在窗口状态变化时手工同步任何值。
    /// </para>
    /// <para>
    /// 老系统（Win10）上这两个属性号不认识，<c>DwmSetWindowAttribute</c> 返回非 0。
    /// 此时**安静放弃**：窗口保持直角无边框，不影响任何功能 —— 不该为装饰让程序起不来。
    /// </para>
    /// </summary>
    public static class WindowCorners
    {
        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（Windows 11 build 22000 起）。</summary>
        private const int DwmWindowCornerPreference = 33;

        /// <summary>DWMWA_BORDER_COLOR（Windows 11 build 22000 起）。</summary>
        private const int DwmWindowBorderColor = 34;

        /// <summary>DWMWCP_ROUND —— 系统标准圆角（随 DPI 缩放）。</summary>
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// 在 <c>Loaded</c> 里调用一次（句柄要 <c>SourceInitialized</c> 之后才存在）。
        /// 换主题后调 <see cref="SyncBorderColor"/> 重设颜色即可，圆角不用管。
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));

            SyncBorderColor(window);
        }

        /// <summary>
        /// 按主题里的 <c>Brush.Window.Border</c> 重设边缘线颜色。
        /// <para>
        /// ⚠️ 颜色必须**从资源里取**而不是在这里写死：深浅两套主题的值不同，
        /// 写死就意味着换主题后边缘线还是旧色。资源缺失时安静跳过（保持系统默认色）。
        /// </para>
        /// </summary>
        public static void SyncBorderColor(Window window)
        {
            if (window == null)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!(window.TryFindResource("Brush.Window.Border") is SolidColorBrush brush))
            {
                return;
            }

            // DWM 要的是 COLORREF：0x00BBGGRR（注意是 BGR，不是 WPF 的 RGB）
            Color c = brush.Color;
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, DwmWindowBorderColor, ref colorRef, sizeof(int));
        }
    }
}
