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
  系统时间写入 `obj/.../BuildInfo.g.cs`。详见 §7.3

## 2. 目录结构

```
DSH_Modbus采集工具/
├── App.xaml / App.xaml.cs      # 入口（OnStartup 里先 ThemeManager.Initialize）
├── AssemblyInfo.cs             # 仅供 ThemeInfo 特性等程序集级声明（无逻辑）
├── AppInfo.cs                  # 项目信息（P5.3）：版本号 / 构建日期 / 运行环境（只读程序集元数据）
├── MainWindow.xaml             # 全部 UI（单窗口，无 UserControl 拆分）
├── MainWindow.xaml.cs          # 交互逻辑（见 §4 分工）
├── MonitorItem.cs              # 自由监控项：DataPoint 的界面包装 + 集合（增删改/落盘/解析）
├── WritePlan.cs                # 一次写入的报文参数（FC05/06/16 三选一）；两条写入路径共用
├── GlobalVarItem.cs            # 全局变量：GlobalVariable 的界面包装 + 集合（增删改/落盘）
├── NumberInputDialog.cs        # STRING 长度输入弹窗（选 STRING 类型时用）
├── XlsxIoTableReader.cs        # xlsx 点位表解析（零依赖 zip+XML）
├── Themes/                     # 主题（P5.1）
│   ├── Light.xaml              # 浅色字典（50 键）；值 = 主题化前的字面值
│   ├── Dark.xaml               # 深色字典（同 50 键）
│   └── ThemeManager.cs         # 换字典 / 跟随系统 / 键一致性校验 / app-settings.json
├── Polling/                    # 采集引擎与连接状态（不含 WPF 类型）
│   ├── ICollectorHost.cs       # 宿主契约：周期 / 监控集 / 可见性快照 / 连接身份 / 回 UI / 断线
│   ├── ModbusPoller.cs         # 轮询编排（IO 分块读 + 自由监控读 + 断线通知）
│   ├── ConnectionSession.cs    # 连接身份与生命周期（客户端 / 目标 / 在途取消源 / 重连计数）
│   ├── ReconnectPolicy.cs      # 退避策略（1→2→4→8→10，封顶；纯函数）
│   └── PollSnapshot.cs         # 可见性快照（页面 / 自由监控 / 当前页 / 周期）
├── Io/                         # IO 点位集（不含 WPF 类型，分页只做数值运算）
│   ├── IoPointSet.cs           # 注释表读写 + 名称归一化 / 地址换算 / 分组排序 / 导入校验
│   ├── IoPager.cs              # 分页纯计算（页数 / 夹取 / 切片 / 翻页 / 页号文本）
│   └── IoTableImporter.cs      # xlsx → 注释表 + 失败明细
├── Modeling/                   # 数据类型系统（P6.4 数据模型层）
│   ├── DataTypes.cs            # DataType / DisplayFormat 枚举 + DataTypeSpec 规格表
│   ├── ValueCodec.cs           # 原始寄存器 ↔ 类型值 编解码 + 显示格式化
│   ├── ByteOrderSettings.cs    # 三套字节顺序（整数 / 浮点 / 字符串）的值类型（P4.3）
│   ├── DataPoint.cs            # 监控项模型 + data-points.json 持久化 + 驱动校验解析
│   ├── GlobalVariable.cs       # 全局变量模型 + 变量名规则 + GlobalVar.json 持久化（值不落盘）
│   ├── CompletionSource.cs     # 变量名补全的纯逻辑：取词 + 筛选 + 候选表（P6.5，不含 WPF）
│   └── GlobalReadPlan.cs       # 全局变量读计划 + 回填（按可见页面采集，P1.2，不含 WPF）
├── Logging/                    # 日志落盘与文案规则（不含 WPF 类型）
│   ├── DeviceLogFileWriter.cs  # 按天追加纯文本 .log + 跨天换文件 + 失败自愈
│   └── DisconnectReasonClassifier.cs  # 异常原文 → 简短中文（底栏与日志共用）
├── Net/                        # 网卡枚举与选择记忆（不含 WPF 类型）
│   └── NicScanner.cs           # 网络接口 → NicInfo + 「上次选择」还原优先级
├── Ui/                         # 窗口呈现辅助（引 WPF，但不碰业务）
│   ├── MaximizedInset.cs       # 最大化溢出补偿：物理像素测量 + 按 DPI 内缩根元素
│   ├── WindowCorners.cs        # 窗口圆角 + 边缘线（DWM 绘制，见 §3.8）
│   ├── StatusBarPresenter.cs   # 底栏：状态文字 / 状态点颜色 / 右侧连接参数
│   ├── DeviceLogPresenter.cs   # 运行日志：内存集合 + 上限裁剪 + 落盘旁路 + 级别映射
│   └── CompletionPopup.cs      # 补全下拉：Popup 绘制 + 键盘/鼠标 + 输入法抑制（P6.5，见 §5.6）
└── Drivers/
    ├── CommDriver.cs           # 模型：ModbusArea / NumberBase / AddressArea / CommDriver（含字节顺序枚举）
    ├── DriverRegistry.cs       # 品牌地址数据表（含各品牌字节顺序约定）
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（MBAP + FC01/02/03/04 读 + FC05/06/16 写 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```

> **不引用 WPF 的层**（可脱离界面阅读/验证，也能被独立工程链接源码断言）：`Drivers/`、`Io/`、
> `Logging/`、`Modeling/`、`Net/`、`Polling/` —— 这 6 个目录下**没有任何** `using System.Windows*`。
> 控件写入（分页控件显隐、绑列、弹窗、`MessageBox`）一律留在 `MainWindow.xaml.cs`。
> 新增逻辑时按同一原则归位。
>
> **引 WPF 的层**：`Ui/`（窗口呈现辅助，不碰业务）与 `Themes/`（`ThemeManager` 要换资源字典），
> 以及根目录的 `MainWindow.xaml.cs` / `MonitorItem.cs` / `NumberInputDialog.cs` / `App.xaml.cs`。
> ⚠️ `MonitorItem.cs` 是「`DataPoint` 的界面包装」、碰 WPF 属正常，但它在**根目录**而不在 `Ui/`
> —— 判断一个文件该不该碰 WPF 看**所在目录**，不要以为「根目录都不碰」。

> `ModelingCheck/`（仓库根，**不属于本工程**）是 P6.4 的独立验证工程：用真机实测数据对编解码做断言，不参与主程序构建。
> `LoggingCheck/`（同上）是 P5.2 的独立验证工程：链接 `DeviceLogFileWriter.cs` 与 `DisconnectReasonClassifier.cs` 源码，断言逐字符行格式 / 跨天换文件 / 续写 / 容错 / 断开原因归类，**44 项**，不参与主程序构建。

> `.tmp/` 下另有一批**一次性**验证工程（**未提交**，随用随弃）。**断言型**（自带 `Check()`，直接 `dotnet run` 即出结论）：
> `ConnCheck`（退避策略 + 连接身份 / 两种断开语义，**35 项**）、`DeviceLogCheck`（日志编排：上限裁剪 / 级别映射 / 共用时间戳 / 清空 / 换主题重建，**23 项**）、`NicCheck`（网卡选择还原优先级，**17 项**）、`PollerCheck`（IO 轮询异常码策略，**10 项**）。
> **探针型**（无断言，需传参数人工读输出）：`IoCheck`（接 xlsx 路径，打印 `IoTableImporter` / `IoPager` 的解析与分页结果）、`FakeSlave` 与 `E2ESlave`（端到端验证用最小假从站）。
> 它们验的是「改一次就够」的行为，不值当长期维护；要重跑时会话记录里有完整源码。

## 3. 页面与 UI

单窗口两行布局：48px 标题栏 + 主体（导航 224 列 + 内容），底部另有一条**全局状态栏**（30px，五个页面都可见）。

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
| `OverviewNav` 总览 | `OverviewPanel` | 视图切换在**页头右侧下拉框**「当前视图」（`FormComboBox`）：「PLC状态机」（P1.2，见 §5.5）/「生产数据」（占位）；卡片内只有画布 + 底部运行日志卡（状态显示已移至全局底栏，见 §3.5） |
| `StatusInfoNav` 状态信息 | `StatusInfoPanel` | IO 监控（**四分段**：输入 X / 输出 Y / 自由监控 / 系统变量）+ 分页 |
| `DeviceConfigNav` 设备配置 | `DeviceConfigPanel` | 连接表单 |
| `AcquisitionConfigNav` 采集配置 | `AcquisitionConfigPanel` | **三分段**：「采集数据」（P3.1 骨架，采集导出未实现）/「全局变量」（P3.3，见 §5.2）/「系统变量」（P3.5，只读清单，见 §5.3） |
| `SettingsNav` 设置 | `SettingsPanel` | 显示外观（P5.1）+ 日志保存（P5.2）+ 项目信息（P5.3） |

切换逻辑：`NavRadioButton` 样式里的 `EventSetter Checked=Nav_Checked`，按 sender 显隐面板。

### 3.2 设计器预览

五个面板同处一个 `StackPanel`，运行时由 `Nav_Checked` 切换，而**设计器只认 `d:Visibility`**（`d:` 在 `mc:Ignorable="d"` 中，不影响编译与运行）。

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
| `FormComboBox` | 设备配置页下拉框：**胶囊**（灰底 + 圆角 6 + chevron），字号 14／高 26。默认 `BorderThickness=0`（无框线）；模板已 `TemplateBinding` `BorderBrush`/`BorderThickness`，个别位置需要框线时自己设 1 即可（总览页「当前视图」就是） |
| `FormComboItem` | 表单下拉项容器（含 `ItemContainerStyle`，勿漏） |
| `FormTextBox` | 设备配置页输入框：**框线**（白底 + `Brush.Input.Border` 圆角 4），字号 14／高 26 |
| `MonitorInlineComboBox` / `MonitorTypeCombo` | 自由监控行内下拉框：胶囊，字号 12／高 22 |
| `MonitorComboItem` | 行内下拉项容器 |
| `MonitorInlineTextBox` / `MonitorValueTextBox` | 自由监控行内输入框：框线，字号 12／高 22 |
| `AcqRowIconButton` | **采集任务组行内图标按钮**（… ↑↓）：22×22，悬停中性灰底 + 深字。**刻意不复用** `MonitorIconButton`（悬停红＝删除）和 `MonitorHeaderIconButton`（悬停主色＝新增）—— 同一行里混两种语义会读错 |
| `AcqRowDeleteButton` | 任务组行「删除本组」×：`BasedOn` `AcqRowIconButton`（尺寸/字体全继承），**只把悬停换成实心红**（`Brush.Danger.Fill.Hover` + 白字），与全局变量行、监控行的行尾删除一致 |
| `AcqEnableSwitch` | 采集任务组「启用」开关，`BasedOn` `ToggleSwitch`，只补「文案随 `IsChecked` 变」（开/关）。⚠️ 必须在 `Style.Triggers` 里换 `Content`，模板触发器压不过局部值 |
| `AcquisitionTaskRowTemplate` | 采集任务组行（7 列：#/名称/类型/执行方式/启用/共N项/… × ↑↓）+ 展开的数据项子表（浅底块） |
| `AcquisitionItemRowTemplate` | 采集数据项行（序号 / 项名称 / 变量名称 / **数据类型只读** / 删除） |
| `LogClearButton` | 日志卡「清空」按钮 |
| `DeviceLogItemTemplate` | 日志行（时间戳灰 + 消息按级别着色） |
| `DeviceLogListStyle` | 日志列表（透明底/无边框/无选中底色） |
| `ScrollBar` | 细滚动条（10px） |

### 3.4 页面细节

- **总览**：顶部仅「总览」标题（原状态卡已移除，连接状态统一在全局底栏）。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**，另两页照滚。
- **状态信息**：见 §5 IO 监控 / §5.1 自由监控。
- **设备配置**：连接方式 / 协议 / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮；**设备通讯区域**另有左「字节顺序」3 个下拉框（32 位整数 / 32 位浮点 / 字符串）+ 竖分隔线 + 右「地址表元件」（见 §8.1）。连接中锁定表单（`SetInputsEnabled`）——**例外是三个字节顺序下拉框**：它们不改报文、不改连接参数，只决定怎么解释寄存器，改完下一拍即生效，锁上反而挡住现场纠正（见 §8.1）。串口模式仅隐藏以太网面板，**功能未实现**。

**连接参数会被记住**（`app-settings.json`）：`TargetNic` / `TargetIp` / `TargetPort`，下次启动回填。

- IP / 端口**失焦才落盘**（`ConfigInput_LostFocus`）：这两个框还挂着 `TextChanged`（只用于刷新底栏概览），逐字符写盘会把 `192.168.1.` 这种半截内容存进 json
- 端口非法（空 / 非数字 / 越界）时**保留上次的有效值**，不拿垃圾覆盖
- 存**空串 / 0** 表示「没保存过」→ 界面保持 XAML 默认值（`192.168.1.100` / `502`）。旧 json 缺字段即为此态，无需迁移
- 点「连接」前焦点会先离开输入框，故 `LostFocus` 一定先于 `Click` 跑，不会出现「改了 IP 直接点连接却没记住」

**目标网卡**（`TargetNic`）的细节：

