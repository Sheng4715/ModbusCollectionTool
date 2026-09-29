# Design.md — 技术设计文档

> 对应当前代码基线：P0（Modbus TCP 采集）已实现并通过端到端验证（2026-09-29）。
> 需求与优先级见 `Brief.md`；协作约定见 `AGENTS.md`。

---

## 1. 设计目标

| 目标 | 说明 |
|------|------|
| 只读采集 | 本期只读线圈/寄存器，不写入（写入属后续决策项） |
| 标准化地址 | 品牌差异全部收敛在数据表（`DriverRegistry`），协议层与 UI 不感知品牌 |
| 零第三方依赖 | 不引 NuGet，Modbus 报文手写（MBAP 帧小、只读四功能码，自研成本低） |
| UI 不卡顿 | 采集在后台线程，UI 只接收结果回切 |
| 故障可见 | 任何通讯异常 2–4 秒内反映到界面状态，绝不静默卡死 |

## 2. 架构总览

```
┌───────────────────────────────────────────────────────┐
│  UI 层  MainWindow.xaml(.cs)                          │
│  导航 / 设备配置表单 / IO 监控灯 / 总览状态             │
│    · IoPoint (INotifyPropertyChanged) 绑定灯           │
│    · 表单锁定 SetInputsEnabled / 状态 SetStatus        │
└──────────────▲────────────────────────┬───────────────┘
               │ Dispatcher 回切        │ 连接/断开/周期设置
┌──────────────┴────────────────────────▼───────────────┐
│  采集编排层  MainWindow 内 PollLoop（后台 Task）        │
│    · 按周期轮询 · 取消令牌生命周期 · 异常→断线兜底      │
└──────────────▲────────────────────────┬───────────────┘
               │ bool[] 线圈数据         │ 读请求
┌──────────────┴────────────────────────▼───────────────┐
│  协议层  Drivers/ModbusTcpClient                       │
│    MBAP 封帧/解析 · 0x01/0x02/0x03/0x04 · 2s 超时      │
│    请求串行(SemaphoreSlim) · tid 自增 · 异常码翻译      │
└──────────────▲────────────────────────────────────────┘
               │ 地址(起始+数量)
┌──────────────┴────────────────────────────────────────┐
│  地址层  Drivers/CommDriver + DriverRegistry           │
│    品牌 → 元件区表 · "X17"(八进制) → Modbus 绝对地址    │
└───────────────────────────────────────────────────────┘
```

**分层原则**：上层只依赖下层的窄接口；品牌差异只存在于地址层，报文差异只存在于协议层。

## 3. 模块职责

| 文件 | 职责 | 关键点 |
|------|------|--------|
| `MainWindow.xaml` | 全部 UI | 三页导航（总览/状态信息/设备配置）；采集周期下拉 7 档；面板显隐由 `Nav_Checked` 控制 |
| `MainWindow.xaml.cs` | 交互编排 | 连接状态机、轮询循环、`IoPoint` 列表（64 点，列优先八进制命名）、`XComments` 注释表 |
| `Drivers/CommDriver.cs` | 地址模型 | `AddressArea`：元件区 ↔ Modbus 地址双向映射；`IndexBase` 支持八进制（X/Y） |
| `Drivers/DriverRegistry.cs` | 品牌数据表 | 已注册 `xinje_xd`、`inovance_easy`；新增品牌 = 追加一个 `CommDriver` 实例 |
| `Drivers/ModbusTcpClient.cs` | Modbus TCP 主站 | 见 §4 |

### 3.1 关键状态字段（MainWindow）

| 字段 | 线程 | 说明 |
|------|------|------|
| `_modbus` | UI 写 / 后台读 | `ModbusTcpClient?`，断开时置 null |
| `_isConnected` | 仅 UI | 连接状态机标志；断线兜底回切后先检查它防重复断开 |
| `_pollCts` | 仅 UI | 轮询取消源；`StartPolling` 创建、`StopPolling` 取消+释放 |
| `_pollIntervalMs` | volatile | 采集周期；**UI 线程写、后台线程读**，故用 volatile 而非跨线程访问控件 |
| `_currentDriver` | UI 写 / 后台读 | 当前品牌驱动；选表时在 UI 线程缓存，避免后台碰 ComboBox（WPF DispatcherObject 不可跨线程访问） |

## 4. Modbus TCP 协议层设计

### 4.1 帧格式（MBAP）

**请求**：`[tid:2][pid=0:2][len:2][unitId:1][PDU]`，`len = 1 + PDU长度`

**响应读取**（⚠️ 验证阶段修过的关键点）：

