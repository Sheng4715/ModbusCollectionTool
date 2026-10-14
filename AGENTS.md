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

## 2. 目录结构

```
DSH_Modbus采集工具/
├── App.xaml / App.xaml.cs      # 入口
├── MainWindow.xaml             # 全部 UI（单窗口，无 UserControl 拆分）
├── MainWindow.xaml.cs          # 交互逻辑（见 §4 分工）
├── MonitorItem.cs              # 自由监控项：DataPoint 的界面包装 + 集合（增删改/落盘/解析）
├── XlsxIoTableReader.cs        # xlsx 点位表解析（零依赖 zip+XML）
├── Modeling/                   # 数据类型系统（P3-3 数据模型层）
│   ├── DataTypes.cs            # DataType / DisplayFormat 枚举 + DataTypeSpec 规格表
│   ├── ValueCodec.cs           # 原始寄存器 ↔ 类型值 编解码 + 显示格式化
│   └── DataPoint.cs            # 监控项模型 + data-points.json 持久化 + 驱动校验解析
└── Drivers/
    ├── CommDriver.cs           # 模型：ModbusArea / NumberBase / AddressArea / CommDriver（含字节顺序枚举）
    ├── DriverRegistry.cs       # 品牌地址数据表（含各品牌字节顺序约定）
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（MBAP + FC01/02/03/04 读 + FC05/06/16 写 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```

> `ModelingCheck/`（仓库根，**不属于本工程**）是 P3-3 的独立验证工程：用真机实测数据对编解码做断言，不参与主程序构建。

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
| `设置` | — | **占位项，无 `x:Name`** → 点击会隐藏全部面板，接页面前需先处理 |

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
| `IoItemTemplate` | IO 点位单元格（灯 10×10、注释 MinWidth 144；`Comment` 为 INPC） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `IoPagerButton` | 分页箭头（26×26，首末页置灰） |
| `LogClearButton` | 日志卡「清空」按钮 |
| `DeviceLogItemTemplate` | 日志行（时间戳灰 + 消息按级别着色） |
| `DeviceLogListStyle` | 日志列表（透明底/无边框/无选中底色） |
| `ScrollBar` | 细滚动条（10px） |

### 3.4 页面细节

- **总览**：顶部仅「总览」标题（原状态卡已移除，连接状态统一在全局底栏）。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**，另两页照滚。
- **状态信息**：见 §5 IO 监控 / §5.1 自由监控。
- **设备配置**：连接方式 / 协议 / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮；**设备通讯区域**另有左「字节顺序」3 个下拉框（32 位整数 / 32 位浮点 / 字符串）+ 竖分隔线 + 右「地址表元件」（见 §8.1）。连接中锁定表单（`SetInputsEnabled`）。串口模式仅隐藏以太网面板，**功能未实现**。

### 3.5 全局底栏（连接状态）

30px 高，**三个页面都可见**（在面板之外，不随导航切换显隐）。

| 位置 | 控件 | 内容 |
|---|---|---|
| 左 | `StatusDot` + `StatusText` | 状态点 + 状态文字：`未连接` / `已连接 ip:端口` / `正在重连 ip:端口 … Ns（第 M 次）` |
| 右 | `StatusBarDetail` | 连接参数：`驱动：连接方式  协议  IP:端口` |

- **两个控件名沿用自旧设备配置表单**，故 `SetStatus()` 逻辑无需改动 —— 它写的就是这两个名字
- `SetStatus(text, colorHex)` 同时设 `StatusText.Text` / `.Foreground` / `StatusDot.Fill`，并顺带调 `UpdateOverviewStatus()`
- `UpdateOverviewStatus()` 只写 `StatusBarDetail`（表单参数变化时刷新）
- 栏内边距 16，**不跟随日志卡片的 36 缩进**

**布局要点（改这块务必先读）**：

- 底栏在主体 Grid 内用 `VerticalAlignment="Bottom"` **叠放**，内容区靠 `Margin="112,0,0,30"` 让位；**没有新增行定义**
- `Margin="112,0,0,0"` 的左缩进 112 = 导航列的视觉右边界（导航是 `Margin="0,0,112,0"`，**右边距** 112）
- ⚠️ **不要把底栏改成窗口级第 3 行或给它加列定义** —— 前者会切走导航列的高度、后者会在底栏里切一刀导致左下角空缺（两种错法都试过）

### 3.6 最大化溢出补偿