- 存的是**网卡名**（如 `WLAN`），不是下拉框显示文本 `WLAN(192.168.1.15)` —— 文本里嵌着 IP，而同一块网卡换个网络 IP 就变（笔记本的 WLAN 在家 `192.168.1.x`、在公司 `10.0.0.x`），按文本匹配**恰好在最需要它的场景下失效**
- 还原优先级：**本次扫描前的选择 → 上次运行记住的 → 第 0 项**
- ⚠️ **扫描过程必须整体罩在 `_restoringNicSelection` 里**：`Items.Clear()` 在原本有选中项时就会触发 `SelectionChanged`，只罩最后那次赋值挡不住。挡掉的意义不只是"别写盘"—— 若某块网卡临时消失（VPN 断开 / 网线拔掉），扫描会回退到第 0 项；把这次回退也写进偏好，用户原先的选择就被**悄悄抹掉**了。实测：预置一块不存在的网卡名，启动后下拉框回退到第 0 项，而 json 里仍是原值
- 为什么必须持久化：`ConnectButton_Click` 会按选中项 `new IPEndPoint(nic.Ip, 0)` **绑定本地网卡**。绑错网卡（如绑到 Tailscale 却去连 `192.168.1.100`）会立刻报 `WSAENETUNREACH`，界面上显示成「连接失败：… 网络不可达」—— 看着像网络故障，其实是**上次的选择没被记住**。自动重连不受影响（`ReconnectTarget` 已快照本地端点），**只有启动后的第一次连接会踩到**
  - ⚠️ **同网段双网卡时症状会变**（2026-10-16 真机实测）：本机 `以太网 192.168.1.80` 与 `WLAN 192.168.1.15` **同为 `192.168.1.x/24`**，而 PLC 只从有线侧可达。此时选错网卡**不会**报 `WSAENETUNREACH` —— 路由表认为该网段直连、SYN 正常发出，只是发到了没有 PLC 的那块网卡，于是表现为**连接超时**（约 3s）。故**「连接超时」在多网卡现场不能直接推断为「PLC 没响应」**，先确认 `Find-NetRoute -RemoteIPAddress <PLC>` 选中的网卡与下拉框一致

### 3.5 全局底栏（连接状态）

30px 高，**五个页面都可见**（在面板之外，不随导航切换显隐）。

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

五个页面的标题与所有卡片都挂在**同一个** `ContentScrollViewer` 下（并列兄弟节点），
故左右留白由它一个元素决定，改一处即五页同步：

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

### 3.8 窗口圆角与边缘线（DWM 绘制）

窗口用 `WindowChrome` 自绘标题栏，且 `GlassFrameThickness=0` + `NonClientFrameEdges=None`，
**系统既不画边框也不画圆角** —— 窗口贴在桌面上没有边界。两件事都交给 DWM 做，在
`Ui/WindowCorners.cs`（`WindowCorners.Apply(this)`，在 `Window_Loaded` 里调一次）：

| 属性 | 值 | 作用 |
|---|---|---|
| `DWMWA_WINDOW_CORNER_PREFERENCE` (33) | `DWMWCP_ROUND` (2) | 系统标准圆角，随 DPI 缩放 |
| `DWMWA_BORDER_COLOR` (34) | 主题 `Brush.Window.Border` | 边缘线颜色（浅 `#C8C8C8` / 深 `#4A4A4A`） |

- ⚠️ **不要在 XAML 里自绘描边**（已试过、已删）：开启 DWM 圆角后系统会自己在最外层补一圈 1px 边框，
  自绘那条会被盖住（实测边缘色变成 DWM 的 `#B5B5B6` 而不是设的 `#C8C8C8`），
  圆角处还会露出两道错开的弧线。**两件事都交给 DWM，圆角与边框才同心同半径。**
- 最大化时 DWM 自动收掉圆角、让边框贴边，**不需要**在 `Window_StateChanged` 里手工同步任何值
- ⚠️ DWM 要的是 `COLORREF`（`0x00BBGGRR`，**BGR 顺序**），不是 WPF 的 RGB；写反会变蓝边
- ⚠️ 换主题时 `DynamicResource` 换不到 DWM 属性，`OnThemeChanged` 里要调一次
  `WindowCorners.SyncBorderColor(this)`；颜色必须从资源取，不能写死
- 老系统（Win10）上这两个属性号不认识，`DwmSetWindowAttribute` 返回非 0，此时**安静放弃** ——
  窗口保持直角无边框，不影响功能

## 4. 代码分工（MainWindow.xaml.cs）

> `MainWindow.xaml.cs` 仍是本工程最大的单文件（两千行量级；P5 重构抽出两块后，又因
> P2.2 读取/写入与 P5 设置页陆续加回，规模回到重构前）。采集编排已抽到 `Polling/`、
> IO 点位集/分页/导入已抽到 `Io/`，主题在 `Themes/`；其余（连接生命周期 / 心跳 / 导航 /
> 自由监控 / 日志）仍在本文件。**自由监控刻意未抽** —— 它有 12 处直接碰控件，见 `Design.md` §8-1。
>
> ⚠️ **不要在这里写具体行数**：该数字已三次未跟上（曾同时出现 2590 / 2190 / 2187 三个
> 互相矛盾的值），它对判断没有作用，只会下次再骗人一次。

| 区块 | 关键成员 |
|---|---|
| 连接状态机 | `ConnectButton_Click`（表单校验 + 按钮三态）/ `TryConnectAsync` / `Disconnect()` / `Disconnect(status,color)` / `SetInputsEnabled`；**身份与生命周期在 `Polling/ConnectionSession.cs`**（`IsConnecting` / `IsCurrent` / `MarkConnected` / `ReleaseClient` / `ForgetTarget` / 重连计数）+ `RunConnectionTasksAsync` / `IsCurrentConnection` |
| 自动重连 | `StartReconnect` / `ReconnectLoopAsync` / `CancelReconnect`（**循环留在窗口**，逐秒刷状态栏/锁表单是界面驱动）；目标快照 `ReconnectTarget` 与退避表 `ReconnectPolicy` 在 `Polling/` |
| 轮询（入口在本类，实现在 `Polling/`） | `_poller.PollLoopAsync` → `Polling/ModbusPoller.cs`（`PollOnceAsync` 按地址区分块 + 2000 点切窗 / `PollFreeMonitorAsync` 自由监控，按区选 FC01/03/04） |
| `ICollectorHost` 实现 | `PollIntervalMs` / `MonitorSet` / `CaptureSnapshot()` / `IsCurrentConnection` / `RunOnUiAsync` / `NotifyConnectionLost` / `ClassifyDisconnectReason` / `ReportCommunicationWarning`（连续异常码告警，见 §5） |
| 心跳编排 | `HeartbeatLoopAsync`（失败时转 `_poller.NotifyConnectionLost`） |
| IO 点位集（实现在 `Io/`） | `RebuildIoPoints`（薄包装）→ `IoPointSet.Rebuild` / `TryCreatePoint` / `NormalizePointName` / `TryValidatePoint` |
| 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（算术在 `IoPager`；控件写入留本类） |
| 导入 | `ImportIoTableButton_Click` → `IoTableImporter.Read`（`MessageBox` 留本类） |
| **自由监控** | `AddMonitorItemButton_Click` / `RemoveMonitorItem_Click` / `MonitorName_TextChanged` / `MonitorName_LostFocus` / `SyncMonitorTypeWithElement` / `ApplyMonitorRowState` / `RefreshMonitorView` / `FocusMonitorRowName` / `RowOf` / `MonitorValue_GotFocus` / `MonitorValue_LostFocus` / `MonitorValue_KeyDown` / `CommitMonitorValueAsync` / `WriteToDeviceAsync` / `ClassifyWriteError`（见 §5.1） |
| **系统变量（P3.5）** | 清单写死在 `Modeling/SystemVariable.cs`；窗口侧只有 `AcqView_Checked` 切面板 + 构造期一次绑定，**没有任何编辑处理器**（见 §5.3） |
| **全局变量（P3.3）** | `AddGlobalVar_Click` / `RemoveGlobalVar_Click` / `ClearGlobalVars_Click` / `GlobalVarName_TextChanged` / `GlobalVarName_LostFocus` / `GlobalVarAddress_LostFocus` / `GlobalVarComment_LostFocus` / `GlobalVarType_SelectionChanged` / `RefreshGlobalVarView` / `FocusGlobalVarRow` / `AcqView_Checked`（见 §5.2；**只管理定义，不读不写**） |
| 持久化 | `LoadComments` / `SaveComments`（薄包装 → `IoPointSet.Load` / `Save`） |
| 网卡 | `ScanNetworkAdapters`（闸门 + 填下拉框）/ `TargetNicComboBox_SelectionChanged`；扫描与还原规则在 `Net/NicScanner.cs`。选择持久化见 §3.4 |
| 日志 | 窗口侧只留三处**控件**动作：`AppendDeviceLog`（薄包装 → 追加 + `ScrollIntoView` + 设置页失败提示）、`ClearDeviceLogButton_Click`、`RefreshDeviceLogColors`（薄包装）；**编排与规则在 `Ui/DeviceLogPresenter.cs`**（内存集合 / 上限 200 / 落盘旁路 / **界面与文件共用时间戳** / 界面级别 → 落盘级别）；`ClassifyDisconnectReason` 仍是薄包装 |
| 底栏 | `SetStatus` / `UpdateOverviewStatus` / `BuildConnectionDetail`（窗口侧薄包装）→ `Ui/StatusBarPresenter.cs`（规则本体）；`UpdateMaximizedInset` → `Ui/MaximizedInset.cs` |
| **主题（P5.1）** | `Themes/ThemeManager.cs`；窗口侧 `ThemeSegment_Checked` / `SyncThemeSegments` / `OnThemeChanged`（底栏的状态色映射在 `Ui/StatusBarPresenter.cs`）；设置页 `SettingsPanel` + `SettingsNav`（见 §9.2） |
| **日志落盘（P5.2）** | `Logging/DeviceLogFileWriter.cs`（按天追加 / 跨天换文件 / 失败自愈）；窗口侧 `_logWriter` / `LogSaveToggle_Changed` / `SyncLogSaveToggle` / `LogDirectoryTextBox_LostFocus` / `LogBrowseButton_Click` / `LogOpenFolderButton_Click` / `LogResetDirectoryButton_Click` / `ApplyLogDirectory` / `RefreshLogSaveStatus`（见 §7.1） |
| **项目信息（P5.3）** | `AppInfo.cs`（读程序集元数据）+ csproj 的 `VersionPrefix`/`VersionSuffix` 与 `WriteBuildDate` 目标；窗口侧 `RefreshProjectInfo`（见 §7.3） |
| 关键字段 | `_session`（连接身份与生命周期）/ `_pollCts` / `_windowClosed` / `_pollIntervalMs` / `_currentDriver` / `_poller` / `_ioSet`（内含 X/Y 点位列表与注释表）/ `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_monitorSet` / `_globalVarSet` / `_logPresenter`（日志呈现器）/ `_logWriter` / `_statusBar`（底栏呈现器）（**哪些是 `volatile` 以上表 §3.1 为准**：`_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` 共 7 个） |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控

- **点位集完全由导入的 IO 表驱动**（不再固定 64 点）；未导入则空列表 + `IoEmptyHint`「尚未导入 IO 表」
- 持久化唯一来源 = `io-comments.json`（`AppContext.BaseDirectory`，**键=点位名、值=注释**）；导入即整体替换并写盘
- 导入校验：**仅接受 X / Y 元件**，地址须落在当前驱动范围内；结果**一律用 `MessageBox`** 汇报（0 项有效则警告框且拒绝替换）
- **分页**：每页 4 列 × `IoPager.RowsPerColumn`(16) = 64 点，X/Y 各自独立页号；单页时整体隐藏。列宽 `*` 等分；行高 22、行距 2、列距 4
- **采集跟随可见性**：只读**当前显示分段 + 当前页**（`_visiblePage` 快照，按所属地址区分块 FC01，同区跨度超 2000 点切窗，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**；断线不清灯色（`IsOn` 保持上次值）
- **三个分段统一「跟随可见性」**（输入 X / 输出 Y / 自由监控）：只有当前显示的才读，离开者**保留上次值、不清空、不断线**。外层另有页面级闸门 `_ioPageActive`（离开状态信息页则全停）。详见 §5.1 与 `Design.md` §5.6
- **卡片内同一时刻只有一条滚动条**：Row=2 的 `Grid` 下，X/Y 共用一个 `IoScrollViewer`（每页固定 64 点，见 §5 分页），自由监控则让 `FreeMonitorPanel` 自己填满该格、只让列表滚动（表头常驻 —— 它是「+」的落点）。⚠️ **列表不要写 `MaxHeight`**：面板整体已绑 `ViewportHeight`，写死高度会让「里层先滚、里层滚不动了外层再滚」，两条滚动条并存（历史缺陷：527f2e2 给自由监控列表套了 `MaxHeight="380"`）
- **合法异常码不算断线**（2026-10-16）：IO 分块读**单独捕 `ModbusResponseException`** 并与成功路径分开 —— 跳过该块、其余块照读、该块的点保持上次值，**不断开也不重连**；某块**连续** `ModbusPoller.ConsecutiveExceptionThreshold`（**5**）次才记一条琥珀「通讯异常」（措辞 `原因（地址 xxx 异常码 N）`），**只记一次、读通即清零**（不这么做，100ms 一拍会让 200 条日志 20 秒内刷满）。**超时 / IO 异常仍照旧断开**。详见 §5.1 / §6 与 `Design.md` §8-12

**灯色（`IoPoint.StateBrush`）**：灭灯 `#C4C4C4`；点亮时**输入 X 绿 `#0B7A0B`、输出 Y 橙 `#FFA500`**。

- 由 `IoPoint.IsOutput`（`init` 属性）区分，创建时在 `IoPointSet.TryCreatePoint` 按点位名首字母定死：`IsOutput = normalized[0] == 'Y'`
- 画刷**按当前主题从 `Io.*` 键取**（上面三个值是浅色主题的值；深色下绿灯提亮为 `#4EC94E`、灭灯为 `#5A5A5A`），取不到时回退到 `InputOnFallback` / `OutputOnFallback` / `OffFallback` 三个 `static readonly` + `Freeze()` 的浅色画刷
- **不要在 `StateBrush` 里 new 画刷**（原实现每次取属性都新建对象）；字典本身有缓存，按键取不产生新对象
- 换主题后靠 `RaiseStateBrushChanged()` 触发绑定重算（`StateBrush` 无 setter）
- XAML 侧不用改：`IoItemTemplate` 的灯仍绑 `Background="{Binding StateBrush}"`

### 5.1 自由监控（P2.2，第三分段）

状态信息页第三个分段（`FreeViewSegment`），列表式监控项：**元件名称 / 数据类型 / 显示格式 / 当前值 / 删除**
（第四分段「系统变量」见 §5.4）。

