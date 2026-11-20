# AGENTS.md — DSH_Modbus采集工具

基于 **WPF / .NET 8** 的 Modbus 数据采集上位机 Demo，界面参考 Windows 11 Fluent（自定义标题栏 + 左侧导航 + 卡片式内容）。**仅一个 NuGet 依赖：微软官方 `System.IO.Ports`（串口 Modbus RTU，P4.4）。**

> 需求与优先级见 `Brief.md`；技术设计与决策理由见 `Design.md`；本文件只讲**代码在哪、怎么改**。
>
> ⚠️ **本文件只留「每次都要用的规则」**，实现细节在 **[`AGENTS-细节.md`](AGENTS-细节.md)**
> （§5 自由监控 / 全局变量 / 系统变量 / 状态机 / 采集、§6 心跳与重连、§7 运行日志、§8 通讯驱动 —— **编号不变**），
> 发布交付在 `AGENTS-发布.md`。**改到这些功能时先读那份的对应章节**，那里有成条的 ⚠️ 坑。
> 本文件曾达 136 KB，**超过工作区指令预算（65536 B）被整体丢弃、规则一条都没生效** —— 所以只留「每次都要用的」。
> 优先使用PowerShell 7 工具（如果环境存在），文档均使用UTF-8编码
> 📏 **预算红线 65536 B**（含框架开销约 365 B）。本文件当前 **54061 B / 余量约 11 KB**（2026-11-18 校）；
> 新增前先 `(Get-Item AGENTS.md).Length`，超 64 KB 会再次静默失效。
> 长段落放 `AGENTS-细节.md`（实现细节）或 `AGENTS-发布.md`（发布交付），这里只留一句指向。
> 📦 **已搬到细节档的查阅型大表**：§15 完整目录树、§16 样式键全表（53 个 `x:Key`）。

---

## 1. 构建与运行

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

- **构建前必须先结束进程**，否则 exe 被锁报 MSB3027
- 脚本 / `Start-Process` 启动时**直接跑 exe**（`bin\Debug\net8.0-windows\DSH_Modbus采集工具.exe`，WinExe 不带控制台）；`Start-Process dotnet run ...` 会额外留一个 **dotnet 黑窗口**（跑完也不会自己关）
- 窗口最小 1024×640（`MainWindow.xaml` 的 `MinWidth/MinHeight`），默认与最小一致
- 用户习惯：**构建成功后自动启动应用**；**不要自动提交**（2026-10-30 定案 —— 改完就停，等用户看过再决定）；**不做深色主题验证**（只验浅色，深色适配由主题键保证）
- **版本号与构建日期在 `DSH_Modbus采集工具.csproj`**：`<VersionPrefix>` 手改（提版本改这一处），`VersionSuffix` 由配置决定（Debug 自动带 `-debug`），`<WriteBuildDate>` 每次编译现取系统时间写 `obj/.../BuildInfo.g.cs`。详见 `AGENTS-细节.md` §7.3

## 2. 目录结构


> 📁 **完整目录树已移到 [`AGENTS-细节.md`](AGENTS-细节.md) §15**（约 10.6 KB，属查阅型参考）。
> 这里只留**每次改动都要判断**的两条：哪些目录不碰 WPF、新增逻辑该放哪。
> ⚠️ **增删源文件时两处都要改**：§15 是全量树，本节只到目录级。

**9 个不引 WPF 的目录**（可脱离界面阅读/断言，**没有任何** `using System.Windows*`）：
`Drivers/` `Expressions/` `Io/` `Logging/` `Modeling/` `Net/` `Polling/` `Scanners/` `Startup/`

**引 WPF 的**：`Ui/` `Themes/` + 根目录的 `MainWindow*.cs` 九个 partial、`MonitorItem.cs` / `GlobalVarItem.cs` / `AcquisitionTaskItem.cs` / `SystemVarRow.cs` / `App.xaml.cs`。

⚠️ **判断该不该碰 WPF 看「所在目录」，不是看文件名**：`MonitorItem.cs` 是 `DataPoint` 的界面包装、碰 WPF 属正常，但它在**根目录** —— 不要以为「根目录都不碰」。

**目录职责一句话**（细节见 §15）：入口 `App`（单实例 → 主题 → 建窗）；`MainWindow.*` 九个 partial 按页面分；
`Polling/` 采集编排、`Io/` 点位集与分页、`Modeling/` 数据模型与纯逻辑、`Themes/` 主题、`Ui/` 呈现与对话框、
`Scanners/` 扫码器、`Startup/` 自启与单实例、`Drivers/` Modbus 报文与品牌地址表、`Logging/` 日志落盘。

> **独立验证工程**（仓库根，不属于本工程）：`ModelingCheck/`（用真机实测数据对编解码做断言）、`LoggingCheck/`（日志行格式 / 跨天换文件 / 续写 / 容错 / 断开原因归类，**44 项**）。
> `.tmp/` 下另有一批**一次性**工程（**未提交**，随用随弃）：断言型 `ConnCheck`(35) / `DeviceLogCheck`(23) / `NicCheck`(17) / `AcquisitionCheck` / `GlobalVarCheck` / `MonitorVarCheck` / `SystemVarCheck`；探针型 `IoCheck` / `FakeSlave` / `E2ESlave` / `MachineSlave` / `CounterSlave` / `OrderSlave` / `OrderProbe` / `xlsxprobe`。验的是「改一次就够」的行为，要重跑时会话记录里有完整源码。
>
> **每次改完的验证口径（2026-10-29 用户定案）**：只跑 (a) `dotnet build DSH_Modbus采集工具.sln` + (b) `.tmp/ExpressionCheck`（构建该工程后**直接跑 exe**；`dotnet run` 每次多花约 5 s 在 SDK/NuGet 探测上）。其余断言工程只在改到它们的断言对象时单独跑那一个。

## 3. 页面与 UI

单窗口两行布局：48px 标题栏 + 主体（导航 224 列 + 内容），底部另有一条**全局状态栏**（30px，五个页面都可见）。

```
Window
└ Grid x:Name="RootGrid"                    Rows: 48 / *
   ├ Border       Row=0                              标题栏（48）
   └ Grid         Row=1                       主体  Columns: 224 / *
      ├ Border     Col=0  Margin="0,0,112,0"         左侧导航
      ├ ScrollViewer ColSpan=2 Margin="112,0,0,30"   内容区
      └ Border     ColSpan=2 Height=30 VerticalAlignment=Bottom  底栏
```

> 主体 Grid **没有行定义**：底栏靠 `VerticalAlignment="Bottom"` **叠在内容区之上**，内容区用**下边距 30** 给它让位。详见 §3.5。

### 3.1 导航

| 导航项 | 面板 | 说明 |
|---|---|---|
| `OverviewNav` 总览 | `OverviewPanel` | 页头右侧下拉框「当前视图」（`FormComboBox`）**三项**：`PLC状态机`（P1.2）/ `生产数据`（占位）/ `数据采集`（P1.4，见 §5.5、§5.8）；卡片内是当前视图 + 底部运行日志卡。⚠️ 选择会记住（`app-settings.json` → `OverviewView`，存**枚举名**，`Modeling/OverviewViewSetting.cs`，见细节 §5.5） |
| `StatusInfoNav` 设备监控 | `StatusInfoPanel` | IO 监控（**四分段**：输入监控 / 输出监控 / 自由监控 / 系统变量）+ 分页 |
| `AcquisitionConfigNav` 数据管理 | `AcquisitionConfigPanel` | **三分段**：「数据采集」（P3.1，见 §5.7）/「全局变量」（P3.3，见 §5.2）/「系统变量」（P3.5 只读清单，见 §5.3）。⚠️ 第四档「扫码器」已于 2026-11-04 删除，并入设备连接页 |
| `DeviceConfigNav` 设备连接 | `DeviceConfigPanel` | **两档**：「PLC连接」（连接表单）/「扫码器连接」（P4.5 连接 + P3.5 交互**已合并为一页**，展开块左连接 / 右交互，见 §5.9） |
| `SettingsNav` 设置 | `SettingsPanel` | **卡片顺序（2026-11-08 定案）**：应用标题（P5.6）→ 显示外观（P5.1）→ 开机自启动（P5.5）→ 日志保存（P5.2）→ 项目信息（P5.3）。⚠️ 「自动连接」卡已于 2026-11-05 删除：PLC 的搬去设备连接页，扫码器改用行上「启用」（§7.2） |

