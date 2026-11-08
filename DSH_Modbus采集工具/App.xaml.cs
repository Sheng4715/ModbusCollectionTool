using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具
{
    /// <summary>Interaction logic for App.xaml</summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 必须在主窗口构造前完成：窗口 XAML 里的 DynamicResource 要在第一次
            // 布局取值时就能找到键，晚于此会先按空值渲染一帧再跳变。
            ThemeManager.Initialize();

            // 全局异常兜底：宁可记一条日志 + 提示一句，也不要让 WPF 直接静默崩掉
            // （WinExe 没有控制台，崩溃时用户只看到窗口消失，现场完全无从查起）。
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                ReportFatal("界面异常", args.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                ReportFatal("后台异常", args.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                // 未被 await 的任务异常：标记已观察，避免进程被终止
                args.SetObserved();
                ReportFatal("异步任务异常", args.Exception);
            };

            base.OnStartup(e);
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
    }
}
