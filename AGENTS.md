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
├── MainWindow.xaml.cs              # 交互逻辑（约 470 行）
└── Drivers/                        # 通讯驱动（标准化地址表）
    ├── CommDriver.cs               # 模型：ModbusArea / NumberBase / AddressArea / CommDriver
    └── DriverRegistry.cs           # 驱动注册表（品牌地址数据）
```

## UI 结构（MainWindow.xaml）

单窗口两行布局：48px 自定义标题栏 + 主体（导航 224 列 + 内容）。

- **标题栏**：`WindowChrome`（`CaptionHeight=48`），Logo、窗口按钮；可交互元素需加 `WindowChrome.IsHitTestVisibleInChrome="True"`
- **导航**（`NavRadioButton` 样式，GroupName="Nav"）：
  - `OverviewNav` 总览 → `OverviewPanel`
  - `StatusInfoNav` 状态信息 → `StatusInfoPanel`（IO 监控）
  - `DeviceConfigNav` 设备配置 → `DeviceConfigPanel`
  - 切换逻辑：样式 `EventSetter Checked=Nav_Checked`，按 sender 显隐对应面板
- **设计器显示**：面板运行时 `Visibility="Collapsed"`，用 `d:Visibility="Visible"` 让 VS 设计器预览（总览/设备配置在设计器中 `d:Collapsed` 避免重叠）

### 关键样式/模板（Window.Resources）

| Key | 用途 |
|-----|------|
| `CaptionButton` / `CloseCaptionButton` | 标题栏按钮（关闭悬停红底） |
| `NavRadioButton` | 导航项（灰底背板，选中加粗） |
| `IoItemTemplate` | IO 点位单元格（灯 10×10、灯与名间距 4、注释 MinWidth 144） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `ScrollBar` | 细滚动条（10px） |

### 页面内容

1. **总览**：连接状态条 `OverviewStatusPrefix`（黑字）+ `OverviewStatusState`（右对齐，未连接黄 #C08A00 / 已连接绿 #0B7A0B）。格式：`驱动：连接方式  协议  IP:端口` + 状态。由 `UpdateOverviewStatus()` 刷新（选择/输入/连接状态变化时调用）。
2. **状态信息**：IO 监控 4 列 × 16 = 64 点（X0–X77 八进制）。列宽 `*` 等分自适应窗口；行高 22、行距 2、列距 4（Grid 列 margin）。注释来自 `MainWindow.xaml.cs` 的 `XComments` 字典（X0–X37 有注释，X40+ 留空）。无模拟定时器，状态静态（`IsOn` 默认 false），待接入真实采集。
3. **设备配置**：连接方式（以太网/串口）、协议类型（ModbusTCP）、设备类型（信捷XD/XL系列 / 汇川Easy系列）；以太网时显示目标网卡（扫描本机网卡 `网卡名(ip)` + 刷新按钮）、IP、端口号、连接按钮 + 状态灯。连接中/已连接时 `SetInputsEnabled(false)` 全部置灰，按钮变「断开」。连接为 **3 秒超时的 TcpClient ConnectAsync**（尚未实现 Modbus 协议报文）。

## 通讯驱动标准（Drivers/）

新增品牌 = 在 `DriverRegistry.Drivers` 中追加一个 `CommDriver`：

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

解析用法：

```csharp
var d = DriverRegistry.GetByKey("xinje_xd")!;
d.TryResolve("X17", ModbusArea.Coil, out int addr, out string err); // 八进制 X17 → 20495
```

已注册：`xinje_xd`（X/Y/M/S/SM/HM + D/SD/HD/HSD/SFD）、`inovance_easy`（M/B/S/X/Y + D/R）。

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
- 表单布局：标签 `Width="80"` + 控件 `Width="220"`、行距 16、控件高与下拉框一致（`SyncInputHeights` 在 Loaded 与面板显示时同步）
- 修改 UI 后构建验证；用户习惯「构建成功后自动启动应用」

## 常见任务速查

| 需求 | 位置 |
|------|------|
| 改导航项/页面显隐 | XAML 导航区 + `Nav_Checked` |
| 加/改 IO 点位注释 | `MainWindow.xaml.cs` → `XComments` |
| 改 IO 行高/行距 | `IoItemTemplate` → `Height` / `Margin` 第 4 值 |
| 改 IO 列宽策略 | `IoColumnsGrid`（4×`*`） |
| 加通讯品牌 | `Drivers/DriverRegistry.cs` |
| 加设备配置字段 | `DeviceConfigPanel` + `SetInputsEnabled` |
| 总览状态格式 | `UpdateOverviewStatus()` |
