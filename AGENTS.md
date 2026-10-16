# AGENTS.md — DSH_Modbus采集工具

基于 **WPF / .NET 8** 的 Modbus 数据采集上位机 Demo，界面参考 Windows 11 Fluent（自定义标题栏 + 左侧导航 + 卡片式内容）。**无第三方 NuGet 依赖。**

> 需求与优先级见 `Brief.md`；技术设计与决策理由见 `Design.md`；本文件只讲**代码在哪、怎么改**。

---

## 1. 构建与运行

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

- **构建前必须先结束进程**，否则 exe 被锁报 MSB3027
- 窗口最小 1024×640（`MainWindow.xaml` 的 `MinWidth/MinHeight`），默认与最小一致
- 用户习惯：**构建成功后自动启动应用**；改动完成后及时提交（中文提交信息）
- **版本号与构建日期在 `DSH_Modbus采集工具.csproj`**：`<VersionPrefix>` 手改（提版本时改这一处），
  `VersionSuffix` 由构建配置决定（Debug 自动带 `-debug`）；`<WriteBuildDate>` 目标每次编译现取
  系统时间写入 `obj/.../BuildInfo.g.cs`。详见 §7.2

## 2. 目录结构

```
DSH_Modbus采集工具/
├── App.xaml / App.xaml.cs      # 入口（OnStartup 里先 ThemeManager.Initialize）
├── AssemblyInfo.cs             # 仅供 ThemeInfo 特性等程序集级声明（无逻辑）
├── AppInfo.cs                  # 项目信息（P5-3）：版本号 / 构建日期 / 运行环境（只读程序集元数据）
├── MainWindow.xaml             # 全部 UI（单窗口，无 UserControl 拆分）
├── MainWindow.xaml.cs          # 交互逻辑（见 §4 分工）
├── MonitorItem.cs              # 自由监控项：DataPoint 的界面包装 + 集合（增删改/落盘/解析）
├── NumberInputDialog.cs        # STRING 长度输入弹窗（选 STRING 类型时用）
├── XlsxIoTableReader.cs        # xlsx 点位表解析（零依赖 zip+XML）
├── Themes/                     # 主题（P5-1）
│   ├── Light.xaml              # 浅色字典（50 键）；值 = 主题化前的字面值
│   ├── Dark.xaml               # 深色字典（同 50 键）
│   └── ThemeManager.cs         # 换字典 / 跟随系统 / 键一致性校验 / app-settings.json
├── Polling/                    # 采集引擎（不含 WPF 类型）
│   ├── ICollectorHost.cs       # 宿主契约：周期 / 监控集 / 可见性快照 / 连接身份 / 回 UI / 断线
│   ├── ModbusPoller.cs         # 轮询编排（IO 分块读 + 自由监控读 + 断线通知）
│   └── PollSnapshot.cs         # 可见性快照（页面 / 自由监控 / 当前页 / 周期）
├── Io/                         # IO 点位集（不含 WPF 类型，分页只做数值运算）
│   ├── IoPointSet.cs           # 注释表读写 + 名称归一化 / 地址换算 / 分组排序 / 导入校验
│   ├── IoPager.cs              # 分页纯计算（页数 / 夹取 / 切片 / 翻页 / 页号文本）
│   └── IoTableImporter.cs      # xlsx → 注释表 + 失败明细
├── Modeling/                   # 数据类型系统（P3-3 数据模型层）
│   ├── DataTypes.cs            # DataType / DisplayFormat 枚举 + DataTypeSpec 规格表
│   ├── ValueCodec.cs           # 原始寄存器 ↔ 类型值 编解码 + 显示格式化
│   └── DataPoint.cs            # 监控项模型 + data-points.json 持久化 + 驱动校验解析
├── Logging/                    # 日志落盘与文案规则（不含 WPF 类型）
│   ├── DeviceLogFileWriter.cs  # 按天追加纯文本 .log + 跨天换文件 + 失败自愈
│   └── DisconnectReasonClassifier.cs  # 异常原文 → 简短中文（底栏与日志共用）
├── Net/                        # 网卡枚举与选择记忆（不含 WPF 类型）
│   └── NicScanner.cs           # 网络接口 → NicInfo + 「上次选择」还原优先级
├── Ui/                         # 窗口呈现辅助（引 WPF，但不碰业务）
│   ├── MaximizedInset.cs       # 最大化溢出补偿：物理像素测量 + 按 DPI 内缩根元素
│   └── StatusBarPresenter.cs   # 底栏：状态文字 / 状态点颜色 / 右侧连接参数
└── Drivers/
    ├── CommDriver.cs           # 模型：ModbusArea / NumberBase / AddressArea / CommDriver（含字节顺序枚举）
    ├── DriverRegistry.cs       # 品牌地址数据表（含各品牌字节顺序约定）
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（MBAP + FC01/02/03/04 读 + FC05/06/16 写 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```

> `Polling/` 与 `Io/` 是 P5 结构重构的产物，**刻意不引用 WPF**：
> 采集编排与点位集判断可脱离界面阅读/验证；控件写入（分页控件显隐、绑列、弹窗）
> 一律留在 `MainWindow.xaml.cs`。新增逻辑时按同一原则归位。

> `ModelingCheck/`（仓库根，**不属于本工程**）是 P3-3 的独立验证工程：用真机实测数据对编解码做断言，不参与主程序构建。
> `LoggingCheck/`（同上）是 P5-2 的独立验证工程：链接 `DeviceLogFileWriter.cs` 与 `DisconnectReasonClassifier.cs` 源码，断言逐字符行格式 / 跨天换文件 / 续写 / 容错 / 断开原因归类，**44 项**，不参与主程序构建。

> `.tmp/` 下另有两个**一次性**回归断言工程（**未提交**，随用随弃）：`PollerCheck`（IO 轮询异常码策略）与 `NicCheck`（网卡选择还原优先级）。它们验的是"改一次就够"的行为，不值当长期维护；要重跑时会话记录里有完整源码。

## 3. 页面与 UI

单窗口两行布局：48px 标题栏 + 主体（导航 224 列 + 内容），底部另有一条**全局状态栏**（30px，三页都可见）。

```
Window
└ Grid x:Name="RootGrid"                         Rows: 48 / *
   ├ Border       Row=0                                  标题栏（48）
   └ Grid         Row=1                            主体  Columns: 224 / *
      ├ Border     Col=0     Margin="0,0,112,0"            左侧导航
      ├ ScrollViewer ColSpan=2 Margin="112,0,0,30"         内容区
      └ Border     ColSpan=2 Height=30 VerticalAlignment=Bottom  底栏
```

> 主体 Grid **没有行定义**：底栏靠 `VerticalAlignment="Bottom"` **叠在内容区之上**，内容区用**下边距 30** 给它让位。详见 §3.5。

### 3.1 导航

| 导航项 | 面板 | 说明 |
|---|---|---|
| `OverviewNav` 总览 | `OverviewPanel` | 底部运行日志卡（状态显示已移至全局底栏，见 §3.5） |
| `StatusInfoNav` 状态信息 | `StatusInfoPanel` | IO 监控（**三分段**：输入 X / 输出 Y / 自由监控）+ 分页 |
| `DeviceConfigNav` 设备配置 | `DeviceConfigPanel` | 连接表单 |
| `采集配置` | — | 占位项，不切面板 |
| `SettingsNav` 设置 | `SettingsPanel` | 显示外观（P5-1）+ 日志保存（P5-2）+ 项目信息（P5-3） |

切换逻辑：`NavRadioButton` 样式里的 `EventSetter Checked=Nav_Checked`，按 sender 显隐面板。

### 3.2 设计器预览

三个面板同处一个 `StackPanel`，运行时由 `Nav_Checked` 切换，而**设计器只认 `d:Visibility`**（`d:` 在 `mc:Ignorable="d"` 中，不影响编译与运行）。

