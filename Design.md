# Design.md — 技术设计文档

> 对应当前代码基线：P0（Modbus TCP 采集）+ P2（IO 监控 X/Y 分段、xlsx 导入、注释持久化）已实现并验证（2026-09-30）；网卡本地端点绑定成功路径已验证。
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
| `MainWindow.xaml` | 全部 UI | 三页导航（总览/状态信息/设备配置）；IO 监控卡片头部行：**左侧** = X/Y 分段切换（`XViewSegment`/`YViewSegment`）+ 「导入 IO 表」按钮（原 `IoCardTitle` 标题文案已移除），**右侧** = 分页控件（`IoPagerPanel` = `IoPrevPageButton`/`IoPageText`/`IoNextPageButton`，仅 >1 页时显示）；卡片底部 `IoEmptyHint`（未导入时的占位文字）；采集周期下拉 4 档（100ms–1s，默认 100ms）；面板显隐由 `Nav_Checked` 控制 |
| `MainWindow.xaml.cs` | 交互编排 | 连接状态机、轮询循环、**动态 IO 点位集**（`_xPoints`/`_yPoints` 由导入表重建：`RebuildIoPoints` → `TryCreateIoPoint`，地址升序）、**分页**（`ApplyIoPage` / `ChangeIoPage` / `UpdateIoPager`，X/Y 独立页号）、xlsx 导入处理（驱动校验 + MessageBox 汇报）、网卡扫描与绑定（`NicInfo`） |
| `XlsxIoTableReader.cs` | xlsx 解析 | 零依赖手写（System.IO.Compression + XmlReader）：首个工作表 → sharedStrings/inlineStr → **地址形单元格 → 右邻为注释** 配对 |
| `Drivers/CommDriver.cs` | 地址模型 | `AddressArea`：元件区 ↔ Modbus 地址双向映射；`IndexBase` 支持八进制（X/Y） |
| `Drivers/DriverRegistry.cs` | 品牌数据表 | 已注册 `xinje_xd`、`inovance_easy`；新增品牌 = 追加一个 `CommDriver` 实例 |
| `Drivers/ModbusTcpClient.cs` | Modbus TCP 主站 | 报文见 §4；`ConnectAsync` 支持可选本地端点绑定（多网卡出口控制） |

### 3.1 关键状态字段（MainWindow）

| 字段 | 线程 | 说明 |
|------|------|------|
| `_modbus` | UI 写 / 后台读 | `ModbusTcpClient?`，断开时置 null |
| `_isConnected` | 仅 UI | 连接状态机标志；断线兜底回切后先检查它防重复断开 |
| `_pollCts` | 仅 UI | 轮询取消源；`StartPolling` 创建、`StopPolling` 取消+释放 |
| `_pollIntervalMs` | volatile | 采集周期；**UI 线程写、后台线程读**，故用 volatile 而非跨线程访问控件 |
| `_currentDriver` | UI 写 / 后台读 | 当前品牌驱动；选表时在 UI 线程缓存，避免后台碰 ComboBox（WPF DispatcherObject 不可跨线程访问） |
| `_xPoints` / `_yPoints` | 仅 UI | X/Y 两段点位列表，**数量由导入的 IO 表决定（不再固定 64）**，按 Modbus 地址升序；`IoPoint` 携带 `Address` 供轮询按偏移回填 |
| `_ioPageActive` | UI 写 / 后台读（volatile） | 状态信息页可见 = 采集；离开即停采（零报文，灯保留上次数据） |
| `_yViewActive` | UI 写 / 后台读（volatile） | 当前分段 true=输出 Y / false=输入 X；只读可见区 |
| `_xPageIndex` / `_yPageIndex` | volatile | X/Y 各自的当前页号（0 基）；UI 线程切页写入，后台经 `_visiblePage` 间接感知 |
| `_visiblePage` | UI 写 / 后台读（volatile） | **当前可见页的点位数组快照**：`ApplyIoPage` 整体替换，`PollOnceAsync` 只读它（不遍历 List，避免与重建竞态）；空数组 = 不发报文 |
| `_comments` | 仅 UI | 点位名→注释（OrdinalIgnoreCase），**既是注释表也是点位集来源**：启动 `LoadComments()` 读 `io-comments.json`，导入后整体替换 + `SaveComments()`；无文件则为空（界面空列表） |

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
- **连接超时 3s**：`ConnectAsync(ip, port, timeout, localEndPoint?)`
- **本地端点绑定**：`localEndPoint` 非空时先 `Socket.Bind(ip, 0)` 再连接 —— 多网卡/子网冲突时强制从所选网卡发出；绑定失败（网卡已拔出等）抛给上层红字提示，不静默回退
- **不释放 `_gate`**：`Dispose` 时可能有在途请求，`SemaphoreSlim` 无 OS 句柄，不释放避免竞态异常
- **事务校验**：tid 回环不匹配 / len 越界 → 抛 `IOException` → 走断线流程