切换逻辑：`NavRadioButton` 样式里的 `EventSetter Checked=Nav_Checked`，按 sender 显隐面板。

### 3.2 设计器预览

五个面板同处一个 `StackPanel`，运行时由 `Nav_Checked` 切换，而**设计器只认 `d:Visibility`**（`d:` 在 `mc:Ignorable="d"` 中，不影响编译与运行）。

- 当前 `OverviewPanel` = `d:Visible`（设计器默认预览总览页）；要预览其它页就把目标页改 `d:Visible`，其余改回 `d:Collapsed`
- 总览页 `Height` 绑 `ContentScrollViewer.ViewportHeight`，设计器里常解析不出 → 调样式时临时注释该绑定

### 3.3 关键样式（Window.Resources）

> 📋 **53 个 `x:Key` 全表已移到 [`AGENTS-细节.md`](AGENTS-细节.md) §16**（约 5.9 KB 查表）。
> 改外观前先去全表里找**现成的**，不要另造；下面只留「必知 + 最容易踩的」。

**名字前缀**：`Io*` = IO 页/分页、`Form*` = 表单、`Monitor*` = 自由监控行内、`Acq*` = 采集任务组行内、`GlobalVar*` / `SystemVar*` = 两个分段模板、`Scanner*` = 扫码器、`Dialog.*` = 对话框（在 `Themes/DialogControls.xaml`，见 §9.3）。

**四条最容易踩的**：

- ⚠️ **隐式 `ScrollBar` 必须保持无 `x:Key`**：隐式样式是唯一能命中 `ScrollViewer` **内部创建**的滚动条的手段，改成 keyed 就全失效
- ⚠️ **横向滚动条要把 `PART_Track.IsDirectionReversed` 翻回 `False`**：模板里那个 `True` 是给纵向的，横向不翻的话 `Value=0` 时滑块贴右端（2026-11-06 实测修复，历史表格的横向条就是这么坏的）
- ⚠️ **`BasedOn` 走 `StaticResource`，按文档顺序解析**：被继承的样式必须**先定义**，否则 `XamlParseException`（`AcquisitionSourceBox` / `AcquisitionTargetBox` / `ScannerFieldTextBox` 都靠这条）
- ⚠️ **同一行混两种语义会读错**：`MonitorIconButton` 悬停红＝删除、`MonitorHeaderIconButton` 悬停主色＝新增、`AcqRowIconButton` 悬停中性灰＝操作；`AcqRowDeleteButton` 只把悬停换实心红。新增行内按钮先想清楚属于哪一种

### 3.4 页面细节

- **总览**：标题 + 右侧「当前视图」下拉框（三项，§3.1）。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**（其余四页照滚）。`数据采集` 视图是卡片内第二层（顶栏顺序：任务组下拉框 → 实时-历史页签 → 文件路径 / 分隔线 / 表格；画布见 §5.8.1），见 §5.8
- **设备监控**：见 §5 IO 监控 / §5.1 自由监控
- **设备连接**：连接方式 / 协议 / **从站站号** / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮；**设备通讯区域**另有左「字节顺序」3 个下拉框（32 位整数 / 浮点 / 字符串）+ 竖分隔线 + 右「地址表元件」（§8.1）。连接中锁定表单（`SetInputsEnabled`）—— **例外是三个字节顺序下拉框**：它们不改报文、不改连接参数，只决定怎么解释寄存器，改完下一拍生效，锁上反而挡住现场纠正。**串口模式（P4.4，2026-11-18 已实现）**：与以太网面板**互斥显隐**，见下

**连接参数会被记住**（`app-settings.json`）：`TargetNic` / `TargetIp` / `TargetPort`，下次启动回填。

- IP / 端口**失焦才落盘**（`ConfigInput_LostFocus`）：这两个框还挂着 `TextChanged`（只用于刷新底栏概览），逐字符写盘会把 `192.168.1.` 这种半截内容存进 json
- 端口非法（空 / 非数字 / 越界）时**保留上次的有效值**，不拿垃圾覆盖
- 存**空串 / 0** 表示「没保存过」→ 界面保持 XAML 默认值（`192.168.1.100` / `502`）；旧 json 缺字段即为此态，无需迁移
- 点「连接」前焦点先离开输入框，`LostFocus` 一定先于 `Click` 跑，不会「改了 IP 直接点连接却没记住」

**目标网卡**（`TargetNic`）：存的是**网卡名**（如 `WLAN`）而非下拉框文本 `WLAN(192.168.1.15)` —— 文本里嵌着 IP，同一块网卡换网络 IP 就变，按文本匹配**恰好在最需要它的场景下失效**。还原优先级「本次扫描前的选择 → 上次运行记住的 → 第 0 项」；⚠️ 扫描必须整体罩在 `_restoringNicSelection` 里（`Items.Clear()` 也会触发 `SelectionChanged`），否则网卡临时消失时那次回退会把用户的选择**悄悄抹掉**。绑错网卡会报 `WSAENETUNREACH`，**同网段双网卡时还会退化成「连接超时」**（路由认为直连，SYN 发到了没有 PLC 的那块网卡）—— 故多网卡现场「超时」不能直接推断「PLC 没响应」。完整实测与排查步骤见 `AGENTS-细节.md` §8.2

### 3.5 全局底栏（连接状态）

30px 高，**五个页面都可见**（在面板之外，不随导航切换显隐）。

| 位置 | 控件 | 内容 |
|---|---|---|
| 左 | `StatusDot` + `StatusText` | 状态点 + 状态文字：`未连接` / `正在连接 …` / `已连接` / `通讯异常：<原因>` / `已断开：<原因>` / `正在重连 … Ns（第 M 次）`。**一律不带 ip:端口** —— 连接参数只由右侧 `StatusBarDetail` 显示；原因经 `ClassifyDisconnectReason` 归一，与日志同一份用词 |
| 右 | `StatusBarDetail` | 连接参数：`驱动：连接方式  协议  IP:端口` |

- **两个控件名沿用自旧设备连接表单**，故 `SetStatus()` 逻辑无需改动 —— 它写的就是这两个名字
- `SetStatus(text, colorHex)` 同时设 `StatusText.Text` / `.Foreground` / `StatusDot.Fill`，并调 `UpdateOverviewStatus()`。**规则在 `Ui/StatusBarPresenter.cs`**：窗口侧 `SetStatus` / `UpdateOverviewStatus` 各只是一行薄包装（12 个调用点未动）；右侧参数串由 `BuildConnectionDetail()` 提供，呈现器因此不认识任何表单控件
- ⚠️ **`_statusBar` 必须可空**：`UpdateOverviewStatus()` 会在 `InitializeComponent()` **期间**被 ComboBox 的 `SelectionChanged` 触发 —— 那时构造函数体还没执行、呈现器尚未创建。改成不可空会让应用**一启动就崩**（实测 `NullReferenceException`，堆栈落在 `InitializeComponent` 里）。构建与独立断言都不会报，只有真跑起来才看得见

**布局要点（改这块务必先读）**：

- 底栏在主体 Grid 内用 `VerticalAlignment="Bottom"` **叠放**，内容区靠 `Margin="112,0,0,30"` 让位；**没有新增行定义**
- `Margin.Left = 112` = 导航列的视觉右边界（导航是 `Margin="0,0,112,0"`，**右边距** 112）
- ⚠️ **不要把底栏改成窗口级第 3 行或给它加列定义** —— 前者会切走导航列的高度、后者会在底栏里切一刀导致左下角空缺（两种错法都试过）

### 3.6 最大化溢出补偿

`WindowChrome` 自绘标题栏时，系统把最大化窗口四边**各外扩一个边框宽度**（150% DPI 实测 11px），底边被推到任务栏之下，而 `WindowChrome` 下客户区覆盖整个窗口 → 底栏最下一条被遮住。

