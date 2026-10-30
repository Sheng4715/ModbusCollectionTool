# AGENTS.md — DSH_Modbus采集工具

基于 **WPF / .NET 8** 的 Modbus 数据采集上位机 Demo，界面参考 Windows 11 Fluent（自定义标题栏 + 左侧导航 + 卡片式内容）。**无第三方 NuGet 依赖。**

> 需求与优先级见 `Brief.md`；技术设计与决策理由见 `Design.md`；本文件只讲**代码在哪、怎么改**。
>
> ⚠️ **本文件只保留「每次都要用的规则」**。各功能的实现细节已拆到 **[`AGENTS-细节.md`](AGENTS-细节.md)**，
> 涵盖 `AGENTS-细节.md` 的 §5 自由监控/全局变量/系统变量/状态机/采集、§6 心跳与重连、
> §7 运行日志、§8 通讯驱动 —— **编号不变**。
> 原因是本文件曾在 136 KB 时**超过工作区指令预算（65536 字节）而被整体丢弃**，规则一条都没进上下文。
> **改到上述任一功能时，动手前先读 `AGENTS-细节.md` 的对应章节** —— 那里有成条的 ⚠️ 坑。
>
> 📏 **预算红线**：本文件当前约 **59 KB**，加上框架开销（约 365 B）后余量只有 **4.4 KB**。
> **新增内容前先算一下总字节**（`(Get-Item AGENTS.md).Length`）；超过 64 KB 就又会被静默丢弃。
> 需要写长段落时放进 `AGENTS-细节.md`，本文件只留一句指向。

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
  系统时间写入 `obj/.../BuildInfo.g.cs`。详见 `AGENTS-细节.md` §7.3

## 2. 目录结构

```
DSH_Modbus采集工具/
├── App.xaml / App.xaml.cs      # 入口（OnStartup 里先 ThemeManager.Initialize）
├── AssemblyInfo.cs             # 仅供 ThemeInfo 特性等程序集级声明（无逻辑）
├── AppInfo.cs                  # 项目信息（P5.3）：版本号 / 构建日期 / 运行环境（只读程序集元数据）
├── MainWindow.xaml             # 全部 UI（单窗口，唯一例外是状态机画布 MachineStateView）
├── MainWindow.xaml.cs          # 交互逻辑主体（见 §4 分工）
├── MainWindow.MachineState.cs  # partial：状态机画布接线（2026-10-29 拆分，见 §4）
├── MainWindow.Acquisition.cs   # partial：采集数据分段
├── MainWindow.GlobalVar.cs     # partial：全局变量分段
├── MainWindow.Monitor.cs       # partial：自由监控分段
├── MainWindow.IoImport.cs      # partial：IO 表导入
├── MonitorItem.cs              # 自由监控项：DataPoint 的界面包装 + 集合（增删改/落盘/解析）
├── GlobalVarItem.cs            # 全局变量：GlobalVariable 的界面包装 + 集合（增删改/落盘）
├── AcquisitionTaskItem.cs      # 采集任务组/数据项的界面包装（列随类型变、配置错误态）
├── SystemVarRow.cs             # 系统变量监控行（P2.3）
├── Expressions/                # 表达式引擎（不含 WPF 类型）：词法 → 语法 → 编译 → 取值
│   ├── ExpressionLexer.cs      # 词法分析
│   ├── ExpressionParser.cs     # 递归下降语法分析
│   ├── ExpressionNode.cs       # 语法树节点
│   ├── ExpressionValue.cs      # 求值结果（数值 / 布尔）
│   └── CompiledExpression.cs   # 编译入口 TryParse / 名字收集 TryValidateNames
├── Themes/                     # 主题（P5.1）
│   ├── Light.xaml              # 浅色字典；值 = 主题化前的字面值
│   ├── Dark.xaml               # 深色字典（与 Light 同键，ThemeManager 启动校验）
│   ├── DialogControls.xaml     # 对话框专用样式（Dialog.* 前缀，App.xaml 合并进 Application.Resources）
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
│   ├── IoTableImporter.cs      # xlsx → 注释表 + 失败明细
│   └── XlsxIoTableReader.cs    # xlsx 解析（零依赖 zip + XML）
├── Modeling/                   # 数据类型系统（P6.4 数据模型层）
│   ├── DataTypes.cs            # DataType / DisplayFormat 枚举 + DataTypeSpec 规格表
│   ├── ValueCodec.cs           # 原始寄存器 ↔ 类型值 编解码 + 显示格式化
│   ├── ByteOrderSettings.cs    # 三套字节顺序（整数 / 浮点 / 字符串）的值类型（P4.3）
│   ├── DataPoint.cs            # 监控项模型 + data-points.json 持久化 + 驱动校验解析
│   ├── WritePlan.cs            # 一次写入的报文参数（FC05/06/16 三选一）；两条写入路径共用
│   ├── GlobalVariable.cs       # 全局变量模型 + 变量名规则 + GlobalVar.json 持久化（值不落盘）
│   ├── CompletionSource.cs     # 变量名补全的纯逻辑：取词 + 筛选 + 候选表（P6.5，不含 WPF）
│   ├── GlobalReadPlan.cs       # 全局变量读计划 + 回填（按可见页面采集，P1.2，不含 WPF）
│   ├── SystemVariable.cs       # 16 项系统变量清单（写死，不落盘）
│   ├── SystemVariableValues.cs # 系统变量取值（脉冲 / 时钟 / 运行时长，纯函数）
│   ├── MachineState.cs         # 状态机模型 + 布局常量 + Lamps + machine-state.json 持久化
│   ├── AcquisitionTask.cs      # 采集任务组/数据项模型 + 规则 + acquisition-tasks.json
│   ├── AcquisitionCsv.cs       # CSV 建文件 / 表头 / 追加 / 列变换文件
│   ├── AcquisitionSource.cs    # 来源变量解析（系统变量优先于全局变量）
│   ├── AcquisitionTaskCheck.cs # 配置错误判据（纯逻辑）
│   └── AcquisitionWrite.cs     # 写入任务的编码与 TryBuild（纯逻辑）
├── Logging/                    # 日志落盘与文案规则（不含 WPF 类型）
│   ├── DeviceLogFileWriter.cs  # 按天追加纯文本 .log + 跨天换文件 + 失败自愈
│   └── DisconnectReasonClassifier.cs  # 异常原文 → 简短中文（底栏与日志共用）
├── Net/                        # 网卡枚举与选择记忆（不含 WPF 类型）
│   └── NicScanner.cs           # 网络接口 → NicInfo + 「上次选择」还原优先级
├── Ui/                         # 窗口呈现辅助与对话框（引 WPF，但不碰业务）
│   ├── MaximizedInset.cs       # 最大化溢出补偿：物理像素测量 + 按 DPI 内缩根元素
│   ├── WindowCorners.cs        # 窗口圆角 + 边缘线（DWM 绘制，见 §3.8）
│   ├── StatusBarPresenter.cs   # 底栏：状态文字 / 状态点颜色 / 右侧连接参数
│   ├── DeviceLogPresenter.cs   # 运行日志：内存集合 + 上限裁剪 + 落盘旁路 + 级别映射
│   ├── CompletionPopup.cs      # 补全下拉：Popup 绘制 + 键盘/鼠标 + 输入法抑制（P6.5，见 `AGENTS-细节.md` §5.6）
│   ├── DialogWindow.cs         # 对话框基类：外壳 / 按钮行 / 内联错误 / 回车与 ESC（见 §9.3）
│   ├── DialogChrome.cs         # 对话框字体 / 底色 / 深色标题栏
│   ├── ColorPalette.cs         # 30 色 drawio 色盘（两个编辑窗共用）
│   ├── MachineStateView.xaml(.cs) # 状态机画布（本工程唯一的 UserControl）
│   ├── MachineStateEditDialog.cs  # 状态框编辑窗
│   ├── MachineLampEditDialog.cs   # 指示灯编辑窗（见 `AGENTS-细节.md` §5.5）
│   ├── AcquisitionTaskDialog.cs   # 任务选项对话框（名称/类型/执行方式/表达式/记录）
│   └── NumberInputDialog.cs    # STRING 长度等单值输入弹窗
└── Drivers/
    ├── CommDriver.cs           # 模型：ModbusArea / NumberBase / AddressArea / CommDriver（含字节顺序枚举）
    ├── DriverRegistry.cs       # 品牌地址数据表（含各品牌字节顺序约定）
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（MBAP + FC01/02/03/04 读 + FC05/06/16 写 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```