## 5. 轮询与生命周期

### 5.1 连接流程

```
用户点「连接」
 → 校验 IP/端口 → 取所选网卡 NicInfo 构造本地端点(源端口 0，可为 null=自动路由)
 → 锁表单 → ModbusTcpClient.ConnectAsync(3s, localEndPoint?)
 → 成功: _modbus/_isConnected 置位，按钮变「断开」，StartPolling()
 → 失败: Dispose 客户端 → 解锁表单 → 红字提示（超时/绑定失败/路由不可达）
```

### 5.2 轮询循环（后台 Task，非 DispatcherTimer）

```
while (!token.IsCancellationRequested)
    PollOnce（采集跟随可见性 + 分页）:
        _ioPageActive == false → return   ← 离开状态信息页：零报文，灯保持上次数据
        _visiblePage 为空（未导入 IO 表）→ return   ← 同样零报文
        取本页点位的 min..max 地址跨度，单次 ReadCoilsAsync(max-min+1, token)
        Dispatcher.InvokeAsync → 按 offset = Address - min 回填各点 IsOn
    等待 max(0, _pollIntervalMs - 本轮耗时)  → 周期从"上轮结束"起算，无累积漂移
异常: OperationCanceledException+token已取消 → 正常退出
      OperationCanceledException+token未取消 → 读超时 → 断线
      其他异常 → 断线
```

> **通讯量**：可见页 + 可见分段单读（1×FC01），离页或未导入为 0；回页/切段/切页后下一周期（≤采集周期）恢复，最多一帧延迟。
> **数据保留**：未显示区/未显示页与离页期间不写入 `IsOn`，灯自然保持上次值（断线同样不清色）。
> **单帧上限**：单页最多 64 点，地址跨度受驱动 X/Y 区大小约束（信捷 64 / 汇川 1024），恒 ≤2000（FC01 单次上限），故不会拆帧。
> **代价**：离页期间无报文 ⇒ 断线检测暂停，回到状态页后首个请求失败才报红（≤2s 读超时）——见 §9-9。

**选 Task 循环而非 DispatcherTimer 的原因**：读是 async I/O，Timer 的 tick 无法 await，会产生重入；Task 循环可自然串行 + 精确补时。

### 5.3 断线兜底（统一出口 `Disconnect(status, color)`）

| 触发源 | 表现 |
|--------|------|
| 用户点「断开」 | 状态灰「未连接」 |
| 读超时 / 协议异常 / 对端关闭 / 写失败 | 状态红「连接已断开：原因」，**回 UI 线程**执行，先查 `_isConnected` 防重入 |
| 关闭窗口 | 先 Disconnect 再 Close |

断开统一做：停轮询（Cancel+Dispose CTS）→ Dispose socket → 复位按钮 → 解锁表单 → 刷新总览。

### 5.4 当前数据流（X/Y 点）

点位集由导入的 IO 表决定（`RebuildIoPoints`）：每个点位用当前驱动解析出自己的 Modbus 地址（如信捷 `X17` → 20495），按**地址升序**排列，再按每页 4×`IoRowsPerColumn` 切片。

```
采集（每周期）: 当前显示分段 + 当前页的 ≤64 个点（_visiblePage 快照）
              → 求 min..max 地址跨度 → FC01 一次读回
              → 按 offset = p.Address - min 回填各点 IsOn
              → IsOn 变更通知 → 灯变色
```

