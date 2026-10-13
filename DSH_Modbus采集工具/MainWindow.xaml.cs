using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具
{
    public class IoPoint : INotifyPropertyChanged
    {
        private bool _isOn;
        private string _comment = string.Empty;

        /// <summary>输入（X）点亮色：绿色。</summary>
        private static readonly Brush InputOnBrush = MakeBrush(0x0B, 0x7A, 0x0B);

        /// <summary>输出（Y）点亮色：橙色 <c>#FFA500</c>，与输入的绿色区分。</summary>
        private static readonly Brush OutputOnBrush = MakeBrush(0xFF, 0xA5, 0x00);

        /// <summary>灭灯色：浅灰（输入 / 输出共用）。</summary>
        private static readonly Brush OffBrush = MakeBrush(0xC4, 0xC4, 0xC4);

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 是否为输出元件（Y）。仅影响点亮色：输出用橙色、输入用绿色。
        /// 创建时由点位名首字母决定，之后不变。
        /// </summary>
        public bool IsOutput { get; init; }

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

        /// <summary>灯色：灭为浅灰；点亮时输入（X）绿、输出（Y）橙。</summary>
        public Brush StateBrush => !IsOn
            ? OffBrush
            : IsOutput ? OutputOnBrush : InputOnBrush;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>网卡下拉项：显示 "网卡名(ip)"，携带真实 IP 供连接时绑定本地端点。</summary>
    public sealed class NicInfo
    {
        public string Name { get; init; } = string.Empty;
        public IPAddress Ip { get; init; } = IPAddress.None;

        public override string ToString() => $"{Name}({Ip})";
    }

    /// <summary>设备状态日志的级别，决定日志行的颜色（沿用状态栏既有配色）。</summary>
    public enum DeviceLogLevel
    {
        /// <summary>中性灰：如用户主动断开。</summary>
        Neutral,

        /// <summary>绿色：连接成功、心跳恢复。</summary>
        Connected,

        /// <summary>红色：连接失败、通讯异常断开。</summary>
        Disconnected,

        /// <summary>琥珀色：告警，如心跳响应异常。</summary>
        Warning,
    }

    /// <summary>设备状态日志的一行（不可变），供总览页底部的 ListBox 绑定。</summary>
    public sealed class LogEntry
    {
        public LogEntry(DateTime time, string message, DeviceLogLevel level)
        {
            TimeText = $"[{time:yyyy-MM-dd HH:mm:ss}]";
            Message = message;
            Color color = level switch
            {
                DeviceLogLevel.Connected => Color.FromRgb(0x0B, 0x7A, 0x0B),
                DeviceLogLevel.Disconnected => Color.FromRgb(0xC4, 0x2B, 0x1C),
                DeviceLogLevel.Warning => Color.FromRgb(0xB2, 0x6A, 0x00),
                _ => Color.FromRgb(0x5D, 0x5D, 0x5D),
            };
            LevelBrush = new SolidColorBrush(color);
        }

        /// <summary>形如 "[2026-10-08 10:23:45]"，固定灰色显示。</summary>
        public string TimeText { get; }

        public string Message { get; }

        public Brush LevelBrush { get; }
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

        /// <summary>
        /// 字节顺序：设备通讯区域三个下拉框的当前选择（初值 = 当前驱动约定，切换设备类型时重同步）。
        /// <b>目前仅收集用户选择，尚未被任何数据解释逻辑读取</b> ——
        /// 具体用途要等 P3-3（数据类型系统）落地，届时按这三个值解释寄存器组。
        /// </summary>
        private ByteOrder32 _int32ByteOrder;
        private ByteOrder32 _floatByteOrder;
        private StringByteOrder _stringByteOrder;

        /// <summary>正在按设备类型同步字节顺序下拉框（用于区分「程序设置」与「用户改动」）。</summary>
        private bool _syncingByteOrderCombos;

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

        /// <summary>设备状态日志（总览页底部展示）。只在 UI 线程追加。</summary>
        private readonly ObservableCollection<LogEntry> _deviceLog = new();

        // ---------- 自动重连（P1-1） ----------

        /// <summary>
        /// 期望保持连接的参数快照（IP / 端口 / 驱动 / 本地端点）。非 null 表示用户想连着 ——
        /// 异常断开后据此自动重连；用户主动断开或连接失败时置 null。
        /// </summary>
        private ReconnectTarget? _reconnectTarget;

        /// <summary>重连尝试任务的取消源；非 null 表示正在退避等待或正在尝试连接。</summary>
        private CancellationTokenSource? _reconnectCts;

        /// <summary>连续重连失败次数，用于计算指数退避；连接成功后清零。</summary>
        private int _reconnectAttempt;

        /// <summary>指数退避间隔（秒）：1→2→4→8→10，封顶 10s。</summary>
        private static readonly int[] ReconnectDelaysSeconds = { 1, 2, 4, 8, 10 };

        /// <summary>保持连接所需的参数快照，供自动重连复用。</summary>
        private sealed record ReconnectTarget(string Ip, int Port, CommDriver Driver, IPEndPoint? LocalEndPoint)
        {
            public string Endpoint => $"{Ip}:{Port}";
        }

        /// <summary>日志内存条数上限：超出后丢弃最旧一条（仅内存，不落盘）。</summary>
        private const int MaxDeviceLogEntries = 200;

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

            // 设备状态日志：只在总览页展示，仅内存保存
            DeviceLogList.ItemsSource = _deviceLog;

            _ioInitialized = true;
            RebuildIoPoints();
            RefreshDriverAreas();
            SyncByteOrderCombos();
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
                IsOutput = normalized[0] == 'Y',
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

        // ---------- 最大化溢出补偿 ----------
        // 用 WindowChrome 自绘标题栏时，系统会把最大化窗口的四边各外扩一个窗口边框宽度
        // （本机 150% DPI 实测 11px：窗口 -11,-11 ~ 1931,1219，而工作区 0,0 ~ 1920,1208），
        // 把窗口底边推到任务栏之下；WindowChrome 下客户区覆盖整个窗口，于是底栏最下一条
        // 被任务栏遮住（表现为「底栏偏下、被切」）。
        //
        // 试过拦 WM_GETMINMAXINFO 把 ptMaxSize 改成工作区 —— 实测无效：系统对带边框的
        // 最大化窗口仍按自己的规则外扩（钩子确实被调用，写回的值被忽略）。
        // 故改为确定性补偿：把根 Grid 四边各内缩「外扩量」，内容正好落进工作区。

        private const int MonitorDefaultToNearest = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        /// <summary>
        /// 按当前窗口矩形与所在显示器工作区之差，给根 Grid 设/清内缩量。
        /// 全部用物理像素测量再按窗口 DPI 换算，避免依赖 WPF 布局时序；
        /// 非最大化时清空内缩。多显示器下由 <c>MonitorFromWindow</c> 取窗口所在那台。
        /// </summary>
        private void UpdateMaximizedInset()
        {
            if (RootGrid == null)
            {
                return;
            }

            if (WindowState != WindowState.Maximized)
            {
                RootGrid.Margin = new Thickness(0);
                return;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            // MONITORINFO 必须填 cbSize，否则 GetMonitorInfo 失败
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out NativeRect window))
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            double overX = ((window.Right - window.Left) - (info.Work.Right - info.Work.Left)) / 2.0 / dpi.DpiScaleX;
            double overY = ((window.Bottom - window.Top) - (info.Work.Bottom - info.Work.Top)) / 2.0 / dpi.DpiScaleY;

            RootGrid.Margin = overX > 0 || overY > 0
                ? new Thickness(Math.Max(0, overX), Math.Max(0, overY), Math.Max(0, overX), Math.Max(0, overY))
                : new Thickness(0);
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

        /// <summary>
        /// 刷新底栏右侧的连接参数（驱动 / 连接方式 / 协议 / IP:端口）。
        /// 底栏左侧的状态点与状态文字由 <see cref="SetStatus"/> 负责，两者互不覆盖：
        /// 参数随表单变化，状态随连接生命周期变化。
        /// </summary>
        private void UpdateOverviewStatus()
        {
            if (StatusBarDetail == null
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

            StatusBarDetail.Text = $"{driver}：{connection}  {protocol}  {ipPort}";
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

                if (Int32OrderComboBox != null)
                {
                    Int32OrderComboBox.Height = comboHeight;
                }

                if (FloatOrderComboBox != null)
                {
                    FloatOrderComboBox.Height = comboHeight;
                }

                if (StringOrderComboBox != null)
                {
                    StringOrderComboBox.Height = comboHeight;
                }

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
                // 日志规则 2：已断开（红）—— 用户主动断开也走红色，与异常断开同色
                AppendDeviceLog("已断开：用户主动断开", DeviceLogLevel.Disconnected);
                Disconnect();
                return;
            }

            // 正在自动重连时再点按钮 = 放弃重连（按钮此时显示「取消重连」）
            if (_reconnectCts != null)
            {
                CancelReconnect();
                SetStatus("已取消自动重连", "#9A9A9A");
                SetInputsEnabled(true);
                ConnectButton.Content = "连接";
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

            // 绑定所选网卡的本地源地址（多网卡/子网冲突时强制走该网卡）；
            // 未选有效网卡项时为 null，交给系统路由自动选择
            IPEndPoint? localEndPoint = TargetNicComboBox.SelectedItem is NicInfo nic
                ? new IPEndPoint(nic.Ip, 0)
                : null;

            ConnectButton.IsEnabled = false;
            SetInputsEnabled(false);
            SetStatus($"正在连接 {ip}:{port} …", "#B26A00");

            var target = new ReconnectTarget(ip, port, driver, localEndPoint);
            if (await TryConnectAsync(target, isReconnect: false, CancellationToken.None))
            {
                return; // 成功后由 StartPolling 接管；表单保持锁定
            }

            // 首次连接失败：不自动重连，交还控制权给用户
            if (!_windowClosed)
            {
                SetInputsEnabled(true);
                ConnectButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// 建立一次连接并在成功后启动采集。返回是否成功。
        /// 首次连接与自动重连共用；失败时由调用方决定后续（提示或退避重试）。
        /// </summary>
        private async Task<bool> TryConnectAsync(ReconnectTarget target, bool isReconnect, CancellationToken token)
        {
            var client = new ModbusTcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _connectCts = connectCts;
            try
            {
                await client.ConnectAsync(target.Ip, target.Port, TimeSpan.FromSeconds(3),
                    target.LocalEndPoint, connectCts.Token);
                if (_windowClosed || connectCts.IsCancellationRequested)
                {
                    client.Dispose();
                    return false;
                }

                _modbus = client;
                _isConnected = true;
                _reconnectAttempt = 0; // 连上即清零退避计数
                _reconnectTarget = target; // 记录期望连接参数，异常断开后据此自动重连
                ConnectButton.Content = "断开";
                ConnectButton.IsEnabled = true;
                SetStatus($"已连接 {target.Endpoint}", "#0B7A0B");
                AppendDeviceLog($"已连接：{target.Endpoint}", DeviceLogLevel.Connected);
                StartPolling(client, target.Driver, target.Endpoint);
                return true;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                if (!_windowClosed && !token.IsCancellationRequested)
                {
                    SetStatus("连接超时", "#C42B1C");
                    if (!isReconnect)
                    {
                        AppendDeviceLog($"连接失败：{target.Endpoint} 连接超时", DeviceLogLevel.Disconnected);
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (!_windowClosed && !token.IsCancellationRequested)
                {
                    SetStatus($"连接失败：{ex.Message}", "#C42B1C");
                    if (!isReconnect)
                    {
                        AppendDeviceLog($"连接失败：{target.Endpoint} {ClassifyDisconnectReason(ex.Message)}",
                            DeviceLogLevel.Disconnected);
                    }
                }
                return false;
            }
            finally
            {
                if (ReferenceEquals(_connectCts, connectCts))
                {
                    _connectCts = null;
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

            if (Int32OrderComboBox != null)
            {
                Int32OrderComboBox.IsEnabled = enabled;
            }

            if (FloatOrderComboBox != null)
            {
                FloatOrderComboBox.IsEnabled = enabled;
            }

            if (StringOrderComboBox != null)
            {
                StringOrderComboBox.IsEnabled = enabled;
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
            // 用户主动断开：清掉重连目标并取消在途重连，之后不再自动重连
            _reconnectTarget = null;
            CancelReconnect();
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

        // ---------- 自动重连（P1-1） ----------

        /// <summary>
        /// 异常断开后启动自动重连：按 <see cref="ReconnectDelaysSeconds"/> 指数退避反复尝试，
        /// 直到连上、用户主动断开、用户点「取消重连」，或窗口关闭。
        /// 重连期间**表单保持锁定、按钮显示「取消重连」**，状态栏显示尝试次数与倒计时。
        /// </summary>
        private void StartReconnect()
        {
            ReconnectTarget? target = _reconnectTarget;
            if (target == null || _windowClosed || _reconnectCts != null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _reconnectAttempt = 0;
            ConnectButton.Content = "取消重连";
            ConnectButton.IsEnabled = true;
            SetInputsEnabled(false); // 重连期间表单保持锁定：参数已快照，改了也不会生效，避免误导
            _ = ReconnectLoopAsync(target, cts);
        }

        private async Task ReconnectLoopAsync(ReconnectTarget target, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested && !_windowClosed)
                {
                    int delay = ReconnectDelaysSeconds[
                        Math.Min(_reconnectAttempt, ReconnectDelaysSeconds.Length - 1)];

                    // 倒计时显示：每秒刷新状态栏，让用户看到还在推进
                    for (int remain = delay; remain > 0 && !token.IsCancellationRequested; remain--)
                    {
                        SetStatus($"正在重连 {target.Endpoint} … {remain}s（第 {_reconnectAttempt + 1} 次）",
                            "#B26A00");
                        await Task.Delay(1000, token);
                    }

                    if (token.IsCancellationRequested || _windowClosed)
                    {
                        return;
                    }

                    _reconnectAttempt++;
                    if (await TryConnectAsync(target, isReconnect: true, token))
                    {
                        return; // 成功：TryConnectAsync 已启动采集并刷新状态与日志
                    }

                    if (_windowClosed || token.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消或关窗：静默退出
            }
            finally
            {
                if (ReferenceEquals(_reconnectCts, cts))
                {
                    _reconnectCts = null;
                }
                cts.Dispose();
            }
        }

        /// <summary>取消在途的自动重连（用户主动断开、点「取消重连」或关窗时调用）。</summary>
        private void CancelReconnect()
        {
            _reconnectCts?.Cancel();
        }

        private void SetStatus(string text, string colorHex)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            UpdateOverviewStatus();
        }

        // ---------- 设备状态日志（总览页底部） ----------

        /// <summary>
        /// 追加一条设备状态日志：超出上限丢弃最旧一条，并滚动到最新一行。
        /// 只在 UI 线程调用（连接/断开/心跳告警回切后均已处于 UI 线程）。
        /// <para>
        /// 日志规则（5 种，2026-10-09 定稿）：
        /// ① 已连接：ip:端口 —— 绿；
        /// ② 已断开：&lt;原因&gt; —— 红（用户主动断开与异常断开同色同式，不带 ip:端口）；
        /// ③ 连接失败：ip:端口 &lt;原因&gt; —— 红（现象名后带冒号，地址与原因之间是空格）；
        /// ④ 通讯异常：&lt;原因&gt; —— 琥珀，**仅当连接仍在**但对端回了合法异常码；
        /// ⑤ 通讯恢复 —— 绿，心跳重新有响应。
        /// 注意「完全无响应」不产生第 ④ 条：它由 NotifyConnectionLost 按第 ② 条记红色「已断开」，
        /// 因此一次断线只产生一条日志。
        /// </para>
        /// </summary>
        private void AppendDeviceLog(string message, DeviceLogLevel level)
        {
            _deviceLog.Add(new LogEntry(DateTime.Now, message, level));
            while (_deviceLog.Count > MaxDeviceLogEntries)
            {
                _deviceLog.RemoveAt(0);
            }

            DeviceLogList.ScrollIntoView(_deviceLog[^1]);
        }

        private void ClearDeviceLogButton_Click(object sender, RoutedEventArgs e)
        {
            _deviceLog.Clear();
        }

        /// <summary>
        /// 把底层异常/状态文本归类为简短的中文断开原因。命中已知模式返回中文短语，
        /// 未命中则原样返回（可能含英文），保证不吞掉信息。
        /// </summary>
        private static string ClassifyDisconnectReason(string raw)
        {
            string text = raw.Trim();

            const string heartbeatPrefix = "心跳检查失败：";
            if (text.StartsWith(heartbeatPrefix, StringComparison.Ordinal))
            {
                text = text[heartbeatPrefix.Length..].Trim();
            }

            // 已是简短中文原因的（含「心跳」这类上下文）直接沿用，不做压缩
            if (text is "读取超时" or "心跳读取超时" or "连接被对方关闭")
            {
                return text;
            }

            if (text.StartsWith("Modbus 异常：", StringComparison.Ordinal))
            {
                return text["Modbus 异常：".Length..]; // 例如「从站设备忙（异常码 6）」
            }

            if (text.StartsWith("Modbus ", StringComparison.Ordinal))
            {
                return "响应帧校验失败";
            }

            string cause;
            if (text.Contains("无法连接的网络", StringComparison.Ordinal)
                || text.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase))
            {
                cause = "网络不可达";
            }
            else if (text.Contains("无法访问目标主机", StringComparison.Ordinal)
                     || text.Contains("host is unreachable", StringComparison.OrdinalIgnoreCase))
            {
                cause = "主机不可达";
            }
            else if (text.Contains("积极拒绝", StringComparison.Ordinal)
                     || text.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                cause = "目标拒绝连接";
            }
            else if (text.Contains("强迫关闭", StringComparison.Ordinal)
                     || text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
            {
                cause = "对端关闭连接";
            }
            else if (text.Contains("超时", StringComparison.Ordinal)
                     || text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                cause = "响应超时";
            }
            else
            {
                cause = text; // 未归类：原样保留，不吞信息
            }

            // 不再给心跳来源加「心跳：」前缀 —— 会出现「已断开：心跳：对端关闭连接」这种双冒号
            return cause;
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
            SyncByteOrderCombos();
            UpdateOverviewStatus();
        }

        /// <summary>
        /// 把当前驱动的字节顺序约定同步到三个下拉框（切换设备类型时调用）。
        /// 索引与 <see cref="ByteOrder32"/> / <see cref="StringByteOrder"/> 的枚举值一一对应，
        /// 故直接按枚举值取下标。
        /// </summary>
        private void SyncByteOrderCombos()
        {
            if (Int32OrderComboBox == null || FloatOrderComboBox == null || StringOrderComboBox == null)
            {
                return;
            }

            CommDriver? driver = _currentDriver;
            if (driver == null)
            {
                return;
            }

            _syncingByteOrderCombos = true;
            try
            {
                Int32OrderComboBox.SelectedIndex = (int)driver.Int32ByteOrder;
                FloatOrderComboBox.SelectedIndex = (int)driver.FloatByteOrder;
                StringOrderComboBox.SelectedIndex = (int)driver.StringByteOrder;
            }
            finally
            {
                _syncingByteOrderCombos = false;
            }
        }

        /// <summary>
        /// 三个字节顺序下拉框共用。此处只做「用户改过」的记录 ——
        /// 具体的数据解释要等 P3-3（数据类型系统）落地后才会被读取，现在仅收集用户选择。
        /// </summary>
        private void ByteOrderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 程序化同步（切换设备类型）不算用户改动，跳过
            if (_syncingByteOrderCombos)
            {
                return;
            }

            if (Int32OrderComboBox == null || FloatOrderComboBox == null || StringOrderComboBox == null)
            {
                return;
            }

            _int32ByteOrder = (ByteOrder32)Math.Max(0, Int32OrderComboBox.SelectedIndex);
            _floatByteOrder = (ByteOrder32)Math.Max(0, FloatOrderComboBox.SelectedIndex);
            _stringByteOrder = (StringByteOrder)Math.Max(0, StringOrderComboBox.SelectedIndex);
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
                        // 日志规则 5/6：连接仍在时的通讯异常（琥珀，对端回了合法异常码）
                        // 与心跳恢复（绿）。注意「完全无响应」不在这里 —— 它会走到下面的
                        // NotifyConnectionLost，按规则 2 记红色的「已断开」
                        AppendDeviceLog(warning == null ? "通讯恢复" : $"通讯异常：{warning}",
                            warning == null ? DeviceLogLevel.Connected : DeviceLogLevel.Warning);
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
                    // 日志规则 2：已断开（红）—— 异常导致断开，只写「已断开：原因」；
                    // 「通讯异常」在新规则里专指「连接还在」，不能挂在这句后面
                    AppendDeviceLog($"已断开：{ClassifyDisconnectReason(reason)}",
                        DeviceLogLevel.Disconnected);
                    // 注意这里直接用 Disconnect(status,color) 重载，而不是无参 Disconnect()：
                    // 后者会清掉 _reconnectTarget —— 那是「用户主动断开」的语义，
                    // 异常断开必须保留目标才能自动重连（P1-1）
                    Disconnect($"连接已断开：{reason}", "#C42B1C");
                    StartReconnect();
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
            CancelReconnect();
            Disconnect();
            base.OnClosed(e);
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (MaximizeButton != null)
            {
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            }

            // 状态刚切换时窗口矩形可能还没落定，故直接算一次、布局后再算一次
            UpdateMaximizedInset();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateMaximizedInset));
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