> **不引用 WPF 的层**（可脱离界面阅读/验证，也能被独立工程链接源码断言）：`Drivers/`、`Expressions/`、
> `Io/`、`Logging/`、`Modeling/`、`Net/`、`Polling/` —— 这 7 个目录下**没有任何** `using System.Windows*`。
> 控件写入（分页控件显隐、绑列、弹窗、`MessageBox`）一律留在 `MainWindow.*.cs`。
> 新增逻辑时按同一原则归位。
>
> **引 WPF 的层**：`Ui/`（窗口呈现辅助与对话框，不碰业务）与 `Themes/`（`ThemeManager` 要换资源字典），
> 以及根目录的 `MainWindow*.cs` 五个 partial / `MonitorItem.cs` / `GlobalVarItem.cs` /
> `AcquisitionTaskItem.cs` / `SystemVarRow.cs` / `App.xaml.cs`。
> ⚠️ `MonitorItem.cs` 是「`DataPoint` 的界面包装」、碰 WPF 属正常，但它在**根目录**而不在 `Ui/`
> —— 判断一个文件该不该碰 WPF 看**所在目录**，不要以为「根目录都不碰」。

> `ModelingCheck/`（仓库根，**不属于本工程**）是 P6.4 的独立验证工程：用真机实测数据对编解码做断言，不参与主程序构建。
> `LoggingCheck/`（同上）是 P5.2 的独立验证工程：链接 `DeviceLogFileWriter.cs` 与 `DisconnectReasonClassifier.cs` 源码，断言逐字符行格式 / 跨天换文件 / 续写 / 容错 / 断开原因归类，**44 项**，不参与主程序构建。

