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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Io;
using DSH_Modbus采集工具.Logging;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Polling;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具
{
    public class IoPoint : INotifyPropertyChanged
    {
        private bool _isOn;
        private string _comment = string.Empty;

        /// <summary>输入（X）点亮色：绿色。</summary>
        private static readonly Brush InputOnFallback = MakeBrush(0x0B, 0x7A, 0x0B);

        /// <summary>输出（Y）点亮色：橙色 <c>#FFA500</c>，与输入的绿色区分。</summary>
        private static readonly Brush OutputOnFallback = MakeBrush(0xFF, 0xA5, 0x00);

        /// <summary>灭灯色：浅灰（输入 / 输出共用）。</summary>
        private static readonly Brush OffFallback = MakeBrush(0xC4, 0xC4, 0xC4);

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

        /// <summary>
        /// 灯色：灭为浅灰；点亮时输入（X）绿、输出（Y）橙。
        /// 每次取都按**当前主题**解析（深色下绿色/灰色都要提亮，否则与深底融在一起）；
        /// 主题资源拿不到时回退到浅色字面值。画刷由 ResourceDictionary 缓存，
        /// 这里不会每次新建对象。
        /// </summary>
        public Brush StateBrush => !IsOn
            ? Themes.ThemeManager.GetBrush("Io.Off") ?? OffFallback
            : IsOutput
                ? Themes.ThemeManager.GetBrush("Io.Output.On") ?? OutputOnFallback
                : Themes.ThemeManager.GetBrush("Io.Input.On") ?? InputOnFallback;

        /// <summary>
        /// 通知界面重取 <see cref="StateBrush"/>。换主题时由窗口调用 ——
        /// StateBrush 是只读派生属性，没有 setter，只能靠变更通知驱动绑定重算。
        /// </summary>
        public void RaiseStateBrushChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateBrush)));

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

    /// <summary>设备状态日志的一行，供总览页底部的 ListBox 绑定。</summary>
    public sealed class LogEntry
    {
        private readonly DateTime _time;

        public LogEntry(DateTime time, string message, DeviceLogLevel level)
        {
            _time = time;
            TimeText = $"[{time:yyyy-MM-dd HH:mm:ss}]";
            Message = message;
            Level = level;
            LevelBrush = MakeLevelBrush(level);
        }

        /// <summary>形如 "[2026-10-08 10:23:45]"，固定灰色显示。</summary>
        public string TimeText { get; }

        public string Message { get; }

        /// <summary>级别（换主题时按它重算颜色）。</summary>
        public DeviceLogLevel Level { get; }

        public Brush LevelBrush { get; }

        /// <summary>
        /// 按当前主题取该级别的画刷。走 <see cref="Themes.ThemeManager.GetBrush"/>，
        /// 取不到时回退到浅色字面值，保证任何时候都有颜色。
        /// </summary>
        private static Brush MakeLevelBrush(DeviceLogLevel level)
        {
            string key = level switch
            {
                DeviceLogLevel.Connected => "Brush.Success",
                DeviceLogLevel.Disconnected => "Brush.Danger",
                DeviceLogLevel.Warning => "Brush.Warning",
                _ => "Brush.Text.Secondary",
            };

            return Themes.ThemeManager.GetBrush(key) ?? Fallback(level);
        }

        /// <summary>换主题后重建一行（颜色随主题变，其它字段不变）。</summary>
        public LogEntry WithThemeColors() => new(_time, Message, Level);

        /// <summary>主题资源不可用时的兜底色（= 浅色主题的对应值）。</summary>
        private static Brush Fallback(DeviceLogLevel level)
        {
            Color color = level switch
            {
                DeviceLogLevel.Connected => Color.FromRgb(0x0B, 0x7A, 0x0B),
                DeviceLogLevel.Disconnected => Color.FromRgb(0xC4, 0x2B, 0x1C),
                DeviceLogLevel.Warning => Color.FromRgb(0xB2, 0x6A, 0x00),
                _ => Color.FromRgb(0x5D, 0x5D, 0x5D),
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>设备配置页展示用：地址表中的一个元件（同名元件的多段地址区合并为一项）。</summary>
    public sealed class AreaElement
    {
        /// <summary>元件符号，如 M、X、SD</summary>
        public string Element { get; init; } = string.Empty;

        /// <summary>悬停提示：各段编号范围与点数</summary>
        public string ToolTipText { get; init; } = string.Empty;
    }

    /// <summary>
    /// 主窗口。<see cref="ICollectorHost"/> 的实现即「窗口 → 采集引擎」的那一侧契约：
    /// 跨线程字段、Dispatcher 调度、当前连接校验都留在这里，引擎因此不认识 WPF。
    /// </summary>
    public partial class MainWindow : Window, ICollectorHost
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

        /// <summary>自由监控项集合（增删改 + 落盘 + 地址解析）。</summary>
        private readonly MonitorItemSet _monitorSet = new();

        /// <summary>STRING 长度的允许范围（选 STRING 时弹框校验用）。</summary>
        private const int StringLengthMin = 1;

        private const int StringLengthMax = 128;

        /// <summary>新建 STRING 时的默认长度（弹框的初值）。</summary>
        private const int StringLengthDefault = 8;

        /// <summary>当前显示的是「自由监控」分段（采集跟随可见分段用）。</summary>
        private volatile bool _freeViewActive;

        /// <summary>
        /// 采集编排器（轮询 + 心跳）。不持有 WPF 控件，与窗口之间只走
        /// <see cref="ICollectorHost"/> 与 <see cref="PollSnapshot"/> 两条通道 —— 见 <c>Polling/</c>。
        /// </summary>
        private readonly ModbusPoller _poller;


        /// <summary>当前页号（0 基）；X / Y 两分段各自独立记页。</summary>
        private volatile int _xPageIndex;
        private volatile int _yPageIndex;

        /// <summary>
        /// 当前可见页的点位快照：UI 线程在切页/切段/重建点位时整体替换，
        /// 后台轮询只读该数组（不直接触碰点位集里的列表，避免与重建竞态）。
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
        /// 见 <c>Io/IoPointSet.cs</c>。
        /// </summary>
        private readonly IoPointSet _ioSet = new();

        /// <summary>设备状态日志（总览页底部展示）。只在 UI 线程追加。</summary>
        private readonly ObservableCollection<LogEntry> _deviceLog = new();

        /// <summary>
        /// 设备状态日志的落盘写入器（P5-2）。所有写入都在 UI 线程（<see cref="AppendDeviceLog"/>
        /// 的调用点全部来自 UI 线程），故内部无需再跨线程调度。窗口关闭时释放句柄。
        /// </summary>
        private readonly DeviceLogFileWriter _logWriter =
            new(ThemeManager.LogDirectory, ThemeManager.LogSaveEnabled);

        // ---------- 底栏状态（换主题时据此重取颜色） ----------

        /// <summary>底栏当前状态文字。换主题后按新主题重设颜色，文字不变。</summary>
        private string _statusText = "未连接";

        /// <summary>底栏当前状态色的浅色字面值，经 <see cref="StatusBrushKey"/> 映射到主题键。</summary>
        private string _statusColorHex = "#9A9A9A";

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

            _poller = new ModbusPoller(this);

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

            // 自由监控：载入持久化项 + 绑定列表（解析失败项保留并标红）
            _monitorSet.SetDriver(_currentDriver);
            _monitorSet.Load();
            MonitorItemsControl.ItemsSource = _monitorSet.Items;
            RefreshMonitorView();
        }

        // ---------- IO 点位集构建 / 分页 ----------

        /// <summary>
        /// 重建 X / Y 监控列表并复位到第 1 页。点位名归一化、地址换算与排序规则见
        /// <see cref="IoPointSet.Rebuild"/>（<c>Io/IoPointSet.cs</c>）。
        /// </summary>
        private void RebuildIoPoints()
        {
            _ioSet.Rebuild(_currentDriver);

            _xPageIndex = 0;
            _yPageIndex = 0;
            ApplyIoPage();
        }

        /// <summary>当前显示的分段（true=输出 Y / false=输入 X）的点位列表。</summary>
        private List<IoPoint> ActiveSegmentPoints => _ioSet.PointsFor(_yViewActive);

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
        private static int PageCountOf(List<IoPoint> points) => IoPager.PageCountOf(points);

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
            int pages = IoPager.PageCountOf(points);

            ActivePageIndex = IoPager.ClampPage(ActivePageIndex, pages);

            List<IoPoint> page = IoPager.Slice(points, ActivePageIndex);

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
            target.ItemsSource = IoPager.SliceColumn(page, col);
        }

        /// <summary>分页控件：只有一页时整体隐藏；首/末页把对应箭头置灰。</summary>
        private void UpdateIoPager(int pages)
        {
            if (IoPagerPanel == null)
            {
                return;
            }

            IoPagerPanel.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            IoPageText.Text = IoPager.PageText(ActivePageIndex, pages);
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
            int next = IoPager.NextPage(ActivePageIndex, PageCountOf(ActiveSegmentPoints), delta);
            if (next < 0)
            {
                return;
            }

            ActivePageIndex = next;
            ApplyIoPage();
        }

        /// <summary>读取 exe 旁的 io-comments.json；文件不存在或损坏时保持内置默认，不阻断启动。</summary>
        private void LoadComments() => _ioSet.Load();

        /// <summary>整表写回 io-comments.json（导入后调用）。失败仅意味着下次启动不记得本次导入，不提示打断。</summary>
        private void SaveComments() => _ioSet.Save();

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
            // 主题已由 App.OnStartup 应用（窗口构造前），这里只同步分段选中态
            SyncThemeSegments();
            Themes.ThemeManager.ThemeChanged += OnThemeChanged;

            // P5-2：日志保存设置同样在 App 启动时已载入，这里只回填界面
            SyncLogSaveSegments();
            ApplyLogDirectory(ThemeManager.LogDirectory);

            // P5-3：项目信息全是常量，填一次即可（不需要进页面时刷新）
            RefreshProjectInfo();

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

            if (SettingsPanel != null)
            {
                bool settingsVisible = ReferenceEquals(sender, SettingsNav);
                SettingsPanel.Visibility = settingsVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // 切进设置页时刷新日志回显：行数/失败原因是"进入时该看到的最新值"，
                // 而 AppendDeviceLog 只在面板已可见时才刷 —— 若进入前就写了几条日志，
                // 不在这里补一次就会一直显示旧数字。
                if (settingsVisible)
                {
                    RefreshLogSaveStatus();
                }
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

        // ---------- 设置：主题外观（P5-1） ----------

        /// <summary>
        /// 主题分段被选中。三个分段共用此处理器；<c>Checked</c> 也会在
        /// <see cref="SyncThemeSegments"/> 里被「设置为已持久化的那一项」触发一次，
        /// 那次写回同一个值，<see cref="ThemeManager.SetMode"/> 幂等故无副作用。
        /// </summary>
        private void ThemeSegment_Checked(object sender, RoutedEventArgs e)
        {
            ThemeMode mode;
            if (ReferenceEquals(sender, ThemeLightSegment))
            {
                mode = ThemeMode.Light;
            }
            else if (ReferenceEquals(sender, ThemeDarkSegment))
            {
                mode = ThemeMode.Dark;
            }
            else
            {
                mode = ThemeMode.System;
            }

            ThemeManager.SetMode(mode);
        }

        /// <summary>
        /// 把当前模式同步到分段选中态。直接赋 <c>IsChecked</c>（而不是让
        /// <c>Checked</c> 事件去驱动），避免「同步选中态」反过来再写一次设置。
        /// </summary>
        private void SyncThemeSegments()
        {
            var target = ThemeManager.Mode switch
            {
                ThemeMode.Light => ThemeLightSegment,
                ThemeMode.Dark => ThemeDarkSegment,
                _ => ThemeSystemSegment,
            };

            foreach (var rb in new[] { ThemeLightSegment, ThemeDarkSegment, ThemeSystemSegment })
            {
                rb.IsChecked = ReferenceEquals(rb, target);
            }
        }

        /// <summary>
        /// 主题换字典后，XAML 里的 <c>DynamicResource</c> 自动跟随，但**由 C# 直接赋值的
        /// 画刷不会** —— IO 灯色、日志级别色、底栏状态点/文字都属后者，需在此重算。
        /// </summary>
        private void OnThemeChanged()
        {
            // 灯色：StateBrush 是只读派生属性，靠 PropertyChanged 通知绑定重取
            foreach (var point in _ioSet.XPoints.Concat(_ioSet.YPoints))
            {
                point.RaiseStateBrushChanged();
            }

            // 日志：LogEntry.LevelBrush 构造时定色，只能按新主题重建整表
            RefreshDeviceLogColors();

            // 底栏：按当前状态重取颜色（文字不变）
            SetStatus(_statusText, _statusColorHex);
        }

        // ---------- 设置：日志保存（P5-2） ----------

        /// <summary>
        /// 日志开关分段被选中。与主题分段同款：<c>Checked</c> 也会被
        /// <see cref="SyncLogSaveSegments"/> 触发一次，那次写回同一个值，幂等。
        /// </summary>
        private void LogSaveSegment_Checked(object sender, RoutedEventArgs e)
        {
            bool enabled = !ReferenceEquals(sender, LogSaveOffSegment);
            ThemeManager.SetLogSave(enabled);
            _logWriter.SetEnabled(enabled);
            RefreshLogSaveStatus();
        }

        private void SyncLogSaveSegments()
        {
            bool enabled = ThemeManager.LogSaveEnabled;
            LogSaveOnSegment.IsChecked = enabled;
            LogSaveOffSegment.IsChecked = !enabled;
        }

        /// <summary>
        /// 路径改动在**失焦时**落盘（与自由监控的元件名称同一约定）：打字途中每敲一个
        /// 字符就写一次 json 既无意义，且中途的半截路径（如 `D:\Lo`）还可能被下一次启动
        /// 当成有效目录。
        /// </summary>
        private void LogDirectoryTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string typed = LogDirectoryTextBox.Text.Trim();

            // 空串 = 回到默认目录；显式清空输入框是一种合理的「恢复默认」手势
            if (typed.Length == 0)
            {
                ThemeManager.SetLogSave(ThemeManager.LogSaveEnabled, string.Empty);
                ApplyLogDirectory(ThemeManager.LogDirectory);
                return;
            }

            // 相对路径按 exe 目录展开：用户手敲「Log」时意图明确，不该拒绝
            string full = Path.IsPathRooted(typed)
                ? typed
                : Path.Combine(AppContext.BaseDirectory, typed);

            try
            {
                // 先试建目录再保存 —— 路径非法（非法字符 / 盘符不存在 / 无权限）在这里
                // 就能发现，而不是等到第一条日志写不进去
                Directory.CreateDirectory(full);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"保存路径无效：{ex.Message}\n\n已恢复为上一个可用路径。",
                    "日志保存路径",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                ApplyLogDirectory(ThemeManager.LogDirectory);
                return;
            }

            ThemeManager.SetLogSave(ThemeManager.LogSaveEnabled, full);
            ApplyLogDirectory(full);
        }

        private void LogBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // WPF 没有内置文件夹选择器；.NET 8 的 OpenFolderDialog 在 Microsoft.Win32 下，
            // 与本项目已用的注册表 API 同命名空间，不引入额外依赖
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择日志保存文件夹",
                InitialDirectory = ThemeManager.LogDirectory,
            };

            if (dialog.ShowDialog(this) == true)
            {
                ThemeManager.SetLogSave(ThemeManager.LogSaveEnabled, dialog.FolderName);
                ApplyLogDirectory(dialog.FolderName);
            }
        }

        private void LogOpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string dir = ThemeManager.LogDirectory;
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法打开文件夹：{ex.Message}", "日志保存",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LogResetDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.SetLogSave(ThemeManager.LogSaveEnabled, string.Empty);
            ApplyLogDirectory(ThemeManager.LogDirectory);
        }

        /// <summary>把路径同步到输入框与写入器，并刷新状态回显。</summary>
        private void ApplyLogDirectory(string directory)
        {
            LogDirectoryTextBox.Text = directory;
            _logWriter.SetDirectory(directory);
            RefreshLogSaveStatus();
        }

        /// <summary>
        /// 回显日志保存状态：关闭 / 当前文件 / 上次失败原因。失败原因优先显示 ——
        /// 那才是需要用户处理的。
        /// </summary>
        private void RefreshLogSaveStatus()
        {
            if (LogSaveStatusText == null)
            {
                return;
            }

            if (!ThemeManager.LogSaveEnabled)
            {
                LogSaveStatusText.Text = "已关闭，运行日志只保留在界面上（不落盘）。";
                LogSaveStatusText.Foreground = Themes.ThemeManager.GetBrush("Brush.Text.Muted")
                    ?? System.Windows.Media.Brushes.Gray;
                return;
            }

            if (_logWriter.LastError != null)
            {
                LogSaveStatusText.Text = $"写入失败：{_logWriter.LastError}";
                LogSaveStatusText.Foreground = Themes.ThemeManager.GetBrush("Brush.Warning")
                    ?? System.Windows.Media.Brushes.DarkOrange;
                return;
            }

            string dir = ThemeManager.LogDirectory;
            string file = Logging.DeviceLogFileWriter.FilePathFor(dir, DateOnly.FromDateTime(DateTime.Now));
            LogSaveStatusText.Text = $"当前文件：{file}（本日已写 {_logWriter.LinesWritten} 行）";
            LogSaveStatusText.Foreground = Themes.ThemeManager.GetBrush("Brush.Text.Muted")
                ?? System.Windows.Media.Brushes.Gray;
        }

        /// <summary>
        /// 填充设置页的项目信息卡（P5-3）。
        ///
        /// <para>
        /// 全是**常量**（版本号来自程序集元数据、构建日期来自 csproj 生成的常量），
        /// 运行期不会变，故只在 <see cref="Window_Loaded"/> 里填一次即可 ——
        /// 不需要像日志状态那样在进页面时刷新。
        /// </para>
        /// </summary>
        private void RefreshProjectInfo()
        {
            if (InfoProductText == null)
            {
                return;
            }

            InfoProductText.Text = AppInfo.ProductName;
            InfoVersionText.Text = $"v{AppInfo.Version}";
            InfoBuildDateText.Text = AppInfo.BuildDate;
            InfoRuntimeText.Text = $"{AppInfo.Runtime}　·　{AppInfo.OperatingSystem}";
            InfoDriversText.Text = BuildDriverSummary();
        }

        /// <summary>
        /// 从驱动注册表现算「已支持驱动」摘要（不写死字符串）——
        /// 以后加品牌，项目信息卡自动跟着变，不会漏改。
        /// </summary>
        private static string BuildDriverSummary()
        {
            var names = Drivers.DriverRegistry.All.Select(d => d.DisplayName).ToArray();
            return $"{string.Join(" / ", names)}（{AppInfo.FunctionCodes}）";
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

        /// <summary>
        /// 设置底栏状态文字与状态点颜色。<paramref name="colorHex"/> 是**浅色主题下的字面值**，
        /// 由 <see cref="StatusBrushKey"/> 映射到语义画刷键 —— 这样调用点无需关心主题，
        /// 深色主题下也不会拿到浅色主题的固定色。
        /// </summary>
        private void SetStatus(string text, string colorHex)
        {
            StatusText.Text = text;

            var brush = Themes.ThemeManager.GetBrush(StatusBrushKey(colorHex))
                        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            StatusText.Foreground = brush;
            StatusDot.Fill = brush;

            _statusText = text;
            _statusColorHex = colorHex;

            UpdateOverviewStatus();
        }

        /// <summary>
        /// 底栏状态色字面值 → 语义画刷键。三个出口与 §9.1 的颜色语义一致：
        /// 红＝配置要改、琥珀＝通讯侧的事、绿＝正常、灰＝未连接。
        /// </summary>
        private static string StatusBrushKey(string colorHex) => colorHex switch
        {
            "#0B7A0B" => "Brush.Success",
            "#C42B1C" => "Brush.Danger",
            "#B26A00" => "Brush.Warning",
            _ => "Brush.Indicator.Off",
        };

        /// <summary>
        /// 按新主题重建日志各行的颜色。日志行色在 <see cref="LogEntry"/> 构造时定死，
        /// 换主题后必须整表重建 —— 用「原样替换」而不是 Clear+Add，避免滚动位置跳动。
        /// </summary>
        private void RefreshDeviceLogColors()
        {
            for (int i = 0; i < _deviceLog.Count; i++)
            {
                _deviceLog[i] = _deviceLog[i].WithThemeColors();
            }
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
            var now = DateTime.Now;

            _deviceLog.Add(new LogEntry(now, message, level));
            while (_deviceLog.Count > MaxDeviceLogEntries)
            {
                _deviceLog.RemoveAt(0);
            }

            DeviceLogList.ScrollIntoView(_deviceLog[^1]);

            // P5-2：落盘与界面**共用同一个时间戳**。分成两次取时，跨秒时写下的
            // 时间会与界面差一秒，事后对照两边对不上。
            _logWriter?.Write(now, FileLevelOf(level), message);

            // 设置页可见时刷新回显（行数 / 失败原因）。不可见就不刷 —— 日志是高频旁路，
            // 让它去更新一个没人看的 TextBlock 没有必要。
            if (SettingsPanel?.Visibility == Visibility.Visible)
            {
                RefreshLogSaveStatus();
            }
        }

        /// <summary>
        /// 界面级别 → 落盘级别的映射（P5-2，用户定稿按**颜色语义**分档）：
        /// 绿（已连接 / 通讯恢复）= INFO，琥珀（通讯异常）= WARN，红（已断开 / 连接失败）= ERROR。
        /// <c>Neutral</c> 是默认灰，按 INFO 处理。
        /// </summary>
        private static Logging.LogFileLevel FileLevelOf(DeviceLogLevel level) => level switch
        {
            DeviceLogLevel.Warning => Logging.LogFileLevel.WARN,
            DeviceLogLevel.Disconnected => Logging.LogFileLevel.ERROR,
            _ => Logging.LogFileLevel.INFO,
        };

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

            // 自由监控项也按新驱动重解析（元件可用性与编号范围随品牌变化）
            _monitorSet.SetDriver(_currentDriver);
            MonitorItemsControl?.Items.Refresh();
            RefreshMonitorView();

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
                    Task.Run(() => _poller.PollLoopAsync(client, driver, token)),
                    Task.Run(() => HeartbeatLoopAsync(client, driver, endpoint, token)));
            }
            catch (Exception ex)
            {
                _poller.NotifyConnectionLost(client, token, ex.Message);
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
                    _poller.NotifyConnectionLost(client, token, "心跳读取超时");
                }
            }
            catch (Exception ex)
            {
                _poller.NotifyConnectionLost(client, token, $"心跳检查失败：{ex.Message}");
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

        // ================= ICollectorHost：窗口 → 采集引擎的契约 =================

        int ICollectorHost.PollIntervalMs => _pollIntervalMs;

        MonitorItemSet ICollectorHost.MonitorSet => _monitorSet;

        /// <summary>
        /// 拍下界面可见性快照。由采集引擎在**每一拍开头**与**自由监控每块发送前**调用，
        /// 故必须便宜（只读几个字段、不分配）—— <c>VisiblePage</c> 直接交引用，
        /// 那是 UI 线程整体替换的数组，不会被就地改动。
        /// </summary>
        PollSnapshot ICollectorHost.CaptureSnapshot() => new(
            IoPageActive: _ioPageActive,
            FreeViewActive: _freeViewActive,
            VisiblePage: _visiblePage,
            PollIntervalMs: _pollIntervalMs);

        bool ICollectorHost.IsCurrentConnection(ModbusTcpClient client, CancellationToken token) =>
            IsCurrentConnection(client, token);

        Task ICollectorHost.RunOnUiAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

        void ICollectorHost.NotifyConnectionLost(ModbusTcpClient client, CancellationToken token, string reason) =>
            NotifyConnectionLost(client, token, reason);

        string ICollectorHost.ClassifyDisconnectReason(string raw) => ClassifyDisconnectReason(raw);

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
            Themes.ThemeManager.ThemeChanged -= OnThemeChanged;
            CancelReconnect();
            Disconnect();
            _logWriter.Dispose();
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
            bool yView = YViewSegment.IsChecked == true;
            bool freeView = FreeViewSegment?.IsChecked == true;

            // 采集跟随可见分段：未显示区不读，保留上次数据
            _yViewActive = yView;
            _freeViewActive = freeView;

            IoColumnsGrid.Visibility = xView ? Visibility.Visible : Visibility.Collapsed;
            YIoColumnsGrid.Visibility = yView ? Visibility.Visible : Visibility.Collapsed;

            if (FreeMonitorPanel != null)
            {
                FreeMonitorPanel.Visibility = freeView ? Visibility.Visible : Visibility.Collapsed;
            }

            // 导入 IO 表按钮只在 X/Y 分段下有意义（自由监控有自己的「新增监控项」）
            if (ImportIoTableButton != null)
            {
                ImportIoTableButton.Visibility = freeView ? Visibility.Collapsed : Visibility.Visible;
            }

            // IO 分页控件在自由监控下无意义
            if (IoPagerPanel != null && freeView)
            {
                IoPagerPanel.Visibility = Visibility.Collapsed;
            }

            // 空提示只属于 X/Y（自由监控有自己的空提示）
            if (IoEmptyHint != null && freeView)
            {
                IoEmptyHint.Visibility = Visibility.Collapsed;
            }

            if (freeView)
            {
                RefreshMonitorView();
            }
            else
            {
                // 切段后本段有自己的页号与页数，重新切片绑定
                ApplyIoPage();
            }
        }

        // ---------- 自由监控（P4-2） ----------

        /// <summary>刷新自由监控的计数与按钮可用性（增删后调用）。</summary>
        private void RefreshMonitorView()
        {
            if (MonitorItemsControl == null)
            {
                return;
            }

            int n = _monitorSet.Items.Count;

            // ⚠️ 表头**不随空列表隐藏**：它是这一页的列说明，也是「+」按钮的落点 ——
            // 藏起来会让人以为此页坏掉了，且没有地方能加第一项。
            if (MonitorEmptyHint != null)
            {
                MonitorEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 「全部删除」在空列表下无意义，置灰防空点（Style 里 IsEnabled=False 会一并去掉手型光标）
            if (ClearMonitorItemsButton != null)
            {
                ClearMonitorItemsButton.IsEnabled = n > 0;
            }

            if (MonitorCountText != null)
            {
                MonitorCountText.Text = n == 0 ? string.Empty : $"共 {n} 项";
            }
        }

        /// <summary>
        /// 删除全部监控项。此操作会**立即清空并落盘**，误点代价是整份配置，
        /// 故先弹确认框（本项目汇报类交互统一用 <see cref="MessageBox"/>）。
        /// </summary>
        private void ClearMonitorItems_Click(object sender, RoutedEventArgs e)
        {
            int n = _monitorSet.Items.Count;
            if (n == 0)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                $"确定要删除全部 {n} 个监控项吗？此操作无法撤销。",
                "删除全部监控项",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            _monitorSet.Clear();
            RefreshMonitorView();
        }

        private void AddMonitorItemButton_Click(object sender, RoutedEventArgs e)
        {
            // 新建项默认：当前驱动第一个寄存器元件 + 起始编号 + INT + DEC。
            // 直接给一个「能解析成功」的初值，避免一新增就标红。
            string element = "D";
            string index = "0";

            if (_currentDriver != null)
            {
                foreach (AddressArea a in _currentDriver.Areas)
                {
                    if (a.Area is ModbusArea.HoldingRegister or ModbusArea.InputRegister)
                    {
                        element = a.Element;
                        // 用 FormatName 取编号：X/Y 是八进制元件，直接 ToString 会给出十进制值
                        index = a.FormatName(a.StartIndex)[element.Length..];
                        break;
                    }
                }
            }

            var model = new DataPoint
            {
                Element = element,
                Index = index,
                Type = DataType.Int,
                Format = DisplayFormat.Dec,
                Comment = string.Empty,
            };

            MonitorItem item = _monitorSet.Add(model);
            RefreshMonitorView();

            // 新增后滚到底部并聚焦元件名称框，便于连续录入
            MonitorItemsControl?.UpdateLayout();
            FocusMonitorRowName(item);
        }

        /// <summary>把焦点移到指定行的元件名称框（新增项后连续录入用）。</summary>
        private void FocusMonitorRowName(MonitorItem item)
        {
            if (MonitorItemsControl == null)
            {
                return;
            }

            // 行容器是 ItemsControl 生成的，需等布局完成后再找
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < MonitorItemsControl.Items.Count; i++)
                {
                    if (MonitorItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                            is not DependencyObject container)
                    {
                        continue;
                    }

                    if (container is FrameworkElement fe && fe.DataContext is MonitorItem rowItem
                        && ReferenceEquals(rowItem, item))
                    {
                        FindNameBox(container)?.Focus();
                        return;
                    }
                }
            }));
        }

        /// <summary>在可视树里找元件名称输入框（DataTemplate 里的控件不能按名字直接取）。</summary>
        private static TextBox? FindNameBox(DependencyObject root)
        {
            if (root is TextBox tb && tb.Name == "NameBox")
            {
                return tb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                TextBox? found = FindNameBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void RemoveMonitorItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is MonitorItem item)
            {
                _monitorSet.Remove(item);
                RefreshMonitorView();
            }
        }

        /// <summary>
        /// 行内控件与行数据的桥接：DataTemplate 里的控件拿不到行对象，
        /// 统一用 Tag 承载行引用（在 <see cref="MonitorRow_Loaded"/> 里挂上）。
        /// </summary>
        private static MonitorItem? RowOf(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.DataContext is MonitorItem fromContext)
                {
                    return fromContext;
                }

                if (fe.Tag is MonitorItem fromTag)
                {
                    return fromTag;
                }
            }

            return null;
        }

        /// <summary>
        /// 元件名称（方案 2 的合并输入框）改动：拆成「符号 + 编号」写回模型，再统一重解析 + 落盘。
        /// <para>
        /// 拆分失败（如打字途中的 <c>"HD"</c>）只标红、<b>不动模型</b>，等
        /// <see cref="MonitorName_LostFocus"/> 把输入框恢复成上次有效名称。
        /// </para>
        /// </summary>
        /// <summary>
        /// 元件名称输入框**打字中**：只做即时视觉反馈，**不提交、不采集**。
        /// <para>
        /// 采集与落盘一律等到<b>失焦</b>（<see cref="MonitorName_LostFocus"/>）——
        /// 否则用户输入 <c>D100</c> 的过程中，<c>D</c>、<c>D1</c>、<c>D10</c> 都会各自
        /// 成为一次"有效配置"并参与采集，既发无谓报文、又让列表内容闪烁。
        /// </para>
        /// <para>
        /// 这里仍要读一次 <see cref="MonitorItem.SetName"/> 是为了**实时标红** ——
        /// 输入明显非法（如 <c>D 100</c>）时立刻给反馈，不必等到失焦。
        /// 但失败只标红，绝不写模型（打字途中的 <c>HD</c> 不能落盘）。
        /// </para>
        /// </summary>
        private void MonitorName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb || RowOf(tb) is not MonitorItem item)
            {
                return;
            }

            // 程序回填（行初始化、失焦后归一）不参与校验，否则会误标红
            if (!tb.IsKeyboardFocusWithin)
            {
                return;
            }

            // 只用「拆分能否成功」做即时标红，**不写模型** ——
            // 写了模型就等于提交了一半：下一拍轮询会拿这个半成品去采集，
            // 而磁盘上还是旧值（内存/磁盘不一致）。
            // 提交（含越界校验、落盘）统一在失焦时做。
            item.SetPreviewError(CommDriver.TrySplitName(tb.Text, out _, out _, out string nameError)
                ? null
                : $"配置有误：{nameError}");
        }

        /// <summary>
        /// 元件名称输入框失焦：**这是唯一提交点** —— 归一文本、重解析、落盘，
        /// 之后该行才参与采集（见 <see cref="MonitorName_TextChanged"/>）。
        /// <para>
        /// 三种结果：① 归一成功且落在驱动范围内 → 提交并开始采集；
        /// ② 归一成功但**编号越界 / 元件不在该驱动** → <b>弹窗提示</b>，且**不提交**（保持上次有效值）；
        /// ③ 拆分失败（如 <c>D 100</c>）→ 回退文本到上次有效名称，不弹窗（打字错误，行内红字已够）。
        /// </para>
        /// <para>
        /// 越界之所以要弹窗：行内 ToolTip 与红字容易被忽略，而"我以为配上了、
        /// 实际一直在读别的地址"是会产生错误判断的静默失效。
        /// </para>
        /// </summary>
        private void MonitorName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || RowOf(tb) is not MonitorItem item)
            {
                return;
            }

            string previous = item.Model.DisplayName;
            string typed = tb.Text;

            if (!item.SetName(typed, out _))
            {
                // ③ 拆分失败（如 "D 100"）：回退到上次有效名称。
                // 不弹窗 —— 这是打字错误，行内红字 + ToolTip 已足够，弹窗会打断连续录入。
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);   // 清掉错误态，不动落盘
                return;
            }

            // 归一（符号大写）后回填输入框
            string canonical = item.Model.DisplayName;
            if (tb.Text != canonical)
            {
                tb.Text = canonical;
            }

            // 名称归一后与落盘值一致、且当前没有预览错误 → 真的什么都没变，不必提交。
            // 注意判据用 previous（SetName 之前的值）而不是"模型当前值"，
            // 否则一旦模型被别处改过就会误判"没变化"而漏掉落盘。
            if (canonical == previous && !item.HasError)
            {
                item.SetPreviewError(null);
                return;
            }

            // 元件变了 → 线圈/寄存器归属可能变，类型候选与当前类型要跟着走
            if (tb.Parent is FrameworkElement row && canonical != previous)
            {
                SyncMonitorTypeWithElement(row, item);
            }

            // ② 名称合法但**不在驱动范围内**（编号越界 / 该驱动无此元件）→ 弹窗且**不提交**。
            // 先只解析不落盘：越界的配置不该进 data-points.json，
            // 否则下次启动会带着一个永远读不到的项。
            item.Resolve(_currentDriver!, out string resolveError);
            if (item.HasError)
            {
                MessageBox.Show(
                    this,
                    $"元件名称「{canonical}」不可用：\n\n{resolveError}\n\n请检查编号范围",
                    "元件名称无效",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                // 回退到上次有效名称并重解析，界面上不留一个坏配置
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);
                return;
            }

            // ① 提交：落盘，下一拍轮询即开始采集该地址
            _monitorSet.AfterChange(item);
        }

        /// <summary>
        /// 按「元件名称」决定该行可选的数据类型。
        /// <para>
        /// 方案 2 里元件是自由文本，故<b>由元件反推类型</b>（方案 1 是反过来的：
        /// 先选类型、元件下拉只列该类型的元件）。不纠正的话，把 <c>D100</c> 改成 <c>X0</c>
        /// 会得到「驱动未定义元件 X」这种驴唇不对马嘴的报错。
        /// </para>
        /// </summary>
        /// <param name="autoCorrect">
        /// true = 用户刚改过元件，类型与元件不符时自动纠正；
        /// false = 载入持久化数据，只摆好候选、不替用户改声明（不匹配就标红让人看见）。
        /// </param>
        private void SyncMonitorTypeWithElement(FrameworkElement row, MonitorItem item, bool autoCorrect = true)
        {
            IReadOnlyList<ModbusArea> areas = _currentDriver?.AreasOfElement(item.Model.Element)
                ?? Array.Empty<ModbusArea>();

            bool coilElement = areas.Contains(ModbusArea.Coil);
            bool known = areas.Count > 0;

            DataTypeSpec spec = DataTypes.Get(item.Model.Type);

            // 元件不在驱动里（打了一半的输入）→ 保留当前类型所在的候选集，别让下拉框突然空掉
            if (known && autoCorrect && spec.CoilOnly != coilElement)
            {
                item.Model.Type = coilElement ? DataType.Bool : DataType.Int;
                spec = DataTypes.Get(item.Model.Type);
                item.Model.Format = spec.DefaultFormat;
            }

            if (row.FindName("TypeCombo") is ComboBox typeCombo)
            {
                // 候选集跟随「元件」；但元件未知（输入未完成）或载入时声明与元件不符时跟随「当前类型」——
                // 保证下拉框里始终能看到并选中已声明的类型，不会显示成空白
                bool useCoil = known && autoCorrect ? coilElement : spec.CoilOnly;
                typeCombo.ItemsSource = useCoil ? DataTypes.CoilTypes : DataTypes.RegisterTypes;
                typeCombo.SelectedItem = spec;
            }

            ApplyMonitorRowState(row, item);
        }

        private void MonitorType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cb || cb.SelectedItem is not DataTypeSpec spec)
            {
                return;
            }

            MonitorItem? item = RowOf(cb);
            if (item == null || item.Model.Type == spec.Type)
            {
                return;
            }

            // ⚠️ 不要在 SelectionChanged 的调用栈里直接 ShowDialog：
            // 此刻下拉 Popup 尚未关闭，模态窗口会在「应用仍在处理 Popup 关闭」的
            // 派发上下文里创建，窗口拿不到显示/激活，表现为弹框不按预期出现。
            //
            // 解法：先让 Popup 关掉、并等一轮渲染真正落定，再在**渲染之后**的
            // 独立派发上下文里弹框。用 Render 优先级 + 再转一次 Normal，
            // 确保完全脱离 Popup 的生命周期。
            //
            // ⚠️ 也不要在延后之前动 cb.IsDropDownOpen / cb.SelectedItem：
            // 那会把本次选择撤销，延后执行时类型已经一致，于是整件事被跳过
            // （实测：加了 IsDropDownOpen=false 之后完全不弹了）。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() =>
                {
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Normal,
                        new Action(() => ApplyTypeChangeWithPrompt(cb, item, spec)));
                }));
        }

        /// <summary>
        /// 换类型：选 STRING 时先弹框问长度，取消则整件事作废（类型与格式都不动）。
        /// 由 <see cref="MonitorType_SelectionChanged"/> 用 BeginInvoke 延后调用。
        /// </summary>
        private void ApplyTypeChangeWithPrompt(ComboBox cb, MonitorItem item, DataTypeSpec spec)
        {
            // 延后执行期间用户可能又改了；类型已一致就不用再问
            if (item.Model.Type == spec.Type)
            {
                return;
            }

            DataType previousType = item.Model.Type;

            if (spec.HasVariableLength)
            {
                int initial = item.Model.Length > 0
                    ? item.Model.Length
                    : StringLengthDefault;

                int? len = NumberInputDialog.Ask(
                    this,
                    "字符串长度",
                    $"请输入 STRING 的字符数（{StringLengthMin} ~ {StringLengthMax}）。",
                    initial,
                    StringLengthMin,
                    StringLengthMax);

                if (len == null)
                {
                    // 作废：把下拉框退回原类型
                    cb.SelectedItem = DataTypes.Get(previousType);
                    return;
                }

                item.Model.Length = len.Value;
            }

            item.Model.Type = spec.Type;

            // 换类型后原格式可能不再合法（如 INT 的 DEC 换到 STRING），
            // 自动落到该类型的默认格式，避免留下一个「非法组合」的项
            if (!spec.AllowsFormat(item.Model.Format))
            {
                item.Model.Format = spec.DefaultFormat;
            }

            _monitorSet.AfterChange(item);

            // 换类型会改变「格式候选」，就地更新本行控件，
            // 不重建整行 —— 重建会打断用户正在进行的输入
            if (cb.Parent is FrameworkElement row)
            {
                ApplyMonitorRowState(row, item);
            }

            RefreshMonitorView();
        }

        private void MonitorFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is DisplayFormat fmt)
            {
                MonitorItem? item = RowOf(cb);
                if (item == null || item.Model.Format == fmt)
                {
                    return;
                }

                item.Model.Format = fmt;
                _monitorSet.AfterChange(item);
            }
        }

        /// <summary>
        /// 每个「当前值」输入框最近一次**已提交**的文本（键为控件本身）。
        /// 用途：回车与失焦都会触发提交，靠它识别「同一次编辑」避免重复写。
        /// </summary>
        private readonly Dictionary<TextBox, string?> _lastCommittedValue = new();

        /// <summary>
        /// 「当前值」格获得焦点：标记为编辑中，轮询回填会跳过该行，
        /// 否则用户输入到一半就被下一拍读值覆盖。
        /// </summary>
        private void MonitorValue_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box && RowOf(box) is MonitorItem item)
            {
                item.IsEditing = true;

                // 新一轮编辑：清掉上一轮的去重记录。
                // 没有这一步，用户写入 100 成功后再想写一次 100 会被当成重复而静默跳过。
                // 放在 GotFocus（而非写入成功后）是因为：要重新输入必然先聚焦，
                // 而同一轮编辑内的「回车后失焦」不会再次触发 GotFocus，去重仍然有效。
                _lastCommittedValue.Remove(box);

                // 选中全部，便于直接覆盖输入（与常见 PLC 上位机的一致做法）
                box.SelectAll();
            }
        }

        /// <summary>「当前值」格失去焦点：解除编辑标记并提交写入。</summary>
        private void MonitorValue_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box && RowOf(box) is MonitorItem item)
            {
                item.IsEditing = false;
                _ = CommitMonitorValueAsync(box, item);
            }
        }

        /// <summary>「当前值」格按键：回车提交写入（与失焦提交等价，但不需先点别处）。</summary>
        private void MonitorValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box || RowOf(box) is not MonitorItem item)
            {
                return;
            }

            e.Handled = true;
            _ = CommitMonitorValueAsync(box, item);
        }

        /// <summary>
        /// 提交「当前值」格的写入。
        /// <para>
        /// 流程：文本 → 编码（<see cref="MonitorItemSet.TryBuildWrite"/>）→ 按区选 FC05/06/16 → 写。
        /// **不回读**：成功后由下一拍轮询自然刷新（需求选择），故只清错误标记，
        /// 显示值保持不变 —— 不做「假装成功」的本地改写。
        /// </para>
        /// <para>
        /// 与读取不同，写失败**不改动显示值**：让用户还能看到自己输入的内容并修正。
        /// </para>
        /// </summary>
        private async Task CommitMonitorValueAsync(TextBox box, MonitorItem item)
        {
            ModbusTcpClient? modbus = _modbus;
            CommDriver? driver = _currentDriver;

            // 按需求：建立连接后即可写入，不要求自由监控分段可见。
            if (!_isConnected || modbus == null || driver == null)
            {
                item.WriteError = "未连接，无法写入";
                return;
            }

            string text = box.Text;

            // 回车与失焦是两条路径，会先后各触发一次提交（回车后焦点转移会连带引得失焦）。
            // 同一次编辑只写一次：值与上次提交的相同就跳过。
            if (_lastCommittedValue.TryGetValue(box, out string? last) && last == text)
            {
                return;
            }

            if (!MonitorItemSet.TryBuildWrite(item, text, driver, out WritePlan plan, out string error))
            {
                // 编码失败（类型不匹配、越界、非法字面量）——本地就能判定，不发报文。
                //
                // ⚠️ 此时**保持编辑态**：否则下一拍轮询立刻用 PLC 的真实值覆盖，
                // 用户还没看清错误提示，自己输的内容就没了。
                // 保持编辑态 = 值留在界面上、错误提示留在 ToolTip 里，用户可继续改。
                item.WriteError = error;
                item.IsEditing = true;
                _lastCommittedValue[box] = text; // 同一份非法内容不重复报告
                return;
            }

            // 记下本次提交的文本：回车与失焦会先后各触发一次，靠它去重。
            // ⚠️ 必须在**发报文之前**记下（而不是成功之后）——
            // 否则第一次成功后清掉记录，紧接着的失焦路径会当作新提交又写一次。
            _lastCommittedValue[box] = text;

            try
            {
                await WriteToDeviceAsync(modbus, plan).ConfigureAwait(true);

                // 成功：清掉错误标记、**解除编辑态**，
                // 之后由下一拍轮询自然刷成 PLC 的真实值（不回读，按需求）。
                item.WriteError = null;
                item.ReadError = null;
                item.IsEditing = false;
            }
            catch (OperationCanceledException)
            {
                item.WriteError = "写入已取消";
                item.IsEditing = true;
            }
            catch (Exception ex)
            {
                // 只读区（FC02/FC04）会走到这里 —— 从站回异常码，原样展示原因。
                // 同样保持编辑态，让用户看到自己输的内容与失败原因。
                item.WriteError = ClassifyWriteError(ex);
                item.IsEditing = true;
                _lastCommittedValue[box] = text; // 同一份内容不重复发失败请求
            }
        }

        /// <summary>按存储区选择写功能码：线圈 FC05，寄存器按长度选 FC06（单）或 FC16（多）。</summary>
        private static Task WriteToDeviceAsync(ModbusTcpClient modbus, WritePlan plan)
        {
            if (plan.Address < 0)
            {
                throw new IOException("地址无效，无法写入");
            }

            ushort address = (ushort)plan.Address;

            if (plan.IsCoil)
            {
                return modbus.WriteSingleCoilAsync(address, plan.CoilValue);
            }

            ushort[] regs = plan.Registers ?? throw new IOException("写入内容为空");
            return regs.Length == 1
                ? modbus.WriteSingleRegisterAsync(address, regs[0])
                : modbus.WriteMultipleRegistersAsync(address, regs);
        }

        /// <summary>
        /// 写入失败的原因文案。与 <see cref="ClassifyDisconnectReason"/> 不同：
        /// 写失败多为「从站拒绝」（异常码），不涉及断线归类，故单独处理。
        /// </summary>
        private static string ClassifyWriteError(Exception ex)
        {
            if (ex is ModbusResponseException mre)
            {
                // 码 2 出现在只读区是最常见的情况，给出可操作的解释
                return mre.ExceptionCode == 2
                    ? "该地址不可写（输入寄存器 / 离散输入为只读区）"
                    : mre.Message;
            }

            return ClassifyDisconnectReason(ex.Message);
        }

        /// <summary>
        /// 行初始化：把行对象挂到 Tag、填入元件名称、按元件推导类型候选与格式候选。
        /// 候选项按行构造（随元件与类型变），故不放在 XAML 里静态定义。
        /// </summary>
        private void MonitorRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement row || row.DataContext is not MonitorItem item)
            {
                return;
            }

            row.Tag = item;

            // 元件名称（方案 2：符号 + 编号合并输入）；文本已一致时不写，避免触发无谓的落盘
            if (row.FindName("NameBox") is TextBox nameBox && nameBox.Text != item.Model.DisplayName)
            {
                nameBox.Text = item.Model.DisplayName;
            }

            // 载入时不自动纠正类型：持久化里的「元件↔类型」不匹配要留给用户看见（标红），
            // 悄悄改掉用户的声明比报错更糟
            SyncMonitorTypeWithElement(row, item, autoCorrect: false);
        }

        /// <summary>
        /// 让行内控件的可见性/候选项与该行当前类型保持一致（初始化时与换类型后都调）。
        /// 集中在一处，避免「换类型」路径漏掉某个控件的联动。
        /// </summary>
        private static void ApplyMonitorRowState(FrameworkElement row, MonitorItem item)
        {
            DataTypeSpec spec = DataTypes.Get(item.Model.Type);

            if (row.FindName("FormatCombo") is ComboBox formatCombo)
            {
                formatCombo.ItemsSource = spec.AllowedFormats;
                formatCombo.SelectedItem = item.Model.Format;
            }
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

            IoImportResult result;
            try
            {
                result = IoTableImporter.Read(_currentDriver, dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "导入 IO 表",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result.SourceRowCount == 0)
            {
                MessageBox.Show(this,
                    "未在表中识别到点位地址（应形如 X0 / Y37），当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ok = result.OkCount;

            if (ok == 0)
            {
                MessageBox.Show(this,
                    $"成功导入 0 项，失败 {result.FailureCount} 项。\n\n" +
                    $"{IoTableImporter.FormatFailures(result.Failures)}\n\n当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _ioSet.Replace(result.Comments);
            SaveComments();
            RebuildIoPoints(); // 点位集随之变化：重建监控列表并复位到第 1 页

            string message = $"成功导入 {ok} 项，失败 {result.FailureCount} 项。";
            if (result.FailureCount > 0)
            {
                message += $"\n\n失败明细：\n{IoTableImporter.FormatFailures(result.Failures)}";
            }

            MessageBox.Show(this, message, "导入 IO 表", MessageBoxButton.OK,
                result.FailureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