- 当前 `OverviewPanel` = `d:Visible`（设计器默认预览总览页）
- 要预览其它页：目标页改 `d:Visible`，其余改回 `d:Collapsed`
- 总览页 `Height` 绑 `ContentScrollViewer.ViewportHeight`，设计器里常解析不出 → 调样式时临时注释该绑定

### 3.3 关键样式（Window.Resources）

| Key | 用途 |
|-----|------|
| `CaptionButton` / `CloseCaptionButton` | 标题栏按钮（关闭悬停红底） |
| `NavRadioButton` | 导航项（灰底背板，选中加粗） |
| `IoSegmentRadioButton` | IO 页 X/Y 分段切换项 |
| `ToggleSwitch` | **开关**（Windows 11 式 ToggleSwitch）：轨道 40×20、柄 12、内缩 4；关＝`Control.Background`、开＝`Accent.Fill`；**状态文字在轨道左侧**，由代码按 `IsChecked` 改写 `Content`（「开」/「关」） |
| `IoItemTemplate` | IO 点位单元格（灯 10×10、注释 MinWidth 144；`Comment` 为 INPC） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `IoPagerButton` | 分页箭头（26×26，首末页置灰） |
| `IoCardButton` | **通用文字按钮**（浅灰实底胶囊／圆角 6／高 26／字号 12；底色走 `Brush.Control.Background`） |
| `FormButton` | 设备配置页按钮，`BasedOn` `IoCardButton`，仅字号 14、内边距 14 |
| `FormComboBox` | 设备配置页下拉框：**胶囊**（灰底无边框圆角 6 + chevron），字号 14／高 26 |
| `FormComboItem` | 表单下拉项容器（含 `ItemContainerStyle`，勿漏） |
| `FormTextBox` | 设备配置页输入框：**框线**（白底 + `Brush.Input.Border` 圆角 4），字号 14／高 26 |
| `MonitorInlineComboBox` / `MonitorTypeCombo` | 自由监控行内下拉框：胶囊，字号 12／高 22 |
| `MonitorComboItem` | 行内下拉项容器 |
| `MonitorInlineTextBox` / `MonitorValueTextBox` | 自由监控行内输入框：框线，字号 12／高 22 |
| `LogClearButton` | 日志卡「清空」按钮 |
| `DeviceLogItemTemplate` | 日志行（时间戳灰 + 消息按级别着色） |
| `DeviceLogListStyle` | 日志列表（透明底/无边框/无选中底色） |
| `ScrollBar` | 细滚动条（10px） |

### 3.4 页面细节

- **总览**：顶部仅「总览」标题（原状态卡已移除，连接状态统一在全局底栏）。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**，另两页照滚。
- **状态信息**：见 §5 IO 监控 / §5.1 自由监控。
- **设备配置**：连接方式 / 协议 / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮；**设备通讯区域**另有左「字节顺序」3 个下拉框（32 位整数 / 32 位浮点 / 字符串）+ 竖分隔线 + 右「地址表元件」（见 §8.1）。连接中锁定表单（`SetInputsEnabled`）。串口模式仅隐藏以太网面板，**功能未实现**。

**目标网卡的选择会被记住**（`app-settings.json` → `TargetNic`），下次启动仍选同一块：

- 存的是**网卡名**（如 `WLAN`），不是下拉框显示文本 `WLAN(192.168.1.15)` —— 文本里嵌着 IP，而同一块网卡换个网络 IP 就变（笔记本的 WLAN 在家 `192.168.1.x`、在公司 `10.0.0.x`），按文本匹配**恰好在最需要它的场景下失效**
- 还原优先级：**本次扫描前的选择 → 上次运行记住的 → 第 0 项**
- ⚠️ **扫描过程必须整体罩在 `_restoringNicSelection` 里**：`Items.Clear()` 在原本有选中项时就会触发 `SelectionChanged`，只罩最后那次赋值挡不住。挡掉的意义不只是"别写盘"—— 若某块网卡临时消失（VPN 断开 / 网线拔掉），扫描会回退到第 0 项；把这次回退也写进偏好，用户原先的选择就被**悄悄抹掉**了。实测：预置一块不存在的网卡名，启动后下拉框回退到第 0 项，而 json 里仍是原值
- 为什么必须持久化：`ConnectButton_Click` 会按选中项 `new IPEndPoint(nic.Ip, 0)` **绑定本地网卡**。绑错网卡（如绑到 Tailscale 却去连 `192.168.1.100`）会立刻报 `WSAENETUNREACH`，界面上显示成「连接失败：… 网络不可达」—— 看着像网络故障，其实是**上次的选择没被记住**。自动重连不受影响（`ReconnectTarget` 已快照本地端点），**只有启动后的第一次连接会踩到**

### 3.5 全局底栏（连接状态）

30px 高，**三个页面都可见**（在面板之外，不随导航切换显隐）。

| 位置 | 控件 | 内容 |
|---|---|---|
| 左 | `StatusDot` + `StatusText` | 状态点 + 状态文字：`未连接` / `正在连接 …` / `已连接` / `通讯异常：<原因>` / `已断开：<原因>` / `正在重连 … Ns（第 M 次）`。**一律不带 ip:端口** —— 连接参数只由右侧 `StatusBarDetail` 显示；断开与失败的原因经 `ClassifyDisconnectReason` 归一，与日志同一份用词 |
| 右 | `StatusBarDetail` | 连接参数：`驱动：连接方式  协议  IP:端口` |

- **两个控件名沿用自旧设备配置表单**，故 `SetStatus()` 逻辑无需改动 —— 它写的就是这两个名字
- `SetStatus(text, colorHex)` 同时设 `StatusText.Text` / `.Foreground` / `StatusDot.Fill`，并顺带调 `UpdateOverviewStatus()`。**这几条规则都实现在 `Ui/StatusBarPresenter.cs`**：窗口侧 `SetStatus` / `UpdateOverviewStatus` 各只是一行薄包装（12 个调用点未动），右侧参数串由窗口的 `BuildConnectionDetail()` 提供（它要读表单控件），呈现器因此不认识任何表单控件
- ⚠️ **`_statusBar` 必须可空**：`UpdateOverviewStatus()` 会在 `InitializeComponent()` **期间**被 ComboBox 的 `SelectionChanged` 触发 —— 那时构造函数体还没执行、呈现器尚未创建。改成不可空会让应用**一启动就崩**（实测 `NullReferenceException`，堆栈落在 `InitializeComponent` 里）。这条只有真跑起来才看得见：构建与独立断言都不会报
- `UpdateOverviewStatus()` 只写 `StatusBarDetail`（表单参数变化时刷新）；栏内边距 16，**不跟随内容区的卡片缩进**（内容区左右留白见 §3.7）

**布局要点（改这块务必先读）**：

- 底栏在主体 Grid 内用 `VerticalAlignment="Bottom"` **叠放**，内容区靠 `Margin="112,0,0,30"` 让位；**没有新增行定义**
- `Margin="112,0,0,0"` 的左缩进 112 = 导航列的视觉右边界（导航是 `Margin="0,0,112,0"`，**右边距** 112）
- ⚠️ **不要把底栏改成窗口级第 3 行或给它加列定义** —— 前者会切走导航列的高度、后者会在底栏里切一刀导致左下角空缺（两种错法都试过）

### 3.6 最大化溢出补偿

`WindowChrome` 自绘标题栏时，系统把最大化窗口四边**各外扩一个边框宽度**（150% DPI 实测 11px），底边被推到任务栏之下，而 `WindowChrome` 下客户区覆盖整个窗口 → 底栏最下一条被遮住。