> `.tmp/` 下另有一批**一次性**验证工程（**未提交**，随用随弃）。**断言型**（自带 `Check()`，直接 `dotnet run` 即出结论）：
> `ConnCheck`（退避策略 + 连接身份 / 两种断开语义，**35 项**）、`DeviceLogCheck`（日志编排：上限裁剪 / 级别映射 / 共用时间戳 / 清空 / 换主题重建，**23 项**）、`NicCheck`（网卡选择还原优先级，**17 项**）、`PollerCheck`（IO 轮询异常码策略，**10 项**）。
> **探针型**（无断言，需传参数人工读输出）：`IoCheck`（接 xlsx 路径，打印 `IoTableImporter` / `IoPager` 的解析与分页结果）、`FakeSlave` 与 `E2ESlave`（端到端验证用最小假从站）。
> 它们验的是「改一次就够」的行为，不值当长期维护；要重跑时会话记录里有完整源码。
>
> **每次改完的验证口径（2026-10-29 用户定案）**：只跑 **(a) `dotnet build DSH_Modbus采集工具.sln` 与 (b) `.tmp/ExpressionCheck`**（`dotnet build` 该工程后直接跑 exe，不要用 `dotnet run`——`dotnet run` 每次要多花约 5 s 在 SDK/NuGet 探测上）。其余断言工程（GlobalVarCheck / AcquisitionCheck / ConnCheck / DeviceLogCheck / MonitorVarCheck / NicCheck / SystemVarCheck）**不再在每次提交前全跑**；只有改动直接触及它们的断言对象时才单独跑那一个。原因：8 个工程各自 link 同一批产品源码重复编译，合计纯机器时间约 37 s，加上杀进程与全量构建约 46 s，而提交本身不到 1 s。

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
| `OverviewNav` 总览 | `OverviewPanel` | 视图切换在**页头右侧下拉框**「当前视图」（`FormComboBox`）：「PLC状态机」（P1.2，见 `AGENTS-细节.md` §5.5）/「生产数据」（占位）；卡片内只有画布 + 底部运行日志卡（状态显示已移至全局底栏，见 §3.5） |
| `StatusInfoNav` 设备监控 | `StatusInfoPanel` | IO 监控（**四分段**：输入 X / 输出 Y / 自由监控 / 系统变量）+ 分页 |
| `DeviceConfigNav` 设备连接 | `DeviceConfigPanel` | 连接表单 |
| `AcquisitionConfigNav` 数据管理 | `AcquisitionConfigPanel` | **三分段**：「采集数据」（P3.1，执行与 CSV 均已实现，见 `AGENTS-细节.md` §5.7）/「全局变量」（P3.3，见 `AGENTS-细节.md` §5.2）/「系统变量」（P3.5，只读清单，见 `AGENTS-细节.md` §5.3） |
| `SettingsNav` 设置 | `SettingsPanel` | 显示外观（P5.1）+ 自动连接（见 `AGENTS-细节.md` §7.2）+ 日志保存（P5.2）+ 项目信息（P5.3） |

切换逻辑：`NavRadioButton` 样式里的 `EventSetter Checked=Nav_Checked`，按 sender 显隐面板。

### 3.2 设计器预览

五个面板同处一个 `StackPanel`，运行时由 `Nav_Checked` 切换，而**设计器只认 `d:Visibility`**（`d:` 在 `mc:Ignorable="d"` 中，不影响编译与运行）。

- 当前 `OverviewPanel` = `d:Visible`（设计器默认预览总览页）
- 要预览其它页：目标页改 `d:Visible`，其余改回 `d:Collapsed`
- 总览页 `Height` 绑 `ContentScrollViewer.ViewportHeight`，设计器里常解析不出 → 调样式时临时注释该绑定

### 3.3 关键样式（Window.Resources）

> 全部定义在 `MainWindow.xaml` 的 `Window.Resources` 里（**43 个键**）。改外观先在这里找现成的；
> 下表只列**会复用、或踩过坑**的那些，其余是某一行专用的模板（`*ItemTemplate`）。

**先认名字前缀**：`Io*` = IO 页/分页、`Form*` = 设备连接页表单、`Monitor*` = 自由监控行内、
`Acq*` = 采集任务组行内、`GlobalVar*` / `SystemVar*` = 后两个分段的模板、`Dialog.*` = 对话框（在 `Themes/DialogControls.xaml`，见 §9.3）。

