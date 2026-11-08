using System.Windows;
using System.Windows.Controls;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 账户与权限（P 权限，2026-11-06 用户定案）。
    ///
    /// <para>
    /// **只管控一件事**：自由监控里对 PLC 的写入。技术员不能用，工程师可以。
    /// 用户原话：「自由监控中对PLC随意写入值是危险的操作，所以需要账户权限来管控」、
    /// 「技术员：除了自由监控不可以写入值，其他无限制」。
    /// </para>
    /// <para>
    /// ⚠️ **不要顺手给别的功能也加限制**（采集任务的写入、扫码器回写、全局变量编辑…）——
    /// 用户只点了自由监控这一处。多加一处就会变成「改个变量也要密码」，
    /// 与需求相反。
    /// </para>
    /// <para>
    /// 启动即技术员、注销退回技术员、**登录状态不落盘**：每次开程序都是技术员。
    /// 上一个人登录工程师、下一个人接手就能写，正是这套权限想避免的事。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>当前账户。启动与注销后都是 <see cref="UserRole.Technician"/>。</summary>
        private UserRole _currentRole = UserRules.DefaultRole;

        /// <summary>当前账户能不能自由监控写入（权限判定的**唯一出口**）。</summary>
        private bool CanWriteMonitor => UserRules.CanWriteMonitor(_currentRole);

        /// <summary>
        /// 当前账户能不能改**应用标题**（2026-11-08 用户新增）。
        /// <para>
        /// ⚠️ 与 <see cref="CanWriteMonitor"/> 一样走 <see cref="UserRules"/>，不要在窗口里
        /// 直接写 <c>_currentRole == UserRole.Engineer</c> —— 权限规则要能离线断言，
        /// 且两处判定必须同源（否则将来改角色定义会漏改一处）。
        /// </para>
        /// </summary>
        private bool CanEditAppTitle => UserRules.CanEditAppTitle(_currentRole);

        /// <summary>
        /// 初始化账户按钮。在构造函数末尾调一次（那时 XAML 里的控件都已就位）。
        /// </summary>
        private void InitializeAccount()
        {
            SyncAccountButton();
            ApplyMonitorWritePermission();
            ApplyAppTitlePermission();
        }

        /// <summary>导航底部账户按钮的文字 = 当前用户名。</summary>
        private void SyncAccountButton()
        {
            if (AccountButton != null)
            {
                AccountButton.Content = UserRules.NameOf(_currentRole);
            }
        }

        /// <summary>
        /// 把权限落到自由监控的「当前值」输入框上。
        /// <para>
        /// ⚠️ 走**样式**而不是逐个控件设 <c>IsEnabled</c>：那一格在
        /// <c>DataTemplate</c> 里，每行一份、还会随增删重建，逐个设置必然会漏
        /// （新建的行拿不到，表现为「置灰了但新加的项能写」）。
        /// 加到 <c>MonitorValueTextBox</c> 的 <c>Style.Triggers</c> 上，
        /// 所有行（含将来新建的）自动跟随。
        /// </para>
        /// </summary>
        private void ApplyMonitorWritePermission()
        {
            // 样式里的触发器盯的是这个附加属性/资源开关，见 MonitorValueTextBox 的注释
            MonitorValueTextBoxReadOnly = !CanWriteMonitor;
        }

        /// <summary>
        /// 由 <c>MonitorValueTextBox</c> 样式的 Triggers 读取：true = 该格置灰不可编辑。
        /// <para>
        /// 用<b>依赖属性</b>而不是普通字段：样式触发器只能绑 DP，
        /// 且改完要能立刻通知已经生成的那一批行（INPC 做不到 —— 它们是绑定到行的，不是绑定到窗口的）。
        /// </para>
        /// </summary>
        public static readonly DependencyProperty MonitorValueTextBoxReadOnlyProperty =
            DependencyProperty.Register(
                nameof(MonitorValueTextBoxReadOnly),
                typeof(bool),
                typeof(MainWindow),
                new PropertyMetadata(false));

        /// <summary>自由监控「当前值」是否只读（= 当前账户无写入权限）。</summary>
        public bool MonitorValueTextBoxReadOnly
        {
            get => (bool)GetValue(MonitorValueTextBoxReadOnlyProperty);
            set => SetValue(MonitorValueTextBoxReadOnlyProperty, value);
        }

        /// <summary>
        /// 点导航底部的账户按钮：开登录框。
        /// <para>
        /// ⚠️ **不写运行日志**（用户 2026-11-06 定案）：登录 / 注销属于界面操作，
        /// 不是设备状态；日志卡只记连接那 5 种现象（ADR-14）。
        /// 账户当前是谁，导航底部那个按钮上一直显示着，不需要日志再复述一遍。
        /// </para>
        /// </summary>
        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            if (!LoginDialog.Ask(this, _currentRole, out UserRole role, out bool logout))
            {
                return;
            }

            // 注销 = 退回技术员（用户定案）。技术员本来就免登录，
            // 所以这一步不需要二次确认，也不会把界面锁起来
            _currentRole = logout ? UserRules.DefaultRole : role;

            SyncAccountButton();
            ApplyMonitorWritePermission();

            // 标题权限跟着账户走：登录 / 注销后立刻生效（无需重开设置页）
            ApplyAppTitlePermission();
        }
    }
}