- 算法在 `Ui/MaximizedInset.cs`（`MaximizedInset.Apply(window, root)`）：按「窗口矩形 − 所在显示器工作区」算外扩量，给 `RootGrid` 四边各内缩该值；非最大化时清零。窗口侧 `UpdateMaximizedInset()` 只是一行薄包装
- 由 `Window_StateChanged` 调两次（切换瞬间 + `DispatcherPriority.Loaded` 后），因为状态刚变时窗口矩形可能还没落定
- 用**物理像素**测量再按 `VisualTreeHelper.GetDpi` 换算，不依赖 WPF 布局时序；多显示器用 `MonitorFromWindow`
- ⚠️ **拦 `WM_GETMINMAXINFO` 改 `ptMaxSize` 的方案无效**（已试过）：钩子确实被调用，但系统对带边框的最大化窗口仍按自己的规则外扩，写回值被忽略。**不要再走这条路**

### 3.7 内容区左右留白（改卡片宽度/对齐必读）

四个页面的标题与所有卡片都挂在**同一个** `ContentScrollViewer` 下（并列兄弟节点），
故左右留白由它一个元素决定，改一处即四页同步：

```xml
<ScrollViewer x:Name="ContentScrollViewer"
              Padding="20,4,20,16" Grid.ColumnSpan="2" Margin="112,0,0,30">
```

| 数 | 含义 | 基准 |
|---|---|---|
| `Margin.Left = 112` | 让开导航列 | 导航 `Border` 是 `Margin="0,0,112,0"`，其**右边距** 112 把竖分隔线推到 x=112 |
| `Padding.Left = 20` | 卡片/标题与**分隔线**的间距 | 分隔线 |
| `Padding.Right = 20` | 卡片与**窗口右缘**的间距 | 窗口右缘 |

- **左右 Padding 必须同值**，卡片才会在内容区里居中（左以分隔线为基准、右以窗口右缘为基准，两者都只是 Padding，同值即对称）。曾经左 20 / 右 36 是历史遗留的不对称，已统一为 20
- ⚠️ **`Margin` 与 `Padding` 在这里分工不同，别混**：`Margin` 管「让开导航」，`Padding` 管「卡片缩进」。分隔线位置只由 `Margin` 决定，改 `Padding` 不会动线
- ⚠️ 改 `Margin.Left` 时必须**同时**改导航 `Border` 的右边距（两个 112 是成对的，见 §3.5）；只改一个会让分隔线与内容起点脱节
- 卡片**外框**已与页面大标题左对齐；卡片**内容**再往内缩各自 `Padding.Left`（各卡不同：设置页三卡 24、设备配置 20、状态信息 12、总览日志卡无）。这是卡片内边距，不是错位

## 4. 代码分工（MainWindow.xaml.cs）

> `MainWindow.xaml.cs` 仍是本工程最大的单文件（两千行量级；P5 重构抽出两块后，又因
> P4-2 读取/写入与 P5 设置页陆续加回，规模回到重构前）。采集编排已抽到 `Polling/`、
> IO 点位集/分页/导入已抽到 `Io/`，主题在 `Themes/`；其余（连接生命周期 / 心跳 / 导航 /
> 自由监控 / 日志）仍在本文件。**自由监控刻意未抽** —— 它有 12 处直接碰控件，见 `Design.md` §8-1。
>
> ⚠️ **不要在这里写具体行数**：该数字已三次未跟上（曾同时出现 2590 / 2190 / 2187 三个
> 互相矛盾的值），它对判断没有作用，只会下次再骗人一次。

| 区块 | 关键成员 |
|---|---|
| 连接状态机 | `ConnectButton_Click` / `Disconnect()` / `Disconnect(status,color)` / `TryConnectAsync` / `RunConnectionTasksAsync` / `IsCurrentConnection` |
| 自动重连 | `StartReconnect` / `ReconnectLoopAsync` / `CancelReconnect` / `ReconnectTarget` / `ReconnectDelaysSeconds` |
| 轮询（入口在本类，实现在 `Polling/`） | `_poller.PollLoopAsync` → `Polling/ModbusPoller.cs`（`PollOnceAsync` 按地址区分块 + 2000 点切窗 / `PollFreeMonitorAsync` 自由监控，按区选 FC01/03/04） |
| `ICollectorHost` 实现 | `PollIntervalMs` / `MonitorSet` / `CaptureSnapshot()` / `IsCurrentConnection` / `RunOnUiAsync` / `NotifyConnectionLost` / `ClassifyDisconnectReason` / `ReportCommunicationWarning`（连续异常码告警，见 §5） |
| 心跳编排 | `HeartbeatLoopAsync`（失败时转 `_poller.NotifyConnectionLost`） |
| IO 点位集（实现在 `Io/`） | `RebuildIoPoints`（薄包装）→ `IoPointSet.Rebuild` / `TryCreatePoint` / `NormalizePointName` / `TryValidatePoint` |
| 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（算术在 `IoPager`；控件写入留本类） |
| 导入 | `ImportIoTableButton_Click` → `IoTableImporter.Read`（`MessageBox` 留本类） |
| **自由监控** | `AddMonitorItemButton_Click` / `RemoveMonitorItem_Click` / `MonitorName_TextChanged` / `MonitorName_LostFocus` / `SyncMonitorTypeWithElement` / `ApplyMonitorRowState` / `RefreshMonitorView` / `FocusMonitorRowName` / `RowOf` / `MonitorValue_GotFocus` / `MonitorValue_LostFocus` / `MonitorValue_KeyDown` / `CommitMonitorValueAsync` / `WriteToDeviceAsync` / `ClassifyWriteError`（见 §5.1） |
| 持久化 | `LoadComments` / `SaveComments`（薄包装 → `IoPointSet.Load` / `Save`） |
| 网卡 | `ScanNetworkAdapters`（闸门 + 填下拉框）/ `TargetNicComboBox_SelectionChanged`；扫描与还原规则在 `Net/NicScanner.cs`。选择持久化见 §3.4 |
| 日志 | `AppendDeviceLog`（顺带落盘 P5-2）/ `ClearDeviceLogButton_Click` / `ClassifyDisconnectReason` / `RefreshDeviceLogColors`（换主题时重建行色）/ `FileLevelOf`（界面级别 → 落盘级别） |
| 底栏 | `SetStatus` / `UpdateOverviewStatus` / `BuildConnectionDetail`（窗口侧薄包装）→ `Ui/StatusBarPresenter.cs`（规则本体）；`UpdateMaximizedInset` → `Ui/MaximizedInset.cs` |
| **主题（P5-1）** | `Themes/ThemeManager.cs`；窗口侧 `ThemeSegment_Checked` / `SyncThemeSegments` / `OnThemeChanged`（底栏的状态色映射在 `Ui/StatusBarPresenter.cs`）；设置页 `SettingsPanel` + `SettingsNav`（见 §9.2） |
| **日志落盘（P5-2）** | `Logging/DeviceLogFileWriter.cs`（按天追加 / 跨天换文件 / 失败自愈）；窗口侧 `_logWriter` / `LogSaveToggle_Changed` / `SyncLogSaveToggle` / `LogDirectoryTextBox_LostFocus` / `LogBrowseButton_Click` / `LogOpenFolderButton_Click` / `LogResetDirectoryButton_Click` / `ApplyLogDirectory` / `RefreshLogSaveStatus`（见 §7.1） |
| **项目信息（P5-3）** | `AppInfo.cs`（读程序集元数据）+ csproj 的 `VersionPrefix`/`VersionSuffix` 与 `WriteBuildDate` 目标；窗口侧 `RefreshProjectInfo`（见 §7.2） |
| 关键字段 | `_modbus` / `_isConnected` / `_pollCts` / `_connectCts` / `_windowClosed` / `_pollIntervalMs` / `_currentDriver` / `_poller` / `_ioSet`（内含 X/Y 点位列表与注释表）/ `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_monitorSet` / `_deviceLog` / `_logWriter` / `_statusText` / `_statusColorHex` / `_reconnectTarget` / `_reconnectCts` / `_reconnectAttempt`（**哪些是 `volatile` 以上表 §3.1 为准**：`_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` 共 7 个） |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控

