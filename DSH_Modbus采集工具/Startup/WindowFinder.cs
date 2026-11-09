using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DSH_Modbus采集工具.Startup
{
    /// <summary>
    /// 找「本程序的另一个实例」的顶层窗口，并把它唤到前台。
    /// <para>
    /// **为什么不用 <c>Process.MainWindowHandle</c>**：它只在**本进程**对象上有意义；
    /// 对别人进程要 <c>Process.GetProcessesByName</c> 再逐个取 <c>MainWindowHandle</c>，
    /// 而多实例并存时取值不确定、启动早期还可能是 <c>IntPtr.Zero</c>（窗口尚未创建）。
    /// 直接枚举顶层窗口 + 按「进程可执行文件路径相同」筛选更准确。
    /// </para>
    /// <para>
    /// 判据用**完整路径**而不是进程名：现场可能同时放着两个版本的发布目录，
    /// 各有一个同名 exe —— 但那属于「同一程序被复制了两份」，按需求也该互相拦住，
    /// 故这里只要求路径相同即可，不要求版本相同。
    /// </para>
    /// </summary>
    internal static class WindowFinder
    {
        private const int SW_RESTORE = 9;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 找一个属于「本程序、但不是本进程」的可见顶层窗口。找不到返回 <see cref="IntPtr.Zero"/>。
        /// </summary>
        public static IntPtr FindMainWindowOfSameExecutable()
        {
            string? selfPath = TryGetSelfPath(out int selfPid);
            if (selfPath == null)
            {
                return IntPtr.Zero;
            }

            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true; // 继续找
                }

                // 有标题的才算「主窗口」：隐藏的辅助窗口 / 消息窗没有标题
                if (GetWindowTextLength(hWnd) == 0)
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out uint pid);
                if ((int)pid == selfPid)
                {
                    return true; // 自己，跳过
                }

                string? otherPath = TryGetProcessPath((int)pid);
                if (otherPath == null)
                {
                    return true;
                }

                if (string.Equals(otherPath, selfPath, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false; // 找到即停
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>还原（若最小化）并尝试置前。返回值只表示「调用过」，不保证真的到了前台。</summary>
        public static bool BringToFront(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }

                // ⚠️ SetForegroundWindow 有前台锁定限制：本进程不是当前前台应用时，
                // 系统可能只让任务栏按钮闪烁而不真置前。这不是 bug，也无需兜底 ——
                // 用户至少看得见它在闪，比静默退出好得多。
                return SetForegroundWindow(hWnd);
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetSelfPath(out int selfPid)
        {
            selfPid = Environment.ProcessId;
            try
            {
                // 优先 Environment.ProcessPath（apphost 形态下就是自己的 exe）
                return Environment.ProcessPath;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetProcessPath(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                return p.MainModule?.FileName;
            }
            catch
            {
                // 权限不足 / 进程已退出：跳过这一个，不影响其它候选
                return null;
            }
        }
    }
}