> 因地址不再连续，早期「索引即地址偏移」的前提已失效，改为 `IoPoint` 自带 `Address` + 偏移回填。
> 切页只改绑定，**不重置 `IsOn`**：翻回上一页仍显示离开时的灯色（与「离页保留数据」一致）。

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
| 采集周期（P0-3） | `_pollIntervalMs` + 4 档下拉（100ms–1s，默认 100ms） | ✅ 完成 |
| 读寄存器（D/R 区） | `PollOnce` 增加 `ReadHoldingRegistersAsync` 分支；数据模型加寄存器点位 | API 已就绪（0x03/0x04） |
| xlsx 导入 IO 表（P2-6） | `ImportIoTableButton_Click` → `XlsxIoTableReader.Read` → 逐行按 `_currentDriver` 校验（仅 X/Y 元件，且编号落在驱动区范围内）→ 整体替换 `_comments`（**该表同时决定监控点位集**）→ `RebuildIoPoints()` 重建列表 + 复位第 1 页 → `SaveComments()` 持久化；结果用 `MessageBox` 汇报「成功导入 N 项，失败 M 项」+ 失败明细 | ✅ 完成（零依赖手写解析，见 §9-8 解析限制） |
| 多页面 IO（P2-7） | X/Y 分段切换：`_xPoints`/`_yPoints` 双列表 + 双 ItemsControl 切 Visibility；**分页**：每页 4×`IoRowsPerColumn`，X/Y 独立页号（`_xPageIndex`/`_yPageIndex`）；再加 M/D 段 = 复制一个列表 + 网格 + 分段按钮（页号同样独立） | ✅ X/Y + 分页完成；M/D 段暂缓（有需求按同模式扩展） |
| 数据存表格 / MES（P3） | 轮询成功回调处挂「数据落盘/上报」旁路，不干扰主循环 | 待设计 |

## 8. 关键设计决策记录（ADR）

| # | 决策 | 理由 | 备选否决原因 |
|---|------|------|------|
| 1 | 手写 Modbus 报文，不引 NuGet | 项目零依赖约定；只需 4 个只读功能码，帧格式简单 | NModbus 引入依赖，且本项目场景窄 |
| 2 | 后台 Task 轮询循环 | async I/O + 精确补时 + 单飞防重入 | DispatcherTimer 无法 await、会重入 |
| 3 | 跨线程用 volatile 字段/缓存对象传递 | WPF 控件不可跨线程访问 | 后台直接读 ComboBox 会抛异常 |
| 4 | ~~灯数据用索引直接对应地址偏移~~ → **2026-09-30 改为点位自带 `Address` + 偏移回填** | 点位集改为表驱动后地址不再连续，「索引即偏移」前提失效；解析只在 `RebuildIoPoints` 做一次，轮询仍只按偏移取值 | 每轮逐点 `TryResolve`（无必要，且每周期 64 次解析） |
| 5 | 异常一律走断线兜底 | 与 §1「故障可见」一致；P1 重连有统一挂点 | 分级重试推迟到 P1 |
| 6 | 单请求 2s / 连接 3s 超时 | 轮询周期最短 100ms，2s 已是 20 个周期，早断早恢复 | 可配置超时暂无需求 |
| 7 | 采集跟随可见性（可见分段 + 可见页单读，离页停采） | 通讯量优化：可见区相对双读减半、离页归零；不写入即数据自然保留 | 双读常开（通讯翻倍、无人看也在跑）；离页心跳（保断线检测但离页仍有报文）——需要时可作为 §9-9 的后续补充 |
| 8 | **IO 点位集由导入的 IO 表决定**（不再固定 64 点） | 现场有多少点位就监控多少，不铺空占位；导入期按驱动校验保证地址可用 | 固定 64 点（表里更多点位无法监控）；按驱动区全量展开（未接线的点位也铺满界面） |
| 9 | **分页固定 4 列 × 16 行 = 64 点/页，X/Y 独立页号** | 与既有 4×16 视觉布局完全一致，超出一页自动分页；单页地址跨度受驱动区大小约束（≤1024），恒小于 FC01 单帧 2000 点上限，仍是 1×FC01 | 每列行数随内容自适应（各列高矮不齐、失去规整）；改用滚动条（与卡片布局及 1024×640 最小窗口冲突） |

## 9. 已知限制与技术债