- **点位集完全由导入的 IO 表驱动**（不再固定 64 点）；未导入则空列表 + `IoEmptyHint`「尚未导入 IO 表」
- 持久化唯一来源 = `io-comments.json`（`AppContext.BaseDirectory`，**键=点位名、值=注释**）；导入即整体替换并写盘
- 导入校验：**仅接受 X / Y 元件**，地址须落在当前驱动范围内；结果**一律用 `MessageBox`** 汇报（0 项有效则警告框且拒绝替换）
- **分页**：每页 4 列 × `IoPager.RowsPerColumn`(16) = 64 点，X/Y 各自独立页号；单页时整体隐藏。列宽 `*` 等分；行高 22、行距 2、列距 4
- **采集跟随可见性**：只读**当前显示分段 + 当前页**（`_visiblePage` 快照，按所属地址区分块 FC01，同区跨度超 2000 点切窗，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**；断线不清灯色（`IsOn` 保持上次值）
- **三个分段统一「跟随可见性」**（输入 X / 输出 Y / 自由监控）：只有当前显示的才读，离开者**保留上次值、不清空、不断线**。外层另有页面级闸门 `_ioPageActive`（离开状态信息页则全停）。详见 §5.1 与 `Design.md` §5.6
- **合法异常码不算断线**（2026-10-16）：IO 分块读**单独捕 `ModbusResponseException`** 并与成功路径分开 —— 跳过该块、其余块照读、该块的点保持上次值，**不断开也不重连**；某块**连续** `ModbusPoller.ConsecutiveExceptionThreshold`（**5**）次才记一条琥珀「通讯异常」（措辞 `原因（地址 xxx 异常码 N）`），**只记一次、读通即清零**（不这么做，100ms 一拍会让 200 条日志 20 秒内刷满）。**超时 / IO 异常仍照旧断开**。详见 §5.1 / §6 与 `Design.md` §8-12

**灯色（`IoPoint.StateBrush`）**：灭灯 `#C4C4C4`；点亮时**输入 X 绿 `#0B7A0B`、输出 Y 橙 `#FFA500`**。

- 由 `IoPoint.IsOutput`（`init` 属性）区分，创建时在 `IoPointSet.TryCreatePoint` 按点位名首字母定死：`IsOutput = normalized[0] == 'Y'`
- 画刷**按当前主题从 `Io.*` 键取**（上面三个值是浅色主题的值；深色下绿灯提亮为 `#4EC94E`、灭灯为 `#5A5A5A`），取不到时回退到 `InputOnFallback` / `OutputOnFallback` / `OffFallback` 三个 `static readonly` + `Freeze()` 的浅色画刷
- **不要在 `StateBrush` 里 new 画刷**（原实现每次取属性都新建对象）；字典本身有缓存，按键取不产生新对象
- 换主题后靠 `RaiseStateBrushChanged()` 触发绑定重算（`StateBrush` 无 setter）
- XAML 侧不用改：`IoItemTemplate` 的灯仍绑 `Background="{Binding StateBrush}"`

### 5.1 自由监控（P4-2，第三分段）

状态信息页第三个分段（`FreeViewSegment`），列表式监控项：**元件名称 / 数据类型 / 显示格式 / 当前值 / 删除**。

- **界面采用 Brief「方案 2」**：元件符号与编号**合并为一个输入框**（`NameBox`，如 `HD100`），不再是「符号下拉 + 编号框」
  - 取舍见 `Design.md` §7.1；存储仍是 `DataPoint.Element` + `Index` 两个字段（旧 `data-points.json` 无损）
  - 合并输入 → 拆分的**唯一实现**是 `CommDriver.TrySplitName`（`TryResolve` 与它共用同一份规则）
- **数据类型由元件反推**（方案 1 是反过来的）：`SyncMonitorTypeWithElement` 按 `CommDriver.AreasOfElement` 判断该符号属于线圈还是寄存器 → 线圈只给 `BOOL`，寄存器给 6 种；改元件导致类型不符时自动纠正
  - ⚠️ `AreasOfElement` **只看符号、不看编号**，故编号越界时仍能给出正确的类型候选与错误文案
- **输入校验**：`TrySplitName` 从严（编号必须 ASCII 数字，拒 `"D 100"` / `"D-1"` / 全角数字）
- **提交时机＝失焦**（不是打字时）：`MonitorName_TextChanged` **只做视觉标红**（`SetPreviewError`），**绝不写模型** —— 打字途中若 `SetName` 就落盘，下一拍轮询会拿半成品名称去采集，而磁盘上仍是旧值（内存/磁盘不一致，实测表现为从站读 `800` 而 json 还是 `100`）
  - `MonitorName_LostFocus` 是**唯一提交点**：拆分失败→静默回退到上次有效名称（不弹窗）；成功则解析，**若越界弹 `MessageBox`**（标题「元件名称无效」，正文含名称、原因与「请检查编号范围或重新选择设备类型。」），确认后同样回退且**不污染 json**
- **悬停提示已全部移除**：整行 `ToolTipText`、`NameBox` 的 `NameToolTip`、`ValueToolTip` **三者恒为 `null`** —— 监控表格是常驻界面，鼠标划过是高频动作，悬停弹提示纯属干扰。错误改由**颜色**表达（见下），越界改由**弹窗**表达。属性保留是为了将若要恢复只改一处、不动 XAML
- **「当前值」初值为空字符串**（不是 `—`）：输入框里放破折号既不是有效值、还得先删掉才能输，而「空」本身已表达"还没读到"
- 持久化 `data-points.json`（`DataPointStore`），增删改即时落盘；`_monitorSet`（`MonitorItemSet`）负责增删改与 `AfterChange` 重解析
- **读取已实现**（跟随可见性，见 §5 与 `Design.md` §5.6）：仅在**本分段可见**时读，`BuildReadPlan` 按 `ModbusArea` 分组 + 地址连续性分块（单块 ≤2000），按区选 FC01/FC03/FC04，经 `ValueCodec.FormatRegisters` 按各行类型/格式/字节顺序格式化后写入 `CurrentValue`
  - ⚠️ **必须按区分组**：信捷 `D0`（保持寄存器）与 `M0`（线圈）**绝对地址都是 0**，只按地址归并会把两者塞进同一次请求 → 从站回异常码
  - **单块读失败不触发断线**，只把原因写到该块的各项上（与 IO 轮询**同为不断开**，差异只在呈现：这里逐项标错，IO 只记一条日志）；一个配错的地址不该拖垮整条连接
  - **读失败（`ReadError`，琥珀 `#B26A00`）与配置错误（`ResolveError`，红 `#C42B1C`）是两个独立通道** —— 红＝去改配置，琥珀＝配置没问题、是通讯侧的事
  - 切换竞态：`_freeViewActive` 由 UI 线程改、轮询在后台读，故在**每块发送前**与**回填前**各复查一次可见性
- **写入已实现**（FC05 / FC06 / FC16）：**「当前值」格直接是输入框**，回车或失焦提交
  - 编码链：`MonitorItemSet.TryBuildWrite` → `ValueCodec.TryEncodeRegisters`（按类型与显示格式解析文本）→ 按值长度选功能码（线圈 FC05 / 单寄存器 FC06 / 多寄存器 FC16）
  - **不需要自由监控分段可见**（与读取不同）：建立连接后即可写。这么定是因为写入是用户的**主动即时操作**，而读取是后台轮询 —— 要求"必须先看着它"反而妨碍操作
  - **正在编辑时不回填**（`MonitorItem.IsEditing` + `ApplyReadValue`）：否则每拍轮询会把用户输入到一半的内容冲掉
  - **提交失败保持编辑态**：值留在界面上、原因留在 ToolTip，用户可继续修改。若提交后立刻解除编辑态，下一拍回填会把他输的内容擦掉，附带的错误提示还没看清就没了
  - **回车与失焦是两条路径**，靠 `_lastCommittedValue`（键=控件）去重，同一次编辑只发一次报文；记录在 **`GotFocus` 时清**（新一轮编辑）—— 放在"写入成功后清"会导致紧随的失焦路径重复发一次
  - ⚠️ `EncodeString` 按**文本长度**自算寄存器数，声明 `STRING[9]` 输入 `"abc"` 只得 2 个寄存器 → 必须用 `ValueCodec.EncodeStringFixed` 补零/截断到 `RegistersForLength`
  - ⚠️ **范围校验必须在截断前做**：`-32769` 先截成 `0x7FFF` 就会变成"合法的 32767"。十进制在解析时校验，HEX/BIN 在 `CheckIntegerRange` 里比对"截断前后是否相等"
  - 写失败（`WriteError`）与读失败**共用琥珀色**（对用户都是"通讯侧的事"），ToolTip 文字区分读写
  - 只读区（输入寄存器 / 离散输入）**不预先拦截**，由从站回异常码 2 → 提示"该地址不可写"。**注意：当前两个驱动的地址表里都没有这两类区**，故实际走不到这条路径，代码是防御性的