| Key | 用途 |
|-----|------|
| `CaptionButton` / `CloseCaptionButton` | 标题栏按钮（关闭悬停红底） |
| `NavRadioButton` | 导航项（灰底背板，选中加粗） |
| `NavItemTemplate` | 导航项的共享 `ControlTemplate`：外距/内距/高度全从 Style 传，**大导航与（已删的）子导航曾共用它**。要做不同缩进改 Style，别复制模板 |
| `IoSegmentRadioButton` | 分段控件（IO 页 X/Y/Z、数据管理三档、主题三档都用它） |
| `ToggleSwitch` | **开关**（Windows 11 式 ToggleSwitch）：轨道 40×20、柄 12、内缩 4；关＝`Control.Background`、开＝`Accent.Fill`；**状态文字在轨道左侧**，由代码按 `IsChecked` 改写 `Content`（「开」/「关」） |
| `IoItemTemplate` | IO 点位单元格（灯 10×10、注释 MinWidth 144；`Comment` 为 INPC） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `IoPagerButton` | 分页箭头（26×26，首末页置灰） |
| `IoCardButton` | **通用文字按钮**（浅灰实底胶囊／圆角 6／高 26／字号 12；底色走 `Brush.Control.Background`） |
| `FormButton` | 设备连接页按钮，`BasedOn` `IoCardButton`，仅字号 14、内边距 14 |
| `FormComboBox` | 设备连接页下拉框：**胶囊**（灰底 + 圆角 6 + chevron），字号 14／高 26。默认 `BorderThickness=0`（无框线）；模板已 `TemplateBinding` `BorderBrush`/`BorderThickness`，个别位置需要框线时自己设 1 即可（总览页「当前视图」就是） |
| `FormComboItem` | 表单下拉项容器（含 `ItemContainerStyle`，勿漏） |
| `FormTextBox` | 设备连接页输入框：**框线**（白底 + `Brush.Input.Border` 圆角 4），字号 14／高 26 |
| `MonitorInlineComboBox` / `MonitorTypeCombo` | 自由监控行内下拉框：胶囊，字号 12／高 22 |
| `MonitorComboItem` | 行内下拉项容器 |
| `MonitorTypeItemTemplate` | 类型下拉的**展开项**模板，绑 `{Binding DisplayName}` —— 数据源必须是 `DataTypeSpec` 对象，放字符串会整列空白（见 `AGENTS-细节.md` §5.2） |
| `MonitorInlineTextBox` / `MonitorValueTextBox` | 自由监控行内输入框：框线，字号 12／高 22 |
| `MonitorItemTemplate` | 自由监控整行（名称/类型/格式/当前值/删除） |
| `MonitorIconButton` | 监控行尾删除 ×（**悬停红**＝删除语义） |
| `MonitorHeaderIconButton` | 表头新增 ＋（**悬停主色**＝新增语义） |
| `MonitorClearAllButton` | 表头「🗑 清空」 |
| `GlobalVarItemTemplate` | 全局变量整行（序号/名称/类型/映射地址/注释/删除） |
| `SystemVarItemTemplate` | 系统变量清单行（只读，无输入框） |
| `SystemVarMonitorItemTemplate` | 系统变量监控行（多一列「当前值」） |
| `AcqRowIconButton` | **采集任务组行内图标按钮**（… ↑↓）：22×22，悬停中性灰底 + 深字。**刻意不复用** `MonitorIconButton`（悬停红＝删除）和 `MonitorHeaderIconButton`（悬停主色＝新增）—— 同一行里混两种语义会读错 |
| `AcqRowDeleteButton` | 任务组行「删除本组」×：`BasedOn` `AcqRowIconButton`（尺寸/字体全继承），**只把悬停换成实心红**（`Brush.Danger.Fill.Hover` + 白字），与全局变量行、监控行的行尾删除一致 |
| `AcqEnableSwitch` | 采集任务组「启用」开关，`BasedOn` `ToggleSwitch`，只补「文案随 `IsChecked` 变」（开/关）。⚠️ 必须在 `Style.Triggers` 里换 `Content`，模板触发器压不过局部值 |
| `AcquisitionSourceBox` / `AcquisitionTargetBox` | 采集写入任务的「来源变量」/「写入变量」输入框，`BasedOn` `MonitorInlineTextBox`，只加「有错时字变红」。⚠️ `BasedOn` 要求被继承者**先定义** |
| `AcquisitionTaskRowTemplate` | 采集任务组行（7 列：#/名称/类型/执行方式/启用/共N项/… × ↑↓）+ 展开的数据项子表（浅底块） |
| `AcquisitionItemRowTemplate` | 采集数据项行（序号 / 项名称 / 变量名称 / **数据类型只读** / 删除） |
| `LogClearButton` | 日志卡「清空」按钮 |
| `DeviceLogItemTemplate` | 日志行（时间戳灰 + 消息按级别着色） |
| `DeviceLogListStyle` | 日志列表（透明底/无边框/无选中底色） |
| `AreaChipTemplate` / `AreaItemsControlStyle` | 设备连接页「地址表元件」那排地址 chip |
| `IconFont` | 图标字体族（`Segoe Fluent Icons, Segoe MDL2 Assets`） |
| `EnumUpperConverter` / `EnumUpperTemplate` / `BoolToVis` | 三个通用转换器/模板（枚举转大写、布尔转 `Visibility`） |
| **隐式 `ScrollBar`**（**无 `x:Key`**） | 细滚动条（10px）。⚠️ 隐式样式是唯一能命中 `ScrollViewer` 内部创建的滚动条的手段，**别改成 keyed** |

### 3.4 页面细节

- **总览**：顶部仅「总览」标题（原状态卡已移除，连接状态统一在全局底栏）。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**，另两页照滚。
- **设备监控**：见 `AGENTS-细节.md` §5 IO 监控 / `AGENTS-细节.md` §5.1 自由监控。
- **设备连接**：连接方式 / 协议 / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮；**设备通讯区域**另有左「字节顺序」3 个下拉框（32 位整数 / 32 位浮点 / 字符串）+ 竖分隔线 + 右「地址表元件」（见 `AGENTS-细节.md` §8.1）。连接中锁定表单（`SetInputsEnabled`）——**例外是三个字节顺序下拉框**：它们不改报文、不改连接参数，只决定怎么解释寄存器，改完下一拍即生效，锁上反而挡住现场纠正（见 `AGENTS-细节.md` §8.1）。串口模式仅隐藏以太网面板，**功能未实现**。

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

- **两个控件名沿用自旧设备连接表单**，故 `SetStatus()` 逻辑无需改动 —— 它写的就是这两个名字
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
- 卡片**外框**已与页面大标题左对齐；卡片**内容**再往内缩各自 `Padding.Left`（各卡不同：设置页三卡 24、设备连接 20、设备监控 12、总览日志卡无）。这是卡片内边距，不是错位

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

## 4. 代码分工（`MainWindow` 的六个 partial）

> **2026-10-29 拆分**：`MainWindow.xaml.cs` 曾长期贴着 4600 行，现已拆成六个 partial ——
> 主体（`MainWindow.xaml.cs`）+ `MachineState`(444) / `Acquisition`(669) / `GlobalVar`(567) /
> `Monitor`(849) / `IoImport`(95)。**新建处理器先按页面归到对应 partial**，
> 只有跨页面的（连接生命周期 / 心跳 / 导航 / 日志 / 底栏）才写回主体。
>
> 已抽出去的部分：采集编排 → `Polling/`、IO 点位集/分页/导入 → `Io/`、主题 → `Themes/`、
> 呈现规则 → `Ui/`。**自由监控刻意未抽成独立的层** —— 它有 12 处直接碰控件，见 `Design.md` §8-1。
>
> ⚠️ **不要在这里写 `MainWindow.xaml.cs` 的具体行数**：该数字已三次未跟上（曾同时出现
> 2590 / 2190 / 2187 三个互相矛盾的值），它对判断没有作用，只会下次再骗人一次。