1. **单文件 code-behind 膨胀**：`MainWindow.xaml.cs` 已 1051 行（动态点位集与分页又增加了约 210 行），采集编排、连接状态机、导航、导入、分页混放；下一次大功能前建议拆出 `PollingService` + `IoPointSet`（哪怕仍由 MainWindow 持有）。
2. ~~X 点位与注释硬编码~~ → **已解决**（2026-09-30，二次改造）：点位集改为**完全由 IO 表驱动**，内置 `XComments` 默认表**已删除**；`io-comments.json` 既是注释表也是点位集来源，无文件时界面为空列表并提示「尚未导入 IO 表」。
3. ~~目标网卡下拉无实际作用~~ → **已解决**（2026-09-29 接入，2026-09-30 成功路径验证）：下拉项为 `NicInfo`（`网卡名(ip)`），连接时 `Bind(所选IP, 0)` 强制源地址。错误路径（选错网卡→「网络不可达」）与成功路径（选 Tailscale 网卡自连 100.95.29.47:502 → 已连接 + 正常轮询）均已端到端验证。
4. **串口模式 UI 可选但功能为空**：选「串口」仅隐藏以太网面板，总览仍拼 IP:端口（P1-5 一并处理）。
5. **仅采集线圈（X/Y）**：D/R 寄存器页未做（FC03/04 API 已就绪）。
6. **断线文案中英混杂**：底层 Socket 异常消息直传（如 "Unable to write data…"）；如需可包一层中文映射。
7. ~~无版本控制~~ → **已解决**（2026-09-29）：已 `git init`（分支 `main`）+ `.gitignore`，首个提交 `75ce810`；Git 2.55 已通过 winget 安装并在 PATH 上，身份为仓库级配置 `G.Sheng`。
8. **xlsx 解析规则有限**（`XlsxIoTableReader`，零依赖的取舍）：仅读**首个工作表**；**不支持合并单元格**（跨列/跨行合并会丢配对）；配对规则固定为「地址形单元格（字母+数字）→ 右侧一列单元格为注释」（匹配 A/B、D/E 双面板模板）；表头等非地址行在解析期即被丢弃；非 X/Y 元件（如 M100、D100）与超出驱动地址范围/八进制非法的点位在导入期计入「失败」并附中文原因；无有效点位时**拒绝替换**（警告对话框）。导入结果一律用 `MessageBox` 汇报（`导入结果的内联文字提示已移除`），失败明细最多列 12 条、其余折叠汇总。复杂模板需先整理为该布局。
9. **离页期间断线检测暂停**（2026-09-30 采集可见性的取舍）：离开状态信息页零报文 ⇒ PLC 断线/链路失效在离页时不被发现，回到状态页后首个请求失败才报红（≤2s 读超时）；离页久置后 TCP 可能被 NAT/设备静默回收，回页首读也会暴露。若将来需要离页保活，可在离页态加低频心跳（如 2s 读 1 点，见 ADR-7 备选）。
10. **单页地址跨度受 FC01 单帧上限（2000 点）约束**：`PollOnceAsync` 对「当前页」取 `min..max` 一次读回，跨度 = `max-min+1`。加入扩展模块后信捷 X 的有效地址从 20480 延伸到 23599，若**同页**同时含本体点（20480）与 ED 点（23599），跨度可达 **3120 > 2000** → 设备返回非法数据值异常 → 触发断线。当前实际表格（X0–X43 + X10000–X10017，跨度 272）安全，但**混排远距离地址的表格会踩雷**。`ModbusTcpClient` 目前不做 count 上限校验，修法见 §9-12。
11. **切换设备类型会重建点位集**：`DeviceTypeComboBox_SelectionChanged` 会按新驱动重新解析地址（`RebuildIoPoints`），新驱动下解析失败的点位会被剔除、页号复位到第 1 页。连接中设备类型被锁定，故不会打断轮询。
12. **（待定）大跨度分页读的加固**：把本页点位按「地址连续段」分组，每段一次 FC01（常态仍是 1 帧，仅跨度超限时才拆多帧）；或对超限跨度按 2000 切窗多次读。**尚未实现** —— 需先确认现场是否存在跨本体/ED 混排的表格。

## 10. 验证方式（已实践）