- ⏳ **未实现**：**串口（Modbus RTU）** —— 设备配置页的串口模式只隐藏以太网面板，功能未做

## 6. 心跳与连接生命周期

- `ModbusHeartbeat.RunAsync` 每 100ms 检查；距上次有效响应满 **1s** 时以 FC01 读**单点线圈**，只读不写
- **心跳地址 = 驱动地址表里「地址最小」的线圈区 `StartAddress`**（信捷 M 区 / 汇川 M 区均为 **0**）。按 `Area` 筛选后 `OrderBy(StartAddress)`，不依赖数组顺序；无需任何配置，与是否导入 IO 表无关
- 地址表里没有线圈区时提示「未启用心跳」并 return（用 `FirstOrDefault` 而非 `First`，否则抛出的异常会被上层当成通讯故障并断开连接）。正常驱动不会出现
- 合法异常响应抛 `ModbusResponseException` → **保持连接**并告警（码 1/2 提示配置无效，其余提示响应异常），后续心跳成功即清除。**IO 轮询自 2026-10-16 起同样不断开**（见 §5）；两条支路的差别只剩阈值与呈现：心跳每次异常即告警并去重，IO 累计**连续 5 次**才记一条
- 读取超时 2s；空闲失联通常**约 3 秒**检测到（1s 空闲 + 2s 超时 + 调度延迟），不是严格 3s 上限
- `RunConnectionTasksAsync` 启动时把**连接与驱动快照**传给两个任务，所有回 UI 回调先过 `IsCurrentConnection`（未关窗 && token 未取消 && 仍连着 && `_modbus` 是同一实例）→ 旧连接的后台回调无法误操作新连接
- `StopPolling` 只取消并置 null，**释放交给任务自身 `finally`**（确认自己仍是当前连接后）；`OnClosed` 统一取消连接尝试、停任务并释放

### 6.1 自动重连（P1-1）

| 项 | 行为 |
|---|---|
| 触发 | **仅「异常断开」**（读超时 / 对端关闭 / 帧校验失败等）。**用户主动断开、首次连接失败均不触发** |
| 退避 | `ReconnectDelaysSeconds = { 1, 2, 4, 8, 10 }`，封顶 10s；连接成功清零 `_reconnectAttempt` |
| UI | 重连期间**表单保持锁定**（参数已快照，改了不生效，避免误导）；按钮变「取消重连」；底栏逐秒显示 `正在重连 … Ns（第 M 次）`（**不带 ip:端口**，目标参数在右侧 `StatusBarDetail`） |
| 日志 | **重连尝试不写日志**（日志只记 5 种设备状态，ADR-14）；连上后记一条常规「已连接：ip:端口」 |

**⚠️ 两种 `Disconnect` 重载语义不同，别用错**：

- `Disconnect()` 无参 → **清空 `_reconnectTarget`** = 「用户主动断开」语义
- `Disconnect(status, color)` 带参 → **保留目标** = 异常断开，之后才能重连

故 `NotifyConnectionLost` 必须用**带参**版本，再紧跟 `StartReconnect()`。用无参会静默失去重连能力。

- `TryConnectAsync(target, isReconnect, token)` 被首次连接与重连**共用**；失败时的日志只在 `!isReconnect` 时写，避免重连刷屏
- `ReconnectTarget` 是 IP/端口/驱动/本地端点的**快照**，重连不依赖表单当前值
- `OnClosed` 顺序：`_windowClosed = true` → `CancelReconnect()` → `Disconnect()`，关窗不残留重连任务

## 7. 运行日志

总览页底部日志卡，只记**设备状态**，5 种规则：

| 规则 | 文案 | 颜色 |
|---|---|---|
| 已连接 | `已连接：ip:端口` | 绿 `#0B7A0B` |
| 已断开 | `已断开：<原因>`（用户主动断开与异常断开同式） | 红 `#C42B1C` |
| 连接失败 | `连接失败：ip:端口 <原因>` | 红 |
| 通讯异常 | `通讯异常：<原因>`（**仅当连接仍在**，对端回合法异常码） | 琥珀 `#B26A00` |
| 通讯恢复 | `通讯恢复` | 绿 |

- 格式 `[yyyy-MM-dd HH:mm:ss] 消息`；**空日志不显示占位文字**；**不记**关窗
- 仅内存保留最近 `MaxDeviceLogEntries`(200) 条，超出丢最旧、不落盘，可一键清空
- **边界**：「完全无响应」不产生「通讯异常」—— 由 `NotifyConnectionLost` 记红色「已断开」，**一次断线只有一条日志**
- 原因经 `ClassifyDisconnectReason` 归一为简短中文（Socket 原文 →「网络不可达」，Modbus 帧错误 →「响应帧校验失败」），未命中原样保留
- 日志区套一层内嵌框线 `Border`（`#C8C8C8` 1px、圆角 4）；时间列用 `SharedSizeGroup="DeviceLogTime"` 跨行共享宽度（**否则比例字宽会让消息左边界抖动**），`DeviceLogList` 上须有 `Grid.IsSharedSizeScope="True"`

### 7.1 日志落盘（P5-2）

同一条日志**既进界面又进文件**，落盘挂在 `AppendDeviceLog` —— 那是 5 种设备状态的唯一出口，7 个调用点零改动。

```
[2026-10-15 11:41:45] [INFO] 已连接：192.168.1.66:502
[2026-10-15 11:41:58] [ERROR] 已断开：用户主动断开
```

| 项 | 行为 |
|---|---|
| 默认目录 | `AppContext.BaseDirectory\Log`（exe 旁） |
| 文件名 | 日期，如 `2026-10-15.log`（**一天一个**，跨天自动换） |
| 格式 | 纯文本 UTF-8 无 BOM，每行 `[yyyy-MM-dd HH:mm:ss] [级别] 消息` |
| 级别 | **按颜色语义**（见 `FileLevelOf`）：绿 → `INFO`、琥珀 → `WARN`、红 → `ERROR` |
| 默认状态 | **启用**（用户定稿） |
| 保留 | **不自动清理**，一直追加 |
| 设置项 | `app-settings.json` → `LogSaveEnabled` / `LogDirectory`（空串 = 默认目录） |
| 失败提示 | 设置页一行 `LogSaveStatusText`：**正常时 `Collapsed`，仅写入失败时显示琥珀色原因** |

**几条容易踩的**：