- **界面采用 Brief「方案 2」**：元件符号与编号**合并为一个输入框**（`NameBox`，如 `HD100`），不再是「符号下拉 + 编号框」
  - 取舍见 `Design.md` §7.1；存储仍是 `DataPoint.Element` + `Index` 两个字段（旧 `data-points.json` 无损）
  - 合并输入 → 拆分的**唯一实现**是 `CommDriver.TrySplitName`（`TryResolve` 与它共用同一份规则）
- **数据类型由元件反推**（方案 1 是反过来的）：`SyncMonitorTypeWithElement` 按 `CommDriver.AreasOfElement` 判断该符号属于线圈还是寄存器 → 线圈只给 `BOOL`，寄存器给 6 种；改元件导致类型不符时自动纠正
  - ⚠️ `AreasOfElement` **只看符号、不看编号**，故编号越界时仍能给出正确的类型候选与错误文案
- **输入校验**：`TrySplitName` 从严（编号必须 ASCII 数字，拒 `"D 100"` / `"D-1"` / 全角数字）
- **校验与提交时机＝失焦或回车**（不是打字时）：输入框**打字途中完全不校验**（需求）—— 一次打完再检查。打字途中若 `SetName` 就落盘，下一拍轮询会拿半成品名称去采集，而磁盘上仍是旧值（内存/磁盘不一致，实测表现为从站读 `800` 而 json 还是 `100`）
  - `CommitMonitorName` 是**唯一提交点**：`MonitorName_LostFocus` 与 `MonitorName_KeyDown` 的 Enter 都转调它。回车**不移走焦点**，故直接跑提交逻辑；随后真正的 LostFocus 会在"没变化"处提前返回，不会重复落盘
  - **名称不合法 → 弹窗**，标题「输入无效」，正文 = **`“{输入内容}”` + 「不是有效软元件或变量」**（如 `“X9”不是有效软元件或变量`，输入为空时不加引号）—— 前缀是用户刚输入的内容（需求），一眼看出被拒的是哪一段。覆盖三种情况：拆不出「元件+编号」、元件不在该驱动、编号越界或八进制非法（如 `X9`）。确认后回退到上次有效名称，**不污染 json**
  - 例外：**是合法变量但不能采集**（内存变量 / 映射地址无效）→ 弹窗「变量无法采集」并给出具体原因 —— 名称本身合法，不该报"不是有效软元件或变量"
  - ⚠️ 曾经的打字中标红（`MonitorName_TextChanged` + `MonitorItem.SetPreviewError`）已按需求**整体移除**，不要再加回来
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

**自由监控里可以监控全局变量**（变量行）：

- 输入框里填**变量名**即可（如 `speed`）；填元件名（如 `D100`）则仍是元件行。
  判定顺序：**先查变量表**，命中即变量行；否则按元件拆分
- 变量行**类型下拉框置灰**（`MonitorItem.IsTypeEditable` = false），显示变量声明的类型 ——
  类型由变量表决定，在监控行里改它等于绕过变量表另立一份定义
- 变量行的类型 / 长度 / 映射地址**每次解析都从变量表现取**（`MonitorItem.ResolveVariable`），
  变量改了类型，监控行自动跟着变，不留过期副本
- 变量**没有映射地址**＝**内存变量**：**可以**放进自由监控，且是**合法行**（不标红）。
  - 它不参与 Modbus 读写：`ReadableItems` 只取 `Address >= 0 && !HasError`，内存变量地址恒为 -1，天然被排除；
    `MarkAllReadFailed` 也要跳过它，否则断线时会给一条根本不读的行标琥珀色
  - **「当前值」格可直接改**（`MonitorItem.TrySetMemoryValue`）：值写进 `GlobalVariable.Value`，
    **一个报文都不发、也不需要连接** —— 故提交逻辑放在 `CommitMonitorValueAsync` 的连接检查**之前**
  - 校验规则与 Modbus 写入完全一致（BOOL 只认 0/1，其余走 `ValueCodec.TryEncodeRegisters`），
    免得同一个值在这里能填、接到地址后却写不进去
  - `MonitorItemSet.TryBuildWrite` 对内存变量直接返回失败（"内存变量不通过 Modbus 写入"），防止被误当成可写地址
  - 值写进**变量**而不是只写本行：监控同一变量的多行保持一致，重新解析时也能取回来
- 变量**改名 / 删除**后，引用它的监控行会标红「变量不存在」——
  窗口在变量的改名 / 改类型 / 改地址 / 删除 / 清空后统一调 `RefreshMonitorRowsAfterVariableChange`
- ⚠️ 变量名与元件名是两个命名空间（`DataPoint.VariableName` vs `Element`+`Index`），
  **不要合并到一个字段** —— 混在一起读代码的人分不清 `D100` 是元件还是恰好叫这名字的变量
- ⚠️ `MonitorItemSet.SetVariableLookup` 必须在 `Load()` **之前**调用，
  否则第一轮会把变量行当成元件行标红

- ⏳ **未实现**：**串口（Modbus RTU）** —— 设备配置页的串口模式只隐藏以太网面板，功能未做

### 5.2 全局变量（P3.3，采集配置页第二分段）

采集配置页分三段：**采集数据**（P3.1 骨架）/ **全局变量**（本项）/ **系统变量**（P3.5，见 §5.3）。
表列：**序号 / 变量名称 / 数据类型 / 映射地址 / 注释 / 删除**。

⚠️ **本页只管理变量定义** —— 不显示当前值、不写入。`GlobalVariable.Value` 仍是内存字段（不落盘），
界面上没有编辑入口。

**但变量值会被采集**（2026-10-21 补，与 IO 监控同一逻辑，见下）：全局变量**本身不轮询**，
只有「当前可见页面用到它」时才采，离开即停。

- **变量名**：C 标识符 `[A-Za-z_][A-Za-z0-9_]*`，最长 **32**，**区分大小写**，**拒绝中文**（`GlobalVariableRules.TryValidateName`）；校验与重名检查都在 `GlobalVariableSet.Add` / `Rename`
  - ⚠️ **不得与系统变量重名**：`sys_True` 等 16 个名字被保留，`GlobalVariableRules.TryValidateName` 直接拒
    （正文 `“sys_True”是系统变量，不能重复定义`）。用 `Ordinal` 精确比对 —— 变量名本身区分大小写，
    故 `sys_true`（全小写）仍允许，不搞「前缀保留」那一套
  - **打字途中不校验**（与自由监控同一节奏）：`CommitGlobalVarName` 是唯一提交点，失焦或回车触发
  - 不合法**弹窗**：标题「变量名称不合法」，正文 = **`“{输入内容}”` + `TryValidateName` / `Rename` 给出的具体规则**（如 `“1abc”变量名称必须以字母或下划线开头`，输入为空时不加引号）—— 前缀是用户刚输入的内容（需求）；重名同样走这条
  - 弹窗后回退到上次有效名称 —— 模型与磁盘里都不留非法变量名
  - ⚠️ 曾经的打字中行内标红（`GlobalVarName_TextChanged`）已按需求移除
- **类型决定地址区**（方案 C，`GlobalVariableRules.CanMapTo`）：`BOOL` → 线圈区；其余 → 寄存器区。实际可写范围由驱动 `AreasOfElement` 决定：**M/Y/S 可读写，X 只读**
- **映射地址可空** = 内存变量：不参与轮询，值由用户手工维护 ——
  放进自由监控后，直接在监控行的「当前值」格里改（见 §5.1）
- **切换数据类型时，仅当存储区变化才清空映射地址**（`GlobalVariableRules.AddressInvalidatedBy`）：
  - `BOOL ↔ 其余类型`（线圈 ↔ 寄存器）→ **清空**，旧地址必然校验失败（`TypeMismatch`）
  - `INT → REAL` 这类**同区**换类型 → **保留**，地址完全可用，清掉只是让用户重打一遍
  - 模型与**控件文本**必须一起清：地址框不是双向绑定（失焦才提交），只改 `Model.Address` 界面还留着旧字
  - 清空发生在「类型已确认」之后 —— 切到 `STRING` 时若用户取消长度输入框，**不清地址**（类型根本没变）
- **STRING 长度弹框与自由监控共用同一个入口**（`AskStringLength`）：标题「字符串长度」、
  提示语「请输入 STRING 的字符数（1 ~ 128）。」、范围 `StringLengthMin` ~ `StringLengthMax`、
  默认值 `StringLengthDefault` 都只在窗口顶部定义一次
  - ⚠️ 两处曾各自内联调用 `NumberInputDialog.Ask`，参数却不同（全局变量写死 `1~255` 与另一套提示语），
    同一个问题在两页得到不同答案。**不要再内联调用**，一律走 `AskStringLength`
  - 初值取该行现有长度（>0 时），重复改类型不丢已有设置（`BOOL` 只 `BIN`、`STRING` 只 `ASCII`）：类型变更时按 `DataTypes.Get(type).DefaultFormat` 自动纠正
  - ⚠️ **不要写死 `Format = DisplayFormat.Dec`**：新建 `BOOL` 变量会立刻校验失败。取默认值一律用 `GlobalVariable.DefaultFormatFor(type)`
- **持久化只存定义**（`GlobalVar.json`）：`[JsonIgnore]` 标记 `Value`，**值不落盘**，每次 `Load()` 重置为 `0`（`STRING` 空串）
- **注释仅给人看**；**与自由监控独立维护**（各用自己的 json）
- **映射地址失焦即校验，失败弹框**（用户定稿）：
  - 软元件与数据类型不符 → 标题「数据类型不符」，正文「软元件与当前数据类型不符合。」
  - 软元件无效（符号不存在 / 编号越界 / 超出地址范围 / 拆不出元件+编号）→ 标题「软元件无效」，正文「“{输入内容}”不是有效软元件」
    （与自由监控 / 变量名两处弹窗同一排版；输入为空时不加引号）
  - **元件符号大小写不敏感**：`y27` 与 `Y27` 是同一个元件。`TryResolve` 拆出符号后统一
    `ToUpperInvariant()` 并**写回 `v.Address`**，界面（失焦后回填）与 json 都存规范大写形式 ——
    与自由监控名称框（`MonitorItem.SetName` 里也做 `ToUpperInvariant`）保持一致
    - ⚠️ 归一发生在 `TryResolve` 内，故**所有**解析入口（新建 / 加载 json / 失焦校验）都受益；
      加载到手工改小写的 json 也能正常解析，下次保存时自动转正
  - 其余（显示格式、STRING 长度、未选设备类型）→ 标题「映射地址无法解析」，正文转达原因
  - 文案本体在 `GlobalVariableRules.DescribeAddressError`（**纯函数**，离线断言直接核对这两句）；
    分类由 `AddressErrorKind`（`None` / `TypeMismatch` / `InvalidElement` / `Configuration`）表达
  - ⚠️ **分类不能靠匹配错误字符串**：`TypeMismatch` 与 `InvalidElement` 的判据是「元件存在、但存储区与类型不匹配」vs「元件本身解析不出来」，
    两者在 `TryResolve` 里走的是**不同分支**（`AreasOfElement` 有候选但 `CanMapTo` 全不通过 = 类型不符）


**下拉框数据源（踩过坑，改前必读）**：

- 类型下拉框放 **`DataTypeSpec` 对象**，不能放 `DisplayName` 字符串 —— `MonitorTypeItemTemplate` 绑的是 `{Binding DisplayName}`，放字符串会让展开列表**整列空白**（只剩选中高亮条）
- 闭合态由 `MonitorTypeCombo` 模板直接读数据上下文的 `GlobalVarItem.TypeDisplayName`，缺这个属性同样显示空白
- 选中处理器要接 `DataTypeSpec`，不是 `string`

**离线断言**：`.tmp/GlobalVarCheck`（**164 项**，未提交），改 `GlobalVariable.cs` / `GlobalVarItem.cs` / `DataPoint.cs` 后跑它；跑前先 `$env:TEMP = "$PWD\.tmp\gv-temp"`。
变量行另有一个 WPF 断言工程 `.tmp/MonitorVarCheck`（**63 项**，未提交；含内存变量可改值、不入读取计划、断线不误标）：`MonitorItem.cs` 用了 `System.Windows.Media.Brush`，故它必须 `UseWPF`；跑前先 `$env:TEMP = "$PWD\.tmp\mv-temp"`。

#### 5.2.1 变量值跟随可见性采集（2026-10-21）

**起因是个 bug**：状态机 9 条表达式全写成 `SysState == 0..8`，但 `SysState` 恒等于初始值 0 ——
画面上永远只有 Stopped 亮，其余 8 个恒灰，**且不报任何错**。原因是 `GlobalVariable.Value`
在整个项目里只有「启动 / 新增时重置为初始值」三处赋值，**没有任何代码从 PLC 读它**。

**用户定案的做法**（比「全局变量统一轮询」轻得多）：全局变量不轮询，但**当前可见页面用到哪几个就采哪几个**，
与 IO 监控同一逻辑。

| | IO 监控（原有） | 状态机变量（本次） |
|---|---|---|
| 何时采 | `PollSnapshot.IoPageActive` + 当前分段 | `PollSnapshot.MachineViewActive` |
| 采哪些 | 当前页点位数组 | 表达式**实际引用到**的全局变量 |
| 分块 | 同区按地址合并、有空洞断开 | 同一套算法（`GlobalReadPlan.Build`） |
| 单块失败 | 不断开连接 | 同 |

**四个文件的分工**：

- `Modeling/GlobalReadPlan.cs`（新）—— 读计划 + 回填，**不引 WPF**，故进 `ModelingCheck` §13（25 项）
- `Polling/PollSnapshot.cs` —— 加 `MachineViewActive` 与 `MachineGlobals` 两个字段
- `Polling/ModbusPoller.cs` —— `PollMachineGlobalsAsync` + `PollOnceAsync` 开头一个分支
- `MainWindow.xaml.cs` —— `RefreshMachineGlobals()`：遍历 9 条表达式的 `UsedNames`，剔除系统变量与不存在的名字、去重

**必须守住的几条**（都是刻意的）：

- `RefreshMachineGlobals()` **只在三处调**：启动载入、表达式被改、变量定义被改。**绝不进每拍** ——
  `CaptureSnapshot` 每拍都跑，必须便宜（只交引用）
