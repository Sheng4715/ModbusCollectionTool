using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DSH_Modbus采集工具.Monitoring;
using DSH_Modbus采集工具.Scanners;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 设备监控页第五分段「通讯监控」（P2.4）的窗口侧接线：
    /// 目标列表（只列已建立的连接）+ 三个控件处理器。抓包与呈现分别在
    /// <c>Monitoring/TrafficTrace.cs</c>（不引 WPF）与 <c>Ui/TrafficMonitorPresenter.cs</c>。
    /// </summary>
    public partial class MainWindow
    {
        private TrafficMonitorPresenter? _traffic;

        /// <summary>
        /// 在构造函数末尾调用：控件此时已由 <c>InitializeComponent</c> 建好。
        /// 分段默认是 X 输入监控，故定时器先不启动（由 <code>IoView_Checked</code> 启停）。
        /// </summary>
        private void InitializeTrafficMonitor()
        {
            if (TrafficMonitorList == null || TrafficTargetComboBox == null
                || TrafficFormatPanel == null || TrafficHexSegment == null
                || TrafficTextSegment == null)
            {
                return;
            }

            _traffic = new TrafficMonitorPresenter();
            _traffic.Attach(
                TrafficMonitorList,
                TrafficTargetComboBox,
                TrafficFormatPanel,
                TrafficHexSegment,
                TrafficTextSegment,
                BuildTrafficTargets);
        }

        /// <summary>
        /// 监控目标：**只列已经建立**的连接 —— PLC 一项（以太网 / 串口）、每台已连接的扫码器
        /// 各占一项、HTTP 一项。没建立的不出现（固定空项只会让人点进去看到一片空白）。
        /// <para>
        /// HTTP 没有长连接，判据取「MES 服务端 URL 已配置」；真正有流量是上报任务执行的时候。
        /// </para>
        /// <para>
        /// ⚠️ 本方法每 200ms 被呈现器问一次，必须便宜：只读状态、不做字符串拼接以外的活。
        /// 改名 / 断线 / 重连都由这种「每次重算」自然反映，不必在连接生命周期里散落调用点。
        /// </para>
        /// </summary>
        private IReadOnlyList<TrafficTarget> BuildTrafficTargets()
        {
            var targets = new List<TrafficTarget>();

            if (_session.IsConnected)
            {
                targets.Add(new TrafficTarget(TrafficKeys.Plc, "PLC连接", TrafficTargetKind.Plc));
            }

            foreach (ScannerItem item in _scannerSet.Items)
            {
                if (_scannerSessions.TryGetValue(item.Model, out ScannerSession? session)
                    && session.IsConnected)
                {
                    // 名字就是用户给这台扫码器起的名字（默认「扫码器1」…），身份仍是同一个 Model 实例
                    targets.Add(new TrafficTarget(item.Model, item.Model.Name, TrafficTargetKind.Scanner));
                }
            }

            if (ThemeManager.MesUrl.Trim().Length > 0)
            {
                targets.Add(new TrafficTarget(TrafficKeys.Http, "HTTP连接", TrafficTargetKind.Http));
            }

            return targets;
        }

        private void TrafficTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            _traffic?.OnTargetSelectionChanged();

        private void TrafficFormat_Checked(object sender, RoutedEventArgs e) =>
            _traffic?.OnFormatChanged();

        private void TrafficClearButton_Click(object sender, RoutedEventArgs e) =>
            _traffic?.Clear();
    }
}