- 算法在 `Ui/MaximizedInset.cs`（`MaximizedInset.Apply(window, root)`）：按「窗口矩形 − 所在显示器工作区」算外扩量，给 `RootGrid` 四边各内缩；非最大化清零。窗口侧 `UpdateMaximizedInset()` 只是一行薄包装
- 由 `Window_StateChanged` 调两次（切换瞬间 + `DispatcherPriority.Loaded` 后），因为状态刚变时窗口矩形可能还没落定
- 用**物理像素**测量再按 `VisualTreeHelper.GetDpi` 换算，不依赖 WPF 布局时序；多显示器用 `MonitorFromWindow`
- ⚠️ **拦 `WM_GETMINMAXINFO` 改 `ptMaxSize` 无效**（已试过）：钩子确实被调用，但系统对带边框的最大化窗口仍按自己的规则外扩，写回值被忽略。**不要再走这条路**

### 3.7 内容区左右留白（改卡片宽度/对齐必读）

五个页面的标题与所有卡片都挂在**同一个** `ContentScrollViewer` 下（并列兄弟节点），故左右留白由它一个元素决定，改一处即五页同步：

```xml
<ScrollViewer x:Name="ContentScrollViewer"
              Padding="20,4,20,16" Grid.ColumnSpan="2" Margin="112,0,0,30">
```

| 数 | 含义 | 基准 |
|---|---|---|
| `Margin.Left = 112` | 让开导航列 | 导航 `Border` 的**右边距** 112 把分隔线推到 x=112 |
| `Padding.Left = 20` | 卡片/标题与**分隔线**的间距 | 分隔线 |
| `Padding.Right = 20` | 卡片与**窗口右缘**的间距 | 窗口右缘 |

- **左右 Padding 必须同值**，卡片才会在内容区里居中（左以分隔线为基准、右以窗口右缘为基准，两者都只是 Padding，同值即对称）。曾左 20 / 右 36 是历史遗留的不对称，已统一为 20
- ⚠️ **`Margin` 与 `Padding` 分工不同**：`Margin` 管「让开导航」、`Padding` 管「卡片缩进」；分隔线位置只由 `Margin` 决定，改 `Padding` 不会动线
- ⚠️ 改 `Margin.Left` 必须**同时**改导航 `Border` 的右边距（两个 112 成对，见 §3.5）；只改一个会让分隔线与内容起点脱节
- 卡片**外框**已与页面大标题左对齐；卡片**内容**再往内缩各自 `Padding.Left`（设置页三卡 24、设备监控 / 数据管理 / 设备连接 12、总览日志卡无）—— 这是卡片内边距，不是错位
- ⚠️ **带分段控件的页面，卡内边距必须是 12**：分段条（`IoSegmentRadioButton` 所在那层 `Border`）靠卡内边距定位，设备连接页曾写 20，导致「PLC连接 / 扫码器连接」比设备监控页往右错 8 DIP（2026-11-04 已统一为 12，UIA 实测两页分段条左缘同为 x=413）

### 3.8 窗口圆角与边缘线（DWM 绘制）

窗口用 `WindowChrome` 自绘标题栏，且 `GlassFrameThickness=0` + `NonClientFrameEdges=None`，**系统既不画边框也不画圆角** —— 窗口贴在桌面上没有边界。两件事都交给 DWM 做，在 `Ui/WindowCorners.cs`（`WindowCorners.Apply(this)`，`Window_Loaded` 里调一次）：

| 属性 | 值 | 作用 |
|---|---|---|
| `DWMWA_WINDOW_CORNER_PREFERENCE` (33) | `DWMWCP_ROUND` (2) | 系统标准圆角，随 DPI 缩放 |
| `DWMWA_BORDER_COLOR` (34) | 主题 `Brush.Window.Border` | 边缘线颜色（浅 `#C8C8C8` / 深 `#4A4A4A`） |

- ⚠️ **不要在 XAML 里自绘描边**（已试过、已删）：开启 DWM 圆角后系统会自己在最外层补一圈 1px 边框，自绘那条被盖住（实测边缘色变成 DWM 的 `#B5B5B6` 而不是设的 `#C8C8C8`），圆角处还会露出两道错开的弧线。**两件事都交给 DWM，圆角与边框才同心同半径**
- 最大化时 DWM 自动收掉圆角、让边框贴边，**不需要**在 `Window_StateChanged` 里手工同步任何值
- ⚠️ DWM 要的是 `COLORREF`（`0x00BBGGRR`，**BGR 顺序**），不是 WPF 的 RGB；写反会变蓝边
- ⚠️ 换主题时 `DynamicResource` 换不到 DWM 属性，`OnThemeChanged` 里要调一次 `WindowCorners.SyncBorderColor(this)`；颜色必须从资源取，不能写死
- 老系统（Win10）上这两个属性号不认识，`DwmSetWindowAttribute` 返回非 0，此时**安静放弃** —— 窗口保持直角无边框，不影响功能

## 4. 代码分工（`MainWindow` 主体 + 九个 partial）

> **2026-10-29 拆分**：`MainWindow.xaml.cs` 曾长期贴着 4600 行，现为主体 + `MachineState` / `Acquisition` / `GlobalVar` / `Monitor` / `IoImport` / `AcquisitionView` / `AcquisitionLive` / `Scanner`。
> **新建处理器先按页面归到对应 partial**，只有跨页面的（连接生命周期 / 心跳 / 导航 / 日志 / 底栏）才写回主体。
> 已抽出去的：采集编排 → `Polling/`、IO 点位集/分页/导入 → `Io/`、主题 → `Themes/`、呈现规则 → `Ui/`。**自由监控刻意未抽成独立的层** —— 它有 12 处直接碰控件，见 `Design.md` §8-1。
> ⚠️ **不要在这里写 `MainWindow.xaml.cs` 的具体行数**：该数字已三次未跟上（曾同时出现 2590 / 2190 / 2187 三个互相矛盾的值），对判断没有作用。