```
读 6 字节 MBAP 头 → 校验 pid=0、tid 回环匹配、len ∈ [2,260]
→ 再读 len 字节（= unitId 1 + PDU）
```

> **教训**：头若读 7 字节（把 unitId 吞进头）再按 len 续读，会**多要 1 字节**导致
> 永久等待 → 读超时。MBAP 头恒为 6 字节，len 字段已包含 unitId。

### 4.2 支持的功能码（只读）

| 功能码 | 用途 | 响应解析 |
|--------|------|----------|
| 0x01 | 读线圈 → `bool[]` | 字节内 **LSB 在前**，`bit[i] = data[i/8] >> (i%8) & 1` |
| 0x02 | 读离散量输入 → `bool[]` | 同上（P1/RTU 预留，UI 暂未使用） |
| 0x03 | 读保持寄存器 → `ushort[]` | 大端序，`v = (b0<<8)|b1` |
| 0x04 | 读输入寄存器 → `ushort[]` | 同上（预留） |

异常响应（`fc|0x80`）翻译为中文：1 非法功能码 / 2 非法数据地址 / 3 非法数据值 / 4 从站设备故障。

### 4.3 并发与超时策略

- **请求串行**：`SemaphoreSlim(1,1)` 保证同连接上单请求在途（Modbus 事务按 tid 配对，但流式 TCP 下串行最稳）
- **单请求超时 2s**：`CancellationTokenSource.CreateLinkedTokenSource(用户取消令牌)` + `CancelAfter(2s)`，区分「主动断开」（退出循环）与「读超时」（触发断线）
- **连接超时 3s**：`ConnectAsync(ip, port, timeout)`
- **不释放 `_gate`**：`Dispose` 时可能有在途请求，`SemaphoreSlim` 无 OS 句柄，不释放避免竞态异常
- **事务校验**：tid 回环不匹配 / len 越界 → 抛 `IOException` → 走断线流程

## 5. 轮询与生命周期

### 5.1 连接流程

```
用户点「连接」
 → 校验 IP/端口 → 锁表单 → ModbusTcpClient.ConnectAsync(3s)
 → 成功: _modbus/_isConnected 置位，按钮变「断开」，StartPolling()
 → 失败: Dispose 客户端 → 解锁表单 → 红字提示（超时/失败原因）
```

### 5.2 轮询循环（后台 Task，非 DispatcherTimer）

```
while (!token.IsCancellationRequested)
    PollOnce:
        _currentDriver.TryResolve("X0", Coil) → 起始地址
        ReadCoilsAsync(起始, 64, token)
        Dispatcher.InvokeAsync → 刷新 _ioPoints[i].IsOn   ← 灯绑定自动更新
    等待 max(0, _pollIntervalMs - 本轮耗时)  → 周期从"上轮结束"起算，无累积漂移
异常: OperationCanceledException+token已取消 → 正常退出
      OperationCanceledException+token未取消 → 读超时 → 断线
      其他异常 → 断线
```

**选 Task 循环而非 DispatcherTimer 的原因**：读是 async I/O，Timer 的 tick 无法 await，会产生重入；Task 循环可自然串行 + 精确补时。

### 5.3 断线兜底（统一出口 `Disconnect(status, color)`）

| 触发源 | 表现 |
|--------|------|
| 用户点「断开」 | 状态灰「未连接」 |
| 读超时 / 协议异常 / 对端关闭 / 写失败 | 状态红「连接已断开：原因」，**回 UI 线程**执行，先查 `_isConnected` 防重入 |
| 关闭窗口 | 先 Disconnect 再 Close |

断开统一做：停轮询（Cancel+Dispose CTS）→ Dispose socket → 复位按钮 → 解锁表单 → 刷新总览。

### 5.4 当前数据流（X 点）

`Mock/PLC 线圈 20480..20543` → FC01 64 bit → `_ioPoints[0..63]`（八进制 X0..X77 顺序即地址顺序）→ `IsOn` 变更通知 → 灯变色。**索引即地址偏移**，无需逐点解析（依赖两品牌 X 区均 `StartIndex=0` 且连续）。

## 6. UI 状态矩阵

| 状态 | 表单（连接方式/协议/设备/周期/IP/端口/网卡） | 连接按钮 | 总览状态 |
|------|:---:|:---:|:---|
| 未连接 | 可编辑 | 「连接」 | 黄「未连接」 |
| 连接中 | 锁定 | 禁用 | — |
| 已连接 | 锁定 | 「断开」 | 绿「已连接」 |
| 断线 | 解锁 | 「连接」 | 红「连接已断开：…」 |

设备类型在连接中锁定 → 保证轮询期间 `_currentDriver` 不被换表。