`WindowChrome` 自绘标题栏时，系统把最大化窗口四边**各外扩一个边框宽度**（150% DPI 实测 11px），底边被推到任务栏之下，而 `WindowChrome` 下客户区覆盖整个窗口 → 底栏最下一条被遮住。

- `UpdateMaximizedInset()`：按「窗口矩形 − 所在显示器工作区」算外扩量，给 `RootGrid` 四边各内缩该值；非最大化时清零
- 由 `Window_StateChanged` 调两次（切换瞬间 + `DispatcherPriority.Loaded` 后），因为状态刚变时窗口矩形可能还没落定
- 用**物理像素**测量再按 `VisualTreeHelper.GetDpi` 换算，不依赖 WPF 布局时序；多显示器用 `MonitorFromWindow`
- ⚠️ **拦 `WM_GETMINMAXINFO` 改 `ptMaxSize` 的方案无效**（已试过）：钩子确实被调用，但系统对带边框的最大化窗口仍按自己的规则外扩，写回值被忽略。**不要再走这条路**

## 4. 代码分工（MainWindow.xaml.cs）

> ⚠️ 单文件已 **2200+ 行**，采集编排 / 连接生命周期 / 心跳 / 导航 / 导入 / 分页 / 自由监控 / 日志混放（见 `Design.md` §8-1）。

| 区块 | 关键成员 |
|---|---|
| 连接状态机 | `ConnectButton_Click` / `Disconnect()` / `Disconnect(status,color)` / `TryConnectAsync` / `RunConnectionTasksAsync` / `IsCurrentConnection` |
| 自动重连 | `StartReconnect` / `ReconnectLoopAsync` / `CancelReconnect` / `ReconnectTarget` / `ReconnectDelaysSeconds` |
| 轮询 | `PollLoopAsync` / `PollOnceAsync`（按地址区分块 + 2000 点切窗）/ `PollFreeMonitorAsync`（自由监控，按区选 FC01/03/04） |
| 心跳编排 | `HeartbeatLoopAsync` / `NotifyConnectionLost` |
| IO 点位集 | `RebuildIoPoints` / `TryCreateIoPoint` / `NormalizePointName` |
| 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager` |
| 导入 | `ImportIoTableButton_Click` |
| **自由监控** | `AddMonitorItemButton_Click` / `RemoveMonitorItem_Click` / `MonitorName_TextChanged` / `MonitorName_LostFocus` / `SyncMonitorTypeWithElement` / `ApplyMonitorRowState` / `RefreshMonitorView` / `FocusMonitorRowName` / `RowOf` / `MonitorValue_GotFocus` / `MonitorValue_LostFocus` / `MonitorValue_KeyDown` / `CommitMonitorValueAsync` / `WriteToDeviceAsync` / `ClassifyWriteError`（见 §5.1） |
| 持久化 | `LoadComments` / `SaveComments` |
| 网卡 | `ScanNetworkAdapters`（`NicInfo`） |
| 日志 | `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `ClassifyDisconnectReason` |
| 底栏 | `SetStatus` / `UpdateOverviewStatus` / `UpdateMaximizedInset` |
| 关键字段 | `_modbus` / `_isConnected` / `_pollCts` / `_connectCts` / `_windowClosed` / `_pollIntervalMs` / `_currentDriver` / `_xPoints` / `_yPoints` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_comments` / `_monitorSet` / `_deviceLog` / `_reconnectTarget` / `_reconnectCts` / `_reconnectAttempt`（**哪些是 `volatile` 以上表 §3.1 为准**：`_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` 共 7 个） |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控

- **点位集完全由导入的 IO 表驱动**（不再固定 64 点）；未导入则空列表 + `IoEmptyHint`「尚未导入 IO 表」
- 持久化唯一来源 = `io-comments.json`（`AppContext.BaseDirectory`，**键=点位名、值=注释**）；导入即整体替换并写盘
- 导入校验：**仅接受 X / Y 元件**，地址须落在当前驱动范围内；结果**一律用 `MessageBox`** 汇报（0 项有效则警告框且拒绝替换）
- **分页**：每页 4 列 × `IoRowsPerColumn`(16) = 64 点，X/Y 各自独立页号；单页时整体隐藏。列宽 `*` 等分；行高 22、行距 2、列距 4
- **采集跟随可见性**：只读**当前显示分段 + 当前页**（`_visiblePage` 快照，按所属地址区分块 FC01，同区跨度超 2000 点切窗，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**；断线不清灯色（`IsOn` 保持上次值）
- **三个分段统一「跟随可见性」**（输入 X / 输出 Y / 自由监控）：只有当前显示的才读，离开者**保留上次值、不清空、不断线**。外层另有页面级闸门 `_ioPageActive`（离开状态信息页则全停）。详见 §5.1 与 `Design.md` §5.6

**灯色（`IoPoint.StateBrush`）**：灭灯 `#C4C4C4`；点亮时**输入 X 绿 `#0B7A0B`、输出 Y 橙 `#FFA500`**。

