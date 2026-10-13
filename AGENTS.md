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
├── XlsxIoTableReader.cs        # xlsx 点位表解析（零依赖 zip+XML）
└── Drivers/
    ├── CommDriver.cs           # 模型：ModbusArea / NumberBase / AddressArea / CommDriver
    ├── DriverRegistry.cs       # 品牌地址数据表
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（MBAP + FC01/02/03/04 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```

## 3. 页面与 UI

单窗口两行布局：48px 标题栏 + 主体（导航 224 列 + 内容）。

### 3.1 导航

| 导航项 | 面板 | 说明 |
|---|---|---|
| `OverviewNav` 总览 | `OverviewPanel` | 状态条 + 底部运行日志卡 |
| `StatusInfoNav` 状态信息 | `StatusInfoPanel` | IO 监控（X/Y 分段 + 分页） |
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

- **总览**：状态条 `OverviewStatusPrefix` + `OverviewStatusState`（未连接黄 `#C08A00` / 已连接绿 `#0B7A0B`），由 `UpdateOverviewStatus()` 刷新。下方固定**运行日志卡**占本页 1/4（`3*`/`1*` 两行），故**本页自身不滚动**，另两页照滚。
- **状态信息**：见 §5 IO 监控。
- **设备配置**：连接方式 / 协议 / 设备类型 / 采集周期 + 网卡 / IP / 端口 / 连接按钮。连接中锁定表单（`SetInputsEnabled`）。串口模式仅隐藏以太网面板，**功能未实现**。

## 4. 代码分工（MainWindow.xaml.cs）

> ⚠️ 单文件已 1380+ 行，采集编排 / 连接生命周期 / 心跳 / 导航 / 导入 / 分页 / 日志混放（见 `Design.md` §9-1）。

| 区块 | 关键成员 |
|---|---|
| 连接状态机 | `ConnectButton_Click` / `Disconnect` / `RunConnectionTasksAsync` / `IsCurrentConnection` |
| 轮询 | `PollLoopAsync` / `PollOnceAsync`（按地址区分块 + 2000 点切窗） |
| 心跳编排 | `HeartbeatLoopAsync` / `NotifyConnectionLost` |
| IO 点位集 | `RebuildIoPoints` / `TryCreateIoPoint` / `NormalizePointName` |
| 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager` |
| 导入 | `ImportIoTableButton_Click` |
| 持久化 | `LoadComments` / `SaveComments` |
| 网卡 | `ScanNetworkAdapters`（`NicInfo`） |
| 日志 | `AppendDeviceLog` / `ClearDeviceLogButton_Click` / `ClassifyDisconnectReason` |
| 关键字段 | `_modbus` / `_isConnected` / `_pollCts` / `_connectCts` / `_windowClosed` / `_pollIntervalMs`(volatile) / `_currentDriver` / `_xPoints` / `_yPoints` / `_ioPageActive` / `_yViewActive` / `_xPageIndex` / `_yPageIndex` / `_visiblePage` / `_comments` / `_deviceLog` |

**线程规则**：WPF 控件不可跨线程访问 → 后台只读 `volatile` 字段或启动时缓存的对象（如 `_currentDriver`）；回 UI 一律 `Dispatcher.InvokeAsync`，且先过 `IsCurrentConnection` 校验。

## 5. IO 监控

- **点位集完全由导入的 IO 表驱动**（不再固定 64 点）；未导入则空列表 + `IoEmptyHint`「尚未导入 IO 表」
- 持久化唯一来源 = `io-comments.json`（`AppContext.BaseDirectory`，**键=点位名、值=注释**）；导入即整体替换并写盘
- 导入校验：**仅接受 X / Y 元件**，地址须落在当前驱动范围内；结果**一律用 `MessageBox`** 汇报（0 项有效则警告框且拒绝替换）
- **分页**：每页 4 列 × `IoRowsPerColumn`(16) = 64 点，X/Y 各自独立页号；单页时整体隐藏。列宽 `*` 等分；行高 22、行距 2、列距 4
- **采集跟随可见性**：只读**当前显示分段 + 当前页**（`_visiblePage` 快照，按所属地址区分块 FC01，同区跨度超 2000 点切窗，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**；断线不清灯色（`IsOn` 保持上次值）

## 6. 心跳与连接生命周期

- `ModbusHeartbeat.RunAsync` 每 100ms 检查；距上次有效响应满 **1s** 时以 FC01 读**单点线圈**，只读不写
- **心跳地址 = 驱动地址表里「地址最小」的线圈区 `StartAddress`**（信捷 M 区 / 汇川 M 区均为 **0**）。按 `Area` 筛选后 `OrderBy(StartAddress)`，不依赖数组顺序；无需任何配置，与是否导入 IO 表无关
- 地址表里没有线圈区时提示「未启用心跳」并 return（用 `FirstOrDefault` 而非 `First`，否则抛出的异常会被上层当成通讯故障并断开连接）。正常驱动不会出现
- 合法异常响应抛 `ModbusResponseException` → **保持连接**并告警（码 1/2 提示配置无效，其余提示响应异常），后续心跳成功即清除。**IO 轮询异常仍沿用断开策略**
- 读取超时 2s；空闲失联通常**约 3 秒**检测到（1s 空闲 + 2s 超时 + 调度延迟），不是严格 3s 上限
- `RunConnectionTasksAsync` 启动时把**连接与驱动快照**传给两个任务，所有回 UI 回调先过 `IsCurrentConnection`（未关窗 && token 未取消 && 仍连着 && `_modbus` 是同一实例）→ 旧连接的后台回调无法误操作新连接
- `StopPolling` 只取消并置 null，**释放交给任务自身 `finally`**（确认自己仍是当前连接后）；`OnClosed` 统一取消连接尝试、停任务并释放

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
| 总览状态格式 | `UpdateOverviewStatus()` |
| IO 点位集构建/排序 | `RebuildIoPoints` → `TryCreateIoPoint` / `NormalizePointName`（按地址升序） |
| 导入 IO 表 / 解析规则 | `ImportIoTableButton_Click` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格） |
| 注释与点位集持久化 | `LoadComments` / `SaveComments` → `io-comments.json` |
| IO 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`（`_xPageIndex`/`_yPageIndex` 独立） |
| 改每页点位数 | `IoRowsPerColumn`（列数固定 4） |
| IO 采集范围 | `PollOnceAsync` → `_visiblePage` 快照 + `CommDriver.FindArea` 分块（单块 ≤2000） |
| 改 IO 行高/行距、列宽 | `IoItemTemplate` 的 `Height`/`Margin`；`IoColumnsGrid`/`YIoColumnsGrid`（4×`*`） |
| 加/改 X-Y 分段切换 | `IoSegmentRadioButton` + `XViewSegment`/`YViewSegment` + `IoView_Checked` |
| Modbus 报文/超时 | `Drivers/ModbusTcpClient.cs` |
| 心跳周期/空闲判断/地址来源 | `Drivers/ModbusHeartbeat.cs` → `RunAsync`；`ModbusTcpClient.HeartbeatIfIdleAsync` |
| 轮询与断线逻辑 | `PollLoopAsync` / `PollOnceAsync` / `Disconnect(status,color)` |
| 采集跟随页面/分段/页 | `_ioPageActive` + `_yViewActive` + `_visiblePage` → `PollOnceAsync` 开头 |
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