- **Mock 服务器**：`%TEMP%\modbus-mock`（`dotnet run`，监听 502，FC01 返回 `(i+tick)%4==0` 变化位图，日志带毫秒时间戳 → 可验周期/地址/tid）
- **UI 自动化**：PowerShell UIA（`x:Name` 即 AutomationId：`IpTextBox`/`ConnectButton`/`StatusInfoNav`…；Collapsed 面板不在 UIA 树，需先切页）
- **截图对比**：两次抓窗口 PNG，对比灯阵偏移 → 证明实时刷新
- **UIA 局限（实测）**：WPF ComboBox 弹出层不在 UIA 树内（ExpandCollapse / Alt+↓ 均取不到条目）→ 涉及下拉选项的操作暂需人工验证或改代码级验证；标题栏最小化/关闭按钮无 `x:Name`（无 AutomationId），关闭窗口用 `WindowPattern.Close()`；模态对话框按钮必须**真实鼠标点击**（UIA Invoke 会阻塞到 ShowDialog 返回）；**WPF 面板（StackPanel/Grid）没有 AutomationPeer**，自身节点查不到（如 `IoPagerPanel`），判定其显隐要改用其子控件（`IoPageText`/`IoPrevPageButton`）是否存在
- **文件对话框可自动化了（2026-09-30 实测，推翻此前「涉及文件对话框只能人工」的结论）**：`OpenFileDialog` 是 `#32770` 顶层窗口，但 **UIA 的 `RootElement.FindAll(Children, pid)` 找不到它**（要用 Win32 `EnumWindows` + `GetClassName` 定位）。它又是 DirectUI 实现：文件名框在 UIA 里只有无 peer 的 `Pane`、`ComboBox` 不支持 `ValuePattern`，且 `SetForegroundWindow` 会被前台锁拒绝 → `SendKeys` 收不到。**可靠做法是纯 Win32 消息，不需要任何键盘焦点**：`EnumChildWindows` 找 控件id=1148 的 `Edit`（文件名）与 控件id=1 的 `Button`（打开），然后 `SendMessage(edit, WM_SETTEXT, path)` + `SendMessage(button, BM_CLICK)`。
  - 注意：跨进程**读**控件文本要用 `WM_GETTEXT`，`GetWindowText` 对其它进程的控件恒返回空（本次踩过，导致误判「赋值失败」）。
- **⚠️ 触发导入按钮：必须用真实鼠标点击，且必须在主脚本里（2026-09-30 二次实测，推翻下面这条旧结论）**：
  - ~~「放进 `Start-Job` 里 UIA `Invoke()`」~~ **行不通**：PowerShell 后台 Job 没有消息泵，`InvokePattern.Invoke()` **静默失败**（不抛异常），对话框永不出现 —— 此前两次探测「找不到对话框」的真因即此，而非对话框实现变化。
  - 正确做法：主脚本内 `GetClickablePoint()` → `ShowWindow(hwnd, 9)` → `SetForegroundWindow` → 等 400ms → `SetCursorPos` → 等 200ms → `mouse_event(LEFTDOWN/LEFTUP)`。鼠标点击本身不阻塞（`ShowDialog` 在应用自己的线程上模态），主脚本可继续驱动对话框。
- **文件对话框填充：`SendKeys` 输路径 + `{ENTER}` 足够（比 Win32 控件 id 更简单）**：对话框出现后（`#32770`，标题=代码里的 `dialog.Title`，如「导入 IO 点位表」）用 `SendKeys.SendWait(path)` 再 `SendWait("{ENTER}")` 即可完成选择。其上文的 `WM_SETTEXT`+`BM_CLICK`（控件 id 1148/1）同样可用，但需先解决前台锁，`SendKeys` 路径实测更省事。
  - 仍成立：跨进程**读**控件文本要用 `WM_GETTEXT`，`GetWindowText` 对其它进程的控件恒返回空。
- **截图局限（实测）**：自动化过程中界面可能被人工操作，且截图相对动作有滞后一拍现象 → 截图仅作辅助参考，**以功能证据为准**（server.log 报文、按钮态、json 内容、UIA isSelected 查询）
- **P2 验证（2026-09-30，全部通过）**：
  - 解析器：合成 xlsx 4 对配对 ✓；真实 `IO模板.xlsx` 60 对（A/B+D/E 双面板）✓（控制台 harness）
  - 导入闭环：真实文件对话框导入 → `io-comments.json` 写入 60 条（X0–X43、Y0–Y27）✓
  - 持久化：重启后显示模板注释（CommentN）而非内置 XComments ✓
  - 分段切换：UIA SelectionItem 选中、标题换为「IO 监控（输出 Y）」、两页各 64 点渲染 ✓
  - 双读轮询：server.log 397×`start=20480` + 397×`start=24576` 严格成对、tid 连续、~100ms 节奏 ✓
  - 实况灯阵：X 绿灯八进制 X0/X4/X10…、Y 绿灯 Y1/Y5/Y11…，与 mock `(i+tick)%4==0` 图案吻合 ✓
  - 连接/断开闭环：绑定网卡自连成功（按钮 断开、总览绿字）→ UIA 断开（按钮回 连接）→ mock 记录 `client closed` ✓
- **采集可见性验证（2026-09-30，11 项断言全过）**：连接后停在设备配置页 5s → **零 FC01**；进状态信息 X 页 → 只见 `start=20480`（25 次/2.5s ≈ 100ms），`start=24576` = 0；切 Y 段 → X 计数冻结、Y 启动；离页 3s → 双计数冻结（灯数据保留的机制保证）；回页 → Y 恢复、X 仍冻结；切回 X → X 恢复、Y 冻结；断开+关闭正常。
  - 脚本方法论：切换/离页动作后先 **settle 600ms** 再取计数快照（在途请求属切换前），否则会误报 +1 假失败。