| 区块 | 关键成员 |
|---|---|
| **处理器在哪个 partial** | 主体 `MainWindow.xaml.cs`＝连接 / 心跳 / 导航 / 底栏 / IO 分页·导入·`SetInputsEnabled` / `AppendDeviceLog` / `SetStatus` / `ScanNetworkAdapters`；`Monitor.cs`＝`Monitor*`·`CommitMonitorValueAsync`·`WriteToDeviceAsync`·`ClassifyDisconnectReason`；`GlobalVar.cs`＝`GlobalVar*`；`MachineState.cs`＝`SyncMachineTimer`·`RefreshMachineStates`·`RefreshMachineGlobals`；`Acquisition.cs`＝`RunAcquisitionTick`·`RunWriteTaskAsync`·`RefreshAcquisitionGlobals`·`WriteToDeviceAsync`；`IoImport.cs`＝IO 表导入；`AcquisitionView.cs`＝总览「数据采集」骨架（任务组下拉框 / 历史表格 / 1 秒增量刷新）；`AcquisitionLive.cs`＝实时画布（卡片拖动 / 缩放 / 背景图）；`Scanner.cs`＝扫码器（`Scanner*` 全部处理器 + `ScannerItem` / `ScannerItemSet` / 判定节拍 / 每台独立重连） |
| 连接状态机 | `ConnectButton_Click`（表单校验 + 按钮三态）/ `TryConnectAsync` / `Disconnect()` / `Disconnect(status,color)` / `SetInputsEnabled`；**身份与生命周期在 `Polling/ConnectionSession.cs`**（`IsConnecting` / `IsCurrent` / `MarkConnected` / `ReleaseClient` / `ForgetTarget` / 重连计数）+ `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed` |
| 自动重连 | `StartReconnect` / `ReconnectLoopAsync` / `CancelReconnect`（**循环留在窗口**，逐秒刷状态栏 / 锁表单是界面驱动）；目标快照 `ReconnectTarget` 与退避表 `ReconnectPolicy` 在 `Polling/` |
| 轮询（入口在本类，实现在 `Polling/`） | `_poller.PollLoopAsync` → `PollOnceAsync`（按地址区分块 + 2000 点切窗 / `PollFreeMonitorAsync` 自由监控，按区选 FC01/03/04） |
| `ICollectorHost` 实现 | `PollIntervalMs` / `MonitorSet` / `CaptureSnapshot()` / `IsCurrentConnection` / `RunOnUiAsync` / `NotifyConnectionLost` / `ClassifyDisconnectReason` / `ReportCommunicationWarning`（连续异常码告警，§5） |
| 心跳编排 | `HeartbeatLoopAsync`（失败时转 `_poller.NotifyConnectionLost`） |
| IO 点位集 / 分页 / 导入 | `RebuildIoPoints`（薄包装）→ `IoPointSet.Rebuild` / `TryCreatePoint` / `NormalizePointName` / `TryValidatePoint`；`ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（算术在 `IoPager`）；`ImportIoTableButton_Click` → `IoTableImporter.Read`（结果弹框 `ThemeMessageDialog` 留本类） |
| **自由监控 / 全局变量 / 系统变量** | 处理器清单见 `AGENTS-细节.md` §5.1 / §5.2 / §5.3（系统变量**没有任何编辑处理器**，只有 `AcqView_Checked` 切面板 + 构造期一次绑定） |
| 持久化 | `LoadComments` / `SaveComments`（薄包装 → `IoPointSet.Load` / `Save`） |
| 网卡 | `ScanNetworkAdapters`（闸门 + 填下拉框）/ `TargetNicComboBox_SelectionChanged`；扫描与还原规则在 `Net/NicScanner.cs`，持久化见 §3.4 / `AGENTS-细节.md` §8.2 |
| 日志 | 窗口侧只留三处**控件**动作：`AppendDeviceLog`（追加 + `ScrollIntoView` + 设置页失败提示）、`ClearDeviceLogButton_Click`、`RefreshDeviceLogColors`（薄包装）；**编排与规则在 `Ui/DeviceLogPresenter.cs`**（内存集合 / 上限 1000 / 落盘旁路 / **界面与文件共用时间戳** / 界面级别 → 落盘级别）；`ClassifyDisconnectReason` 仍是薄包装 |
| 底栏 / 最大化 | `SetStatus` / `UpdateOverviewStatus` / `BuildConnectionDetail` → `Ui/StatusBarPresenter.cs`；`UpdateMaximizedInset` → `Ui/MaximizedInset.cs` |
| **主题（P5.1）** | `Themes/ThemeManager.cs`；窗口侧 `ThemeSegment_Checked` / `SyncThemeSegments` / `OnThemeChanged`（底栏状态色映射在 `Ui/StatusBarPresenter.cs`） |
| **日志落盘（P5.2）** | `Logging/DeviceLogFileWriter.cs`（按天追加 / 跨天换文件 / 失败自愈）；窗口侧 `_logWriter` / `LogSaveToggle_Changed` / `SyncLogSaveToggle` / `LogDirectoryTextBox_LostFocus` / `LogBrowseButton_Click` / `LogOpenFolderButton_Click` / `LogResetDirectoryButton_Click` / `ApplyLogDirectory` / `RefreshLogSaveStatus`（§7.1） |
| **项目信息（P5.3）** | `AppInfo.cs`（读程序集元数据）+ csproj 的 `VersionPrefix`/`VersionSuffix` 与 `WriteBuildDate` 目标；窗口侧 `RefreshProjectInfo`（§7.3） |
| **开机自启动（P5.5）** | `Startup/AutoStartCommand.cs`（纯逻辑：命令串 / 同 exe 判定 / 禁用记录 `& 0x01` 判据）+ `Startup/AutoStartRegistry.cs`（HKCU `Run` + `StartupApproved` 读写）；窗口侧 `AutoStartToggle_Changed` / `SyncAutoStartToggle` / `RefreshAutoStartStatus`（§7.4） |
| **应用标题（P5.6）** | `Modeling/AppTitle.cs`（纯逻辑：两个默认值 / 清洗换行与超长 / 默认识别）+ `ThemeManager.AppTitleText` / `SetAppTitle`；窗口侧 `ApplyAppTitle` / `ApplyAppTitlePermission` / `AppTitleTextBox_LostFocus`；权限 `UserRules.CanEditAppTitle`（§7.5）。⚠️ 改的是**两处**：`TitleBarCaption` + `Window.Title`。⚠️ 卡上只有「标题」+ 输入框：**清空即恢复默认**，没有按钮、没有账户说明（2026-11-08 定案） |
| 关键字段 | `_session` / `_pollCts` / `_windowClosed` / `_pollIntervalMs` / `_currentDriver` / `_poller` / `_ioSet`（内含 X/Y 点位列表与注释表）/ `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_machineViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_monitorSet` / `_globalVarSet` / `_logPresenter` / `_logWriter` / `_statusBar`。**`volatile` 共 12 个**：`_windowClosed` / `_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_machineViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_machineGlobals` / `_acquisitionGlobals` / `_scannerGlobals` |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控 · 6. 心跳与连接生命周期 · 7. 运行日志 · 8. 通讯驱动标准

> **这四章已整段移到 [AGENTS-细节.md](AGENTS-细节.md)，编号不变。**
> 涉及**自由监控 / 全局变量 / 系统变量 / 状态机 / 采集 / 心跳与重连 / 日志落盘 / 字节顺序 / 网卡**的改动，动手前先读对应章节（`§5.1` … `§8.2` 这些引用仍然有效，只是换个文件查）。

## 9. 代码约定

- 命名空间 `DSH_Modbus采集工具`（含中文，路径亦含中文，命令行注意编码/引号）
- 事件处理命名：`Xxx_Click` / `Nav_Checked` / `Xxx_SelectionChanged`
- 颜色：**一律用 §9.2 的主题键**，XAML 里不得写 `#RRGGBB`
- 字体：`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`；标题用 `Segoe UI Variable Display`；图标 `Segoe Fluent Icons, Segoe MDL2 Assets`（资源键 `IconFont`）
- 表单：标签 `Width="80"` + 控件 `Width="200"`、行距 16；`SyncInputHeights` 在 Loaded 与面板显示时同步控件高

### 9.1 控件风格约定（新增控件必读）

**全应用统一一套视觉语言，新增任何输入框 / 下拉框 / 按钮都必须套用现成样式，不要手写外观。**

| 控件 | 用哪个样式 | 外观 |
|---|---|---|
| 下拉框 | `FormComboBox`（表单）／ `MonitorInlineComboBox`（行内） | **胶囊**：灰底 `#EDEDED`、无边框、圆角 6、右侧 chevron |
| 输入框 | `FormTextBox`（表单）／ `MonitorInlineTextBox`、`MonitorValueTextBox`（行内） | **框线**：白底、`#C8C8C8` 1px 边框、圆角 4 |
| 按钮 | `FormButton`（表单）／ `IoCardButton`（其它） | **灰底胶囊** `#EDEDED`、圆角 6 |
| 开关（二值偏好，如「启用 / 不启用」） | `ToggleSwitch` | **Win11 ToggleSwitch**：状态文字在左、轨道在右；关＝灰轨道柄靠左，开＝主色轨道柄靠右 |

**默认不写提示文字。** 输入框下方、按钮旁边的浅灰小字（示例、用法说明、快捷键提示）**一律不加** —— 用户原话：「以后如无说明默认不加提示」。确有必要时（例如颜色项旁标出当前色值）才加，且加之前先确认。既有页面不追溯，新增页面不再加。

**什么时候用开关、什么时候用分段控件**：

- **只有两档、且是「开 / 关」语义** → `ToggleSwitch`（如日志保存）。轨道变灰已经说明了「关」，再摆一个「关闭」档是多余的一层信息
- **两档以上（浅色 / 深色 / 跟随系统），或各档是并列的「模式」而非「开关」** → `IoSegmentRadioButton`

尺寸只有两档，**按所在场景选**，不要另立数值：表单 14/26（设备连接页）；行内 / 卡片 12/22（自由监控表格行）。

**几条容易踩的**：

