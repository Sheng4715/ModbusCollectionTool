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
        private string _comment = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Comment
        {
            get => _comment;
            set
            {
                if (_comment == value)
                {
                    return;
                }

                _comment = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
            }
        }

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

    /// <summary>网卡下拉项：显示 "网卡名(ip)"，携带真实 IP 供连接时绑定本地端点。</summary>
    public sealed class NicInfo
    {
        public string Name { get; init; } = string.Empty;
        public IPAddress Ip { get; init; } = IPAddress.None;

        public override string ToString() => $"{Name}({Ip})";
    }

    public partial class MainWindow : Window
    {
        private ModbusTcpClient? _modbus;
        private bool _isConnected;
        private CancellationTokenSource? _pollCts;
        private volatile int _pollIntervalMs = 100;
        private CommDriver? _currentDriver;
        private readonly List<IoPoint> _xPoints = new();
        private readonly List<IoPoint> _yPoints = new();

        /// <summary>状态信息页可见时才发起采集；离开页面停止报文，灯保持上次数据（UI 线程写、后台只读）。</summary>
        private volatile bool _ioPageActive;

        /// <summary>当前显示的分段：true=输出 Y，false=输入 X；只读可见区（UI 线程写、后台只读）。</summary>
        private volatile bool _yViewActive;

        /// <summary>运行时注释表：默认取 <see cref="XComments"/>，若 exe 旁存在 io-comments.json 则以文件为权威（P2 导入后持久化）。</summary>
        private Dictionary<string, string> _comments = new(StringComparer.OrdinalIgnoreCase);

        private static string CommentsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "io-comments.json");

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

            // 注释表：内置默认为底，若 exe 旁存在 io-comments.json 则整表覆盖
            _comments = new Dictionary<string, string>(XComments, StringComparer.OrdinalIgnoreCase);
            LoadComments();

            InitIoPoints();
        }

        private void InitIoPoints()
        {
            for (int col = 0; col < 4; col++)
            {
                var xColumn = new List<IoPoint>();
                var yColumn = new List<IoPoint>();

                for (int row = 0; row < 16; row++)
                {
                    int tens = col * 2 + row / 8;
                    int ones = row % 8;
                    string suffix = tens == 0 ? $"{ones}" : $"{tens}{ones}";
                    string xName = $"X{suffix}";
                    string yName = $"Y{suffix}";

                    xColumn.Add(new IoPoint { Name = xName, Comment = LookupComment(xName) });
                    yColumn.Add(new IoPoint { Name = yName, Comment = LookupComment(yName) });
                }

                _xPoints.AddRange(xColumn);
                _yPoints.AddRange(yColumn);

                switch (col)
                {
                    case 0:
                        IoColumn0.ItemsSource = xColumn;
                        YIoColumn0.ItemsSource = yColumn;
                        break;
                    case 1:
                        IoColumn1.ItemsSource = xColumn;
                        YIoColumn1.ItemsSource = yColumn;
                        break;
                    case 2:
                        IoColumn2.ItemsSource = xColumn;
                        YIoColumn2.ItemsSource = yColumn;
                        break;
                    case 3:
                        IoColumn3.ItemsSource = xColumn;
                        YIoColumn3.ItemsSource = yColumn;
                        break;
                }
            }
        }

        private string LookupComment(string name) =>
            _comments.TryGetValue(name, out string? comment) ? comment : string.Empty;

        /// <summary>读取 exe 旁的 io-comments.json；文件不存在或损坏时保持内置默认，不阻断启动。</summary>
        private void LoadComments()
        {
            try
            {
                if (!File.Exists(CommentsFilePath))
                {
                    return;
                }

                var loaded = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(File.ReadAllText(CommentsFilePath));
                if (loaded != null)
                {
                    _comments = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 损坏/不可读 → 回退内置注释
            }
        }

        /// <summary>整表写回 io-comments.json（导入后调用）。失败仅意味着下次启动不记得本次导入，不提示打断。</summary>
        private void SaveComments()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(
                    _comments,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CommentsFilePath, json);
            }
            catch
            {
                // 忽略写入失败
            }
        }

        /// <summary>按当前注释表刷新全部灯项文本（Comment 已实现变更通知）。</summary>
        private void ApplyComments()
        {
            foreach (IoPoint p in _xPoints)
            {
                p.Comment = LookupComment(p.Name);
            }

            foreach (IoPoint p in _yPoints)
            {
                p.Comment = LookupComment(p.Name);
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
            // 采集跟随页面可见性：离开状态信息页即停采（零报文，灯保留上次数据）
            _ioPageActive = ReferenceEquals(sender, StatusInfoNav);

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

            string? previous = TargetNicComboBox.SelectedItem?.ToString();

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

                TargetNicComboBox.Items.Add(new NicInfo { Name = nic.Name, Ip = ipv4 });
            }

            if (TargetNicComboBox.Items.Count == 0)
            {
                TargetNicComboBox.Items.Add("未找到可用网卡");
                TargetNicComboBox.SelectedIndex = 0;
                return;
            }

            // 按显示文本恢复刷新前的选择（每次扫描都是新对象，不能用引用比较）
            int index = -1;
            if (previous != null)
            {
                for (int i = 0; i < TargetNicComboBox.Items.Count; i++)
                {
                    if (string.Equals(TargetNicComboBox.Items[i]?.ToString(), previous, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }

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

            // 绑定所选网卡的本地源地址（多网卡/子网冲突时强制走该网卡）；
            // 未选有效网卡项时为 null，交给系统路由自动选择
            IPEndPoint? localEndPoint = TargetNicComboBox.SelectedItem is NicInfo nic
                ? new IPEndPoint(nic.Ip, 0)
                : null;

            var client = new ModbusTcpClient();
            try
            {
                await client.ConnectAsync(ip, port, TimeSpan.FromSeconds(3), localEndPoint);

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
            // 离开状态信息页：不发起任何报文（优化通讯），灯保留上次采集数据，回页后下一周期恢复
            if (!_ioPageActive)
            {
                return;
            }

            ModbusTcpClient? modbus = _modbus;
            CommDriver? driver = _currentDriver;
            if (modbus == null || driver == null)
            {
                throw new IOException("连接或驱动未就绪");
            }

            // 只读当前显示的分段（未显示区不读，切回后下一周期即刷新）
            bool yView = _yViewActive;
            string element = yView ? "Y0" : "X0";
            if (!driver.TryResolve(element, ModbusArea.Coil, out int start, out string error))
            {
                throw new InvalidOperationException(error);
            }

            List<IoPoint> points = yView ? _yPoints : _xPoints;
            bool[] coils = await modbus.ReadCoilsAsync((ushort)start, (ushort)points.Count, token);

            // 回到 UI 线程刷新绑定（IoPoint 为 INPC 对象，IsOn 变更驱动灯色）
            await Dispatcher.InvokeAsync(() =>
            {
                for (int i = 0; i < points.Count && i < coils.Length; i++)
                {
                    points[i].IsOn = coils[i];
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

        // ---------- IO 元件区切换 / IO 表导入（P2） ----------

        private void IoView_Checked(object sender, RoutedEventArgs e)
        {
            if (IoColumnsGrid == null || YIoColumnsGrid == null
                || XViewSegment == null || YViewSegment == null)
            {
                return;
            }

            bool xView = XViewSegment.IsChecked == true;
            _yViewActive = !xView; // 采集跟随可见分段：未显示区不读，保留上次数据
            IoColumnsGrid.Visibility = xView ? Visibility.Visible : Visibility.Collapsed;
            YIoColumnsGrid.Visibility = xView ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ImportIoTableButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入 IO 点位表",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            List<(string Address, string Comment)> rows;
            try
            {
                rows = XlsxIoTableReader.Read(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "导入 IO 表",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (rows.Count == 0)
            {
                MessageBox.Show(this,
                    "未在表中识别到点位地址（应形如 X0 / Y37），当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 全量替换：以表为权威重建注释字典。
            // 每行先按当前通讯驱动校验地址是否落在驱动定义范围内（超范围即失败），
            // 再确认是 IO 监控页上的点位；任一环节不通过都计入「失败」，不写入注释表。
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();

            foreach ((string address, string comment) in rows)
            {
                string name = address.Trim();
                if (!TryValidateIoPoint(name, out string reason))
                {
                    failures.Add($"{name} —— {reason}");
                    continue;
                }

                next[name] = comment.Trim();
            }

            int ok = next.Count;

            if (ok == 0)
            {
                MessageBox.Show(this,
                    $"成功导入 0 项，失败 {failures.Count} 项。\n\n{FormatFailures(failures)}\n\n当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _comments = next;
            ApplyComments();
            SaveComments();

            string message = $"成功导入 {ok} 项，失败 {failures.Count} 项。";
            if (failures.Count > 0)
            {
                message += $"\n\n失败明细：\n{FormatFailures(failures)}";
            }

            MessageBox.Show(this, message, "导入 IO 表", MessageBoxButton.OK,
                failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>
        /// 校验点位能否生效：① 形如 X0/Y37；② 元件在当前通讯驱动中已定义、且编号未超出该区范围；
        /// ③ 属于 IO 监控页上的点位（X0–X77 / Y0–Y77）。失败时输出中文原因。
        /// </summary>
        private bool TryValidateIoPoint(string name, out string reason)
        {
            reason = string.Empty;

            if (!IsPointName(name))
            {
                reason = "格式无效，应为「元件+编号」如 X0";
                return false;
            }

            CommDriver? driver = _currentDriver;
            if (driver == null)
            {
                reason = "尚未选择设备类型（通讯驱动）";
                return false;
            }

            // IO 监控页采集的是线圈（FC01），故按 Coil 区校验地址范围
            if (!driver.TryResolve(name, ModbusArea.Coil, out _, out string error))
            {
                reason = DescribeResolveFailure(driver, name, error);
                return false;
            }

            if (!IsMonitoredPoint(name))
            {
                reason = "非 IO 监控点位（本页仅显示 X0–X77 / Y0–Y77）";
                return false;
            }

            return true;
        }

        /// <summary>把驱动层的解析失败翻译得更具体：未定义元件 / 非线圈元件 / 编号越界或进制非法。</summary>
        private static string DescribeResolveFailure(CommDriver driver, string name, string error)
        {
            int split = 0;
            while (split < name.Length && char.IsLetter(name[split]))
            {
                split++;
            }

            string element = name[..split];
            var areas = driver.Areas.Where(a => a.Element == element).ToList();

            if (areas.Count == 0)
            {
                return $"驱动「{driver.DisplayName}」未定义元件 {element}";
            }

            if (areas.All(a => a.Area != ModbusArea.Coil))
            {
                return $"元件 {element} 不是线圈，IO 监控仅支持线圈点位";
            }

            return error;
        }

        /// <summary>失败明细文本：最多列前 12 条，其余折叠为一行汇总。</summary>
        private static string FormatFailures(List<string> failures)
        {
            const int maxShown = 12;
            var lines = failures.Take(maxShown).ToList();
            if (failures.Count > maxShown)
            {
                lines.Add($"…… 其余 {failures.Count - maxShown} 项从略");
            }

            return string.Join("\n", lines);
        }

        /// <summary>形如 X0 / Y37：字母开头、其后全为数字（表头「地址」、说明文字在此被排除）。</summary>
        private static bool IsPointName(string name)
        {
            if (name.Length < 2 || !char.IsLetter(name[0]))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>是否为当前监控页上的点位（X0–X77 / Y0–Y77 之一）。</summary>
        private bool IsMonitoredPoint(string name) =>
            _xPoints.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            || _yPoints.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
