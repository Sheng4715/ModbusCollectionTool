using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具
{
    public class IoPoint : INotifyPropertyChanged
    {
        private bool _isOn;

        public string Name { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value)
                {
                    return;
                }

                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateBrush)));
            }
        }

        public Brush StateBrush => IsOn
            ? new SolidColorBrush(Color.FromRgb(0x0B, 0x7A, 0x0B))
            : new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xC4));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private ModbusTcpClient? _modbus;
        private bool _isConnected;
        private CancellationTokenSource? _pollCts;
        private volatile int _pollIntervalMs = 500;
        private CommDriver? _currentDriver;
        private readonly List<IoPoint> _ioPoints = new();

        private static readonly Dictionary<string, string> XComments = new()
        {
            ["X0"] = "接料到位感应",
            ["X1"] = "接料阻挡气缸原点",
            ["X2"] = "接料定位气缸原点",
            ["X3"] = "取料升降缸1原点",
            ["X4"] = "取料升降缸1动点",
            ["X5"] = "取料升降缸2原点",
            ["X6"] = "取料升降缸2动点",
            ["X7"] = "取料吸真空1感应",
            ["X10"] = "取料吸真空2感应",
            ["X11"] = "取料二次定位气缸原点",
            ["X12"] = "取料二次定位气缸动点",
            ["X13"] = "二次定位有无料检测",
            ["X14"] = "载具顶升气缸原点",
            ["X15"] = "载具侧推气缸原点",
            ["X16"] = "产品起翘对射感应",
            ["X17"] = "载具回传到位感应",
            ["X20"] = "接料到位感应",
            ["X21"] = "接料阻挡气缸原点",
            ["X22"] = "接料定位气缸原点",
            ["X23"] = "取料升降缸1原点",
            ["X24"] = "取料升降缸1动点",
            ["X25"] = "取料升降缸2原点",
            ["X26"] = "取料升降缸2动点",
            ["X27"] = "取料吸真空1感应",
            ["X30"] = "取料吸真空2感应",
            ["X31"] = "取料二次定位气缸原点",
            ["X32"] = "取料二次定位气缸动点",
            ["X33"] = "二次定位有无料检测",
            ["X34"] = "载具顶升气缸原点",
            ["X35"] = "载具侧推气缸原点",
            ["X36"] = "产品起翘对射感应",
            ["X37"] = "载具回传到位感应",
        };

        public MainWindow()
        {
            InitializeComponent();

            // 设备类型下拉绑定驱动注册表（品牌地址表）
            DeviceTypeComboBox.ItemsSource = DriverRegistry.All.Select(d => d.DisplayName).ToList();
            DeviceTypeComboBox.SelectedIndex = 0;

            InitIoPoints();
        }

        private void InitIoPoints()
        {
            for (int col = 0; col < 4; col++)
            {
                var column = new List<IoPoint>();

                for (int row = 0; row < 16; row++)
                {
                    int tens = col * 2 + row / 8;
                    int ones = row % 8;
                    string name = tens == 0 ? $"X{ones}" : $"X{tens}{ones}";

                    column.Add(new IoPoint
                    {
                        Name = name,
                        Comment = XComments.TryGetValue(name, out string? comment) ? comment : string.Empty,
                    });
                }

                _ioPoints.AddRange(column);

                switch (col)
                {
                    case 0:
                        IoColumn0.ItemsSource = column;
                        break;
                    case 1:
                        IoColumn1.ItemsSource = column;
                        break;
                    case 2:
                        IoColumn2.ItemsSource = column;
                        break;
                    case 3:
                        IoColumn3.ItemsSource = column;
                        break;
                }
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            SyncInputHeights();
            ScanNetworkAdapters();
            UpdateOverviewStatus();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (OverviewPanel != null)
            {
                OverviewPanel.Visibility = ReferenceEquals(sender, OverviewNav)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (StatusInfoPanel != null)
            {
                StatusInfoPanel.Visibility = ReferenceEquals(sender, StatusInfoNav)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (DeviceConfigPanel == null)
            {
                return;
            }

            DeviceConfigPanel.Visibility = ReferenceEquals(sender, DeviceConfigNav)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (DeviceConfigPanel.Visibility == Visibility.Visible)
            {
                DeviceConfigPanel.UpdateLayout();
                SyncInputHeights();
                ScanNetworkAdapters();
            }

            if (OverviewPanel != null && OverviewPanel.Visibility == Visibility.Visible)
            {
                UpdateOverviewStatus();
            }
        }

        private void UpdateOverviewStatus()
        {
            if (OverviewStatusPrefix == null || OverviewStatusState == null
                || DeviceTypeComboBox == null || ConnectionTypeComboBox == null
                || ProtocolTypeComboBox == null
                || IpTextBox == null || PortTextBox == null)
            {
                return;
            }

            string driver = DeviceTypeComboBox.SelectedItem as string
                            ?? (DeviceTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                            ?? DeviceTypeComboBox.Text;
            string connection = (ConnectionTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                                ?? ConnectionTypeComboBox.Text;
            string protocol = (ProtocolTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                              ?? ProtocolTypeComboBox.Text;
            string ipPort = $"{IpTextBox.Text.Trim()}:{PortTextBox.Text.Trim()}";

            OverviewStatusPrefix.Text = $"{driver}：{connection}  {protocol}  {ipPort}";
            OverviewStatusPrefix.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A1A")!);

            OverviewStatusState.Text = _isConnected ? "已连接" : "未连接";
            string colorHex = _isConnected ? "#0B7A0B" : "#C08A00";
            OverviewStatusState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
        }

        private void ScanNetworkAdapters()
        {
            if (TargetNicComboBox == null)
            {
                return;
            }

            string? previous = (TargetNicComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                               ?? TargetNicComboBox.SelectedItem as string;

            TargetNicComboBox.Items.Clear();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPAddress? ipv4 = nic.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4 == null)
                {
                    continue;
                }

                TargetNicComboBox.Items.Add($"{nic.Name}({ipv4})");
            }

            if (TargetNicComboBox.Items.Count == 0)
            {
                TargetNicComboBox.Items.Add("未找到可用网卡");
                TargetNicComboBox.SelectedIndex = 0;
                return;
            }

            int index = previous == null ? -1 : TargetNicComboBox.Items.IndexOf(previous);
            TargetNicComboBox.SelectedIndex = index >= 0 ? index : 0;
        }

        private void SyncInputHeights()
        {
            if (ConnectionTypeComboBox == null || IpTextBox == null || PortTextBox == null)
            {
                return;
            }

            double comboHeight = ConnectionTypeComboBox.ActualHeight;
            if (comboHeight > 0)
            {
                IpTextBox.Height = comboHeight;
                PortTextBox.Height = comboHeight;
                ProtocolTypeComboBox.Height = comboHeight;
                DeviceTypeComboBox.Height = comboHeight;
                PollIntervalComboBox.Height = comboHeight;

                if (ConnectButton != null)
                {
                    ConnectButton.Height = comboHeight;
                }

                if (RefreshNicButton != null)
                {
                    RefreshNicButton.Height = comboHeight;
                }
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                Disconnect();
                return;
            }

            string ip = IpTextBox.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                SetStatus("IP 不能为空", "#C42B1C");
                return;
            }

            if (!int.TryParse(PortTextBox.Text.Trim(), out int port) || port is < 1 or > 65535)
            {
                SetStatus("端口号无效", "#C42B1C");
                return;
            }

            ConnectButton.IsEnabled = false;
            SetInputsEnabled(false);
            SetStatus($"正在连接 {ip}:{port} …", "#B26A00");

            var client = new ModbusTcpClient();
            try
            {
                await client.ConnectAsync(ip, port, TimeSpan.FromSeconds(3));

                _modbus = client;
                _isConnected = true;
                ConnectButton.Content = "断开";
                ConnectButton.IsEnabled = true;
                SetStatus($"已连接 {ip}:{port}", "#0B7A0B");
                StartPolling();
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                SetInputsEnabled(true);
                SetStatus("连接超时", "#C42B1C");
            }
            catch (Exception ex)
            {
                client.Dispose();
                SetInputsEnabled(true);
                SetStatus($"连接失败：{ex.Message}", "#C42B1C");
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private void SetInputsEnabled(bool enabled)
        {
            if (ConnectionTypeComboBox != null)
            {
                ConnectionTypeComboBox.IsEnabled = enabled;
            }

            if (ProtocolTypeComboBox != null)
            {
                ProtocolTypeComboBox.IsEnabled = enabled;
            }

            if (DeviceTypeComboBox != null)
            {
                DeviceTypeComboBox.IsEnabled = enabled;
            }

            if (PollIntervalComboBox != null)
            {
                PollIntervalComboBox.IsEnabled = enabled;
            }

            if (TargetNicComboBox != null)
            {
                TargetNicComboBox.IsEnabled = enabled;
            }

            if (RefreshNicButton != null)
            {
                RefreshNicButton.IsEnabled = enabled;
            }

            if (IpTextBox != null)
            {
                IpTextBox.IsEnabled = enabled;
            }

            if (PortTextBox != null)
            {
                PortTextBox.IsEnabled = enabled;
            }
        }

        private void Disconnect()
        {
            Disconnect("未连接", "#9A9A9A");
        }

        private void Disconnect(string statusText, string colorHex)
        {
            StopPolling();
            _modbus?.Dispose();
            _modbus = null;
            _isConnected = false;
            ConnectButton.Content = "连接";
            SetInputsEnabled(true);
            SetStatus(statusText, colorHex);
        }

        private void SetStatus(string text, string colorHex)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            UpdateOverviewStatus();
        }

        private void RefreshNicButton_Click(object sender, RoutedEventArgs e)
        {
            ScanNetworkAdapters();
        }

        private void ConnectionTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateOverviewStatus();

            if (EthernetOptionsPanel == null || ConnectionTypeComboBox == null)
            {
                return;
            }

            bool isEthernet = ConnectionTypeComboBox.SelectedIndex == 0;
            EthernetOptionsPanel.Visibility = isEthernet ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DeviceTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DeviceTypeComboBox == null)
            {
                return;
            }

            string name = DeviceTypeComboBox.SelectedItem as string ?? DeviceTypeComboBox.Text;
            _currentDriver = DriverRegistry.GetByDisplayName(name);
            UpdateOverviewStatus();
        }

        private void PollIntervalComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox combo
                && combo.SelectedItem is System.Windows.Controls.ComboBoxItem item
                && item.Tag is string tag
                && int.TryParse(tag, out int ms))
            {
                _pollIntervalMs = ms;
            }
        }

        private void ProtocolTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateOverviewStatus();
        }

        private void ConfigInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateOverviewStatus();
        }

        // ---------- Modbus 采集轮询（P0） ----------

        private void StartPolling()
        {
            StopPolling();
            _pollCts = new CancellationTokenSource();
            CancellationToken token = _pollCts.Token;
            _ = Task.Run(() => PollLoopAsync(token), token);
        }

        private void StopPolling()
        {
            if (_pollCts == null)
            {
                return;
            }

            _pollCts.Cancel();
            _pollCts.Dispose();
            _pollCts = null;
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                DateTime start = DateTime.UtcNow;

                try
                {
                    await PollOnceAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // 我方取消 = 正常停止；读超时 = 设备无响应，按断线处理
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    NotifyConnectionLost("读取超时");
                    break;
                }
                catch (Exception ex)
                {
                    NotifyConnectionLost(ex.Message);
                    break;
                }

                // 动态读取采集周期（_pollIntervalMs 为 UI 线程维护的 volatile 字段）
                TimeSpan wait = TimeSpan.FromMilliseconds(_pollIntervalMs) - (DateTime.UtcNow - start);
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task PollOnceAsync(CancellationToken token)
        {
            ModbusTcpClient? modbus = _modbus;
            CommDriver? driver = _currentDriver;
            if (modbus == null || driver == null)
            {
                throw new IOException("连接或驱动未就绪");
            }

            // 按当前驱动地址表解析 X0 起始地址（X0–X77 连续 64 点）
            if (!driver.TryResolve("X0", ModbusArea.Coil, out int start, out string error))
            {
                throw new InvalidOperationException(error);
            }

            int count = _ioPoints.Count;
            bool[] coils = await modbus.ReadCoilsAsync((ushort)start, (ushort)count, token);

            // 回到 UI 线程刷新绑定
            await Dispatcher.InvokeAsync(() =>
            {
                for (int i = 0; i < count && i < coils.Length; i++)
                {
                    _ioPoints[i].IsOn = coils[i];
                }
            });
        }

        private void NotifyConnectionLost(string reason)
        {
            // 轮询在后台线程，回 UI 线程执行断开
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isConnected)
                {
                    Disconnect($"连接已断开：{reason}", "#C42B1C");
                }
            }));
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
            Close();
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (MaximizeButton != null)
            {
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            }
        }

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
