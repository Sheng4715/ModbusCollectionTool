using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DSH_Modbus采集工具.Startup;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    /// <summary>Interaction logic for App.xaml</summary>
    public partial class App : Application
    {
        private bool _fatalDialogShown;

        /// <summary>
        /// 单实例锁。**全程持有到进程退出**，不能放进 using —— 一旦释放，
        /// 第二个实例立刻就能起来，保护等于没有。
        /// </summary>
        private SingleInstanceGuard? _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例检查必须是**第一件事**，早于异常兜底、早于 ThemeManager.Initialize()：
            // 第二个实例不该碰任何配置（Initialize 会读 app-settings.json 并可能写盘），
            // 也不该在退出前弹一堆初始化相关的框。
            _singleInstance = SingleInstanceGuard.Acquire();
            if (!_singleInstance.IsFirstInstance)
            {
                RejectSecondInstance(_singleInstance);
                return; // 不调 base.OnStartup：StartupUri 不会再建主窗口
            }

            // 全局异常兜底必须**先于任何可能抛异常的初始化**注册（2026-11-08）：
            // 曾放在 ThemeManager.Initialize() 之后，若 ValidateKeyParity 在 Initialize
            // 内抛异常，兜底还没挂上 —— WinExe 无控制台，用户只看到窗口消失，crash.log 也不会写。
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 必须在主窗口构造前完成：窗口 XAML 里的 DynamicResource 要在第一次
            // 布局取值时就能找到键，晚于此会先按空值渲染一帧再跳变。
            ThemeManager.Initialize();

            base.OnStartup(e);
        }

        /// <summary>
        /// 已有实例在跑：尽量把它的窗口唤到前台，然后本进程退出。
        /// <para>
        /// **为什么不静默退出**：双击图标没任何反应，用户会以为程序坏了、再点几次，
        /// 甚至去重装。哪怕只是闪烁一下任务栏，也比「什么都没发生」好。
        /// </para>
        /// <para>
        /// 唤起失败（已有实例还在启动中、窗口未创建 / 前台锁定限制）时退化为**弹框提示**，
        /// 并带上 <see cref="SingleInstanceGuard.FailureReason"/>（权限不足那一路的说法不同）。
        /// </para>
        /// </summary>
        private void RejectSecondInstance(SingleInstanceGuard guard)
        {
            try
            {
                if (guard.TryActivateExistingWindow())
                {
                    Shutdown();
                    return;
                }

                string reason = guard.FailureReason ?? "程序已经在运行。";
                ThemeMessageDialog.Info(
                    owner: null,
                    "程序已在运行",
                    reason + "\n同一台计算机上只允许运行一个实例，请切换到已打开的窗口。");
            }
            catch
            {
                // 提示失败也绝不能留一个「什么都不做」的进程在这里：照常退出
            }
            finally
            {
                guard.Dispose();
                Shutdown();
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            args.Handled = true;
            ReportFatal("界面异常", args.Exception);
            ShowFatalNotice();
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
            ReportFatal("后台异常", args.ExceptionObject as Exception);

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            // 未被 await 的任务异常：标记已观察，避免进程被终止
            args.SetObserved();
            ReportFatal("异步任务异常", args.Exception);
        }

        /// <summary>
        /// 兜底报告：写一份崩溃日志到 exe 旁，并尽量提示用户。
        /// <para>
        /// **不再往上抛** —— 走到这里说明已有兜底义务；进程继续运行总比当场消失好，
        /// 用户至少还能保存现场、看到提示。
        /// </para>
        /// </summary>
        private static void ReportFatal(string category, Exception? ex)
        {
            string message = ex?.ToString() ?? "（无异常对象）";

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}]{Environment.NewLine}" +
                    message + Environment.NewLine + new string('-', 72) + Environment.NewLine);
            }
            catch
            {
                // 连日志都写不了（目录只读等）：放弃记录，别在兜底里再抛一次
            }
        }

        /// <summary>
        /// 给用户一个**可见**的提示：界面异常被吞掉后不能只剩一份没人知道在哪的 crash.log，
        /// 否则现场表现是「窗口还在但没反应」。
        /// <para>
        /// 去重：兜底里的弹窗自己也可能触发别的异常，用一个标志防止连环弹。
        /// </para>
        /// </summary>
        private void ShowFatalNotice()
        {
            if (_fatalDialogShown)
            {
                return;
            }

            _fatalDialogShown = true;
            try
            {
                ThemeMessageDialog.Info(
                    Application.Current?.MainWindow,
                    "程序出现异常",
                    "界面出现了一个异常，详情已写入程序目录下的 crash.log。\n程序将继续运行，建议保存现场后重启。");
            }
            catch
            {
                // 弹窗失败不再处理，避免在兜底里再抛
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 显式释放互斥量：进程退出内核也会回收，但显式释放能让「调试器反复启动」
            // 这个常见场景立刻恢复正常（调试宿主可能比被测进程活得久）
            _singleInstance?.Dispose();
            _singleInstance = null;
            base.OnExit(e);
        }
    }
}