| 区块 | 关键成员 |
|---|---|
| **处理器在哪个 partial** | `MainWindow.xaml.cs`＝连接/心跳/导航/底栏/IO 分页·导入·`SetInputsEnabled`/`AppendDeviceLog`/`SetStatus`/`ScanNetworkAdapters`；`MainWindow.Monitor.cs`＝`Monitor*`·`CommitMonitorValueAsync`·`WriteToDeviceAsync`·`ClassifyDisconnectReason`；`MainWindow.GlobalVar.cs`＝`GlobalVar*`·`RefreshMachineGlobals`；`MainWindow.Acquisition.cs`＝`RunAcquisitionTick`·`RunWriteTaskAsync`·`RefreshAcquisitionGlobals`·`WriteToDeviceAsync`；`MainWindow.MachineState.cs`＝`SyncMachineTimer`·`RefreshMachineStates`·`RefreshMachineGlobals`；`MainWindow.IoImport.cs`＝IO 表导入 |
| 连接状态机 | `ConnectButton_Click`（表单校验 + 按钮三态）/ `TryConnectAsync` / `Disconnect()` / `Disconnect(status,color)` / `SetInputsEnabled`；**身份与生命周期在 `Polling/ConnectionSession.cs`**（`IsConnecting` / `IsCurrent` / `MarkConnected` / `ReleaseClient` / `ForgetTarget` / 重连计数）+ `RunConnectionTasksAsync` / `IsCurrentConnection` |
| 自动重连 | `StartReconnect` / `ReconnectLoopAsync` / `CancelReconnect`（**循环留在窗口**，逐秒刷状态栏/锁表单是界面驱动）；目标快照 `ReconnectTarget` 与退避表 `ReconnectPolicy` 在 `Polling/` |
| 轮询（入口在本类，实现在 `Polling/`） | `_poller.PollLoopAsync` → `Polling/ModbusPoller.cs`（`PollOnceAsync` 按地址区分块 + 2000 点切窗 / `PollFreeMonitorAsync` 自由监控，按区选 FC01/03/04） |
| `ICollectorHost` 实现 | `PollIntervalMs` / `MonitorSet` / `CaptureSnapshot()` / `IsCurrentConnection` / `RunOnUiAsync` / `NotifyConnectionLost` / `ClassifyDisconnectReason` / `ReportCommunicationWarning`（连续异常码告警，见 `AGENTS-细节.md` §5） |
| 心跳编排 | `HeartbeatLoopAsync`（失败时转 `_poller.NotifyConnectionLost`） |
| IO 点位集（实现在 `Io/`） | `RebuildIoPoints`（薄包装）→ `IoPointSet.Rebuild` / `TryCreatePoint` / `NormalizePointName` / `TryValidatePoint` |
| 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（算术在 `IoPager`；控件写入留本类） |
| 导入 | `ImportIoTableButton_Click` → `IoTableImporter.Read`（`MessageBox` 留本类） |
| **自由监控** | `AddMonitorItemButton_Click` / `RemoveMonitorItem_Click` / `MonitorName_TextChanged` / `MonitorName_LostFocus` / `SyncMonitorTypeWithElement` / `ApplyMonitorRowState` / `RefreshMonitorView` / `FocusMonitorRowName` / `RowOf` / `MonitorValue_GotFocus` / `MonitorValue_LostFocus` / `MonitorValue_KeyDown` / `CommitMonitorValueAsync` / `WriteToDeviceAsync` / `ClassifyWriteError`（见 `AGENTS-细节.md` §5.1） |
| **系统变量（P3.5）** | 清单写死在 `Modeling/SystemVariable.cs`；窗口侧只有 `AcqView_Checked` 切面板 + 构造期一次绑定，**没有任何编辑处理器**（见 `AGENTS-细节.md` §5.3） |
| **全局变量（P3.3）** | `AddGlobalVar_Click` / `RemoveGlobalVar_Click` / `ClearGlobalVars_Click` / `GlobalVarName_TextChanged` / `GlobalVarName_LostFocus` / `GlobalVarAddress_LostFocus` / `GlobalVarComment_LostFocus` / `GlobalVarType_SelectionChanged` / `RefreshGlobalVarView` / `FocusGlobalVarRow` / `AcqView_Checked`（见 `AGENTS-细节.md` §5.2；**只管理定义，不读不写**） |
| 持久化 | `LoadComments` / `SaveComments`（薄包装 → `IoPointSet.Load` / `Save`） |
| 网卡 | `ScanNetworkAdapters`（闸门 + 填下拉框）/ `TargetNicComboBox_SelectionChanged`；扫描与还原规则在 `Net/NicScanner.cs`。选择持久化见 §3.4 |
| 日志 | 窗口侧只留三处**控件**动作：`AppendDeviceLog`（薄包装 → 追加 + `ScrollIntoView` + 设置页失败提示）、`ClearDeviceLogButton_Click`、`RefreshDeviceLogColors`（薄包装）；**编排与规则在 `Ui/DeviceLogPresenter.cs`**（内存集合 / 上限 200 / 落盘旁路 / **界面与文件共用时间戳** / 界面级别 → 落盘级别）；`ClassifyDisconnectReason` 仍是薄包装 |
| 底栏 | `SetStatus` / `UpdateOverviewStatus` / `BuildConnectionDetail`（窗口侧薄包装）→ `Ui/StatusBarPresenter.cs`（规则本体）；`UpdateMaximizedInset` → `Ui/MaximizedInset.cs` |
| **主题（P5.1）** | `Themes/ThemeManager.cs`；窗口侧 `ThemeSegment_Checked` / `SyncThemeSegments` / `OnThemeChanged`（底栏的状态色映射在 `Ui/StatusBarPresenter.cs`）；设置页 `SettingsPanel` + `SettingsNav`（见 §9.2） |
| **日志落盘（P5.2）** | `Logging/DeviceLogFileWriter.cs`（按天追加 / 跨天换文件 / 失败自愈）；窗口侧 `_logWriter` / `LogSaveToggle_Changed` / `SyncLogSaveToggle` / `LogDirectoryTextBox_LostFocus` / `LogBrowseButton_Click` / `LogOpenFolderButton_Click` / `LogResetDirectoryButton_Click` / `ApplyLogDirectory` / `RefreshLogSaveStatus`（见 `AGENTS-细节.md` §7.1） |
| **项目信息（P5.3）** | `AppInfo.cs`（读程序集元数据）+ csproj 的 `VersionPrefix`/`VersionSuffix` 与 `WriteBuildDate` 目标；窗口侧 `RefreshProjectInfo`（见 `AGENTS-细节.md` §7.3） |
| 关键字段 | `_session`（连接身份与生命周期）/ `_pollCts` / `_windowClosed` / `_pollIntervalMs` / `_currentDriver` / `_poller` / `_ioSet`（内含 X/Y 点位列表与注释表）/ `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_monitorSet` / `_globalVarSet` / `_logPresenter`（日志呈现器）/ `_logWriter` / `_statusBar`（底栏呈现器）（**哪些是 `volatile` 以上表 §3.1 为准**：`_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` 共 7 个） |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控 · 6. 心跳与连接生命周期 · 7. 运行日志 · 8. 通讯驱动标准

