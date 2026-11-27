# DSH_Modbus采集工具

面向工业自动化设备的 **Modbus 数据采集上位机**：实时监控 PLC 的 IO 状态、自由读写寄存器、
把采集结果按周期导出为表格文件，并可与扫码器、MES 系统联动。

基于 **WPF / .NET 8**，界面参考 Windows 11 Fluent（自绘标题栏 + 左侧导航 + 卡片式内容），中文界面。
**全部 NuGet 依赖只有一个**：微软官方 `System.IO.Ports`（串口 Modbus RTU 用）。

> **仓库地址**：<https://github.com/Sheng4715/ModbusCollectionTool.git>

```powershell
git clone https://github.com/Sheng4715/ModbusCollectionTool.git
```

---

## 功能概览

### 总览
- **运行日志卡**：固定占本页 1/4，只记设备状态（已连接 / 已断开 / 连接失败 / 通讯异常 / 通讯恢复），
  内存保留最近 1000 条，可一键清空
- **PLC 状态机**：9 个状态框 + 14 条转移连线 + Error / Fault 指示灯，状态由 BOOL 表达式驱动，
  双击可编辑文本 / 字体 / 颜色 / 表达式
- **数据采集视图**：以卡片形式实时展示采集任务的值，支持拖动与缩放排版

### 设备监控
- **IO 监控**：输入 X / 输出 Y 分页表格，灯色实时反映点位状态，支持从 Excel 导入 IO 表（`IO模板.xlsx`）
- **自由监控**：任意元件地址的读取与写入（FC01 / 03 / 04 / 05 / 06 / 16），支持 INT / UINT / FLOAT /
  STRING / ARRAY 等类型，行内直接编辑并回写设备
- **系统变量**：17 项内置变量（日期时间 7 项、运行时长 4 项、脉冲 4 项、`sys_True` / `sys_False`）
  只读清单，可在表达式与全局变量中引用
- **通讯监控**：抓取当前选中连接的**完整原始报文**（Modbus TCP 的 MBAP 帧 / RTU 的站号+PDU+CRC），
  HEX / TEXT 两种视图；CRC 错误、站号不符的坏帧同样保留

### 数据管理
- **数据采集**：按周期或表达式触发，读取一组变量存为 CSV，支持「执行完成输出变量」（1=正常 / 2=异常）
- **全局变量**：用户自定义变量的集中管理，改名会同步更新所有引用
- **系统变量**：只读清单

### 设备连接
- **PLC 连接**：以太网（Modbus TCP）与串口（Modbus RTU）两种方式，其余表单互斥显隐
  - 支持设备类型：**信捷 XD 系列**、**汇川 Easy 系列**（各自的元件地址表）
  - 多网卡环境可显式指定目标网卡，选择会被记住
  - 32 位整数 / 浮点 / 字符串三套字节顺序可独立配置
  - 采集周期 100 ms – 1 s，默认 100 ms
- **扫码器连接**：每台扫码器独立连接与重连，支持触发表达式与结果解析
- **MES 连接**：HTTP + POST / PUT，JSON_RAW 数据模式，服务端 URL 可配置

### 设置
应用标题 / 显示外观（浅色 / 深色 / 跟随系统）/ 开机自启动 / 日志保存 / 项目信息。

### 引擎与健壮性
- 自动重连：分级退避，重连期间锁定表单并在底栏显示倒计时
- 轮询跟随可见性：只采集当前显示的界面，其余分段不占用带宽（采集任务、扫码器触发、心跳除外）
- 合法 Modbus 异常码**不断开**：同一块连续 5 次才记一条琥珀色「通讯异常」，读通即清零
- 配置落盘全部走「临时文件 → 落盘 → 替换 + 保留 `.bak`」，读失败逐元素抢救并备份原文件
- 单实例保护：同一台机器只允许一个进程，重复启动会唤起已有窗口
- 操作权限：技术员（默认，免登录）/ 工程师（密码进入）两档。工程师专属项为**自由监控写入**
  与**应用标题编辑**，其余功能两档均可用

---

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 / 11 |
| 运行时 | .NET 8 Desktop Runtime |
| SDK（仅编译） | .NET 8 SDK |
| 目标设备 | 支持 Modbus TCP 或 Modbus RTU 的 PLC / 设备 |

## 构建与运行

```powershell
# 构建前必须先结束进程，否则 exe 被锁会报 MSB3027
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

构建产物：`DSH_Modbus采集工具\bin\Debug\net8.0-windows\DSH_Modbus采集工具.exe`

## 发布

框架依赖发布，产物约 5 个文件，目标机需预装 .NET 8 Desktop Runtime：

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish "DSH_Modbus采集工具\DSH_Modbus采集工具.csproj" `
  -c Release --self-contained false -p:DebugType=None `
  -o "Release\DSH_Modbus采集工具_1.0.2" -v q -nologo
```

覆盖更新时只替换程序本体，配置与 `Images\`、`Log\` 保留原样。详见 [`AGENTS-发布.md`](AGENTS-发布.md)。

---

## 项目结构

```
DSH_Modbus采集工具.sln
├─ DSH_Modbus采集工具/          主工程（WPF）
│  ├─ Drivers/                  Modbus 报文（TCP / RTU）、PDU、CRC16、品牌地址表
│  ├─ Expressions/              表达式引擎
│  ├─ Io/                       IO 点位集、分页、Excel 导入
│  ├─ Logging/                  运行日志落盘与断开原因归类
│  ├─ Modeling/                 数据模型与纯逻辑（变量、采集、状态机、编解码）
│  ├─ Monitoring/               通讯监控抓包缓冲
│  ├─ Net/                      网卡扫描、串口扫描
│  ├─ Polling/                  采集编排、连接生命周期、重连策略
│  ├─ Scanners/                 扫码器定义与 TCP 收帧
│  ├─ Startup/                  开机自启动、单实例保护
│  ├─ Themes/                   浅色 / 深色主题字典
│  ├─ Ui/                       呈现规则、画布、对话框
│  └─ MainWindow.*.cs           窗口主体 + 十个按页面拆分的 partial
├─ ModelingCheck/               独立断言工程：编解码（真机实测数据）
└─ LoggingCheck/                独立断言工程：日志格式 / 跨天 / 续写 / 容错
```

其中 `Drivers/` `Expressions/` `Io/` `Logging/` `Modeling/` `Monitoring/` `Net/` `Polling/` `Scanners/` `Startup/`
**十个目录不引用任何 WPF 类型**，可脱离界面单独阅读与断言。

## 文档

| 文件 | 内容 |
|---|---|
| [`Brief.md`](Brief.md) | 需求与功能范围（`P1.1` … `P6.6` 编号），验收标准 |
| [`Design.md`](Design.md) | 技术设计与决策理由 |
| [`AGENTS.md`](AGENTS.md) | 代码在哪、怎么改（开发约定、易踩的坑） |
| [`AGENTS-细节.md`](AGENTS-细节.md) | 各功能的实现细节 |
| [`AGENTS-发布.md`](AGENTS-发布.md) | 发布与交付 |

---

## 说明

- 本项目为单连接架构：同一时刻只连接一台 PLC
- 配置与日志默认写在 exe 同级目录（`app-settings.json`、`io-comments.json`、`Log\`）