- **下拉框必须同时给 `ItemContainerStyle`**（已写进 `FormComboBox` 样式里，别在控件上覆盖）—— 漏了展开项会用系统默认蓝条，与胶囊风格明显不搭
- ⚠️ **不要给 ComboBox 设 `DisplayMemberPath`**：它与自定义模板里的 `SelectionBoxItemTemplate` 不合成，闭合态会显示成类型名
- `ComboBoxItem` 模板里的 `ContentPresenter` **必须写 `ContentSource="Content"`**，否则条目退化成对象的 `ToString()`
- **下拉项的左边距 = `ComboBox.Padding` − 2**（因为 item 的 `Border` 还有 `Margin="2,1"`）：改了一边必须同步另一边，否则展开时文字比闭合态偏右、切换选项会横向跳一下
- 颜色的三个语义出口：**红 `#C42B1C`＝配置要改**、**琥珀 `#B26A00`＝通讯侧的事**、主色 `#0067C0`＝聚焦
- XML 注释里**不能出现连续两个减号**（Markdown 表格分隔行会直接编译失败 `MC3000`）
- `IO模板.xlsx` 仅**导入功能测试用**，其注释列与 `io-comments.json` 都是占位文本（`CommentX0`/`Comment0`），**无业务含义** —— 不必维护、不必恢复，也不是待处理问题

### 9.2 主题与配色（P5.1，改颜色必读）

**XAML 里不得再出现颜色字面量**（`#RRGGBB`）。所有颜色都写成 `{DynamicResource Brush.Xxx}`，值取自 `Themes/Light.xaml` / `Dark.xaml`。

- **必须是 `DynamicResource`，不能是 `StaticResource`**：后者在加载时解析一次，运行期换主题不会跟着变
- 两个字典的键**必须完全一致**。`ThemeManager.ValidateKeyParity()` 在启动时校验，缺键直接抛异常（缺键的后果是控件静默变黑/透明，只在切到某个主题时才暴露，极难查）
- **浅色字典的值 = 主题化前的字面值，逐个搬入、一个没改**（149 处替换已脚本校验 1:1）。改浅色值 = 改既有外观，须确认是有意为之
- 键按**角色**命名（`Brush.Input.Border`、`Brush.Text.Muted`），不按色值。新增控件先判断「这是哪一类」再挑键

**C# 侧直接赋画刷的地方也必须随主题变**（这些不经过 `DynamicResource`）：

| 位置 | 做法 |
|---|---|
| `IoPoint.StateBrush`（灯色） | 每次取值都 `ThemeManager.GetBrush("Io.*")`；换主题后由 `OnThemeChanged` 调 `RaiseStateBrushChanged()` 通知绑定重算 |
| `LogEntry.LevelBrush`（日志行色） | 构造时按主题取（类在 `Ui/DeviceLogPresenter.cs`）；换主题后 `RefreshDeviceLogColors()` → `DeviceLogPresenter.RefreshThemeColors()` 重建整表（保留原 `DateTime`，不重新 `Parse` 文本） |
| `SetStatus(text, colorHex)`（底栏） | 实现在 `Ui/StatusBarPresenter.cs`：入参仍是**浅色字面值**，由内部 `BrushKey()` 映射到语义键；它记住上次的文字与颜色，换主题后 `ReapplyTheme()` 重设一次 |
| `Ui/SourceForegroundConverter`（采集「来源变量」框字色） | `IMultiValueConverter` 拿到的是**一次性快照画刷**，换主题不会自动重算；`OnThemeChanged` 调 `AcquisitionTaskItemSet.RaiseSourceColorRefresh()` 触发两条 INPC，让 MultiBinding 重跑转换器 |

**几条容易踩的**：

- ⚠️ **落盘的是整个对象，归一化必须放在 `Save()` 里**：`app-settings.json` 里主题 / 日志 / 网卡同处一个对象，每次保存都写整个对象。所以「日志目录等于默认值时归回空串」由 `Save()` 统一做，**不要**写进各个 Setter —— 曾经只在 `SetMode` 里调，于是任何新增的整对象保存路径（如 `SetTargetNic`）都会把「看一眼路径」留下的绝对默认路径写进文件，exe 一移动日志就写不回新位置的 `Log\`
- ⚠️ **半透明叠加必须换色**：`Brush.Overlay.*` 浅色是黑（`#0A000000`）、深色是白（`#14FFFFFF`）。黑叠加压在深底上等于没效果
- ⚠️ **`*.Fill` 三个实底色深浅同值**（主色 `#0067C0`、危险 `#C42B1C` 系），因为它们上面压白字，改了会掉对比度
- ⚠️ **IO 灯色深浅值不同**：浅色用深绿 `#0B7A0B`，深色必须提亮到 `#4EC94E`，否则与深底融在一起、看不出「亮/灭」
- ⚠️ **禁用态灰有 3 档**（`Brush.Text.Disabled` = `#B0B0B0` 下拉框 / `.Soft` = `#C0C0C0` 分页箭头与文字按钮 / `.Faint` = `#C8C8C8` 图标按钮）。主题化前就是 3 个不同字面值，**合并成一档会被看出差别**
- 主题切换是**整体换字典**（先加新字典再删旧的，避免中间态取不到键），不遍历可视树
- 「跟随系统」读注册表 `AppsUseLightTheme` 并挂 `SystemEvents.UserPreferenceChanged`；**显式选了浅/深就不再跟随系统**
- 选择持久化在 `app-settings.json`（与 `io-comments.json` 同级），字段缺失或损坏都回退到「跟随系统」

### 9.3 对话框控件（新增对话框必读）

**对话框用另一套样式，键前缀 `Dialog.`，定义在 `Themes/DialogControls.xaml`**（由 `App.xaml` 合并进 `Application.Resources`）。**不要**在对话框里复用 `FormComboBox` / `FormTextBox` / `FormButton` —— 那些是 `Window.Resources`，作用域到不了别的窗口；且主窗口的胶囊风格尺寸不适合对话框。

| 用途 | 键 |
|---|---|
| 标签 / 说明 / 正文 / 红色错误 | `Dialog.Label` / `Dialog.Hint` / `Dialog.Body` / `Dialog.Error` |
| 输入框 / **密码框** | `Dialog.TextBox` / **`Dialog.PasswordBox`**（`PasswordBox` 与 `TextBox` 是两个类型，样式不通用） |
| 下拉框 / 下拉项 | `Dialog.ComboBox` / `Dialog.ComboBoxItem` |
| 单选框 | `Dialog.RadioButton`（16 圆圈，选中整圈填主色 + 6 白点，**文字在右**） |
| 按钮（次色 / 主色 / 破坏性） | `Dialog.Button.Secondary` / `Dialog.Button.Primary` / `Dialog.Button.Danger` |
| 提示 / 确认弹框 | **`Ui/ThemeMessageDialog.cs`**（`Info` / `Confirm`，见 §9.3 第 9 条） |
| 窗口外壳（字体、边框、底色、深色标题栏） | `Ui/DialogWindow.cs` 基类构造里已调 `DialogChrome.Apply(this)` |

**一律继承 `Ui/DialogWindow.cs`**（2026-10-29 抽出，四个对话框共减 445 行样板）。它已经包掉：居中归属（`AttachOwner`）、抢焦点 + `ShowDialog`（`ShowOwned`）、取不到样式就抛（`RequireStyle`）、控件工厂（`AddLabel` / `Place` / `MakeTextBox` / `MakeCombo` / `MakeRadio` / `MakeButton`）、`ComboItem<T>`（显示文本 + 真实取值，**不要靠字符串反解**）。**子类只写「自己有几行、每行放什么」**，不要再抄外壳代码：

```csharp
public sealed class MyDialog : DialogWindow
{
    private MyDialog() : base("标题", 442)
    {
        var root = new Grid();
        // AddLabel(root, "提示", row: 0);
        // Place(root, MakeTextBox(null), row: 0, margin: new Thickness(0, 0, 0, 10), stretch: true);
        Content = root;
    }

    public static bool Ask(Window owner)
    {
        var dlg = new MyDialog { /* 初值 */ };
        dlg.AttachOwner(owner);
        return dlg.ShowOwned(dlg._firstFocusable);   // 返回是否点了确定
    }
}
```