> **这四章已整段移到 [AGENTS-细节.md](AGENTS-细节.md)，编号不变。**
> 原因：本文件曾达 136 KB，超过工作区指令预算（65536 字节）而被**整体丢弃**，
> 等于规则一条都没生效。现在本文件只留「每次都要用的规则」，实现细节按需打开。
>
> 涉及**自由监控 / 全局变量 / 系统变量 / 状态机 / 采集 / 心跳与重连 / 日志落盘 / 字节顺序**的改动，
> 动手前先读 `AGENTS-细节.md` 的对应章节（`§5.1` … `§8.1` 这些引用仍然有效，只是换个文件查）。

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
| 表单 | 14 | 26 | 设备连接页 |
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
| 窗口外壳（字体、边框、底色、深色标题栏） | `Ui/DialogWindow.cs` 基类构造里已调 `DialogChrome.Apply(this)` |

**一律继承 `Ui/DialogWindow.cs`**（2026-10-29 抽出，四个对话框共减 445 行样板）。它已经包掉：
居中归属（`AttachOwner`）、抢焦点 + `ShowDialog`（`ShowOwned`）、取不到样式就抛（`RequireStyle`）、
控件工厂（`AddLabel` / `Place` / `MakeTextBox` / `MakeCombo` / `MakeRadio` / `MakeButton`）、
`ComboItem<T>`（显示文本 + 真实取值，**不要靠字符串反解**）。
**子类只写「自己有几行、每行放什么」**，不要再抄外壳代码：

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

**八条容易踩的**：

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
   因为那 1px 落在被裁的像素上）。用 `Place(..., stretch: true)`。
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
8. ⚠️ **「行距对齐」= 行高固定，不是每行留一样的下边距**。`Place(..., margin)` 的 10 DIP
   是靠 `Auto` 行高**叠加**在内容高度上的，于是行距 = 内容高 + 10：同屏有单选行（16）、
   文本框行（30）、带按钮的路径行（32）时，实测行距变成 60/49/39/51/62/60 px（150% DPI），
   肉眼就是「每行间距都不一样」。**正确做法**是给表单行统一的 `RowDefinition.Height`
   （`AcquisitionTaskDialog` 用 40 = 该行最高的内容 32 + 呼吸），标签与控件都
   `VerticalAlignment=Center`；此时 `Place` 的边距要传 `Thickness(0)`、
   `AddLabel` 要传 `bottomMargin: 0`，否则那 10 又把内容顶偏。
   错误行与按钮行仍留 `Auto`：错误行多数时候折叠，按钮行不该被撑到 40。

## 10. 任务速查