- **动态点位集 + 分页验证（2026-09-30，全部通过）**：
  - 表驱动：`io-comments.json` 60 条 → X 段渲染 **36 点**（4 列 16/16/4/0）、Y 段 **24 点**（16/8/0/0），与表内容严格一致 ✓（UIA 逐列枚举点位名核对：X0–X17 / X20–X37 / X40–X43）
  - 空列表：删除 `io-comments.json` 重启 → 各列 0 点、`IoEmptyHint` 显示「尚未导入 IO 表」、分页控件隐藏 ✓
  - 分页：信捷单段上限恰好 64 点（=一页）无法自然超页，故临时把 `IoRowsPerColumn` 改为 4（每页 16 点）来触发 → 36 点分 3 页，`IoPageText` 依次 `1/3`→`2/3`→`3/3`，列内容 X0-X17 / X20-X37 / X40-X43 正确，首/末页箭头分别置灰 ✓；**验证后已还原为 16 并复验**（代码中无 `TEMP-PAGINATION-TEST` 残留）
  - 回归：Y→X 切回后 X 各列 16/16/4/0 正常；冷启动默认 X 段正常 ✓
  - **过程中发现并修复 1 个真 bug**：`ApplyIoPage` 起初只把页面切片绑到 X 的 4 个列控件，导致 Y 段网格永远拿不到 `ItemsSource`（Y 段渲染全空）——已改为按 `_yViewActive` 绑定「当前分段」对应的网格。
- **完整 UI 导入端到端验证（2026-09-30，真实鼠标点击 + SendKeys 路径，全部通过）**：
  - 驱动：启动应用（标题「采集工具」）→ 状态信息页 → **真实鼠标点击**「导入 IO 表」→ `#32770` 对话框出现（标题「导入 IO 点位表」）→ `SendKeys` 输 `IO模板.xlsx` 全路径 + `{ENTER}` → 导入完成
  - 落盘：`io-comments.json` 由 60 条 → **92 条**（X 52 / Y 40，扩展模块 32 条）
  - 界面（重启后加载）：X 段 4 列 **16/16/16/4 = 52 点**、Y 段 **16/16/8/0 = 40 点**，与落盘数据、与离线复算**三方一致**；点位按地址升序（X40,X41,X42,X43 → X10000..X10017）
  - 分页控件不可见（各 1 页）、`IoEmptyHint` 不在树中（有数据）—— 均符合预期
  - 离线复算同一模板（复用真实 `XlsxIoTableReader`+`CommDriver`+`DriverRegistry`）：接受 92/失败 0；**每页 FC01 跨度 272 ≪ 2000 上限**；边界输入 17 项（X8/X100/X11800/X40000/M100/D10/Z9/SM5/地址/X/1X0 拒绝）全符合预期
  - 已知数据现象（非代码问题）：模板扩展段 `Y10010–Y10017` 的注释为占位文本，与 X 侧编号不对应 —— 属表格内容，程序忠实导入
- **信捷扩展模块地址 + 导入闭环验证（2026-09-30，全部通过）**：
  - 驱动层独立控制台 harness（`%TEMP%\dsh_drvtest`，只编译 `CommDriver.cs` + `DriverRegistry.cs`，不依赖 UI）**29 项断言全过**：本体 `X0/X43/X77`、扩展 `X10000/X10017/X10077/X10100/X11777`、BD `X20000/X20100/X20177`、ED `X30000/X30077`，以及 Y 侧对应值与 `M100`；`X8`（八进制非法）/`X100`（本体与扩展之间的空档）/`X11800`（扩展区外）/`Z9`（未定义元件）均正确拒绝
  - 端到端导入真实 `IO模板.xlsx`（经上述文件对话框自动化驱动）→ **`io-comments.json` 落盘 92 条**（本体 60 + 扩展模块 `X10000–X10017` / `Y10000–Y10017` 共 32），失败 0 —— 证明「多段地址区 + `TryResolve` 修复 + 导入链路」全通

## 11. 构建与运行

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build DSH_Modbus采集工具.sln -v q
dotnet run --project DSH_Modbus采集工具
```

---

*变更请同步更新本文件 §3–§8 与 `Brief.md` 变更记录。*