⚠️ `MakeTextBox` **不写死宽度**：交给 `Place(..., stretch: true)` 随列宽拉伸，否则 150% DPI 下右边框会被裁掉。

**九条容易踩的**：

1. ⚠️ **样式取不到必须抛，不要静默用默认外观**。WPF 默认 ComboBox 模板把 ToggleButton 画成**硬编码的浅色渐变、完全忽略 `Background`** —— 深色主题下文字 `#F0F0F0` 落在 `#EBEBEB` 底上，实测对比度 **1.05:1**（等于看不见）。这种「改了颜色却没用」的 bug 极难查，所以宁可启动就崩。
2. ⚠️ **ComboBox 必须用 `Dialog.ComboBox`，不能只改颜色**。原因同上：默认模板不吃 `Background`，只有整份重绘才修得掉。
3. ⚠️ **`Dialog.ScrollBar` 必须是隐式样式**（`<Style TargetType="ScrollBar" .../>` 无 `x:Key`）。弹层里的滚动条由 `ScrollViewer` **内部创建**，拿不到 keyed 样式，深色下会是一条白条。**不能**把样式写进 `ControlTemplate` 里的 `ScrollViewer.Resources`：模板作用域内 `StaticResource` 查不到后定义的键，会直接崩。
4. ⚠️ **输入框不要写死 `Width`，让它在列里拉伸**。对话框客户区比看起来窄：`Width=442` 的窗口在 150% DPI 下，列 1 只有约 **327 DIP**。写死 `Width=330` 会超出 3 DIP 被裁掉 —— 表现为**右边框整条不见了**（左/上/下三条都在，因为那 1px 落在被裁的像素上）。用 `Place(..., stretch: true)`。
5. ⚠️ **TextBox 模板里不要再给 `PART_ContentHost` 套 `Margin="{TemplateBinding Padding}"`**。`TextBoxView` 自己就会吃一遍 `Padding`，套两次等于双倍：实测文字起点距左边框 20 DIP，而下拉框只有 10 DIP。只保留 `TextBox.Padding` 一处。
6. ⚠️ **对话框内的错误提示一律内联（`Dialog.Error` 红字）**：在「改到对为止」的循环里弹窗会抢焦点打断它。**联动坑**：若校验失败后会**程序回填**输入框（如恢复上次有效值），`TextChanged` 里那个「一改字就清错误」的逻辑会把刚显示的错误**立刻擦掉** —— 表现为「校验失败了但界面一个字都不显示」。加一个 `_reverting` 标志挡住程序回填。
7. ⚠️ **一组单选按钮必须显式给 `GroupName`**。WPF 默认按**逻辑父容器**分组，而对话框里常把某个选项和它的参数框放同一个 `StackPanel`（如「周期（s）」+ 秒数输入框），于是几个单选按钮各自成组、**可以同时选中**。真机截图里就是两个蓝圈同亮 —— 表现为「切到触发档，但表达式框仍是灰的」。实测踩过一次，见 `AcquisitionTaskDialog`。
8. ⚠️ **「行距对齐」= 行高固定，不是每行留一样的下边距**。`Place(..., margin)` 的 10 DIP 是靠 `Auto` 行高**叠加**在内容高度上的，于是行距 = 内容高 + 10：同屏有单选行（16）、文本框行（30）、带按钮的路径行（32）时，实测行距变成 60/49/39/51/62/60 px（150% DPI）。**正确做法**是给表单行统一的 `RowDefinition.Height`（`AcquisitionTaskDialog` 用 40 = 该行最高的内容 32 + 呼吸），标签与控件都 `VerticalAlignment=Center`；此时 `Place` 的边距要传 `Thickness(0)`、`AddLabel` 要传 `bottomMargin: 0`。错误行与按钮行仍留 `Auto`。
9. ⚠️ **提示 / 确认一律用 `Ui/ThemeMessageDialog.cs`，不要再写 `MessageBox.Show`**（2026-11-06 用户定案「所有弹出对话框风格统一」）：系统消息框不吃主题，深色下弹一块白板，字体/圆角/按钮与其余对话框完全不是一套。两个入口 —— `ThemeMessageDialog.Info(owner, 标题, 正文)`、`ThemeMessageDialog.Confirm(owner, 标题, 正文, danger:, confirmText:)` 返回 `bool`。**破坏性操作（删除 / 清空）传 `danger: true`**：确定钮是实心红（`Dialog.Button.Danger`）**且默认焦点落在「取消」**，防误按回车。正文用 `Dialog.Body`（13 号、可折行），别用 `Dialog.Label`（那 12 号灰字是表单行标签）。

## 10. 任务速查