- 采集清单**只含真正被用到的变量**：全表都采等于把没人看的地址拖上总线，与「只采可见页」相悖
- **单块读失败保留上次值，不清零**：清零会让 `SysState == 0` 算出「Stopped 亮」，
  看起来像设备真停了 —— 比不刷新更误导。有断言守着（`长度不足时保留上次值（不清零）`）
- `_machineViewActive` 在 `SyncMachineTimer` 里赋值，且必须在**早退之前**，否则「状态没变」时标志更新不到

**端到端怎么验**（`.tmp/machine-poll-e2e.ps1` + `.tmp/MachineSlave`，均未提交）：
假从站记录每一帧的 `(功能码, 起始, 数量)`，然后断言「总览页 D10 在采 / 切到状态信息页停采 / 切回来恢复」。

- ⚠️ **假从站必须绑在应用所选网卡的地址上**（如 `192.168.1.80`）：应用会把本地端点绑到那块网卡，
  绑 `127.0.0.1` 时它连不上，从站一帧都收不到 —— 表现成「代码没跑」，其实只是连不上
- ⚠️ **断言只能数 `FC3 start=10` 那一种帧**：切到状态信息页后应用**仍会**读 IO（FC1 两个块），
  数全部帧会把 IO 采集误判成「还在读状态机变量」（第一版就这么误报了 74 帧）
- ⚠️ 脚本里**不要 `Start-Process` 起从站**：实测会挂住。从站用单独的后台任务先起好

**变量的读写**（需求里的「读变量值按映射地址变化，写变量值即写映射地址」）：

- **写**：自由监控里引用变量的行，写入走**普通元件写入路径** ——
  `item.Model.Address` 是从 `variable.ResolvedAddress` 同步来的，报文直接打到映射地址（FC05/06/16）。
  内存变量（无地址）则走 `TrySetMemoryValue`，只改内存、不发报文、也不需要连接
- **写完不回读**，由下一拍轮询自然刷新（需求选择）。不做本地「假装成功」的改写 ——
  从站拒绝或被程序改掉时，界面才不会显示假值
- **读回时同步到变量本身**（2026-10-21 补）：`MonitorItem.ApplyReadValue` 在更新 `CurrentValue` 的同时，
  变量行还会写回 `GlobalVariable.Value`
  - ⚠️ 不这么做的后果：同一份值在 `CurrentValue` 与 `GlobalVariable.Value` 各存一份，只更新前者的话，
    状态机表达式读到的是**旧值** —— 而且**只在「自由监控可见、状态机不可见」时**露出来
    （那种组合下只有监控行在刷新，`GlobalReadPlan` 不跑）
  - 同步之后「变量只有一份值」才成立，不再依赖哪个页面可见
  - 编辑中（`IsEditing`）仍然整个跳过：用户手上的输入优先，两处都不动

### 5.3 系统变量（P3.5，采集配置页第三分段）

摆出一份**写死、只读**的预定义变量清单（16 项）。
表列：**序号 / 变量名称 / 数据类型 / 注释** —— 比全局变量少了**映射地址**与**删除**两列。

- 清单在 `Modeling/SystemVariable.cs` 的 `SystemVariable.All`：**逐项写死、不落盘、不可增删改**
- 前 5 项 `BOOL`（`sys_True` / `sys_False` / 三个周期时钟），后 11 项 `INT`（7 个系统时钟 + 4 个运行时长）
- **每格都是 `TextBlock`**：没有输入框、没有下拉框，连焦点都拿不到 ——
  只读性由**控件类型**表达，不靠 `IsReadOnly`（后者仍能选中、能弹输入法）
- 数据类型显示名走 `DataTypes.Get(Type).DisplayName`（`BOOL` / `INT`），**不另写字面量**
- 表头右侧只有「共 16 个」计数，**没有** ＋ / 删除全部按钮
- 列宽 34 / 140 / 106 / `*`，行高 30，与全局变量逐值一致；
  ⚠️ 表头与 `SystemVarItemTemplate` 的 `ColumnDefinition` **必须同步改**，否则表头与内容错位
- `sys_DateTime_*` 是 `DateTime`（不是 ~~`DataTime`~~，需求文档原稿的笔误，已定案更正）
- ⚠️ **不要给它加排序 / 搜索 / 编辑**：清单是对外约定，改动等于改协议 ——
  先改 `Brief.md` P3.5，再改代码与断言

**取值**（`Modeling/SystemVariableValues.cs`，纯函数 `Capture(now, uptime)`）：

- **两个时间源是故意分开的**：`sys_DateTime_*` 用墙上时间（`DateTime.Now`，用户改系统时间它就该跟着变）；
  `sys_RunTime_*` 与 `sys_Pulse*` 用**单调时长**（`Stopwatch`，系统校时不能让运行时长跳变、脉冲抖一下）
- `BOOL` 显示 `"1"` / `"0"`（与界面里 BOOL 的 `BIN` 一致），不写 `True` / `False`
- 脉冲：`Pulse100ms` 50ms 翻转、`Pulse1s` 500ms、`Pulse2s` 1000ms；**启动瞬间为 ON**（`ms / 半周期` 偶数即 ON）
- 星期 **1~7**（周一=1、周日=7），不是 .NET 原生的 0~6 —— `IsoWeekday` 负责换
- 运行时长起点 = **程序启动**；秒/分/时各自取模（0~59 / 0~59 / 0~23），**日累加不取模**
- 负数 `uptime` 按 0 处理
- ⚠️ **取值按 `SystemVariableKind` 分派，不是按变量名**：名字改过一次（`sys_true` → `sys_True`），
  行为不该跟着名字变；按名字写 `switch` 的话每次改名都得同步改一处看不见的取值逻辑
- ⚠️ `switch` **不写 `default`**，并用 `#pragma warning disable CS8524` 只压「未命名枚举值」那条噪声告警 ——
  **CS8509 必须留着**：新增一个 `Kind` 却忘了加分支时，它才是那个哨兵

**离线断言**：`.tmp/SystemVarCheck`（**124 项**，未提交），改 `SystemVariable.cs` / `SystemVariableValues.cs` 后跑它；跑前先 `$env:TEMP = "$PWD\.tmp\svc-temp"`。
断言覆盖：16 项规模 / 序号 1..16 / 名称唯一且符合 `GlobalVariableRules` / 逐字名称表 / 类型与显示名 / 注释非空 /
常量 / 三种脉冲的 0·49·50·99·100ms 相位 / 星期 1~7 连续七天 / 零点与年末边界 / 秒分时日的进位 /
负数时长 / 脉冲与运行时长不受墙上时间影响 / 键集合与清单一致 / Kind 无重复且覆盖全部枚举值。

### 5.4 系统变量监控（P2.3，状态信息页第四分段）

状态信息页第四分段（`SysVarViewSegment`），把 `SystemVariable.All` 那 16 项**连值一起**摆出来：
**序号 / 变量名称 / 数据类型 / 当前值 / 注释**。与 P3.5 的定义表相比只多一列「当前值」，其余几何完全一致。

- 行对象 `SystemVarRow`（`SystemVarRow.cs`）：纯展示 —— 只有 `Value` 会变，名称/类型/注释钉在清单上
- 值来自 `SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed)`（见 §5.3）
- **定时器 50ms、`DispatcherPriority.Background`**，只在「本分段可见 **且** 状态信息页可见」时跑（`SyncSystemVarTimer`）
  - ⚠️ **为什么是 50ms 而不是 100ms**：`sys_Pulse100ms` 的半周期就是 50ms，每 100ms 采一次，
    采样点永远落在「50ms 的整数倍、步长 2」那一列 —— 相位固定，读出来恒为同一个值，看上去**卡住不闪**。
    50ms 一拍刚好每半周期采到一次
  - ⚠️ **为什么必须是 Background 优先级**：用 `DispatcherPriority.Render` 时每拍都会牵动渲染调度
    （实测同一刷新频率下 7.6% → 1% 单核）。这只是个「看一眼」的页面，不该抢渲染优先级
  - 同值赋值由 `SystemVarRow.Value` 的 setter 挡掉，不产生绑定通知
- `_runClock` 是 `Stopwatch`，**不是** `DateTime.Now - 启动时刻`：系统校时会让后者跳变
- 表头右侧只有「共 16 个」计数，**没有** ＋ / 删除按钮（清单写死）
- **自由监控不许添加系统变量**（P2.3 需求）：
  - 界面入口在 `CommitMonitorName`：输入框失焦/回车时先查 `SystemVariable.Find`，
    命中就回退并弹「系统变量」标题的窗（正文 `“sys_True”是系统变量，不能在自由监控中使用。`）
  - 为什么必须单独拦：系统变量不在全局变量表里，不拦会一路掉到元件解析，
    报成「不是有效软元件或变量」—— **原因不对**，用户会去改元件名
  - 手工改过的 `data-points.json` 里若留着系统变量名，`ResolveVariable` 也给了同一句错误文案
  - `SystemVariable.Find` **区分大小写**（与变量名规则一致）：`sys_true` 不命中 `sys_True`

**离线断言**：`.tmp/SystemVarCheck`（**124 项**）覆盖取值语义与 `SystemVarRow` 的通知行为（同值不通知、整表推送后与 `Capture` 一致）。

### 5.5 PLC 状态机（P1.2，总览页第一分段）

视图切换是**页头右侧的下拉框**，卡片里只放内容：

```
总览                                    当前视图 [PLC状态机 ▾]
┌ 卡片 ──────────────────────────────────────────┐
│                                                 │
│              画布 / 规划中                       │
└────────────────────────────────────────────────┘
```

- 页头是两栏 `Grid`：左「总览」标题、右 `StackPanel`（右对齐）里放
  「当前视图」标签 + `OverviewViewCombo`。标签字号 **14**，必须与 `FormComboBox` 的
  `FontSize` 一致 —— 早先写 13，比右侧选项小一号，一眼就能看出来。
- 下拉框直接用 `FormComboBox`（采集配置 / 设备配置页同一套：高 26、圆角 6、灰底）。
  **不要**为它另造样式，否则以后调外观会各走各的。
  - 总览页这一个额外加了 `BorderThickness="1"` + `BorderBrush="{DynamicResource Brush.Input.Border}"`，
    因为它悬在页头空白处、没有卡片衬底，不加框线看不出来是个控件。
  - ⚠️ `FormComboBox` 的模板原本**完全忽略** `BorderBrush`/`BorderThickness`，写上去没效果。
    现已改成 `TemplateBinding`，并在 Style 里补 `BorderThickness=0` 作为默认值 ——
    不补这一条，其余 8 个下拉框会跟着一起长出框线。
- 「生产数据」目前只有一句「规划中」占位；`ProductionDataHint` 与画布叠在同一个格子里，
  由 `OverviewView_SelectionChanged` 切换两者可见性。
- ⚠️ 初始化顺序：XAML 里的 `SelectedIndex="0"` 会在 `InitializeComponent` 期间就触发一次
  `SelectionChanged`，那时 `MachineStateViewControl` 还没造出来，处理器必须**先判空返回**；
  初始可见性由 XAML 默认值保证（画布可见、「规划中」折叠），与第 0 项一致。
- 卡片下方 1/4 固定给「运行日志」卡片（`Grid.Row="1"`），
  日志卡片带 `Margin="0,4,0,0"` —— 不留缝的话两张卡片各自的 1 DIP 边框会叠成一条 2.67 DIP 的粗线。

> **历史（别重复踩）**：2026-10-24 这条分段换过好几版 —— 卡片内横排胶囊 → 卡片左侧竖排 +
> 大导航同款样式（`cafa332` → `96f8fd4` → `92cfbae`）→ 导航挪到卡片外 + 竖分隔线
> + 两张卡片对齐（`e859f86`）→ 还原成横排胶囊（`71bba37`）→ 现在的页头下拉框。
> 用户最终选定下拉框版，`SubNavRadioButton` 样式已删（`NavItemTemplate` 保留，大导航仍在用）。
> 若日后又要竖排，去 `e859f86` 取回；那条路的两个坑：**列定义必须提到外层网格**
> 才能让两张卡片真正对齐（不能靠 `Margin` 凑），**分隔线要 `RowSpan="2"`**
> 否则会在日志卡片处断掉。

**布局是写死的**，全部来自项目根目录的 `PLC_MachineState.drawio`：
9 个状态框 + **14 条**转移连线 + 灰底容器。坐标表在 `MachineStateLayout`（`Modeling/MachineState.cs`）里，
**不要在代码里手调坐标** —— 要改布局请改 drawio 再重新换算，否则框和线会对不上。

- 设计坐标：横向 ×1.5 拉开列距、纵向原样，画布 1095×446，由 `Viewbox` 等比缩放到实际空间
  - 拉宽是因为原图宽高比 1.43 而画布格约 2.6，不拉会「图缩在中间、两边大片空白」
- **连线完全照 drawio**：端口（exitX/entryX）与折点原样搬过来，一条都没重画
  - `Fault` 与 `Immediate Stop` 的起点是**灰底容器的下边缘**（drawio 就是这么画的，不是笔误）
  - `Toggle` 是唯一的两端箭头
  - 每条边有稳定 `Id`（`Reset` / `ResetDone` …），落盘按它合并
  - ⚠️ **同一段文字可以是两条不同的线**：`HoldOrError`（Running → Held）与
    `HoldOrErrorCompleting`（Completing → Held）都叫「Hold or Error」，但起点不同、是两条独立的边。
    不要因为文字相同就把它们合并，也不要试图用文字当键 —— **键永远是 `Id`**
  - `HoldOrErrorCompleting` 走右侧通道：从 Completing 底边竖直下到 Held 的水平中线，
    再左折进 Held 右边。它与 `Fault` 那条竖线共线，视觉上像一条从 Completing 直通下方的线
    （drawio 里就是这么画的）