- 由 `IoPoint.IsOutput`（`init` 属性）区分，创建时在 `TryCreateIoPoint` 按点位名首字母定死：`IsOutput = normalized[0] == 'Y'`
- 三个画刷是 `static readonly` + `Freeze()`，**不要在 `StateBrush` 里 new**（原实现每次取属性都新建对象）
- XAML 侧不用改：`IoItemTemplate` 的灯仍绑 `Background="{Binding StateBrush}"`

### 5.1 自由监控（P4-2，第三分段）

状态信息页第三个分段（`FreeViewSegment`），列表式监控项：**元件名称 / 数据类型 / 显示格式 / 当前值 / 删除**。

- **界面采用 Brief「方案 2」**：元件符号与编号**合并为一个输入框**（`NameBox`，如 `HD100`），不再是「符号下拉 + 编号框」
  - 取舍见 `Design.md` §7.1；存储仍是 `DataPoint.Element` + `Index` 两个字段（旧 `data-points.json` 无损）
  - 合并输入 → 拆分的**唯一实现**是 `CommDriver.TrySplitName`（`TryResolve` 与它共用同一份规则）
- **数据类型由元件反推**（方案 1 是反过来的）：`SyncMonitorTypeWithElement` 按 `CommDriver.AreasOfElement` 判断该符号属于线圈还是寄存器 → 线圈只给 `BOOL`，寄存器给 6 种；改元件导致类型不符时自动纠正
  - ⚠️ `AreasOfElement` **只看符号、不看编号**，故编号越界时仍能给出正确的类型候选与错误文案
- **输入校验**：`TrySplitName` 从严（编号必须 ASCII 数字，拒 `"D 100"` / `"D-1"` / 全角数字）；拆分失败**只标红、不写模型**（打字途中的 `"HD"` 不能落盘），失焦时回退成上次有效名称
- **错误提示**：行 `ToolTip` 与 `NameBox` 的 `NameToolTip` 都显示原因 —— 后者**出错时优先显示错误原因**（否则静态格式提示会遮住真原因）
- 持久化 `data-points.json`（`DataPointStore`），增删改即时落盘；`_monitorSet`（`MonitorItemSet`）负责增删改与 `AfterChange` 重解析
- **读取已实现**（跟随可见性，见 §5 与 `Design.md` §5.6）：仅在**本分段可见**时读，`BuildReadPlan` 按 `ModbusArea` 分组 + 地址连续性分块（单块 ≤2000），按区选 FC01/FC03/FC04，经 `ValueCodec.FormatRegisters` 按各行类型/格式/字节顺序格式化后写入 `CurrentValue`
  - ⚠️ **必须按区分组**：信捷 `D0`（保持寄存器）与 `M0`（线圈）**绝对地址都是 0**，只按地址归并会把两者塞进同一次请求 → 从站回异常码
  - **单块读失败不触发断线**，只把原因写到该块的各项上（与 IO 轮询的断线策略**刻意不同**）；一个配错的地址不该拖垮整条连接
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
- 合法异常响应抛 `ModbusResponseException` → **保持连接**并告警（码 1/2 提示配置无效，其余提示响应异常），后续心跳成功即清除。**IO 轮询异常仍沿用断开策略**
- 读取超时 2s；空闲失联通常**约 3 秒**检测到（1s 空闲 + 2s 超时 + 调度延迟），不是严格 3s 上限
- `RunConnectionTasksAsync` 启动时把**连接与驱动快照**传给两个任务，所有回 UI 回调先过 `IsCurrentConnection`（未关窗 && token 未取消 && 仍连着 && `_modbus` 是同一实例）→ 旧连接的后台回调无法误操作新连接
- `StopPolling` 只取消并置 null，**释放交给任务自身 `finally`**（确认自己仍是当前连接后）；`OnClosed` 统一取消连接尝试、停任务并释放

