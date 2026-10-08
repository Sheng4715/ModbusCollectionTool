# AGENTS.md — DSH_Modbus采集工具

## 项目概述

基于 **WPF / .NET 8** 的 Modbus 数据采集上位机 Demo，界面参考 Windows 11 Fluent / 微软商店风格（自定义 WindowChrome 标题栏 + 左侧导航 + 卡片式内容）。

- 解决方案：`DSH_Modbus采集工具.sln`
- 项目：`DSH_Modbus采集工具/DSH_Modbus采集工具.csproj`（`net8.0-windows`，`UseWPF=true`）
- 无第三方 NuGet 依赖

## 构建与运行

```powershell
dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

- 构建前若应用正在运行，需先结束进程，否则 exe 被锁定会报 MSB3027：
  `Get-Process -Name "DSH_Modbus采集工具" | Stop-Process -Force`
- 窗口最小尺寸 1024×640（`MainWindow.xaml` 的 `MinWidth/MinHeight`），默认尺寸与最小一致

## 目录结构

```
DSH_Modbus采集工具/
├── App.xaml / App.xaml.cs          # 应用入口
├── MainWindow.xaml                 # 全部 UI（单窗口，无 UserControl 拆分）
├── MainWindow.xaml.cs              # 交互逻辑（导航/连接生命周期/轮询与心跳编排/网卡绑定/动态IO点位集与分页/xlsx导入）
├── XlsxIoTableReader.cs            # xlsx 点位表解析（零依赖 zip+XML，地址→右邻配对）
└── Drivers/                        # 通讯驱动
    ├── CommDriver.cs               # 模型：ModbusArea / NumberBase / AddressArea / CommDriver
    ├── DriverRegistry.cs           # 驱动注册表（品牌地址数据）
    ├── ModbusTcpClient.cs          # Modbus TCP 主站（MBAP + FC01/02/03/04 + 本地端点绑定 + 空闲响应检查）
    └── ModbusHeartbeat.cs          # 独立心跳调度、心跳异常响应分类与恢复提示