- **连线标签（SC，切换条件）可编辑**：双击就地变输入框
  - 回车（主键盘 `Key.Return` / 小键盘 `Key.Enter` 都认）提交、**Shift+回车**换行、ESC 取消、失焦提交
  - 文字为空时**留一个可见的小空框**（不是整块消失）—— 否则那条连线再也点不回来
  - 标签周围有 3px 内边距、悬停变手型，否则 11pt 的细字很难点中
  - 折行改成**文字里带 `\n`**：早先「按空格硬折」的做法已废弃 —— 文字可编辑后，
    再自动折会让用户敲的空格凭空消失
  - ⚠️ 只判 `Key.Enter` 的话按主键盘回车**毫无反应**（那是个是**小键盘**回车）。
    实测踩过，两个都要认
- **连线标签压在线上**：先用卡片底色盖住线、再写文字，所以线在文字处断开（与 drawio 观感一致）
- 状态框 150×50（drawio 的 100×50 × 1.5），默认文字就是状态名，默认色取 drawio 的填充色
- **值为 1 用配置色，值为 0 一律浅灰**（文字也跟着变灰），与状态本身的颜色无关
  - 浅灰走主题：`Brush.MachineState.Inactive*`，深浅两套各有自己的值
  - 点亮时框内文字固定 `#1A1A1A`（用户挑的填充色基本都是浅色底）
- 求值节拍 **50ms、`DispatcherPriority.Background`**，只在「总览页可见 **且** 停在 PLC状态机 分段」时跑
  - ⚠️ 定时器的**起步点**在 `Window_Loaded` 末尾补了一次 `SyncMachineTimer()`：
    `InitializeComponent` 期间 `Nav_Checked` 就跑了，那会儿分段按钮还没造出来，
    条件必然为假。不补这一下，图会一直是灰的（实测踩过）
- **表达式引擎**在 `Expressions/`（词法 / 语法 / 求值 / 带位置的报错），UI 无关，可离线测
  - 语法：`|| && ! == != > < >= <= ( )`、`TRUE`/`FALSE`（关键字不区分大小写）、数字、变量
  - 比较一律按数值比，BOOL 当 0/1 —— 所以 `sys_True == 1` 能写
  - 比较**不能连写**（`a < b < c` 报错）
  - 空表达式合法，恒为 `FALSE`
  - 报错都带「第 N 个字符」；单写 `=` / `&` / `|` 有专门提示，全角符号也会点出来
  - ⚠️ 位置按**用户实际输入的字符**算（不 Trim），否则前导空格会让位置对不上
- 表达式能引用**系统变量 + 全局变量**，不含元件地址
  - 求值时**系统变量优先**：虽然名字已经不允许重名（见 §5.2），但手工改过的 json 仍可能撞上
- 持久化 `machine-state.json`（挨着 exe），只存文字 / 字形 / 字号 / 颜色 / 表达式，**不存坐标**
  - 读盘按 `Id` 合并进出厂值：文件里少 id 用默认，多 id 忽略 —— 加状态、改默认值都不必写迁移
  - ⚠️ `JsonSerializerOptions` 必须开 `PropertyNameCaseInsensitive`：
    这份文件很可能被人手改，默认区分大小写会让小写键**静默**匹配不上、全部退回默认值（实测踩过，表现是「图一直是灰的」）

**双击编辑**（`Ui/MachineStateEditDialog.cs`，零 XAML 自绘、外观走主题资源）：

- 五项：状态文本 / 字体样式 / 字体大小 / 状态颜色 / 表达式，底部取消、确定
- 色盘用 **drawio 调色板**（30 色）：9 个出厂色全在第二行，改完还能选回来
- **表达式失焦即校验**：不合法弹窗「表达式无效」+ 把输入框退回上次有效值（与自由监控 / 全局变量同一节奏）；
  点确定时再兜一次（防止没失焦就按回车）
- **状态文本允许为空**（用户确认过），空框在画布上就是空白
- 除颜色旁标出的当前色值外，**不写任何提示小字**（见 §9.1 的通用约定）
- 字号下拉若当前值不在预设里会**插进去**，不会悄悄改成别的值
- 确定后写回模型 → `MachineStateSet.Save()` → 刷新画布

**离线断言**：`.tmp/ExpressionCheck` **101 项**（表达式 + 状态机模型）；
系统变量名保留另在 `.tmp/SystemVarCheck` **124 项**里覆盖。

### 5.6 变量名补全（P6.5，三处输入框共用一套）

打字即弹候选列表，像 IDE 那样联想变量名。**只联想变量名，不联想通讯地址**（用户定案）。

| 位置 | 挂载点 | 候选来源 |
|---|---|---|
| 自由监控「元件名称」框 | `MonitorName_Loaded`（模板里每行一个，走 `ConditionalWeakTable` 防重复挂载） | `CompletionSource.ForMonitorName` → **全局变量** |
| 状态机编辑窗「表达式」框 | `MachineStateEditDialog` 构造时 | `CompletionSource.ForExpression` → **系统变量 + 全局变量** |
| 采集数据「变量名称」框 | `AcquisitionItemVariableBox_Loaded`（同样每行一个，共用 `_nameCompletions` 弱表去重） | `CompletionSource.ForMonitorName` → **全局变量** |

**分层**（照 `ValueCodec` 的老规矩）：

- `Modeling/CompletionSource.cs` —— **纯逻辑**：取词 + 筛选 + 候选表。不引用 WPF，
  因此能链接进 `ModelingCheck` 断言（§12 共 35 项）
- `Ui/CompletionPopup.cs` —— 只管显示与按键，只能靠 UIA 端到端测

**语义**（改之前先看这几条，都是踩过的）：

- 前缀命中排在子串命中之前（敲 `sys_` 时想要的几乎总是 `sys_*`）
- 大小写不敏感匹配，但插入**原始大小写**
- 标识符规则与 `ExpressionLexer` 一致；**词以数字开头不弹**（`123` 是数字字面量）
- 列表开着时 `Enter`/`Tab`/`↑`/`↓`/`Esc` **全部归列表**：
  `Enter` 在这两个框里原本是「提交」、`Esc` 是「关窗」，不拦就会误操作
- 弹窗用 `WS_EX_NOACTIVATE` **不抢焦点**：否则点候选会让文本框先失焦，
  触发「失焦即校验」弹出「不是有效软元件或变量」
- ⚠️ **三处输入框都关掉了输入法**（`InputMethod.SetIsInputMethodEnabled(box, false)`）：
  变量名是 C 标识符、表达式只有标识符/数字/运算符，中文输入法在这里没有任何用处，
  却会挡住候选窗、吃掉回车。这行代码在 `CompletionPopup.Attach` 里，**凡是挂了补全的框都会自动关**，
  所以新增挂载点时不用再写一遍；但**别推广到没挂补全的普通文本框**

### 5.7 采集数据（P3.1，采集配置页第一分段）

任务组与数据项可以增删改、落盘到 `acquisition-tasks.json`；
**采集执行与 CSV 落盘已接**（2026-10-26，见本节末尾「执行与落盘」）。
**未接**：数据写入的「写入值」真正下发到 PLC —— 当前写入类型也只读变量、只记表格。

结构是**两层**：任务组（`AcquisitionTask`）下面挂数据项（`AcquisitionItem`）。

| 列 | 内容 | 可编辑 |
|---|---|---|
| 任务名称 | `任务组N`（默认名，重名会被拒） | 走「…」对话框 |
| 任务类型 | 数据读取 / 数据写入（默认**数据读取**） | 走「…」对话框 |
| 执行方式 | `周期 5s` / `触发（上升沿）` | 走「…」对话框 |
| 启用 | `AcqEnableSwitch` | 行内开关，即时落盘 |
| # | 序号（1 起，**不落盘**） | 只读 |
| 数据记录 | 记录到文件的开关（与行内「启用」是**两件事**） | 对话框 |
| 数据项 | `共 N 项`（只报数量，**不放按钮**） | 只读 |
| 操作 | … ↑↓ | 行内 |

子表（展开后）：序号 / 项名称 / 变量名称 / **数据类型** / 删除。

**几条要点**（都是刻意的）：

- **数据类型是只读的**，由「变量名称」指向的**全局变量**反推。填了不存在的名字 ⇒
  这一格显示红色 `—`（`DataTrigger` 绑 `HasVariableError`），悬停给原因。
  与相邻两个输入框底色不同，这个视觉差异本身就在说「这列不可选」
- **增删数据项都在展开后的子表里**（用户定案）：`+` 与 🗑 在子表表头右侧、
  删除在每行行尾 `×`，任务组行只报「共 N 项」。子表表头的 `+` 压在行尾 `×`
  正上方（右距 2 DIP 算出来的，中心差 0.5px），一眼看出「这个 + 加的就是下面这些行」
- ⚠️ **两个 `+` 不是一回事**：页面表头那个加的是**任务组**，子表表头那个加的是**数据项**。
  靠位置区分（一个在页头、一个在组内），别把它们的 Click 接错
- **子表的列随任务类型变**：读取 4 列（#/项名称/读取变量/数据类型），
  写入 5 列（#/项名称/**来源变量**/写入变量/数据类型）。⚠️ 来源列**不是**用
  `Visibility` 藏的 —— 列宽由 `ColumnDefinition.Style` 的 DataTrigger 在 0 与 150 之间切；
  用 Visibility 的话列宽仍占着，读取类型下会凭空空出一截。`Width` 只能写在 Style 里，
  写成属性就是局部值，会压过触发器
- **任务组名称要当文件夹名用**，故名称校验除长度/重名外还挡：非法字符 `\ / : * ? " < > |`、
  结尾的空格与点（Windows 静默吃掉，两个名字会指向同一目录）、Windows 保留名
  （CON/PRN/COM1…）。**漏掉这些的话名字一路绿灯存进 json，等写文件那步才炸**
- **任务组行的按钮顺序是 `… × ↑↓`**：设置 → 删除 → 展开。删除夹在两个非破坏性按钮中间，
  悬停才变红；顺序别调，用户按「删除在展开左边」定位
- **删任务组的确认框只对「有数据项」的组弹**：空组删了没有损失，每次都问只是噪音。
  与全局变量行、监控行的行尾 `×`（一律直接删）不同 —— 那两处一行只丢一条，
  这里丢的可能是一整张子表
- **序号列的表头写 `#`**（任务组与数据项两处都是），不写「序号」：列宽只有 34，两个字放不下。
  序号是**位置**不是数据：`Index` 不落盘，每次增删都调 `Reindex()` 重排 ——
  否则删掉中间一组，界面会出现 1、2、4、5 这样的跳号
- **展开后的子表整块套浅底 + 1px 边框 + 圆角**，底色比卡片深一档（深色主题更深一档），
  让「这些行属于哪个任务组」一眼可见 —— 只靠缩进只能说明「不在最左」，说不清是哪一组。
  底色块左边与「任务名称」表头**齐平**（容器对容器），序号列由 `Padding` 让开
- **任务组级的增删在表头右侧**（共 N 组 / 删除全部 / ＋），与全局变量表、系统变量表同款位置
- 展开态 `IsExpanded` 是**界面态，不落盘**：载入时首个任务组展开、其余收起，
  新增的任务组默认展开（点「+」的下一步必然是往里加项）
- 数据项的名称/变量名是**逐字符**写入模型的（否则右列类型不会跟着变），
  但**写盘在失焦**（`AcquisitionItemField_LostFocus`）—— 逐字符写文件没有意义
- `AcquisitionTaskSet`（纯逻辑，不引用 WPF）与 `AcquisitionTaskItemSet`（界面包装）**分开**，
  照 `GlobalVarSet` / `GlobalVarItemSet` 的老规矩；前者能链接进离线断言
- 表达式框接的是 P6.5 补全（系统变量 + 全局变量），校验口径与 PLC 状态机一致（编译 + 变量存在性）
- ⚠️ 表达式里的名字**必须是变量**，所以对话框里有两个签名相同的委托：`taskNameExists`
  （任务名重名检查）与 `variableExists`（表达式校验）。**别合并** —— 合并会写出
  「表达式里填任务名也算合法」的 bug

**执行与落盘**（2026-10-26，`Modeling/AcquisitionCsv.cs` + 窗口里的 `RunAcquisitionTick`）：

- **任务组引用的变量常驻采集，不随页面可见性停**。这是与 IO / 自由监控 / 状态机**唯一相反**的一处：
  那三个都是「可见才跑」，而采集任务一旦因为用户切页停掉，记录里会凭空缺一段 ——
  文件里只有时间戳，**没有「这段没采」的标记**，事后完全看不出来。
  实现上 `PollSnapshot.AcquisitionGlobals` 与 `MachineGlobals` 并列，但 poller 里
  它排在 `if (!snap.IoPageActive) return;` **之前**
- 常驻清单由 `RefreshAcquisitionGlobals()` 重算，**只收启用的组**（停用的组把变量拖上总线等于白读），
  写入类型连「来源变量」一起收。调用点要跟着任务组/数据项/变量/启用的每一次变更走
- **触发判定是纯函数**（`AcquisitionTrigger.ShouldFire`，不引用 WPF/Modbus，能离线断言）：
  周期式记「下次该采的时刻」而不是「上次 + 间隔」，这样周期不随定时器抖动累积漂移；
  触发式只认**上升沿**，持续为真不重复采
- ⚠️ 周期式以**本次**为基准推下一次，不是以旧的下次时刻 —— 后者在长时间停采（断线重连）后
  会连发一串补采，把文件写满重复数据。断言 ⑥ 专门锁这条
- ⚠️ **首拍要延后一个周期**（`GetOrCreateRuntime` 里设 `NextFireLocal = Now + 周期`）：
  记录定时器与采集引擎是两个独立的 `DispatcherTimer`，启动瞬间先后没有保证。
  不延后的话首行会写进变量的**初始值**（0 / 空串），实测确实如此
- ⚠️ **未连接时不记录**。断开期间 `GlobalVariable.Value` 停在最后一次读到的值，
  照记的话 CSV 会连续出现同一个数，与「设备真的稳定输出这个值」**无法区分** ——
  比缺一段更糟，因为它是假的
- 触发式表达式可能引用**系统变量**，而 `_machineVariables` 的系统快照平时只由状态机定时器刷新
  （那个定时器在别的页面是停的）。`RunAcquisitionTick` 里要先补一次 `UpdateSystemValues`，
  否则切走页面后表达式拿**过期的值**求值