## 7. 扩展点设计

| 需求（Brief 编号） | 挂接点 | 预留情况 |
|---|---|---|
| 新增品牌（P0-2 已具备） | `DriverRegistry` 追加 `CommDriver`；UI 自动出现（数据驱动） | ✅ 完成 |
| Modbus RTU 串口（P1-5） | 新建 `ModbusRtuClient`，复用 PDU 构造/解析（MBAP 之外加 CRC16）；`ConnectionTypeComboBox` 已能切换面板 | 协议层 PDU 部分与传输无关，可抽出共用 |
| 断线重连（P1） | 断线出口已收敛到 `Disconnect(status,color)`，加重连即在此处挂退避重试 | ✅ 出口统一 |
| 采集周期（P0-3） | `_pollIntervalMs` + 7 档下拉 | ✅ 完成 |
| 读寄存器（D/R 区） | `PollOnce` 增加 `ReadHoldingRegistersAsync` 分支；数据模型加寄存器点位 | API 已就绪（0x03/0x04） |
| xlsx 导入 IO 表（P2-6） | 替换 `XComments` 来源；注意**无第三方依赖**约束（xlsx 是 zip+xml，需评估手写 or 放宽为 CSV） | ⚠️ 待决策 |
| 多页面 IO（P2-7） | `_ioPoints` 目前绑死 X/Coil；抽 `IIoSource` 后可加 Y/M/D 页 | 需重构 |
| 数据存表格 / MES（P3） | 轮询成功回调处挂「数据落盘/上报」旁路，不干扰主循环 | 待设计 |

## 8. 关键设计决策记录（ADR）

| # | 决策 | 理由 | 备选否决原因 |
|---|------|------|------|
| 1 | 手写 Modbus 报文，不引 NuGet | 项目零依赖约定；只需 4 个只读功能码，帧格式简单 | NModbus 引入依赖，且本项目场景窄 |
| 2 | 后台 Task 轮询循环 | async I/O + 精确补时 + 单飞防重入 | DispatcherTimer 无法 await、会重入 |
| 3 | 跨线程用 volatile 字段/缓存对象传递 | WPF 控件不可跨线程访问 | 后台直接读 ComboBox 会抛异常 |
| 4 | 灯数据用索引直接对应地址偏移 | 两品牌 X 区均从 0 连续，简单且免逐点解析 | 逐点 `TryResolve` 每轮 64 次解析，无收益 |
| 5 | 异常一律走断线兜底 | 与 §1「故障可见」一致；P1 重连有统一挂点 | 分级重试推迟到 P1 |
| 6 | 单请求 2s / 连接 3s 超时 | 轮询周期最短 100ms，2s 已是 20 个周期，早断早恢复 | 可配置超时暂无需求 |

## 9. 已知限制与技术债

1. **单文件 code-behind 膨胀**：`MainWindow.xaml.cs` 已 ~640 行，采集编排、连接状态机、导航混放；P2 多页面前建议拆出 `PollingService`（哪怕仍由 MainWindow 持有）。
2. **X 点位与注释硬编码**：`XComments` 写死双工位重复注释，待 xlsx 导入（P2-6）。
3. **目标网卡下拉无实际作用**：`ScanNetworkAdapters` 未接入 `ConnectAsync` 本地端点绑定；多网卡环境下连接走系统路由。
4. **串口模式 UI 可选但功能为空**：选「串口」仅隐藏以太网面板，总览仍拼 IP:端口（P1-5 一并处理）。
5. **仅采集 X（线圈）**：D/R 寄存器页未做（API 已就绪）。
6. **断线文案中英混杂**：底层 Socket 异常消息直传（如 "Unable to write data…"）；如需可包一层中文映射。
7. **无版本控制**：目录非 git 仓库，建议尽快 `git init` + `.gitignore`（排除 `bin/ obj/ .vs/`）。

## 10. 验证方式（已实践）

- **Mock 服务器**：`%TEMP%\modbus-mock`（`dotnet run`，监听 502，FC01 返回 `(i+tick)%4==0` 变化位图，日志带毫秒时间戳 → 可验周期/地址/tid）
- **UI 自动化**：PowerShell UIA（`x:Name` 即 AutomationId：`IpTextBox`/`ConnectButton`/`StatusInfoNav`…；Collapsed 面板不在 UIA 树，需先切页）
- **截图对比**：两次抓窗口 PNG，对比灯阵偏移 → 证明实时刷新
- 已验证项见 `Brief.md` 变更记录（2026-09-29 P0 验证通过）

## 11. 构建与运行

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

---

*变更请同步更新本文件 §3–§8 与 `Brief.md` 变更记录。*
