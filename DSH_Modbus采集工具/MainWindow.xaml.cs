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

        /// <summary>Modbus 绝对地址，由当前通讯驱动解析得出；轮询按此回填 <see cref="IsOn"/>。</summary>
        public int Address { get; set; }

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

    /// <summary>设备配置页展示用：地址表中的一个元件（同名元件的多段地址区合并为一项）。</summary>
    public sealed class AreaElement
    {
        /// <summary>元件符号，如 M、X、SD</summary>
        public string Element { get; init; } = string.Empty;

        /// <summary>悬停提示：各段编号范围与点数</summary>
        public string ToolTipText { get; init; } = string.Empty;
    }

    public partial class MainWindow : Window
    {
        private ModbusTcpClient? _modbus;
        private bool _isConnected;
        private CancellationTokenSource? _pollCts;
        private CancellationTokenSource? _connectCts;
        private bool _windowClosed;
        private volatile int _pollIntervalMs = 100;
        private CommDriver? _currentDriver;
        private readonly List<IoPoint> _xPoints = new();
        private readonly List<IoPoint> _yPoints = new();

        /// <summary>每页 4 列 × 16 行 = 64 点；本分段点位超出一页即分页。</summary>
        private const int IoRowsPerColumn = 16;
        private const int IoPageSize = 4 * IoRowsPerColumn;

        /// <summary>Modbus FC01 单次读取上限（协议规定 2000 点）。</summary>
        private const int MaxCoilsPerRead = 2000;

        /// <summary>当前页号（0 基）；X / Y 两分段各自独立记页。</summary>
        private volatile int _xPageIndex;
        private volatile int _yPageIndex;

        /// <summary>
        /// 当前可见页的点位快照：UI 线程在切页/切段/重建点位时整体替换，
        /// 后台轮询只读该数组（不直接触碰 <see cref="_xPoints"/><see cref="_yPoints"/>，避免与重建竞态）。
        /// </summary>
        private volatile IoPoint[] _visiblePage = Array.Empty<IoPoint>();

        /// <summary>构造期结束标志：此后切换设备类型才需要按新驱动重建点位地址。</summary>
        private bool _ioInitialized;

        /// <summary>状态信息页可见时才发起采集；离开页面停止报文，灯保持上次数据（UI 线程写、后台只读）。</summary>
        private volatile bool _ioPageActive;

        /// <summary>当前显示的分段：true=输出 Y，false=输入 X；只读可见区（UI 线程写、后台只读）。</summary>
        private volatile bool _yViewActive;

        /// <summary>
        /// 点位集 + 注释表（键 = 点位名如 "X17"，值 = 注释）。**IO 监控列表完全由它驱动**：
        /// 启动时从 exe 旁的 io-comments.json 载入，导入 IO 表后整体替换并写回；未导入则为空（界面显示空列表）。
        /// </summary>
        private Dictionary<string, string> _comments = new(StringComparer.OrdinalIgnoreCase);

        private static string CommentsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "io-comments.json");

        public MainWindow()
        {
            InitializeComponent();

            // 设备类型下拉绑定驱动注册表（品牌地址表）
            DeviceTypeComboBox.ItemsSource = DriverRegistry.All.Select(d => d.DisplayName).ToList();
            DeviceTypeComboBox.SelectedIndex = 0;

            // 点位集来自 exe 旁的 io-comments.json（导入 IO 表后写回）；文件不存在则为空列表
            LoadComments();

            _ioInitialized = true;
            RebuildIoPoints();
            RefreshDriverAreas();
        }

        // ---------- IO 点位集构建 / 分页 ----------

        /// <summary>
        /// 按当前点位集（<see cref="_comments"/>）重建 X / Y 监控列表：归一化点位名、用当前通讯驱动
        /// 换算出 Modbus 地址、按地址升序排序，并复位到第 1 页。未通过驱动校验的条目忽略（导入时已过滤，此处兜底）。
        /// </summary>
        private void RebuildIoPoints()
        {
            _xPoints.Clear();
            _yPoints.Clear();

            foreach (string name in _comments.Keys)
            {
                if (TryCreateIoPoint(name, out IoPoint? point))
                {
                    (point!.Name[0] == 'Y' ? _yPoints : _xPoints).Add(point);
                }
            }

            // 地址升序：八进制编号解析后即数值序，与轮询回填时的偏移一致
            _xPoints.Sort(static (a, b) => a.Address.CompareTo(b.Address));
            _yPoints.Sort(static (a, b) => a.Address.CompareTo(b.Address));

            _xPageIndex = 0;
            _yPageIndex = 0;
            ApplyIoPage();
        }

        /// <summary>把 "X17" 这类点位名转成 IoPoint：仅接受 X / Y 元件，且地址须在当前驱动定义范围内。</summary>
        private bool TryCreateIoPoint(string name, out IoPoint? point)
        {
            point = null;
            CommDriver? driver = _currentDriver;
            if (driver == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string normalized = NormalizePointName(name);
            if (normalized.Length < 2 || (normalized[0] != 'X' && normalized[0] != 'Y'))
            {
                return false;
            }

            if (!driver.TryResolve(normalized, ModbusArea.Coil, out int address, out _))
            {
                return false;
            }

            _comments.TryGetValue(normalized, out string? comment);
            point = new IoPoint
            {
                Name = normalized,
                Address = address,
                Comment = comment ?? string.Empty,
            };
            return true;
        }

        /// <summary>归一化点位名：元件符号转大写（"x17" → "X17"、"sm5" → "SM5"），编号部分原样保留。</summary>
        private static string NormalizePointName(string name)
        {
            int i = 0;
            while (i < name.Length && char.IsLetter(name[i]))
            {
                i++;
            }

            return name[..i].ToUpperInvariant() + name[i..];
        }

        /// <summary>当前显示的分段（true=输出 Y / false=输入 X）的点位列表。</summary>
        private List<IoPoint> ActiveSegmentPoints => _yViewActive ? _yPoints : _xPoints;

        /// <summary>当前分段的页号（0 基）。</summary>
        private int ActivePageIndex
        {
            get => _yViewActive ? _yPageIndex : _xPageIndex;
            set
            {
                if (_yViewActive)
                {
                    _yPageIndex = value;
                }
                else
                {
                    _xPageIndex = value;
                }
            }
        }

        /// <summary>总页数；空列表也算 1 页，便于统一处理分页控件显隐。</summary>
        private static int PageCountOf(List<IoPoint> points) =>
            Math.Max(1, (points.Count + IoPageSize - 1) / IoPageSize);

        /// <summary>
        /// 把当前页切片绑定到 4 个纵向 ItemsControl（每列固定 16 行，末页不满留空），
        /// 刷新分页控件与空状态，并替换后台轮询用的可见页快照。
        /// </summary>
        private void ApplyIoPage()
        {
            if (IoColumn0 == null || IoColumn1 == null || IoColumn2 == null || IoColumn3 == null
                || YIoColumn0 == null || YIoColumn1 == null || YIoColumn2 == null || YIoColumn3 == null)
            {
                return;
            }

            List<IoPoint> points = ActiveSegmentPoints;
            int pages = PageCountOf(points);

            if (ActivePageIndex > pages - 1)
            {
                ActivePageIndex = pages - 1;
            }

            if (ActivePageIndex < 0)
            {
                ActivePageIndex = 0;
            }

            List<IoPoint> page = points
                .Skip(ActivePageIndex * IoPageSize)
                .Take(IoPageSize)
                .ToList();

            // 只给「当前显示的分段」对应的 4 个列控件绑定（另一分段的网格保持原内容，切回时重新绑定）
            BindIoColumn(_yViewActive ? YIoColumn0 : IoColumn0, page, 0);
            BindIoColumn(_yViewActive ? YIoColumn1 : IoColumn1, page, 1);
            BindIoColumn(_yViewActive ? YIoColumn2 : IoColumn2, page, 2);
            BindIoColumn(_yViewActive ? YIoColumn3 : IoColumn3, page, 3);

            _visiblePage = page.ToArray();

            UpdateIoPager(pages);
            UpdateIoEmptyHint(points.Count == 0);
        }

        /// <summary>取本页第 col 列的 16 个点（不足则少）绑定到对应 ItemsControl。</summary>
        private static void BindIoColumn(System.Windows.Controls.ItemsControl target, List<IoPoint> page, int col)
        {
            target.ItemsSource = page
                .Skip(col * IoRowsPerColumn)
                .Take(IoRowsPerColumn)
                .ToList();
        }

        /// <summary>分页控件：只有一页时整体隐藏；首/末页把对应箭头置灰。</summary>
        private void UpdateIoPager(int pages)
        {
            if (IoPagerPanel == null)
            {
                return;
            }

            IoPagerPanel.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            IoPageText.Text = $"{ActivePageIndex + 1}/{pages}";
            IoPrevPageButton.IsEnabled = ActivePageIndex > 0;
            IoNextPageButton.IsEnabled = ActivePageIndex < pages - 1;
        }

        /// <summary>空状态提示：仅在未导入 IO 表 / 本分段无点位时出现。</summary>
        private void UpdateIoEmptyHint(bool empty)
        {
            if (IoEmptyHint != null)
            {
                IoEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void IoPrevPageButton_Click(object sender, RoutedEventArgs e) => ChangeIoPage(-1);

        private void IoNextPageButton_Click(object sender, RoutedEventArgs e) => ChangeIoPage(1);

        private void ChangeIoPage(int delta)
        {
            int next = ActivePageIndex + delta;
            if (next < 0 || next >= PageCountOf(ActiveSegmentPoints))
            {
                return;
            }

            ActivePageIndex = next;
            ApplyIoPage();
        }

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
            if (_windowClosed || _connectCts != null)
            {
                return;
            }

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

            CommDriver? driver = _currentDriver;
            if (driver == null)
            {
                SetStatus("请选择设备类型", "#C42B1C");
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
            using var connectCts = new CancellationTokenSource();
            _connectCts = connectCts;
            try
            {
                await client.ConnectAsync(ip, port, TimeSpan.FromSeconds(3), localEndPoint, connectCts.Token);
                if (_windowClosed || connectCts.IsCancellationRequested)
                {
                    client.Dispose();
                    return;
                }

                _modbus = client;
                _isConnected = true;
                ConnectButton.Content = "断开";
                ConnectButton.IsEnabled = true;
                SetStatus($"已连接 {ip}:{port}", "#0B7A0B");
                StartPolling(client, driver, $"{ip}:{port}");
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                if (!_windowClosed)
                {
                    SetInputsEnabled(true);
                    SetStatus("连接超时", "#C42B1C");
                }
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (!_windowClosed)
                {
                    SetInputsEnabled(true);
                    SetStatus($"连接失败：{ex.Message}", "#C42B1C");
                }
            }
            finally
            {
                if (ReferenceEquals(_connectCts, connectCts))
                {
                    _connectCts = null;
                }
                if (!_windowClosed)
                {
                    ConnectButton.IsEnabled = true;
                }
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
            _connectCts?.Cancel();
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

            // 换驱动后 X/Y 的 Modbus 地址随之变化，按新驱动重建点位地址（构造期尚未就绪，跳过）
            if (_ioInitialized)
            {
                RebuildIoPoints();
            }

            RefreshDriverAreas();
            UpdateOverviewStatus();
        }

        /// <summary>
        /// 刷新设备配置页的地址表元件展示：按当前驱动分「线圈区 / 寄存器区」两行列出元件，
        /// 同名元件的多段地址区合并为一项（悬停提示列出各段编号范围与点数）。
        /// </summary>
        private void RefreshDriverAreas()
        {
            if (CoilAreaItems == null || RegisterAreaItems == null)
            {
                return;
            }

            CommDriver? driver = _currentDriver;
            if (driver == null)
            {
                CoilAreaItems.ItemsSource = null;
                RegisterAreaItems.ItemsSource = null;
                return;
            }

            CoilAreaItems.ItemsSource = BuildAreaElements(
                driver.Areas.Where(a => a.Area == ModbusArea.Coil));

            RegisterAreaItems.ItemsSource = BuildAreaElements(
                driver.Areas.Where(a => a.Area is ModbusArea.HoldingRegister or ModbusArea.InputRegister));
        }

        /// <summary>把同一元件的若干地址区合并成一项；多段时提示里逐段列出范围与点数。</summary>
        private static List<AreaElement> BuildAreaElements(IEnumerable<AddressArea> areas)
        {
            var result = new List<AreaElement>();

            foreach (var group in areas.GroupBy(a => a.Element))
            {
                var list = group.ToList();
                bool multi = list.Count > 1;

                var lines = new List<string> { group.Key };
                for (int i = 0; i < list.Count; i++)
                {
                    AddressArea a = list[i];
                    // FormatName 按该区进制输出编号（八进制区输出 X0/X77 这类），故传十进制存储值即可
                    string from = a.FormatName(a.StartIndex);
                    string to = a.FormatName(a.StartIndex + a.Count - 1);
                    string prefix = multi ? $"  段{i + 1}  " : "  ";
                    lines.Add($"{prefix}{from} – {to}   共 {a.Count} 点");
                }

                result.Add(new AreaElement
                {
                    Element = group.Key,
                    ToolTipText = string.Join("\n", lines),
                });
            }

            return result;
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

        private void StartPolling(ModbusTcpClient client, CommDriver driver, string endpoint)
        {
            StopPolling();
            var cts = new CancellationTokenSource();
            _pollCts = cts;
            // 两项任务持有自己的连接和驱动快照，结束后才释放取消源。
            _ = RunConnectionTasksAsync(client, driver, endpoint, cts);
        }

        private async Task RunConnectionTasksAsync(ModbusTcpClient client, CommDriver driver,
            string endpoint, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            try
            {
                await Task.WhenAll(
                    Task.Run(() => PollLoopAsync(client, driver, token)),
                    Task.Run(() => HeartbeatLoopAsync(client, driver, endpoint, token)));
            }
            catch (Exception ex)
            {
                NotifyConnectionLost(client, token, ex.Message);
            }
            finally
            {
                // 本方法从 UI 线程启动，await 后仍回 UI 线程，避免与 StopPolling 竞争释放。
                if (ReferenceEquals(_pollCts, cts))
                {
                    _pollCts = null;
                }
                cts.Dispose();
            }
        }

        private async Task HeartbeatLoopAsync(ModbusTcpClient client, CommDriver driver,
            string endpoint, CancellationToken token)
        {
            try
            {
                await ModbusHeartbeat.RunAsync(client, driver, async warning =>
                {
                    if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted)
                    {
                        return;
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsCurrentConnection(client, token))
                        {
                            return;
                        }
                        SetStatus(warning == null ? $"已连接 {endpoint}" : $"已连接；{warning}",
                            warning == null ? "#0B7A0B" : "#B26A00");
                    });
                }, token);
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                {
                    NotifyConnectionLost(client, token, "心跳读取超时");
                }
            }
            catch (Exception ex)
            {
                NotifyConnectionLost(client, token, $"心跳检查失败：{ex.Message}");
            }
        }

        private bool IsCurrentConnection(ModbusTcpClient client, CancellationToken token) =>
            !_windowClosed && !token.IsCancellationRequested && _isConnected
            && ReferenceEquals(_modbus, client);

        private void StopPolling()
        {
            if (_pollCts == null)
            {
                return;
            }

            _pollCts.Cancel();
            _pollCts = null;
        }

        private async Task PollLoopAsync(ModbusTcpClient client, CommDriver driver, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                DateTime start = DateTime.UtcNow;

                try
                {
                    await PollOnceAsync(client, driver, token);
                }
                catch (OperationCanceledException)
                {
                    // 我方取消 = 正常停止；读超时 = 设备无响应，按断线处理
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    NotifyConnectionLost(client, token, "读取超时");
                    break;
                }
                catch (Exception ex)
                {
                    NotifyConnectionLost(client, token, ex.Message);
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

        private async Task PollOnceAsync(ModbusTcpClient modbus, CommDriver driver, CancellationToken token)
        {
            // 离开状态信息页仅停采 IO；独立心跳继续工作，灯保留上次采集数据。
            if (!_ioPageActive)
            {
                return;
            }

            // 只读当前显示分段「当前页」的点位；点位集由 IO 表决定，未导入时为空 → 不发报文
            IoPoint[] points = _visiblePage;
            if (points.Length == 0)
            {
                return;
            }

            // 本页点位可能跨多段地址区（如信捷 X 本体 + 扩展模块）：区与区之间的空隙地址从站不接受，
            // 跨区一次读会回异常响应而被判为断线。故按所属地址区分块，每块读本区最小..最大地址（FC01），再按偏移回填。
            // 连接期间设备类型下拉已置灰，_currentDriver 不会变化。
            var results = new List<(IoPoint[] Points, int Min, bool[] Coils)>();

            foreach (var group in points.GroupBy(p => driver.FindArea(ModbusArea.Coil, p.Address)))
            {
                // 单次 FC01 最多 2000 点；同区跨度超限时再按 2000 切分
                int baseAddress = group.Min(p => p.Address);
                foreach (var chunk in group.GroupBy(p => (p.Address - baseAddress) / MaxCoilsPerRead))
                {
                    IoPoint[] chunkPoints = chunk.ToArray();
                    int min = chunkPoints.Min(p => p.Address);
                    int max = chunkPoints.Max(p => p.Address);
                    bool[] coils = await modbus.ReadCoilsAsync((ushort)min, (ushort)(max - min + 1), token);
                    results.Add((chunkPoints, min, coils));
                }
            }

            // 回到 UI 线程刷新绑定（IoPoint 为 INPC 对象，IsOn 变更驱动灯色）
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentConnection(modbus, token))
                {
                    return;
                }
                foreach (var (chunkPoints, min, coils) in results)
                {
                    foreach (IoPoint p in chunkPoints)
                    {
                        int offset = p.Address - min;
                        if (offset >= 0 && offset < coils.Length)
                        {
                            p.IsOn = coils[offset];
                        }
                    }
                }
            });
        }

        private void NotifyConnectionLost(ModbusTcpClient client, CancellationToken token, string reason)
        {
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            {
                return;
            }
            // 轮询在后台线程，回 UI 线程执行断开
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsCurrentConnection(client, token))
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
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _windowClosed = true;
            Disconnect();
            base.OnClosed(e);
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

            // 切段后本段有自己的页号与页数，重新切片绑定
            ApplyIoPage();
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
            SaveComments();
            RebuildIoPoints(); // 点位集随之变化：重建监控列表并复位到第 1 页

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

            string normalized = NormalizePointName(name);
            if (normalized[0] != 'X' && normalized[0] != 'Y')
            {
                reason = "非 IO 监控点位（IO 监控仅支持输入 X / 输出 Y）";
                return false;
            }

            CommDriver? driver = _currentDriver;
            if (driver == null)
            {
                reason = "尚未选择设备类型（通讯驱动）";
                return false;
            }

            // IO 监控页采集的是线圈（FC01），故按 Coil 区校验地址范围
            if (!driver.TryResolve(normalized, ModbusArea.Coil, out _, out string error))
            {
                reason = DescribeResolveFailure(driver, normalized, error);
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

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