- 落盘路径 `{SavePath}\{任务组名称}\{yyyy-MM-dd}.csv`：文件夹按**名称**（不是序号 —— 序号会重排），
  文件按**日期**。时间列只有时分秒，故**跨天必须换文件**，否则第二天追加进同一份文件会丢日期信息
- ⚠️ **列集合一变也要换文件**（用户定案），文件名接序号：`2026-10-26.csv` → `2026-10-26_2.csv`。
  表头只在建文件时写一次，中途增删/改名数据项还往同一文件追加，表头与数据行就**列数不等** ——
  Excel 打开不报错、只是列错位，属于「发现时已攒了一周脏数据」的那类问题。
  判据是**表头那一行的原文**（`AcquisitionTaskState.RecordHeaderKey`），比逐个比项名简单且能覆盖改名
- ⚠️ 换文件时只跟**当天最后一个文件**比表头，不回头找表头相同的老文件：
  那样 `A,B` → `A,B,C` → `A,B` 会写回第一个文件，同一份文件里的时间戳出现**回跳**，
  下游按时间排序直接乱掉。多一个文件是可见的代价，时间戳乱序是隐蔽的代价
- 当前文件路径缓存在 `AcquisitionTaskState.RecordPath`（连同 `RecordDay`），
  否则周期小到 0.1s 时每拍都要扫一遍目录
- CSV **UTF-8 带 BOM**：不带的话 Excel 双击打开中文表头是乱码
- 字段含逗号 / 引号 / 换行要**加引号、内部引号翻倍**。变量值里出现一个逗号就会把整行错位，
  而且错得隐蔽 —— Excel 打开只是列对不上，不报错
- 表头只在新文件（或空文件）时写一次；项名称**允许重复**（用户定案，CSV 本身允许同名列），
  项名称为空时兜「项N」
- **写入类型只记「写入变量」的值**，不记来源（用户定案）；读取类型记「读取变量」的值。
  两档取的都是 `GlobalVariable.Value`（已按显示格式转好的文本）
- ⚠️ **来源变量可以是系统变量**（用户定案 2026-10-26），写入变量**不行**。
  两列口径不同，别为了"统一"把系统变量塞进写入变量 —— 它没有 PLC 地址，填了也写不出去。
  解析统一走 `AcquisitionSource.Resolve`（`Modeling/AcquisitionSource.cs`，纯逻辑可断言）：
  **先查系统变量、再查全局变量**，同名时以系统变量为准 —— 否则用户造一个叫 `sys_True`
  的全局变量就能顶掉「sys_True 恒为真」这条对外承诺
- 补全也是两套：来源变量走 `CompletionSource.ForSourceVariable`（系统 + 全局），
  写入变量走 `ForMonitorName`（只有全局）。XAML 里是两个不同的 `Loaded` 处理器
  （`AcquisitionSourceBox_Loaded` / `AcquisitionItemVariableBox_Loaded`），**别合并**
- 来源变量的值有两个来源：全局变量取 `GlobalVariable.Value`，系统变量要传当时的快照
  （`SystemVariableValues.Capture`）。`AcquisitionSource.ValueOf` 按名字自动分派
- ⚠️ 来源变量填错了必须**字变红**（`AcquisitionSourceBox` 样式），不能只留 ToolTip：
  用户不知道它错就不会去悬停，等于没有任何反馈。与「数据类型」列的红字同一套语义

**写入变量的编辑规则**（2026-10-27，用户定案：填对来源才能编辑、类型必须相同、地址可以以后补）：

- **来源变量填对之前，写入变量一律置灰不可编辑**（`CanEditVariable`）。只绑 `IsEnabled` 就够 ——
  `MonitorInlineTextBox` 模板自带 `IsEnabled=False` 触发器，会把底色切成
  `Brush.Input.Background.Disabled`。但必须同时加 `ToolTipService.ShowOnDisabled="True"`：
  禁用控件默认不弹 ToolTip，而提示恰恰只在禁用态才有用（说了「先填来源」，却看不见）
- 类型必须**完全相同**（`AcquisitionTaskRules.WriteTypesMatch` 用 `==`，不做隐式转换）。
  不符时输入框变红（`AcquisitionTargetBox` 样式，绑 `HasVariableError`），**并且「数据类型」列同时变红** ——
  类型列的文本取自写入变量，只红输入框的话说不清「错在这一列的取值」
- **允许未映射地址**（用户定案）：`HasVariableError` 故意**不看** `HasAddress`，
  只在提示尾缀「未映射地址（数据不会下发到设备）」。拦下的话用户连第一版数据都填不进去 ——
  地址是等 PLC 点位确认后才补的事
- ⚠️ 「先填来源」这句提示要**跟着来源改口**：判据是 `_isWriteTask && SourceVariable == null`。
  首版写成 `_isWriteTask ? …`，实机里来源填好后置灰解除了、提示还在催「先填写来源变量」，等于骗人
- ⚠️ **行自己看不出所属任务是读还是写**（读取类型下来源列宽为 0，行上没有那个信号），
  所以 `SetWriteTask` 由**父行注入**，三处都要：构造函数、`AddItem()`、`RefreshFromModel()`。
  漏掉 `RefreshFromModel` 的话，读写类型互切后整张子表还留着上一档的置灰状态
- 样式顺序同 10.27 的坑：`AcquisitionTargetBox` 的 `BasedOn` 指向 `MonitorInlineTextBox`，
  必须定义在它**之后**
- 变量没填 / 找不到 ⇒ **空单元格**，不写占位符：表格是给下游（Excel、MES）读的，
  混进「—」会变成脏数据
- 写盘失败**不弹窗**（50ms 一拍会弹到用户崩溃），只写一条 `DeviceLogLevel.Warning` 日志
- **每次执行都写一条运行日志**（用户定案），文案由 `AcquisitionTaskRules.DescribeLogSuccess` /
  `DescribeLogFailure` 生成，形如 `任务组1（压力采集）：触发读取任务执行完成`。
  三个要点：短词（`读取`/`写入`，不是界面上的 `数据读取`）、**成功不带原因也不带冒号**、
  序号给当下定位而名称给事后可读（名称是写日志那刻的快照，改名不回溯）
- ⚠️ **未连接时一条任务组日志都不写**（用户定案：不执行任何任务组）。断线期间
  `Value` 停在最后读到的值，记日志或记 CSV 都是在记录**假数据**
- **日志上限从 200 提到 1000**：5s 周期下 200 条只够 16 分钟，「连接失败」那行早被挤掉了。
  ⚠️ 这个上限只管**界面**，落盘文件按天写、不受它约束
- ⚠️ **日志自动滚动要区分「用户翻上去」与「面板被折叠」**：`ScrollChanged` 在面板折叠时
  也会触发，且 `ViewportHeight` 变 0，用「此刻在底部吗」当场判定必然得 false ——
  切个页面就把跟随关掉了，切回来停在最老一条。故用显式的 `_logFollowTail` 状态，
  且在 `!IsVisible` 时直接 return 不碰它
- ⚠️ 切回总览页补滚动时，`ScrollIntoView` 在「刚可见」那一帧对虚拟化列表**不生效**。
  要排到 `DispatcherPriority.Loaded`、先 `UpdateLayout()`、再直接 `ScrollViewer.ScrollToEnd()`。
  三步缺一不可（前两步各漏一次都实测复现过）
- 运行态（`AcquisitionTaskState`：下次该采的时刻、表达式上一拍的值、编译缓存）**只在内存**，
  不落盘；键用 `AcquisitionTask` 的**实例引用**而不是名称 —— 名称可变，改名会丢计时状态

**配置错误（`Modeling/AcquisitionTaskCheck.cs`，2026-10-27 用户定案）**：

- 判据全在纯逻辑里（不碰 WPF）：`Describe(task, findVariable)` 返回问题清单，
  `ErrorText = "配置错误"`。读取类型查读取变量；
  写入类型**按填写先后短路只报第一处**（来源空 → 来源查不到 → 写入变量空 → 写入变量查不到 → 类型不符），
  否则一项没填会对用户刷三条
- ⚠️ **未映射地址不算配置错误**：用户定案允许（见上）
- **组级问题先报（触发表达式）**：`DescribeTrigger` 在逐项检查之前跑，只对触发式（`Trigger == Edge`）生效。
  三种都算配置错误：表达式解析不了（`CompiledExpression.TryParse` 的 error 原样带出）、
  表达式为空（空表达式恒为假＝永不触发，而任务选项对话框允许存空，不报就永远不会跑）、
  `TryValidateNames` 报某个变量不存在（error 自带「第 N 个字符：变量“X”不存在」）。
  名字存在性按「系统变量 ∪ 全局变量」判 —— 与任务选项对话框 `_nameExistsOfVariable` 同一口径。
  这条把「变量被删除/改名后任务悄悄永不再触发」变成可见的配置错误（用户 m02209 定的 A 项）
- 触发表达式判定的成本：一次 `Describe` 约 **4.5 µs**（含词法+语法分析），每 50 ms 每任务一次，
  10 个任务组约 0.09% CPU，**故意不加缓存**（缓存会引入「改了表达式还按旧结果判」的过期风险）
- 呈现：**不弹窗、不阻止编辑、不用徽标**。只在行上「启用」开关右侧放一句琥珀色「配置错误」
  （`TextBlock` + `Brush.Warning`，`Visibility` 绑 `HasConfigError`）。
  只给这四个字、不列明细 —— 用户定「只提示配置错误就可以了不用这么详细」。
  色义不变：**红 = 这个格子填错了，琥珀 = 这东西现在不可用**
- **关掉并锁住开关，合并成一个门**：`EffectiveEnabled => Model.Enabled && !HasConfigError`，
  开关 `IsChecked` 双向绑它、`IsEnabled` 绑 `CanToggleEnabled => !HasConfigError`。
  这样「配置错误」与「用户停用」走同一条不采集路径，执行侧只判 `EffectiveEnabled` 一处
- ⚠️ **`EffectiveEnabled` 必须可写**（`set => Enabled = value;`）：`CheckBox.IsChecked` 默认 TwoWay，
  绑到只读属性会让行模板一实例化就抛 `XamlParseException`（「无法对只读属性进行 TwoWay 绑定」），
  整个采集页直接打不开，而编译期 0 警告 0 错误 —— 完全看不出来
- ⚠️ **`Model.Enabled` 存用户意图，程序绝不改写它**：采集配置边输边存，清空变量名重打的那一瞬间
  就是配置错误，据此落盘会让组永久静默停采且无痕迹；新组默认 `Enabled = true` 且 0 项，
  一创建就被关等于默认值作废；改写后也分不出「我关的」与「它自己关的」。
  因此开关显示为关、点不动，修好配置后自己回到「开」
- 拦截只判 `EffectiveEnabled` 一处：`RefreshAcquisitionGlobals()` 跳过（不把变量拖上总线）+
  `RunAcquisitionTick()` 在 `GetOrCreateRuntime` **之前** `continue`。
  顺序关键：不建 runtime 就不会推进 `NextFireLocal`，配置修好后从「现在 + 一个周期」开始重新计，
  不会补偿式补写一串陈年时间戳
- ⚠️ **行看不出父组**，开关与提示的新鲜度靠父 `AcquisitionTaskItem` 订阅子行 `PropertyChanged`
  （`AttachItem` 订阅 / `RemoveItem`·`ClearItems` 必须退订，否则删掉的行还挂在父的事件上）。
  只在 `VariableName`·`SourceVariableName` 变化时刷新；`RaiseCount()` 与
  `RefreshFromModel()`·`RefreshVariables()` 末尾也要刷（读写互切、变量表改名删变量都会改判据），
  `Enabled` setter 末尾还要 raise `EffectiveEnabled`
- 项名前缀 `项3（压力）：`，但项名空白或恰好等于默认名「项N」时**只给序号**，
  否则出现「项1（项1）：」，像 bug。现在明细只进断言消息、不上界面，但这规则别丢
- 列宽：**「启用」列必须 ≥170** —— 开关实测 96×30（不是按模板算的 64，`ContentPresenter`+`Track`
  之外还有 32 的额外宽度），提示 66×22，加 8 间距。现定 `启用 180 / 数据项 80`
  （与原 150+110 等宽，总宽不变）。**行模板与表头两处 `ColumnDefinitions` 必须同改**，
  只改一处会表头与行错位（实测过）


**写入任务真正下发（`Modeling/AcquisitionWrite.cs`，2026-10-27）**：

- 链路接在采集节拍里，不新建通道：`RunAcquisitionTick` 对 Write 任务分叉到 `BeginWriteTask`
  fire-and-forget `RunWriteTaskAsync`，逐项 `AcquisitionWrite.TryBuild` →
  `WriteToDeviceAsync`（与自由监控同一个写函数）
- ⚠️ **重入守卫 `WriteInFlight` 必须放在 `ShouldFire` 之前**，否则一次慢写入会把整个周期往后挤；
  **`BeginWriteTask` 里不能 await**（50ms 定时器回调里 await 会卡节拍）；
  fire-and-forget 的异常没人接，`RunWriteTaskAsync` 必须 try/catch/finally 兜底
- `TryBuild` 判定顺序：变量不存在 → 无地址 **Skipped**（静默跳过、不算错误）→
  `ResolvedAddress < 0` → 只读区 → `CoilOnly` 文本须 `0`/`1` → `TryEncodeRegisters`。
  **Skipped 与 Failed 必须分开**：前者是允许的常态，不能污染失败日志
- ⚠️ **编码规则（最易写错）**：用**来源变量的 `Format`** 解析文本，用**写入变量的 `Type`/`Length`**
  定字宽与范围（两者 Type 已被 `WriteTypesMatch` 强制相同，只有 Format 可能不同；
  来源 INT/Hex 的 `"2A"` 按目标的 Dec 解析会失败）。因此 `AcquisitionSource` 必须带 `Format`，
  系统变量取 `DefaultFormat`
- 一项失败**不短路**（Modbus 无事务，短路换不来原子性）；失败文案 `DescribeFailure` 拼成
  `2/3 项写入失败：项1（压力）：…`，项名前缀**复用 `AcquisitionTaskCheck.DescribeItemPrefix`**
  （故它是 public，两处各写一份必然走样）
