using System;
using System.Text;
using System.Threading;

namespace DSH_Modbus采集工具.Startup
{
    /// <summary>
    /// 单实例保护：抢 <c>Global\</c> 命名互斥量；抢不到就把已有实例的窗口唤到前台。
    /// <para>
    /// **必须与 <see cref="Dispose"/> 成对使用**：互斥量由持有进程持有，进程正常退出时
    /// 内核自动释放，但**显式 Release + Dispose 更干净**（否则调试器反复启动会看到
    /// 「上一个实例还在」的假象，因为调试宿主进程可能没退）。
    /// </para>
    /// <para>
    /// 判重的唯一正确写法是 <see cref="Mutex.WaitOne(int)"/> 传 0（非阻塞）：
    /// <c>new Mutex(initiallyOwned, name, out createdNew)</c> 里的 <c>createdNew</c>
    /// **不能用来判重** —— 第二次创建时它恒为 <c>false</c>，只说明「这个名字已存在」，
    /// 完全不代表有别的进程持有它（名字会一直存在，直到所有句柄关闭）。
    /// </para>
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private Mutex? _mutex;
        private bool _ownsMutex;
        private bool _disposed;

        /// <summary>是否成功抢到（= 本进程是唯一实例）。</summary>
        public bool IsFirstInstance { get; private set; }

        private SingleInstanceGuard() { }

        /// <summary>
        /// 尝试成为唯一实例。
        /// <para>
        /// ⚠️ **三类异常都要当成「抢到了」**，否则一个已经死掉的旧实例能永久锁死程序：
        /// </para>
        /// <list type="bullet">
        /// <item><see cref="AbandonedMutexException"/> —— 上一个持有者被强杀 / 崩溃。
        /// 这是**唯一**会抛它的场景，语义正是「锁归你了」；</item>
        /// <item><see cref="UnauthorizedAccessException"/> —— 另一个**更高权限**的进程
        /// （例如以管理员身份跑的旧实例）持有同名对象，标准用户连打开句柄都不被允许。
        /// 此时按「已有实例」处理：本进程不该启动；</item>
        /// <item>其它异常（名字非法等）—— 属开发期错误，退化为「允许启动」比
        /// 让人打不开程序安全，同时返回原因供调用方记录。</item>
        /// </list>
        /// </summary>
        public static SingleInstanceGuard Acquire()
        {
            var guard = new SingleInstanceGuard();
            string name = SingleInstanceName.FullName;

            try
            {
                guard._mutex = new Mutex(initiallyOwned: false, name);

                bool acquired;
                try
                {
                    // 非阻塞：拿不到立刻返回 false，不要等 —— 等会让第二个实例卡几秒才退出
                    acquired = guard._mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个实例异常退出。异常本身就表示「所有权已转移给本线程」
                    acquired = true;
                }

                // UnauthorizedAccessException 走外层 catch：高权限进程持有同名对象时，
                // 连 new Mutex 都可能被拒 —— 那种情况按「已有实例」处理
                guard.IsFirstInstance = acquired;
                guard._ownsMutex = acquired;
            }
            catch (UnauthorizedAccessException)
            {
                // 另一个更高权限的实例在跑：视为「已有实例」，本进程不启动
                guard.IsFirstInstance = false;
                guard.FailureReason = "另一个以更高权限运行的实例正在使用该程序。";
            }
            catch (Exception ex)
            {
                // 兜底：宁可允许启动，也不要让人打不开程序
                guard.IsFirstInstance = true;
                guard.FailureReason = "单实例检查失败（已放行）：" + ex.Message;
            }

            return guard;
        }

        /// <summary>
        /// 未能抢到时的人类可读原因（成功时为 <c>null</c>）。
        /// <para>用于第二种失败路径（权限不足）给出比「已在运行」更准确的说法。</para>
        /// </summary>
        public string? FailureReason { get; private set; }

        /// <summary>
        /// 把已存在实例的窗口唤到前台并还原（若它最小化了）。
        /// <para>
        /// 返回是否找到并尝试唤起了窗口。**找不到窗口不算失败** —— 已有实例可能还在启动中
        /// （窗口尚未创建），调用方应退化为「弹提示」而不是静默退出。
        /// </para>
        /// </summary>
        public bool TryActivateExistingWindow()
        {
            IntPtr hwnd = WindowFinder.FindMainWindowOfSameExecutable();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            return WindowFinder.BringToFront(hwnd);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_ownsMutex && _mutex != null)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
                // ReleaseMutex 只在「本线程不持有」时抛；非拥有者路径不会走到这里
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略
            }

            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }
}