- ⚠️ **时间戳必须共用**：`AppendDeviceLog` 里 `var now = DateTime.Now` 只取一次，界面与文件用同一个值。各取一次会在跨秒时让两边差一秒，事后对照对不上
- ⚠️ **跨天判断取「写入那一刻」的日期**，不是进程启动日 —— 应用常驻跨零点必须能正确切分。日期**回退也要换文件**（不能"只认更大日期"）
- ⚠️ **`FileShare.ReadWrite`**：文件句柄在应用运行期间一直持有，用户要用记事本 / VS Code / `tail` 打开当前日志。只给 `Read` 时这些工具会报"文件被占用"（实测验到过）
- ⚠️ **所有 IO 异常一律吞掉**：磁盘满 / 目录只读 / 文件被占都不能让应用崩，失败原因留在 `LastError` 由设置页显示，**下一次写入自动重试打开**（自愈）
- ⚠️ **失败提示是"只在失败时出现"的一行**（`LogSaveStatusText`）：正常态 `Collapsed`，不占位、不留空隙。日志悄悄写不进去是最危险的情形 —— 应用照跑，人以为有记录，事后复盘才发现全是空的，所以这条通道**不能**跟着界面上其它文字一起去掉。它挂 `AppendDeviceLog`（面板可见时才刷）与 `Nav_Checked`（切进设置页补刷）两处，自愈后下次刷新自动隐藏
- **开关是 `ToggleSwitch`（非分段控件）**：与「日志保存」标题同一行、靠右；状态文字「开 / 关」在轨道左侧。⚠️ **文字不能用 `ControlTemplate` 里的 `Setter Property="Content"` 来切** —— 那是模板优先级，赢不过控件上的局部值 `Content="开"`，写了等于没写（实测两种状态都显示「开」）。文案在 `LogSaveToggle_Changed` / `SyncLogSaveToggle` 里由代码改
- 设置页**不再回显**「当前文件 + 本日已写 N 行」与「已关闭」文案（用户定为噪声）：开关状态由开关本身表达，过程信息不必常驻。故 `DeviceLogFileWriter.CurrentFilePath` 已删除，`LinesWritten` 保留但仅供 `LoggingCheck` 断言用
- 写完立即 `Flush`：崩溃后复盘才看得到最后几条，而那几条往往正是崩溃原因
- 设置页路径改动**失焦才落盘**，且**先 `Directory.CreateDirectory` 再保存** —— 非法路径当场报错并回退，不拖到第一条日志写不进去才暴露
- 开关/路径改动**立即生效**（`SetEnabled` / `SetDirectory`），关掉或换目录时释放旧句柄（否则用户没法移动文件夹）
- 独立验证工程 `LoggingCheck`（**44 项**断言）覆盖行格式 / 跨天 / 续写 / 换目录 / 开关 / 容错 / **断开原因归类**，改动写入器或归类规则后跑它
- ⚠️ **在 DSH 下跑它时先改 TEMP**：`%TEMP%` 上挂着沙箱 ACE，子进程（`dotnet run`）建目录会被拒（`UnauthorizedAccessException`），而 pwsh 自己建得出来，故报错看着莫名其妙。先 `$env:TEMP = "$PWD\.tmp\lc-temp"` 再跑

### 7.2 项目信息（P5-3）

设置页第三张卡，显示 **名称 / 版本 / 构建日期 / 运行环境**。全是常量，在 `Window_Loaded` 里 `RefreshProjectInfo()` 填一次。

| 项 | 来源 |
|---|---|
| 名称 | csproj `<Product>` → `AssemblyProductAttribute` |
| 版本 | csproj `<VersionPrefix>` + `VersionSuffix` → `AssemblyInformationalVersion`（**显示时砍掉 `+` 之后的构建元数据**，前缀 `V` 由界面加） |
| 构建日期 | csproj `WriteBuildDate` 目标生成的 `BuildInfo.BuildDate` 常量 |
| 运行环境 | `Environment.Version` + `Environment.OSVersion` + `Environment.Is64BitProcess` |

**几条容易踩的**：

- ⚠️ **版本号手写在 csproj，不自动递增**：自动递增会把「版本号」变成构建次数的函数，而现场排障要的是「这个 exe 是哪一版」，不是「编译过几次」。提版本由人决定（改 `<VersionPrefix>` 一处）
- ⚠️ **`-debug` 后缀由构建配置推导，不要硬编码**：`VersionSuffix` 只在 `Configuration == Debug` 时设 `debug`，SDK 自动合成 `0.6.0-debug`。硬编码意味着每次切 Release 都得记得删掉，忘一次就会把调试版当正式版发出去（与「构建日期不手写」是同一条道理：能推导的就别手写）。实测 Debug 下 `ProductVersion = 0.6.0-debug+<sha>`，Release 下为干净的 `0.6.0`
- ⚠️ **`AssemblyVersion` / `FileVersion` 固定不带后缀**（仍是 `0.6.0.0`）：程序集身份必须稳定，否则换一次配置就让引用方全部重编 —— 与「日期用独立目标而不覆盖 `<Version>`」同一个考虑
- ⚠️ **构建日期必须编译期生成**：手写必然忘记更新；用**文件时间**也不行 —— 复制/解压/杀软扫描都会改动它，与"代码什么时候编的"无关
- ⚠️ **日期用独立目标 + 独立常量，不要覆盖 `<Version>`**：把日期塞进 `Version` 会让程序集版本每次编译都变，进而让增量构建失效（引用方全部重编）
- ⚠️ **`InformationalVersion` 会被 SDK 自动追加 `+<git sha>`**（实测 `0.6.0-debug+945b5d9...`），显示前必须砍掉 `+` 之后的部分，否则用户看到一长串哈希
- 生成的 `BuildInfo.g.cs` 落在 `obj/` 下，**不要手改、也不要提交**（已在 `.gitignore` 覆盖范围内）
- 四行标签用 `SharedSizeGroup="InfoLabel"` 对齐；**末行不留底边距**（否则卡片底部多出一段空白）

## 8. 通讯驱动标准

新增品牌 = 在 `DriverRegistry.Drivers` 追加一个 `CommDriver`：

```csharp
new CommDriver
{
    Key = "brand_key",
    DisplayName = "显示名",          // 与 UI「设备类型」下拉项对应
    Areas = new[]
    {
        new AddressArea
        {
            Element = "M",                        // 元件符号
            Area = ModbusArea.Coil,               // Coil / DiscreteInput / HoldingRegister / InputRegister
            StartIndex = 0, Count = 8000,         // 元件编号范围
            StartAddress = 0,                     // 对应 Modbus 十进制绝对地址
            IndexBase = NumberBase.Decimal,       // X/Y 用 Octal
        },
    },
}
```

```csharp
var d = DriverRegistry.GetByKey("xinje_xd")!;
d.TryResolve("X17", ModbusArea.Coil, out int addr, out string err); // 八进制 X17 → 20495
```

- 已注册：`xinje_xd`（X/Y/M/S/SM/HM + D/SD/HD/HSD/SFD；**X/Y 各 4 段**：本体 / 扩展 #1–#16 / BD / ED）、`inovance_easy`（M/B/S/X/Y + D/R）
- **同一元件可有多段地址区**（信捷 X/Y 即如此）：`TryResolve(string,…)` 遍历**全部**匹配区，任一段命中即成功，全部越界才报「超出驱动定义范围」
- `StartIndex` 用**十进制**值表达八进制编号（X10000₈ → `4096`）；`Count` 为段内点数；`StartAddress` 为该段起始 Modbus 十进制地址

### 8.1 字节顺序（数据解释方式）

`CommDriver` 上有三个**品牌约定**属性，设备配置页各对应一个下拉框：

| 属性 | 枚举 | 信捷 XD 实测值 |
|---|---|---|
| `Int32ByteOrder` | `ByteOrder32`（`1234`/`2143`/`3412`/`4321`） | `Order3412` |
| `FloatByteOrder` | 同上 | `Order3412` |
| `StringByteOrder` | `StringByteOrder`（`12`/`21`） | `Order12` |

- 记号含义见 `CommDriver.cs` 的枚举注释：把 4 个字节**按重要性**编号 1=MSB…4=LSB，名称即字节流里的排列顺序
- **信捷的值是真机定案**（`Design.md` §10.3 / §10.4）；**汇川未实测**，暂用大端 `1234` 并已在代码注释标红
- 切换设备类型时下拉框按驱动的值重置默认（`ByteOrderComboBox_SelectionChanged`）；这三个值是 `ValueCodec` 各方法的入参，**编解码器不读全局状态**
- ⚠️ 字符串**不涉及「字序」**：它只是字节序列，没有字的权重之分，别把 `3412` 套到 `STRING` 上