| 需求 | 位置 |
|------|------|
| **加输入框 / 下拉框 / 按钮（新增控件必读）** | 套用现成样式，别手写外观 —— 见 **§9.1 控件风格约定** |
| **改任何颜色（必读）** | 用主题键，**不要写字面量** —— 见 **§9.2 主题与配色** |
| **新增 / 改对话框（必读）** | 继承 `Ui/DialogWindow.cs` + 用 `Dialog.*` 样式，别复用主窗口样式 —— 见 **§9.3 对话框控件** |
| **弹提示 / 弹确认** | `Ui/ThemeMessageDialog.cs` 的 `Info` / `Confirm`（**不要 `MessageBox.Show`**）—— 见 **§9.3 第 9 条** |
| **加/改主题、深色适配、跟随系统** | `Themes/`（`Light.xaml` / `Dark.xaml` 同键）+ `Themes/ThemeManager.cs`；C# 侧画刷见 §9.2 |
| **设置页（P5）** | `SettingsPanel` + `SettingsNav` + `Nav_Checked`；主题分段 `ThemeSegment_Checked`；日志开关 `LogSaveToggle_Changed`、路径 `LogDirectoryTextBox_LostFocus`；开机自启动 `AutoStartToggle_Changed` / `SyncAutoStartToggle` / `RefreshAutoStartStatus`（§7.4）；应用标题 `AppTitleTextBox_LostFocus` / `ApplyAppTitlePermission`（§7.5，**工程师专属**，清空即恢复默认）；项目信息 `RefreshProjectInfo`（§7.3）。⚠️ 自动连接**不在设置页**了（2026-11-05）：PLC 的在 `DeviceConfigPanel` 表单末行 `AutoConnectPlcToggle_Changed` / `SyncAutoConnectToggle` / `TryAutoConnect`，扫码器用行上「启用」（§7.2） |
| 改导航项/页面显隐 | `MainWindow.xaml` 导航区 + `Nav_Checked` |
| 加通讯品牌 | `Drivers/DriverRegistry.cs`（§8） |
| 加设备连接字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| **切换设备类型（品牌）** | `DeviceTypeComboBox_SelectionChanged` → `ConfirmDriverSwitch`（计数 + 确认框）→ `ApplyDriverSwitchCleanup`（清映射地址 / 删元件监控项 / 清 IO 表）→ 才换 `_currentDriver`。纯逻辑 `Modeling/DriverSwitchImpact.cs`，执行在 `GlobalVarItemSet.ClearAllAddresses` / `MonitorItemSet.RemoveElementItems` / `IoPointSet.ClearAll`（§7.6）。⚠️ 取消要回填下拉框并罩 `_revertingDeviceType`；清理必须在 `_currentDriver` 赋值之前 |
| 总览状态格式 / 底栏文字颜色 | 窗口 `BuildConnectionDetail()` 拼串 → `Ui/StatusBarPresenter.cs`（`Set` / `RefreshDetail` / `ReapplyTheme`）；窗口侧 `SetStatus` / `UpdateOverviewStatus` 只是薄包装 |
| 底栏布局/对齐、最大化被任务栏遮住 | 主体 Grid 末尾的底栏 `Border`（§3.5，**改前必读**）；`Ui/MaximizedInset.cs`（§3.6） |
| 自动重连退避/触发/取消 | `Polling/ReconnectPolicy.cs`（退避表）+ `Polling/ConnectionSession.cs`（目标/计数）+ 窗口 `StartReconnect` / `CancelReconnect`（§6.1） |
| **IO 灯色 / 点位集 / 分页 / 导入 / 持久化** | `IoPoint.StateBrush` + `InputOnBrush` / `OutputOnBrush` / `OffBrush`（`IsOutput` 在 `TryCreatePoint` 定）；`Io/IoPointSet.cs`（`Rebuild` / `TryCreatePoint` / `NormalizePointName`，按地址升序 → `io-comments.json`）；`Io/IoPager.cs`（`RowsPerColumn` / `ColumnCount`）；`Io/IoTableImporter.cs` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格） |
| IO 采集范围 | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| **自由监控读取 / 轮询入口** | `MonitorItemSet.BuildReadPlan`（按 `ModbusArea` 分组 + 地址连续性分块）+ `ApplyBlock`（偏移切片 + 格式化回填）；`PollOnceAsync` 开头按 `_freeViewActive` 分流 → `PollFreeMonitorAsync`（按区选 FC01/FC03/FC04，单块失败不断线） |
| **系统变量（P3.5 只读 / P2.3 监控）** | `Modeling/SystemVariable.cs`（17 项清单）+ `SystemVarRow.cs` + `SystemVariableValues.cs` + 窗口 `AcqView_Checked` / `RefreshSystemVarValues` / `SyncSystemVarTimer` / `IoView_Checked`（§5.3 / §5.4；无编辑入口） |
| **PLC 状态机（P1.2）+ Error / Fault 指示灯** | `Expressions/` + `Modeling/MachineState.cs`（布局/模型/持久化 + `MachineStateLamp` + `MachineStateLayout.Lamps` / `LampDotSize` / `LampFontSize`）+ `Ui/MachineStateView.xaml(.cs)`（`BuildLamps` / `ApplyLampState` / `LampVisual`）+ `Ui/MachineStateEditDialog.cs` / `MachineLampEditDialog.cs` + 窗口 `RefreshMachineStates` / `SyncMachineTimer`（§5.5） |
| **全局变量（P3.3，只管理定义）** | `Modeling/GlobalVariable.cs`（模型/规则/持久化/`GlobalVariableSet.Move`）+ `GlobalVarItem.cs`（`GlobalVarItemSet.Move`）+ 窗口 `GlobalVar*`；**排序** = 表头 ↑↓ 作用于选中行（§5.2；不读不写）。⚠️ **改名会连带改所有引用**（`VariableRenamer` + 四个集合的 `RenameVariableReferences`，见 §5.2.2） |
| **数据采集（P3.1）** | 模型 `Modeling/AcquisitionTask.cs`（+ `AcquisitionTaskRules`）+ 行包装 `AcquisitionTaskItem.cs` + 对话框 `Ui/AcquisitionTaskDialog.cs`；执行与落盘 `Modeling/AcquisitionCsv.cs` + 窗口 `RunAcquisitionTick` / `RefreshAcquisitionGlobals`（50ms 常驻定时器，**不随页面可见性停**，§5.7） |
| **采集任务「配置错误」关开关/拦截** | 判据 `Modeling/AcquisitionTaskCheck.cs` + 行属性 `HasConfigError` / `EffectiveEnabled`（**必须可写**）/ `CanToggleEnabled`；执行侧只判 `EffectiveEnabled`（§5.7） |
| **采集「执行完成输出变量」** | 模型 `AcquisitionTask.CompletionVariableName` + 规则 `AcquisitionTaskRules.TryValidateCompletionVariable`（对话框与配置检查共用）；执行侧 `QueueCompletionWrite` / `DrainCompletionWritesAsync`（先回填内存、有地址再 FC06/FC16；**固定 `DisplayFormat.Dec`**）；1=正常、2=异常，固定不可配（§5.7） |
| **数组只能引用元素（带下标）** | 统一出口 `Modeling/VariableName.cs`（拆写法）+ `VariableReference.cs`（查变量表 / 下标范围 / **变量自身 `ResolveError`** / `Describe` / `ReadValue`）。数组整条（`var1`）在**所有**引用框一律被拒；正确写法 `var1[0]`（0 基）。词法器把 `[数字]` 并入变量名；`TryValidateNames` 改收「返回原因」的委托；`GlobalReadPlan.Apply` 逐元素回填；`AcquisitionWrite.TryBuild(..., elementIndex, ...)` 按元素类型编码（§5.2.3） |
| **表达式结果必须是 BOOL** | 三个表达式入口保存时多一道静态校验：`CompiledExpression.TryValidateResultType`（类型表来自 `VariableReference.DescribeType`）。`INT var` 单独当条件原先**存得进、永远不生效**（框不亮 / 任务不触发），现在当场拦下，文案统一 `表达式不合法`（`CompiledExpression.NotBoolean`，不带位置/变量名）。顺序 **语法 → 名字 → 类型**；推不出的放行（§5.2.3） |
| **常驻轮询三清单** | `_machineGlobals`（状态机，跟可见性）/ `_acquisitionGlobals`（采集，常驻）/ `_scannerGlobals`（扫码器触发表达式，常驻，2026-11-11）。`RefreshScannerGlobals` 在扫码器表达式提交 / 增删 / 启停 / 变量表变化 / 启动时重算；`RefreshAfterVariableChange` 必须重算后两份（存的是对象引用，删后同名重建会读旧对象）并 `RefreshAllScannerItemStates()`（§5.7） |
| **采集「写入任务」真正下发** | `Modeling/AcquisitionWrite.cs`（TryBuild / **ApplyToMemory** / DescribeFailure）+ `RunWriteTaskAsync`；**`CannotAddress`=无地址 → 只回填内存、不发报文**（2026-11-05，早期是静默跳过导致变量永不更新）、失败不短路；CSV 只记写入变量回读值。**只读区只拦「写」不拦「映射」** —— 判定唯一来源 `GlobalVariableStore.IsReadOnlyElement`（§5.7） |
| **扫码器（P4.5 + P3.5 合并一页）** | `Scanners/`（定义+规则+`scanner.json` / 裸 TCP 收帧 / 连接身份 / 上升沿）+ `MainWindow.Scanner.cs`（`Scanner*` 全部处理器 + 50ms 判定节拍 + 每台独立重连）（§5.9）。⚠️ **触发测试**（§5.9.9）：按钮在「触发请求内容」右边，`ScannerTestTrigger_Click` + `Scanners/ScannerTestResult.cs`（文案纯逻辑）；**不写变量、不写日志**，弹框看完即走 |
| **扫码器触发测试布局** | 触发行用 `StackPanel` + 输入框定宽 200，**靠把标签从 168 收窄到 140 腾空间**（两列共省 56 DIP）—— 这样五个输入框才能精确等宽对齐。⚠️ 别改成 `DockPanel`（按钮能显示但输入框会被撑成 236、与其余行不齐）。⚠️ 1536 px 窗口下余量仅约 13 DIP，**再往右加任何东西都会溢出**（§5.9.9） |
| 采集跟随可见性（三分段统一） | `_ioPageActive`（页面级）+ `_freeViewActive` / `_yViewActive` → `PollOnceAsync` 开头；详见 `Design.md` §5.6 |
| **自由监控写入（FC05/06/16）** | `MonitorItemSet.TryBuildWrite`（文本 → `WritePlan`）+ `ValueCodec.TryEncodeRegisters` / `EncodeStringFixed`；`MainWindow.Monitor.cs` 的 `CommitMonitorValueAsync` → `WriteToDeviceAsync`；报文层 `WriteSingleCoilAsync` / `WriteSingleRegisterAsync` / `WriteMultipleRegistersAsync` |
| **写入的文本解析与范围校验** | `ValueCodec.TryParseIntegerLiteral`（DEC 支持 `0x`/`0b` 前缀与负号）+ `CheckIntegerRange`；**校验必须在位宽截断前**（`-32769` 截成 `0x7FFF` 会变成合法值） |
| 改 IO 行高/列宽、加分段切换 | `IoItemTemplate` 的 `Height`/`Margin`；`IoColumnsGrid`/`YIoColumnsGrid`（4×`*`）；`IoSegmentRadioButton` + `XViewSegment` / `YViewSegment` / `FreeViewSegment` + `IoView_Checked` |
| **自由监控增删改/校验** | `MonitorItem.cs`（`MonitorItem` / `MonitorItemSet`）+ `MainWindow.Monitor.cs` 的 `Monitor*` 处理器（§5.1） |
| **账户与权限（P 权限）** | `Modeling/UserSession.cs`（`UserRole` / `UserRules`：密码 `Admin8080`、`CanWriteMonitor` 唯一出口、`TryLogin`）+ `Ui/LoginDialog.cs` + `MainWindow.Account.cs`（`_currentRole` / `AccountButton_Click` / `MonitorValueTextBoxReadOnly` 依赖属性）。置灰走 `MonitorValueTextBox` 的 **Style.Triggers** 绑窗口 DP，**不要逐行设 IsEnabled**（行在 DataTemplate 里会重建，逐个设必然漏）。入口在导航底部 `AccountButton`（`AccountButton` 样式：头像 18 + Padding 10，**比 `NavRadioButton` 紧凑**，否则「技术员」被 112 DIP 的导航列截断；分隔线在它**之后**、设置之前） |
| 元件名称 → 符号+编号、类型候选 | `CommDriver.TrySplitName`（与 `TryResolve`、自由监控共用）；`CommDriver.AreasOfElement` + `DataTypes.CoilTypes` / `RegisterTypes` |
| 监控项持久化 | `DataPointStore`（`data-points.json`）；模型 `Modeling/DataPoint.cs` |
| **配置落盘 / 容错读 / 写失败可见（改任何 `*Store` 必读）** | 一律走 `Modeling/AtomicFile.cs`：写走「临时文件 → `Flush(flushToDisk)` → `File.Replace` 保留 `.bak`」；读走「整体失败 → 逐元素抢救 + 原文件备份 `.bad-时间戳`」。`ThemeManager.Save` 失败经 `SettingsSaveFailed` 上浮到运行日志。**不要再用 `File.WriteAllText` 直写配置** —— 见 `AGENTS-细节.md` §13 |
| **字节顺序（字序/字符串序）** | `Modeling/ByteOrderSettings.cs`（三套值）+ 设备连接页三个下拉框 + `ApplyByteOrders` / `SyncByteOrderCombos`（§8.1）；读取用 `MonitorItemSet.ByteOrders` |
| **类型值编解码与显示格式化** | `Modeling/ValueCodec.cs`（入参传字节顺序，不读全局状态） |
| **ARRAY 数组类型（P6.4）** | `Modeling/DataTypeEnums.cs`（枚举，零依赖）+ `ArrayRules.cs`（声明规则，零依赖）+ `ArrayValueCodec.cs`（逐元素寻址/编解码）+ `Ui/ArrayDeclareDialog.cs`；子行模型 `MonitorElement` 与 `TryBuildElementWrite` 在 `MonitorItem.cs`。⚠️ 显示名写 `INT[20]`（元素类型+个数）、**不做整组聚合**、**只能对元素写**、占用按「个数 × 单元素单元数」算 —— 见 §5.2.3 |
| Modbus 报文/超时、心跳 | `Drivers/ModbusTcpClient.cs`（TCP）/ `Drivers/ModbusRtuClient.cs`（串口 RTU）；**两者共用** `Drivers/ModbusPdu.cs`（PDU）与 `Drivers/IModbusClient.cs`（接口）；`Drivers/ModbusHeartbeat.cs` → `RunAsync`、`HeartbeatIfIdleAsync` |
| **串口 Modbus RTU（P4.4，2026-11-18）** | `Drivers/ModbusRtuClient.cs` + `Drivers/Crc16.cs`（0xA001 / 初值 0xFFFF / **帧尾低字节在前**）+ `Drivers/SerialSettings`（默认 **9600 8N1**，`Describe()` 出 `COM3 9600 8N1`）。⚠️ **RTU 没有长度字段**，收帧靠「站号 + 功能码」推算（`ReadFrame`）；⚠️ **站号参与 CRC**；⚠️ **站号 TCP/RTU 共用**一个 `StationNumber`。UI 在设备连接页 `SerialOptionsPanel`（与 `EthernetOptionsPanel` **互斥显隐**）；**连接按钮两个**（`ConnectButtonEthernet` / `ConnectButtonSerial`，各贴自家「刷新」右侧，状态必须成对走 `SetConnectButtonText`/`SetConnectButtonEnabled`）+ `Net/SerialPortScanner.cs`（下拉显示「COM4 · 设备名」，设备名取自注册表、零依赖；⚠️ 下拉装的是对象，取端口名走 `SelectedPortName()`）+ `ScanSerialPorts` / `SerialSetting_Changed` / `ReadSerialSettings`；**协议类型由连接方式唯一决定、下拉只列对应一项**（`SyncProtocolOptions`；⚠️ 启动时要在构造函数里显式调一次，条目是运行时填的）。持久化 `ThemeManager.Serial*`。✅ **真机验收通过**（汇川 Easy @ COM4，19 项）＋ 离线 `56 项` —— 见 `AGENTS-细节.md` §8.3（含 §8.3.2b 写应答 8 字节的坑） |
| 轮询与断线逻辑 | `Polling/ModbusPoller.cs` 的 `PollLoopAsync` / `PollOnceAsync` + `Disconnect(status,color)` |
| **IO 合法异常码不再断开（连续 5 次才记日志）** | `PollOnceAsync` 的 `catch (ModbusResponseException)` + `ConsecutiveExceptionThreshold` + `_blockExceptionStreaks`；告警通道 `ICollectorHost.ReportCommunicationWarning` → 窗口 `ReportCommunicationWarning`（§5，证据 `Design.md` §10.2） |
| 网卡扫描/绑定/记忆 | `Net/NicScanner.cs`（`NicInfo` / `Scan` / `IndexOf` / `RestoreIndex`）；窗口侧 `ScanNetworkAdapters` / `TargetNicComboBox_SelectionChanged` / `ConnectButton_Click`（§3.4、§8.2） |
| **日志编排 / 落盘 / 颜色** | `Ui/DeviceLogPresenter.cs`（`Append` / `Clear` / `RefreshThemeColors` / `ToFileLevel`）+ `Logging/DeviceLogFileWriter.cs` + `LogEntry`（`DeviceLogLevel` → `LevelBrush`）/ `DeviceLogItemTemplate`；独立验证 `LoggingCheck`（§7.1） |
| 断开原因归类 | `Logging/DisconnectReasonClassifier.cs`（规则本体，纯函数、可独立断言）；窗口侧 `ClassifyDisconnectReason` 只是薄包装（为满足 `ICollectorHost`） |
| **单实例保护（每台机器一个）** | `Startup/SingleInstanceName.cs`（命名）+ `SingleInstanceGuard.cs`（抢锁 / 异常）+ `WindowFinder.cs`（唤起已有窗口）；接线在 `App.OnStartup` 最开头、`OnExit` 释放。⚠️ 用 `Global\` 不用 `Local\`、⚠️ `createdNew` 不能判重 —— **改前必读细节 §14** |

## 11. 版本控制

- git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`；身份为仓库级配置（`G.Sheng`）
- **提交信息用中文**；提交前先 `git diff` 确认范围，**不要整文件盲加**（会把别人的 WIP 一起带走）
- **不要自动提交**（2026-10-30 用户定案）：改完就停，等用户看过再决定是否提交。
- **提交前的验证只跑两样（2026-10-29 用户定案）**：`dotnet build DSH_Modbus采集工具.sln` + `.tmp/ExpressionCheck`（跑 exe，不用 `dotnet run`），详见 §2。

## 12. 发布与交付

> **这一章已整段移到 [AGENTS-发布.md](AGENTS-发布.md)。**
> 内容：框架依赖发布的产物构成 / 发布命令 / 目录可写性验证 / 覆盖更新只替换 exe 与其配置 /
> 目标机 .NET 8 Desktop Runtime 前置条件。
>
> 要发版、改发布参数、或排查「现场机双击没反应」时先打开那份。