- CSV **一行不改**：它记写入变量的回读值，来源变量从不进文件；写通后该列自然变成真值
- 只读区拦截（用户定「在全局变量表映射地址时限制」）：`GlobalVariableStore.TryResolve` 先判只读
  再赋地址，命中 `AddressErrorKind.ReadOnlyArea`。⚠️ **代价：全局变量表从此不能映射 X**
  （X 仍可在自由监控直接读；将来要读 X 得改成「仅警告」）。⚠️ 测试要用 BOOL 变量，
  INT 填 X0 会先在 `CanMapTo` 报「类型不符」走不到这里

## 6. 心跳与连接生命周期

- `ModbusHeartbeat.RunAsync` 每 100ms 检查；距上次有效响应满 **1s** 时以 FC01 读**单点线圈**，只读不写
- **心跳地址 = 驱动地址表里「地址最小」的线圈区 `StartAddress`**（信捷 M 区 / 汇川 M 区均为 **0**）。按 `Area` 筛选后 `OrderBy(StartAddress)`，不依赖数组顺序；无需任何配置，与是否导入 IO 表无关
- 地址表里没有线圈区时提示「未启用心跳」并 return（用 `FirstOrDefault` 而非 `First`，否则抛出的异常会被上层当成通讯故障并断开连接）。正常驱动不会出现
- 合法异常响应抛 `ModbusResponseException` → **保持连接**并告警（码 1/2 提示配置无效，其余提示响应异常），后续心跳成功即清除。**IO 轮询自 2026-10-16 起同样不断开**（见 §5）；两条支路的差别只剩阈值与呈现：心跳每次异常即告警并去重，IO 累计**连续 5 次**才记一条
- 读取超时 2s；空闲失联通常**约 3 秒**检测到（1s 空闲 + 2s 超时 + 调度延迟），不是严格 3s 上限
- `RunConnectionTasksAsync` 启动时把**连接与驱动快照**传给两个任务，所有回 UI 回调先过 `IsCurrentConnection`（未关窗 && token 未取消 && 仍连着 && 是同一个客户端实例 —— 判定集中在 `ConnectionSession.IsCurrent`）→ 旧连接的后台回调无法误操作新连接
- `StopPolling` 只取消并置 null，**释放交给任务自身 `finally`**（确认自己仍是当前连接后）；`OnClosed` 统一取消连接尝试、停任务并释放
- **连接的身份与生命周期状态**（当前客户端 / 是否连着 / 期望目标 / 在途取消源 / 重连计数）在 `Polling/ConnectionSession.cs`，**async 编排留在窗口** —— 等待循环里逐秒刷状态栏、锁表单、改按钮文案全是界面驱动的东西。两种「断开」语义也在那里落了地：`ForgetTarget()` = 用户主动断开（目标清掉 → 不重连），只 `ReleaseClient()` = 异常断开（目标留着 → 才能重连）

### 6.1 自动重连（P6.3）

| 项 | 行为 |
|---|---|
| 触发 | **仅「异常断开」**（读超时 / 对端关闭 / 帧校验失败等）。**用户主动断开、首次连接失败均不触发** |
| 退避 | `ReconnectPolicy.DelaysSeconds = { 1, 2, 4, 8, 10 }`，封顶 10s（`DelayForAttempt` 是纯函数，越界取表尾）；连接成功清零计数 |
| UI | 重连期间**表单保持锁定**（参数已快照，改了不生效，避免误导）；按钮变「取消重连」；底栏逐秒显示 `正在重连 … Ns（第 M 次）`（**不带 ip:端口**，目标参数在右侧 `StatusBarDetail`） |
| 日志 | **重连尝试不写日志**（日志只记 5 种设备状态，ADR-14）；连上后记一条常规「已连接：ip:端口」 |

**⚠️ 两种 `Disconnect` 重载语义不同，别用错**：

- `Disconnect()` 无参 → 调 `ConnectionSession.ForgetTarget()` **清空期望目标** = 「用户主动断开」语义
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
- 仅内存保留最近 `DeviceLogPresenter.DefaultMaxEntries`(200) 条，超出丢最旧、不落盘，可一键清空（上限与裁剪在 `Ui/DeviceLogPresenter.cs`）
- **边界**：「完全无响应」不产生「通讯异常」—— 由 `NotifyConnectionLost` 记红色「已断开」，**一次断线只有一条日志**
- 原因经 `ClassifyDisconnectReason` 归一为简短中文（Socket 原文 →「网络不可达」，Modbus 帧错误 →「响应帧校验失败」），未命中原样保留
- 日志区套一层内嵌框线 `Border`（`#C8C8C8` 1px、圆角 4）；时间列用 `SharedSizeGroup="DeviceLogTime"` 跨行共享宽度（**否则比例字宽会让消息左边界抖动**），`DeviceLogList` 上须有 `Grid.IsSharedSizeScope="True"`

### 7.1 日志落盘（P5.2）

同一条日志**既进界面又进文件**，落盘挂在 `Ui/DeviceLogPresenter.Append` —— 窗口的 `AppendDeviceLog` 是 5 种设备状态的唯一出口，7 个调用点零改动。

```
[2026-10-15 11:41:45] [INFO] 已连接：192.168.1.66:502
[2026-10-15 11:41:58] [ERROR] 已断开：用户主动断开
```

| 项 | 行为 |
|---|---|
| 默认目录 | `AppContext.BaseDirectory\Log`（exe 旁） |
| 文件名 | 日期，如 `2026-10-15.log`（**一天一个**，跨天自动换） |
| 格式 | 纯文本 UTF-8 无 BOM，每行 `[yyyy-MM-dd HH:mm:ss] [级别] 消息` |
| 级别 | **按颜色语义**（见 `DeviceLogPresenter.ToFileLevel`）：绿 → `INFO`、琥珀 → `WARN`、红 → `ERROR` |
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
- 独立验证工程 `DeviceLogCheck`（`.tmp/`，**23 项**断言）覆盖 `DeviceLogPresenter` 的内存上限裁剪 / 级别映射 / **共用时间戳** / 清空 / 换主题重建；改动日志编排后跑它（**未提交**，随用随弃，手法同 `PollerCheck`：链接源码 + 打桩 WPF 类型）
- ⚠️ **在 DSH 下跑它时先改 TEMP**：`%TEMP%` 上挂着沙箱 ACE，子进程（`dotnet run`）建目录会被拒（`UnauthorizedAccessException`），而 pwsh 自己建得出来，故报错看着莫名其妙。先 `$env:TEMP = "$PWD\.tmp\lc-temp"` 再跑

### 7.2 自动连接（设置页第二张卡）

一张卡装一组开关，标题「自动连接」，副标题「设置程序启动后自动连接」。
目前只有一个子项 **PLC连接**（`AutoConnectPlcToggle`，`ToggleSwitch`，**默认打开**）；后续同组还会加（如 MES 连接），
新子项照抄下面的 `Grid` 两列结构即可。

- 字段：`app-settings.json` → `AutoConnectPlc`（`bool`，缺字段按默认 `true`）
- 开关文案在 `AutoConnectPlcToggle_Changed` / `SyncAutoConnectToggle` 里由代码切 —— 与日志保存开关同一个坑：模板触发器里的 `Setter` 赢不过局部 `Content`
- **启动时机**：`Window_Loaded` 末尾用 `Dispatcher.InvokeAsync(TryAutoConnect, DispatcherPriority.ApplicationIdle)` ——
  先让首帧画出来，再发起连接（连接是异步的，失败原因要能落到界面上）
- `TryAutoConnect` 的跳过条件：开关关 / 窗口已关 / 已连接或正在连 / **不是以太网模式**（串口未实现）/ IP 为空
- **失败不重试**：与手动连接一致，首次连接失败交还控制权给用户
- ⚠️ **不写设备日志**（需求）：自动连接是后台行为，成功由 `TryConnectAsync` 记「已连接」、失败记「连接失败」，不必再加一条

### 7.3 项目信息（P5.3）

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

| 属性 | 枚举 | 信捷 XD 实测值 | 汇川 Easy 实测值 |
|---|---|---|---|
| `Int32ByteOrder` | `ByteOrder32`（`1234`/`2143`/`3412`/`4321`） | `Order3412` | `Order3412` |
| `FloatByteOrder` | 同上 | `Order3412` | `Order3412` |
| `StringByteOrder` | `StringByteOrder`（`12`/`21`） | `Order12` | **`Order21`** |

- 记号含义见 `CommDriver.cs` 的枚举注释：把 4 个字节**按重要性**编号 1=MSB…4=LSB，名称即字节流里的排列顺序
- **两品牌的值都是真机定案**：信捷见 `Design.md` §10.3 / §10.4，汇川见 §10.16（2026-10-21，`192.168.1.10:502`）
- ⚠️ **两品牌唯一差异是字符串**：信捷 `12`（第 1 个字符在高字节）、汇川 `21`（低字节）。
  照抄对方的值会把 `ABCD` 显示成 `BADC`。整数与浮点两品牌相同（`3412`）
- 新增品牌时**不要照抄任何一家的值**：先用 `.tmp/OrderProbe` 探针实测（见下）
- **项目里不存在"猜"的字序了**：两台验收机的驱动默认都是实测值；
  `ByteOrderSettings.Default`（未选驱动时的兜底）也取信捷实测值 `3412`/`3412`/`12`，
  不再是照通用惯例猜的大端 `1234`
- ⚠️ 字符串**不涉及「字序」**：它只是字节序列，没有字的权重之分，别把 `3412` 套到 `STRING` 上

**三套各自独立，且真的会被读**（P4.3，2026-10-21 接线）：

- 载体是 `Modeling/ByteOrderSettings.cs`（`Int32` / `Float` / `String` 三个字段 + `FromDriver`）。
  `ValueCodec.FormatRegisters` / `TryEncodeRegisters` 收的是**整份** settings，按行类型取对应那套 ——
  编解码器仍然**不读全局状态**，只认入参
- ⚠️ `REAL` 与 `DINT` **不再共用一套字序**。早先两者绑在一个 `byteOrder` 参数上，
  「32位浮点数字节顺序」下拉框改了也不生效（界面显示 1234、数值仍按 3412 解，**不报错但全是错值**）
- **切换设备类型 = 重置为驱动默认**（用户定案）：`SyncByteOrderCombos` 里 `ApplyByteOrders(FromDriver(driver))`，
  `MonitorItemSet.SetDriver` 也同步重置一次；**不持久化**，重启回到驱动默认
- 所有改动路径都走窗口的 `ApplyByteOrders`（写 `_byteOrders` + `_monitorSet.SetByteOrders`）——
  **不要只改下拉框**，那样界面与数据会各说各话
- 轮询回填取 `_host.MonitorSet.ByteOrders` 那份快照（`ApplyBlock` 的入参）：后台线程不能读窗口字段
- 验证：`ModelingCheck` §11 共 14 项（改浮点不影响整数、改整数不影响浮点、编码侧同样各走各的；
  另含两品牌出厂默认的对照 —— 信捷字符串 `12`、汇川 `21`，这条同时证明「切驱动 = 重取默认」）。全部 **100 项**通过

**测字序的现成工具**：`.tmp/OrderProbe`（一次性，未提交；链接 `ValueCodec` 源码，**只发 FC03 不写**）：

```powershell
OrderProbe <ip> <port> <unitId> <localIp|-> <addr:count> [addr:count ...]
# 例：OrderProbe 192.168.1.10 502 1 192.168.1.80 800:4 810:2
```

它打印原始十六进制 + 按 4 种字序 / 2 种字符串序各解一遍，一次读取即可判定（不用试错）。
测试值建议：`DINT=305419896`（`0x12345678`，4 个字节互不相同）、`REAL=123.45`、
`STRING[4]='ABCD'`；32 位与浮点**必须分开测**（两套字序独立，见上）。

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

**默认不写提示文字。** 输入框下方、按钮旁边的浅灰小字（示例、用法说明、快捷键提示）
**一律不加** —— 用户原话：「以后如无说明默认不加提示」。确有必要时（例如颜色项旁标出当前色值）
才加，且加之前先确认。既有页面不追溯，新增页面不再加。

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

**对话框用另一套样式，键前缀 `Dialog.`，定义在 `Themes/DialogControls.xaml`**
（由 `App.xaml` 合并进 `Application.Resources`）。**不要**在对话框里复用
`FormComboBox` / `FormTextBox` / `FormButton` —— 那些是 `Window.Resources`，
作用域到不了别的窗口；且主窗口的胶囊风格尺寸不适合对话框。

| 用途 | 键 |
|---|---|
| 标签 / 说明 / 红色错误 | `Dialog.Label` / `Dialog.Hint` / `Dialog.Error` |
| 输入框 | `Dialog.TextBox` |
| 下拉框 / 下拉项 | `Dialog.ComboBox` / `Dialog.ComboBoxItem` |
| 单选框 | `Dialog.RadioButton`（16 圆圈，选中整圈填主色 + 6 白点，**文字在右**） |
| 按钮（次色 / 主色） | `Dialog.Button.Secondary` / `Dialog.Button.Primary` |
| 窗口外壳（字体、边框、底色、深色标题栏） | `Ui/DialogChrome.cs` 的 `DialogChrome.Apply(this)` |

新建对话框的最小骨架：

```csharp
public sealed class MyDialog : Window
{
    public MyDialog()
    {
        DialogChrome.Apply(this);              // 字体 / 底色 / 深色标题栏，必须第一个调
        Title = "标题";

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "提示", Style = RequireStyle("Dialog.Label") });
        root.Children.Add(new TextBox { Style = RequireStyle("Dialog.TextBox") });
        // ...
        Content = root;
    }

    // 取不到样式就抛 —— 见下方第 1 条
    private static Style RequireStyle(string key) =>
        (Style)Application.Current.TryFindResource(key)
        ?? throw new InvalidOperationException($"缺少样式 {key}");
}
```

**七条容易踩的**：