## 9. 代码约定

- 命名空间 `DSH_Modbus采集工具`（含中文，路径亦含中文，命令行注意编码/引号）
- 事件处理命名：`Xxx_Click` / `Nav_Checked` / `Xxx_SelectionChanged`
- 颜色：**一律用 §9.2 的主题键**，XAML 里不得写 `#RRGGBB`。语义：主色／背景／顶栏／卡片等在 `Themes/Light.xaml` 里逐项对应
- 字体：`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`；标题用 `Segoe UI Variable Display`；图标 `Segoe Fluent Icons, Segoe MDL2 Assets`（资源键 `IconFont`）
- 表单：标签 `Width="80"` + 控件 `Width="200"`、行距 16；`SyncInputHeights` 在 Loaded 与面板显示时同步控件高

### 9.1 控件风格约定（新增控件必读）

**全应用统一一套视觉语言，新增任何输入框 / 下拉框 / 按钮都必须套用现成样式，不要手写外观。**

| 控件 | 用哪个样式 | 外观 |
|---|---|---|
| 下拉框 | `FormComboBox`（表单）／ `MonitorInlineComboBox`（行内） | **胶囊**：灰底 `#EDEDED`、无边框、圆角 6、右侧 chevron |
| 输入框 | `FormTextBox`（表单）／ `MonitorInlineTextBox`、`MonitorValueTextBox`（行内） | **框线**：白底、`#C8C8C8` 1px 边框、圆角 4 |
| 按钮 | `FormButton`（表单）／ `IoCardButton`（其它） | **灰底胶囊** `#EDEDED`、圆角 6 |
| 开关（二值偏好，如「启用 / 不启用」） | `ToggleSwitch` | **Windows 11 ToggleSwitch**：状态文字在左、轨道在右；关＝灰轨道柄靠左，开＝主色轨道柄靠右 |

**什么时候用开关、什么时候用分段控件**：

- **只有两档、且是「开 / 关」语义** → `ToggleSwitch`（如日志保存）。轨道变灰已经说明了「关」，再摆一个「关闭」档是多余的一层信息
- **两档以上（浅色 / 深色 / 跟随系统），或各档是并列的「模式」而非「开关」** → `IoSegmentRadioButton` 分段控件

尺寸只有两档，**按所在场景选**，不要另立数值：

| 场景 | 字号 | 高 | 例 |
|---|---|---|---|
| 表单 | 14 | 26 | 设备配置页 |
| 行内 / 卡片 | 12 | 22 | 自由监控表格行 |

**几条容易踩的**：

- **下拉框必须同时给 `ItemContainerStyle`**（已写进 `FormComboBox` 样式里，别在控件上覆盖）—— 漏了展开项会用系统默认蓝条，与胶囊风格明显不搭
- ⚠️ **不要给 ComboBox 设 `DisplayMemberPath`**：它与自定义模板里的 `SelectionBoxItemTemplate` 不合成，闭合态会显示成类型名
- `ComboBoxItem` 模板里的 `ContentPresenter` **必须写 `ContentSource="Content"`**，否则条目退化成对象的 `ToString()`
- **下拉项的左边距 = `ComboBox.Padding` − 2**（因为 item 的 `Border` 还有 `Margin="2,1"`）：改了一边必须同步另一边，否则展开时文字比闭合态偏右、切换选项会横向跳一下
- 颜色的三个语义出口：**红 `#C42B1C`＝配置要改**、**琥珀 `#B26A00`＝通讯侧的事**、主色 `#0067C0`＝聚焦
- XML 注释里**不能出现连续两个减号**（Markdown 表格分隔行会直接编译失败 `MC3000`）

- `IO模板.xlsx` 仅**导入功能测试用**，其注释列与 `io-comments.json` 都是占位文本（`CommentX0`/`Comment0`），**无业务含义** —— 不必维护、不必恢复，也不是待处理问题

### 9.2 主题与配色（P5-1，改颜色必读）

**XAML 里不得再出现颜色字面量**（`#RRGGBB`）。所有颜色都写成 `{DynamicResource Brush.Xxx}`，值取自 `Themes/Light.xaml` / `Dark.xaml`。

- **必须是 `DynamicResource`，不能是 `StaticResource`**：后者在加载时解析一次，运行期换主题不会跟着变
- 两个字典的键**必须完全一致**。`ThemeManager.ValidateKeyParity()` 在启动时校验，缺键直接抛异常（缺键的后果是控件静默变黑/透明，只在切到某个主题时才暴露，极难查）
- **浅色字典的值 = 主题化前的字面值，逐个搬入、一个没改**（149 处替换已脚本校验 1:1）。改浅色值 = 改既有外观，须确认是有意为之
- 键按**角色**命名（`Brush.Input.Border`、`Brush.Text.Muted`），不按色值。新增控件先判断「这是哪一类」再挑键

**C# 侧直接赋画刷的地方也必须随主题变**（这些不经过 `DynamicResource`）：

| 位置 | 做法 |
|---|---|
| `IoPoint.StateBrush`（灯色） | 每次取值都 `ThemeManager.GetBrush("Io.*")`；换主题后由 `OnThemeChanged` 调 `RaiseStateBrushChanged()` 通知绑定重算 |
| `LogEntry.LevelBrush`（日志行色） | 构造时按主题取；换主题后 `RefreshDeviceLogColors()` 重建整表（保留原 `DateTime`，不重新 `Parse` 文本） |
| `SetStatus(text, colorHex)`（底栏） | 实现在 `Ui/StatusBarPresenter.cs`：入参仍是**浅色字面值**，由内部 `BrushKey()` 映射到语义键；它记住上次的文字与颜色，换主题后 `ReapplyTheme()` 重设一次 |

**几条容易踩的**：

- ⚠️ **落盘的是整个对象，归一化必须放在 `Save()` 里**：`app-settings.json` 里主题 / 日志 / 网卡同处一个对象，每次保存都写整个对象。所以「日志目录等于默认值时归回空串」由 `Save()` 统一做，**不要**写进各个 Setter —— 曾经只在 `SetMode` 里调，于是任何新增的整对象保存路径（如 `SetTargetNic`）都会把「看一眼路径」留下的绝对默认路径写进文件，exe 一移动日志就写不回新位置的 `Log\`
- ⚠️ **半透明叠加必须换色**：`Brush.Overlay.*` 浅色是黑（`#0A000000`）、深色是白（`#14FFFFFF`）。黑叠加压在深底上等于没效果
- ⚠️ **`*.Fill` 三个实底色深浅同值**（主色 `#0067C0`、危险 `#C42B1C` 系），因为它们上面压白字，改了会掉对比度
- ⚠️ **IO 灯色深浅值不同**：浅色用深绿 `#0B7A0B`，深色必须提亮到 `#4EC94E`，否则与深底融在一起、看不出「亮/灭」
- ⚠️ **禁用态灰有 3 档**（`Brush.Text.Disabled` = `#B0B0B0` 下拉框 / `.Soft` = `#C0C0C0` 分页箭头与文字按钮 / `.Faint` = `#C8C8C8` 图标按钮）。主题化前就是 3 个不同字面值，**合并成一档会被看出差别**
- 主题切换是**整体换字典**（先加新字典再删旧的，避免中间态取不到键），不遍历可视树
- 「跟随系统」读注册表 `AppsUseLightTheme` 并挂 `SystemEvents.UserPreferenceChanged`；**显式选了浅/深就不再跟随系统**
- 选择持久化在 `app-settings.json`（与 `io-comments.json` 同级），字段缺失或损坏都回退到「跟随系统」

## 10. 任务速查