```

## UI 结构（MainWindow.xaml）

单窗口两行布局：48px 自定义标题栏 + 主体（导航 224 列 + 内容）。

- **标题栏**：`WindowChrome`（`CaptionHeight=48`），Logo、窗口按钮；可交互元素需加 `WindowChrome.IsHitTestVisibleInChrome="True"`
- **导航**（`NavRadioButton` 样式，GroupName="Nav"）：
  - `OverviewNav` 总览 → `OverviewPanel`
  - `StatusInfoNav` 状态信息 → `StatusInfoPanel`（IO 监控）
  - `DeviceConfigNav` 设备配置 → `DeviceConfigPanel`
  - 占位项：`采集配置`（本地 `Checked=RadioButton_Checked` 空实现，不切面板）、`设置`（底部，无 x:Name → 走 `Nav_Checked` 会**隐藏全部面板**，接页面前需先处理）
  - 切换逻辑：样式 `EventSetter Checked=Nav_Checked`，按 sender 显隐对应面板
- **设计器显示**：面板运行时 `Visibility="Collapsed"`，用 `d:Visibility="Visible"` 让 VS 设计器预览（总览/设备配置在设计器中 `d:Collapsed` 避免重叠）

### 关键样式/模板（Window.Resources）

| Key | 用途 |
|-----|------|
| `CaptionButton` / `CloseCaptionButton` | 标题栏按钮（关闭悬停红底） |
| `NavRadioButton` | 导航项（灰底背板，选中加粗） |
| `IoSegmentRadioButton` | IO 页 X/Y 分段切换项（灰底背板、悬停加深、选中白底加粗） |
| `IoItemTemplate` | IO 点位单元格（灯 10×10、灯与名间距 4、注释 MinWidth 144；Comment 为 INPC，导入后自动刷新） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `IoPagerButton` | IO 分页箭头按钮（卡片右上角 `<` `>`，26×26，首末页置灰） |
| `ScrollBar` | 细滚动条（10px） |

### 页面内容

1. **总览**：连接状态条 `OverviewStatusPrefix`（黑字）+ `OverviewStatusState`（右对齐，未连接黄 #C08A00 / 已连接绿 #0B7A0B）。格式：`驱动：连接方式  协议  IP:端口` + 状态。由 `UpdateOverviewStatus()` 刷新（选择/输入/连接状态变化时调用）。
2. **状态信息**（IO 监控）：**点位集完全由导入的 IO 表驱动，不再固定 64 点**。卡片左上 = **X/Y 分段切换**（`XViewSegment` 输入监控 / `YViewSegment` 输出监控，GroupName="IoView"，`IoView_Checked` 切网格显隐并重新分页）+ **「导入 IO 表」按钮**（`ImportIoTableButton_Click`：逐行按当前通讯驱动 `_currentDriver` 校验——**仅接受 X / Y 元件**且地址须落在驱动定义范围内；通过者整体替换点位集，结果**一律用 `MessageBox` 对话框**汇报：解析失败→红错误框 / 0 项有效→警告框且不替换 / 否则「成功导入 N 项，失败 M 项」+ 失败明细；**已无内联提示文字**）。**分页**：每页固定 4 列 × `IoRowsPerColumn`（默认 16）= **64 点**，超出即分页；X/Y **各自独立记页**（`_xPageIndex` / `_yPageIndex`），分页控件在卡片右上角（`IoPagerPanel` = `IoPrevPageButton` + `IoPageText`「当前/总数」+ `IoNextPageButton`），**只有一页时整体隐藏**，首/末页对应箭头置灰。**未导入时列表为空**并显示 `IoEmptyHint`「尚未导入 IO 表」。点位按**地址升序**排序（`RebuildIoPoints`）。列宽 `*` 等分；行高 22、行距 2、列距 4。点位集/注释唯一来源 = `io-comments.json`（AppContext.BaseDirectory，导入后写入；**键=点位名，值=注释**）—— 内置 `XComments` 默认表**已移除**；`IoPoint.Comment` 是 INPC 属性，导入即刷 UI。**采集跟随可见性**：连接后 `PollLoopAsync` 每周期只读**当前显示分段 + 当前页**的点位（`_visiblePage` 快照，按所属地址区分块、每块读本区最小..最大地址（FC01，跨区空隙从站不接受）再按偏移回填，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**（`_ioPageActive` 仅控制 IO 轮询），未显示页/区与离页期间灯保留上次数据，回页/切页后下一周期恢复采集；断线不清灯色（`IsOn` 保持上次值）。
3. **设备配置**：连接方式（以太网/串口）、协议类型（ModbusTCP）、设备类型（**来自 `DriverRegistry.All`**，选中即缓存 `_currentDriver`）、采集周期（100ms–1s 四档，默认 100ms）；以太网时显示目标网卡（`NicInfo`：扫描本机网卡 `网卡名(ip)` + 刷新按钮）、IP、端口号、连接按钮 + 状态灯。连接中/已连接时 `SetInputsEnabled(false)` 全部置灰，按钮变「断开」。连接 = **3 秒超时 ConnectAsync + 绑定所选网卡本地端点**，成功后并行启动 IO 轮询与独立心跳，两者共用串行请求锁；读超时/对端关闭 → 自动断开红字提示。心跳详情见下节。串口模式目前仅隐藏以太网面板，功能未实现（P1）。

### 独立心跳与连接生命周期

- `ModbusHeartbeat.RunAsync` 每 100ms 检查一次；距上次有效响应满 1 秒时，以 FC01 读取驱动配置的 `HeartbeatCoil`，数量为 1。信捷、汇川均配置 `M0`，只读不写。
- `HeartbeatIfIdleAsync` 在请求锁内复查空闲时间；正常采集响应可省略额外心跳。总览、设备配置及空点位页均继续检测，心跳不更新 IO 灯。
- 心跳合法异常响应抛 `ModbusResponseException`，说明对端仍在回应：码 1/2 提示配置无效，其余码提示响应异常；保持连接并继续按空闲间隔尝试，后续心跳成功清除警告。**IO 轮询异常仍沿用断开策略**。
- 未配置或无法解析心跳地址时，明确提示「未启用心跳」，不猜测地址；此类新驱动在离页/空列表时无法依靠心跳检测断线。
- 读取超时为 2 秒；空闲连接失去响应后通常约 3 秒检测到，另含检查/线程调度延迟；本地模拟实测 3095ms，不是严格 3 秒上限。
- `RunConnectionTasksAsync` 捕获连接/驱动快照；回调通过 `IsCurrentConnection` 校验实例、取消令牌和窗口关闭标志，避免旧任务误操作新连接。`StopPolling` 取消两个任务，任务结束后释放取消源；`OnClosed` 统一取消连接尝试、停任务并释放连接。

## 通讯驱动标准（Drivers/）

新增品牌 = 在 `DriverRegistry.Drivers` 中追加一个 `CommDriver`：

```csharp
new CommDriver
{
    Key = "brand_key",
    DisplayName = "显示名",          // 与 UI「设备类型」下拉项对应
    HeartbeatCoil = "M0",            // 须由该品牌手册确认可用；FC01 单点只读，缺省则提示未启用心跳
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

解析用法：

```csharp
var d = DriverRegistry.GetByKey("xinje_xd")!;
d.TryResolve("X17", ModbusArea.Coil, out int addr, out string err); // 八进制 X17 → 20495
```

已注册：`xinje_xd`（X/Y/M/S/SM/HM + D/SD/HD/HSD/SFD；其中 **X/Y 各 4 段**：本体 / 扩展模块 #1–#16 / BD / ED）、`inovance_easy`（M/B/S/X/Y + D/R）。

> **同一元件符号可有多段地址区**（信捷 X/Y 即如此）。`CommDriver.TryResolve(string,…)` 会遍历**全部**匹配 `Element+Area` 的区：任一段命中即成功，全部越界才报「超出驱动定义范围」。
> `AddressArea.StartIndex` 用编号的**十进制**值表达八进制编号（如 X10000₈ → `4096`），`Count` 为段内点数，`StartAddress` 为该段起始 Modbus 十进制地址。

## 版本控制

- 已初始化 git 仓库（分支 `main`），`.gitignore` 排除 `bin/ obj/ .vs/ *.user`
- git 已通过 winget 安装（`C:\Program Files\Git`，2.55+，在 PATH 上；旧会话可能需刷新 PATH）
- 身份为仓库级配置（`G.Sheng`），修改：`git config user.name / user.email`
- 提交信息用中文，改动完成后及时提交

## 代码约定

- 命名空间：`DSH_Modbus采集工具`（含中文，文件路径亦含中文，命令行注意编码/引号）
- UI 事件处理方法：`Xxx_Click` / `Nav_Checked` / `Xxx_SelectionChanged`；构造函数中 `InitializeComponent()` 后初始化数据
- 颜色常量：主色 `#0067C0`，背景 `#F3F3F3`，顶栏 `#E7E7E7`，卡片白底 + `#E8E8E8` 边框、圆角 8
- 字体：全局 `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`；标题用 `Segoe UI Variable Display`；图标 `Segoe Fluent Icons, Segoe MDL2 Assets`（资源键 `IconFont`）
- 表单布局：标签 `Width="80"` + 控件 `Width="200"`、行距 16、控件高与下拉框一致（`SyncInputHeights` 在 Loaded 与面板显示时同步）
- 修改 UI 后构建验证；用户习惯「构建成功后自动启动应用」
- `IO模板.xlsx` 仅为**导入功能测试用**模板：其注释列与 `io-comments.json` 的内容都是占位文本（`CommentX0`/`Comment0`），**无业务含义** —— 不必维护、不必恢复，也不要把它当成待处理问题

## 常见任务速查

| 需求 | 位置 |
|------|------|
| 改导航项/页面显隐 | XAML 导航区 + `Nav_Checked` |
| 加/改 IO 点位与注释 | 导入 xlsx（**唯一入口**）；点位集 + 注释持久化在 `io-comments.json`（`LoadComments` / `SaveComments`） |
| IO 点位集构建/排序 | `RebuildIoPoints` → `TryCreateIoPoint`（仅 X/Y + 驱动范围校验）/ `NormalizePointName`（元件转大写）；按地址升序 |
| IO 分页 | `ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`；`_xPageIndex` / `_yPageIndex` 各自独立，每页 4×`IoRowsPerColumn` |
| 改每页点位数 | `MainWindow.xaml.cs` → `IoRowsPerColumn`（列数固定 4，每页 = 4 × 该值） |
| IO 采集范围 | `PollOnceAsync` 读 `_visiblePage` 快照 → 按地址区分块 FC01（`CommDriver.FindArea`，单块 ≤2000 点） |
| 改 IO 行高/行距 | `IoItemTemplate` → `Height` / `Margin` 第 4 值 |
| 改 IO 列宽策略 | `IoColumnsGrid` / `YIoColumnsGrid`（均 4×`*`） |
| 加/改 X-Y 分段切换 | `IoSegmentRadioButton` 样式 + 卡片左上 `XViewSegment`/`YViewSegment` + `IoView_Checked` |
| 导入 IO 表 / 解析规则 | `ImportIoTableButton_Click` + `XlsxIoTableReader.cs`（地址→右邻配对，仅首表，不支持合并单元格）；导入期按 `_currentDriver` 校验地址范围与监控点位，结果用 MessageBox 汇报成功/失败项数 |
| 注释持久化 | `LoadComments` / `SaveComments` → `io-comments.json`（**同时是点位集来源**；键=点位名） |
| 加通讯品牌 | `Drivers/DriverRegistry.cs` |
| 加设备配置字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | `UpdateOverviewStatus()` |
| Modbus 报文/超时 | `Drivers/ModbusTcpClient.cs` |
| 心跳周期/响应空闲判断 | `Drivers/ModbusHeartbeat.cs` → `RunAsync`（100ms 检查、1s 空闲）；`ModbusTcpClient.HeartbeatIfIdleAsync`（锁内复查） |
| 品牌心跳地址 | `CommDriver.HeartbeatCoil` + `DriverRegistry`（现有两驱动均为 M0） |
| 连接任务隔离/关闭清理 | `RunConnectionTasksAsync` / `IsCurrentConnection` / `OnClosed`；CTS 在任务结束后释放 |
| 轮询与断线逻辑 | `MainWindow.xaml.cs` → `PollLoopAsync` / `PollOnceAsync`（按所属地址区分块读取，同区超 2000 点切窗；离页停采）/ `Disconnect(status,color)` |
| 采集跟随页面/分段/页 | `_ioPageActive`（`Nav_Checked`）+ `_yViewActive`（`IoView_Checked`）+ `_visiblePage`（`ApplyIoPage`）→ `PollOnceAsync` 开头判断 |
| 网卡扫描/绑定 | `ScanNetworkAdapters` + `ConnectButton_Click`（`NicInfo`） |
| 采集周期档位 | `PollIntervalComboBox`（XAML）+ `_pollIntervalMs` |