1. ⚠️ **样式取不到必须抛，不要静默用默认外观**。WPF 默认 ComboBox 模板把
   ToggleButton 画成**硬编码的浅色渐变、完全忽略 `Background`** —— 深色主题下
   文字 `#F0F0F0` 落在 `#EBEBEB` 底上，实测对比度 **1.05:1**（等于看不见）。
   这种「改了颜色却没用」的 bug 极难查，所以宁可启动就崩。
2. ⚠️ **ComboBox 必须用 `Dialog.ComboBox`，不能只改颜色**。原因同上：默认模板不吃
   `Background`，只有整份重绘才修得掉。
3. ⚠️ **`Dialog.ScrollBar` 必须是隐式样式**（`<Style TargetType="ScrollBar" .../>` 无 `x:Key`）。
   弹层里的滚动条由 `ScrollViewer` **内部创建**，拿不到 keyed 样式，深色下会是一条白条。
   **不能**把样式写进 `ControlTemplate` 里的 `ScrollViewer.Resources`：模板作用域内
   `StaticResource` 查不到后定义的键，会直接崩。
4. ⚠️ **输入框不要写死 `Width`，让它在列里拉伸**。对话框客户区比看起来窄：
   `Width=442` 的窗口在 150% DPI 下，列 1 只有约 **327 DIP**。写死 `Width=330`
   会超出 3 DIP 被裁掉 —— 表现为**右边框整条不见了**（左/上/下三条都在，
   因为那 1px 落在被裁的像素上）。用 `HorizontalAlignment="Stretch"`。
5. ⚠️ **TextBox 模板里不要再给 `PART_ContentHost` 套 `Margin="{TemplateBinding Padding}"`**。
   `TextBoxView` 自己就会吃一遍 `Padding`，套两次等于双倍：实测文字起点距左边框
   20 DIP，而下拉框只有 10 DIP，一眼看出没对齐。只保留 `TextBox.Padding` 一处。
6. ⚠️ **错误提示一律内联（`Dialog.Error` 红字），不要用 `MessageBox`**。系统消息框
   不跟主题（深色下弹一块白板）、且会抢焦点打断「改到对为止」的循环。
   **联动坑**：若校验失败后会**程序回填**输入框（如恢复上次有效值），`TextChanged`
   里那个「一改字就清错误」的逻辑会把刚显示的错误**立刻擦掉** —— 表现为
   「校验失败了但界面一个字都不显示」。加一个 `_reverting` 标志挡住程序回填。
7. ⚠️ **一组单选按钮必须显式给 `GroupName`**。WPF 默认按**逻辑父容器**分组，
   而对话框里常把某个选项和它的参数框放同一个 `StackPanel`（如「周期（s）」+ 秒数输入框），
   于是几个单选按钮各自成组、**可以同时选中**。真机截图里就是两个蓝圈同亮，
   读取处读到第一个为真就停了 —— 表现为「切到触发档，但表达式框仍是灰的」。
   实测踩过一次，见 `AcquisitionTaskDialog`。

## 10. 任务速查

| 需求 | 位置 |
|------|------|
| **加输入框 / 下拉框 / 按钮（新增控件必读）** | 套用现成样式，别手写外观 —— 见 **§9.1 控件风格约定** |
| **改任何颜色（必读）** | 用主题键，**不要写字面量** —— 见 **§9.2 主题与配色** |
| **新增 / 改对话框（必读）** | 用 `Dialog.*` 样式 + `DialogChrome.Apply`，别复用主窗口样式 —— 见 **§9.3 对话框控件** |
| **加/改主题、深色适配、跟随系统** | `Themes/`（`Light.xaml` / `Dark.xaml` 同键）+ `Themes/ThemeManager.cs`；C# 侧画刷见 §9.2 |
| **设置页（P5）** | `SettingsPanel`（`MainWindow.xaml`）+ `SettingsNav` + `Nav_Checked`；主题分段 `ThemeSegment_Checked`；日志开关 `LogSaveToggle_Changed`、路径 `LogDirectoryTextBox_LostFocus`；自动连接 `AutoConnectPlcToggle_Changed` / `SyncAutoConnectToggle` / `TryAutoConnect`（见 §7.2）；项目信息 `RefreshProjectInfo`（见 §7.3） |
| 改导航项/页面显隐 | `MainWindow.xaml` 导航区 + `Nav_Checked` |
| 加通讯品牌 | `Drivers/DriverRegistry.cs`（§8） |
| 加设备配置字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | 窗口 `BuildConnectionDetail()` 拼串 → `Ui/StatusBarPresenter.cs` 写 `StatusBarDetail` |
| 底栏状态文字/颜色 | `Ui/StatusBarPresenter.cs`（`Set` / `RefreshDetail` / `ReapplyTheme`）；窗口侧 `SetStatus` / `UpdateOverviewStatus` 只是薄包装 |
| 底栏布局/对齐 | `MainWindow.xaml` 主体 Grid 末尾的底栏 `Border`（见 §3.5，**改前必读**） |
| 最大化底栏被任务栏遮住 | `Ui/MaximizedInset.cs`（`Apply`）+ 窗口侧 `UpdateMaximizedInset` / `Window_StateChanged`（见 §3.6） |
| 自动重连退避/触发/取消 | `Polling/ReconnectPolicy.cs`（退避表）+ `Polling/ConnectionSession.cs`（目标/计数）+ 窗口侧 `StartReconnect` / `CancelReconnect`（见 §6.1） |
| IO 灯色（输入绿/输出橙） | `IoPoint.StateBrush` + `InputOnBrush` / `OutputOnBrush` / `OffBrush`；`IsOutput` 在 `TryCreatePoint` 定 |
| IO 点位集构建/排序 | `Io/IoPointSet.cs`（`Rebuild` / `TryCreatePoint` / `NormalizePointName`，按地址升序）；窗口侧薄包装 `RebuildIoPoints` |
| 导入 IO 表 / 解析规则 | `Io/IoTableImporter.cs` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格）；弹窗在 `ImportIoTableButton_Click` |
| 注释与点位集持久化 | `Io/IoPointSet.cs` 的 `Load` / `Save` → `io-comments.json`（窗口侧 `LoadComments` / `SaveComments` 为薄包装） |
| IO 分页 | `Io/IoPager.cs`（算术）+ `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（控件写入）；`_xPageIndex`/`_yPageIndex` 独立 |
| 改每页点位数 | `IoPager.RowsPerColumn` / `IoPager.ColumnCount`（PageSize 由其相乘得出） |
| IO 采集范围 | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| **自由监控读取（分块规划）** | `MonitorItemSet.BuildReadPlan`（按 `ModbusArea` 分组 + 地址连续性分块）+ `MonitorItemSet.ApplyBlock`（偏移切片 + 格式化回填） |
| **自由监控轮询入口** | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` 开头按 `_freeViewActive` 分流 → `PollFreeMonitorAsync`（按区选 FC01/FC03/FC04，单块失败不断线） |
| **系统变量（P3.5，写死只读）** | `Modeling/SystemVariable.cs`（16 项清单）+ 窗口 `AcqView_Checked`（见 **§5.3**；无编辑入口） |
| **PLC 状态机（P1.2，总览页）** | `Expressions/`（表达式引擎）+ `Modeling/MachineState.cs`（布局/模型/持久化）+ `Ui/MachineStateView.xaml(.cs)`（画布）+ 窗口 `RefreshMachineStates` / `SyncMachineTimer`（见 **§5.5**） |
| **系统变量监控（P2.3，第四分段）** | `SystemVarRow.cs` + `SystemVariableValues.cs` + 窗口 `RefreshSystemVarValues` / `SyncSystemVarTimer` / `IoView_Checked`（见 **§5.4**） |
| **全局变量（P3.3，只管理定义）** | `Modeling/GlobalVariable.cs`（模型/规则/持久化）+ `GlobalVarItem.cs`（界面包装）+ 窗口 `GlobalVar*` 处理器（见 **§5.2**；不读不写） |
| **采集数据（P3.1，任务组/数据项/执行/CSV）** | 模型 `Modeling/AcquisitionTask.cs`（+ `AcquisitionTaskRules` 文案与类型规则）+ 行包装 `AcquisitionTaskItem.cs` + 对话框 `Ui/AcquisitionTaskDialog.cs`；执行与落盘 `Modeling/AcquisitionCsv.cs` + 窗口 `RunAcquisitionTick` / `RefreshAcquisitionGlobals`（50ms 常驻定时器，**不随页面可见性停**，见 **§5.7**） |
| **采集任务「配置错误」关开关/拦截** | 判据 `Modeling/AcquisitionTaskCheck.cs`（纯逻辑）+ 行属性 `HasConfigError` / `EffectiveEnabled` / `CanToggleEnabled`；提示在 `AcquisitionTaskRowTemplate` 第 4 列开关右侧；执行侧只判 `EffectiveEnabled`（`RefreshAcquisitionGlobals` 与 `RunAcquisitionTick`，见 **§5.7**） |
| **采集「写入任务」真正下发** | `Modeling/AcquisitionWrite.cs`（TryBuild / DescribeFailure）+ `RunWriteTaskAsync`；Skipped=无地址静默跳过、失败不短路；CSV 只记写入变量回读值；只读区在 `GlobalVariableStore.TryResolve` 拦（X 从此不能映射）（见 §5.7） |
| **采集跟随可见性（三分段统一）** | `_ioPageActive`（页面级）+ `_freeViewActive` / `_yViewActive` → `PollOnceAsync` 开头；详见 `Design.md` §5.6 |
| **自由监控写入（FC05/06/16）** | `MonitorItemSet.TryBuildWrite`（文本 → `WritePlan`）+ `ValueCodec.TryEncodeRegisters` / `EncodeStringFixed`；`MainWindow.xaml.cs` 的 `CommitMonitorValueAsync` → `WriteToDeviceAsync`；报文层 `ModbusTcpClient.WriteSingleCoilAsync` / `WriteSingleRegisterAsync` / `WriteMultipleRegistersAsync` |
| **写入的文本解析与范围校验** | `ValueCodec.TryParseIntegerLiteral`（DEC 支持 `0x`/`0b` 前缀与负号）+ `CheckIntegerRange`；**校验必须在位宽截断前**（`-32769` 截成 `0x7FFF` 会变成合法值） |
| 改 IO 行高/行距、列宽 | `IoItemTemplate` 的 `Height`/`Margin`；`IoColumnsGrid`/`YIoColumnsGrid`（4×`*`） |
| 加/改 X-Y 分段切换 | `IoSegmentRadioButton` + `XViewSegment`/`YViewSegment` + `IoView_Checked`（自由监控 = 第三个 `FreeViewSegment`） |
| **自由监控增删改/校验** | `MonitorItem.cs`（`MonitorItem` / `MonitorItemSet`）+ `MainWindow.xaml.cs` 的 `Monitor*` 处理器（见 §5.1） |
| **元件名称输入 → 符号+编号拆分** | `CommDriver.TrySplitName`（`TryResolve` 与自由监控共用） |
| **数据类型候选（线圈/寄存器）** | `CommDriver.AreasOfElement` + `DataTypes.CoilTypes` / `RegisterTypes` |
| **监控项持久化** | `DataPointStore`（`data-points.json`）；模型 `Modeling/DataPoint.cs` |
| **字节顺序（字序/字符串序）** | `Modeling/ByteOrderSettings.cs`（三套值）+ 设备配置页三个下拉框 + 窗口 `ApplyByteOrders` / `SyncByteOrderCombos`（见 §8.1）；读取用 `MonitorItemSet.ByteOrders` |
| **类型值编解码与显示格式化** | `Modeling/ValueCodec.cs`（入参传字节顺序，不读全局状态） |
| Modbus 报文/超时 | `Drivers/ModbusTcpClient.cs` |
| 心跳周期/空闲判断/地址来源 | `Drivers/ModbusHeartbeat.cs` → `RunAsync`；`ModbusTcpClient.HeartbeatIfIdleAsync` |
| 轮询与断线逻辑 | `Polling/ModbusPoller.cs` 的 `PollLoopAsync` / `PollOnceAsync` + `Disconnect(status,color)` |
| **IO 合法异常码不再断开（连续 5 次才记日志）** | `ModbusPoller.PollOnceAsync` 的 `catch (ModbusResponseException)` + `ConsecutiveExceptionThreshold` + `_blockExceptionStreaks`；告警通道 `ICollectorHost.ReportCommunicationWarning` → 窗口 `ReportCommunicationWarning`（见 §5，证据 `Design.md` §10.2） |
| 网卡扫描/绑定/记忆 | `Net/NicScanner.cs`（`NicInfo` / `Scan` / `IndexOf` / `RestoreIndex`）；窗口侧 `ScanNetworkAdapters` / `TargetNicComboBox_SelectionChanged` / `ConnectButton_Click` |
| 采集周期档位 | `PollIntervalComboBox` + `_pollIntervalMs` |
| **日志编排（上限 / 落盘旁路 / 级别映射）** | `Ui/DeviceLogPresenter.cs`（`Append` / `Clear` / `RefreshThemeColors` / `ToFileLevel`）；窗口侧 `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `RefreshDeviceLogColors` 只是薄包装（见 **§7.1**） |
| 日志颜色与格式 | `LogEntry`（`DeviceLogLevel` → `LevelBrush`，类在 `Ui/DeviceLogPresenter.cs`）+ `DeviceLogItemTemplate` |
| **日志落盘（改落盘行为必读）** | `Logging/DeviceLogFileWriter.cs`；窗口侧 `_logWriter` / `RefreshLogSaveStatus`；级别映射 `Ui/DeviceLogPresenter.ToFileLevel`；独立验证 `LoggingCheck`（见 **§7.1**） |
| 断开原因归类 | `Logging/DisconnectReasonClassifier.cs`（规则本体，纯函数、可独立断言）；窗口侧 `ClassifyDisconnectReason` 只是薄包装，为满足 `ICollectorHost` |
| 连接任务隔离/关闭清理 | `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed` |

## 11. 版本控制

- git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`
- 身份为仓库级配置（`G.Sheng`）
- **提交信息用中文**；提交前先 `git diff` 确认范围，**不要整文件盲加**（会把别人的 WIP 一起带走）