| 需求 | 位置 |
|------|------|
| **加输入框 / 下拉框 / 按钮（新增控件必读）** | 套用现成样式，别手写外观 —— 见 **§9.1 控件风格约定** |
| **改任何颜色（必读）** | 用主题键，**不要写字面量** —— 见 **§9.2 主题与配色** |
| **加/改主题、深色适配、跟随系统** | `Themes/`（`Light.xaml` / `Dark.xaml` 同键）+ `Themes/ThemeManager.cs`；C# 侧画刷见 §9.2 |
| **设置页（P5）** | `SettingsPanel`（`MainWindow.xaml`）+ `SettingsNav` + `Nav_Checked`；主题分段 `ThemeSegment_Checked`；日志开关 `LogSaveToggle_Changed`、路径 `LogDirectoryTextBox_LostFocus`；项目信息 `RefreshProjectInfo`（见 §7.2） |
| 改导航项/页面显隐 | `MainWindow.xaml` 导航区 + `Nav_Checked` |
| 加通讯品牌 | `Drivers/DriverRegistry.cs`（§8） |
| 加设备配置字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | 窗口 `BuildConnectionDetail()` 拼串 → `Ui/StatusBarPresenter.cs` 写 `StatusBarDetail` |
| 底栏状态文字/颜色 | `Ui/StatusBarPresenter.cs`（`Set` / `RefreshDetail` / `ReapplyTheme`）；窗口侧 `SetStatus` / `UpdateOverviewStatus` 只是薄包装 |
| 底栏布局/对齐 | `MainWindow.xaml` 主体 Grid 末尾的底栏 `Border`（见 §3.5，**改前必读**） |
| 最大化底栏被任务栏遮住 | `Ui/MaximizedInset.cs`（`Apply`）+ 窗口侧 `UpdateMaximizedInset` / `Window_StateChanged`（见 §3.6） |
| 自动重连退避/触发/取消 | `ReconnectDelaysSeconds` / `StartReconnect` / `CancelReconnect`（见 §6.1） |
| IO 灯色（输入绿/输出橙） | `IoPoint.StateBrush` + `InputOnBrush` / `OutputOnBrush` / `OffBrush`；`IsOutput` 在 `TryCreatePoint` 定 |
| IO 点位集构建/排序 | `Io/IoPointSet.cs`（`Rebuild` / `TryCreatePoint` / `NormalizePointName`，按地址升序）；窗口侧薄包装 `RebuildIoPoints` |
| 导入 IO 表 / 解析规则 | `Io/IoTableImporter.cs` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格）；弹窗在 `ImportIoTableButton_Click` |
| 注释与点位集持久化 | `Io/IoPointSet.cs` 的 `Load` / `Save` → `io-comments.json`（窗口侧 `LoadComments` / `SaveComments` 为薄包装） |
| IO 分页 | `Io/IoPager.cs`（算术）+ `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（控件写入）；`_xPageIndex`/`_yPageIndex` 独立 |
| 改每页点位数 | `IoPager.RowsPerColumn` / `IoPager.ColumnCount`（PageSize 由其相乘得出） |
| IO 采集范围 | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| **自由监控读取（分块规划）** | `MonitorItemSet.BuildReadPlan`（按 `ModbusArea` 分组 + 地址连续性分块）+ `MonitorItemSet.ApplyBlock`（偏移切片 + 格式化回填） |
| **自由监控轮询入口** | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` 开头按 `_freeViewActive` 分流 → `PollFreeMonitorAsync`（按区选 FC01/FC03/FC04，单块失败不断线） |
| **采集跟随可见性（三分段统一）** | `_ioPageActive`（页面级）+ `_freeViewActive` / `_yViewActive` → `PollOnceAsync` 开头；详见 `Design.md` §5.6 |
| **自由监控写入（FC05/06/16）** | `MonitorItemSet.TryBuildWrite`（文本 → `WritePlan`）+ `ValueCodec.TryEncodeRegisters` / `EncodeStringFixed`；`MainWindow.xaml.cs` 的 `CommitMonitorValueAsync` → `WriteToDeviceAsync`；报文层 `ModbusTcpClient.WriteSingleCoilAsync` / `WriteSingleRegisterAsync` / `WriteMultipleRegistersAsync` |
| **写入的文本解析与范围校验** | `ValueCodec.TryParseIntegerLiteral`（DEC 支持 `0x`/`0b` 前缀与负号）+ `CheckIntegerRange`；**校验必须在位宽截断前**（`-32769` 截成 `0x7FFF` 会变成合法值） |
| 改 IO 行高/行距、列宽 | `IoItemTemplate` 的 `Height`/`Margin`；`IoColumnsGrid`/`YIoColumnsGrid`（4×`*`） |
| 加/改 X-Y 分段切换 | `IoSegmentRadioButton` + `XViewSegment`/`YViewSegment` + `IoView_Checked`（自由监控 = 第三个 `FreeViewSegment`） |
| **自由监控增删改/校验** | `MonitorItem.cs`（`MonitorItem` / `MonitorItemSet`）+ `MainWindow.xaml.cs` 的 `Monitor*` 处理器（见 §5.1） |
| **元件名称输入 → 符号+编号拆分** | `CommDriver.TrySplitName`（`TryResolve` 与自由监控共用） |
| **数据类型候选（线圈/寄存器）** | `CommDriver.AreasOfElement` + `DataTypes.CoilTypes` / `RegisterTypes` |
| **监控项持久化** | `DataPointStore`（`data-points.json`）；模型 `Modeling/DataPoint.cs` |
| **字节顺序（字序/字符串序）** | `CommDriver.Int32ByteOrder` / `FloatByteOrder` / `StringByteOrder` + 设备配置页三个下拉框（见 §8.1） |
| **类型值编解码与显示格式化** | `Modeling/ValueCodec.cs`（入参传字节顺序，不读全局状态） |
| Modbus 报文/超时 | `Drivers/ModbusTcpClient.cs` |
| 心跳周期/空闲判断/地址来源 | `Drivers/ModbusHeartbeat.cs` → `RunAsync`；`ModbusTcpClient.HeartbeatIfIdleAsync` |
| 轮询与断线逻辑 | `Polling/ModbusPoller.cs` 的 `PollLoopAsync` / `PollOnceAsync` + `Disconnect(status,color)` |
| **IO 合法异常码不再断开（连续 5 次才记日志）** | `ModbusPoller.PollOnceAsync` 的 `catch (ModbusResponseException)` + `ConsecutiveExceptionThreshold` + `_blockExceptionStreaks`；告警通道 `ICollectorHost.ReportCommunicationWarning` → 窗口 `ReportCommunicationWarning`（见 §5，证据 `Design.md` §10.2） |
| 网卡扫描/绑定/记忆 | `Net/NicScanner.cs`（`NicInfo` / `Scan` / `IndexOf` / `RestoreIndex`）；窗口侧 `ScanNetworkAdapters` / `TargetNicComboBox_SelectionChanged` / `ConnectButton_Click` |
| 采集周期档位 | `PollIntervalComboBox` + `_pollIntervalMs` |
| 日志追加/清空/上限 | `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `MaxDeviceLogEntries` |
| 日志颜色与格式 | `LogEntry`（`DeviceLogLevel` → `LevelBrush`）+ `DeviceLogItemTemplate` |
| **日志落盘（改落盘行为必读）** | `Logging/DeviceLogFileWriter.cs`；窗口侧 `_logWriter` / `FileLevelOf` / `RefreshLogSaveStatus`；独立验证 `LoggingCheck`（见 **§7.1**） |
| 断开原因归类 | `Logging/DisconnectReasonClassifier.cs`（规则本体，纯函数、可独立断言）；窗口侧 `ClassifyDisconnectReason` 只是薄包装，为满足 `ICollectorHost` |
| 连接任务隔离/关闭清理 | `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed` |

## 11. 版本控制

- git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`
- 身份为仓库级配置（`G.Sheng`）
- **提交信息用中文**；提交前先 `git diff` 确认范围，**不要整文件盲加**（会把别人的 WIP 一起带走）
