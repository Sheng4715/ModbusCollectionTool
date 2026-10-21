using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Io;
using DSH_Modbus采集工具.Logging;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Net;
using DSH_Modbus采集工具.Polling;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    // ---------- 辅助类型：IO 点位 ----------

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


    // ---------- 辅助类型：地址表元件（设备配置页展示用） ----------

    /// <summary>设备配置页展示用：地址表中的一个元件（同名元件的多段地址区合并为一项）。</summary>
    public sealed class AreaElement
    {
        /// <summary>元件符号，如 M、X、SD</summary>
        public string Element { get; init; } = string.Empty;

        /// <summary>悬停提示：各段编号范围与点数</summary>
        public string ToolTipText { get; init; } = string.Empty;
    }

    // ---------- 主窗口：ICollectorHost 的窗口侧实现（跨线程 / Dispatcher / 当前连接校验） ----------

    /// <summary>
    /// 主窗口。<see cref="ICollectorHost"/> 的实现即「窗口 → 采集引擎」的那一侧契约：
    /// 跨线程字段、Dispatcher 调度、当前连接校验都留在这里，引擎因此不认识 WPF。
    /// </summary>
    public partial class MainWindow : Window, ICollectorHost
    {
        // 连接身份/生命周期（_modbus / _isConnected / _connectCts 等）已收进 _session；
        // _pollCts 留在窗口 —— 它属于采集任务的启停（ICollectorHost 那一侧）。
        private CancellationTokenSource? _pollCts;
        private bool _windowClosed;
        private volatile int _pollIntervalMs = 100;
        private CommDriver? _currentDriver;

        /// <summary>
        /// 字节顺序：设备通讯区域三个下拉框的当前选择，**三套各自独立**（整数 / 浮点 / 字符串）。
        /// <para>
        /// 初值 = 当前驱动约定；切换设备类型时由 <see cref="SyncByteOrderCombos"/> 重置为驱动默认
        /// （用户 2026-10-21 定案），用户改动后立即生效于读取与写入。
        /// 轮询侧拿的是 <c>_monitorSet.ByteOrders</c> 那份快照 —— 后台线程不能读窗口字段。
        /// </para>
        /// </summary>
        private ByteOrderSettings _byteOrders = ByteOrderSettings.Default;

        /// <summary>正在按设备类型同步字节顺序下拉框（用于区分「程序设置」与「用户改动」）。</summary>
        private bool _syncingByteOrderCombos;

        /// <summary>
        /// 已挂上变量名补全的「元件名称」文本框（P7.1）。
        /// <para>
        /// 用弱表而不是普通字典：监控行会随模板虚拟化反复创建销毁，
        /// 普通字典会一直攥着已丢弃的文本框不放。
        /// </para>
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Ui.CompletionPopup> _nameCompletions = new();

        /// <summary>
        /// 正在程序化重设网卡下拉框（扫描列表时）。与 <see cref="_syncingByteOrderCombos"/> 同理：
        /// 扫描会 Clear 再重新 Add，过程中 SelectionChanged 会被触发多次，
        /// 那些都不是用户意图，不该被写进持久化偏好。
        /// </summary>
        private bool _restoringNicSelection;

        /// <summary>自由监控项集合（增删改 + 落盘 + 地址解析）。</summary>
        private readonly MonitorItemSet _monitorSet = new();

        /// <summary>全局变量集合（P3.3）。与自由监控各自独立维护。</summary>
        private readonly GlobalVarItemSet _globalVarSet = new();

        /// <summary>
        /// 系统变量的**运行时长起点**（单调时钟，P2.3）。
        /// <para>
        /// 用 <see cref="Stopwatch"/> 而不是 `DateTime.Now - 启动时刻`：系统校时会把后者
        /// 瞬间挪几个小时甚至倒退，`sys_RunTime_*` 跟着乱跳、脉冲还会抖一下。
        /// </para>
        /// </summary>
        private readonly Stopwatch _runClock = Stopwatch.StartNew();

        /// <summary>
        /// 系统变量监控表的刷新节拍。
        /// <para>
        /// 取 <b>50ms</b> 而不是 100ms：`sys_Pulse100ms` 的半周期正好是 50ms，
        /// 若每 100ms 采一次，采样点永远是「50ms 的整数倍中步长 2」那一列 ——
        /// 相位固定，读出来恒为同一个值，看上去就是**卡住不闪**。
        /// 50ms 一拍刚好每半周期采到一次，值才会真的翻转。
        /// </para>
        /// <para>
        /// ⚠️ 优先级必须是 <b>Background</b>：用 `DispatcherPriority.Render` 时，
        /// 每拍都会牵动一次渲染调度，实测 7.6% 单核；改成 Background 后同样的刷新频率只有 1%。
        /// 这个页面只是「看一眼」，没理由去抢渲染优先级。
        /// </para>
        /// </summary>
        private readonly DispatcherTimer _sysVarTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        /// <summary>系统变量监控表的行（与 <c>SystemVariable.All</c> 一一对应，构造后不再变）。</summary>
        private readonly List<SystemVarRow> _sysVarRows =
            SystemVariable.All.Select(v => new SystemVarRow(v)).ToList();

        /// <summary>PLC 状态机（P1.2）的 9 个状态与它们的表达式。</summary>
        private readonly MachineStateSet _machineSet = new();

        /// <summary>
        /// 状态机求值节拍：与系统变量页一样 50ms。
        /// <para>同样必须是 <see cref="DispatcherPriority.Background"/> —— 见 <see cref="_sysVarTimer"/> 的说明。</para>
        /// </summary>
        private readonly DispatcherTimer _machineTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        /// <summary>是否停在总览页（决定状态机定时器启停）。</summary>
        private bool _overviewPageActive;

        /// <summary>状态机表达式取值用的适配器（每拍刷新一次系统变量快照）。</summary>
        private readonly ExpressionVariables _machineVariables = new();

        /// <summary>是否停在状态信息页的「系统变量」分段（决定定时器启停）。</summary>
        private bool _sysViewActive;

        /// <summary>是否停在采集配置页的「全局变量」分段（跨线程读，故 volatile）。</summary>

        /// <summary>STRING 长度的允许范围（选 STRING 时弹框校验用）。</summary>
        private const int StringLengthMin = 1;

        private const int StringLengthMax = 128;

        /// <summary>新建 STRING 时的默认长度（弹框的初值）。</summary>
        private const int StringLengthDefault = 8;

        /// <summary>
        /// 询问 STRING 的字符数。**自由监控与全局变量共用这一个入口**，
        /// 标题 / 提示语 / 范围 / 默认值都只在这里定义一次。
        /// <para>
        /// 两处各自内联调用过 NumberInputDialog.Ask，参数却不一样（一边 1~128、一边写死 1~255，
        /// 提示语也各说一套），同一个问题在两页得到不同答案。收在这里就不会再分叉。
        /// </para>
        /// </summary>
        /// <param name="current">该行现有的长度；大于 0 时作为初值，让重复改类型不丢已有设置</param>
        /// <returns>用户确认的长度；取消返回 null</returns>
        private int? AskStringLength(int current)
        {
            int initial = current > 0 ? current : StringLengthDefault;

            return NumberInputDialog.Ask(
                this,
                "字符串长度",
                $"请输入 STRING 的字符数（{StringLengthMin} ~ {StringLengthMax}）。",
                initial,
                StringLengthMin,
                StringLengthMax);
        }

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

        /// <summary>
        /// 设备状态日志（总览页底部展示）。内存集合、上限裁剪、落盘旁路与级别映射都在
        /// <see cref="DeviceLogPresenter"/>；窗口侧只留滚动到最新一行与设置页失败提示刷新。
        /// </summary>
        private readonly DeviceLogPresenter _logPresenter = new();

        /// <summary>
        /// 设备状态日志的落盘写入器（P5-2）。所有写入都在 UI 线程（<see cref="AppendDeviceLog"/>
        /// 的调用点全部来自 UI 线程），故内部无需再跨线程调度。窗口关闭时释放句柄。
        /// </summary>
        private readonly DeviceLogFileWriter _logWriter =
            new(ThemeManager.LogDirectory, ThemeManager.LogSaveEnabled);

        // ---------- 底栏状态（呈现规则在 Ui/StatusBarPresenter.cs） ----------

        /// <summary>
        /// 底栏呈现器：状态文字、状态点颜色、右侧连接参数三者由它统一维护。
        /// <para>
        /// ⚠️ **必须可空**：<see cref="UpdateOverviewStatus"/> 会在 <c>InitializeComponent()</c>
        /// **期间**被 ComboBox 的 <c>SelectionChanged</c> 触发（早于构造函数体），那时这里还是 null。
        /// 改成不可空会在启动时直接 <c>NullReferenceException</c>（实测过）。
        /// </para>
        /// </summary>
        private StatusBarPresenter? _statusBar;

        // ---------- 连接状态（身份与生命周期见 Polling/ConnectionSession.cs） ----------

        /// <summary>
        /// 连接状态机：当前客户端 / 是否连着 / 期望目标 / 在途取消源 / 重连计数。
        /// 规则本体内聚在 <see cref="ConnectionSession"/>；async 编排（等待循环、逐秒刷状态栏、
        /// 按钮文案、表单锁定）刻意留在窗口 —— 那是界面驱动的东西，搬走只会多一层接口。
        /// </summary>
        private readonly ConnectionSession _session = new();

        private static string CommentsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "io-comments.json");

        // ---------- 构造函数 ----------

        public MainWindow()
        {
            InitializeComponent();

            _poller = new ModbusPoller(this);

            // 底栏：状态文字 / 状态点颜色 / 右侧连接参数三者的规则集中在 StatusBarPresenter
            _statusBar = new StatusBarPresenter(StatusDot, StatusText, StatusBarDetail, BuildConnectionDetail);

            // 设备类型下拉绑定驱动注册表（品牌地址表）
            DeviceTypeComboBox.ItemsSource = DriverRegistry.All.Select(d => d.DisplayName).ToList();
            DeviceTypeComboBox.SelectedIndex = 0;

            // 点位集来自 exe 旁的 io-comments.json（导入 IO 表后写回）；文件不存在则为空列表
            LoadComments();

            // 设备状态日志：只在总览页展示，仅内存保存
            DeviceLogList.ItemsSource = _logPresenter.Entries;

            _ioInitialized = true;
            RebuildIoPoints();
            RefreshDriverAreas();
            SyncByteOrderCombos();

            // 全局变量（P3.3）：先载入 —— 自由监控里可能有监控变量行，
            // 载入时就要按变量表解析，否则第一轮会把这些行当成元件行标红
            _globalVarSet.SetDriver(_currentDriver);
            _globalVarSet.Load();
            GlobalVarItemsControl.ItemsSource = _globalVarSet.Items;
            RefreshGlobalVarView();

            // 自由监控：载入持久化项 + 绑定列表（解析失败项保留并标红）
            _monitorSet.SetDriver(_currentDriver);
            _monitorSet.SetVariableLookup(_globalVarSet.FindByName);
            _monitorSet.Load();
            MonitorItemsControl.ItemsSource = _monitorSet.Items;
            RefreshMonitorView();

            // 系统变量（P3.5）：清单写死在 Modeling/SystemVariable.cs —— 没有加载、
            // 没有保存、也没有增删，绑定一次就完事（与两个可编辑表格的区别正在于此）
            SystemVarItemsControl.ItemsSource = SystemVariable.All;
            SystemVarCountText.Text = $"共 {SystemVariable.All.Count} 个";

            // PLC 状态机（P1.2）：从 machine-state.json 读 9 个状态的文字/颜色/表达式，
            // 再把固定布局画出来。求值由 _machineTimer 驱动，这里只负责首次绑定。
            _machineVariables.AttachGlobals(_globalVarSet);
            _machineSet.Load(ExpressionNameExists);
            _machineSet.RecompileAll(ExpressionNameExists);
            MachineStateViewControl.Bind(_machineSet.Items, _machineSet.EdgeLabels);
            MachineStateViewControl.StateDoubleClicked += MachineStateView_StateDoubleClicked;
            MachineStateViewControl.EdgeLabelCommitted += (_, _) => _machineSet.Save();
            _machineTimer.Tick += (_, _) => RefreshMachineStates();

            // 系统变量监控（P2.3）：同一份清单，行对象多一个「当前值」；
            // 定时器先不启动 —— 由「分段可见 + 页面可见」两个条件共同决定（见 SyncSystemVarTimer）
            SystemVarMonitorItemsControl.ItemsSource = _sysVarRows;
            SystemVarMonitorCountText.Text = $"共 {_sysVarRows.Count} 个";
            _sysVarTimer.Tick += (_, _) => RefreshSystemVarValues();
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
        // 补偿方案、以及「拦 WM_GETMINMAXINFO 为什么无效」的完整记录见 Ui/MaximizedInset.cs。

        /// <summary>
        /// 薄包装：算法在 <see cref="MaximizedInset"/>（物理像素测量 + 按 DPI 换算）；
        /// 窗口侧只保留这个名字，<see cref="Window_StateChanged"/> 前后各调一次。
        /// </summary>
        private void UpdateMaximizedInset() => MaximizedInset.Apply(this, RootGrid);

        // ---------- 窗口生命周期与导航 ----------

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 主题已由 App.OnStartup 应用（窗口构造前），这里只同步分段选中态
            SyncThemeSegments();
            Themes.ThemeManager.ThemeChanged += OnThemeChanged;

            // P5-2：日志保存设置同样在 App 启动时已载入，这里只回填界面
            SyncLogSaveToggle();
            ApplyLogDirectory(ThemeManager.LogDirectory);

            // 连接参数与「自动连接」开关同理：启动时已从 app-settings.json 载入，这里只回填
            ApplyConnectionSettings();
            SyncAutoConnectToggle();

            // 自动连接排到窗口真正显示之后：连接是异步的，失败原因要能落到界面上。
            // 用 ApplicationIdle 而不是 Loaded 里直接调，是为了先让首帧画出来。
            Dispatcher.InvokeAsync(TryAutoConnect, DispatcherPriority.ApplicationIdle);

            // P5-3：项目信息全是常量，填一次即可（不需要进页面时刷新）
            RefreshProjectInfo();

            SyncInputHeights();
            ScanNetworkAdapters();
            UpdateOverviewStatus();

            // 状态机定时器：InitializeComponent 期间 Nav_Checked 就跑了，
            // 那会儿分段按钮还没造出来，条件必然为假 —— 窗口显示完必须补一次，
            // 否则停在总览页也不刷新（整张图一直是灰的）。
            _overviewPageActive = OverviewNav?.IsChecked == true;
            SyncMachineTimer();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // 采集跟随页面可见性：离开状态信息页即停采（零报文，灯保留上次数据）
            _ioPageActive = ReferenceEquals(sender, StatusInfoNav);
            SyncSystemVarTimer();

            // 状态机只在总览页可见时求值（离开就停，别白白每 50ms 算一遍）
            _overviewPageActive = ReferenceEquals(sender, OverviewNav);
            SyncMachineTimer();

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

            if (AcquisitionConfigPanel != null)
            {
                AcquisitionConfigPanel.Visibility = ReferenceEquals(sender, AcquisitionConfigNav)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (SettingsPanel != null)
            {
                bool settingsVisible = ReferenceEquals(sender, SettingsNav);
                SettingsPanel.Visibility = settingsVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // 切进设置页时补刷一次失败提示：AppendDeviceLog 只在面板**已可见**时才刷，
                // 若失败发生在进入之前（那时这个 TextBlock 没人看），不补这一下就永远看不到原因。
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
            _statusBar?.ReapplyTheme();
        }

        // ---------- 设置：日志保存（P5-2） ----------

        /// <summary>
        /// 日志开关被切换。与分段控件时期同款：<c>Checked</c> 也会被
        /// <see cref="SyncLogSaveToggle"/> 触发一次，那次写回同一个值，幂等。
        ///
        /// <para>
        /// 开关只表达"要不要落盘"，**不再需要「关闭」这一档文案** —— 关＝轨道变灰、
        /// 文字变「关」，本身就是状态，这正是 ToggleSwitch 比两段分段控件更省一层信息的原因。
        /// </para>
        /// </summary>
        private void LogSaveToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = LogSaveToggle.IsChecked == true;

            // 状态文案（开 / 关）必须在这里改：ControlTemplate 触发器里的
            // Setter Property="Content" 是模板优先级，赢不过控件上的局部值
            LogSaveToggle.Content = enabled ? "开" : "关";

            ThemeManager.SetLogSave(enabled);
            _logWriter.SetEnabled(enabled);
            RefreshLogSaveStatus();
        }

        private void SyncLogSaveToggle()
        {
            bool enabled = ThemeManager.LogSaveEnabled;
            LogSaveToggle.IsChecked = enabled;
            LogSaveToggle.Content = enabled ? "开" : "关";
        }

        // ---------- 设置：自动连接（PLC 连接开关） ----------

        private void SyncAutoConnectToggle()
        {
            bool enabled = ThemeManager.AutoConnectPlc;
            AutoConnectPlcToggle.IsChecked = enabled;
            AutoConnectPlcToggle.Content = enabled ? "开" : "关";
        }

        private void AutoConnectPlcToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = AutoConnectPlcToggle.IsChecked == true;

            // 与 LogSaveToggle 同理：状态文案必须在这里改，
            // ControlTemplate 触发器里的 Setter 是模板优先级，赢不过控件上的局部值
            AutoConnectPlcToggle.Content = enabled ? "开" : "关";

            ThemeManager.SetAutoConnectPlc(enabled);
        }

        /// <summary>
        /// 按「自动连接」设置尝试连一次 PLC。
        /// <para>
        /// 只在**以太网**模式下自动连：串口（Modbus RTU）还没实现，连了也只会失败。
        /// 失败**不重试** —— 与手动连接一致，首次连接失败交还控制权给用户。
        /// </para>
        /// </summary>
        private void TryAutoConnect()
        {
            if (!ThemeManager.AutoConnectPlc || _windowClosed)
            {
                return;
            }

            if (_session.IsConnected || _session.IsConnecting)
            {
                return;
            }

            if (ConnectionTypeComboBox?.SelectedIndex != 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(IpTextBox?.Text))
            {
                return;
            }

            // 不写设备日志（需求）：自动连接是后台行为，静默进行即可；
            // 成功会由 TryConnectAsync 记「已连接」，失败会记「连接失败」，不必再加一条。
            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        }

        // ---------- 连接参数持久化（IP / 端口） ----------

        /// <summary>
        /// 把持久化的连接参数回填到界面。
        /// <para>
        /// 没保存过的项（空串 / 0）**保持 XAML 里的默认值** ——
        /// 这样升级用户的旧 json 里没有这些字段时，行为与升级前完全一致。
        /// </para>
        /// </summary>
        private void ApplyConnectionSettings()
        {
            if (IpTextBox != null && ThemeManager.TargetIp.Length > 0)
            {
                IpTextBox.Text = ThemeManager.TargetIp;
            }

            if (PortTextBox != null && ThemeManager.TargetPort > 0)
            {
                PortTextBox.Text = ThemeManager.TargetPort.ToString();
            }
        }

        /// <summary>
        /// IP / 端口失焦落盘。
        /// <para>
        /// **不能在 TextChanged 里写**：那是逐字符触发，用户打到 "192.168.1." 这半截时
        /// 就被存进 json 了。与变量表的映射地址同一套「失焦提交」规则。
        /// </para>
        /// </summary>
        private void ConfigInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box)
            {
                return;
            }

            if (ReferenceEquals(box, IpTextBox))
            {
                ThemeManager.SetTargetIp(box.Text);
                return;
            }

            if (ReferenceEquals(box, PortTextBox))
            {
                // 端口非法（空 / 非数字 / 越界）时**保留上次的有效值**，不拿垃圾覆盖它
                if (int.TryParse(box.Text.Trim(), out int port) && port is >= 1 and <= 65535)
                {
                    ThemeManager.SetTargetPort(port);
                }
            }
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
        /// 回显日志写入失败的原因（P5-2）。
        ///
        /// <para>
        /// **只在写入失败时显示**，正常时整行隐藏。这么定是因为两类信息价值不同：
        /// 「启用 / 关闭」已由上面的分段控件表达，而「当前文件 + 本日行数」属于过程信息，
        /// 常驻显示只是噪声；失败原因相反 —— 那是**别处看不到、且必须由用户处理**的事
        /// （目录只读 / 磁盘满 / 文件被占），不能跟着一起静默，否则日志悄悄不写了没人知道。
        /// </para>
        /// <para>
        /// 写入器会自愈：下一次写成功即清空 <see cref="Logging.DeviceLogFileWriter.LastError"/>，
        /// 而本方法在每次落盘后都会被调用一次，故提示随自愈自动消失，无需手动清除。
        /// </para>
        /// </summary>
        private void RefreshLogSaveStatus()
        {
            if (LogSaveStatusText == null)
            {
                return;
            }

            // 关闭状态下没有"写入"这回事，自然也无失败可言（开关本身已表达状态）
            if (!ThemeManager.LogSaveEnabled || _logWriter.LastError == null)
            {
                LogSaveStatusText.Visibility = Visibility.Collapsed;
                return;
            }

            LogSaveStatusText.Text = $"日志写入失败：{_logWriter.LastError}";
            LogSaveStatusText.Foreground = Themes.ThemeManager.GetBrush("Brush.Warning")
                ?? System.Windows.Media.Brushes.DarkOrange;
            LogSaveStatusText.Visibility = Visibility.Visible;
        }

        // ---------- 设置：项目信息（P5-3） ----------

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
            // 前缀用大写 V：版本号里那个字母是"Version"的缩写，属于标识的一部分，
            // 不是句首，故不用小写（旧版小写 v 与 0.6.0 的圆点看着更"轻"，已统一为大写）。
            InfoVersionText.Text = $"V{AppInfo.Version}";
            InfoBuildDateText.Text = AppInfo.BuildDate;
            InfoRuntimeText.Text = $"{AppInfo.Runtime}　·　{AppInfo.OperatingSystem}";
        }

        // ---------- 底栏：右侧连接参数 ----------

        /// <summary>
        /// 刷新底栏右侧连接参数。底栏左侧的状态点与状态文字由 <see cref="SetStatus"/> 负责，
        /// 两者互不覆盖：参数随表单变化，状态随连接生命周期变化。
        /// </summary>
        private void UpdateOverviewStatus() => _statusBar?.RefreshDetail();

        /// <summary>
        /// 拼底栏右侧的连接参数串（读的是表单当前值）。控件尚未就绪时返回 <c>null</c>，
        /// 由 <see cref="StatusBarPresenter.RefreshDetail"/> 保持原样。
        /// </summary>
        private string? BuildConnectionDetail()
        {
            if (DeviceTypeComboBox == null || ConnectionTypeComboBox == null
                || ProtocolTypeComboBox == null
                || IpTextBox == null || PortTextBox == null)
            {
                return null;
            }

            string driver = DeviceTypeComboBox.SelectedItem as string
                            ?? (DeviceTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                            ?? DeviceTypeComboBox.Text;
            string connection = (ConnectionTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                                ?? ConnectionTypeComboBox.Text;
            string protocol = (ProtocolTypeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string
                              ?? ProtocolTypeComboBox.Text;
            string ipPort = $"{IpTextBox.Text.Trim()}:{PortTextBox.Text.Trim()}";

            return $"{driver}：{connection}  {protocol}  {ipPort}";
        }

        // ---------- 网卡扫描与记忆 ----------

        private void ScanNetworkAdapters()
        {
            if (TargetNicComboBox == null)
            {
                return;
            }

            // 记住当前选择（取网卡名，不含 IP）。整个扫描过程都算「程序化改动」：
            // Items.Clear() 在原本有选中项时就会触发一次 SelectionChanged，
            // 所以标志必须罩住整段，不能只罩最后那次赋值。
            string? previous = (TargetNicComboBox.SelectedItem as NicInfo)?.Name;

            _restoringNicSelection = true;
            try
            {
                List<NicInfo> found = NicScanner.Scan();

                TargetNicComboBox.Items.Clear();
                foreach (NicInfo nic in found)
                {
                    TargetNicComboBox.Items.Add(nic);
                }

                if (found.Count == 0)
                {
                    TargetNicComboBox.Items.Add("未找到可用网卡");
                    TargetNicComboBox.SelectedIndex = 0;
                    return;
                }

                // 还原优先级（本次选择 → 记住的 → 第 0 项）与「按网卡名而非显示文本
                // 匹配」的理由见 NicScanner.RestoreIndex。
                TargetNicComboBox.SelectedIndex =
                    NicScanner.RestoreIndex(found, previous, ThemeManager.TargetNic);
            }
            finally
            {
                _restoringNicSelection = false;
            }
        }

        /// <summary>
        /// 用户手动改选网卡时记住它，下次启动仍选同一块。
        ///
        /// <para>
        /// 程序化改动由 <see cref="_restoringNicSelection"/> 挡掉：扫描网卡列表
        /// （启动、切进本页、点刷新）都会重设 SelectedIndex，那不是用户意图。
        /// 挡掉还顺带修掉一个隐患 —— 若某块网卡临时消失（VPN 断开、网线拔掉），
        /// 扫描会回退到第 0 项；若把这次回退也写进偏好，用户原先的选择就被悄悄抹掉了。
        /// </para>
        /// </summary>
        private void TargetNicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringNicSelection)
            {
                return;
            }

            if (TargetNicComboBox.SelectedItem is NicInfo nic)
            {
                ThemeManager.SetTargetNic(nic.Name);
            }
        }

        // ---------- 表单控件高度同步 ----------

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

        // ---------- 连接状态机（建连 / 断开） ----------

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_windowClosed || _session.IsConnecting)
            {
                return;
            }

            if (_session.IsConnected)
            {
                // 日志规则 2：已断开（红）—— 用户主动断开也走红色，与异常断开同色
                AppendDeviceLog("已断开：用户主动断开", DeviceLogLevel.Disconnected);
                Disconnect();
                return;
            }

            // 正在自动重连时再点按钮 = 放弃重连（按钮此时显示「取消重连」）
            if (_session.IsReconnecting)
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
            SetStatus("正在连接 …", "#B26A00");

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
            using var connectCts = _session.BeginConnect(token);
            try
            {
                await client.ConnectAsync(target.Ip, target.Port, TimeSpan.FromSeconds(3),
                    target.LocalEndPoint, connectCts.Token);
                if (_windowClosed || connectCts.IsCancellationRequested)
                {
                    client.Dispose();
                    return false;
                }

                // 接管客户端、记住期望目标（异常断开后据此自动重连）、清零退避计数
                _session.MarkConnected(client, target);
                ConnectButton.Content = "断开";
                ConnectButton.IsEnabled = true;
                SetStatus("已连接", "#0B7A0B");
                AppendDeviceLog($"已连接：{target.Endpoint}", DeviceLogLevel.Connected);
                StartPolling(client, target.Driver);
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
                    SetStatus($"连接失败：{ClassifyDisconnectReason(ex.Message)}", "#C42B1C");
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
                _session.EndConnect(connectCts);
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

            // ⚠️ 三个「字节顺序」下拉框**刻意不参与连接期锁定**（P4.3，2026-10-21）：
            // 它们不改报文、不改连接参数，只决定「同一批寄存器怎么解释」，
            // 改完下一拍即生效、无需重连。锁上反而挡住最该做的动作 ——
            // 现场看到数值不对（如汇川字序与默认不符）时，当场换一套字序就能验证。

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
            _session.ForgetTarget();
            Disconnect("未连接", "#9A9A9A");
        }

        private void Disconnect(string statusText, string colorHex)
        {
            // 顺序有讲究：先取消在途的连接尝试，再停采集，最后释放客户端 ——
            // 反过来的话，正在 ConnectAsync 里等着的那一段会拿着已释放的客户端收尾。
            _session.CancelConnect();
            StopPolling();
            _session.ReleaseClient();
            ConnectButton.Content = "连接";
            SetInputsEnabled(true);
            SetStatus(statusText, colorHex);
        }

        // ---------- 自动重连：循环与退避（P1-1） ----------

        /// <summary>
        /// 异常断开后启动自动重连：按 <see cref="ReconnectPolicy"/> 指数退避反复尝试，
        /// 直到连上、用户主动断开、用户点「取消重连」，或窗口关闭。
        /// 重连期间**表单保持锁定、按钮显示「取消重连」**，状态栏显示尝试次数与倒计时。
        /// </summary>
        private void StartReconnect()
        {
            ReconnectTarget? target = _session.Target;
            if (target == null || _windowClosed || _session.IsReconnecting)
            {
                return;
            }

            CancellationTokenSource cts = _session.BeginReconnect();
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
                    int delay = ReconnectPolicy.DelayForAttempt(_session.Attempt);

                    // 倒计时显示：每秒刷新状态栏，让用户看到还在推进
                    for (int remain = delay; remain > 0 && !token.IsCancellationRequested; remain--)
                    {
                        SetStatus($"正在重连 … {remain}s（第 {_session.Attempt + 1} 次）",
                            "#B26A00");
                        await Task.Delay(1000, token);
                    }

                    if (token.IsCancellationRequested || _windowClosed)
                    {
                        return;
                    }

                    _session.CountAttempt();
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
                _session.EndReconnect(cts);
                cts.Dispose();
            }
        }

        /// <summary>取消在途的自动重连（用户主动断开、点「取消重连」或关窗时调用）。</summary>
        private void CancelReconnect() => _session.CancelReconnect();

        // ---------- 底栏：左侧状态文字与颜色 ----------

        /// <summary>
        /// 薄包装：状态色 → 主题键的映射、以及「换主题后按记住的文字重设颜色」都在
        /// <see cref="StatusBarPresenter"/>；窗口侧保留这个名字，12 个调用点未动。
        /// </summary>
        private void SetStatus(string text, string colorHex) => _statusBar?.Set(text, colorHex);

        // ---------- 设备状态日志（总览页底部） ----------

        /// <summary>
        /// 薄包装：按新主题重建日志各行的颜色。规则本体在
        /// <see cref="DeviceLogPresenter.RefreshThemeColors"/>（整表重建，避免滚动位置跳动）。
        /// </summary>
        private void RefreshDeviceLogColors() => _logPresenter.RefreshThemeColors();

        /// <summary>
        /// 追加一条设备状态日志：入内存（含上限裁剪）并落盘，然后滚动到最新一行。
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
        /// <para>
        /// 入内存、上限裁剪、**界面与文件共用同一个时间戳**、级别映射都在
        /// <see cref="DeviceLogPresenter.Append"/>；这里只做两件**控件**的事：
        /// 滚动到最新一行、设置页可见时刷新失败提示。
        /// </para>
        /// </summary>
        private void AppendDeviceLog(string message, DeviceLogLevel level)
        {
            LogEntry entry = _logPresenter.Append(message, level, _logWriter);

            DeviceLogList.ScrollIntoView(entry);

            // 设置页可见时刷新失败提示（失败 → 显示，自愈后 → 自动隐藏）。不可见就不刷 ——
            // 日志是高频旁路，让它去更新一个没人看的 TextBlock 没有必要。
            if (SettingsPanel?.Visibility == Visibility.Visible)
            {
                RefreshLogSaveStatus();
            }
        }

        private void ClearDeviceLogButton_Click(object sender, RoutedEventArgs e)
        {
            _logPresenter.Clear();
        }

        // ---------- 断开原因归类（底栏与日志共用） ----------

        /// <summary>
        /// 薄包装：规则本身在 <see cref="DisconnectReasonClassifier"/>（故意不引用 WPF，
        /// 可被 LoggingCheck 链接源码断言），窗口侧只保留这个名字以满足
        /// <see cref="ICollectorHost"/> 契约。
        /// </summary>
        private static string ClassifyDisconnectReason(string raw) => DisconnectReasonClassifier.Classify(raw);

        // ---------- 设备配置表单（驱动 / 字节序 / 采集周期） ----------

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

            // 全局变量的映射地址同样按新驱动重解析
            _globalVarSet.SetDriver(_currentDriver);
            GlobalVarItemsControl?.Items.Refresh();
            RefreshGlobalVarView();

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

            // 下拉框是「用户看到的值」，_byteOrders 是「真正拿去解释的值」——
            // 两者必须在这里一起重置，否则切换设备类型后界面显示新默认、数据仍按旧值解。
            ApplyByteOrders(ByteOrderSettings.FromDriver(driver));
        }

        /// <summary>
        /// 落定一份字节顺序：记到窗口字段 + 同步给监控集合（轮询回填读的是它）。
        /// 所有改动路径（同步下拉框、用户改下拉框）都必须走这里，避免两处真值漂移。
        /// </summary>
        private void ApplyByteOrders(ByteOrderSettings orders)
        {
            _byteOrders = orders;
            _monitorSet.SetByteOrders(orders);
        }

        /// <summary>
        /// 三个字节顺序下拉框共用：任一改动都重建整份 <see cref="ByteOrderSettings"/> 并立即生效
        /// （下一拍读取即按新字序解释，写入同样）。三套互相独立，改浮点不影响 32 位整数。
        /// </summary>
        private void ByteOrderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 程序化同步（切换设备类型）不算用户改动，跳过 —— 那条路径由 SyncByteOrderCombos 落定
            if (_syncingByteOrderCombos)
            {
                return;
            }

            if (Int32OrderComboBox == null || FloatOrderComboBox == null || StringOrderComboBox == null)
            {
                return;
            }

            ApplyByteOrders(new ByteOrderSettings(
                (ByteOrder32)Math.Max(0, Int32OrderComboBox.SelectedIndex),
                (ByteOrder32)Math.Max(0, FloatOrderComboBox.SelectedIndex),
                (StringByteOrder)Math.Max(0, StringOrderComboBox.SelectedIndex)));
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

        private void StartPolling(ModbusTcpClient client, CommDriver driver)
        {
            StopPolling();
            var cts = new CancellationTokenSource();
            _pollCts = cts;
            // 两项任务持有自己的连接和驱动快照，结束后才释放取消源。
            _ = RunConnectionTasksAsync(client, driver, cts);
        }

        private async Task RunConnectionTasksAsync(ModbusTcpClient client, CommDriver driver,
            CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            try
            {
                await Task.WhenAll(
                    Task.Run(() => _poller.PollLoopAsync(client, driver, token)),
                    Task.Run(() => HeartbeatLoopAsync(client, driver, token)));
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
            CancellationToken token)
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
                        SetStatus(warning == null ? "已连接" : $"通讯异常：{warning}",
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
            _session.IsCurrent(client, token, _windowClosed);

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

        void ICollectorHost.ReportCommunicationWarning(ModbusTcpClient client, CancellationToken token, string reason) =>
            ReportCommunicationWarning(client, token, reason);

        /// <summary>
        /// IO 轮询某块**连续多次**收到合法异常码的告警：链路仍在，只是从站拒绝了该请求。
        /// 走与心跳同一条「通讯异常」现象（琥珀），不新增日志种类，也**不断开**连接。
        /// </summary>
        private void ReportCommunicationWarning(ModbusTcpClient client, CancellationToken token, string reason)
        {
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsCurrentConnection(client, token))
                {
                    AppendDeviceLog($"通讯异常：{reason}", DeviceLogLevel.Warning);
                }
            }));
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
                    // 后者会调 ForgetTarget() 清掉期望目标 —— 那是「用户主动断开」的语义，
                    // 异常断开必须保留目标才能自动重连（P1-1）
                    Disconnect($"已断开：{ClassifyDisconnectReason(reason)}", "#C42B1C");
                    StartReconnect();
                }
            }));
        }

        // ---------- 标题栏按钮 / 关闭 / 窗口状态 ----------

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
            _sysVarTimer.Stop();
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

        // ---------- IO 元件区切换（P2） ----------

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
            bool sysView = SysVarViewSegment?.IsChecked == true;

            // 采集跟随可见分段：未显示区不读，保留上次数据
            _yViewActive = yView;
            _freeViewActive = freeView;
            _sysViewActive = sysView;

            IoColumnsGrid.Visibility = xView ? Visibility.Visible : Visibility.Collapsed;
            YIoColumnsGrid.Visibility = yView ? Visibility.Visible : Visibility.Collapsed;

            // X/Y 共用的滚动容器：另外两个分段下整层隐藏，
            // 免得空容器与面板叠在同一格
            if (IoScrollViewer != null)
            {
                IoScrollViewer.Visibility = freeView || sysView
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (FreeMonitorPanel != null)
            {
                FreeMonitorPanel.Visibility = freeView ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SystemVarMonitorPanel != null)
            {
                SystemVarMonitorPanel.Visibility = sysView ? Visibility.Visible : Visibility.Collapsed;
            }

            // 导入 IO 表按钮只在 X/Y 分段下有意义（另外两个分段各有自己的内容）
            if (ImportIoTableButton != null)
            {
                ImportIoTableButton.Visibility = freeView || sysView
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            // IO 分页控件在另外两个分段下无意义
            if (IoPagerPanel != null && (freeView || sysView))
            {
                IoPagerPanel.Visibility = Visibility.Collapsed;
            }

            // 空提示只属于 X/Y（另外两个分段各有自己的内容）
            if (IoEmptyHint != null && (freeView || sysView))
            {
                IoEmptyHint.Visibility = Visibility.Collapsed;
            }

            // 系统变量定时器跟随分段启停
            SyncSystemVarTimer();

            if (freeView)
            {
                RefreshMonitorView();
            }
            else if (sysView)
            {
                RefreshSystemVarValues();
            }
            else
            {
                // 切段后本段有自己的页号与页数，重新切片绑定
                ApplyIoPage();
            }
        }

        // ---------- PLC 状态机（P1.2） ----------

        /// <summary>表达式里能引用的变量名：系统变量 + 全局变量（两者不会重名）。</summary>
        private bool ExpressionNameExists(string name) =>
            SystemVariable.Find(name) != null || _globalVarSet.FindByName(name) != null;

        /// <summary>
        /// 全局变量模型列表，供补全用（P7.1）。
        /// <para>
        /// 走 <c>Items.Select(Model)</c> 而不是把 <c>GlobalVarItemSet</c> 直接传下去：
        /// <c>Modeling/</c> 不引用界面层，<c>CompletionSource</c> 只认 <see cref="GlobalVariable"/>。
        /// </para>
        /// </summary>
        private IEnumerable<GlobalVariable> GlobalVarModels =>
            _globalVarSet.Items.Select(item => item.Model);

        /// <summary>
        /// 状态机定时器启停：**总览页可见 且 停在「PLC状态机」分段** 才跑。
        /// </summary>
        private void SyncMachineTimer()
        {
            bool shouldRun = _overviewPageActive
                && MachineStateSegment?.IsChecked == true
                && !_windowClosed;

            if (shouldRun == _machineTimer.IsEnabled)
            {
                return;
            }

            if (shouldRun)
            {
                RefreshMachineStates();
                _machineTimer.Start();
            }
            else
            {
                _machineTimer.Stop();
            }
        }

        /// <summary>
        /// 一拍：刷新系统变量快照 → 求值 9 条表达式 → 把变了的框重画。
        /// </summary>
        private void RefreshMachineStates()
        {
            _machineVariables.UpdateSystemValues(
                SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed));

            _machineSet.EvaluateAll(_machineVariables);

            foreach (MachineStateItem item in _machineSet.Items)
            {
                MachineStateViewControl.ApplyVisualState(item);
            }
        }

        /// <summary>
        /// 双击状态框：弹编辑窗。确定后写回模型并落盘。
        /// <para>表达式在弹窗里已经校验并回退过了，这里拿到的都是合法值。</para>
        /// </summary>
        private void MachineStateView_StateDoubleClicked(object? sender, MachineStateItem item)
        {
            Ui.MachineStateEditDialog? dialog =
                Ui.MachineStateEditDialog.Ask(
                this, item, ExpressionNameExists, () => CompletionSource.ForExpression(GlobalVarModels));

            if (dialog != null)
            {
                item.Text = dialog.StateText;
                item.FontStyle = dialog.FontStyleValue;
                item.FontSize = dialog.FontSizeValue;
                item.Color = dialog.ColorHex;

                // 走集合的唯一入口：编译 + 变量存在性校验 + 清掉旧的错误态
                _machineSet.TrySetExpression(item, dialog.ExpressionText, ExpressionNameExists, out _);
                _machineSet.Save();

                MachineStateViewControl.ApplyVisualState(item);
                RefreshMachineStates();
            }

            // 编辑期间定时器可能停过（弹窗是模态的，但页面仍可见），这里顺手校一次
            SyncMachineTimer();
        }

        /// <summary>总览页分段切换（PLC状态机 / 生产数据）。</summary>
        private void OverviewView_Checked(object sender, RoutedEventArgs e)
        {
            if (MachineStateViewControl == null)
            {
                return;
            }

            bool machineView = MachineStateSegment?.IsChecked == true;

            MachineStateViewControl.Visibility = machineView
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ProductionDataHint != null)
            {
                ProductionDataHint.Visibility = machineView
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            SyncMachineTimer();

            if (machineView)
            {
                RefreshMachineStates();
            }
        }

        /// <summary>
        /// 表达式取值适配器：把「系统变量快照 + 全局变量当前值」按 BOOL / 数字 交给表达式引擎。
        /// <para>
        /// ⚠️ 系统变量优先：虽然现在禁止重名（<c>GlobalVariableRules.TryValidateName</c> 会拦），
        /// 但手工改过的 json 仍可能撞上，顺序定死才不会出现「同一份配置两台机器算出不同结果」。
        /// </para>
        /// </summary>
        private sealed class ExpressionVariables : IExpressionVariables
        {
            private IReadOnlyDictionary<string, string> _system =
                new Dictionary<string, string>(StringComparer.Ordinal);

            private GlobalVarItemSet? _globals;

            public void UpdateSystemValues(IReadOnlyDictionary<string, string> values) =>
                _system = values;

            public void AttachGlobals(GlobalVarItemSet globals) => _globals = globals;

            public bool TryGetValue(string name, out ExpressionValue value, out string error)
            {
                if (SystemVariable.Find(name) is { } system)
                {
                    if (_system.TryGetValue(name, out string? text))
                    {
                        return Convert(system.Type, text, out value, out error);
                    }

                    error = $"系统变量“{name}”当前取不到值";
                    value = default;
                    return false;
                }

                GlobalVariable? global = _globals?.FindByName(name);
                if (global == null)
                {
                    error = $"变量“{name}”不存在";
                    value = default;
                    return false;
                }

                return Convert(global.Type, global.Value, out value, out error);
            }

            private static bool Convert(
                DataType type, string? text, out ExpressionValue value, out string error)
            {
                string raw = (text ?? string.Empty).Trim();

                if (type == DataType.Bool)
                {
                    // 与界面里 BOOL 的 "1"/"0" 显示一致；顺手容忍 TRUE/FALSE
                    bool b = raw is "1"
                        || raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

                    value = ExpressionValue.FromBool(b);
                    error = string.Empty;
                    return true;
                }

                if (double.TryParse(
                        raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    value = ExpressionValue.FromNumber(number);
                    error = string.Empty;
                    return true;
                }

                value = ExpressionValue.FromNumber(0);
                error = $"变量值“{raw}”不是数字";
                return false;
            }
        }

        // ---------- 系统变量监控（P2.3） ----------

        /// <summary>
        /// 把当前时刻的系统变量值推给界面。
        /// <para>
        /// 只写「变了的值」—— <see cref="SystemVarRow.Value"/> 的 setter 会挡掉同值赋值，
        /// 故 100ms 一拍、16 行也不会产生无谓的绑定通知。
        /// </para>
        /// </summary>
        private void RefreshSystemVarValues()
        {
            IReadOnlyDictionary<string, string> values =
                SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed);

            foreach (SystemVarRow row in _sysVarRows)
            {
                if (values.TryGetValue(row.Name, out string? value) && value != null)
                {
                    row.Value = value;
                }
            }
        }

        /// <summary>
        /// 系统变量定时器启停：**本分段可见 且 状态信息页可见** 才跑。
        /// <para>
        /// 与 X/Y、自由监控「采集跟随可见分段」同一套规矩 ——
        /// 用户在看别的页面时，没有理由每 100ms 算一遍 16 个变量。
        /// </para>
        /// </summary>
        private void SyncSystemVarTimer()
        {
            bool shouldRun = _sysViewActive && _ioPageActive && !_windowClosed;

            if (shouldRun == _sysVarTimer.IsEnabled)
            {
                return;
            }

            if (shouldRun)
            {
                RefreshSystemVarValues();
                _sysVarTimer.Start();
            }
            else
            {
                _sysVarTimer.Stop();
            }
        }

        // ---------- 采集配置页分段（P3.1 / P3.3） ----------

        /// <summary>采集配置页分段切换：采集数据 / 全局变量 / 系统变量。</summary>
        private void AcqView_Checked(object sender, RoutedEventArgs e)
        {
            if (AcquisitionDataPanel == null || GlobalVarPanel == null || SystemVarPanel == null
                || AcquisitionDataSegment == null)
            {
                return;
            }

            bool varView = GlobalVarSegment?.IsChecked == true;
            bool sysView = SystemVarSegment?.IsChecked == true;

            // 本页只管理变量定义，不采集值（值/格式的读写属后续阶段）；
            // 系统变量连定义都不能改 —— 它是写死的清单，这里只负责摆出来。
            AcquisitionDataPanel.Visibility = varView || sysView
                ? Visibility.Collapsed
                : Visibility.Visible;
            GlobalVarPanel.Visibility = varView ? Visibility.Visible : Visibility.Collapsed;
            SystemVarPanel.Visibility = sysView ? Visibility.Visible : Visibility.Collapsed;

            if (varView)
            {
                RefreshGlobalVarView();
            }
        }

        // ---------- 全局变量（P3.3） ----------

        /// <summary>
        /// 全部数据类型的规格（线圈区 + 寄存器区合并，顺序同规格表）。
        /// <para>
        /// <c>DataTypes.AllSpecs</c> 是私有的（对外只暴露 <c>CoilTypes</c> / <c>RegisterTypes</c>），
        /// 而变量表要在一个下拉框里同时给出两类，故在这里拼一次。
        /// </para>
        /// </summary>
        private static IEnumerable<DataTypeSpec> AllTypeSpecs()
        {
            foreach (DataTypeSpec spec in DataTypes.CoilTypes)
            {
                yield return spec;
            }

            foreach (DataTypeSpec spec in DataTypes.RegisterTypes)
            {
                yield return spec;
            }
        }

        /// <summary>刷新全局变量的计数、空提示与按钮可用性。</summary>
        private void RefreshGlobalVarView()
        {
            if (GlobalVarItemsControl == null)
            {
                return;
            }

            int n = _globalVarSet.Items.Count;

            // 表头不随空列表隐藏 —— 它是列说明，也是「+」的落点
            if (GlobalVarEmptyHint != null)
            {
                GlobalVarEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ClearGlobalVarsButton != null)
            {
                ClearGlobalVarsButton.IsEnabled = n > 0;
            }

            if (GlobalVarCountText != null)
            {
                GlobalVarCountText.Text = n == 0 ? string.Empty : $"共 {n} 个";
            }
        }

        private void AddGlobalVar_Click(object sender, RoutedEventArgs e)
        {
            // 生成一个默认不重名的名字：var1、var2 …
            string name = "var1";
            for (int i = 2; _globalVarSet.Items.Any(x => x.Name == name); i++)
            {
                name = "var" + i;
            }

            GlobalVarItem? item = _globalVarSet.Add(name, out string error);
            if (item == null)
            {
                MessageBox.Show(this, error, "新增变量失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshGlobalVarView();
            GlobalVarItemsControl?.UpdateLayout();
            FocusGlobalVarRow(item);
        }

        private void RemoveGlobalVar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GlobalVarItem item)
            {
                _globalVarSet.Remove(item);
                RefreshGlobalVarView();

                // 被删的变量可能正被监控行引用，那些行现在应标红「变量不存在」
                RefreshMonitorRowsAfterVariableChange();
            }
        }

        private void ClearGlobalVars_Click(object sender, RoutedEventArgs e)
        {
            if (_globalVarSet.Items.Count == 0)
            {
                return;
            }

            if (MessageBox.Show(this, $"确定删除全部 {_globalVarSet.Items.Count} 个变量？",
                    "删除全部变量", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK)
            {
                return;
            }

            _globalVarSet.Clear();
            RefreshGlobalVarView();

            // 全部删完，所有监控变量行都会变成「变量不存在」
            RefreshMonitorRowsAfterVariableChange();
        }

        /// <summary>行容器加载：把行对象挂到 Tag，供事件处理器取用。</summary>
        private void GlobalVarRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not GlobalVarItem item)
            {
                return;
            }

            fe.Tag = item;

            // 回填各控件初值（DataTemplate 里不能直接双向绑定全部字段：
            // 名称 / 地址 / 注释都要走「失焦提交」而不是逐字符提交）
            if (FindGlobalVarBox(fe, "VarNameBox") is TextBox nameBox)
            {
                nameBox.Text = item.Name;
            }

            if (FindGlobalVarBox(fe, "VarAddressBox") is TextBox addrBox)
            {
                addrBox.Text = item.Address;
            }

            if (FindGlobalVarBox(fe, "VarCommentBox") is TextBox commentBox)
            {
                commentBox.Text = item.Comment;
            }

            SyncGlobalVarTypeCombo(fe, item);
            SyncGlobalVarFormatCombo(fe, item);
        }

        private static TextBox? FindGlobalVarBox(DependencyObject root, string name)
        {
            if (root is TextBox tb && tb.Name == name)
            {
                return tb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                TextBox? found = FindGlobalVarBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static ComboBox? FindGlobalVarCombo(DependencyObject root, string name)
        {
            if (root is ComboBox cb && cb.Name == name)
            {
                return cb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ComboBox? found = FindGlobalVarCombo(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void SyncGlobalVarTypeCombo(DependencyObject root, GlobalVarItem item)
        {
            if (FindGlobalVarCombo(root, "VarTypeCombo") is not ComboBox combo)
            {
                return;
            }

            // ⚠️ 必须放 DataTypeSpec **对象**，不能放 DisplayName 字符串：
            // MonitorTypeItemTemplate 绑的是 {Binding DisplayName}，字符串没这个属性，
            // 展开列表会整列空白（只看到选中条，没有文字）。
            List<DataTypeSpec> specs = AllTypeSpecs().ToList();
            combo.ItemsSource = specs;

            DataTypeSpec current = DataTypes.Get(item.Model.Type);
            DataTypeSpec? hit = specs.FirstOrDefault(sp => sp.Type == current.Type);
            combo.SelectedItem = hit ?? specs[0];
        }

        private static void SyncGlobalVarFormatCombo(DependencyObject root, GlobalVarItem item)
        {
            if (FindGlobalVarCombo(root, "VarFormatCombo") is not ComboBox combo)
            {
                return;
            }

            // 格式下拉放枚举值本身，由 EnumUpperTemplate 转成大写名
            IReadOnlyList<DisplayFormat> formats = DataTypes.Get(item.Model.Type).AllowedFormats;
            combo.ItemsSource = formats;

            bool known = formats.Contains(item.Model.Format);
            combo.SelectedItem = known ? item.Model.Format : formats[0];
        }

        /// <summary>
        /// 从行内某个控件往上找该行的模板根（<c>GlobalVarRow_Loaded</c> 把行对象挂在它的 Tag 上）。
        /// <para>
        /// 用于「改了本行某一格，要顺带改同一行另一格」的场景 —— 如切换数据类型时清空映射地址。
        /// </para>
        /// </summary>
        private static FrameworkElement? FindGlobalVarRowRoot(DependencyObject from)
        {
            DependencyObject? node = from;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.Tag is GlobalVarItem)
                {
                    return fe;
                }

                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        /// <summary>
        /// 切换数据类型后清空该行的映射地址（模型与控件一起清）。
        /// <para>
        /// 地址框不是双向绑定（失焦才提交），所以改 <c>Model.Address</c> 不会自动刷控件，
        /// 必须顺着触发控件往上找到本行模板根，再显式把文本清空。
        /// </para>
        /// </summary>
        /// <param name="from">触发切换的控件（用来定位所在行）</param>
        /// <param name="item">该行对应的变量</param>
        private static void ClearGlobalVarAddress(DependencyObject from, GlobalVarItem item)
        {
            item.Address = string.Empty;

            if (FindGlobalVarRowRoot(from) is FrameworkElement root
                && FindGlobalVarBox(root, "VarAddressBox") is TextBox addrBox)
            {
                addrBox.Text = string.Empty;
            }
        }

        private static GlobalVarItem? VarRowOf(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.DataContext is GlobalVarItem fromContext)
                {
                    return fromContext;
                }

                if (fe.Tag is GlobalVarItem fromTag)
                {
                    return fromTag;
                }
            }

            return null;
        }

        /// <summary>
        /// 变量定义变了（改名 / 改类型 / 改地址 / 删除）之后，让自由监控里引用变量的行重新解析。
        /// <para>
        /// 监控行的类型与地址都是从变量表现取的，变量一改它就得跟着重算 ——
        /// 不刷新的话，界面上会继续显示旧类型、继续按旧地址采集。
        /// </para>
        /// </summary>
        private void RefreshMonitorRowsAfterVariableChange()
        {
            if (_monitorSet.Items.Count == 0)
            {
                return;
            }

            _monitorSet.ReResolveAll();

            // 已生成的行容器要同步类型框（置灰与否、显示的类型名都可能变了）。
            // 遍历**可视树**而不是 ItemContainerGenerator：容器未必已生成，
            // 且这里要的是模板根（Tag 上挂着 MonitorItem 的那个），容器层级并不保证。
            if (MonitorItemsControl != null)
            {
                foreach (FrameworkElement row in EnumerateRows(MonitorItemsControl))
                {
                    if (row.Tag is not MonitorItem item)
                    {
                        continue;
                    }

                    if (item.IsVariable)
                    {
                        ApplyVariableRowState(row, item);
                    }
                    else
                    {
                        SyncMonitorTypeWithElement(row, item, autoCorrect: false);
                    }
                }
            }

            // 不落盘：变量定义变了但监控行的持久化字段（变量名 / 元件）没变，
            // 类型与地址都是解析时从变量表现取的临时值，本来就不进 json
        }

        /// <summary>
        /// 变量名失焦（回车同效）：**唯一提交点**。
        /// <para>
        /// 打字途中**不校验**（需求）：一次打完再检查。不合法直接弹窗
        /// 「变量名称不合法」+ 具体规则，随后回退到上次有效名称 ——
        /// 模型与磁盘里都不留非法变量名。
        /// </para>
        /// </summary>
        private void CommitGlobalVarName(TextBox tb)
        {
            if (VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            string wanted = tb.Text.Trim();

            if (_globalVarSet.Rename(item, wanted, out string error))
            {
                tb.Text = item.Name;
                _globalVarSet.Save();

                // 改名会让引用旧名字的监控行失效，必须重解析一遍
                RefreshMonitorRowsAfterVariableChange();
                return;
            }

            // 失败：先回退文本，再把规则说清楚。
            // 正文最前面是**用户刚输入的内容**（外加双引号，需求），一眼看出被拒的是哪一段；
            // 标题点出性质，正文后接具体规则。输入为空时不加引号。
            tb.Text = item.Name;
            string input = wanted.Length == 0 ? string.Empty : $"“{wanted}”";
            MessageBox.Show(
                this,
                $"{input}{error}",
                "变量名称不合法",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>变量名框失焦：交给唯一的提交点。</summary>
        private void GlobalVarName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                CommitGlobalVarName(tb);
            }
        }

        /// <summary>变量名框按回车：等同失焦，立刻校验。</summary>
        private void GlobalVarName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb)
            {
                return;
            }

            e.Handled = true;
            CommitGlobalVarName(tb);
        }

        /// <summary>
        /// 映射地址失焦：空 = 内存变量；非空则按驱动解析。
        /// <para>
        /// 解析失败**弹框告知**，因为地址是「元件 + 编号」的自由文本，用户看不出自己错在哪：
        /// 类型不符要说清「这个元件不能配这种类型」，软元件无效要说清「这个地址不存在」。
        /// </para>
        /// </summary>
        private void GlobalVarAddress_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            string wanted = tb.Text.Trim();
            item.Address = wanted;
            _globalVarSet.ResolveOnly(item, out AddressErrorKind kind);

            if (kind != AddressErrorKind.None)
            {
                // 失败时保留用户原样输入，方便就地改
                ShowGlobalVarAddressError(item, wanted, kind);
            }
            else if (tb.Text != item.Address)
            {
                // 解析成功时元件符号已归一大写（y27 → Y27），把规范形式回填到框里，
                // 与自由监控的名称框一致；不这么做，界面留着 y27、json 里却是 Y27
                tb.Text = item.Address;
            }

            _globalVarSet.Save();

            // 地址变了，引用该变量的监控行要跟着换地址
            RefreshMonitorRowsAfterVariableChange();
        }

        /// <summary>
        /// 映射地址校验失败的提示框。
        /// <para>
        /// 文案本身在 <see cref="GlobalVariableRules.DescribeAddressError"/> —— 那是纯函数，
        /// 离线断言能直接核对，这里只负责弹出来。
        /// </para>
        /// </summary>
        private void ShowGlobalVarAddressError(GlobalVarItem item, string input, AddressErrorKind kind)
        {
            GlobalVariableRules.DescribeAddressError(
                kind, input, item.ResolveError, out string title, out string body);

            MessageBox.Show(this, body, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void GlobalVarComment_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            item.Comment = tb.Text;
            _globalVarSet.Save();
        }

        private void GlobalVarType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo || VarRowOf(combo) is not GlobalVarItem item)
            {
                return;
            }

            // 下拉项是 DataTypeSpec 对象（见 SyncGlobalVarTypeCombo），不是显示名字符串
            if (combo.SelectedItem is not DataTypeSpec picked)
            {
                return;
            }

            DataType matched = picked.Type;

            if (item.Model.Type == matched)
            {
                return;
            }

            // STRING 需要用户声明长度。
            // 与自由监控走**同一个**对话框与同一套参数（见 AskStringLength），
            // 不在这里另写一份文案与范围 —— 两处各写一份，改一处就悄悄不一致了。
            if (matched == DataType.String)
            {
                int? len = AskStringLength(item.Model.Length);

                if (len == null)
                {
                    SyncGlobalVarTypeCombo(combo, item);
                    return;
                }

                item.Model.Length = len.Value;
            }

            DataType oldType = item.Model.Type;
            item.Model.Type = matched;
            item.RaiseTypeChanged();

            // 只在**存储区真的变了**时才清空映射地址：
            // BOOL 走线圈、其余走寄存器，跨区换类型后旧地址必然校验失败；
            // 而 INT → REAL 这类同区换类型地址完全可用，清掉只是让用户重打一遍
            if (GlobalVariableRules.AddressInvalidatedBy(oldType, matched))
            {
                ClearGlobalVarAddress(combo, item);
            }

            // 格式必须随类型纠正（BOOL 只允许 BIN、STRING 只允许 ASCII）——
            // 本页不显示格式列，但模型仍要合法，否则读取/后续导出会拿到非法组合
            DataTypeSpec newSpec = DataTypes.Get(matched);
            if (!newSpec.AllowsFormat(item.Model.Format))
            {
                item.Model.Format = newSpec.DefaultFormat;
            }

            _globalVarSet.ResolveOnly(item);
            _globalVarSet.Save();
            RefreshGlobalVarView();

            // 类型变了，引用该变量的监控行要跟着换类型与读取长度
            RefreshMonitorRowsAfterVariableChange();
        }

        /// <summary>把焦点移到指定变量的名称框（新增后连续录入用）。</summary>
        private void FocusGlobalVarRow(GlobalVarItem item)
        {
            if (GlobalVarItemsControl == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < GlobalVarItemsControl.Items.Count; i++)
                {
                    if (GlobalVarItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                            is not DependencyObject container)
                    {
                        continue;
                    }

                    if (container is FrameworkElement fe && fe.DataContext is GlobalVarItem rowItem
                        && ReferenceEquals(rowItem, item))
                    {
                        FindGlobalVarBox(container, "VarNameBox")?.Focus();
                        return;
                    }
                }
            }));
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
        /// 元件名称输入框**失焦**（回车同效）：**这是唯一提交点** —— 归一文本、重解析、落盘。
        /// <para>
        /// 打字途中**不校验**（需求）：用户不必在输入 <c>D</c>、<c>D1</c> 时被红字打断，
        /// 一次打完再检查。检查时机是失焦或回车。
        /// </para>
        /// <para>
        /// 三种结果：
        /// ① 合法且能解析 → 提交、落盘，下一拍轮询开始采集；
        /// ② 名称不合法（拆不出元件 / 元件不在该驱动 / 编号越界）→
        /// <b>弹窗「不是有效软元件或变量」</b>，且**不提交**（保持上次有效值）；
        /// ③ 是合法变量但不能采集（没有映射地址 / 地址无效）→ 弹窗说明具体原因。
        /// </para>
        /// <para>
        /// 不合法的配置**绝不落盘**：否则下次启动会带着一个永远读不到的项。
        /// </para>
        /// </summary>
        private void CommitMonitorName(TextBox tb)
        {
            if (RowOf(tb) is not MonitorItem item)
            {
                return;
            }

            string previous = item.Name;
            string typed = tb.Text;

            // 系统变量（P2.3）：不在全局变量表里，不拦的话它会一路掉到元件解析，
            // 报成「不是有效软元件或变量」—— 原因不对，用户会去改元件名。
            // 这里单独说清「不能用」，而不是「不存在」。
            if (SystemVariable.Find(typed.Trim()) != null)
            {
                tb.Text = previous;
                MessageBox.Show(
                    this,
                    $"“{typed.Trim()}”是系统变量，不能在自由监控中使用。",
                    "系统变量",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!item.SetName(typed, out _))
            {
                // ②-1 拆不出「元件 + 编号」，且不是已定义的变量名
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);
                ShowInvalidMonitorName(typed);
                return;
            }

            // 归一（元件符号大写 / 变量名原样）后回填输入框
            string canonical = item.Name;
            if (tb.Text != canonical)
            {
                tb.Text = canonical;
            }

            // 名称归一后与落盘值一致、且当前没有错误态 → 真的什么都没变，不必提交。
            // 注意判据用 previous（SetName 之前的值）而不是"模型当前值"，
            // 否则一旦模型被别处改过就会误判"没变化"而漏掉落盘。
            if (canonical == previous && !item.HasError)
            {
                return;
            }

            // 变量行：类型由变量表决定，**不能**按元件反推类型（那会把变量声明改掉）
            if (item.IsVariable)
            {
                item.Resolve(_currentDriver!, out string varError);
                if (item.HasError)
                {
                    // ③ 变量本身合法，只是**不能用于采集**
                    MessageBox.Show(
                        this,
                        $"变量「{canonical}」不可用于监控：\n\n{varError}",
                        "变量无法采集",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    tb.Text = previous;
                    item.SetName(previous, out _);
                    _monitorSet.ResolveOnly(item);
                    return;
                }

                _monitorSet.AfterChange(item);
                if (FindMonitorRowRoot(tb) is FrameworkElement varRow)
                {
                    ApplyVariableRowState(varRow, item);
                }

                return;
            }

            // 元件变了 → 线圈/寄存器归属可能变，类型候选与当前类型要跟着走
            if (tb.Parent is FrameworkElement row && canonical != previous)
            {
                SyncMonitorTypeWithElement(row, item);
            }

            // ②-2 语法成立、但软元件本身无效（符号不在驱动 / 编号越界 / 八进制非法）
            item.Resolve(_currentDriver!, out _);
            if (item.HasError)
            {
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);
                ShowInvalidMonitorName(typed);
                return;
            }

            // ① 提交：落盘，下一拍轮询即开始采集该地址
            _monitorSet.AfterChange(item);
        }

        /// <summary>
        /// 名称无效的统一提示（需求定稿文案）。
        /// <para>
        /// 正文最前面是**用户刚输入的内容**（外加双引号），后接定稿文案。
        /// 输入为空时不加引号 —— 空的 <c>“”</c> 只是噪音。
        /// </para>
        /// </summary>
        private void ShowInvalidMonitorName(string? input)
        {
            string text = (input ?? string.Empty).Trim();
            string prefix = text.Length == 0 ? string.Empty : $"“{text}”";

            MessageBox.Show(
                this,
                $"{prefix}不是有效软元件或变量",
                "输入无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>
        /// 元件名称框加载：挂变量名补全（P7.1）。
        /// <para>
        /// 挂在 <c>Loaded</c> 而不是 XAML 里直接写，是因为这一行在 <c>DataTemplate</c> 里 ——
        /// 每个监控项各自一份，没有可以一次性写死的那个文本框。
        /// </para>
        /// <para>
        /// 用 <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
        /// 记住「这个框已经挂过了」：模板复用/虚拟化会让同一个框多次 Loaded，
        /// 重复挂载会让同一次按键被处理两遍（表现是补全连跳两格）。
        /// </para>
        /// </summary>
        private void MonitorName_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb)
            {
                return;
            }

            if (_nameCompletions.TryGetValue(tb, out _))
            {
                return;
            }

            // 候选现算：变量表随时可变，挂载时抓快照会联想出已删掉的变量
            var popup = Ui.CompletionPopup.Attach(tb, () => CompletionSource.ForMonitorName(GlobalVarModels));
            _nameCompletions.Add(tb, popup);
        }

        /// <summary>元件名称框失焦：交给唯一的提交点。</summary>
        private void MonitorName_LostFocus(object sender, RoutedEventArgs e)
        {
            // 补全列表开着时的失焦，是「用户正在点候选」，不是「输入结束」——
            // 此刻提交会把半截名字（如 "sys_"）当成错误弹窗。
            // 弹窗本身用 WS_EX_NOACTIVATE 不抢焦点，正常情况走不到这里；
            // 这一句是兜底：万一某些输入法/主题下焦点还是丢了，也不至于误报。
            if (sender is TextBox tb && _nameCompletions.TryGetValue(tb, out Ui.CompletionPopup? popup) && popup.IsOpen)
            {
                return;
            }

            if (sender is TextBox box)
            {
                CommitMonitorName(box);
            }
        }

        /// <summary>
        /// 元件名称框按回车：等同失焦，立刻校验。
        /// <para>
        /// 回车不会自己移走焦点，故直接调提交逻辑；之后真正的 LostFocus 再来一次时，
        /// 名称已归一且无错误，会在"没变化"处提前返回，不会重复落盘。
        /// </para>
        /// </summary>
        private void MonitorName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb)
            {
                return;
            }

            e.Handled = true;
            CommitMonitorName(tb);
        }

        /// <summary>
        /// 变量行：类型下拉框**置灰**并显示变量声明的类型。
        /// <para>
        /// 类型来自变量表，改它等于绕过变量表另立一份定义，两边立刻不一致 ——
        /// 故这里只展示、不开放编辑。要改类型请去「全局变量」页。
        /// </para>
        /// </summary>
        private static void ApplyVariableRowState(FrameworkElement row, MonitorItem item)
        {
            if (row.FindName("TypeCombo") is not ComboBox typeCombo)
            {
                return;
            }

            // 候选集只放「变量声明的这一个类型」：置灰后用户点不开，
            // 但闭合态仍要显示正确文字（模板绑的是数据上下文的 TypeDisplayName）
            typeCombo.ItemsSource = new[] { DataTypes.Get(item.Model.Type) };
            typeCombo.SelectedIndex = 0;
            typeCombo.IsEnabled = item.IsTypeEditable;
        }

        /// <summary>深度遍历可视树，取出所有「Tag 上挂着 MonitorItem」的行根。</summary>
        private static IEnumerable<FrameworkElement> EnumerateRows(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Tag is MonitorItem)
                {
                    yield return fe;
                }

                foreach (FrameworkElement nested in EnumerateRows(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// 从行内某个控件往上找该行的模板根（<c>MonitorRow_Loaded</c> 把行对象挂在它的 Tag 上）。
        /// </summary>
        private static FrameworkElement? FindMonitorRowRoot(DependencyObject from)
        {
            DependencyObject? node = from;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.Tag is MonitorItem)
                {
                    return fe;
                }

                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            return null;
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
                // 从变量行改回元件行时，类型框要恢复可用（变量行里它是灰的）
                typeCombo.IsEnabled = item.IsTypeEditable;

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
                // 与全局变量共用同一个入口，参数不在这里重复定义
                int? len = AskStringLength(item.Model.Length);

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
            ModbusTcpClient? modbus = _session.Client;
            CommDriver? driver = _currentDriver;

            string text = box.Text;

            // 回车与失焦是两条路径，会先后各触发一次提交（回车后焦点转移会连带引得失焦）。
            // 同一次编辑只写一次：值与上次提交的相同就跳过。
            if (_lastCommittedValue.TryGetValue(box, out string? last) && last == text)
            {
                return;
            }

            // 内存变量：没有映射地址，没有寄存器可写，**也不需要连接** ——
            // 值直接存进变量本身，一个报文都不发。
            // 放在连接检查之前，否则没连 PLC 时连内存变量都改不了。
            if (item.IsMemoryVariable)
            {
                if (!item.TrySetMemoryValue(text, _byteOrders, out string memoryError))
                {
                    item.WriteError = memoryError;
                    item.IsEditing = true;
                    _lastCommittedValue[box] = text; // 同一份非法内容不重复报告
                    return;
                }

                _lastCommittedValue[box] = text;
                item.WriteError = null;
                item.ReadError = null;
                item.IsEditing = false;
                return;
            }

            // 按需求：建立连接后即可写入，不要求自由监控分段可见。
            if (!_session.IsConnected || modbus == null || driver == null)
            {
                item.WriteError = "未连接，无法写入";
                return;
            }

            if (!MonitorItemSet.TryBuildWrite(item, text, _byteOrders, out WritePlan plan, out string error))
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

            // 输入框文本：变量行是变量名，元件行是 HD100 这样的元件名。
            // 文本已一致时不写，避免触发无谓的落盘
            if (row.FindName("NameBox") is TextBox nameBox && nameBox.Text != item.Name)
            {
                nameBox.Text = item.Name;
            }

            // 变量行：类型来自变量表，下拉框置灰只展示，不再按元件反推候选
            if (item.IsVariable)
            {
                ApplyVariableRowState(row, item);
                ApplyMonitorRowState(row, item);
                return;
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

        // ---------- IO 表导入（P2） ----------

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

    }
}
