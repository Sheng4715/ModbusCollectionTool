using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 对话框的统一外壳：卡片底色、统一字体、**深色主题下标题栏也跟着变深**。
    ///
    /// <para>
    /// **为什么标题栏要单独处理**：客户区里的控件换主题是 DynamicResource 的事，
    /// 但标题栏是 DWM 画的非客户区，WPF 管不到。不调 <c>DwmSetWindowAttribute</c>
    /// 的话，深色主题下会变成「白色标题栏 + 深色内容」，实测对话框顶部一整条
    /// <c>#EEF4F9</c> 横在深色卡片上方，非常割裂。
    /// </para>
    /// <para>
    /// **为什么两个属性号都试**：<c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> 在 Windows 10
    /// 20H1 之前是 19，之后是 20。老系统上传 20 会返回失败（非 0），此时退回 19。
    /// 两个都失败就安静放弃 —— 那只是「标题栏没变深」，不该让对话框打不开。
    /// </para>
    /// </summary>
    public static class DialogChrome
    {
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeLegacy = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// 在构造对话框时调用（<c>ShowDialog</c> 之前）。
        /// <para>幂等：重复调用不会重复挂事件（每个窗口只该调一次，这里不额外做保护）。</para>
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            window.FontSize = 12;
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Card.Background");

            // 句柄在 SourceInitialized 之后才存在；Loaded 再刷一次，覆盖「创建后被换主题」
            void Sync() => ApplyTitleBar(window);

            window.SourceInitialized += (_, _) => Sync();
            window.Loaded += (_, _) => Sync();

            // 对话框是模态的，正常不会在打开期间换主题；但主窗口若从别处改了主题，
            // 这里跟着刷新，免得下次打开时是旧色。Closed 时摘掉，避免长命静态事件泄漏。
            ThemeManager.ThemeChanged += Sync;
            window.Closed += (_, _) => ThemeManager.ThemeChanged -= Sync;
        }

        private static void ApplyTitleBar(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int dark = ThemeManager.IsDarkEffective ? 1 : 0;

            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }
        }
    }
}