### 6.1 自动重连（P1-1）

| 项 | 行为 |
|---|---|
| 触发 | **仅「异常断开」**（读超时 / 对端关闭 / 帧校验失败等）。**用户主动断开、首次连接失败均不触发** |
| 退避 | `ReconnectDelaysSeconds = { 1, 2, 4, 8, 10 }`，封顶 10s；连接成功清零 `_reconnectAttempt` |
| UI | 重连期间**表单保持锁定**（参数已快照，改了不生效，避免误导）；按钮变「取消重连」；底栏逐秒显示 `正在重连 ip:端口 … Ns（第 M 次）` |
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
- 颜色：主色 `#0067C0`、背景 `#F3F3F3`、顶栏 `#E7E7E7`、卡片白底 + `#E8E8E8` 边框圆角 8
- 字体：`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`；标题用 `Segoe UI Variable Display`；图标 `Segoe Fluent Icons, Segoe MDL2 Assets`（资源键 `IconFont`）
- 表单：标签 `Width="80"` + 控件 `Width="200"`、行距 16；`SyncInputHeights` 在 Loaded 与面板显示时同步控件高
- `IO模板.xlsx` 仅**导入功能测试用**，其注释列与 `io-comments.json` 都是占位文本（`CommentX0`/`Comment0`），**无业务含义** —— 不必维护、不必恢复，也不是待处理问题

## 10. 任务速查

| 需求 | 位置 |
|------|------|
| 改导航项/页面显隐 | `MainWindow.xaml` 导航区 + `Nav_Checked` |
| 加通讯品牌 | `Drivers/DriverRegistry.cs`（§8） |
| 加设备配置字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | `UpdateOverviewStatus()`（写底栏 `StatusBarDetail`） |
| 底栏状态文字/颜色 | `SetStatus(text, colorHex)` → `StatusText` + `StatusDot` |
| 底栏布局/对齐 | `MainWindow.xaml` 主体 Grid 末尾的底栏 `Border`（见 §3.5，**改前必读**） |
| 最大化底栏被任务栏遮住 | `UpdateMaximizedInset()` + `Window_StateChanged`（见 §3.6） |
| 自动重连退避/触发/取消 | `ReconnectDelaysSeconds` / `StartReconnect` / `CancelReconnect`（见 §6.1） |
| IO 灯色（输入绿/输出橙） | `IoPoint.StateBrush` + `InputOnBrush` / `OutputOnBrush` / `OffBrush`；`IsOutput` 在 `TryCreateIoPoint` 定 |
| IO 点位集构建/排序 | `RebuildIoPoints` → `TryCreateIoPoint` / `NormalizePointName`（按地址升序） |
| 导入 IO 表 / 解析规则 | `ImportIoTableButton_Click` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格） |
| 注释与点位集持久化 | `LoadComments` / `SaveComments` → `io-comments.json` |
| IO 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（`_xPageIndex`/`_yPageIndex` 独立） |
| 改每页点位数 | `IoRowsPerColumn`（列数固定 4） |
| IO 采集范围 | `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| **自由监控读取（分块规划）** | `MonitorItemSet.BuildReadPlan`（按 `ModbusArea` 分组 + 地址连续性分块）+ `MonitorItemSet.ApplyBlock`（偏移切片 + 格式化回填） |
| **自由监控轮询入口** | `PollOnceAsync` 开头按 `_freeViewActive` 分流 → `PollFreeMonitorAsync`（按区选 FC01/FC03/FC04，单块失败不断线） |
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
| 轮询与断线逻辑 | `PollLoopAsync` / `PollOnceAsync` / `Disconnect(status,color)` |
| 网卡扫描/绑定 | `ScanNetworkAdapters` + `ConnectButton_Click`（`NicInfo`） |
| 采集周期档位 | `PollIntervalComboBox` + `_pollIntervalMs` |
| 日志追加/清空/上限 | `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `MaxDeviceLogEntries` |
| 日志颜色与格式 | `LogEntry`（`DeviceLogLevel` → `LevelBrush`）+ `DeviceLogItemTemplate` |
| 断开原因归类 | `ClassifyDisconnectReason` |
| 连接任务隔离/关闭清理 | `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed` |

## 11. 版本控制

- git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`
- 身份为仓库级配置（`G.Sheng`）
- **提交信息用中文**；提交前先 `git diff` 确认范围，**不要整文件盲加**（会把别人的 WIP 一起带走）
