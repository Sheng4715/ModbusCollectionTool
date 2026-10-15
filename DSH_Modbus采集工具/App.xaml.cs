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

            base.OnStartup(e);
        }
    }
}