| 需求 | 位置 |
|------|------|
| **加输入框 / 下拉框 / 按钮（新增控件必读）** | 套用现成样式，别手写外观 —— 见 **§9.1 控件风格约定** |
| **改任何颜色（必读）** | 用主题键，**不要写字面量** —— 见 **§9.2 主题与配色** |
| **新增 / 改对话框（必读）** | 继承 `Ui/DialogWindow.cs` + 用 `Dialog.*` 样式，别复用主窗口样式 —— 见 **§9.3 对话框控件** |
| **加/改主题、深色适配、跟随系统** | `Themes/`（`Light.xaml` / `Dark.xaml` 同键）+ `Themes/ThemeManager.cs`；C# 侧画刷见 §9.2 |
| **设置页（P5）** | `SettingsPanel`（`MainWindow.xaml`）+ `SettingsNav` + `Nav_Checked`；主题分段 `ThemeSegment_Checked`；日志开关 `LogSaveToggle_Changed`、路径 `LogDirectoryTextBox_LostFocus`；自动连接 `AutoConnectPlcToggle_Changed` / `SyncAutoConnectToggle` / `TryAutoConnect`（见 `AGENTS-细节.md` §7.2）；项目信息 `RefreshProjectInfo`（见 `AGENTS-细节.md` §7.3） |
| 改导航项/页面显隐 | `MainWindow.xaml` 导航区 + `Nav_Checked` |
| 加通讯品牌 | `Drivers/DriverRegistry.cs`（`AGENTS-细节.md` §8） |
| 加设备连接字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | 窗口 `BuildConnectionDetail()` 拼串 → `Ui/StatusBarPresenter.cs` 写 `StatusBarDetail` |
| 底栏状态文字/颜色 | `Ui/StatusBarPresenter.cs`（`Set` / `RefreshDetail` / `ReapplyTheme`）；窗口侧 `SetStatus` / `UpdateOverviewStatus` 只是薄包装 |
| 底栏布局/对齐 | `MainWindow.xaml` 主体 Grid 末尾的底栏 `Border`（见 §3.5，**改前必读**） |
| 最大化底栏被任务栏遮住 | `Ui/MaximizedInset.cs`（`Apply`）+ 窗口侧 `UpdateMaximizedInset` / `Window_StateChanged`（见 §3.6） |
| 自动重连退避/触发/取消 | `Polling/ReconnectPolicy.cs`（退避表）+ `Polling/ConnectionSession.cs`（目标/计数）+ 窗口侧 `StartReconnect` / `CancelReconnect`（见 `AGENTS-细节.md` §6.1） |
| IO 灯色（输入绿/输出橙） | `IoPoint.StateBrush` + `InputOnBrush` / `OutputOnBrush` / `OffBrush`；`IsOutput` 在 `TryCreatePoint` 定 |
| IO 点位集构建/排序 | `Io/IoPointSet.cs`（`Rebuild` / `TryCreatePoint` / `NormalizePointName`，按地址升序）；窗口侧薄包装 `RebuildIoPoints` |
| 导入 IO 表 / 解析规则 | `Io/IoTableImporter.cs` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格）；弹窗在 `ImportIoTableButton_Click` |
| 注释与点位集持久化 | `Io/IoPointSet.cs` 的 `Load` / `Save` → `io-comments.json`（窗口侧 `LoadComments` / `SaveComments` 为薄包装） |
| IO 分页 | `Io/IoPager.cs`（算术）+ `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（控件写入）；`_xPageIndex`/`_yPageIndex` 独立 |
| 改每页点位数 | `IoPager.RowsPerColumn` / `IoPager.ColumnCount`（PageSize 由其相乘得出） |
| IO 采集范围 | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| **自由监控读取（分块规划）** | `MonitorItemSet.BuildReadPlan`（按 `ModbusArea` 分组 + 地址连续性分块）+ `MonitorItemSet.ApplyBlock`（偏移切片 + 格式化回填） |
| **自由监控轮询入口** | `Polling/ModbusPoller.cs` 的 `PollOnceAsync` 开头按 `_freeViewActive` 分流 → `PollFreeMonitorAsync`（按区选 FC01/FC03/FC04，单块失败不断线） |
| **系统变量（P3.5，写死只读）** | `Modeling/SystemVariable.cs`（16 项清单）+ 窗口 `AcqView_Checked`（见 **`AGENTS-细节.md` §5.3**；无编辑入口） |
| **PLC 状态机（P1.2，总览页）** | `Expressions/`（表达式引擎）+ `Modeling/MachineState.cs`（布局/模型/持久化）+ `Ui/MachineStateView.xaml(.cs)`（画布）+ 窗口 `RefreshMachineStates` / `SyncMachineTimer`（见 **`AGENTS-细节.md` §5.5**） |
| **状态机指示灯 Error / Fault** | `MachineStateLamp`（含 Color/DotSize/FontSize）+ `MachineStateLayout.Lamps`/`LampDotSize`/`LampFontSize`（`Modeling/MachineState.cs`）+ 画布 `BuildLamps`/`ApplyLampState`/`LampVisual`（`Ui/MachineStateView.xaml.cs`）+ `Ui/MachineLampEditDialog.cs`（见 **`AGENTS-细节.md` §5.5**） |
| **系统变量监控（P2.3，第四分段）** | `SystemVarRow.cs` + `SystemVariableValues.cs` + 窗口 `RefreshSystemVarValues` / `SyncSystemVarTimer` / `IoView_Checked`（见 **`AGENTS-细节.md` §5.4**） |
| **全局变量（P3.3，只管理定义）** | `Modeling/GlobalVariable.cs`（模型/规则/持久化）+ `GlobalVarItem.cs`（界面包装）+ 窗口 `GlobalVar*` 处理器（见 **`AGENTS-细节.md` §5.2**；不读不写） |
| **采集数据（P3.1，任务组/数据项/执行/CSV）** | 模型 `Modeling/AcquisitionTask.cs`（+ `AcquisitionTaskRules` 文案与类型规则）+ 行包装 `AcquisitionTaskItem.cs` + 对话框 `Ui/AcquisitionTaskDialog.cs`；执行与落盘 `Modeling/AcquisitionCsv.cs` + 窗口 `RunAcquisitionTick` / `RefreshAcquisitionGlobals`（50ms 常驻定时器，**不随页面可见性停**，见 **`AGENTS-细节.md` §5.7**） |
| **采集任务「配置错误」关开关/拦截** | 判据 `Modeling/AcquisitionTaskCheck.cs`（纯逻辑）+ 行属性 `HasConfigError` / `EffectiveEnabled` / `CanToggleEnabled`；提示在 `AcquisitionTaskRowTemplate` 第 4 列开关右侧；执行侧只判 `EffectiveEnabled`（`RefreshAcquisitionGlobals` 与 `RunAcquisitionTick`，见 **`AGENTS-细节.md` §5.7**） |
| **采集「写入任务」真正下发** | `Modeling/AcquisitionWrite.cs`（TryBuild / DescribeFailure）+ `RunWriteTaskAsync`；Skipped=无地址静默跳过、失败不短路；CSV 只记写入变量回读值。**只读区（X / 离散输入 / 输入寄存器）只拦「写」不拦「映射」** —— 判定唯一来源是 `GlobalVariableStore.IsReadOnlyElement`（见 `AGENTS-细节.md` §5.7） |
| **采集跟随可见性（三分段统一）** | `_ioPageActive`（页面级）+ `_freeViewActive` / `_yViewActive` → `PollOnceAsync` 开头；详见 `Design.md` §5.6 |
| **自由监控写入（FC05/06/16）** | `MonitorItemSet.TryBuildWrite`（文本 → `WritePlan`）+ `ValueCodec.TryEncodeRegisters` / `EncodeStringFixed`；`MainWindow.Monitor.cs` 的 `CommitMonitorValueAsync` → `WriteToDeviceAsync`；报文层 `ModbusTcpClient.WriteSingleCoilAsync` / `WriteSingleRegisterAsync` / `WriteMultipleRegistersAsync` |
| **写入的文本解析与范围校验** | `ValueCodec.TryParseIntegerLiteral`（DEC 支持 `0x`/`0b` 前缀与负号）+ `CheckIntegerRange`；**校验必须在位宽截断前**（`-32769` 截成 `0x7FFF` 会变成合法值） |
| 改 IO 行高/行距、列宽 | `IoItemTemplate` 的 `Height`/`Margin`；`IoColumnsGrid`/`YIoColumnsGrid`（4×`*`） |
| 加/改 X-Y 分段切换 | `IoSegmentRadioButton` + `XViewSegment`/`YViewSegment` + `IoView_Checked`（自由监控 = 第三个 `FreeViewSegment`） |
| **自由监控增删改/校验** | `MonitorItem.cs`（`MonitorItem` / `MonitorItemSet`）+ `MainWindow.Monitor.cs` 的 `Monitor*` 处理器（见 `AGENTS-细节.md` §5.1） |
| **元件名称输入 → 符号+编号拆分** | `CommDriver.TrySplitName`（`TryResolve` 与自由监控共用） |
| **数据类型候选（线圈/寄存器）** | `CommDriver.AreasOfElement` + `DataTypes.CoilTypes` / `RegisterTypes` |
| **监控项持久化** | `DataPointStore`（`data-points.json`）；模型 `Modeling/DataPoint.cs` |
| **字节顺序（字序/字符串序）** | `Modeling/ByteOrderSettings.cs`（三套值）+ 设备连接页三个下拉框 + 窗口 `ApplyByteOrders` / `SyncByteOrderCombos`（见 `AGENTS-细节.md` §8.1）；读取用 `MonitorItemSet.ByteOrders` |
| **类型值编解码与显示格式化** | `Modeling/ValueCodec.cs`（入参传字节顺序，不读全局状态） |
| Modbus 报文/超时 | `Drivers/ModbusTcpClient.cs` |
| 心跳周期/空闲判断/地址来源 | `Drivers/ModbusHeartbeat.cs` → `RunAsync`；`ModbusTcpClient.HeartbeatIfIdleAsync` |
| 轮询与断线逻辑 | `Polling/ModbusPoller.cs` 的 `PollLoopAsync` / `PollOnceAsync` + `Disconnect(status,color)` |
| **IO 合法异常码不再断开（连续 5 次才记日志）** | `ModbusPoller.PollOnceAsync` 的 `catch (ModbusResponseException)` + `ConsecutiveExceptionThreshold` + `_blockExceptionStreaks`；告警通道 `ICollectorHost.ReportCommunicationWarning` → 窗口 `ReportCommunicationWarning`（见 `AGENTS-细节.md` §5，证据 `Design.md` §10.2） |
| 网卡扫描/绑定/记忆 | `Net/NicScanner.cs`（`NicInfo` / `Scan` / `IndexOf` / `RestoreIndex`）；窗口侧 `ScanNetworkAdapters` / `TargetNicComboBox_SelectionChanged` / `ConnectButton_Click` |
| 采集周期档位 | `PollIntervalComboBox` + `_pollIntervalMs` |
| **日志编排（上限 / 落盘旁路 / 级别映射）** | `Ui/DeviceLogPresenter.cs`（`Append` / `Clear` / `RefreshThemeColors` / `ToFileLevel`）；窗口侧 `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `RefreshDeviceLogColors` 只是薄包装（见 **`AGENTS-细节.md` §7.1**） |
| 日志颜色与格式 | `LogEntry`（`DeviceLogLevel` → `LevelBrush`，类在 `Ui/DeviceLogPresenter.cs`）+ `DeviceLogItemTemplate` |
| **日志落盘（改落盘行为必读）** | `Logging/DeviceLogFileWriter.cs`；窗口侧 `_logWriter` / `RefreshLogSaveStatus`；级别映射 `Ui/DeviceLogPresenter.ToFileLevel`；独立验证 `LoggingCheck`（见 **`AGENTS-细节.md` §7.1**） |
| 断开原因归类 | `Logging/DisconnectReasonClassifier.cs`（规则本体，纯函数、可独立断言）；窗口侧 `ClassifyDisconnectReason` 只是薄包装，为满足 `ICollectorHost` |
| 连接任务隔离/关闭清理 | `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed` |

## 11. 版本控制

- git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`
- 身份为仓库级配置（`G.Sheng`）
- **提交信息用中文**；提交前先 `git diff` 确认范围，**不要整文件盲加**（会把别人的 WIP 一起带走）
- **提交前的验证只跑两样（2026-10-29 用户定案）**：`dotnet build DSH_Modbus采集工具.sln` + `.tmp/ExpressionCheck`（跑 exe，不用 `dotnet run`）。其余断言套件不再全量复跑，详见 §2 末尾的同名说明。
