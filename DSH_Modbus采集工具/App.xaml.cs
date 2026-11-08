using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    /// <summary>Interaction logic for App.xaml</summary>
    public partial class App : Application
    {
        private bool _fatalDialogShown;

        protected override void OnStartup(StartupEventArgs e)
        {
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
    }
}