# AGENTS-细节.md — 实现细节（按需查阅）

> **本文件不会被自动加载进上下文**，是 AGENTS.md 的配套细节档。
> 拆分原因：两档原本合在 AGENTS.md 里共 136 KB，超过工作区指令预算（65536 字节），
> 结果是**整份 AGENTS.md 被丢弃**、规则一条都没进上下文。
>
> **什么时候必须读这里**：动到自由监控 / 全局变量 / 系统变量 / PLC 状态机 / 采集数据 /
> 心跳与自动重连 / 运行日志落盘 / 字节顺序 —— 也就是下面四章覆盖的任何一块。
> 只看 AGENTS.md 动手，会漏掉这些章节里成条的 ⚠️ 坑。
>
> 章节编号与 AGENTS.md 保持同一套：**§5.x / §6.x / §7.x / §8.x 都在本文件**，
>§1–§4、§9–§11 在 AGENTS.md。两个文件里的 § 引用按编号就能找到位置。

---
## 5. IO 监控

- **点位集完全由导入的 IO 表驱动**（不再固定 64 点）；未导入则空列表 + `IoEmptyHint`「尚未导入 IO 表」
- 持久化唯一来源 = `io-comments.json`（`AppContext.BaseDirectory`，**键=点位名、值=注释**）；导入即整体替换并写盘
- 导入校验：**仅接受 X / Y 元件**，地址须落在当前驱动范围内；结果**一律用 `ThemeMessageDialog`** 汇报（0 项有效则警告框且拒绝替换）
- **分页**：每页 4 列 × `IoPager.RowsPerColumn`(16) = 64 点，X/Y 各自独立页号；单页时整体隐藏。列宽 `*` 等分；行高 22、行距 2、列距 4
- **采集跟随可见性**：只读**当前显示分段 + 当前页**（`_visiblePage` 快照，按所属地址区分块 FC01，同区跨度超 2000 点切窗，默认 100ms）；**离开本页停止 IO 采集，独立心跳继续**；断线不清灯色（`IsOn` 保持上次值）
- **五个分段统一「跟随可见性」**（输入监控 / 输出监控 / 自由监控 / 系统变量 / 通讯监控）：只有当前显示的才读，离开者**保留上次值、不清空、不断线**。外层另有页面级闸门 `_ioPageActive`（离开设备监控页则全停）。详见 §5.1 与 `Design.md` §5.6
  ⚠️ **第五分段「通讯监控」（P2.4，已实现 2026-11-26）同样遵循这条**：它一选中，前四个分段全被遮住，
  于是 IO 与自由监控的轮询本就会停（用户 2026-11-25 定案接受这点）。此时仍能看到的是
  **不看可见性**的那几类流量：采集任务组变量、扫码器触发变量、心跳、扫码器收发、HTTP 上报。
  不要为了「让通讯监控看到 IO 报文」去给轮询加例外 —— 那会破坏这条统一口径，见 `Design.md` ADR-45
- **卡片内同一时刻只有一条滚动条**：Row=2 的 `Grid` 下，X/Y 共用一个 `IoScrollViewer`（每页固定 64 点，见 §5 分页），自由监控则让 `FreeMonitorPanel` 自己填满该格、只让列表滚动（表头常驻 —— 它是「+」的落点）。⚠️ **列表不要写 `MaxHeight`**：面板整体已绑 `ViewportHeight`，写死高度会让「里层先滚、里层滚不动了外层再滚」，两条滚动条并存（历史缺陷：527f2e2 给自由监控列表套了 `MaxHeight="380"`）
- **合法异常码不算断线**（2026-10-16）：IO 分块读**单独捕 `ModbusResponseException`** 并与成功路径分开 —— 跳过该块、其余块照读、该块的点保持上次值，**不断开也不重连**；某块**连续** `ModbusPoller.ConsecutiveExceptionThreshold`（**5**）次才记一条琥珀「通讯异常」（措辞 `原因（地址 xxx 异常码 N）`），**只记一次、读通即清零**（不这么做，100ms 一拍会让 1000 条日志 100 秒内刷满）。**超时 / IO 异常仍照旧断开**。详见 §5.1 / §6 与 `Design.md` §8-12

**灯色（`IoPoint.StateBrush`）**：灭灯 `#C4C4C4`；点亮时**输入 X 绿 `#0B7A0B`、输出 Y 橙 `#FFA500`**。

- 由 `IoPoint.IsOutput`（`init` 属性）区分，创建时在 `IoPointSet.TryCreatePoint` 按点位名首字母定死：`IsOutput = normalized[0] == 'Y'`
- 画刷**按当前主题从 `Io.*` 键取**（上面三个值是浅色主题的值；深色下绿灯提亮为 `#4EC94E`、灭灯为 `#5A5A5A`），取不到时回退到 `InputOnFallback` / `OutputOnFallback` / `OffFallback` 三个 `static readonly` + `Freeze()` 的浅色画刷
- **不要在 `StateBrush` 里 new 画刷**（原实现每次取属性都新建对象）；字典本身有缓存，按键取不产生新对象
- 换主题后靠 `RaiseStateBrushChanged()` 触发绑定重算（`StateBrush` 无 setter）
- XAML 侧不用改：`IoItemTemplate` 的灯仍绑 `Background="{Binding StateBrush}"`

### 5.1 自由监控（P2.2，第三分段）

设备监控页第三个分段（`FreeViewSegment`），列表式监控项：**元件名称 / 数据类型 / 显示格式 / 当前值 / 删除**
（第四分段「系统变量」见 §5.4；第五分段「通讯监控」见 §5.10，2026-11-26 已实现）。

**账户权限（P 权限，2026-11-06）**：这一页是**全应用唯一受权限管控的地方** ——
「当前值」可以对 PLC 随意写入，故技术员账户下整列置灰。

- 角色与密码在 `Modeling/UserSession.cs`（`UserRole` / `UserRules`），登录框 `Ui/LoginDialog.cs`，
  接线 `MainWindow.Account.cs`；入口是**左侧导航底部**的账户按钮（Win11 头像 + 用户名）
- ⚠️ **置灰走 `MonitorValueTextBox` 的 `Style.Triggers`，不要逐行设 `IsEnabled`**：
  这一格在 `DataTemplate` 里、每行一份，增删会重建，逐个设置必然漏
  （新建的行拿不到，表现为「已置灰但新加的项还能写」）。
  触发器绑窗口上的 `MonitorValueTextBoxReadOnly` 依赖属性（`RelativeSource AncestorType=Window`）——
  权限是「当前账户」的属性，不是「这一行」的
- ⚠️ **只挡写入，不挡其它操作**：名称框、增删、排序照旧（用户原话「除了自由监控不可以写入值，其他无限制」）。
  采集任务的写入、扫码器回写、全局变量编辑**一律不受限** —— 别顺手也加限制
- 缺省技术员（免登录）、注销即退回技术员、**登录状态不落盘**（每次启动都是技术员）
- ⚠️ **登录 / 注销不写运行日志**（用户 2026-11-06 定案）：那是界面操作不是设备状态；
  当前账户一直显示在导航底部按钮上，不需要日志复述
- ⚠️ **密码框必须是 `PasswordBox`**（`Dialog.PasswordBox` 样式），不是 TextBox 手动打码 ——
  后者会在窗口里留一份可读明文。注意 `PasswordBox` 的取值属性是 `Password` 不是 `Text`，
  事件是 `PasswordChanged` 不是 `TextChanged`
- ⚠️ **登录框的输入框与按钮必须左右对齐**（2026-11-06 用户要求「把这两个按钮与输入框对齐」）：
  三个元素吃**同一个列宽**（`Place(..., stretch: true)`），按钮行 `Grid.SetColumn(buttons, 1)`、
  **不跨列**。按钮行内部两列都用 `*`（等分），**不要 Auto** ——
  用 Auto 时前面那个 Star 列会吃掉剩余空间，两个按钮被挤到右边：
  UIA 实测右缘齐了、**左缘差 104 DIP**（输入框左 827 / 按钮组左 931）。等分后四个边全部落在框线上
- ⚠️ **输入框宽度是算出来的，不是写死的**。用户 2026-11-06 定「物理宽 260px」：
  150% 缩放下 260px = **173.33 DIP**，故窗口宽 = `173.33 + 标签列68 + 左右外边距32 = 273.33`，
  **再补窗口边框 14.66**（`Window.Width` 是含边框的总宽，每边 7.33）→ 定 **288 DIP**。
  ⚠️ 只写 273.33 时输入框量出来是 238px，比目标少 22px —— 这条实测过。
- ⚠️ **两个按钮的 `MinWidth` 要清零**：`Dialog.Button` 默认 84，两个 84 + 8 间隔 = 176 DIP，
  而窄窗口下这一列只有 173.33 → 撑出 2.7 DIP（实测物理 **2px，右缘对不齐**）。
  清零后按钮宽度只由列宽决定（124px × 2 + 12 间隔 = 260px 正好）
- ⚠️ **导航底栏的分隔线要在「设置」正上方**（2026-11-06 用户第二次定案）：
  顺序是「账户按钮 → 分隔线 → 设置」。曾把 `Separator` 排在账户按钮**之前**，
  于是那条线落在「日志卡底部」与「账户按钮」之间，看起来像给账户按钮划的
- ⚠️ **账户按钮内部比 `NavRadioButton` 更紧凑**（2026-11-06 用户报「技术员」被截断后调）：
  导航列固定 112 DIP、右边距让出后可用约 96，而 `NavRadioButton` 的
  Padding 14 + 头像 20 + 间距 10 + 间距 14 = 58，留给三个汉字只剩 **38 DIP**（FontSize 14 需约 42）。
  故 `AccountButton` 单独收窄：**Padding 14→10、头像 20→18、图标右边距 10→8**，省出 12 DIP（38→50）。
  ⚠️ **`Margin` 左边距必须仍是 8** —— 与 `NavRadioButton` 一致，UIA 实测两者左缘同为 204。
  ⚠️ 不要为此加宽导航列：112 是**成对常量**（内容区 `Margin="112,0,0,30"` 与底栏左缩进都依赖它）

**排序（2026-10-31）**：表头右端有 **↑ / ↓** 两个按钮，作用于**当前选中行**，
与全局变量表（§5.2）同一套交互 —— 点行内任意处选中、底色变 `Brush.Accent.Subtle`、
首行 ↑ 灰 / 末行 ↓ 灰 / 没选中都灰。判断只在 `UpdateMonitorMoveButtons` 一处，
置灰与点击拦截必须同源。

- 选中态是**纯界面态**（`MonitorItem.IsSelected` + `_selectedMonitorItem`），不落盘
- `MonitorItemSet._items` **同时**是界面集合与落盘顺序（`DataPointStore` 按它写盘），
  故 `Move` 一次就把两边都改了 —— 不需要像全局变量那样维护两份列表
- ⚠️ **`Move` 里必须重排行号**：`MonitorItem.Id` 转发的是 `Model.Id`，而 `Model.Id`
  只在新增时由 `NextId()` 分配，删过项之后就会跳号（实测原数据是 `1..6, 8, 9, 10`
  —— 第 7 项被删过）。排序后若不重排，同一屏里会同时出现「跳号的旧序号」和「新位置」
- ⚠️ **`Id` 变了不会自动通知**：`MonitorItem.Id` 是只读转发属性，必须由
  `RaiseIdChanged()` 显式发一次 `PropertyChanged`，否则左边那列 # 停在旧值
- 增删后要重算可用性（`RefreshMonitorView` 末尾调）；删掉的正好是选中行时还要清掉
  `_selectedMonitorItem`，否则 ↑↓ 会作用于一个已不在列表里的对象

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

- ✅ **串口（Modbus RTU）已实现**（2026-11-18，P4.4）：串口模式与以太网面板互斥显隐，参数面板 / 持久化 / 探测读 / 断线重连俱全，详见 §8.3

### 5.2 全局变量（P3.3，数据管理页第二分段）

数据管理页分三段：**采集数据**（P3.1 骨架）/ **全局变量**（本项）/ **系统变量**（P3.5，见 §5.3）。
表列：**序号 / 变量名称 / 数据类型 / 映射地址 / 注释 / 删除**。

⚠️ **本页只管理变量定义** —— 不显示当前值、不写入。`GlobalVariable.Value` 仍是内存字段（不落盘），
界面上没有编辑入口。

**排序（2026-10-31）**：表头「共 N 个」右侧有 **↑ / ↓** 两个按钮，作用于**当前选中行**。

- 选中：点行内任意处（含输入框 —— 不影响编辑），该行底色变 `Brush.Accent.Subtle`
- ↑↓ 可用性：没选中 → 都灰；首行 → ↑ 灰；末行 → ↓ 灰。**三档判断只在
  `UpdateGlobalVarMoveButtons` 一处**（置灰与 Click 里的越界拦截必须同源，
  否则会出现「按钮亮着但点了没反应」）
- 选中态是**纯界面态**（`GlobalVarItem.IsSelected` + `_selectedGlobalVar`），不落盘；
  重启后没有选中行是正常的
- 增删改后要重算可用性（`RefreshGlobalVarView` 末尾调）—— 删掉末行后原来那句
  「不能下移」就过期了；删掉的正好是选中行时还要把 `_selectedGlobalVar` 清掉，
  否则 ↑↓ 会作用在一个已不在列表里的对象上
- **顺序就是 `GlobalVar.json` 的数组顺序**：`GlobalVariableSet._items` 是 `List`，
  `Save` 按它写盘、`Load` 按文件顺序读回 → 改完顺序重启即生效，不需要额外的排序字段
- ⚠️ `Move` 必须**同时**改两处：界面的 `ObservableCollection`（画面顺序）与模型的
  `List`（落盘顺序）。只改一边 = 画面动了但重启变回原样，或存对了但界面不刷新
- ⚠️ `GlobalVarItem.Id` 必须是**会通知**的属性：排序后 `Reindex()` 要重排一批行的序号，
  自动属性不会触发绑定，画面上的 # 会停在旧值

> **为什么不用拖动**：先做过「行内拖动把手」的版本（约 180 行），实测崩过 ——
> 行模板根的父级是 `ContentPresenter` 而不是 `Panel`，`if (_dragRow.Parent is Panel)` 判空通过后
> 没摘掉父级，紧接着 `canvas.Children.Add(_dragRow)` 抛「already the logical child of another
> element」。即便修好，还要自己处理鼠标捕获、窗口外释放、拖到滚动区自动滚、150% DPI 下的坐标换算。
> ↑↓ 版约 40 行、纯数据操作、无崩溃面，且长列表与键盘可达性都更好 —— **不要因为"拖动更直觉"再改回去**。

> **表头不放「共 N 个」计数**（2026-10-31 用户定案，五处一起删）：数据采集表 / 全局变量 /
> 系统变量 / 系统变量监控 / 自由监控。理由：行数一眼可见，计数既占位置又要跟着增删维护，
> 而表头右端真正需要的是**操作按钮**（↑↓ / 删除全部 / 新增）。
> ⚠️ **行内的「共 N 项」列不是这个** —— 那是采集任务组的数据项数量（AcquisitionTaskItem.ItemCountText），
> 表示"这一组采几个点"，**必须保留**。删计数时别把它一起删了。

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
- ⚠️ **全局变量不可重复映射同一地址**（用户定案 2026-11-05：
  「全局变量不可以重复映射相同地址，在输入地址失焦后弹出对话框提示：“{地址}”已被使用」）：
  - 弹窗标题「地址重复」、正文 **`"{地址}"已被使用`**（引号是**半角双引号**，与用户原话一致；
    注意与「软元件无效」那条用**全角**引号的「“…”」不同，两者别统一，各自照抄定稿）
  - 判定在 `GlobalVariableSet.FindByAddress(address, exclude)`（纯逻辑，可离线断言）：
    比**归一化后**的 `Address`，**大小写不敏感**（`d16` 与 `D16` 是同一元件），前后空白忽略
  - ⚠️⚠️ **检查必须放在 `ResolveOnly` 之后**：`TryResolve` 会把元件符号 `ToUpperInvariant` 并写回
    `v.Address`，在它之前比就是拿未归一的原文去比已归一的旧值 —— `y27` vs `Y27` 这类**必漏判**
  - 冲突时**回退到改之前的值**并**重新解析一次**（`ResolveOnly`）：
    不回退则界面留着非法配置；不重新解析则 `ResolvedAddress` / `Area` 还停在冲突地址上，
    会「界面显示旧地址、内部按新地址读报文」
  - 空地址（内存变量）**永不参与**冲突判定：它们不落 PLC，可以同时存在多条
  - ⚠️ 这是**只在失焦时检查的界面级拦截**，不写进 `GlobalVariable` 模型校验 ——
    手工改 json 仍可造出重复地址（与「只读区只拦写不拦映射」同一层级的取舍）


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
假从站记录每一帧的 `(功能码, 起始, 数量)`，然后断言「总览页 D10 在采 / 切到设备监控页停采 / 切回来恢复」。

- ⚠️ **假从站必须绑在应用所选网卡的地址上**（如 `192.168.1.80`）：应用会把本地端点绑到那块网卡，
  绑 `127.0.0.1` 时它连不上，从站一帧都收不到 —— 表现成「代码没跑」，其实只是连不上
- ⚠️ **断言只能数 `FC3 start=10` 那一种帧**：切到设备监控页后应用**仍会**读 IO（FC1 两个块），
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

### 5.2.2 改名连带改引用（2026-11-09）

**需求**：「全局变量名称变更后，相关引用也自动变更名称」（用户原话）。

**为什么必须做**：变量名是**按名字**被引用的，散在四份 json 里。只改变量表的话，改名那一刻
所有引用就**同时失效** —— 表达式里的标识符查不到变量 → 采集组 / 扫码器整组被判「配置错误」停摆，
而用户只是改了个名字。这不是「少改一处」的程度问题，是**改名即让配置全线崩掉**。

| 引用位置 | 文件 | 字段 |
|---|---|---|
| 自由监控变量行 | `data-points.json` | `VariableName` |
| 采集的读取 / 写入变量 | `acquisition-tasks.json` | `Items[].VariableName` |
| 采集的来源变量（写入任务） | `acquisition-tasks.json` | `Items[].SourceVariableName` |
| 采集触发表达式 | `acquisition-tasks.json` | `Expression` |
| 状态机 9 个状态框表达式 | `machine-state.json` | `States[].Expression` |
| 状态机 2 个指示灯表达式 | `machine-state.json` | `Lamps[].Expression` |
| 扫码器缓冲区 / 结果变量 | `scanner.json` | `BufferVariableName` / `NotifyVariableName` |
| 扫码器触发表达式 | `scanner.json` | `TriggerExpression` |

**规则本体**：`Modeling/VariableRenamer.cs`（纯逻辑、不引 WPF，可离线断言）。
两个方法，**都不做子串替换**：

- `RenameReference(reference, oldName, newName)` —— 纯名字字段，**全等**才换（`Ordinal`）
- `TryRenameExpression(expression, oldName, newName, out result)` —— 表达式，**按标识符**换

⚠️ **表达式必须按标识符替换**：`speed` → `rate` 时，`speed2` / `my_speed` 里的 `speed` 一个字符
都不许动。故复用表达式引擎自己的 `ExpressionLexer.TryTokenize` —— 它认得标识符 / 关键字 / 数字。
⚠️ 表达式**本身有语法错误时返回 `false` 且原文不动**：那种表达式当前根本跑不起来，
硬改只会把「用户的笔误」和「我们的替换」混成一处。
⚠️ 替换**从后往前**做：前面改长度会挪动后面 token 的位置，倒序才不用记偏移补偿。

**四个集合各有一个 `RenameVariableReferences(oldName, newName)`**，各自负责自己的文件与落盘：

| 集合 | 位置 | 返回 |
|---|---|---|
| `MonitorItemSet` | `MonitorItem.cs` | 改了几行；0 不落盘 |
| `AcquisitionTaskItemSet` | `AcquisitionTaskItem.cs` | 改了几处（含表达式） |
| `MachineStateSet` | `Modeling/MachineState.cs` | 改了几条表达式 |
| `ScannerItemSet` | `MainWindow.Scanner.cs` | 改了几处 |

**接线在窗口 `MainWindow.GlobalVar.cs`**：`CommitGlobalVarName` 改名成功后、**在
`RefreshAfterVariableChange()` 之前**调 `RenameVariableReferencesEverywhere(旧名, 新名)`
（四集合 + `SyncMonitorNameBoxes`）。顺序不能反 —— 那一步会按新名字重解析监控行、
重编译表达式，引用还停在旧名字的话重算结果就是「变量不存在」。

⚠️ **状态机改写表达式后必须 `_machineSet.RecompileAll(ExpressionNameExists)`**：
`MachineStateItem.Compiled` 是**运行期缓存**，光改 `Expression` 文本不会让它跟着变 ——
不重编译就是「文本看着对、求值仍报变量不存在」。
⚠️ **自由监控的名称框是普通 `TextBox`**（`NameBox`，文本由 `MonitorRow_Loaded` 一次性写入、
刻意不绑 `Name`：绑了会边打字边推半截名字给模型）。故改完模型还要 `SyncMonitorNameBoxes()`
遍历已生成的行补文本；不可见分段没有容器，重新可见时由 `MonitorRow_Loaded` 填对。

**静默执行、不弹框**（用户 2026-11-09 定案）：改名是「失焦即提交」的高频操作，
每次都弹「同步了 N 处」只会打断连续编辑。

**验证**（`curl` 不适用，UIA + json 实测）：
- 离线断言 `.tmp/ExpressionCheck` **§23（14 项）**：全等替换 / 大小写不同不换 / `speed2` 与
  `my_speed` 不受影响 / 多处引用全换 / 短名换长名不串位 / 关键字不被当变量名 / 语法错误不硬改
- UIA 真机：改 `Scanner1_ExecuteResult` → 自由监控、采集三字段、采集表达式、
  扫码器结果变量**四处同时改名**；`Scanner2_*` 与状态机表达式**一字未动**
- 改 `SysState` → 9 条状态表达式全部同步，2 个指示灯（引用 `SysError` / `SySFault`）**未被动**
- 子串安全：`Scanner1` 改名时表达式里的 `Scanner1_Done` **完好**（只精确命中才换）
- 改名后采集页「配置错误」出现次数 **0**（没有被误判）

### 5.2.3 ARRAY 数组类型（2026-11-09）

**需求**（Brief P6.4）：「新增数组 ARRAY 类型，选择为 ARRAY 时，弹出对话框显示
数据类型（除 STRING）+ 元素数量（最大 128）」；「数组同样遵守不能重复映射同样地址的规则」。

**用户后续定案的三条**：
1. **类型显示名写 `INT[20]`**（元素类型 + 元素个数，方括号；不是 `ARRAY<INT>` 那种尖括号写法）——
   与 `STRING[9]` 同一套写法，一眼看出里面是几个
2. **不做整组 `[1, 2, 3]` 展示**：数组项占一行，展开出 N 个元素子行，每行独立
3. **只能对元素写**：整组写入被拒

| 文件 | 职责 |
|---|---|
| `Modeling/DataTypeEnums.cs` | `DataType.Array = 7`（**追加末尾**）+ `DisplayFormat`。**零依赖**，单独成文件的原因见下 |
| `Modeling/ArrayRules.cs` | 声明规则（元素类型合法 + 个数 1~128）。**零依赖**，地址校验也要用 |
| `Modeling/ArrayValueCodec.cs` | 元素寻址 / 切片 / 格式化 / 编码 |
| `Ui/ArrayDeclareDialog.cs` | 「数组设置」对话框（元素类型下拉 + 元素数量） |
| `MonitorItem.cs` | `MonitorElement` 子行模型 + `ApplyArrayRegisterBlock` / `ApplyArrayCoilBlock` + `TryBuildElementWrite` |
| `MainWindow.xaml` | `MonitorItemTemplate` 外层套两行 Grid：第 0 行普通行、第 1 行元素子行 |

**必须守住的**：

- ⚠️ **数组占用量 = 元素个数 × 单元素单元数**，**不能**套 `RegistersForLength`
  —— 那个是按 STRING 的「每寄存器 2 字符」做的 ⌈n/2⌉，3 个 INT 会被算成占 1 个（实为 3）。
  入口是 `DataTypes.ArrayUnitsOf` / `DataTypeSpec.UnitsFor`。
- ⚠️ **存储区按元素类型判**：`ARRAY<BOOL>` 占 N 个线圈、`ARRAY<INT>` 占 N 个寄存器。
  `DataTypeSpec.CoilOnly` 对数组**不足以判断**，一律走 `IsCoilOnlyFor(elementType)`。
  漏了这条，`ARRAY<BOOL>` 会被当寄存器类型去校验，报「元件是线圈元件，不能声明为 ARRAY」。
- ⚠️ **元素偏移 = 下标 × 单元素单元数**（`ArrayValueCodec.OffsetOf`）。
  这依赖「元素定长」，也正是**元素不能是 STRING** 的原因。
- ⚠️ **子行不进 `data-points.json`**：它们是 `Type=Array` + `ElementType` + `Length` 的推导结果，
  落盘会产生两份真值。
- ⚠️ **子行可见性绑 `IsExpanded`，不绑派生属性**：曾绑 `ElementsVisibility`（= `IsArray && IsExpanded`），
  结果点箭头后行高纹丝不动、元素一个都不出来 —— 而诊断日志明明显示 `展开=True 元素数=3`。
  绑定派生属性要求每个中间状态都发 `PropertyChanged`，多一层中转就多一处可漏；直接绑源头最稳。
- ⚠️ **容器重建时要 `RebuildElements()`**：`Elements` 是运行期集合，只有类型切换那条路径建过它。
  漏了的表现是「重启后数组行看着正常、点展开什么都没有」。
- ⚠️ **`RebuildElements` 不能只比个数**：首次载入时 `Elements` 是空集合而 `count` 可能是 3，
  只比个数会直接 return。判据要连元素下标一起比。
- ⚠️ **`DataTypeEnums.cs` 与 `ArrayRules.cs` 刻意拆开**：枚举零依赖，而 `DataTypes.cs` 的规格表要用
  驱动层常量。拆开后 `ArrayRules` 才能被最小断言工程单独链接（否则 `ExpressionCheck` 编不过）。
- ⚠️⚠️ **凡是「从变量表同步定义」的地方，`ElementType` 必须一起同步**（2026-11-10 用户报的 bug）。
  `MonitorItem.Resolve` 有两条路径（有映射地址 / 内存变量），都只写了 `Model.Type` / `Format` / `Length`，
  漏了 `Model.ElementType` → `DataPoint.ElementType` 停默认值 `Int` → **`BOOL[80]` 的变量行显示成 `INT[80]`**。
  症状是「明明是 BOOL 数组，监控行显示 INT」，而 json 里存的确实是对的 —— 因为错的是**同步**那一步。
- ⚠️ **存储区归属必须按元素类型判**，不能用 `spec.CoilOnly`（ARRAY 恒 false）：
  `SyncMonitorTypeWithElement` 曾用它，把 `BOOL[4]` 映射到线圈元件 `M100` 时判成「类型不符」
  并**自动改成 INT** —— 用户的 BOOL 数组当场丢失。一律走 `DataTypeSpec.IsCoilOnlyFor(elementType)`。
- ⚠️ **软元件（裸地址）不能用 ARRAY**（2026-11-10 用户定案）：「不想让软元件以 ARRAY 类型显示」。
  监控表的一行对应**一个元件地址**、「当前值」格就该有一个确定的值；而数组占一段连续单元 ——
  两个前提冲突。故监控表的候选类型是 `DataTypes.MonitorRegisterTypes`（= `RegisterTypes` **去掉 ARRAY**），
  与全局变量页的 `RegisterTypes`（**仍含 ARRAY**）**刻意不同**。
  ⚠️ 不要为了「统一」把数组从全局变量也删掉 —— 那样 ARRAY 就彻底没入口了。
  `ApplyTypeChangeWithPrompt` 里另有一道兜底：万一有其他路径走到 ARRAY，弹框说明并退回原类型。
- ⚠️ **数组行的类型框与格式框都置灰**（用户 2026-11-10 要求）：元素类型/个数只能在「数组设置」里配。
  要改就先把类型切走再切回 ARRAY（会重弹对话框）。**故意不做「置灰但仍可点开」** ——
  `IsEnabled=false` 在 WPF 里同时挡掉点击，要那样得换按钮或改模板，代价大于收益。
- ⚠️⚠️ **模板里给文字写死画刷颜色 = 禁用态没有任何视觉变化**（2026-11-10 用户报的 bug）：
  `MonitorTypeCombo` 的 `TextBlock.Foreground` 原先写死 `Brush.Text.Primary`，
  于是 `IsEnabled=false` 只是「点不动」，看起来与可编辑时一模一样。
  **正确做法照抄 `FormComboBox`**：模板里的文字/chevron 用 `{TemplateBinding Foreground}`，
  Style 里给默认值 + 一条 `<Trigger Property="IsEnabled" Value="False">
  <Setter Property="Foreground" Value="{DynamicResource Brush.Text.Disabled}"/></Trigger>`。
  ⚠️ 只设 `ComboBox.Foreground` 而模板里是硬编码画刷 / `ContentPresenter` 不理它时，那行 Setter 是**无效的**。
- ⚠️ **拆枚举的连带影响**：所有链接 `DataTypes.cs` 的断言工程都要**同时**加 `DataTypeEnums.cs`
  （`.tmp` 下 6 个 + `ModelingCheck` + `ModelingCheck/live` + `OrderProbe`）；
  链接 `DataPoint.cs` / `GlobalVariable.cs` 的还要加 `ArrayRules.cs`；碰编解码的才加 `ArrayValueCodec.cs`。
  **`DriverSwitchCheck` 只做地址校验，只需要 `ArrayRules`** —— 给它链 `ArrayValueCodec` 会连带拉进
  `ByteOrderSettings` 而编译失败。

**地址重复的处理**（数组引入后升级）：`GlobalVariableSet.FindByAddressOverlap` 比**区间**而非全等。
⚠️ 冲突提示要区分两种情形 —— 同起点报「"D11"已被使用」；区间重叠要说清是被**哪条变量**占的
（只说「D11 已被使用」，用户会去变量表里找一条叫 D11 的，而实际占着它的是 D10 起的数组）。

**验证**：
- `.tmp/ExpressionCheck` **§24（12 项）**：`ArrayRules` 的元素类型候选 / 数量边界 / 拒绝原因文案 / 枚举值 = 7
- `ModelingCheck` **§ARRAY（33 项）**：占用量四种元素类型 / 不按 ⌈n/2⌉ 算 / 偏移 / 切片越界不读邻居 /
  线圈元素 / 元素名 / 元素编码定长 / BOOL 拒绝按寄存器编码 / 显示名 `INT[20]`·`BOOL[80]` /
  存储区按元素类型判 / 变量行元素类型同步（含「漏同步会显示成 INT[80]」的反例）
- 合计 **403 项断言通过**（176 + 227）
- UIA 实测：类型下拉里有 ARRAY → 弹出「数组设置」→ 确认后 `data-points.json` 落成
  `{"Type":"Array","Length":3,"ElementType":"Int"}` → 点箭头行高 48→230、出现 `D200[0..2]` 三个可写子行
- **变量行实测（2026-11-10）**：`var1` 声明为 `ARRAY<BOOL>[80]` → 监控行显示 **`BOOL[80]`**、
  类型框与格式框均 `IsEnabled=False`

**数组只认元素（2026-11-10 用户定案，**所有引用框通用**）**：

- **规则**：数组变量 `var1` **不能直接引用**，必须写成 `var1[0]`（下标 0 基、范围 `0 ~ Length-1`）。
  适用于：表达式框（状态机 / 采集触发 / 扫码器触发）、采集的读取 / 写入 / 来源变量、
  执行完成输出变量、扫码器接收缓冲区 / 执行结果输出变量、实时卡片、CSV 取值。
- **唯一实现**：`Modeling/VariableName.cs`（**零依赖**，只拆写法：`var1` / `var1[0]` / 报错）
  + `Modeling/VariableReference.cs`（查变量表：存在性 / 是否数组 / 下标范围 / **变量自身 `ResolveError`** / `Describe` / `ReadValue`）。
  ⚠️ **不要在别处再写一遍 `[` 的解析**：改名、表达式词法、CSV、配置检查、扫码器全部走这两个类。
- ⚠️ **引用校验要连带查变量自身的配置**（2026-11-11）：`ResolveError` 非空（地址解析失败）或数组声明非法时，
  `GlobalReadPlan.Build` 会把变量整个过滤掉、一个报文都不发。若界面仍报「配置完整」，
  用户会以为在采 —— 故 `VariableReference.TryResolve` 先把这两类拦住。
- ⚠️ **`var1 [0]`（下标前有空格）一律无效**：表达式词法器要求 `[` 紧跟标识符，
  变量框若放行，同一种写法会在两个入口得到不同结论。
- **表达式里的 `var1[0]` 是一个标识符**：`ExpressionLexer` 把 `[数字]` 直接并入变量名，
  `VariableNode.Name` / `CompiledExpression.UsedNames` 存的就是 `"var1[0]"`，
  故求值/校验接口仍是 `string`，不必给语法树加下标节点。
  ⚠️ 词法器对 `var1[]` / `var1[a]` / `var1[0` 各有专门报错，不要退回「无法识别的字符」。
- **校验函数返回原因而不是 bool**：`CompiledExpression.TryValidateNames(Func<string,string?>, out error)`
  （旧的 `Func<string,bool>` 重载只给「变量不存在」，**认不出数组**，仅为兼容断言工程保留）。
  `MachineStateSet` / 两个状态机编辑对话框 / 采集任务对话框 / 扫码器全部改用它；
  统一的名字校验出口是 `VariableReference.Describe(name, findGlobal, allowWholeArray: false)`。
- ⚠️ **表达式还多一道「结果必须是 BOOL」的静态校验**（2026-11-11，方案 1）：
  `CompiledExpression.TryValidateResultType(Func<string, ExpressionVariableType>, out error)`。
  名字校验**认不出类型**，故 `INT var` 单独当条件（`var`）原先语法、名字全过，存进 json 后
  运行期每拍求值失败 —— 状态框永远不亮、触发式任务与扫码器永远不触发，错误还被 `out _` 吞掉。
  - **文案统一是一句 `表达式不合法`**（`CompiledExpression.NotBoolean`，2026-11-11 用户定案）：
    **不带位置、不带变量名、不带类型、不给改法示例**。原稿是「表达式的结果必须是 BOOL，
    但「SysState」是 INT 变量（可写成…）」，在对话框里折成两行、把按钮区顶下去。
    红字就贴在出错的那个框下面，用户已经知道是哪个框。⚠️ 改文案前先看断言 §26 第 ⑨ 条
    （它守的就是「就是这一句、不带位置前缀」）。
  - **推断规则**（`ExpressionNode.InferKind`，纯逻辑、无新源文件）：`BOOL` → Bool；
    四个整数与 `REAL` → Number；`STRING` → **在 `VariableNode` 处直接报错**（表达式里无合法用法）；
    比较运算恒 Bool；`&&`/`||`/`!` 两侧必须是 Bool。四处报错**共用同一个常量**，不要各写一份。
  - ⚠️ **`Unknown` 一律放行**：变量不存在时类型也推不出来，那条由 `TryValidateNames` 报。
    两处都报会给出互斥说法。
  - ⚠️ **顺序必须是 语法 → 名字 → 结果类型**：名字查不到时先报「不存在」才指向正确改法。
  - 类型查询出口 `VariableReference.DescribeType(name, findGlobal)`：复用同一套拆写法
    （`var1[0]` 取**元素类型**），查不到/越界一律回 `UnknownType`（放行，不报错）。
  - **接线点**（`typeOf` 为 `null` 时跳过，行为与改动前一致 —— 断言工程靠这条保持原状）：
    `MachineStateSet.Load` / `RecompileAll` / `TrySetExpression`（都加了可选参数）、
    两个状态机编辑对话框（`Ask(..., typeOf:)`）、`AcquisitionTaskCheck.Describe`、
    采集任务对话框、扫码器的配置校验与失焦提交。
  - ⚠️ **全局变量换类型后必须重编译**：`RefreshAfterVariableChange` 里补了
    `_machineSet.RecompileAll` —— `flag` 从 BOOL 改成 INT 后，原来合法的 `flag` 就非法了。
  - ⚠️ **`VariableReference.Describe` 的文案没跟着改**：它同时给采集的读取 / 写入 / 来源变量框
    与扫码器缓冲区用，那些**不是表达式**，报「表达式不合法」会指错方向。两套文案分开维护。
  - 断言：`.tmp/ExpressionCheck` §26（**211 项**）。
- **读**：`GlobalReadPlan.Apply` 遇到 `Type == Array` 时按元素切片回填
  `GlobalVariable.SetElementValue(i, text, numeric)`（线圈数组走 `TryGetCoilElement`）。
  **数组不写 `Value`**（整条不可引用），元素值/数值形态各存一份。
- **写**：`AcquisitionWrite.TryBuild(..., int? elementIndex, ...)`：地址 = 基址 + `ArrayValueCodec.OffsetOf`，
  编码用**元素类型**，元素一律 `DisplayFormat.Dec`；`ApplyToMemory(..., elementIndex)` 只回填该元素。
  整条数组 / 非数组带下标 → `Failed` 且文案说清。
- **改名**：`VariableRenamer.RenameReference` / `TryRenameExpression` 只换**基础名**、保留 `[下标]`。
  例：`var1[0] == 1 && var10 == 1` → `v2[0] == 1 && var10 == 1`。
- **补全**：数组变量的候选给 `var1[0]` 作样板，`Detail` 写「数组，请填元素下标（0 ~ N-1）」。
  - **候选行显示四栏**（2026-11-11 用户定案）：**变量名 · 数据类型 · 注释 · 来源**。
    字段分别是 `CompletionCandidate.Text` / `TypeName` / `Comment` / `Kind`。
    - ⚠️ **`Detail` 不上屏**：它是「类型 + 数组下标提示 + 注释」合并成的一串，既长又把行挤乱，
      数组那句提示在自由监控框里还是错的。`ModelingCheck` 仍有断言在校 `Detail`，**字段别删**。
    - ⚠️ **列宽四列全 `Auto` + 全进 `SharedSizeGroup`**：`Auto` 让宽度按内容走，
      共享组让「没有注释的那行也占住注释列」—— 少了共享组，空注释行的右侧列会**左移**
      （实测症状：`SysError` / `SySFault` 的「全局变量」比 `SysState` 那行靠左，看着没对齐）。
    - ⚠️ **不要用 `*` 按比例分宽度**（曾试 1:1 与 2:1）：零和的，拉宽一边必然压窄另一边。
    - ⚠️ **不要在 `Show()` 里手工 `Measure` 再赋 `Width`**：共享尺寸要等整个 `ListBox`
      布局过一遍才成立，手工量只算到**第一行**，固化下来就是实测的「右侧被截成『全局3』」。
      正解是 `Width = NaN` + `MinWidth`(240) / `MaxWidth`(560，屏幕宽 ×0.8 取小)。
  - ⚠️ **同一个 `ForMonitorName` 被两类语义相反的框共用，靠档位区分**（2026-11-11 已修）：
    - 自由监控「元件或变量名称」→ **`WholeArrayGlobal`**：数组给**整条名字** `var1`
      （`SetName` 按整名查变量表，数组在本页是「占一行、展开成 N 个元素子行」）；
    - 采集的读取 / 写入变量、扫码器结果变量 → **`AnyGlobal`**：数组给**元素** `var1[0]`。
    - ⚠️ 修之前两边共用默认档，自由监控的补全会给出 `var1[0]` —— 填进去查不到变量、
      退回元件解析直接报错，**补全给了一个填了必然错的写法**。
    - 今后给 `ForMonitorName` 加调用点，**必须显式写出档位**，别再依赖默认值。
  - 用户要别的下标**自己改数字**（不自动定位光标到方括号内）。
- **扫码器缓冲区是例外**：接收内容输出变量**只能是 STRING**（用户 2026-11-10 补充），
  而数组元素不能是 STRING（`ArrayRules` 禁止），所以缓冲区**拒绝元素写法**、
  仍只接受整条 STRING 变量；执行结果输出变量可以是整数元素。

### 5.3 系统变量（P3.5，数据管理页第三分段）

摆出一份**写死、只读**的预定义变量清单（17 项）。
表列：**序号 / 变量名称 / 数据类型 / 注释** —— 比全局变量少了**映射地址**与**删除**两列。

- 清单在 `Modeling/SystemVariable.cs` 的 `SystemVariable.All`：**逐项写死、不落盘、不可增删改**
- 前 6 项 `BOOL`（`sys_True` / `sys_False` / **四个**周期时钟：100ms、1s、2s、1min），后 11 项 `INT`（7 个系统时钟 + 4 个运行时长）
- **每格都是 `TextBlock`**：没有输入框、没有下拉框，连焦点都拿不到 ——
  只读性由**控件类型**表达，不靠 `IsReadOnly`（后者仍能选中、能弹输入法）
- 数据类型显示名走 `DataTypes.Get(Type).DisplayName`（`BOOL` / `INT`），**不另写字面量**
- 表头右侧**什么都不放**（原「共 N 个」计数已于 2026-10-31 按用户要求删除），也没有 ＋ / 删除全部按钮
- 列宽 34 / 140 / 106 / `*`，行高 30，与全局变量逐值一致；
  ⚠️ 表头与 `SystemVarItemTemplate` 的 `ColumnDefinition` **必须同步改**，否则表头与内容错位
- `sys_DateTime_*` 是 `DateTime`（不是 ~~`DataTime`~~，需求文档原稿的笔误，已定案更正）
- ⚠️ **不要给它加排序 / 搜索 / 编辑**：清单是对外约定，改动等于改协议 ——
  先改 `Brief.md` P3.5，再改代码与断言

**取值**（`Modeling/SystemVariableValues.cs`，纯函数 `Capture(now, uptime)`）：

- **两个时间源是故意分开的**：`sys_DateTime_*` 用墙上时间（`DateTime.Now`，用户改系统时间它就该跟着变）；
  `sys_RunTime_*` 与 `sys_Pulse*` 用**单调时长**（`Stopwatch`，系统校时不能让运行时长跳变、脉冲抖一下）
- `BOOL` 显示 `"1"` / `"0"`（与界面里 BOOL 的 `BIN` 一致），不写 `True` / `False`
- 脉冲：`Pulse100ms` 50ms 翻转、`Pulse1s` 500ms、`Pulse2s` 1000ms、`Pulse1min` 30000ms；**启动瞬间为 ON**（`ms / 半周期` 偶数即 ON）。⚠️ 新增脉冲一律走**毫秒**（`ms / 半周期`），不要换成「秒 × N」—— 少一处换算就少一处出错机会
- 星期 **1~7**（周一=1、周日=7），不是 .NET 原生的 0~6 —— `IsoWeekday` 负责换
- 运行时长起点 = **程序启动**；秒/分/时各自取模（0~59 / 0~59 / 0~23），**日累加不取模**
- 负数 `uptime` 按 0 处理
- ⚠️ **取值按 `SystemVariableKind` 分派，不是按变量名**：名字改过一次（`sys_true` → `sys_True`），
  行为不该跟着名字变；按名字写 `switch` 的话每次改名都得同步改一处看不见的取值逻辑
- ⚠️ `switch` **不写 `default`**，并用 `#pragma warning disable CS8524` 只压「未命名枚举值」那条噪声告警 ——
  **CS8509 必须留着**：新增一个 `Kind` 却忘了加分支时，它才是那个哨兵

**离线断言**：`.tmp/SystemVarCheck`（**135 项**，未提交），改 `SystemVariable.cs` / `SystemVariableValues.cs` 后跑它；跑前先 `$env:TEMP = "$PWD\.tmp\svc-temp"`。
断言覆盖：17 项规模 / 序号 1..17 / 名称唯一且符合 `GlobalVariableRules` / 逐字名称表 / 类型与显示名 / 注释非空 /
常量 / 四种脉冲的相位边界（含 `Pulse1min` 的 0·29999·30000·59999·60000·90000ms）/ 星期 1~7 连续七天 / 零点与年末边界 / 秒分时日的进位 /
负数时长 / 脉冲与运行时长不受墙上时间影响 / 键集合与清单一致 / Kind 无重复且覆盖全部枚举值。

### 5.4 系统变量监控（P2.3，设备监控页第四分段）

设备监控页第四分段（`SysVarViewSegment`），把 `SystemVariable.All` 那 17 项**连值一起**摆出来：
**序号 / 变量名称 / 数据类型 / 当前值 / 注释**。与 P3.5 的定义表相比只多一列「当前值」，其余几何完全一致。

- 行对象 `SystemVarRow`（`SystemVarRow.cs`）：纯展示 —— 只有 `Value` 会变，名称/类型/注释钉在清单上
- 值来自 `SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed)`（见 §5.3）
- **定时器 50ms、`DispatcherPriority.Background`**，只在「本分段可见 **且** 设备监控页可见」时跑（`SyncSystemVarTimer`）
  - ⚠️ **为什么是 50ms 而不是 100ms**：`sys_Pulse100ms` 的半周期就是 50ms，每 100ms 采一次，
    采样点永远落在「50ms 的整数倍、步长 2」那一列 —— 相位固定，读出来恒为同一个值，看上去**卡住不闪**。
    50ms 一拍刚好每半周期采到一次
  - ⚠️ **为什么必须是 Background 优先级**：用 `DispatcherPriority.Render` 时每拍都会牵动渲染调度
    （实测同一刷新频率下 7.6% → 1% 单核）。这只是个「看一眼」的页面，不该抢渲染优先级
  - 同值赋值由 `SystemVarRow.Value` 的 setter 挡掉，不产生绑定通知
- `_runClock` 是 `Stopwatch`，**不是** `DateTime.Now - 启动时刻`：系统校时会让后者跳变
- 表头右侧**什么都不放**（计数已删），也没有 ＋ / 删除按钮（清单写死）
- **自由监控不许添加系统变量**（P2.3 需求）：
  - 界面入口在 `CommitMonitorName`：输入框失焦/回车时先查 `SystemVariable.Find`，
    命中就回退并弹「系统变量」标题的窗（正文 `“sys_True”是系统变量，不能在自由监控中使用。`）
  - 为什么必须单独拦：系统变量不在全局变量表里，不拦会一路掉到元件解析，
    报成「不是有效软元件或变量」—— **原因不对**，用户会去改元件名
  - 手工改过的 `data-points.json` 里若留着系统变量名，`ResolveVariable` 也给了同一句错误文案
  - `SystemVariable.Find` **区分大小写**（与变量名规则一致）：`sys_true` 不命中 `sys_True`

**离线断言**：`.tmp/SystemVarCheck`（**135 项**）覆盖取值语义与 `SystemVarRow` 的通知行为（同值不通知、整表推送后与 `Capture` 一致）。

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
- 下拉框直接用 `FormComboBox`（数据管理 / 设备连接页同一套：高 26、圆角 6、灰底）。
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

**「当前视图」会记住上次选择**（2026-11-09 用户要求：下次打开回到上次那个视图）：

- 落 `app-settings.json` → `OverviewView`，值取 **枚举名**（`MachineState` / `ProductionData` / `Acquisition`）。
  枚举与换算在 `Modeling/OverviewViewSetting.cs`（**纯逻辑**，`ToIndex` / `FromIndex` / `TryParse` / `Format`）
- 回填在 `Window_Loaded` 的 `RestoreOverviewView()`，**必须排在状态机/采集定时器那次 `Sync` 之前** ——
  回填会触发 `SelectionChanged`，而那里末尾要把两个定时器按新视图重新同步
- 落盘在 `OverviewView_SelectionChanged` **末尾**，且罩在 `_restoringOverviewView` 闸门里
  （与 `_restoringNicSelection` / `_syncingByteOrderCombos` 同一手法）：程序化回填不该被当成「用户刚改」再写一次盘
- 存的是第 0 项时 WPF **不会** raise `SelectionChanged`（值没变），此时界面可见性由 XAML 默认值保证
- ⚠️ **`TryParse` 只认枚举名，不能只写 `Enum.TryParse`**：它会把纯数字串也解析成功
  （`"2"` → `Acquisition`），而 `Enum.IsDefined` 拦不住（2 本来就等于有名字的成员）。
  端到端实测抓到的：喂 `"2"` 时界面跳到了数据采集。现在按 `Enum.GetNames` 逐个比名字，
  认不出的值一律退回 `MachineState`，**且不改写 json**（用户手改的痕迹留着，便于排查）
- ⚠️ **`Count` 常量与 XAML 的项数必须同步**：`OverviewViewSetting.Count = 3` 是下标边界的唯一来源
- 端到端验证：`.tmp/OverviewViewCheck/run.ps1`（UIA，六条：旧 json 无字段 / 切换落盘 / 重启恢复 ×2 /
  非法值回退 / 数字串回退）。⚠️ 定位控件用 `AutomationId="OverviewViewCombo"`（= `x:Name`）——
  **折叠态下 WPF 不生成 ListItem**，靠「找含『PLC状态机』的 ComboBox」是找不到的；
  读选中项必须先 `Expand()` 再枚举 `IsSelected`
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
9 个状态框 + **14 条**转移连线 + 灰底容器 + **2 个指示灯**（Error / Fault，见本节末尾）。坐标表在 `MachineStateLayout`（`Modeling/MachineState.cs`）里，
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
- 除颜色旁标出的当前色值外，**不写任何提示小字**（见 `AGENTS.md` §9.1 的通用约定）
- 字号下拉若当前值不在预设里会**插进去**，不会悄悄改成别的值
- 确定后写回模型 → `MachineStateSet.Save()` → 刷新画布

**指示灯 Error / Fault**（2026-10-28，用户「风格直接复用 IO 监控里的」）：

- 位置写死在 `MachineStateLayout.Lamps`：`Error` = (600, 368, 100, 30)、`Fault` = (700, 368, 100, 30)，
  落在 ManualControl 右边（489）与 Aborted 左边（879）之间的空白带。
  （2026-10-28 用户画红线后从「居中 526/692 + 宽 150」→ 红线标定的 587/754 + 宽 120 → 再收到现在这组。
  换算与取证见 `Design.md` §10.33；**以代码里 600/700/100 为准**，`MachineState.cs` 的注释也这么写）
  - **宽度 100 不是 120/150**，两个理由：① 灯的可点区域就是这个 Border，150 会让 Fault 右边缘逼近
    Aborted(879)，双击 Aborted 会弹出灯的编辑窗；② 两灯左边缘相距 100，**宽度 100 才恰好相邻不重叠** ——
    宽 120 会重叠 20 px，那 20 px 里双击算哪个灯取决于 z 序（Fault 后建、在上），是说不清的行为

- 视觉照抄 IO 监控那颗圆点：`Border` 直径 × 直径、`CornerRadius = 直径/2`，
  **亮 = 模型色、灭 = `Io.Off`**（灭态复用主题键，没有新键）；文字走 `Brush.Text.Primary`
- 模型是 `MachineStateLamp`（Id / Text / Expression / IsOn / **Color / DotSize / FontSize**），`Expression` 空串恒为假
  - 出厂：`DotSize = MachineStateLayout.LampDotSize`（14）、`FontSize = LampFontSize`（14）、`Color = MachineStateLayout.LampOnColor`（`#0B7A0B`）
  - `Color` **永远是一个具体的 `#RRGGBB`**，**没有「跟随主题」这一档**（用户 2026-10-28 明确要求删掉）：
    用户改过的颜色换主题就变，会让人以为设置没生效。所以 `LampOnColor` 是**常量**而不是资源引用
  - 夹取：`DotSize` 6..28（上限 28 因为这些灯格高只有 30）、`FontSize` 8..32
  - **没有 FontStyle**：灯永远常规体（一个点的文字加粗没意义）
  - `MachineStateLamp.NormalizeColor` 认不出时退回 `#0B7A0B`，`MachineStateItem.NormalizeColor` 退回 `#F8CECC`
    —— 同一取舍（认不出就给个能画的颜色），只是退回的色不同
- 双击弹 `Ui/MachineLampEditDialog.cs`（**五项**：显示文字 / 文字大小 / 灯大小 / 灯颜色 / 表达式），校验口径与状态框完全一致
  - 色盘就是状态框那 30 个 drawio 原色，10 列 × 3 行，**没有第 4 行**（原来的「跟随主题」格已删）
  - 预览条**永远画点亮态** —— 颜色只在点亮时可见，预览灭态等于永远看不出颜色
- ⚠️ `LampVisual` 的「上次尺寸」哨兵**不能初始化为 `double.NaN`**：
  `Math.Abs(NaN - x) > 0.01` 恒为 `false`，灯会永远停在 `BuildLamps` 的初值（实测踩过：
  把灯大小 22 改成 24，json 写对了、画布纹丝不动）。用 `-1`（尺寸永远为正）
- 落盘在 `machine-state.json` 的 `Lamps` 段，按 `Id` 合并出厂值；**老文件没有这一段也能读**
- ⚠️ `Bind` 里 `BuildLamps` 必须放在 `BuildBoxes` **之后**（z 序 = 添加顺序，灯要压在连线标签之上）
- ⚠️ 灯的 host `Border` 必须给 `Background = Brushes.Transparent`：
  **`Background` 为 null 的 Border 不参与命中测试**，双击会直接穿过去打不开编辑窗
- ⚠️ 灯的表达式引用的全局变量要在 `RefreshMachineGlobals()` 里一起收（它现在对
  `_machineSet.Items` 与 `_machineSet.Lamps` **各调一次** `Collect`），否则灯永远不亮

**离线断言**：`.tmp/ExpressionCheck` **143 项**（表达式 + 状态机模型 + 指示灯 + 灯外观及其持久化）；
系统变量名保留另在 `.tmp/SystemVarCheck` **135 项**里覆盖。

### 5.6 变量名补全（P6.5，三处输入框共用一套）

打字即弹候选列表，像 IDE 那样联想变量名。**只联想变量名，不联想通讯地址**（用户定案）。

| 位置 | 挂载点 | 候选来源 |
|---|---|---|
| 自由监控「元件名称」框 | `MonitorName_Loaded`（模板里每行一个，走 `ConditionalWeakTable` 防重复挂载） | `ForMonitorName(..., WholeArrayGlobal)` → **全局变量**；⚠️ **数组给整条名 `var1`** |
| 状态机编辑窗「表达式」框 | `MachineStateEditDialog` 构造时 | `CompletionSource.ForExpression` → **系统变量 + 全局变量**；数组给元素 |
| 采集数据「变量名称」框 | `AcquisitionItemVariableBox_Loaded`（同样每行一个，共用 `_nameCompletions` 弱表去重） | `ForMonitorName(..., AnyGlobal)` → **全局变量**；⚠️ **数组给元素 `var1[0]`** |
| 采集「来源变量」框 | `AcquisitionSourceBox_Loaded` | `ForSourceVariable` → 系统变量 + 全局变量 + 常量示例 |
| 扫码器 缓冲区 / 结果变量 | `ScannerBufferVariableBox_Loaded` / `ScannerNotifyVariableBox_Loaded` | `ForMonitorName(..., StringScalarOnly / IntegerScalarOrElement)` |

> ⚠️ **数组的两种写法取决于框要整条还是元素**（2026-11-11）：自由监控那一栏按**整名**
> 查变量表（数组是「占一行、展开成 N 个元素子行」），其余框只要**元素**。
> 新增 `ForMonitorName` 调用点**必须显式写档位**，别依赖默认值 —— 2026-11-11 之前
> 就是因为共用默认档，自由监控的补全给出了 `var1[0]` 这种填了必然报错的写法。

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

### 5.7 采集数据（P3.1，数据管理页第一分段）

任务组与数据项可以增删改、落盘到 `acquisition-tasks.json`；
**采集执行与 CSV 落盘已接**（2026-10-26，见本节末尾「执行与落盘」）。
**写入下发也已接**（2026-10-27，见本节末尾「写入任务真正下发」）。

结构是**两层**：任务组（`AcquisitionTask`）下面挂数据项（`AcquisitionItem`）。

| 列 | 内容 | 可编辑 |
|---|---|---|
| 任务名称 | `任务组N`（默认名，重名会被拒） | 走「…」对话框 |
| 任务类型 | 数据读取 / 数据写入 / HTTP数据上报（默认**数据读取**；上报见 §5.7.3） | 走「…」对话框 |
| 执行方式 | `周期` / `触发`（**只有两个字**，用户定案 2026-10-29；秒数与上升沿语义在「…」对话框里看） | 走「…」对话框 |
| 启用 | `AcqEnableSwitch` | 行内开关，即时落盘 |
| # | 序号（1 起，**不落盘**） | 只读 |
| 日志记录 | 任务组活动记不记运行日志（**默认开**；与下面「数据记录」是两件事） | 对话框 |
| 数据记录 | 记录到文件的开关（与行内「启用」是**两件事**，默认关） | 对话框 |
| 执行完成输出变量 | 跑完写 1（正常）/ 2（异常）的全局变量（**留空 = 不写**；只认 INT/UINT/DINT/UDINT） | 对话框 |

> 任务选项对话框的**九个**表单行是**固定行高 40**（不是自动高度 + 下边距），
> 第九行「执行完成输出变量」下面还有一行说明小字（**Auto 行**，不与 40 高的表单行混排）。
> 详见 `AGENTS.md` §9.3 第 8 条 —— 改行距/加行前先读。
> ⚠️ 标签列宽 **112**（不再是 68）：「执行完成输出变量」8 个字，68 会硬裁断；
> 对话框宽度同步从 442 → 500，否则右列被压得放不下按钮行。
> ⚠️ **行高会按任务类型动态归零**（2026-11-24）：任务类型选「HTTP数据上报」时，
> 「数据记录」「保存路径」「实时背景图」三组**整行隐藏** —— `SyncKindRows` 把这三行的
> `RowDefinition.Height` 在 `FormRowHeight`(40) 与 `0` 之间切，同时切 label 与内容控件的
> `Visibility`。**只设 Visibility 不够**：固定行高的行会留下 40 高的空洞。
> ⚠️ **必须跟着下拉实时切**，不能在构造时判一次就完 —— 任务类型下拉就在本对话框里，
> 用户改成上报的当场就该看到三行消失（2026-11-24 修：原先只在构造时判 `task.Kind`，
> 于是「打开后改成上报」三行不消失，即用户截图那个 bug）。
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
  写入 5 列（#/项名称/**来源变量**/写入变量/数据类型），
  上报 4 列（#/**字段名称**/来源变量/数据类型，无「读取/写入变量」列）。
  ⚠️ 来源列与「读取/写入变量」列都**不是**用 `Visibility` 藏的 —— 列宽由
  `ColumnDefinition.Style` 的 DataTrigger 在 0 与 150/180 之间切；
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
- **手动排序：子表表头 ↑↓ 作用于选中的【数据项】**（2026-11-11，口径照搬全局变量表）。
  ⚠️ 排的是**任务组展开后的数据项**，不是任务组本身 —— 任务组的序不影响任何行为，
  而数据项的序**决定 CSV 的列序**。三层缺一不可：
  - `AcquisitionItemRow.IsSelected`（界面态、不落盘）+ `AcquisitionItemRowTemplate` 里
    `<Border.Style>` 的 `DataTrigger` 绑它换底色 `Brush.Accent.Subtle`，点击走
    `AcquisitionItemRow_PreviewMouseLeftButtonDown` → `SelectAcquisitionItem`；
  - `AcquisitionTaskItem.MoveItem`：**同时**改界面的 `Items`（ObservableCollection）与
    模型的 `Model.Items`，然后 `ReindexItems`。⚠️ 只改一边就是「画面动了重启变回去」
    或「存对了但界面不刷」；不重排 # 号则出现跳号；
  - `UpdateAcquisitionItemMoveButtons` / `UpdateMoveButtonsForTask`：没选中 → 两个都灰；
    首项 → ↑ 灰；末项 → ↓ 灰。**置灰与点击拦截必须同源**。
  - ⚠️ **按钮在 `DataTemplate` 里**（每个展开的任务组各有一对），**没有窗口级字段**：
    只能遍历已生成的行容器、按 `x:Name` 在视觉树里找（`FindByName`）。
    故 `MoveAcquisitionItemUpButton` 这类名字**不能**在 code-behind 里直接引用。
  - ⚠️ **XAML 里必须写 `IsEnabled="False"` 作默认值**：`RefreshAcquisitionView` 在初始化期
    跑时容器还没生成、找不到按钮，不写默认值就会「一行没选、两个箭头都亮着」（实测踩过）。
  - ⚠️ **展开的那一刻容器还没建好**，`ToggleAcquisitionExpand` 里要用
    `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)` 排到布局之后再刷一次。
  - ⚠️ **移完要 `Save()` + `RefreshAcquisitionView()`**：列序变了，总览的实时 / 历史要按新列序重读。
  - ⚠️ **当天已写过 CSV 时改序 → 换新文件**（`2026-11-11.csv` → `2026-11-11_2.csv`）：
    表头签名变了就换文件，这是既有设计（防列错位）。后果是**历史页签只显示序号最大的那个**，
    改序前那半天的行在 `_1` 里、界面上看不到。跨天会自然恢复。见 Brief P3.1 / P1.4。
  - 与扫码器页面的差别：**扫码器刻意没有选中行与上下移**（§5.9，用户 2026-11-04 定案
    「去掉选中焦点」）。别看到这里就顺手给扫码器也补一套。
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
- ⚠️ **触发式任务的表达式引用也要收**（2026-10-31 修）。原来只遍历 `task.Items`，
  于是「表达式写着 `sys_X > 0` 等变量变化才触发、但那个变量没被任何数据项引用」时，
  它**永远不会被采**，一直停在初始值 0 —— 触发沿永远等不到，用户只会看到「这组从来不动」。
  修法：`Task.Trigger == Edge` 时按 `Expression` 源文本现编一次（`CompiledExpression.TryParse`），
  把 `UsedNames` 逐个 `Take`；编不出来就跳过（非法表达式不该把整组变量拖上总线）。
  **编译是惰性的**，正常拍里由 `EvaluateTrigger` 触发，故这里必须自己编一次、不能用 state 上那个
- **触发判定是纯函数**（`AcquisitionTrigger.ShouldFire`，不引用 WPF/Modbus，能离线断言）：
  周期式记「下次该采的时刻」而不是「上次 + 间隔」，这样周期不随定时器抖动累积漂移；
  触发式只认**上升沿**，持续为真不重复采
- ⚠️ 周期式以**本次**为基准推下一次，不是以旧的下次时刻 —— 后者在长时间停采（断线重连）后
  会连发一串补采，把文件写满重复数据。断言 ⑥ 专门锁这条
- ⚠️ **首拍要延后一个周期**（`GetOrCreateRuntime` 里设 `NextFireLocal = Now + 周期`）：
  记录定时器与采集引擎是两个独立的 `DispatcherTimer`，启动瞬间先后没有保证。
  不延后的话首行会写进变量的**初始值**（0 / 空串），实测确实如此
- 📏 **节拍口径（2026-10-31 用户定案，三条并存）**：

  | 场景 | 何时采 | 节拍 |
  |---|---|---|
  | 设备监控（IO / 自由监控） | 页面可见时 | 设备连接页 `100/200/500/1000 ms` |
  | 数据管理 → 采集数据 | 周期式最低 `0.1 s`；触发式只在上升沿 | 任务组自己的 `PeriodSeconds` |
  | 总览 → 数据采集 → 实时数据 | 只更新卡片文本，不额外发报文 | 采集引擎的常驻轮询（连接设置） |

  - **采集不发报文**：`RunAcquisitionTick` 只判定 + 取 `GlobalVariable.Value` + 写 CSV，
    值由 `PollAcquisitionGlobalsAsync` 常驻读。故切页面不影响记录、也不重复发报文
  - **tick 固定 50 ms**（`_acqTimer`），与连接周期**无关**，**刻意不改**。
    任务周期 100 ms 时实际最快 100 ms 一次，但值是 0～50 ms 前的 —— 验收按这个口径
  - ⚠️ **已知限制，未修**：任务周期 < 连接周期时同一个值会被重复记多行
    （连接 1 s + 任务 100 ms → 每个新值记 10 行）。CSV 上看不出哪些是复制品。
    用户 2026-10-31 知情并接受，**改它之前先问清「设备稳定输出同一值时是否仍要按周期记」**
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

**来源也可以是常量**（2026-11-05 定，**方案 B：`#` 前缀**）：

- 判据：**以 `#` 开头即常量**。干净的原因 —— 全局变量名规则要求「字母或下划线开头 +
  字母数字下划线」（`GlobalVariableRules.TryValidateName`），**变量名不可能以 `#` 开头**，
  两者永不歧义，旧 json 里的纯变量名零影响
- ⚠️ **常量不带类型前缀，类型由「写入变量」决定** —— 这是选 B 不选 A（`INT : 123`）的全部理由：
  写入变量本身是全局变量、天然带类型，而既有定案要求「来源类型必须与写入变量完全相同」
  （`WriteTypesMatch` 用 `==`）。常量再声明一遍就是同一信息写两次，还会让 `#123` **写不进 DINT 变量**
- 语法（`Modeling/AcquisitionConstant.cs`，纯逻辑可断言）：
  `#123` / `#-5` 十进制、`#0x1F` 十六进制、`#0b1010` 二进制、`#1.234` 小数、
  `#"ABCD"` 字符串（**双引号必填**）、`#1` / `#0` / `#TRUE` / `#FALSE` BOOL（两种写法都接受）
- ⚠️ **字符串常量内部不允许双引号**（用户定案）：不做转义语法，出现即明确报错，
  免得 `#"A"B"` 被猜成别的意思
- ⚠️⚠️ **常量必须按字面量自己的进制解析，不能沿用来源的 `Format`**：
  变量的 `Format` 是「给人看的显示格式」（可能是 Hex/Bin），拿它去解析 `#123` 会当成
  `0x123 = 291` —— **静默写错值**。进制只认字面量的 `0x` / `0b` 前缀，
  用 `AcquisitionConstant.FormatFor` 算，见 `MainWindow.Acquisition.cs` 的 `sourceFormat`
- 校验走 `AcquisitionTaskCheck.DescribeConstantProblem`：**复用编码层**
  （`ValueCodec.TryEncodeRegisters`）而不是另写一套，与运行时同源；
  BOOL 单独判（`CoilOnly` 不进寄存器路径，只认归一后的 `0`/`1`）
- ⚠️ **`HasVariableError` / `VariableHint` 必须显式排除常量**：
  常量的 `AcquisitionSource.Type` 是占位值，拿去和写入变量比必然不等，会让每行常量都标红
- ⚠️ **执行侧取格式要分岔**：常量用 `FormatFor`，变量用 `source.Format`。
  见 `MainWindow.Acquisition.cs` 里 `sourceFormat` 那句三元表达式
- 常量的值**恒定不变**，故 `RefreshAcquisitionGlobals` 的 `Take` 查到查不到都无所谓，
  不要为它补分支（注释已写明）
- 补全候选末尾附了三条常量示例（`#123` / `#0x1F` / `#"ABCD"`），当语法提示用
- **常量的来源框整段显示橙色**（2026-11-05 定）：合法常量橙（`Brush.Warning`）、
  有错（变量名查不到 / 常量语法错）红（`Brush.Danger`）、其它正常字色。
  ⚠️⚠️ **必须用一个转换器一次算定，不要用两条 `DataTrigger`**（实测踩过）：
  两个 `DataTrigger` 同时成立时 WPF 的优先级取决于声明顺序，而且**两种顺序都试过仍不生效** ——
  `#"ABC`（既是常量、又解析失败）一直显示橙色、红色出不来。
  现在的做法：`Ui/SourceForegroundConverter.cs` 是 **`IMultiValueConverter`**，
  由 `AcquisitionSourceBox` 用 `MultiBinding` 显式绑 `HasSourceError` + `SourceVariableName`
  两条 INPC 路径，内部按「有错 → 常量 → 正常」顺序返回一个颜色，没有互相覆盖的余地。
  ⚠️ **不要绑整行对象**（`RelativeSource Self` 的 `DataContext`）：整行对象的引用不变，
  `HasSourceError` 的 INPC 通知**不会**让绑定重算 —— 表现为「编辑后颜色不刷新、
  切走再回来才对」（2026-11-05 实测并修掉）。
  颜色走 `ThemeManager.GetBrush`（C# 侧赋画刷不经过 `DynamicResource`）
- ⚠️ **做不到「`#` 橙 + 值紫」那种分段着色**：`TextBox` 的 `PART_ContentHost` 内部
  `TextBoxView` 只认控件级 `Foreground`，没有 run 级着色接口。要分段只能换 `RichTextBox`
  （22px 行高会被 `FlowDocument` 撑开、中英混排基线跳动）或自绘 `OnRender`
  （光标/选区要自己复刻，差 1px 就看得出错位）。**用户 2026-11-05 定案选整段换色**，
  不要为了分段再去改这个框的控件类型

**写入变量的编辑规则**（2026-10-27，用户定案：填对来源才能编辑、类型必须相同、地址可以以后补）：

- **来源变量填对之前，写入变量一律置灰不可编辑**（`CanEditVariable`）。只绑 `IsEnabled` 就够 ——
  `MonitorInlineTextBox` 模板自带 `IsEnabled=False` 触发器，会把底色切成
  `Brush.Input.Background.Disabled`。但必须同时加 `ToolTipService.ShowOnDisabled="True"`：
  禁用控件默认不弹 ToolTip，而提示恰恰只在禁用态才有用（说了「先填来源」，却看不见）
- 类型必须**完全相同**（`AcquisitionTaskRules.WriteTypesMatch` 用 `==`，不做隐式转换）。
  不符时输入框变红（`AcquisitionTargetBox` 样式，绑 `HasVariableError`），**并且「数据类型」列同时变红** ——
  类型列的文本取自写入变量，只红输入框的话说不清「错在这一列的取值」
- **允许未映射地址**（用户定案）：`HasVariableError` 故意**不看** `HasAddress`，
  只在提示尾缀「未映射地址（只写内存，不下发到设备）」。拦下的话用户连第一版数据都填不进去 ——
  地址是等 PLC 点位确认后才补的事。
  ⚠️ 措辞 **别说成「不会写入」**：2026-11-05 起无地址的写入变量**照样会写**，只是写进内存
  （见 §5.7 的 `CannotAddress`），说成「不会写入」就是界面在骗人
- ⚠️ 「先填来源」这句提示要**跟着来源改口**：判据是 `_isWriteTask && SourceVariable == null`。
  首版写成 `_isWriteTask ? …`，实机里来源填好后置灰解除了、提示还在催「先填写来源变量」，等于骗人
- ⚠️ **行自己看不出所属任务是读还是写**（读取类型下来源列宽为 0，行上没有那个信号），
  所以 `SetWriteTask` / `SetReportTask` 由**父行注入**，三处都要：构造函数、`AddItem()`、`RefreshFromModel()`。
  漏掉 `RefreshFromModel` 的话，读写/上报类型互切后整张子表还留着上一档的置灰状态。
  上报的「数据类型」列反推自**来源变量**（`AcquisitionItemRow.TypeDisplayName`），不是写入变量
- 样式顺序同 10.27 的坑：`AcquisitionTargetBox` 的 `BasedOn` 指向 `MonitorInlineTextBox`，
  必须定义在它**之后**
- 变量没填 / 找不到 ⇒ **空单元格**，不写占位符：表格是给下游（Excel、MES）读的，
  混进「—」会变成脏数据
- 写盘失败**不弹窗**（50ms 一拍会弹到用户崩溃），只写一条 `DeviceLogLevel.Warning` 日志
- **每次执行都写一条运行日志**（用户定案），文案由 `AcquisitionTaskRules.DescribeLogSuccess` /
  `DescribeLogFailure` 生成，形如 `任务组1（压力采集）：触发读取任务执行完成`。
  三个要点：短词（`读取`/`写入`，不是界面上的 `数据读取`）、**成功不带原因也不带冒号**、
  序号给当下定位而名称给事后可读（名称是写日志那刻的快照，改名不回溯）。
  ⚠️ 上报（Report）的动作名是「触发/周期HTTP数据上报」，**不带「任务」后缀**（用户原需求文案）
- ⚠️ **组级「日志记录」关掉就整组不记**（用户定案 2026-10-30，默认开）。判定**只有一个出口**
  `LogTaskActivity(task, message, level)`（`MainWindow.Acquisition.cs`），四个调用点
  （只跑不记 / 记录成功 / 记录失败 / 写入异常兜底）都走它 —— 在调用点各自判 `task.LogEnabled`
  迟早漏一个，表现为「日志记录已关，日志却还在刷」。
  ⚠️ 它只管**任务组自身活动**：断线 / 连接失败这类系统级日志不归它管
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
  写入类型**按填写先后短路只报第一处**（来源空 → 来源查不到 → 写入变量空 → 写入变量查不到 → 类型不符
  → **写入变量落在只读区**），否则一项没填会对用户刷三条
- **写入变量落在只读区也算配置错误**（2026-10-29 用户定案）：`variable.IsReadOnly` 为真时
  报「写入变量「X」映射到 X5，属于只读区（输入元件），不能作为写入目标」。
  为什么放在这里而不是映射地址时拦：全局变量表**允许**映射 X（见下节「只读区只拦写不拦映射」），
  「读得到、写不了」这件事只有写入任务知道，所以只能在这一层报
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
- ⚠️ **`Model.Enabled` 存用户意图，程序绝不改写它**：数据管理页边输边存，清空变量名重打的那一瞬间
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
- `TryBuild` 判定顺序：变量不存在 → **无地址（`CannotAddress`，见下）** →
  `ResolvedAddress < 0`（**解析失败**，`ResolveError` 有值就带出来）→ `IsReadOnly`（只读区兜底，**仅对有地址的变量**）→
  `CoilOnly` 文本须 `0`/`1` → `TryEncodeRegisters`。注意「无地址」与「解析失败」是两回事：
  前者 `HasAddress = false` 走 `CannotAddress`，后者是地址填了但解析不出来（`ResolvedAddress = -1`）走 Failed。
  **`CannotAddress` 与 `Failed` 必须分开**：前者是允许的常态，不能污染失败日志
- ⚠️⚠️ **无地址的写入变量也要能写入 —— 只是写进内存**（2026-11-05 用户定案，改名 `Skipped` → `CannotAddress`）：
  早期这里直接 `return Skipped`、**连编码都不做**，后果是「写入变量没地址 → 变量值永远不变」，
  而日志照样写着「触发写入任务执行完成」，界面完全看不出问题（实测：任务组2 空转一整天）。
  现在：无地址**继续往下编码**，返回 `CannotAddress` + 有效 `plan`，
  调用方用 `AcquisitionWrite.ApplyToMemory(target, plan, orders)` 回填 `Value` + `Numeric`，不发报文。
  ⚠️ `Value` 与 `Numeric` **必须一起设**（前者是显示格式文本、后者是十进制数值，实时卡片两个都用）
- ⚠️ **`RunWriteTaskAsync` 开头不能因「PLC 未连接」整轮 return**（2026-11-05 修订）：
  写入变量可能全是无地址的内存变量，那种项不发报文、PLC 连没连都无所谓。
  整轮 return 会让「只写内存」的任务组在 PLC 掉线时悄悄停摆。
  正确做法：`bool plcConnected` 记下来，**逐项**在真正要发报文前判断并跳过
- ⚠️ **发报文成功后也要 `ApplyToMemory`**：否则「PLC 写成功了、界面上还是旧值」，
  要等下一轮采集读回才更新
- ⚠️ **编码规则（最易写错）**：用**来源变量的 `Format`** 解析文本，用**写入变量的 `Type`/`Length`**
  定字宽与范围（两者 Type 已被 `WriteTypesMatch` 强制相同，只有 Format 可能不同；
  来源 INT/Hex 的 `"2A"` 按目标的 Dec 解析会失败）。因此 `AcquisitionSource` 必须带 `Format`，
  系统变量取 `DefaultFormat`
- 一项失败**不短路**（Modbus 无事务，短路换不来原子性）；失败文案 `DescribeFailure` 拼成
  `2/3 项写入失败：项1（压力）：…`，项名前缀**复用 `AcquisitionTaskCheck.DescribeItemPrefix`**
  （故它是 public，两处各写一份必然走样）
- CSV **一行不改**：它记写入变量的回读值，来源变量从不进文件；写通后该列自然变成真值
- **只读区只拦「写」，不拦「映射」**（用户 2026-10-29 定案，推翻了 2026-10-27 的硬拦截）：
  全局变量表**允许**把地址填成 X / 离散输入 / 输入寄存器 —— X 读得进变量、进记录表、进表达式。
  `GlobalVariableStore.TryResolve` 命中只读元件时只置 `v.IsReadOnly = true` 并**照常赋地址**、
  照常返回 `true`；`AddressErrorKind` 里**已经没有 `ReadOnlyArea` 这一档**（连枚举成员一起删了）。
  「不许写」改在写入任务那一层报两处：① `AcquisitionTaskCheck.DescribeItem` 的 Write 分支
  命中 `variable.IsReadOnly` → 算**配置错误**（整组拦下，比运行期写失败好）；
  ② `AcquisitionWrite.TryBuild` 兜底拒写（给旧数据 / 手改 json 用）。
  行内也标：`AcquisitionTaskItem.HasVariableError` 与 `VariableHint` 都会报「属于只读区（输入元件）」。
  ⚠️ 判定只有 `GlobalVariableStore.IsReadOnlyElement` **一处来源**，写入侧只管读 `IsReadOnly` 标志

**常驻轮询的三份清单（2026-11-11）**：

| 清单 | 来源 | 何时采 |
|---|---|---|
| `_machineGlobals` | 状态机 9 框 + 2 灯表达式 | 总览页「PLC状态机」可见时 |
| `_acquisitionGlobals` | 采集任务组的读取 / 写入 / 来源变量 + 触发表达式 | **常驻**（与页面无关） |
| `_scannerGlobals` | 扫码器触发表达式 | **常驻**（与页面无关） |

- ⚠️ **扫码器触发表达式必须有自己的清单**（2026-11-11 修）：原先只刷系统变量快照，
  表达式里的**全局变量**没有任何地方采 → 触发条件写 `speed > 0` / `var1[0] == 1`
  会永远拿初始值求值，界面上却显示「配置完整」，用户只会以为扫码器坏了。
  入口 `RefreshScannerGlobals()`（`MainWindow.MachineState.cs`），
  在**扫码器表达式提交 / 增删 / 启停 / 变量表变化 / 启动载入**时重算。
- ⚠️ **变量表一变，两份常驻清单都要重算**：`RefreshAfterVariableChange()` 里
  `_acquisitionGlobals` / `_scannerGlobals` 存的是 `GlobalVariable` **对象引用** ——
  删掉变量后重建同名变量，清单里仍是旧对象，任务引用新对象、轮询读旧对象，值永远不更新。
- ⚠️ **`RefreshScannerReferenceHints()` 只算「变量已被引用」的黄字，不算配置错误**：
  变量删除 / 换类型后还要 `RefreshAllScannerItemStates()` 才能让行上的「配置错误」与红框跟上。
- ⚠️ **新增变量也要 `RefreshAfterVariableChange()`**：新增可能让别处原本「变量不存在」的引用恢复，
  只调 `RefreshGlobalVarView()` 的话任务组 / 扫码器不会自动变回可用。

**执行完成输出变量（2026-11-10，口径照扫码器「执行结果输出变量」）**：

- 任务组选项对话框最后一行，模型字段 `AcquisitionTask.CompletionVariableName`（空串 = 不写）。
  写入值固定：**正常执行完成 `1`、异常执行完成 `2`**（`AcquisitionTaskRules.ResultSuccess` / `ResultFailure`），
  框下一行小字 `ResultValueHint`，**不做可配**（与扫码器同口径）
- **只认 INT / UINT / DINT / UDINT**；留空合法。校验只有一处
  `AcquisitionTaskRules.TryValidateCompletionVariable`，**对话框与 `AcquisitionTaskCheck` 共用它** ——
  填了查不到 / 类型不对就报「配置错误」、整组拦下（与扫码器校验、既有配置完整性同一套）
- 「异常」的判据**复用现有执行日志的判据**：读取任务看 `AcquisitionCsv.Append` 的返回；
  写入任务看 `DescribeFailure` 汇总出的 `reason`。即「日志写执行完成 → 写 1，日志写执行失败 → 写 2」，
  不新增第二套成败判断
- 下发链路 `QueueCompletionWrite`（`MainWindow.Acquisition.cs`）：**先 `ApplyToMemory` 无条件回填内存**
  （无地址 / PLC 未连也能在界面看到结果）→ 有地址且已连接时再经 FC06 / FC16 写 PLC。
  ⚠️ 编码格式**固定用 `DisplayFormat.Dec`**，不沿用变量自己的显示格式 ——
  `Format=Hex` 的变量会把 `"1"` 当成 0x1 解析（碰巧对），但 Bin 变量会把 `"1"` 当二进制、
  格式化回读时变成另一串文本；结果值只有十进制 1 / 2，格式由我们说了算
- ⚠️ **不能 await**：读取任务的调用点是 50ms 定时器回调，写入任务也不该为结果变量再拖住一轮。
  用 `AcquisitionTaskState.CompletionWriteInFlight` 挡重入 + `PendingCompletionValue`
  **只保留最新一个**（周期 0.1s 时排队旧值只会越积越多，最后写进去的全是过期结果）
- 未连接时**丢掉待写值**：重连后补写一个陈年结果比不写更危险，与采集「断线不补采」同口径
- 改名连带改引用：`AcquisitionTaskItemSet.RenameVariableReferences` 里按
  `VariableRenamer.RenameReference` 改这一格（与扫码器两个变量框同一手法）
- 只读区（X 等）**不在配置检查里拦**：与扫码器同口径，运行期由 `AcquisitionWrite.TryBuild`
  拒写并记一条 `执行完成输出变量：写入「X」失败：…` 日志

### 5.8 总览「数据采集」视图（P1.4，2026-10-31）

总览页「当前视图」下拉框的**第三项**。卡片内两块：

```
[实时数据][历史数据]  2026-10-31.csv              [心跳 ▾]   ← 左：页签 + 文件名；右：下拉框
────────────────────────────────────────────
┌──────────────────────────────────────┐
│ 时间   任务类型   压力    …（列随表头变） │   ← 历史数据：DataGrid，1 秒增量刷新
└──────────────────────────────────────┘
```

> 下拉框**不带「任务组」标签、不加外框**（2026-10-31 用户定案）：卡片里只有这一个选择器，
> 语义自明；裸胶囊 = 灰底 `Brush.Control.Background` + chevron，靠 `FormComboBox` 的默认
> `BorderThickness=0`。⚠️ 别为「跟页头一致」给它补上 `BorderThickness=1` —— 页头那个是
> 有意框出来的（那里左侧还有「当前视图」标签做区分），这里不需要。

代码全在 `MainWindow.AcquisitionView.cs`（新 partial），CSV 解析在
`Modeling/AcquisitionCsv.cs` 的 `ParseLine` / `FindLatestRecordFile`（纯逻辑，可离线断言）。

**下拉框只列「能用」的组**（`RefreshAcqGroupCombo`）。判据三条，**缺一不可**：

1. `EffectiveEnabled`（= 用户启用 **且** 无配置错误，见 `AcquisitionTaskItem`）；
2. **`Model.RecordEnabled`**（2026-11-11 用户要求）—— 本视图展示的是**落盘的 CSV**，
   不开数据记录的组在这里既没有历史文件、也不该占一个下拉项，选中它只会得到
   「该任务组还没有保存的数据」这种等于没说的空状态；
3. 一个数据项都没有的组本来就会被 `AcquisitionTaskCheck` 判成配置错误，含在第 1 条里。

⚠️ **空状态提示必须区分三种情况**（`DescribeNoUsableGroup`）：一个组都没建 / 建了但停用或
配置错误 / **能跑但没开数据记录**。只写「没有可用的任务组」会让用户去翻启用开关，
而真实原因那个开关在**任务选项对话框**里、根本不在这一页。

⚠️ 实时与历史两个页签**共用**这一个下拉框，所以筛选条件对两者同时生效
（当前一套判据，没有「实时能看、但不记录」这种中间态）。

### 5.8.1 实时数据子页签（2026-10-31）

代码在 **`MainWindow.AcquisitionLive.cs`**（又一个 partial，与上面那个分开：
上面是「历史表格 + 页签 + 下拉框」，这边是「画布 + 卡片 + 拖动」）。
选项框 `Ui/AcquireLiveCardOptionDialog.cs`（**两个字号 + 小数显示位数 + X/Y 坐标 + 重置位置**；
2026-11-03 曾去掉 X/Y（只靠拖动），同日又按用户要求加回 —— 两者互补：
拖动适合「大致摆好」，坐标框适合「两张卡片对齐」「按数字复现某次布局」。
⚠️ **「小数显示位数」只在变量是浮点时才建那一行**（2026-11-06 用户定案，原话
「如果是浮点数数据，则选项显示：小数显示位数（默认1位）」）：整数没有小数位、
BOOL 是 TRUE/FALSE、STRING 是文本，摆出来只会让人以为能改显示精度。
判定在 `MainWindow.AcquisitionLive.cs` 的 `LiveCard_DoubleClick` 里
（`FindByName(VariableName)?.Type == DataType.Real`；查不到按「不是浮点」处理），
结果作 `isFloat` 传给 `Ask`。值存在**数据项**上（`AcquisitionItem.LiveFloatDecimals`），
不是任务组 —— 同组里各变量量纲不同（压力 2 位、温度 1 位）。
⚠️ **按钮行号必须跟着表单行数走**（`Grid.SetRow(buttonRow, row++)`）：
写死 4 的话浮点模式下「重置位置 / 取消 / 确定」会**叠在 Y 坐标行上**。
⚠️ 小数位数走 `AcquisitionLiveText.ClampLiveFloatDecimals`（`null` / 负数 → 默认 1 位，
超上限 → 4 位），下拉框缺省选中也用同一个函数 —— 否则旧 json 读出来是 0 会让下拉框
一个都没选中、闭合态显示成一片空白。
⚠️ **负零要归零**：`−0.04` 按 1 位四舍五入得到 `−0.0`，`ToString("F1")` 会如实打印
「-0.0」，卡片上像程序算错了。`FormatFloat` 里 `if (rounded == 0) rounded = 0;`
（`−0.0 == 0` 为真，只在结果确实是零时才抹符号）。这条是断言测出来的。
⚠️ **坐标只到整数，且从输入上就挡住非法字符**（2026-11-03 用户定案，
取代原先「先让输、点确定时再红字报错」）：非数字字符**根本打不进去**，
于是不需要「请填整数」这类提示。
规则本体在 `Modeling/LiveCoordinateText.cs`（**纯函数、可离线断言**，断言 ⑫①…㉘ 共 28 项）：
`IsAcceptableIntegerText`（哪些文本可留在框里）/ `Parse`（空串与纯 `-` 当 0）/
`Format`（拖动遗留的小数四舍五入显示）/ `SimulateInput`（按选区模拟插入结果）。
⚠️ **不要写成「这个字符是不是数字」** —— 单独的 `-` 也必须放行，
否则用户永远打不出负数（先打 `-` 就被拦掉了）。
⚠️ 粘贴也要过一遍（`DataObject.AddPastingHandler`），否则贴一段带小数点的文字就钻空子。
⚠️ 表单控件**宽度必须统一**：下拉框走 `MakeCombo(FieldWidth)`，而 `MakeTextBox`
造出来的框**不设宽度**，必须手动 `box.Width = FieldWidth` ——
漏了这一步两者宽度不一致，右侧看着参差不齐（实测踩到）。
⚠️ 该框的 `root` **必须有 `Margin = new Thickness(16)`**，漏了就是内容贴着窗口边缘，实测踩到过）；文案与格式化
`Modeling/AcquisitionTask.cs` 里的 `AcquisitionLiveText`（**纯函数**，可离线断言）。

```
ScrollViewer                        ← 画布放不下就滚
└ Canvas 1280×720（写死）            ← 坐标基准
   ├ Image  AcqLiveBackground       ← 背景图，Stretch=Fill，0,0 起铺满
   ├ Border LiveCard0               ← 每个数据项一张，Canvas.SetLeft/Top 摆位
   ├ Border LiveCard1
   └ …
```

**⚠️ 十条容易踩的**：

1. **逻辑坐标固定 1280×720，显示尺寸跟着图片比例走**（2026-11-02 用户改定）。
   画布**不再是** 1280×720 固定尺寸、也不再出滚动条：按可用区域把图缩到
   「能完整显示的最大比例」。
   - 逻辑空间（存盘坐标、选项框里的 X/Y）**永远是 1280×720** —— 换窗口大小、
     换机器，同一个数字都落在图上同一点。只有**显示**时才乘比例。
   - ⚠️ **画布尺寸要跟着图片真实长宽比**（`BitmapImage.PixelWidth/PixelHeight`）。
     写死 16:9 画布 + `Stretch=Uniform` 会让 4:3 的图上下留黑边
     （2026-11-02 实测踩到，截图里图只占中间一条）。画布与图同比例，图才铺满且不变形。
   - ⚠️ **X/Y 是两个比例，不是一个**：16:9 的逻辑空间铺到 4:3 的显示矩形上，
     两轴倍率必然不同。用统一倍率就只能留黑边或裁图。见 `_liveScaleX` / `_liveScaleY`。
   - **卡片整体跟随比例缩放**（2026-11-03 用户改定；早前是「只缩位置、字号固定」）：
     字号、内距、圆角、数值框内距全部乘 `_liveScaleX`，窗口拉大拉小，卡片与图片的
     相对大小关系保持不变。用户设定的字号是**逻辑字号**（存模型、永不改；
     默认 **20 / 20**，2026-11-04 用户定），只有显示时才乘比例（见 `ApplyLiveCardScale`）。
     ⚠️ **两个字号必须用同一个比例、同一个下限策略**，否则名称:数值 的视觉比例会歪。
     当前下限 `LiveMinReadableFontSize = 0`（= 严格跟随、不兜底）——
     曾试过 3.5 / 5，那会让名称与数值比例失真，用户明确要「严格跟随」。
     将来要兜底就改这一个常量，**别只对其中一个字号兜底**。
   - **卡片可以放到图片之外**（2026-11-02 用户定案），只要还在父卡片范围内：
     画布撑满父卡片（保持 `Stretch`，**不要**设 Width/Height），图片在画布内**居中**，
     四周露出的就是可放卡片的留白。逻辑坐标原点因此是**图片左上角**，
     屏幕位置 = 图片偏移 + 逻辑坐标 × 比例（见 `_liveImageOffsetX/Y`）。
   - ⚠️ **`LiveX/LiveY` 是 `double?`，null = 没摆过**。**不能**用 `>= 0` 判：
     卡片可以拖到图片左边，那时坐标是**负数**（实测存过 `X=-19.66`）。
     旧 json 里「没摆过」存的是 `-1`，`AcquisitionTaskSet.Load` 会把它转成 null。
   - ⚠️ **别给 Canvas 设 `HorizontalAlignment=Center` / `VerticalAlignment=Center`**：
     那会让它按内容测量、`ActualWidth/ActualHeight` 变成 **0**，于是画布没有命中区域 ——
     **卡片看得见但点不到、拖不动**（2026-11-02 实际排查了很久，加探针才发现）。
     保持默认 `Stretch`。
2. **卡片直接塞进 `Canvas`，不要套 `ItemsControl`**。拖动要 reparent，
   而 `ItemsControl` 生成的那层 `ContentPresenter` 会让「摘父级」失败 ——
   2026-10-31 在全局变量表试拖动时就因为这个抛
   「already the logical child of another element」直接崩溃。
   这次父级链里只有 `Canvas`，所以安全。
3. **必须 `CaptureMouse()`**，且要判它的返回值。不捕获的话鼠标一快就滑出卡片，
   后续 `MouseMove` 收不到，卡片「粘」在原地不动。判返回值是为了失败时能回滚 ——
   否则 `_liveDragging` 永远非 null，之后所有点击都被当成拖动。
4. **`LostMouseCapture` 里要收尾**：Alt+Tab、弹窗抢焦点都会打断拖动，
   不接这个事件就丢状态。
5. **只在 `mouseup` 落盘**。`MouseMove` 每秒上百次，每次都写 json 会把文件写坏。
   判据是 `card.DragStarted`（越过阈值才算真拖过）—— 只点一下不该写盘。
6. **拖动阈值 3 DIP**（`LiveDragThreshold`）。没有阈值时手一抖就把卡片挪偏，
   而用户以为自己只是点了一下。
7. **双击在 `PreviewMouseLeftButtonDown` 里按 `ClickCount > 1` 判**：
   `Border` **没有** `MouseDoubleClick` 事件（那是 `Control` 的），
   写 `root.MouseDoubleClick += …` 编译期就报 CS1061。
8. **改任务组名称 / 启用状态后要立即重填下拉框**（2026-11-03 用户反馈修）。
   总览页「数据采集」的任务组下拉框只在 `RefreshAcqGroupCombo()` 里重填，而它原先
   只在切视图 / 切页签时被调 —— 表现为「改完名称要切一次当前视图才刷新」。
   现在三处都补了调用：`RefreshAcquisitionView`（增删组）、`AcquisitionTaskEnable_Click`（启停）、
   `OpenAcquisitionTaskDialog_Click`（改选项，含改名）。
   ⚠️ **不要给这些调用加「视图可见才刷新」的判断** —— 用户正是在**数据管理页**改的，
   那一刻 `AcquisitionDataView` 一定是 `Collapsed`，加了判断等于永不生效（第一版就是这么错的）。
   ⚠️ **改名时还要先把 `_acqHistoryGroup` 同步成新名**：`RefreshAcqGroupCombo` 靠它
   「尽量停在原来那一组」，它还记着旧名，不同步会匹配失败退回第 0 组
   —— 多组时表现为「改个名，下拉框自己跳到第一组去了」。
9. **改数据项的项名称 / 变量名要立即重建卡片**（2026-11-03 用户反馈修）。
   卡片上的文字是**建的时候一次性写进去**的，不重建就一直是旧值。
   入口在 `AcquisitionItemField_LostFocus`（数据管理页那三个输入框的失焦事件）里
   调一次 `RebuildAcqLiveCanvas()`。
   ⚠️ **那里不能加 `IsAcqLiveVisible` 之类的可见性判断** —— 用户此时正在「数据管理」页
   编辑，实时视图必然是隐藏的；加了判断等于永不生效，于是「改完名称要切一次当前视图才刷新」。
   重建代价很小（卡片只有几张），且**背景图有缓存**：`_liveImageCache` 记住上次解码的位图，
   同一张图不重读硬盘（一张工位图常几百 KB 到几 MB）。
10. **⚠️ 换任务组时「历史表」和「实时画布」必须一起换**（2026-11-06 用户反馈修）。
    `AcqGroupCombo_SelectionChanged` 原先只做 `ResetAcqHistory()` + `RefreshAcqHistoryTable(force:true)`，
    **漏了 `RebuildAcqLiveCanvas()`** —— 停在「实时数据」页签时换组，下拉框变了、
    画布还是上一组的卡片，用户报的就是「切换任务组没有实际切换页面」。
    现在两条一起走，末尾还要 `RefreshAcqLiveValues()`：不立刻刷一次的话，
    卡片会先显示占位符 `—`，等下一拍定时器（最多 1s）才出数。
    ⚠️ 别只在「历史页签可见」时才刷历史表、只在「实时页签可见」时才重建画布 ——
    两个页签是同一个「当前组」的两种呈现，谁没被切到，切过去时也必须是新的
    （`AcqSubView_Checked` 里那句「切回实时数据补一次重建」就是为此）。
11. **数值不能从 `GlobalVariable.Value` 反解**。那是**按显示格式转好的文本**：
   一个 INT/Hex 变量的值是 `"2A"` 而不是 `"42"`，而 STRING 里也可能正好是 `"2A"` ——
   文本没法区分「十六进制数」和「内容就是 2A 的字符串」。
   故回填时顺手存一份 `GlobalVariable.Numeric`（`ValueCodec.DecodeNumber`），
   卡片只读它。**`ResetValues` 里要一并清 `Numeric`**，否则「重置了但卡片还显示旧数」。
12. **`AcquisitionItem.Id` 是布局的键**，不是项名称：名称允许重复、允许改，
   改一次布局就丢；也不能用数组下标（增删一项后面全体错位）。
   旧 json 没这个字段 → `AcquisitionTaskSet.Load` 自动补发 GUID。
13. **背景图要拷贝进 `Images\`、只存相对路径**。存绝对路径的话现场机换个盘符、
    清一次 D 盘图就没了，而用户完全不知道（表现为实时页一片空白）。
    同名文件不覆盖、加 `_N` 序号 —— 两个任务组可能各配一张同名图。
    规则本体在 `Modeling/LiveImageStore.cs`（**纯文件操作、不含 WPF**，可离线断言）。
    ⚠️ **判「是不是同一张图」要比内容、不能只比路径**：用户第一次选
    `D:\图\panel.png` 拷成 `Images\panel.png`，第二次又选**原来那个** ——
    两者路径完全不同，只比路径就会判成新文件、一路存成 `panel_1.png`、`panel_2.png`。
    实测踩到过，断言 ⑪④ 抓住的。也不能只比文件名：不同目录下的同名文件是**不同的图**，
    复用会让用户换了图却看不出变化（断言 ⑪⑤ 守着这条）。
14. **背景图只有一个入口**（2026-11-03 用户定案）：
    `数据管理 → 采集数据 → 任务选项 →「实时背景图」行`。
    总览页实时数据页签右侧原有的「导入图片」按钮**已删除** ——
    两者走同一个 `Ui/LiveImagePicker.cs`、写同一个 `AcquisitionTask.LiveImagePath`，
    是同一件事；同一字段两个入口用户不知道该用哪个，且那个按钮能力更弱
    （不能清除、看不到当前图名、只能作用于下拉框当前选中的组）。
    拷贝规则在 `Modeling/LiveImageStore.cs`（纯文件操作、可离线断言），**不要写第二份** ——
    「同名加序号」这类规则写两遍必然分叉。

**显示规则**（用户在 `AcquisitionLiveText` 里定案，逐条有断言）：

| 类型 | 显示 | 备注 |
|---|---|---|
| `BOOL` | `TRUE` / `FALSE` | 不是 `1`/`0`；读 `Numeric` |
| `STRING` | 最多 20 字，超出 `…` | 读 `Value`（字符串本来就没有格式问题） |
| 整数 | 十进制，最多 7 位 | 超出退化成 `E2` 科学计数法 |
| 浮点 | 十进制，整数位 ≤5、小数位数**按项配置**（默认 1 位，0~4 可选） | 整数位超出退化成 `E2` |
| 任意类型但没采到值 | `—` | 与「值就是 0」区分开 |

> **数值外面有一圈矩形框**（2026-11-03 用户要求）：`Border` + `Brush.Input.Background`，
> 圆角 4、内距 6×1。作用是让「值」和「项名称」在视觉上分开 ——
> 只靠字号差，小窗口下不够明显。视觉上接近输入框，但卡片是**只读**的。
> 数值**靠左**对齐（`TextAlignment=Left`）：卡片宽度会被内容撑开，居中的话
> 数值在框里的相对位置随卡片宽度浮动，看着不稳。
> ⚠️ **框线颜色与父卡片对调**（2026-11-03 用户要求）：内层数值框用 `Brush.Card.Border`
> （浅 `#E8E8E8`），外层卡片用 `Brush.Input.Border`（深 `#C8C8C8`）。
> 层次是「外面更实、里面更轻」；反过来（内深外浅）会让数值框比卡片本体还抢眼。
> 实测像素验证：外框 `#C8C8C8`、内框 `#E8E8E8`。
> ⚠️ 那个 `Border` 必须 `IsHitTestVisible = false`，否则它会把鼠标事件吃掉，
> 用户点数值区就拖不动卡片。

> ⚠️ **不管变量自己配的显示格式**（Hex/Bin 也不用管）—— 卡片一律十进制。
> 这是与自由监控 / CSV 的**刻意差异**，别为了「三处一致」改回去。

**⚠️ 九条容易踩的**：

1. **下拉框从两态变三态**：原来 `OverviewView_SelectionChanged` 只判
   `IsMachineViewSelected`，非状态机就当「生产数据」。加第三项后这么写会让「数据采集」
   显示成「规划中」。现在按 `OverviewViewCombo.SelectedIndex` **逐项判**，
   别再取反。新视图的判据是 `IsAcquisitionViewSelected`（索引 2）。
2. **`DataGrid` 是本项目唯一的例外**，其余表格都是 `ItemsControl + DataTemplate`、
   **没有任何虚拟化**。历史表格列数是运行时的（读 CSV 首行表头）、一天能到上万行，
   用 ItemsControl 会直接卡死，所以这里用内置 `DataGrid`（零依赖、自带行列虚拟化）。
   代价是必须整套重画样式（`AcqHistoryDataGrid` / `AcqHistoryColumnHeader` /
   `AcqHistoryCell` / `AcqHistoryRowStyle`），否则是 Win7 白底蓝框的默认外观。
3. **⚠️ 滚动补偿不能乘行高**：`DataGrid` 启用虚拟化后内部 `ScrollViewer` 是
   **按条滚动**（`CanContentScroll=True`），`VerticalOffset` 的单位是**行数**不是像素。
   曾按「插入行数 × 行高(28)」补偿，实测视图直接往下甩了约 **57 行**。
   正确做法：插入前记下视口顶部那一行（`CanContentScroll` 时索引 = `VerticalOffset`），
   插完 `ScrollToVerticalOffset(该行新索引)` 把它滚回顶部。像素滚动时才用行高乘。
4. **末尾半行不能吃**：采集侧正在往同一个文件追加，读到半行是常态。
   只消费到**最后一个 `\n` 为止**的字节，把 `consumed` 游标停在行边界上；
   把半行也解析了会多出一行垃圾，而且游标停在行中间，下一拍接着读就全错位。
5. **UTF-8 BOM 要跳过**：写盘侧带 BOM（Excel 双击中文不乱码），
   `offset == 0` 时若不跳 3 个字节，首列表头会变成 `\uFEFF时间`，与后面读到的表头对不上。
6. **刷新要有三档**：① 长度没变 → 整轮跳过（连文件都不读）；
   ② 文件换了（跨天 / 表头变出 `_2.csv`）→ 整表重载并重建列；
   ③ 同一文件 → 只解析新增字节、新行插到顶部。**先比 `FileInfo.Length`**，
   否则每拍都在读文件、白烧磁盘。
   - 定时器只在「总览页 + 数据采集视图 + 历史数据页签」三者同时成立时跑，
     切走立刻 `Stop()`；数据采集视图也不是状态机时的那条 `SyncMachineTimer`，
     两条互不影响（同时只可能跑一条）。
   - 连续读取失败只在**首次**显示红字（`_acqHistoryErrorShown`），恢复后清掉 ——
     否则文件被 Excel 独占时每秒刷一条错误。
7. **⚠️ 胶囊外观靠外层灰底 `Border`，不在 `IoSegmentRadioButton` 里**：
   那个样式的模板背景是 `Transparent`，选中态才给 `Brush.Control.Selected`（白）。
   「灰底轨道 + 白色浮起」的观感来自外面这圈容器：
   `Background="{DynamicResource Brush.Control.Background}" CornerRadius="6" Padding="2"`。
   漏掉它页签就退化成纯文字（首版就是这样，被用户一眼看出「没有胶囊样式」）。
8. **⚠️ 任务组下拉框不能直接把 `Model.Name` 塞进 `Items`**：
   禁用组要显示成「任务组2（停用）」，但回查任务组必须用**真名**。
   所以每项是 `ComboBoxItem { Content = 显示名, Tag = 真名 }`，
   `SelectionChanged` 里读 `Tag`。拿 `Content` 去比会永远比不到（后缀是纯展示）。
9. **⚠️ 重填下拉框期间必须手工同步“当前组”**：`Items.Clear()` + 逐项 `Add` 会连打一串
   `SelectionChanged`，所以整段罩在 `_acqComboRebuilding` 里挡住。**但挡住事件的代价是
   那个事件里“更新 `_acqHistoryGroup`”这一步也一起没了** —— 必须在这里手工补上。
   漏了的表现很迷惑：下拉框明明显示「心跳」，表格却说「请先选择一个任务组」
   （因为 `_acqHistoryGroup` 还停在空串上，实测踩到过）。
   只在**选中组确实变了**时才 `ResetAcqHistory()`，否则每次重填都会把已经读好的表格清空重来。

**其它口径**：行数上限 `AcqHistoryMaxRows = 500`（倒序，最新在最上）；
列从文件首行**动态生成**，`Width=Auto` + `MinWidth=80`，列多时横向滚动；
时间列就是文件里的 `HH:mm:ss`，日期靠标题的文件名承担；
任务组胶囊列**全部**组（读 + 写），禁用的名称后加「（停用）」；
0 个组时页签与表格整块收起，只留「尚未添加任务组，请到数据管理页新增」；
空/出错时把 `DataGrid` 也收起 —— 没有列时它仍画一条表头下边框，悬在提示文字上方像条野线。
文件名只在「历史数据」页签下显示（`SyncAcqHistoryFileTextVisibility`），
摆在实时数据下会让人以为实时数据也出自该文件。

### 5.9 扫码器（P4.5 连接 + P3.5 交互，2026-11-04 合并为一页）

**一句话**：一台扫码器 = 一份 `ScannerDefinition`（共用定义）；**连接参数与交互参数同处一行**（§5.9.2 一页两列），
触发条件成立 → 发触发文本 → 按结束符收一帧 → 结果写进缓冲区变量 + 通知变量（可选再下发到 PLC）。
**每个扫码器一条独立 TCP 连接、独立重连，16 台互不影响。**

> ⚠️ **2026-11-04 起只有一页**。原先 P4.5（设备连接 → 扫码器连接）与 P3.5（数据管理 → 扫码器）各占一页、
> 各有一套列表；用户定案合并：一行一台，展开后**左边连接相关参数、右边交互相关参数、中间一条竖分隔线**。
> 数据管理页的那个「扫码器」分段与 `ScannerConfigPanel` **整段已删除**（`grep ScannerConfigPanel` 应为 0 命中），
> **不要再加回来**。本节 §5.9.2 是这一页的结构，§5.9.3 是它右边那半边的交互参数口径。

| 文件 | 职责 |
|---|---|
| `Scanners/ScannerDefinition.cs` | `ScannerDefinition` 模型 + `ScannerRules`（规则常量与校验）+ `ScannerStore`（`scanner.json`） |
| `Scanners/ScannerTcpClient.cs` | 裸 TCP：原样发送 + 按结束符/静默窗口收帧（**收帧绝不取消在途 ReadAsync**，见 §5.9.5） |
| `Scanners/ScannerSession.cs` | 每台的连接身份（`Client` / `Target` / `Connect`·`Reconnect` 取消源 / 重连计数） |
| `Scanners/ScannerExchange.cs` | `ScannerRuntimeState`（运行时态）+ `ScannerTrigger.ShouldFire`（上升沿） |
| `MainWindow.Scanner.cs` | 全部 `Scanner*` 处理器 + `ScannerItem` / `ScannerItemSet` + 50ms 判定节拍 + 连接生命周期 |

#### 5.9.1 数据与规则

- `ScannerDefinition`：`Name` / `Ip` / `Port` / `Terminator`（默认 `CrLf`）/ `TriggerExpression` / `TriggerText` / `BufferVariableName` / `TimeoutSeconds`（默认 3.0）/ `NotifyVariableName` / `Enabled`（默认 true）。**只有配置，没有任何运行时态字段** —— 运行时态全在 `ScannerRuntimeState` 与 `ScannerSession`，落盘才不会把「上次连没连上」写进 json
- `enum ScannerFrameTerminator { CrLf = 0, Cr = 1, Lf = 2, None = 3 }`，`ScannerRules.TerminatorBytes` → `{0D,0A}` / `{0D}` / `{0A}` / `Array.Empty<byte>()`
- `ScannerRules` 常量：`MaxTriggerTextLength = 20`、`MinTimeoutSeconds = 0.5`、`MaxTimeoutSeconds = 10`、`DefaultTimeoutSeconds = 3.0`、`MaxScanners = 16`
- ⚠️ **触发文本只允许 ASCII，且按 ASCII 长度算**（`TryValidateAsciiLength`）：「触发文本不能为空」/「不能超过 20 个字符」/「只能包含 ASCII 字符」。中文扫码枪指令现场基本没有，但一个全角字符在 `Encoding.ASCII.GetBytes` 下会变成 `?`，静默发错字节比报错难查得多
- ⚠️ **`scanner.json` 的 `JsonSerializerOptions` 必须带 `JsonStringEnumConverter`**：枚举写成名字而非数字，否则以后往 `ScannerFrameTerminator` 中间插一个成员，历史配置会被静默解释成别的结束符（收帧全错但不报错）。同时带 `UnsafeRelaxedJsonEscaping`，中文名不会变成 `\uXXXX`
- `ScannerStore.Load()` 文件不存在 / 空内容 / 反序列化异常 → 一律空列表（**不抛**）；`Save()` 返回 `bool`

#### 5.9.2 扫码器连接页（设备连接 → 扫码器连接，一页两列）

- 设备连接页顶部两档页签 `PlcConnectionSegment` / `ScannerConnectionSegment`，`GroupName="DeviceView"`，`Checked="DeviceView_Checked"`。原表单整段包进 `PlcConnectionPanel`，扫码器那段是 `ScannerConnectionPanel`
- **一行一台，行尾箭头展开该台自己的参数**（2026-11-04 改，与采集任务组同款；改造前是「左列表 + 右列一份共享表单」）。表头列宽 **`30/*/124/30/30`**：`#` /「扫码器名称」/「启用」（含配置错误四个字）/ `×` / 展开箭头；右上角 `+`（`AddScannerButton` 的 `Grid.Column="3" Grid.ColumnSpan="2"`）。**行高 `42`、下边框 `0,0,0,1` = 与采集任务组主行同一数值**，改一处必须同步另一处
- 行上从左到右：`Id` / `ScannerNameBox`（行内改名，唯一改名入口）/ 启用开关 + 琥珀「配置错误」/ `×` 删除 / 展开箭头。**启用开关只在合并后的这一行有**（原 P4.5 页没有开关）
- ⚠️ **展开块是两列 + 中间一条竖线**（用户定案原话：「分为两列，左侧连接相关的参数，右边是交互相关的参数，中间加分割线」）：外层 `Border`（`Grid.Row="1"`、`Margin="0,4,0,4"`、`Padding="10,10,10,6"`、`Brush.Acquisition.SubTable.Background`、`BorderThickness=1`、`CornerRadius=4`）内直接是 `<Grid>` 三列 `Auto / Auto / *`：
  - **左列**（5 行连接参数）：连接方式 / 协议 / IP地址 / 端口号 / 结果帧结束符
  - **中列**：`<Border Grid.Column="1" Width="1" Margin="18,0,18,0" HorizontalAlignment="Center" Background="{DynamicResource Brush.Divider}"/>`。宽度只用 `1`，**别用 `1*`** —— 星号列会去分剩余宽度、竖线就跑偏了
  - **右列**（5 行交互参数）：触发条件表达式 / 触发请求内容 / 接收内容输出变量 / 接收超时判定（秒）/ 执行结果输出变量（含框下一行小字）。**标签 `Width=168`**、控件 `Width=200`（「接收超时判定（秒）」框 `Width=80`）、行 `Margin="0,0,0,12"`、末行 `0,0,0,12`（末尾挂着小字，不再靠末行 2 收口）
  - ⚠️ **标签列宽 168 是「用户把标签改长」之后定的**（2026-11-04）：原来左右两列都写 `Width=110`，用户把 5 个标签改成了更完整的说法（见 §5.9.3 的对照表），最长的是「接收超时判定（秒）」与「执行结果输出变量」（前者 10 个字符含全角括号），110 会**硬裁断**（`TextBlock` 没有 `TextTrimming`）并直接压到输入框上。两列各 168 + 200 + 竖线 37 = 773 DIP，最小窗口 1024 时展开块内可用约 828 DIP（150% DPI 实测右列输入框右缘距窗口右缘仍有 141px 余量），放得下。**再加长标签或再加列要先算这笔账**
- ⚠️ **展开块里没有「配置明细文字」，错误靠输入框标红表达**（用户 2026-11-04 第三次定案，原话：「去掉红色的提示文字，相关配置错误给输入框标红即可」）：改造前这里有个 `<StackPanel>` 包着一行 `{Binding Problem}` 的红字，**已整段删除**，现在 `Border` 的直接子元素就是那个三列 `Grid`。明细仍留在 `ScannerItem.Problem` 里（给悬停/日志/行上四个字用），只是界面上不再成段显示
- ⚠️ **展开块在行下方 → 模板内取不到 `x:Name`**：所有处理器统一 `ScannerRowOf(sender)`（`fe.DataContext as ScannerItem ?? fe.Tag as ScannerItem`）取那一台，开头一律 `if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item) { return; }`。**不要再写「先选中再右侧填」那套**
- ⚠️ **没有「选中行」这个概念了**（用户 2026-11-04 定案：「去掉选中焦点」）：两页的 `<Border.Style>` + `IsSelected` DataTrigger、`PreviewMouseLeftButtonDown`、`SelectScanner`、`ScannerItem.IsSelected` / `_isSelected` **已全部删除**。当初的选中态只为「右列表单跟着走」，表单搬进行内后它就没有用处了，留着只会让人以为点一下能做什么。同理**上下移也删了**（`ScannerMoveUp_Click` / `ScannerMoveDown_Click` / `MoveScanner` / `ScannerItemSet.Move` 全删）—— 顺序不再可调，落盘顺序就是建行顺序
- 「连接方式」「协议」「结束符」三个下拉框的候选项改由 `ScannerItem` 的静态数组 `ConnectionTypeOptions` / `ProtocolOptions` / `TerminatorOptions` 提供（`ItemsSource="{Binding ConnectionTypes}"` 等），首版在 `InitializeScanners()` 里给 x:Name 赋值，搬进模板后那段已删
- ⚠️ **「连接方式」「协议」只有一项但必须可展开**（用户定案 2026-11-04）：首版写了 `IsEnabled="False"`，看着像「没配好、也点不开确认」。现在两个框都是正常可点的 `FormComboBox`，只是选项各只有一个；**不要为了「反正改不了」把它灰掉** —— 以后加品牌/协议时这里就是要选的地方
- ⚠️ **只给 `ItemsSource` 不给选中项 = 空框**：`FormComboBox` 的关闭态显示 `SelectedItem`，没有就是一片空白 —— 截图里看着像「这两个字段没配」，实际是显示 bug（2026-11-04 实测）。连接方式 / 协议各补一个占位投影 `ConnectionTypeIndex` / `ProtocolIndex`（都恒返回 `0`），xaml 里 `SelectedIndex="{Binding ConnectionTypeIndex, Mode=OneWay}"`。**别写死 `SelectedIndex="0"`**：模板复用时不统一
- 展开块里全部绑定都是 **`Mode=OneWay`**（名称框 `NameText` + 左列 5 个连接参数投影 + 右列 8 个交互参数投影 = 14 个）：这些投影是 get-only，TwoWay 会报绑定错误；而处理器一律读 `box.Text`，写回本来就不发生（回填靠 `item.RefreshFields()`）
- ⚠️ **`Terminators` 的顺序必须与枚举定义顺序一致**（`CrLf`/`Cr`/`Lf`/`None` → `\r\n`/`\r`/`\n`/无），因为代码里直接 `(ScannerFrameTerminator)SelectedIndex`。中间插一项就等于全体配置错位
- **名称也是行内编辑，全程没有对话框**（2026-11-04 二次改，`Ui/ScannerEditDialog.cs` 已删除）：行里是 `TextBox x:Name="ScannerNameBox"`（`Style="{StaticResource MonitorInlineTextBox}"`、`Text="{Binding NameText, Mode=OneWay}"`），`LostFocus="ScannerName_LostFocus"` + `KeyDown="ScannerName_KeyDown"`（回车等同失焦），两者都汇到**唯一提交点** `CommitScannerName(TextBox)`
- ⚠️ **名称要有独立的 `NameText` 投影，不要复用 `Name`**：名称框必须能装下用户乱输入的内容（含空串、重名），校验通过才写模型；`Name` 仍是只读投影（日志前缀等处用它）。`CommitScannerName` 的两条校验：空 → 「扫码器名称不能为空」；`IsNameTaken(wanted, item)` → 「名称「X」已被别的扫码器使用」。失败走 `RejectScannerField` 回退，成功 `_scannerSet.Rename` + `item.RefreshFields()` + `RefreshScannerItemState` + `RefreshScannerView()`（改名会影响日志前缀与引用提示）
- ⚠️ **复用 `MonitorInlineTextBox` 时必须自己给 `Foreground`**：那个样式的 `Foreground` setter 是 `{Binding RowBrush}`（给自由监控行的 `MonitorItem` 用的），`ScannerItem` 没这个属性 → 会刷绑定错误且文字色退成默认黑。扫码器名称框显式写 `Foreground="{DynamicResource Brush.Text.Primary}"`
- 新增 = 点 `+` **直接落一行**（`ScannerRules.NextName` 取 `scanner1`/`scanner2`…），`IsExpanded` 置 true（合并后只有这一个展开标志，`IsConfigExpanded` 已随 P3.5 页删除），然后 `FocusScannerName(item)` 把焦点送进名称框（`Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)` + `ItemContainerGenerator.ContainerFromIndex` + 遍历可视树找 `ScannerNameBox`，照 `FocusGlobalVarRow` / `FindGlobalVarBox` 那套）。**不再弹「起名」对话框** —— 参数已能就地填，只剩起名一个作用就是重复劳动（用户原话见 `Brief.md` 同期记录）
- 端口失焦：空 → 存 0（表示没配）；非法 → **回退到上次有效值** + 该行「端口号」框标红（与 PLC 表单同一口径，见 §3.4）。错误归**每行一份**，落在 `ScannerItem.Problem` + `ScannerItem.ProblemField`；行上收起态的标记见 §5.9.3（「配置错误」四个字）
- 删除扫码器 → `DropScannerSession` 断开连接 + 从两个字典移除，**该台的交互配置随定义一起消失**（同一份定义）

#### 5.9.3 交互参数（合并页的右半边，P3.5 原内容）

> ⚠️ **原「数据管理 → 扫码器」那一页 2026-11-04 已整段删除**（`ScannerSegment` 分段、
> `ScannerConfigPanel`、`ScannerConfigItemsControl`、`ScannerConfigEmptyHint`、
> `ToggleScannerConfigExpand_Click`、`RefreshScannerConfigView()`、`ScannerItem.IsConfigExpanded`
> 全部下线）。下面这些**交互参数**现在住在合并页展开块的**右列**，行与列的排法见 §5.9.2。

- 右列竖排 **5 行**：触发条件表达式 / 触发请求内容 / 接收内容输出变量 / 接收超时判定（秒）/ 执行结果输出变量。**标签统一 `Width=168`、控件 `Width=200`**（「接收超时判定（秒）」框 `Width=80`）
- ⚠️ **界面标签在 2026-11-04 被用户改长过一次，代码里仍是旧字段名** —— 改文案时别顺手改模型属性，两者是两回事：

  | 界面标签（现在） | 模型字段 / 方法 | 旧界面文字 |
  |---|---|---|
  | 触发请求内容 | `TriggerText` | 触发文本 |
  | 接收内容输出变量 | `BufferVariableName` | 接收缓冲区变量 |
  | 接收超时判定（秒） | `TimeoutSeconds` | 超时判定（秒） |
  | 执行结果输出变量 | `NotifyVariableName` | 通知变量 |

  ⚠️ **校验文案仍是旧叫法**（`MainWindow.Scanner.cs` 里「接收缓冲区变量「X」不存在」/「超时判定时间需填写数字」等）：这些串只在 `ScannerItem.Problem` 里，**界面上已没有任何位置成段显示**（红字那段已删，行上只有「配置错误」四个字），所以暂时读不到；要改就说一声，一并统一成新叫法。**「执行结果输出变量」的两条文案已随本次改动改成新叫法**（因为它同时被行上判据与提示用到）
- ⚠️ **错误定位机制：哪个框错哪个框红**（用户 2026-11-04 定案，替换掉原先的一行红字明细）。三件套：
  1. `Scanners/ScannerDefinition.cs` 的 `enum ScannerField { None, Name, Ip, Port, TriggerExpression, TriggerText, BufferVariable, TimeoutSeconds, NotifyVariable }` —— 就是把 `DescribeScannerProblems` 里那一串 return 各给一个身份
  2. `MainWindow.Scanner.cs`：`DescribeScannerProblems(def, out ScannerField field)` 每个分支先赋值 `field`；`ScannerItem.ProblemField` 的 setter 里 `Raise` 掉 8 个投影（`NameError` / `IpError` / `PortError` / `TriggerExpressionError` / `TriggerTextError` / `BufferVariableError` / `TimeoutSecondsError` / `NotifyVariableError`，每个都是 `=> _problemField == ScannerField.Xxx`）。**提交失败的即时错误也要写它**：`RejectScannerField(item, box, 旧值, error, field)` 的第 5 个参数、`ScannerPort_LostFocus` 的非法分支
  3. `Ui/ScannerErrorMark.cs` 的附加属性 `IsError`（**必须是 `typeof(bool)` 的 `DependencyProperty`**）+ `MainWindow.xaml` 的 `ScannerFieldTextBox` / `ScannerNameTextBox` 两个样式，触发器 `<DataTrigger Binding="{Binding RelativeSource={RelativeSource Self}, Path=(ui:ScannerErrorMark.IsError)}" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource Brush.Danger}"/></DataTrigger>`
- ⚠️ **不能用 `Tag` 传这个标志**：`Tag` 是 `object`，`DataTrigger.Value="True"` 是字符串，与 boxed `bool` 永远不相等，触发器不亮。这就是为什么要专门写一个 `bool` 类型的附加属性
- ⚠️ **`FormTextBox` / `MonitorInlineTextBox` 的 `BorderBrush` 必须走局部值 + `TemplateBinding`**：这两份模板原先把 `BorderBrush` 写死成 `{DynamicResource Brush.Input.Border}`，**控件上设 `BorderBrush` 一律看不见**（标红完全不生效，且没有任何报错）。2026-11-04 已改成样式里给 `<Setter Property="BorderBrush">` 默认值 + 模板 `Border x:Name="Bd"` 用 `BorderBrush="{TemplateBinding BorderBrush}"`。⚠️ 改这两个样式等于改全应用所有表单框的行内框，动之前先想清楚
- ⚠️ **两个新样式必须写在 `FormTextBox` / `MonitorInlineTextBox` 之后**：`BasedOn` 走 `StaticResource`，靠文档顺序解析，写前面直接 `XamlParseException`
- ⚠️ **收起态用「配置错误」四个字，不是图标**（用户 2026-11-04 第二次定案，原话：和任务组一样显示配置错误就好了，配置错误不能启用，去掉感叹号）：直接照抄采集任务组的同款写法 —— `<StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">` 里 `CheckBox Style="{StaticResource AcqEnableSwitch}" IsChecked="{Binding EffectiveEnabled, Mode=TwoWay}" IsEnabled="{Binding CanToggleEnabled}" Click="ScannerEnabled_Click"` + 右侧 `<TextBlock Margin="8,0,0,0" Text="{Binding ConfigErrorText}" FontSize="11" Foreground="{DynamicResource Brush.Warning}" Visibility="{Binding HasConfigError, Converter={StaticResource BoolToVis}}"/>`。**用琥珀不用红**（与 §9.1 里「红=配置要改」看似冲突）：任务组当初定的口径是「红字是格子里填错了，琥珀是这东西现在不可用」，这里表达的是**不可用**，故跟任务组保持一致而不是跟感叹号保持一致
- ⚠️ **合并后启用开关搬到了行上，并且是这一页唯一的开关**：原先只有 P3.5 页有开关、P4.5 页只有四个字；合并后一套界面里「启不启用 + 为什么不能启用」都在同一行。**不要再给展开块里加第二个开关**
- ⚠️ **`EffectiveEnabled` 必须可写**（`get => Model.Enabled && !HasConfigError; set => Enabled = value;`）：`CheckBox.IsChecked` 默认 TwoWay，绑只读属性会在模板实例化时抛 `XamlParseException`「无法对只读属性进行 TwoWay 绑定」，**整个页面打不开**（采集任务组实机踩过）。`Model.Enabled` 存用户意图、`EffectiveEnabled` 永远由 get 算、**不存第二份状态**
- ⚠️ **三个 Raise 要挂在 `ScannerItem.Problem` 的 setter 里**：`HasConfigError` / `EffectiveEnabled` / `CanToggleEnabled`。`Problem` 是唯一的「重算」出口（`RefreshScannerItemState(item)` 写它，17 个调用点），挂在别处会漏。`Enabled` 的 setter 也要补 `Raise(nameof(EffectiveEnabled))` / `Raise(nameof(CanToggleEnabled))`
- ⚠️ **执行侧必须同口径**：`RunScannerTick()` 里判 `if (!item.EffectiveEnabled) continue;`（**不是** `def.Enabled`），`needsSystemValues` 也改用 `i.EffectiveEnabled`。否则界面写着「配置错误不能启用」、后台照旧按半截配置发报文。**但启动自动连接仍判 `item.Model.Enabled`**（`TryAutoConnectScanners`）—— 连接不等于触发，与「关掉启用只挡触发与收发，连接仍在」同一口径
- 「配置错误」四个字集中在 `ScannerRules.ConfigErrorText`（`Scanners/ScannerDefinition.cs`），与采集的 `AcquisitionTaskCheck.ErrorText` **刻意同值**：同一种状态在两个页面用同一句话，不再各写一份字面量
- ⚠️ **名称格仍是 `StackPanel HorizontalAlignment="Left"` + 名称限宽**（`TextBox Width="200"`），理由与当初的感叹号相同：两列 `Grid`（`*` + `Auto`）时名称会**撑满整列**，后面那段字被推到最后 —— 实测离名称文字尾部 ~1870px（2026-11-04 用 UIA 矩形量出来的）；名称要省略号就必须给 `MaxWidth`，否则 `TextTrimming` 撑满时等于不生效
- ⚠️ **标签列宽不能小于最长标签的实际宽度**：「结果帧结束符」6 个汉字在 `FontSize=14` 下需要 84px（`Width=80` 时字直接贴在控件边上没有间隙，首版截图一眼看出）；现在一律 **168**，见 §5.9.2 那条「标签列宽 168 是用户把标签改长之后定的」
- `DescribeScannerProblems(def, out ScannerField field)` 返回**第一条**待改项（不是全部），顺序固定：IP 空 → 端口越界 → 触发表达式空 → 表达式解析失败 → 名字不存在 → 触发文本 → 超时秒数 → 缓冲区变量 → 执行结果输出变量。它由 `RefreshScannerItemState(item)` 调（`InitializeScanners()` 载入后先刷一遍、每次提交后刷一遍），同时写 `ScannerItem.Problem`（文案）与 `ProblemField`（哪一栏）
- 三个框挂了补全：触发表达式走 `CompletionSource.ForExpression`，接收内容输出变量 / 执行结果输出变量走 `CompletionSource.ForMonitorName`；`Loaded` 里先 `TryGetValue` 去重再 `Add`（行模板会重复 Loaded）
- 各输入框**失焦才提交**，失败一律 `RejectScannerField(item, box, 旧值, error, field)`（回退文本框 + 写该行 `Problem` 与 `ProblemField`，对应框随即变红框），成功才写模型 + `Save()` + `RefreshScannerItemState(item)`。⚠️ 展开块里的框绑的是**只读投影**（`NameText` + `TriggerExpressionText` 等 10 个），故提交后必须 `item.RefreshFields()` 显式通知 —— 不再有 `_scannerFormSyncing` 那种回填标志（**那个字段已删除**），因为投影是单向的，回填不会反向触发提交

#### 5.9.4 校验口径（写错一个现场就查不出来）

- **接收缓冲区变量只能是 STRING**（`ValidateScannerBufferVariable`）：`GlobalVariableStore` 里不存在 → 「不存在」；类型不是 STRING → 「只能是 STRING，当前是 X」。留空合法（= 不写缓冲区）
- **执行结果输出变量只接受 INT / UINT / DINT / UDINT**（`ValidateScannerNotifyVariable`）。留空合法（= 不写结果）
- ⚠️ **写入值不再可配，固定 1 / 2**（2026-11-04 定）：`ScannerRules.ResultSuccess = "1"`、`ResultFailure = "2"`，说明文字 `ResultValueHint` 挂在界面上（`ScannerItem.ResultValueHint` 转发 → XAML 里一行 `FontSize=11` / `Brush.Text.Muted` 的小字）。原先的 `SuccessValue` / `TimeoutValue` / `TimeoutText` 三个字段与 `ValidateScannerValueText` 的预校验**已全部删除** —— 值不再是用户输入，就没有可校验的东西
- **空串算正常返回**（超时才算异常）：扫码器回一帧空内容也是合法结果，缓冲区写空串 + 执行结果输出变量写 `1`
- **结果超长按声明长度截断**，不算失败（`EncodeStringFixed` 先截字符再补 `0x00`）
- ⚠️ **异常档不碰缓冲区**：`ApplyScannerTimeoutAsync` **只**写执行结果输出变量（写 `2`），缓冲区一个字节都不动。原先「超时写入文本为空则保留旧值」那套 `NonEmptyOrNull` 已随字段一起删除
- 变量被多处引用时只提示不拦（`ScannerRules.CountReferences`，`ReferenceEquals` 排除自身），黄字 `Brush.Warning`「变量已在别处被引用」

#### 5.9.5 触发与收发

- `_scannerTimer` 是 50ms 的 `DispatcherTimer`（`DispatcherPriority.Background`），**不随页面可见性停**（和采集的 `_acqTimer` 同一口径）。没有扫码器时第一行就早退
- 同一节拍还负责**连接维护**（2026-11-05）：每台先过一遍 `EnsureScannerConnecting`，**不等触发沿** —— 启用、未连接、退避到期就补一次。早先只在触发沿调它，遇到「常真」表达式（如 `sys_True`）只有一个上升沿，首连失败后就再也不会重试（见 §5.9.7）
- ⚠️ **`state.InFlight` 的 `continue` 必须写在 `ShouldFire` 之前**：`ShouldFire` 有副作用（把 `wasTrue` 置为当前值），执行中忽略新的上升沿时若还调了它，那一拍的边沿就被吞掉
- ⚠️ **`ShouldFire` 的 `ref` 参数不能直接传自动属性**：`ScannerRuntimeState.WasConditionTrue` 是 `{ get; set; }`，`ref state.WasConditionTrue` 编不过（`CS0206`）。过一手局部变量再写回：
  ```csharp
  bool wasTrue = state.WasConditionTrue;
  if (!ScannerTrigger.ShouldFire(condition, ref wasTrue)) { continue; }
  state.WasConditionTrue = wasTrue;
  ```
- 表达式按字符串缓存（`CompiledSource` vs `TriggerExpression` 比对），解析失败退化到 `CompiledExpression.Empty`（恒 false），不每拍重解析
- 收发：`DiscardPendingAsync()` 先扔掉非响应期的主动数据 → `SendAsync(ASCII(TriggerText))` **原样发、不补结束符** → `ReadFrameAsync(TerminatorBytes, timeout)` → `ApplyScannerSuccessAsync`
- ⚠️ **`ReadFrameAsync` 只在「已收到半帧」时才关连接**（2026-11-11 修正）：
  `catch` 里判 `payload.Count > 0` 才 `Dispose()`，**一个字节都没收到就不关**。
  - 为什么：**超时 ≠ 断开**。对端只是这一轮没回话（联调时故意不回包很常见），
    TCP 明明是通的，旧写法却把它判成断线 → 1 秒退避重连 → 日志紧跟一条「已连接」，
    看着像连接不稳。实测：前两轮零字节超时连接全程保持。
  - 半帧仍然必须断：流里剩着不完整字节，下一轮会把旧帧尾巴当新帧开头（失步）。
    实测半帧场景仍能正确断开。
  - 与「**收帧绝不取消在途 `ReadAsync`**」是同一条道理的两面（见 §5.9.5）：
    取消 `ReadAsync` 会让套接字进入断开态，同样会造成「连上 → 收一次 → 断开 → 重连」。
  - ⚠️ **零字节超时必须有兜底**（`ZeroByteTimeoutLimit = 3`）：若对端已半开
    （拔网线 / 断电 / 老交换机没发 RST），TCP 层不会报错 —— `DataAvailable` 恒 false、
    `WriteAsync` 照样成功，于是每轮白等一个超时、**永不自愈**。
    故连续 3 次零字节超时就主动 `Dispose()`，让上层的退避重连重建连接；
    **收到一帧或新建连接都把计数清零**。
    实测：no-reply 前 2 轮保持、第 3 轮断开；正常回包 5 轮全程保持。
  - 调用侧 `MainWindow.Scanner.cs` 的 `!client.IsConnected` 判据**不用改** ——
    它现在正好对应「只有真断才重连」（含上面那条兜底）。
- ⚠️ **空结束符走 100ms 静默窗口**（`ScannerTcpClient.SilenceInterval`），且 `payload.Count > 0` 才起静默计时 —— 否则连上后对方一个字都没发时，第一个 100ms 就把空帧当结果交付了
- ⚠️⚠️ **静默收帧绝不能用「取消在途 `ReadAsync`」来判帧结尾**（2026-11-05 真机实测，代价极大）：
  取消 `NetworkStream.ReadAsync` 会让**套接字进入断开态** —— 每成功收帧一次，`client.Connected` 立刻变 `false`，
  下一轮就发不出去，表现为「连上 → 收一次 → 断开 → 重连」的无限循环，日志里是
  `The operation is not allowed on non-connected sockets`。
  **正确写法**：`DataAvailable` 轮询（`PollInterval` = 5ms）判断有没有数据、`Stopwatch` 判断静默间隔是否到，
  `ReadAsync` **只在 `DataAvailable` 为真之后调用且不传任何会取消的 token**。
  验证记录：`.tmp/ScanProbe/CancelProbe`（复现：收帧成功后 `Connected=False`）与
  `.tmp/ScanProbe/DataAvailProbe`（修复：连续 5 轮 `Connected=True`）。
  **同一个坑别在别的 TCP 路径上再踩一次** —— `ModbusTcpClient` 走定长 `ReadExactAsync`，不涉及取消，暂时安全
- `n == 0` 抛 `new IOException("连接被对方关闭")`，走「通讯中断」分支而不是超时分支
- 异常分流：`OperationCanceledException` → 异常档（`ApplyScannerTimeoutAsync`：只把执行结果输出变量写 `2`）；其它异常 → 记 `扫码器「X」通讯中断：<原因>`（`Disconnected` 级别）+ `NotifyScannerLost` + 异常档
- ⚠️ **异常档不写接收内容输出变量**：上一轮收到的结果仍然有效，覆盖成空串或占位文本只会丢信息（原先的「超时写入文本」及其 `NonEmptyOrNull` 兜底已随字段删除）
- `finally { state.InFlight = false; }` —— 成功与失败都必须放开互斥，漏了这台扫码器从此不再触发

#### 5.9.6 写变量（口径 B）

`ApplyScannerVariableAsync(variable, text, label)` 的顺序不能动：

1. `variable == null || text == null` → return（没配就不写）
2. `ValueCodec.TryEncodeRegisters` 失败 → **只记 Warning 日志，不走超时档**（这是配置问题，不是扫码器失败）
3. `variable.Value = ValueCodec.FormatRegisters(...)` + **`variable.Numeric = ValueCodec.DecodeNumber(...)`** —— 两个都要设，`Numeric` 供实时卡片十进制显示，不能从 `Value` 反解
4. `!variable.HasAddress` → return（内存变量，到此为止）
5. **PLC 未连接 → return（口径 B）**：扫码器照常收发、结果进内存、只是不回写 PLC。扫码器**不**以 PLC 连接为前置
6. `AcquisitionWrite.TryBuild`（`CannotAddress` → 只回填内存 / `Failed` 记 Warning）→ `WriteToDeviceAsync`

#### 5.9.7 连接生命周期（每台独立）

- 启动 `TryAutoConnectScanners()`：遍历 `item.Model.Enabled` 且有可用端点的，`_ = TryConnectScannerAsync(...)` **不 await**（16 台串行等会拖到几十秒）。**「启用」就是扫码器的自动连接开关**，没有全局开关（口径见 §7.2）
- `HasUsableScannerEndpoint` = IP 非空 && 端口 1~65535
- `ConnectAsync` 的**本地端点传 `null`**（不像 PLC 会绑网卡）：扫码枪都在现场同一网段，绑错网卡反而制造故障
- `EnsureScannerConnecting`：`Target != null`（**曾经连上过**）→ 走退避重连；否则立刻首连。两条路都先过 `ScannerSession.CanAttemptNow` 这一道闸门
- ⚠️ **「从未连上」也受退避约束**（2026-11-05 方案 B，用户定案）：早先只有「曾经连上过」才进退避，首次失败后 `Target` 仍是 `null`，下一拍立刻再连 —— 间隔只等于 3 秒连接超时，日志里「连接超时」一行接一行（16 台更甚）。现在 `TryConnectScannerAsync` 失败时调 `ScannerSession.RegisterConnectFailure`（失败次数加一、把 `NextAttemptAt` 推到 `ReconnectPolicy.DelayForAttempt` 之后）；`MarkConnected` / `ForgetTarget` / **改 IP 或端口**都会 `ResetBackoff` 清零。故首连失败后按 1→2→4→5s 重试、把地址改对立刻重试，不再有 3 秒一次的握手风暴
- 重连复用 `Polling/ReconnectPolicy.DelayForAttempt`（1→2→4→5s，封顶 5s），与 PLC 同一张表
- ⚠️ **重连尝试不写日志**（2026-11-05 方案 B，对齐 PLC 的 ADR-14）：断线那一刻已有「通讯中断」、连上时记「已连接」。首连失败只在**本轮第一次**记一行（判据 `session.Attempt == 0`，文案只写失败原因，**不提示「将自动重试」**），之后静默。原先每次尝试记一行「正在重连 … Ns」，16 台并发时会把运行日志冲干净
- 状态**只进运行日志、不动底栏**：底栏是 PLC 的连接状态，扫码器写进去会让人以为 PLC 断了。连上用 `Connected`（绿）、断开用 `Disconnected`（红）；**重连过程不写日志**（2026-11-05 方案 B），故不再有「重连中」这一档
- `NotifyScannerLost(item, client)` 先 `ReferenceEquals(session.Client, client)` 校验 —— 旧连接的失联回调不能把新连接拆掉
- `OnClosed` → `ShutdownScanners()`：停节拍 + 逐台 `CancelConnect` / `CancelReconnect` / `ReleaseClient` / `ForgetTarget`，再清两个字典

#### 5.9.8 引用刷新

`MainWindow.GlobalVar.cs` 的 `RefreshAfterVariableChange()` 里调 `RefreshScannerReferenceHints()`，**必须在 `if (_monitorSet.Items.Count == 0) return;` 之前** —— 否则只配了扫码器、没配监控项时，改完全局变量名称，扫码器那边的「变量不存在」提示与红框不会更新。

#### 5.9.9 触发测试（2026-11-08）

右列「触发请求内容」输入框**右边**一个「测试触发」按钮：按当前配置当场发一次触发，
把收到的内容用 `ThemeMessageDialog.Info` 弹框显示（`ScannerTestTrigger_Click`）。

**三条定案，改之前先想清楚**：

- ⚠️ **刻意不影响任何变量**（用户 2026-11-08 定案）：不写「接收内容输出变量」、也不写「执行结果输出变量」。
  这是**调试动作**，不是业务数据 —— 点一下就把执行结果写成 1，等于让「测一下」冒充
  「真的跑完了一轮扫描」，采集任务与状态机会据此做出错误判断。
- ⚠️ **不写运行日志**：与登录 / 注销同一取向 —— 界面调试动作不是设备状态；
  每测一次都塞一行，很快就把真正的连接事件挤掉。结果只在弹框里，看完即走。
- ⚠️ **弹框正文只有两行：收到内容 / 字节数**（2026-11-08 用户定案删除「未写入变量」那行）：
  本功能**永远不写变量**，那行必然是「未写入变量」—— 一条永远相同、对使用者没有信息量的状态说明，只是噪音。
  `BuildSuccessBody(raw, byteCount)` 也据此**不再接收** `wroteBuffer` / `bufferVariable` 两个参数。
- **复用同一条通讯路径**（丢弃残留 → 原样发触发文本 → 按结束符收一帧），
  所以「测试通过」足以说明生产路径也是通的；差别只在结果去处。

**实现要点**：

- 文案拼装在 `Scanners/ScannerTestResult.cs`（**纯逻辑**），`.tmp/ScannerTestCheck` **35 项**断言
- ⚠️ **收到内容必须转义再显示**：真实扫码结果几乎都带 `\r\n`，原样塞进 `TextBlock` 会让
  本来一行的结果变两行，用户分不清「内容真有换行」还是「弹框自己折的行」。
  `FormatReceived` 把 `\r`/`\n`/`\t` 转成字面量、其余控制字符转 `\xNN`，超 500 字符截断并标注总长
- **没连上就当场连一次**（测试的语义是「现在试一下」，不该受自动重连的退避节流）；
  这种临时连接用完即弃（`borrowed` 标志 + `finally` 里 Dispose），**不写进 session** ——
  否则下一轮会把它当「正式连接」继续用，与 session 状态脱节
- **点按钮前先把编辑中的框提交掉**：用户很可能刚敲完触发文本就来点测试，
  而那个框是「失焦提交」，此时框里是新内容、模型里还是旧的。用 `RaiseEvent(LostFocusEvent)` 走正常路径
- ⚠️ **布局：`StackPanel` + 定宽输入框，标签收窄到 140**（2026-11-08 二次定案）：
  最初标签 168 + 输入框 200 定宽，加按钮后被推出可视区、**被裁成「测试」两个字**（实测）。
  先试过 `DockPanel` 让按钮占右侧、输入框吃剩余 —— 按钮能显示了，但**输入框变成 236 DIP、与其余行的 200 不再等宽**，
  用户要求「还原到原来的长度以保持对齐」，故改成：**把标签从 168 收窄到 140**（两列共省 56 DIP，够放按钮），
  行的结构仍是 `StackPanel` + 定宽 200，**五个输入框左右边缘因此精确对齐**（UIA 实测 x 全为同一值、宽全 300 px）。
  按钮**不写 `Width`**，宽度由内容自适应。
  ⚠️ 改回去之前先量：右列可用宽 = 内容区宽 − 左列 340 − 竖线 − 间距；触发行需要 `140 + 200 + 8 + 按钮宽`。
  在 1536 px 宽（150% DPI）窗口下余量约 13 DIP，**再往右加任何东西都会溢出**

**验证**：`.tmp/ScannerTestCheck` 35 项；UIA 端到端两条 —— 成功（假扫码器回 `EDG20260907123\r\n` → 弹框显示
「收到内容：EDG20260907123\r\n / 字节数：14」，**只有这两行**）、失败（端口无人监听 → 「触发失败：目标拒绝连接」；
不应答 → 「触发失败：服务端响应超时 / 等待超时：5 秒」）。

此外**顺手修掉一处既有笔误**：`RunScannerExchangeAsync` 里 `if (client == null || !client.IsConnected)`
被重复写了两遍（多套一层花括号，行为等价但明显是复制粘贴事故），已删掉外层那对。

## 6. 心跳与连接生命周期

- `ModbusHeartbeat.RunAsync` 每 100ms 检查；距上次有效响应满 **1s** 时以 FC01 读**单点线圈**，只读不写
- **心跳地址 = 驱动地址表里「地址最小」的线圈区 `StartAddress`**（信捷 M 区 / 汇川 M 区均为 **0**）。按 `Area` 筛选后 `OrderBy(StartAddress)`，不依赖数组顺序；无需任何配置，与是否导入 IO 表无关
- 地址表里没有线圈区时提示「未启用心跳」并 return（用 `FirstOrDefault` 而非 `First`，否则抛出的异常会被上层当成通讯故障并断开连接）。正常驱动不会出现
- 合法异常响应抛 `ModbusResponseException` → **保持连接**并告警（码 1/2 提示配置无效，其余提示响应异常），后续心跳成功即清除。**IO 轮询自 2026-10-16 起同样不断开**（见 §5）；两条支路的差别只剩阈值与呈现：心跳每次异常即告警并去重，IO 累计**连续 5 次**才记一条
- 读取超时 2s；空闲失联通常**约 3 秒**检测到（1s 空闲 + 2s 超时 + 调度延迟），不是严格 3s 上限
- ✅ **2026-11-18 真机实测过**（信捷 XD @`192.168.1.66:502`，`.tmp/HeartbeatProbe` 抓帧代理，11 项断言全通过）：
  空闲期心跳为 FC01 / M0 / 数量 1、间隔实测 **1100 ms**（100ms 的 `PeriodicTimer` 粒度决定命中窗口
  1000～1100 ms，**上沿属正常不是缺陷**）；**轮询期间心跳帧数为 0**（「正常应答时被设计性省略」成立）；
  停采后约 900 ms 自动恢复。证据见 `Design.md` §10.44。⚠️ 想再验一次别只看日志 ——
  心跳被省略时**本来就无痕**，「发了没记」与「没发」在应用内无法区分，必须抓帧
- `RunConnectionTasksAsync` 启动时把**连接与驱动快照**传给两个任务，所有回 UI 回调先过 `IsCurrentConnection`（未关窗 && token 未取消 && 仍连着 && 是同一个客户端实例 —— 判定集中在 `ConnectionSession.IsCurrent`）→ 旧连接的后台回调无法误操作新连接
- `StopPolling` 只取消并置 null，**释放交给任务自身 `finally`**（确认自己仍是当前连接后）；`OnClosed` 统一取消连接尝试、停任务并释放
- **连接的身份与生命周期状态**（当前客户端 / 是否连着 / 期望目标 / 在途取消源 / 重连计数）在 `Polling/ConnectionSession.cs`，**async 编排留在窗口** —— 等待循环里逐秒刷状态栏、锁表单、改按钮文案全是界面驱动的东西。两种「断开」语义也在那里落了地：`ForgetTarget()` = 用户主动断开（目标清掉 → 不重连），只 `ReleaseClient()` = 异常断开（目标留着 → 才能重连）

### 5.10 通讯监控（P2.4，设备监控页第五分段，已实现 2026-11-26）

需求见 `Brief.md` P2.4，取舍见 `Design.md` ADR-45 / ADR-46，验证见 `Design.md` §10.46。实现分三层：

| 层 | 文件 | 职责 |
|---|---|---|
| 抓包 | `Monitoring/TrafficTrace.cs`（无 WPF） | 目标身份 / 方向 / 环形缓冲 1000 条 / 全局 `TrafficTap`（只在「选中目标 + 可见」时入缓冲） |
| 呈现 | `Ui/TrafficMonitorPresenter.cs` | 200ms 增量刷新 / 目标下拉重建 / HEX-TEXT / 跟随滚底 / 清空 |
| 接线 | `MainWindow.Traffic.cs` | `BuildTrafficTargets`（每拍按状态重算）+ 三个控件处理器 |

- 抓包点（**只用已经算好的原始字节**，不另拼一遍帧）：
  - TCP：`ModbusTcpClient.RequestAsync` 的 `frame`（发送）与 `header + body`（接收，拼回完整 MBAP）
  - RTU：`Crc16.Append` 后的帧（发送）与 `ReadFrame` 返回的完整帧（接收，**在 `ValidateFrame` 之前**，坏 CRC 也能看到）
  - 扫码器：`ScannerTcpClient.SendAsync` 入参 / `ReadFrameAsync` 收满的帧（补回被剥掉的结束符）；`TraceOwner` 由 `TryConnectScannerAsync` 设
  - HTTP：`RunReportTaskAsync` 的请求 JSON / 响应状态码与响应体（响应体**只在抓包时读**）
- ⚠️ 热路径：`TrafficTap.Frame` 先两次 volatile 读早退；回调里只写环形缓冲，不格式化、不碰控件。
- 目标列表**只列已建立**连接：PLC / 每台已连接扫码器 / HTTP（HTTP 判据 = `ThemeManager.MesUrl` 非空）。
  **每 200ms 重算**，不在连接生命周期散落调用点；重建下拉框时要罩 `_rebuilding` 挡 `SelectionChanged`。
- **重进分段必须显式补一次 `SelectTarget`**：目标列表可能没变化、`RefreshTargets` 会早退，而离开时已
  `Deactivate`；漏了就是「下拉框选中、缓冲一直空」（2026-11-26 实测踩到）。
- 只抓选中的那一个目标；换目标 / 离开分段 / 离开设备监控页都清空缓冲，回来从头记。
- 缓冲 1000 条、自动滚底（用户翻上去就不跟随，与运行日志同口径）、清空按钮、空缓冲留白、不落盘。
- 每行接收靠左、发送靠右，各带 `[HH:MM:SS]` 前缀；HEX 按字节空格分隔、TEXT 按 UTF-8 直出。
  **HTTP 目标隐藏 HEX/TEXT 页签并强制 TEXT**（文本协议不做 HEX）。
- `IoView_Checked` 维护 `_trafficViewActive`；`PollSnapshot.TrafficViewActive` 在 `PollOnceAsync` 里
  **早退**，否则会落到「读当前页 IO 点位」那条分支（本分段一选中前四个分段都不可见）。
### 6.1 自动重连（P6.3）

| 项 | 行为 |
|---|---|
| 触发 | **仅「异常断开」**（读超时 / 对端关闭 / 帧校验失败等）。**用户主动断开、首次连接失败均不触发** |
| 退避 | `ReconnectPolicy.DelaysSeconds = { 1, 2, 4, 5 }`，封顶 **5s**（`DelayForAttempt` 是纯函数，越界取表尾）；连接成功清零计数 |
| UI | 重连期间**表单保持锁定**（参数已快照，改了不生效，避免误导）；按钮变「取消重连」；底栏逐秒显示 `正在重连 … Ns（第 M 次）`（**不带 ip:端口**，目标参数在右侧 `StatusBarDetail`） |
| 日志 | **重连尝试不写日志**（日志只记 5 种设备状态，ADR-14）；连上后记一条常规「PLC已连接：ip:端口」 |

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
| 已连接 | `PLC已连接：ip:端口` | 绿 `#0B7A0B` |
| 已断开 | `PLC已断开：<原因>`（用户主动断开与异常断开同式） | 红 `#C42B1C` |
| 连接失败 | `PLC连接失败：ip:端口 <原因>` | 红 |
| 通讯异常 | `PLC通讯异常：<原因>`（**仅当连接仍在**，对端回合法异常码） | 琥珀 `#B26A00` |
| 通讯恢复 | `PLC通讯恢复` | 绿 |

- ⚠️ **第 6 条例外：`PLC自动重连已停止：<原因>`**（红，2026-11-09 加）。它与上表五条的
  性质不同 —— 上表是**连接状态变化**，这条是**重连循环被预期外异常打断**：
  `ReconnectLoopAsync` 是 fire-and-forget（`_ = ReconnectLoopAsync(...)`），不接住异常就
  没有任何地方观察它，表现为「自动重连静默停摆 + 按钮停在取消重连 + 表单锁着」，而日志
  一个字都没有。故补了 `catch (Exception ex)`：记这一条、把按钮/表单恢复成「连接」可用态，
  **不原地重试**（异常来自代码缺陷而非通讯状况，重试只会刷屏）。
  ⚠️ 与 ADR-14「重连尝试不写日志」不冲突：那条管**尝试失败**（常规现象），这条管**循环本身挂了**。

- ⚠️ **日志文案必须带设备前缀**（2026-11-06 用户定案）：PLC 五条带 `PLC`，扫码器自带 `扫码器「X」`。
  日志卡里两种连接混排，不带前缀看不出是谁。**底栏不带**（底栏只表示 PLC 一条连接）。
  例外：采集任务组的活动日志（`LogTaskActivity`）按任务组报，不属于连接状态

- 格式 `[yyyy-MM-dd HH:mm:ss] 消息`；**空日志不显示占位文字**；**不记**关窗
- 仅内存保留最近 `DeviceLogPresenter.DefaultMaxEntries`(200) 条，超出丢最旧、不落盘，可一键清空（上限与裁剪在 `Ui/DeviceLogPresenter.cs`）
- **边界**：「完全无响应」不产生「通讯异常」—— 由 `NotifyConnectionLost` 记红色「已断开」，**一次断线只有一条日志**
- 原因经 `ClassifyDisconnectReason` 归一为简短中文（Socket 原文 →「网络不可达」，Modbus 帧错误 →「响应帧校验失败」），未命中原样保留
- 日志区套一层内嵌框线 `Border`（`#C8C8C8` 1px、圆角 4）；时间列用 `SharedSizeGroup="DeviceLogTime"` 跨行共享宽度（**否则比例字宽会让消息左边界抖动**），`DeviceLogList` 上须有 `Grid.IsSharedSizeScope="True"`

### 7.1 日志落盘（P5.2）

同一条日志**既进界面又进文件**，落盘挂在 `Ui/DeviceLogPresenter.Append` —— 窗口的 `AppendDeviceLog` 是 5 种设备状态的唯一出口，7 个调用点零改动。

```
[2026-10-15 11:41:45] [INFO] PLC已连接：192.168.1.66:502
[2026-10-15 11:41:58] [ERROR] PLC已断开：用户主动断开
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
- 独立验证工程 `LoggingCheck`（**54 项**断言）覆盖行格式 / 跨天 / 续写 / 换目录 / 开关 / 容错 / **断开原因归类**，改动写入器或归类规则后跑它
- 独立验证工程 `DeviceLogCheck`（`.tmp/`，**23 项**断言）覆盖 `DeviceLogPresenter` 的内存上限裁剪 / 级别映射 / **共用时间戳** / 清空 / 换主题重建；改动日志编排后跑它（**未提交**，随用随弃，手法同 `PollerCheck`：链接源码 + 打桩 WPF 类型）
- ⚠️ **在 DSH 下跑它时先改 TEMP**：`%TEMP%` 上挂着沙箱 ACE，子进程（`dotnet run`）建目录会被拒（`UnauthorizedAccessException`），而 pwsh 自己建得出来，故报错看着莫名其妙。先 `$env:TEMP = "$PWD\.tmp\lc-temp"` 再跑

### 7.2 自动连接（2026-11-05 已搬到「设备连接 → PLC连接」页）

> ⚠️ **设置页那张「自动连接」卡已删除**（2026-11-05 用户定案）。
> 原因：卡片叫「自动连接」但只管 PLC，用户会以为扫码器也归它管 —— 属误导。
> 现在分两处，各管各的：
>
> | 谁 | 开关在哪 | 字段 |
> |---|---|---|
> | **PLC** | 设备连接 → PLC连接 → 表单末行「自动连接」（`AutoConnectPlcToggle`，`ToggleSwitch`，**默认打开**） | `app-settings.json` → `AutoConnectPlc`（`bool`，缺字段按默认 `true`） |
> | **扫码器** | **每行自己行上的「启用」开关**，没有全局开关 | `scanner.json` → `Enabled`（每台一份） |

**PLC 侧口径不变**：

- 开关文案在 `AutoConnectPlcToggle_Changed` / `SyncAutoConnectToggle` 里由代码切 —— 与日志保存开关同一个坑：模板触发器里的 `Setter` 赢不过局部 `Content`
- **启动时机**：`Window_Loaded` 末尾用 `Dispatcher.InvokeAsync(TryAutoConnect, DispatcherPriority.ApplicationIdle)` ——
  先让首帧画出来，再发起连接（连接是异步的，失败原因要能落到界面上）
- `TryAutoConnect` 的跳过条件：开关关 / 窗口已关 / 已连接或正在连 / **配置不全**（串口模式未选端口、以太网模式 IP 为空）。
  ⚠️ **串口也是一等公民**（2026-11-18 起）：早先「不是以太网模式就跳过」的分支已删除
- **失败不重试**：与手动连接一致，首次连接失败交还控制权给用户
- ⚠️ **不写设备日志**（需求）：自动连接是后台行为，成功由 `TryConnectAsync` 记「已连接」、失败记「连接失败」，不必再加一条

**扫码器侧（2026-11-05 新口径）**：

- ⚠️ **「启用」是扫码器唯一的自动连接开关**：`TryAutoConnectScanners()` 只连 `item.Model.Enabled` 且端点可用的；
  `EnsureScannerConnecting()` 里也加了 `!item.Model.Enabled` 早退 —— 停用的**任何路径都不会自动连**
- **连不上就自愈，但受退避约束**：首次连接失败后按 1→2→4→5s 重试（`ScannerSession.CanAttemptNow` /
  `RegisterConnectFailure`），**重试不写日志**；只在第一次失败记一行失败原因（不提示重试）、连上时记「已连接」。
  与 PLC「首次失败不重试」故意不同 —— 现场扫码枪晚上断电，早上要自己回来（§5.9.7）
- ⚠️ **一度加过全局的「自动连接扫码器」开关，已废弃**：它与「启动连不连」和「运行期要不要连」两种语义纠缠不清 ——
  关掉后仍会被 `EnsureScannerConnecting` 连起来，开关形同虚设。
  每行自己的「启用」语义干净（关掉 = 这台不触发、不收发、也不自动连），还能按台区分
- `ScannerEnabled_Click` 关掉时要同时做三件事：`CancelReconnect` + `ReleaseClient` + **`ForgetTarget`**。
  只用 `ReleaseClient` 会保留目标，下一拍 `EnsureScannerConnecting` 又按「异常断开自动重连」把它连回来
- ⚠️ 该处理器里**必须显式 `item.Enabled = box.IsChecked == true`**，不能指望 TwoWay 绑定：
  绑定目标是 `EffectiveEnabled`（`Model.Enabled && !HasConfigError`），`Click` 早于源更新，
  此时读 `item.Enabled` 拿到旧值会把「关掉」当成「打开」、反而去连一次（实测：点关后连接数不降反升）


### 7.3 项目信息（P5.3）

设置页第五张卡，显示 **名称 / 版本 / 构建日期 / 运行环境**。全是常量，在 `Window_Loaded` 里 `RefreshProjectInfo()` 填一次。

| 项 | 来源 |
|---|---|
| 名称 | csproj `<Product>` → `AssemblyProductAttribute` |
| 版本 | csproj `<VersionPrefix>` + `VersionSuffix` → `AssemblyInformationalVersion`（**显示时砍掉 `+` 之后的构建元数据**，前缀 `V` 由界面加） |
| 构建日期 | csproj `WriteBuildDate` 目标生成的 `BuildInfo.BuildDate` 常量 |
| 运行环境 | `Environment.Version` + `Environment.OSVersion` + `Environment.Is64BitProcess` |

**几条容易踩的**：

- ⚠️ **版本号手写在 csproj，不自动递增**：自动递增会把「版本号」变成构建次数的函数，而现场排障要的是「这个 exe 是哪一版」，不是「编译过几次」。提版本由人决定（改 `<VersionPrefix>` 一处）
- ⚠️ **`-debug` 后缀由构建配置推导，不要硬编码**：`VersionSuffix` 只在 `Configuration == Debug` 时设 `debug`，SDK 自动合成 `<VersionPrefix>-debug`。硬编码意味着每次切 Release 都得记得删掉，忘一次就会把调试版当正式版发出去（与「构建日期不手写」是同一条道理：能推导的就别手写）。实测（1.0.0）：Debug 下 `ProductVersion = 1.0.0-debug+<sha>`，Release 下为干净的 `1.0.0`
- ⚠️ **`AssemblyVersion` / `FileVersion` 固定不带后缀**（两配置都是 `1.0.0.0`）：程序集身份必须稳定，否则换一次配置就让引用方全部重编 —— 与「日期用独立目标而不覆盖 `<Version>`」同一个考虑
- ⚠️ **构建日期必须编译期生成**：手写必然忘记更新；用**文件时间**也不行 —— 复制/解压/杀软扫描都会改动它，与"代码什么时候编的"无关
- ⚠️ **日期用独立目标 + 独立常量，不要覆盖 `<Version>`**：把日期塞进 `Version` 会让程序集版本每次编译都变，进而让增量构建失效（引用方全部重编）
- ⚠️ **`InformationalVersion` 会被 SDK 自动追加 `+<git sha>`**（实测形如 `1.0.0-debug+11d444a…`），显示前必须砍掉 `+` 之后的部分，否则用户看到一长串哈希
- 生成的 `BuildInfo.g.cs` 落在 `obj/` 下，**不要手改、也不要提交**（已在 `.gitignore` 覆盖范围内）
- 四行标签用 `SharedSizeGroup="InfoLabel"` 对齐；**末行不留底边距**（否则卡片底部多出一段空白）

### 7.4 开机自启动（P5.5，2026-11-08）

设置页第三张卡（顺序：应用标题 → 显示外观 → **开机自启动** → 日志保存 → 项目信息），右边是 `AutoStartToggle`（`ToggleSwitch`）。

**实现位置**：`Startup/AutoStartCommand.cs`（**纯逻辑**，无 WPF/无注册表）+ `Startup/AutoStartRegistry.cs`（只做注册表 IO）。
窗口侧三个处理器在 `MainWindow.xaml.cs`：`AutoStartToggle_Changed` / `SyncAutoStartToggle` / `RefreshAutoStartStatus`。

| 项 | 值 |
|---|---|
| 位置 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（**当前用户，不需要管理员**） |
| 值名 | `DSH_Modbus采集工具`（与产品名一致，任务管理器「启动应用」页显示的就是它） |
| 值 | `"<exe 完整路径>"` —— **带引号**（路径含空格），**不带任何参数** |

**几条容易踩的**：

- ⚠️ **持久化不写 `app-settings.json`**：真值只有一个，就是注册表。另存一份必然漂移 ——
  用户在任务管理器「启动应用」页关掉它，json 里还写着「开」，下次启动界面就在撒谎。
  故开关每次切换都是「写注册表 → 读回来 → 按读回结果回填」。
- ⚠️ **「已启用」= 已登记 且 未被系统禁用 且 指向当前 exe**，三条缺一不可（`AutoStartRegistry.IsEnabled`）。
  对应三种真实情形：没登记 / 被「启动应用」页单独禁用 / 登记的是旧路径（exe 被移动或换了发布目录）。
  只判其一，开关就会撒谎。
- ⚠️ **系统禁用记录的首字节判据是 `& 0x01`，不是「≥ 3」**：实际出现的四个值是
  `0x02`/`0x06` = 启用、`0x03`/`0x07` = 禁用，按数值大小比较会把 `0x06` 这个**启用**态误判成禁用。
  这个错误是 `.tmp/AutoStartCheck` 断言抓出来的（38 项），别改回去。
- ⚠️ **打开开关时必须同时清掉 `StartupApproved` 的禁用记录**（写一条首字节 `0x02` 的 12 字节值）：
  否则「Run 里有值 + 设置页开关是开的 + 开机就是不起来」，这是本功能最难向用户解释的状态。
- **关闭只删 `Run` 里的值**，不动 `StartupApproved`（删不删都不影响结果，那条记录带着「何时被禁用」的时间戳，
  留着与用户在「启动应用」页手动关掉后的系统状态一致）。值本来不存在时不算失败（幂等）。
- **`ExecutablePath` 优先用 `Environment.ProcessPath`**，仅当它的文件名等于「程序集名 + .exe」时才采信；
  否则退回 `AppContext.BaseDirectory + 程序集名 + .exe` —— 前者在 `dotnet exec` 一类宿主下会返回宿主的 exe，
  照抄就会把 `dotnet.exe` 写进自启项。（本程序是 apphost 形态，正常路径走不到回退分支。）
- **`_revertingAutoStart` 闸门**：`SyncAutoStartToggle(raiseEvents: false)` 程序化回填时挡住
  `Checked` 事件再写一次注册表；写注册表**失败**时也走这条（否则「回填 → 再触发 → 再失败」）。
  与 `_restoringNicSelection` / `_syncingByteOrderCombos` 同一手法。
- **进设置页时重读一次**（`Nav_Checked` 的 `settingsVisible` 分支）：用户很可能在**本程序之外**
  用任务管理器改掉了它，程序收不到任何通知，只有重读才不会显示旧状态。
- **提示行只在需要用户处理时显示**（正常时 `Collapsed` 不占位，与日志保存卡的失败提示同一取向），两种：
  ① 被「启动应用」页禁用；② 登记的是另一个路径（提示里带上那个旧路径）。
  颜色走 `Brush.Warning` 琥珀，与日志写入失败提示同色。
- **功能自验证**：`.tmp/AutoStartCheck`（`AutoStartCheck.exe` 38 项纯逻辑断言 +
  `uia-e2e.ps1` / `uia-edge.ps1` / `uia-restart.ps1` 三段 UIA 端到端）。⚠️ 跑这些脚本会**真的改注册表**，
  脚本末尾自带清场；跑完用 `Get-ItemProperty HKCU:\...\Run` 确认没有残留。

### 7.5 应用标题（P5.6，2026-11-08）

设置页**第一张卡**（最上方，「显示外观」之上）：只有一行标签「标题」+ 输入框，**失焦生效**。

**实现位置**：`Modeling/AppTitle.cs`（**纯逻辑**：默认值 / 清洗 / 默认识别）+ `Themes/ThemeManager.cs`
（`AppTitleText` / `SetAppTitle`，落 `app-settings.json`）+ 窗口侧 `ApplyAppTitle` / `ApplyAppTitlePermission` /
`AppTitleTextBox_LostFocus`。

| 项 | 值 |
|---|---|
| 标题栏那行字 | `TitleBarCaption`（`MainWindow.xaml` 里那个 `TextBlock`），默认 `数据采集Demo` |
| `Window.Title` | 任务栏 / Alt+Tab，默认 `采集工具` |
| 持久化 | `app-settings.json` → `AppTitleText`，**空串 = 没设置过** |
| 权限 | `UserRules.CanEditAppTitle`（工程师），窗口侧 `CanEditAppTitle` 只是薄包装 |

**设置页卡片顺序**（2026-11-08 用户定案）：**应用标题 → 显示外观 → 开机自启动 → 日志保存 → 项目信息**。

**几条容易踩的**：

- ⚠️ **两个默认值本来就不同**（`数据采集Demo` / `采集工具`），本次**刻意没有统一** ——
  统一会改掉现有外观。所以未设置时各回各自默认，设置了才两处都换。
- ⚠️ **必须清洗换行**：这个值要进 `TextBlock` 与窗口标题，含 `\r\n` 时标题栏那行会变两行、
  把 48px 的标题栏撑变形（粘贴多行文本就会触发）。`AppTitle.Normalize` 把 `\r`/`\n`/`\t`
  压成空格、连续空格合并、超 40 字截断并去掉截断处的尾部空白。
- ⚠️ **卡片上只有这一个控件，不要再加第二个**（2026-11-08 用户定案）：曾有过「恢复默认」按钮与
  「当前账户：X」说明文字，都被要求删掉。**清空输入框就是恢复默认**（空串 = 没设置过），
  多一个按钮反而多一处要同步置灰；当前账户在导航底部那个按钮上一直显示着，卡片里重复一遍是噪声。
- **这里可以直接设 `IsEnabled`**，与自由监控那格不同：自由监控在 `DataTemplate` 里、每行一份还会重建，
  逐个设必然漏；这里只有一个固定控件。别把两者当成同一套写法。
- **权限要跟账户走**：`MainWindow.Account.cs` 的 `InitializeAccount` 与 `AccountButton_Click`
  都会调 `ApplyAppTitlePermission()`，登录 / 注销后立即生效，无需重开设置页。
- **登录 / 注销本身不改标题**：标题是全局设置，注销只是降权限，不该把用户设的标题清掉。
- **等默认值归空串放在 `Save()` 里**（与 `LogDirectory` 同一不变式）：用户原样敲一遍默认标题并失焦，
  不该在 json 里留下一条看着像自定义、实际与默认相同的记录。
- **功能自验证**：`.tmp/AppTitleCheck`（`AppTitleCheck.exe` 42 项纯逻辑断言 + `uia-title.ps1` 端到端）。
  ⚠️ UIA 有个坑：**带 Owner 的对话框是主窗口的子孙节点，不是独立顶层窗口** ——
  按顶层窗口枚举找一个都找不到（登录框实测踩过）。取 `$win.FindFirst(TreeScope.Descendants, ControlType.Window)`。

### 7.6 切换设备类型的清理与确认（P4.3，2026-11-08）

切换「设备类型」（品牌）是**破坏性操作**：它会清掉三类按旧品牌地址表建立起来的配置。
`DeviceTypeComboBox_SelectionChanged` 里先问、再清、最后才真正换驱动。

| 会被处理的东西 | 处置 | 为什么 |
|---|---|---|
| 全局变量的**映射地址** | **清空**（变量本身保留：名字 / 类型 / 注释都在） | 地址是元件名，换品牌后可能不存在或指向别处 |
| 自由监控的**元件监控项** | **整条删除** | 元件名绑具体品牌的地址表，换品牌后可能根本不存在 |
| **已导入的 IO 表** | **整份清除**并落盘 | 注释表是按旧品牌校验过的产物，换品牌后点位可能越界/不存在 |
| 自由监控里**引用全局变量**的行 | **保留** | 它靠变量名，变量还在，下一拍按新地址重解析 |

**实现位置**：`Modeling/DriverSwitchImpact.cs`（纯逻辑：计数 + 提示文案）+ `MainWindow.xaml.cs` 的
`ConfirmDriverSwitch` / `ApplyDriverSwitchCleanup` + 各集合的执行方法
（`GlobalVarItemSet.ClearAllAddresses` / `MonitorItemSet.RemoveElementItems` / `IoPointSet.ClearAll`）。

**几条容易踩的**：

- ⚠️ **三个数字先算再问**：弹框里要写「将删除 N 个」。`DriverSwitchImpact` 是纯逻辑，
  `.tmp/DriverSwitchCheck` 31 项断言覆盖（含「真实表的规模：15 条里 11 条有地址」这种口径）。
- ⚠️ **三项都是 0 时不弹框、直接切**：没有东西会丢的时候还拦一道，会让用户养成
  「看都不看就点确定」的习惯，真正有删除时那道确认也就失效了（`BuildPrompt` 返回空串即此意）。
- ⚠️ **取消必须把下拉框选回原项**，且回填要罩在 `_revertingDeviceType` 闸门里 ——
  否则这次回填又会进 `SelectionChanged` 一次。与 `_restoringNicSelection` 同一手法。
- ⚠️ **清理必须在 `_currentDriver` 赋值之前**：`RebuildIoPoints` 用 `_currentDriver`，
  在清理里调它就会用**旧驱动**重建一遍（先删了又按旧品牌建回来）。清理里只调 `ApplyIoPage`
  刷空提示，真正的重建留给后面的正常流程。
- **确认框 `danger: true`**：确定钮实心红、默认焦点落「取消」（§9.3 第 9 条）。
- **确认后记一条运行日志**（WARNING 级）：`已切换设备类型：清空 N 个映射地址、删除 N 个元件监控项、清除 IO 表 N 个点位`。
  这三个数字事后要能查 —— 重启后用户只会看到「地址都没了」。
- **只在真的换了品牌时才问**：`ReferenceEquals(next, _currentDriver)` 为真（同一个对象）直接跳过，
  避免构造期回填下拉框也弹框。

**设备类型会持久化**（2026-11-09 用户要求；此前构造函数里写死 `SelectedIndex = 0`）：

- `app-settings.json` → `DeviceTypeKey`，存驱动的 **`Key`**（`xinje_xd` / `inovance_easy`）。
  ⚠️ **不存 `DisplayName`** —— 那是给人看的文案，改文案或调整品牌顺序就对不上，
  与 `TargetNic` 存网卡名而不是下拉框文本同一取舍
- 回填在**构造函数**里（`ResolveStartupDriver()` → `DriverRegistry.GetByKey`），
  取不到（旧 json 无字段 / 品牌被移除 / Key 手改坏）就退回 `DriverRegistry.All[0]`，
  **静默回退不弹框**：启动时弹「你说的品牌找不到」只会让用户困惑于一个他没做过的操作
- ⚠️⚠️ **回填必须先把 `_currentDriver` 设好，再把下拉框赋值罩在 `_revertingDeviceType` 里**。
  `SelectionChanged` 把「换了个对象」当成用户切品牌，会走 `ConfirmDriverSwitch` +
  `ApplyDriverSwitchCleanup` —— 那是**破坏性**的（清映射地址 / 删元件监控项 / 清 IO 表）。
  构造期集合恰好是空的、`BuildPrompt` 算出「没东西要删」所以不弹框，**但那只是巧合**：
  以后谁把 `LoadComments` / `_globalVarSet.Load` 提到这行之前，启动就会真删数据。
  闸门用 `try/finally` 括住，别只写一行赋值
- 落盘在 `SelectionChanged` **末尾**（`ThemeManager.SetDeviceType(_currentDriver?.Key)`）。
  两条程序化回填路径（启动回填 / 取消切换回填）都被 `_revertingDeviceType` 早退挡住，到不了那里，
  所以不会误写 —— **取消切换后 json 仍是原品牌**
- ⚠️ **字节顺序仍不持久化**：它由驱动默认值决定、切品牌时整份重置（§8.1）。这是刻意的 ——
  存下来会出现「换了品牌、字序还是旧品牌」的静默错值

**验证**：`.tmp/DriverSwitchCheck` 31 项纯逻辑断言；UIA 端到端两条路径 ——
取消（下拉框回退到信捷、三类数据一个没动）、确认（汇川 + 地址全空 + 只剩变量行 + IO 表 0 点位 + 日志一条）。
持久化的端到端另见 `.tmp/drivertype-check/`：
`run.ps1`（满配置启动不弹框不清数据 / 空配置切换落盘重启 / 非法 Key 回退）与
`cancel.ps1`（取消路径：11 地址 + 10 监控项 + 156 IO 点，哈希全不变）。
⚠️ **写这类"会弹模态框"的 UIA 用例必须用 Win32**（`PostMessage` 发按键 + 按标题找对话框窗口 +
只对对话框做 UIA）：模态消息循环下对主窗口子树的 UIA 调用会超时并返回 null，会把
「弹了框」误判成「没弹框」、把强杀进程误判成「数据被清了」—— 踩过一次假阳性。
弹框实测文案：

```
切换到「汇川Easy系列」后，以下内容将被删除且无法撤销：

· 全局变量表中的 2 个映射地址（变量本身保留）
· 自由监控表中的 2 个元件监控项
· 已导入的 IO 表（4 个点位）

确定要切换吗？
```

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

`CommDriver` 上有三个**品牌约定**属性，设备连接页各对应一个下拉框：

| 属性 | 枚举 | 信捷 XD | 汇川 Easy |
|---|---|---|---|
| `Int32ByteOrder` | `ByteOrder32`（`1234`/`2143`/`3412`/`4321`） | `Order3412` | `Order3412` |
| `FloatByteOrder` | 同上 | `Order3412` | `Order3412` |
| `StringByteOrder` | `StringByteOrder`（`12`/`21`） | **`Order21`** | `Order21` |

- 记号含义见 `CommDriver.cs` 的枚举注释：把 4 个字节**按重要性**编号 1=MSB…4=LSB，名称即字节流里的排列顺序
- ⚠️ **信捷字符串序 2026-11-20 由 `12` 改为 `21`**（用户真机实测）。原因：**信捷后来新增了真正的
  `STRING` 类型**，此前该功能是用 **byte 数组**模拟的 —— 两者在寄存器内的字节落位不同。
  旧结论（`12` / `abcd` → `0x6162`，见 `Design.md` §10.4）针对的是 byte 数组那套写法，
  **不适用于新 STRING 类型**。`ByteOrderSettings.Default` 的兜底值同步改为 `21`
- ⚠️ 因此「**两品牌唯一差异是字符串**」这条旧结论**已失效** —— 现在两品牌的字符串序一致（都是 `21`）。
  将来做多品牌对照时别再拿「字符串序」当区分依据
- **32 位整型 / 浮点的两品牌默认仍都是 `3412`**（各自真机实测，见 `Design.md` §10.3 / §10.16）
- 新增品牌时**不要照抄任何一家的值**：先用 `.tmp/OrderProbe` 探针实测（见下）；尤其别假设信捷与汇川还不同
- ⚠️ 字符串**不涉及「字序」**：它只是字节序列，没有字的权重之分，别把 `3412` 套到 `STRING` 上

**三套各自独立，且真的会被读**（P4.3，2026-10-21 接线）：

- 载体是 `Modeling/ByteOrderSettings.cs`（`Int32` / `Float` / `String` 三个字段 + `FromDriver`）。
  `ValueCodec.FormatRegisters` / `TryEncodeRegisters` 收的是**整份** settings，按行类型取对应那套 ——
  编解码器仍然**不读全局状态**，只认入参
- ⚠️ `REAL` 与 `DINT` **不再共用一套字序**。早先两者绑在一个 `byteOrder` 参数上，
  「32位浮点数字节顺序」下拉框改了也不生效（界面显示 1234、数值仍按 3412 解，**不报错但全是错值**）
- **切换设备类型 = 重置为驱动默认**（用户定案，2026-11-20 起改为「方案 B」）：
  - **持久化**：三套字序存 `app-settings.json` → `Int32ByteOrder` / `FloatByteOrder` /
    `StringByteOrder`（存**枚举数值**；`-1` = 没保存过。⚠️ 字符串序**不能用 0 当哨兵** ——
    `Order12` 的枚举值就是 0）
  - **同品牌重启 → 恢复用户调过的值**；**换品牌 → 强制重置为新驱动默认**
  - 实现：`SyncByteOrderCombos` 按 `_driverJustSwitched` 分支 —— 该标志只在
    `DeviceTypeComboBox_SelectionChanged` 的切换路径内置位，为真时**忽略持久化值**、
    一律 `FromDriver`
  - ⚠️ **换品牌后要用「新驱动默认」重新落盘**（`PersistByteOrders(FromDriver(_currentDriver))`）：
    否则 json 里仍是旧品牌的值，下次启动会把它恢复回来 —— 那正是当初不敢持久化的原因
  - ⚠️ **三个字序要么全恢复、要么全不用**（`TryGetSavedByteOrders` 任一越界即整体放弃）：
    只恢复一半会造出「整数是上次的、浮点是默认的」混合态，界面上根本看不出哪项是旧的
  - `MonitorItemSet.SetDriver` 也同步重置一次；所有改动路径都走窗口的 `ApplyByteOrders`
    （写 `_byteOrders` + `_monitorSet.SetByteOrders`）—— **不要只改下拉框**，那样界面与数据会各说各话
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

### 8.2 目标网卡（TargetNic）选择
设备连接页的「目标网卡」下拉框决定 `ConnectButton_Click` 里 `new IPEndPoint(nic.Ip, 0)` 的**本地端点**
（即绑定哪块网卡出去）。规则与坑：

- **存的是网卡名**（如 `WLAN`），不是下拉框显示文本 `WLAN(192.168.1.15)` —— 文本里嵌着 IP，而同一块网卡换个网络
  IP 就变（笔记本的 WLAN 在家 `192.168.1.x`、在公司 `10.0.0.x`），按文本匹配**恰好在最需要它的场景下失效**
- 还原优先级：**本次扫描前的选择 → 上次运行记住的 → 第 0 项**（`Net/NicScanner.RestoreIndex`）
- 规则本体在 `Net/NicScanner.cs`；窗口侧只有 `ScanNetworkAdapters`（闸门 + 填下拉框）与
  `TargetNicComboBox_SelectionChanged`。持久化字段 `TargetNic` 与 `TargetIp` / `TargetPort` 同在 `app-settings.json`
  （整对象保存，归一化只放 `Save()` 里 —— 见 `AGENTS.md` §9.2 第一条）
- ⚠️ **扫描过程必须整体罩在 `_restoringNicSelection` 里**：`Items.Clear()` 在原本有选中项时就会触发
  `SelectionChanged`，只罩最后那次赋值挡不住。挡掉的意义不只是「别写盘」—— 若某块网卡临时消失
  （VPN 断开 / 网线拔掉），扫描会回退到第 0 项；把这次回退也写进偏好，用户原先的选择就被**悄悄抹掉**了。
  实测：预置一块不存在的网卡名，启动后下拉框回退到第 0 项，而 json 里仍是原值
- **为什么必须持久化**：绑错网卡（如绑到 Tailscale 却去连 `192.168.1.100`）会立刻报 `WSAENETUNREACH`，
  界面上显示成「连接失败：… 网络不可达」—— 看着像网络故障，其实是**上次的选择没被记住**。
  自动重连不受影响（`ReconnectTarget` 已快照本地端点），**只有启动后的第一次连接会踩到**

⚠️ **同网段双网卡时症状会变**（2026-10-16 真机实测）：本机 `以太网 192.168.1.80` 与 `WLAN 192.168.1.15`
**同为 `192.168.1.x/24`**，而 PLC 只从有线侧可达。此时选错网卡**不会**报 `WSAENETUNREACH` ——
路由表认为该网段直连、SYN 正常发出，只是发到了没有 PLC 的那块网卡，于是表现为**连接超时**（约 3s）。
故**「连接超时」在多网卡现场不能直接推断为「PLC 没响应」**，先确认路由选中的网卡与下拉框一致：

```powershell
Find-NetRoute -RemoteIPAddress <PLC>
```

## 13. 持久化与健壮性加固（2026-11-09）

> 本章是 2026-11-08/09 一轮健壮性审查 + 加固的落地记录。
> **改任何 `*Store` / 配置文件 / 断连逻辑 / 跨线程集合访问之前先读本章**，
> 里面的每一条都对应一个已修掉的真实缺陷，别再退回去。

### 13.1 为什么要做：一条完整的「静默丢数据」链路

加固前的四个 Store（`DataPointStore` / `GlobalVariableStore` / `AcquisitionTaskStore` / `IoPointSet`）
长得一模一样，且都踩同一个坑：

```csharp
// 加固前的形状（已删，不要再写回来）
public static List<T> Load()
{
    try { ... return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new(); }
    catch { return new List<T>(); }   // ← ① 反序列化失败静默回空
}
public static bool Save(IEnumerable<T> items)
{
    File.WriteAllText(FilePath, json);   // ← ③ 无条件整体覆盖
}
```

触发链（**四个条件缺一不可，但现场全都会碰到**）：

1. `data-points.json` / `GlobalVar.json` / `acquisition-tasks.json` / `io-comments.json`
   被**瞬时锁住**（杀软扫描、编辑器未关）、写到一半断电、或人手改错一个字段类型；
2. `Load()` 反序列化抛异常 → catch → **返回空集合** → 界面看起来「配置全没了」；
3. 用户以为是自己弄丢的，随手补一条 / 重新导入一次；
4. `Save()` **把这一条覆盖式写回** → 原文件里剩下没读出来的数据**永久消失**。

第 ②③ 步之间没有任何提示，第 ④ 步把证据也抹了。`MachineStateStore` 早年踩过一次，
用「逐条容错」修了，但另外四个没跟上 —— 本章把这件事收成统一实现。

### 13.2 `Modeling/AtomicFile.cs`（唯一出口）

**纯逻辑、无 WPF**，可链接进断言工程。两个方法，都不抛异常：

| 方法 | 做什么 |
|---|---|
| `WriteAllText(path, content)` | 原子写：`path + ".tmp"` → `StreamWriter` + `Flush()` + `fs.Flush(flushToDisk: true)` → `File.Replace(tmp, path, path + ".bak")`。`File.Replace` 在跨卷 / 权限受限时失败则退化为 `File.Move(overwrite: true)`。返回 `bool` |
| `ReadList<T>(path, options)` | 容错读：文件不存在/空白 → 空集合；整体反序列化成功 → 原样返回；**整体失败 → 逐个元素抢救**（`JsonDocument` 遍历，单条 `try` 失败就跳过）**并把原文件改名备份为 `path + ".bad-yyyyMMddHHmmss"`** |
| `BackupBad(path)` | 上一条内部用，也可单独调（`IoPointSet` 用） |

**几条必须守住的**：

- ⚠️ **`Flush(flushToDisk: true)` 不能省**：只调 `StreamWriter.Flush()` 只是把托管缓冲推给 OS，
  断电时 OS 缓存里的内容照样丢。这一句才是「关机/掉电也不留半个文件」的关键
- ⚠️ **写失败要能让调用方知道**：`WriteAllText` 返回 `bool`，别把它当 `void` 用。
  配置类（`ThemeManager`）靠这个 `bool` 上浮提示用户（§13.3）；数据类 Store 返回 `bool` 给调用方
- ⚠️ **`.bad-时间戳` 备份是「抢救证据」，不是「恢复入口」**：它只保证原始字节还在，
  恢复得人工看 json。**不要**在启动时自动读 `.bad-*` —— 那等于把坏文件当配置用
- ⚠️ **备份失败不阻断**（`try/catch` 吞掉）：磁盘只读时备份也做不成，但不能因此让程序起不来
- ⚠️ **`ReadList` 的「整体成功」优先**：只有整体反序列化真的抛了才走逐元素。
  否则正常文件也要多遍历一遍 `JsonDocument`，白白多一次分配

### 13.3 五处接入点（改了哪、没改哪）

| 文件 | 接入方式 |
|---|---|
| `Modeling/DataPoint.cs` | `Load()` → `AtomicFile.ReadList`；`Save()` → `AtomicFile.WriteAllText` |
| `Modeling/GlobalVariable.cs` | 同上 |
| `Modeling/AcquisitionTask.cs` | 同上 |
| `Modeling/MachineState.cs` | 只有 `Save` 改原子写（**读**仍是它自己那套「逐条容错 + `ReadElementByElement`」，早就有，别动） |
| `Io/IoPointSet.cs` | `Save()` 改原子写；`Load()` 因为结构是 `Dictionary<string,string>`（不是 List），自带一套 `TryReadCommentsElementByElement` + `AtomicFile.BackupBad` |

- ⚠️ **`IoPointSet.Load` 里那个「逐条重建字典」的循环不能省**：
  `new Dictionary(loaded, StringComparer.OrdinalIgnoreCase)` 在键只差大小写时抛 `ArgumentException`
  （`{"x17":"a","X17":"b"}` 就会命中），而 json 是人手可改的
- ⚠️ **`MachineStateStore.Save(items, edges)` 那个两参重载会丢指示灯段**（`lamps: null`），
  **产品代码一处都不能调** —— 生产路径走三参。保留只是为了断言工程能单独测状态/连线往返
- ⚠️ **新增 `*Store` 一律用 `AtomicFile`**，不要再写 `File.WriteAllText` 直写配置

### 13.4 设置落盘失败要「看得见」（`ThemeManager`）

`app-settings.json` 装着**主题 / 日志开关与目录 / 网卡名 / IP / 端口 / 自动连接 / 应用标题**七项，
一次 `Save` 写整个对象。加固前 `catch { }` 全吞，后果是：

> 用户改了主题 → 界面立刻变了 → 关掉重开又变回去，**而且一条日志都没有**（写日志本身也可能失败）。

现在：

- `Save(AppSettings)` 返回 `bool`，并设 `LastSaveError`
- 失败时触发静态事件 `SettingsSaveFailed(string reason)`
- 窗口 `Window_Loaded` 订阅、`OnClosed` 退订；处理器 `OnSettingsSaveFailed` 把它写进**运行日志（琥珀警告）**

⚠️ **这是唯一一处「配置写失败会出现在运行日志里」的通道**，别绕开 `Save` 自己写 json。
⚠️ 事件订阅/退订必须成对（`OnThemeChanged` 旁边那两行），否则窗口关了还会被回调。

### 13.5 断连：先等采集循环退出，再 `Dispose` 客户端

**加固前的错误顺序**（看着天经地义，实际有竞态）：

```
CancelConnect() → StopPolling()（只 Cancel，不 await）→ ReleaseClient()（立刻 Dispose TcpClient）
```

`StopPolling` 只是**发取消信号**，后台 `PollLoopAsync` / `HeartbeatLoopAsync` 可能正卡在
`NetworkStream.ReadAsync` 上。UI 线程那条语句相当于「在正在 IO 的套接字上 Dispose」——
典型的 use-after-dispose。当前靠 `RequestAsync` 的 catch-all 兜住，不崩也不泄漏，
但这是**没有串行化保证**的受控竞态。

现在的做法（`MainWindow.xaml.cs`）：

- `_pollCompletion` 字段记住 `RunConnectionTasksAsync(...)` 返回的 Task（`StartPolling` 里赋值）
- `Disconnect(status,color)` 里先取快照 `pending` 与 `clientToRelease`，再 `StopPolling()`，
  然后 `_ = ReleaseClientAfterPollAsync(pending, clientToRelease)`
- `ReleaseClientAfterPollAsync`：`await pending`（异常吞掉，循环内部已收敛）→
  **`ReferenceEquals(_session.Client, client)` 才 `ReleaseClient()`**

⚠️ **引用相等守卫不能省**：`await` 期间自动重连可能已经接管了**新**客户端，
不加守卫就会把刚建好的连接拆掉 —— 与 `ScannerSession.NotifyScannerLost` 那条同理。
⚠️ `Disconnect` 恒在 UI 线程调用，`await` 后仍回 UI 线程，不需要额外 `Dispatcher`。

### 13.6 自由监控读计划必须回 UI 线程算

`PollFreeMonitorAsync` 原先是 `_host.MonitorSet.BuildReadPlan()` **直接在采集线程**执行，
而 `MonitorItemSet._items` 是 UI 线程维护的 `ObservableCollection`。连接期间自由监控页仍可增删行
（只有设备连接表单被锁），字节顺序下拉框**刻意不锁**且会写 `_monitorSet` —— 撞上就是
`InvalidOperationException: Collection was modified`，再被 `PollLoopAsync` 的 `catch (Exception)`
**当成通讯断线**：记一条红色「已断开」并触发自动重连。**一次纯粹的界面编辑被误判成断线。**

现在 `ICollectorHost` 多了一个重载：

```csharp
Task<T> RunOnUiAsync<T>(Func<T> func);   // 窗口侧 = Dispatcher.InvokeAsync(func).Task
```

```csharp
List<ReadBlock> plan = await _host.RunOnUiAsync(() => _host.MonitorSet.BuildReadPlan());
```

⚠️ **不要在后台线程遍历任何 `ObservableCollection` / 界面持有的 `List`**。要么像
`PollSnapshot` 那样由 UI 线程交**不可变快照**，要么用这个重载把计算搬回 UI 线程。
⚠️ `VisiblePage` / `MachineGlobals` / `AcquisitionGlobals` 三个字段走的是「UI 线程整体替换数组引用」，
所以后台**只交引用**是安全的 —— 但前提是**没人就地改那个数组**（保持「整体替换」这个约定）。

### 13.7 `volatile` 的口径（9 个）

UI 线程写、采集线程每拍读的字段，一律显式 `volatile`：

`_pollIntervalMs` / `_ioPageActive` / `_yViewActive` / `_freeViewActive` / `_xPageIndex` /
`_yPageIndex` / `_visiblePage` / **`_machineGlobals`** / **`_acquisitionGlobals`**

后两个是 2026-11-09 补的（漏标）：引用赋值本身原子，但没有内存屏障时后台**可能长期读到陈旧引用**，
表现为「采的变量集合与配置对不上」。⚠️ 新增跨线程字段时**先判断是不是这一档**，别等审查发现。

### 13.8 全局异常兜底（顺序 + 可见性）

`App.OnStartup` 里两处改动：

1. **异常兜底注册挪到 `ThemeManager.Initialize()` 之前**。`ValidateKeyParity()` 是故意「启动即崩」的，
   但兜底晚于它注册时，那种崩溃**连 `crash.log` 都不会写**（WinExe 无控制台，用户只看到窗口消失）；
2. **界面异常除了写日志，还弹一次 `ThemeMessageDialog.Info`**。原来只 `File.AppendAllText`，
   现场表现是「窗口还在但没反应」，而多数人不知道 exe 旁有个 crash.log。
   用 `_fatalDialogShown` 去重，防止兜底里的弹窗再触发异常造成连环弹。

⚠️ 后台异常（`AppDomain.UnhandledException`）**不弹窗** —— 进程可能马上就没，弹窗没有意义。
⚠️ 别在兜底里再抛异常：`ReportFatal` / `ShowFatalNotice` 内部都有 `try/catch`。

### 13.9 换主题时两处必须手工刷新（不是 `DynamicResource`）

| 位置 | 症状（漏了会怎样） | 修法 |
|---|---|---|
| `Ui/SourceForegroundConverter`（采集「来源变量」框字色） | 转换器拿的是 `ThemeManager.GetBrush` 的**一次性快照**，`IMultiValueConverter` 只在绑定路径变化时重算 → 浅↔深切换后停在旧主题的近黑 `#1A1A1A`，压在深色输入框底 `#1E1E1E` 上几乎不可读 | `OnThemeChanged` 调 `_acqTaskSet.RaiseSourceColorRefresh()` → 逐层转给 `AcquisitionItemRow.RaiseSourceColorRefresh()`，触发 `SourceVariableName` + `HasSourceError` 两条 INPC，MultiBinding 重跑转换器 |
| `Ui/MachineStateView.LabelVisual` | 换主题本身没问题，但 `Bind()` 重建画面时**旧 visual 订阅的模型 `PropertyChanged` 从未退订** → 模型（长命对象）一直持有已脱离可视树的 `Border`/`TextBox`，每次 `Bind` 泄漏一批 | `LabelVisual.Detach()` 退订；`Bind()` 开头先遍历旧 `_labels` 逐个 `Detach()` 再 `Clear()` |

⚠️ **凡是 C# 侧「取一次画刷存起来」的地方，都要问一句「换主题谁负责刷新」** ——
`DynamicResource` 只管 XAML。完整清单见 `AGENTS.md` §9.2 那张表。

### 13.10 自动重连循环的异常兜底（第 6 条日志）

`ReconnectLoopAsync` 是 fire-and-forget（`_ = ReconnectLoopAsync(...)`），加固前只捕 `OperationCanceledException`。
若 `SetStatus` 之类抛出别的异常，任务静默故障：**自动重连停摆、按钮停在「取消重连」、表单锁着、
日志里一个字都没有** —— 现场只会觉得「它自己不再连了」。

现在多一个 `catch (Exception ex)`（`finally` 里的 `EndReconnect` / `Dispose` 不变）：

- 非关窗、非取消时：记 `PLC自动重连已停止：<原因>`（红）+ 底栏同文案 + 按钮恢复「连接」+ 表单解锁
- **不原地重试**：异常来自代码缺陷而非通讯状况，重试只会刷屏

⚠️ 与 ADR-14「重连尝试不写日志」**不冲突**：那条管「尝试失败」（退避表里每次 delay 后的结果是常规现象），
这条管「循环本身挂了」。文案规则见 §7 的「第 6 条例外」。

### 13.11 断言工程的连带维护（新增源文件必读）

`.tmp/*Check` 与 `ModelingCheck/` 都是**链接源码**（`<Compile Include="..\..\DSH_Modbus采集工具\..." />`），
不是引 dll。所以：

- **新增一个被 Store 引用的源文件，必须同步往这些 csproj 里加链接**，否则断言工程编不过
  （本次新增 `AtomicFile.cs` 就漏了这一步，`ExpressionCheck` 报 `CS0103: 不存在名称 AtomicFile`）
- 已加链接的：`ExpressionCheck` / `AcquisitionCheck` / `GlobalVarCheck` / `MonitorVarCheck` /
  `SystemVarCheck` / `DriverSwitchCheck` / `IoCheck` / `ModelingCheck` / `ModelingCheck/live`
- ⚠️ `.tmp/AcquisitionCheck` 与 `.tmp/MonitorVarCheck` 里还有**陈旧的**编译错误
  （`AcquisitionConstant` 未链接、`AcquisitionWriteStatus.Skipped` 已改名 `CannotAddress`、
  `WritePlan.cs` 路径写错），与本章无关，属于更早迭代留下的 —— 要跑它们得先修这些
- ⚠️ 跑 `.tmp` 下的工程前先 `$env:TEMP = "$PWD\.tmp\<名字>-temp"`（沙箱下 `%TEMP%` 挂 ACE，建目录会被拒）

## 14. 单实例保护（2026-11-09）

**需求**：每台机器只允许运行一个实例（用户定案）。此前可以随便开多个，
危害是实的：多实例同写一份 `app-settings.json` / `*.json`、`AcquisitionCsv` 的
`File.AppendAllText` 会让 CSV 行交错、多个实例连同一个 PLC 时写入互相打架。

### 14.1 为什么是 `Global\` 命名互斥量

| 方案 | 结论 |
|---|---|
| **`Global\` 命名互斥量 + 唤起已有窗口** | ✅ **采用** |
| `Local\` 命名互斥量 | ❌ 只在**当前登录会话**可见。远程桌面 / 快速用户切换 / 另一账户登录是另一个会话，看不到 → 照样两个实例。需求是「每台机器一个」，必须跨会话 |
| 文件锁（独占打开 .lock） | ⚠️ 可行但更差：要挑落盘位置（`ProgramData` 又要写权限）、崩溃后留残留文件让人误会、且 `File.Exists` 判据经典坑 |
| 进程枚举（`GetProcessesByName`） | ❌ 有竞态：两进程同时枚举时都看不到对方。双击两下就能穿透 |
| 命名管道 / 内存映射 | ❌ 过度设计：本项目没有跨进程传参需求 |

**实测（本机，标准用户非管理员）**：
- 标准用户**可以**创建 `Global\` 互斥量，无需提权
- 有真实持有者时 `WaitOne(0)` 返回 **`false`**（判重可靠）
- 持有者被强杀后抛 **`AbandonedMutexException`**，必须 catch 并当作「拿到所有权」

### 14.2 ⚠️ `createdNew` 不能用来判重

```csharp
// 错！第二次创建时 createdNew 恒为 false，但它只说明「名字已存在」，
// 完全不代表有别的进程持有它（名字会一直存在，直到所有句柄关闭）
new Mutex(false, name, out bool createdNew);

// 对：非阻塞 WaitOne(0)
bool acquired = mutex.WaitOne(0);
```

这条由 `.tmp/SingleInstanceCheck` 的 3、4 两组断言分别从「语义」和「跨线程持有」
两个角度钉住。

### 14.3 名字刻意不含路径 / 版本 / 用户名

`Startup/SingleInstanceName.cs`：`Global\` + `"DSH_Modbus采集工具"`

- **不含 exe 路径** —— 否则同一台机器上两个发布目录（`_1.0.0` / `_1.0.1`）各起一个，
  同时连同一个 PLC，正是要防的场景
- **不含版本号** —— 升级期间新旧版不能共存。代价是「升级前必须先关旧的」，
  但第二个实例会明确提示，不会让人以为双击没反应
- **不含用户名** —— 含了就退化成「每用户一个」，与需求相反
- 用固定产品名而不是程序集名：程序集名将来改了不至于让新旧两版互相看不见

### 14.4 三类异常都要当成「抢到了」，否则死锁

`SingleInstanceGuard.Acquire()`：

- `AbandonedMutexException` → **拿到所有权**（上一个实例崩溃/被强杀）
- `UnauthorizedAccessException` → **按「已有实例」处理**：一个**更高权限**的进程
  （如管理员跑的旧实例）持有同名对象时，标准用户连打开句柄都不被允许
- 其它异常 → **放行**（`IsFirstInstance = true`）。宁可允许启动，也不要让人打不开程序；
  原因留在 `FailureReason` 里供提示用

### 14.5 唤起已有窗口：不能用 `Process.MainWindowHandle`

`Startup/WindowFinder.cs` 走 `EnumWindows` + 按**完整 exe 路径**匹配 + 跳过多余窗口：

- 判据是**可见 + 有标题**（主窗口都有标题；隐藏的辅助窗口 / 消息窗没有）
- 必须跳过**本进程**的窗口（否则唤起自己）
- `MainModule.FileName` 取不到时（权限不足 / 进程已退出）**跳过该候选**，不影响其它候选
- 找到后 `IsIconic` 则先 `SW_RESTORE`，再 `SetForegroundWindow`
- ⚠️ `SetForegroundWindow` 受**前台锁定限制**：本进程不是当前前台应用时，
  系统可能只闪任务栏而不真置前。**这不是 bug，也不需兜底** —— 看得见它在闪，
  比静默退出好得多

### 14.6 第二实例的表现：唤起 → 失败则弹框 → 退出

`App.RejectSecondInstance()`：

1. 先试 `TryActivateExistingWindow()`，成功则 `Shutdown()`
2. 找不到窗口（已有实例还在启动中）或唤起失败 → 弹 `ThemeMessageDialog.Info`
   「程序已在运行」，带上 `FailureReason`
3. `finally` 里 `Dispose()` + `Shutdown()`

⚠️ **绝不静默退出**：双击图标没任何反应，用户会以为程序坏了、再点几次、甚至去重装。
哪怕只是闪一下任务栏也比「什么都没发生」好。

### 14.7 检查必须是 `OnStartup` 的第一件事

`_singleInstance = SingleInstanceGuard.Acquire();` 放在异常兜底与
`ThemeManager.Initialize()` **之前**：

- 第二个实例不该碰任何配置（`Initialize` 会读 `app-settings.json` 并可能写盘）
- 也不该在退出前弹一堆初始化相关的框
- 抢不到时**不调 `base.OnStartup(e)`** → `StartupUri` 不会再建主窗口

⚠️ **锁要全程持有到进程退出**，不能放进 `using` —— 一释放第二个实例立刻就能起来。
`OnExit` 里显式 `Dispose()`（进程退出内核也会回收，但显式释放能让「调试器反复启动」
这个常见场景立刻恢复正常：调试宿主可能比被测进程活得久）。

### 14.8 与开机自启的交互（安全）

自启实例先拿到锁；用户之后手动双击 → 只唤起已有窗口，不会起第二个、不会重复连接。
自启的 `TryAutoConnect` 只在**唯一实例**里跑，因此不会被第二个进程重复触发。

### 14.9 验证

`.tmp/SingleInstanceCheck`（**17 项**，纯逻辑）：命名规则跨会话性 / 名字不含路径版本用户 /
`createdNew` 语义 / `WaitOne(0)` 跨线程持有 / Abandoned 可重获 / 不同名字互不干扰 /
标准用户权限。

`.tmp/single-instance/run.ps1` + `activate.ps1`（端到端）：

| 场景 | 结果 |
|---|---|
| 启动第二个实例 | 立刻退出（退出码 0），进程总数恒为 1 ✅ |
| 连开三次（模拟狂点图标） | 三次都自动退出 ✅ |
| 第一个实例是否被误杀 | 存活、窗口标题正常 ✅ |
| 关掉后再启动 | 能正常起来（锁已释放）✅ |
| **强杀后立刻重启** | 能起来（Abandoned 路径，不被死锁）✅ |
| **最小化第一个 → 启动第二个** | 第二个退出；第一个被**还原并置前**（`GetForegroundWindow == 它的 hwnd`）✅ |
| `crash.log` | 无 ✅ |

⚠️ **跨会话（远程桌面）那条未能实测**：本机只有一个活动会话（`console` ID=1，
`rdp-tcp` 仅 Listen），且当前身份非管理员，无法创建第二个会话的进程。
`Global\` 跨会话可见是 Windows 既定语义（对象在全局 `\BaseNamedObjects`，
`Local\` 只在会话自己的命名空间），上现场前建议用远程桌面实连一次确认。

### 14.10 顺带修复：`.tmp` 固定名 → GUID

`AtomicFile.WriteAllText` 原来用固定的 `path + ".tmp"` 作中转。单实例保护挡住的是
**同一台机器同目录**双写，但挡不住**两台机器共用同一个网络共享目录的配置** ——
那种场景下固定名的 `.tmp` 会被两个进程同时 `FileMode.Create`，一个正在写、
另一个把它 `Replace` 掉，结果是一份**内容被截断的配置**。

现在：
- 临时名 `path + "." + Guid.NewGuid().ToString("N") + ".tmp"`
- `FileMode.CreateNew`（不是 `Create`）：名字重复时直接失败，而不是静默共用
- `try/finally` 里清理残留（替换成功时 tmp 已不存在；失败时留着会不断堆积垃圾）

验证见 `.tmp/AtomicFileCheck`（**22 项**）：基本往返 / `.bak` 只留上一版 /
**并发写同一文件后内容必为某一次写入的完整原文（200 KB payload）** /
坏文件逐元素抢救 + `.bad-*` 备份 / 不存在与纯空白 / 深层目录自动创建 / 无 BOM。

## 15. 完整目录树（原 AGENTS.md §2）

> **为什么搬到这里**：AGENTS.md 有 65536 B 硬预算（超了整份被丢弃），
> 这份树约 10.6 KB 且属于**查阅型参考**（找文件在哪），不是每次改动都要照着做的规则。
> AGENTS.md §2 只留「9 个无 WPF 目录 + 反 WPF 判断原则 + 断言工程清单」这些必须每次记住的部分。
> **新增/删除源文件时两处都要改**：这里是全量树，AGENTS.md §2 只列目录级说明。

```
DSH_Modbus采集工具/
├── App.xaml(.cs)               # 入口（OnStartup 先 ThemeManager.Initialize）
├── AssemblyInfo.cs             # ThemeInfo 等程序集级声明
├── AppInfo.cs                  # 版本号 / 构建日期 / 运行环境（只读程序集元数据）
├── MainWindow.xaml             # 全部 UI（唯一 UserControl 是状态机画布）
├── MainWindow.xaml.cs          # 交互主体：连接 / 心跳 / 导航 / 底栏 / IO 分页·导入 / 日志
├── MainWindow.MachineState.cs  # partial：状态机画布接线
├── MainWindow.Acquisition.cs   # partial：采集任务组 / 数据项 / 执行
├── MainWindow.GlobalVar.cs     # partial：全局变量
├── MainWindow.Monitor.cs       # partial：自由监控
├── MainWindow.IoImport.cs      # partial：IO 表导入
├── MainWindow.AcquisitionView.cs  # partial：总览「数据采集」骨架
├── MainWindow.AcquisitionLive.cs  # partial：实时画布（拖动 / 缩放 / 背景图）
├── MainWindow.Scanner.cs       # partial：扫码器（连接 + 交互已合并一页）
├── MainWindow.Account.cs       # partial：账户与权限（登录 / 注销 / 自由监控写入置灰）
├── MainWindow.Traffic.cs       # partial：通讯监控（P2.4，目标列表 + 三个控件处理器）
├── Scanners/                   # 扫码器（无 WPF）
│   ├── ScannerDefinition.cs    # 定义 + 规则 + scanner.json
│   ├── ScannerTcpClient.cs     # 裸 TCP：原样发 + 按结束符收帧
│   ├── ScannerSession.cs       # 每台的连接身份与重连计数
│   ├── ScannerExchange.cs      # 上升沿判定 + 运行时态
│   └── ScannerTestResult.cs    # 触发测试的结果文案：控制字符转义 / 截断（§5.9.9）
├── MonitorItem.cs              # 自由监控项包装 + 集合（增删改 / 落盘 / 解析）
├── GlobalVarItem.cs            # 全局变量包装 + 集合
├── AcquisitionTaskItem.cs      # 采集任务组 / 项包装（含配置错误态）
├── SystemVarRow.cs             # 系统变量监控行
├── Expressions/                # 表达式引擎（无 WPF）：词法 → 语法 → 编译 → 取值
│   ├── ExpressionLexer.cs / ExpressionParser.cs / ExpressionNode.cs
│   ├── ExpressionValue.cs      # 求值结果（数值 / 布尔）
│   └── CompiledExpression.cs   # 入口 TryParse / 名字收集 TryValidateNames
├── Themes/                     # 主题（P5.1）
│   ├── Light.xaml / Dark.xaml  # 两套字典，必须同键（Light 值 = 主题化前的字面值）
│   ├── DialogControls.xaml     # 对话框样式（Dialog.* 前缀，App.xaml 合并）
│   └── ThemeManager.cs         # 换字典 / 跟随系统 / 键一致性校验 / app-settings.json
├── Monitoring/                 # 通讯监控抓包（无 WPF）
│   └── TrafficTrace.cs         # 目标 / 方向 / 环形缓冲 1000 条 / 全局 tap（只抓选中目标）
├── Polling/                    # 采集引擎与连接状态（无 WPF）
│   ├── ICollectorHost.cs       # 宿主契约：周期 / 监控集 / 快照 / 连接身份 / 回 UI / 断线
│   ├── ModbusPoller.cs         # 轮询编排（IO 分块读 + 自由监控读 + 断线通知）
│   ├── ConnectionSession.cs    # 连接身份与生命周期（客户端 / 目标 / 取消源 / 重连计数）
│   ├── ReconnectPolicy.cs      # 退避 1→2→4→5 封顶 5s（纯函数）
│   └── PollSnapshot.cs         # 可见性快照（页面 / 自由监控 / 当前页 / 周期）
├── Io/                         # IO 点位集（无 WPF，分页只做数值运算）
│   ├── IoPointSet.cs           # 注释表读写 + 名称归一化 / 地址换算 / 分组排序 / 导入校验
│   ├── IoPager.cs              # 分页纯计算（页数 / 夹取 / 切片 / 翻页 / 页号文本）
│   ├── IoTableImporter.cs      # xlsx → 注释表 + 失败明细
│   └── XlsxIoTableReader.cs    # xlsx 解析（零依赖 zip + XML）
├── Modeling/                   # 数据模型与纯逻辑（30 个，均无 WPF）
│   ├── AtomicFile.cs           # 配置文件原子写（.tmp→Replace+`.bak`）+ 容错读（逐元素抢救）
│   ├── DataTypes.cs            # 类型枚举 + 规格表
│   ├── DataTypeEnums.cs        # DataType / DisplayFormat 两个枚举（零依赖，可被最小断言工程单独链接）
│   ├── ArrayRules.cs           # ARRAY 声明规则：元素类型候选 / 个数 1~128（零依赖）
│   ├── ArrayValueCodec.cs      # ARRAY 逐元素寻址 / 切片 / 格式化 / 编码（§5.2.3）
│   ├── ValueCodec.cs           # 寄存器 ↔ 类型值编解码 + 显示格式化
│   ├── ByteOrderSettings.cs    # 三套字节顺序（整数 / 浮点 / 字符串）
│   ├── DataPoint.cs            # 监控项模型 + data-points.json + 驱动校验解析
│   ├── WritePlan.cs            # 一次写入的报文参数（FC05/06/16），两条写入路径共用
│   ├── GlobalVariable.cs       # 全局变量模型 + 变量名规则 + GlobalVar.json（值不落盘）
│   ├── VariableRenamer.cs      # 全局变量改名后同步各处引用（按标识符，禁子串替换，§5.2.2）
│   ├── VariableName.cs         # 引用**写法**解析：`var1` / `var1[0]`（零依赖，§5.2.3）
│   ├── VariableReference.cs    # 引用**查表**校验：存在性 / 是否数组 / 下标 / ResolveError
│   ├── GlobalReadPlan.cs       # 读计划 + 回填（按可见页面采集）
│   ├── CompletionSource.cs     # 变量名补全纯逻辑（取词 / 筛选 / 候选表）
│   ├── SystemVariable.cs / SystemVariableValues.cs   # 17 项清单 / 取值（脉冲·时钟·运行时长）
│   ├── UserSession.cs          # 账户角色 + 密码常量 + 写入权限（纯逻辑）
│   ├── MachineState.cs         # 状态机模型 + 布局常量 + Lamps + machine-state.json
│   ├── AcquisitionTask.cs      # 任务组 / 项模型 + 规则 + acquisition-tasks.json
│   ├── AcquisitionTaskCheck.cs # 配置错误判据
│   ├── AcquisitionWrite.cs     # 写入编码 + TryBuild
│   ├── AcquisitionCsv.cs       # CSV 建文件 / 表头 / 追加 / 列变换文件
│   ├── AcquisitionSource.cs    # 来源解析（常量 → 系统变量 → 全局变量）
│   ├── AcquisitionConstant.cs  # # 开头的常量字面量（语法 / 进制自标定），方案 B
│   ├── LiveImageStore.cs       # 背景图拷贝规则（同名加序号 / 同内容复用）
│   ├── LiveCoordinateText.cs   # 卡片坐标框文本规则（只接受整数）
│   ├── DriverSwitchImpact.cs   # 切换设备类型的影响统计 + 确认文案（P4.3）
│   ├── OverviewViewSetting.cs  # 总览「当前视图」：枚举 ↔ 下标 ↔ 落盘名（§5.5）
│   └── AppTitle.cs             # 应用标题：默认值 / 清洗 / 默认识别（P5.6）
├── Logging/                    # 日志（无 WPF）
│   ├── DeviceLogFileWriter.cs  # 按天追加 .log + 跨天换文件 + 失败自愈
│   └── DisconnectReasonClassifier.cs  # 异常原文 → 简短中文（底栏与日志共用）
├── Net/NicScanner.cs           # 网络接口 → NicInfo + 「上次选择」还原优先级
├── Net/SerialPortScanner.cs    # 串口 → SerialPortInfo（端口名 + 注册表里的设备名，零依赖）
├── Ui/                         # 窗口呈现辅助与对话框（引 WPF，不碰业务）
│   ├── MaximizedInset.cs       # 最大化溢出补偿：物理像素 + 按 DPI 内缩（§3.6）
│   ├── WindowCorners.cs        # 圆角 + 边缘线（DWM 绘制，§3.8）
│   ├── StatusBarPresenter.cs   # 底栏：状态文字 / 状态点颜色 / 右侧连接参数
│   ├── SourceForegroundConverter.cs  # 采集「来源变量」框字色（MultiBinding + 换主题重算，§13.9）
│   ├── DeviceLogPresenter.cs   # 日志：内存集合 + 上限裁剪 + 落盘旁路 + 级别映射
│   ├── TrafficMonitorPresenter.cs  # 通讯监控：目标下拉 / HEX-TEXT / 200ms 增量刷新 / 跟随滚底
│   ├── CompletionPopup.cs      # 补全下拉：Popup + 键鼠 + 输入法抑制（§5.6）
│   ├── DialogWindow.cs         # 对话框基类：外壳 / 按钮行 / 内联错误 / 回车与 ESC（§9.3）
│   ├── DialogChrome.cs         # 对话框字体 / 底色 / 深色标题栏
│   ├── ColorPalette.cs         # 30 色 drawio 色盘（两个编辑窗共用）
│   ├── MachineStateView.xaml(.cs)  # 状态机画布（本工程唯一 UserControl）
│   ├── MachineStateEditDialog.cs / MachineLampEditDialog.cs  # 状态框 / 指示灯编辑窗（§5.5）
│   ├── AcquisitionTaskDialog.cs   # 任务选项对话框
│   ├── AcquireLiveCardOptionDialog.cs  # 实时卡片：两个字号 + 小数位数(仅浮点) + X/Y + 重置
│   ├── LiveImagePicker.cs      # 背景图选择弹框（两个入口共用）
│   ├── ScannerErrorMark.cs     # 扫码器输入框标红用的 bool 附加属性
│   ├── NumberInputDialog.cs    # STRING 长度等单值输入弹窗
│   ├── ArrayDeclareDialog.cs   # ARRAY 声明：元素类型 + 元素数量（§5.2.3）
│   ├── LoginDialog.cs          # 账户登录框（用户名下拉 + 密码掩码 + 注销/登录）
│   └── ThemeMessageDialog.cs   # 全应用唯一的提示 / 确认弹框（Info / Confirm，§9.3）
├── Startup/                    # 开机自启动 + 单实例（无 WPF）
│   ├── AutoStartCommand.cs     # 纯逻辑：命令串 / 同 exe 判定 / 禁用记录判据
│   ├── AutoStartRegistry.cs    # 只做注册表 IO（HKCU Run + StartupApproved）
│   ├── SingleInstanceName.cs   # 纯逻辑：互斥量命名（Global\ + 产品名，§14）
│   ├── SingleInstanceGuard.cs  # 抢 Global\ 互斥量 + Abandoned/权限异常处理
│   └── WindowFinder.cs         # EnumWindows 找同一 exe 的窗口并置前
└── Drivers/
    ├── CommDriver.cs           # ModbusArea / NumberBase / AddressArea / CommDriver
    ├── DriverRegistry.cs       # 品牌地址数据表（含各品牌字节顺序约定）
    ├── ModbusTcpClient.cs      # Modbus TCP 主站（FC01/02/03/04 读 + FC05/06/16 写 + 本地端点绑定）
    └── ModbusHeartbeat.cs      # 独立心跳调度与异常分类
```


## 16. 样式键全表（原 AGENTS.md §3.3）

> **为什么搬到这里**：这是 Window.Resources 里 **53 个 x:Key** 的查表，约 5.9 KB。
> 改外观时按需查；AGENTS.md §3.3 只留「名字前缀约定 + 4 条最容易踩的」。

> 全部定义在 `MainWindow.xaml` 的 `Window.Resources` 里（**53 个 `x:Key`** + 一个**无 key 的隐式 `ScrollBar`**）。改外观先在这里找现成的；下表只列**会复用、或踩过坑**的，其余是某行专用模板（`*ItemTemplate`）。

**名字前缀**：`Io*` = IO 页/分页、`Form*` = 表单、`Monitor*` = 自由监控行内、`Acq*` = 采集任务组行内、`GlobalVar*` / `SystemVar*` = 两个分段模板、`Scanner*` = 扫码器、`Dialog.*` = 对话框（在 `Themes/DialogControls.xaml`，见 §9.3）。

| Key | 用途 |
|-----|------|
| `CaptionButton` / `CloseCaptionButton` | 标题栏按钮（关闭悬停红底） |
| `NavRadioButton` | 导航项（灰底背板，选中加粗） |
| `AccountButton` | 导航底部的账户入口（头像 18 + 用户名）。**比 `NavRadioButton` 紧凑**（Padding 10 / 头像 18 / 间距 8）：导航列 112 DIP 放不下「技术员」三个汉字，按 `NavRadioButton` 的 14+20+10+14 只剩 38 DIP（需约 42）。⚠️ `Margin` 左边距仍是 8，左缘才与其余导航项齐平 |
| `NavItemTemplate` | 导航项共享 `ControlTemplate`：外距/内距/高度全从 Style 传。要做不同缩进改 Style，别复制模板 |
| `IoSegmentRadioButton` | 分段控件，**15 处**：IO 页输入监控 / 输出监控 / 自由监控 / 系统变量、数据管理三档、设置页主题三档、设备连接三档（PLC / 扫码器 / MES，2026-11-24） |
| `ToggleSwitch` | **开关**（Win11 式）：轨道 40×20、柄 12、内缩 4；关＝`Control.Background`、开＝`Accent.Fill`；**状态文字在轨道左侧**，由代码按 `IsChecked` 改写 `Content`（「开」/「关」） |
| `IoItemTemplate` | IO 点位单元格（灯 10×10、注释 MinWidth 144；`Comment` 为 INPC） |
| `IoItemsControlStyle` | IO 条目横向拉伸 |
| `IoPagerButton` | 分页箭头（26×26，首末页置灰） |
| `IoCardButton` | **通用文字按钮**（浅灰实底胶囊／圆角 6／高 26／字号 12；底色走 `Brush.Control.Background`） |
| `FormButton` | 表单按钮，`BasedOn` `IoCardButton`，仅字号 14、内边距 14 |
| `FormComboBox` | 表单下拉框：**胶囊**（灰底 + 圆角 6 + chevron），字号 14／高 26。默认 `BorderThickness=0`；模板已 `TemplateBinding` `BorderBrush`/`BorderThickness`，个别位置需要框线时自己设 1（总览页「当前视图」就是） |
| `FormComboItem` | 表单下拉项容器（含 `ItemContainerStyle`，勿漏） |
| `FormTextBox` | 表单输入框：**框线**（白底 + `Brush.Input.Border` 圆角 4），字号 14／高 26 |
| `MonitorInlineComboBox` / `MonitorTypeCombo` | 行内下拉框：胶囊，字号 12／高 22 |
| `MonitorComboItem` | 行内下拉项容器 |
| `MonitorTypeItemTemplate` | 类型下拉的**展开项**模板，绑 `{Binding DisplayName}` —— 数据源必须是 `DataTypeSpec` 对象，放字符串会整列空白（§5.2） |
| `MonitorInlineTextBox` / `MonitorValueTextBox` | 行内输入框：框线，字号 12／高 22 |
| `MonitorItemTemplate` | 自由监控整行（名称/类型/格式/当前值/删除）；`IsSelected` 时整行底色 `Brush.Accent.Subtle` |
| `MonitorIconButton` | 行尾删除 ×（**悬停红**＝删除语义） |
| `MonitorHeaderIconButton` | 表头新增 ＋（**悬停主色**＝新增语义） |
| `MonitorClearAllButton` | 表头「🗑 清空」 |
| `GlobalVarItemTemplate` | 全局变量整行（序号/名称/类型/映射地址/注释/删除）；选中底色同上 |
| `SystemVarItemTemplate` / `SystemVarMonitorItemTemplate` | 系统变量清单行（只读，无输入框）/ 监控行（多一列「当前值」） |
| `AcqRowIconButton` | 采集任务组行内图标按钮（… ↑↓）：22×22，悬停中性灰底 + 深字。**刻意不复用** `MonitorIconButton`（红＝删除）与 `MonitorHeaderIconButton`（主色＝新增）—— 同一行混两种语义会读错 |
| `AcqRowDeleteButton` | 行「删除本组」×，`BasedOn` `AcqRowIconButton`，**只把悬停换实心红**（`Brush.Danger.Fill.Hover` + 白字），与全局变量 / 监控行的行尾删除一致 |
| `AcqEnableSwitch` | 「启用」开关，`BasedOn` `ToggleSwitch`，只补「文案随 `IsChecked` 变」。⚠️ 必须在 `Style.Triggers` 里换 `Content`，模板触发器压不过局部值 |
| `AcquisitionSourceBox` / `AcquisitionTargetBox` | 采集写入任务的来源 / 写入变量框，`BasedOn` `MonitorInlineTextBox`，只加「有错时字变红」。⚠️ `BasedOn` 要求被继承者**先定义** |
| `AcqGroupCombo` | 总览「数据采集」的任务组下拉框，`BasedOn` `FormComboBox`，只把底色改透明 + 补 1px 框线（该页是卡片内嵌，胶囊灰底会显得沉重） |
| `GlobalVarMoveButton` | 全局变量表头 ↑↓ 排序按钮（24×24、图标字体、竖排）。**排序对象是当前选中行**；没选中或已在两端时置灰 |
| `SourceForegroundConverter` | 采集「来源变量」框的字色转换器（常量橙 / 有错红 / 正常）。`IMultiValueConverter`，换主题要 `RaiseSourceColorRefresh()` 触发重算（§9.2） |
| `AcquisitionTaskRowTemplate` / `AcquisitionItemRowTemplate` | 任务组行（7 列 + 展开子表）/ 数据项行（序号·名称·变量·类型只读·删除） |
| `AcqLiveCard`（代码构造） | 实时数据卡片：`Border` + 项名称 + 数值，放 Canvas 里拖动（§5.8.1） |
| `LogClearButton` / `DeviceLogItemTemplate` / `DeviceLogListStyle` | 日志卡清空 / 日志行（时间戳灰 + 按级别着色）/ 列表（无选中底色） |
| `AreaChipTemplate` / `AreaItemsControlStyle` | 设备连接页「地址表元件」那排地址 chip |
| `IconFont` | 图标字体族（`Segoe Fluent Icons, Segoe MDL2 Assets`） |
| `EnumUpperConverter` / `EnumUpperTemplate` / `BoolToVis` | 枚举转大写 / 布尔转 `Visibility` |
| **隐式 `ScrollBar`**（**无 `x:Key`**） | 细滚动条（10px）。⚠️ 隐式样式是唯一能命中 `ScrollViewer` 内部创建的滚动条的手段，**别改成 keyed**。⚠️ **横向必须在 `ControlTemplate.Triggers` 里把 `PART_Track.IsDirectionReversed` 翻回 `False`**：模板那个 `True` 是给纵向的，横向不翻的话 `Value=0` 时滑块贴右端（2026-11-06 实测复现并修真，历史表格的横向条就是这么坏的） |



---

## 8.3 串口 Modbus RTU（P4.4，2026-11-18）

> 本节编号接在 §8.2 之后；物理位置在文件末尾（§15/§16 是查阅型大表，在其之后追加不打断它们）。

**用户三条定案**：引微软官方 `System.IO.Ports`（**打破原「零第三方依赖」红线**）、
串口真机验收**延后**（现场无设备）、默认 **9600 8N1**。

### 8.3.1 为什么必须抽协议层

`ModbusTcpClient` 原先**同时**负责 PDU 与 MBAP 装订，且采集引擎 / 心跳 / 写入 /
`ConnectionSession` 共 **41 处**引用这个具体类型。不抽的话加 RTU 只有两条路：
每处长一个 `if (isSerial)`，或复制一整套采集逻辑 —— 后者会让分块与失败策略慢慢走偏。

| 文件 | 职责 | 无 WPF |
|---|---|---|
| `Drivers/ModbusPdu.cs` | PDU 构造 / 解析 / 异常码 / 写回显校验，**传输无关** | ✅ |
| `Drivers/Crc16.cs` | RTU 校验（0xA001、初值 0xFFFF、**帧尾低字节在前**） | ✅ |
| `Drivers/IModbusClient.cs` | 6 读写 + 心跳 + 站号 + 连接状态 | ✅ |
| `Drivers/ModbusTcpClient.cs` | MBAP 装订 + TCP；PDU 改调 `ModbusPdu` | ✅ |
| `Drivers/ModbusRtuClient.cs` | 串口 + `SerialSettings` + 按功能码推算帧长 | ✅ |

⚠️ **连接方法刻意不在 `IModbusClient` 里**：TCP 要 IP/端口/本地绑定端点，RTU 要
COM 口 + 波特率 + 数据位 + 校验 + 停止位。硬塞进一个签名只能变成「一堆可空参数的万能连接」，
那种签名既看不懂也用不对。两种连接各自留在实现类上，由窗口按连接方式分支调用。

### 8.3.2 RTU 与 TCP 的三处本质差异（最容易埋坑的地方）

1. ⚠️ **RTU 没有长度字段**。收帧必须先读「站号 + 功能码」，再推算剩余字节：
   异常响应**恒 5 字节**；正常响应看**第 3 字节**声明的数据字节数。
   推错就整条流失步 —— 故 `ReadFrame` 与 `ValidateFrame` 都做成 `static`，
   只为能脱离串口独立断言。
2. ⚠️ **站号参与 CRC**。改站号后 CRC 必变，所以站号配错的表现是
   **「CRC 校验失败」而不是「站号不符」** —— 现场别一看 CRC 错就只怀疑线路。
3. ⚠️ **CRC 低字节在前**。写反与「CRC 算错」现象完全一样：从站静默丢弃、主机只见超时。

### 8.3.2b ⚠️ 写应答是 8 字节回显，**不能**按读应答解析（2026-11-18 真机踩中）

RTU 三类响应结构**完全不同**，必须先看功能码 + 请求类型再决定读多少字节：

| 响应类型 | 整帧长度 | 结构 |
|---|---|---|
| 异常应答 | **5 字节** | 站号 + (FC\|0x80) + 异常码 + CRC(2) |
| **写应答（回显）** | **8 字节** | 站号 + FC + 地址(2) + 值/数量(2) + CRC(2) —— **没有「字节数」字段** |
| 读应答 | **变长** | 站号 + FC + 字节数(1) + 数据(N) + CRC(2) |

**踩坑现象**（汇川 Easy @COM4，实际发生）：

- 写 `M0`（地址 `0x0000`）→ 把地址高字节 `0x00` 当成字节数 → 只读 5 字节就校验 CRC → **`IOException`**
- 写 `Y7`（地址 `0xFC07`）→ 把高字节 `0xFC`=252 当成字节数 → 死等 254 字节 → **`TimeoutException`**
- ⚠️ **写入其实早就成功了**：写前读、写后再读，值确实翻转了。只是应答被误解析，
  所以表现为「写失败但设备真的动了」—— 这种「成功了却报错」最难判断

**修法**：`ReadFrame` 增加 `isWrite` 参数，写路径**固定读 6 字节**（前 2 已读，合计 8）。
⚠️ 不要试图靠「第三字节像不像字节数」去猜 —— 地址高字节可任意取值，必然误判。

**诊断手法（值得复用）**：区分「请求被丢弃」与「写成功但不回包」，就**写前读一次、写后再读一次**。
只看异常类型会把「回包格式错」误判成「从站不支持写」。

**超时与清缓冲**（2026-11-20 重新设计）：单次请求的超时**下限** 2 s，实际上限按
「(请求字节 + 响应字节) × 每字节位数 ÷ 波特率 + 500 ms」推算（`TimeoutFor`）——
1200 baud 读 125 个寄存器时响应有 255 字节、光上线就 2.1 s，固定 2 s 会**每次必超时**。
接收超时按**一个请求一个截止时刻**（`ReadExact` 每次 Read 的 ReadTimeout = 剩余时间），
不是「每次 Read 各 2 s」——后者最多 3 段读会累加，实测慢速对端要 3001 ms，现在 2001 ms。
任何失败（超时 / 取消 / CRC 错 / IO 错）都 `DiscardInBuffer()` **并记一段静默保护**（见 §8.3.2c），
**发送前**还会再清一次缓冲。合法异常响应**不清理、不加静默**（帧已完整读完，串口可复用）。

### 8.3.2c ⚠️ 迟到帧会被当成「下一条请求的应答」——CRC 全对、静默错值（2026-11-20 实测）

RTU 没有事务标识，主机没有任何办法把一帧应答和某条请求对上号：**超时之后才到达的那一帧，
会被下一条请求当成自己的应答**。实测（`.tmp/RtuProbe`，ELTIMA 虚拟串口对 COM10↔COM11 自建假从站）：

| 迟到程度 | 现象 |
|---|---|
| 比超时**晚 10 ms**（落在「失败清缓冲之后、下一条请求窗口内」） | 改前：第 2 条读到第 1 条的 `0xFF`（位全 1）；改后：读到自己那条 `0x00`，代价仅 19 ms |
| 比超时晚 **300 ms**（远超任何静默保护窗口） | **仍会被下一条吃掉**（第 2 条 300 ms 后「成功」读到上一条的 `0xFF`）—— RTU 固有残余风险 |

危害在于那帧是**合法帧，CRC 必然通过**，异常不会抛、界面与日志都正常 —— CSV 里只是多了一段错值。
轮询反复读同一块（请求/应答长度恒定）恰好绕过 `ValidateReadByteCount` 这层长度校验，
所以最容易中招的正是最常走的那条路径。

**两条防线（已实现）**：

1. **发送前** `DiscardInBuffer()` —— 清掉「已经躺在缓冲里」的迟到帧。早期只在失败后清，
   挡不住「清完之后才到」的那一帧；
2. **失败后静默保护** `ResyncGuard()` = max(20 ms, 3.5 个字符时间) —— 期间到达的字节
   会在发送前被丢掉；顺带避免在总线上还剩半帧时立刻插话。

⚠️ **残余风险清不掉**：迟到帧若在静默保护**之后**才到，仍然分不出来（除非协议层有事务号）。
本应用的兜底是「链路失败连续 3 次即断线重连」（重开串口、缓冲归零，见 §8.3.3）——
**不要**为了「少掉线」把那个阈值调得很宽，否则这些错值会一直读下去。

### 8.3.3 UI 与持久化

- `SerialOptionsPanel` 与 `EthernetOptionsPanel` **互斥显隐**（`ConnectionTypeComboBox_SelectionChanged`）
- ⚠️ **连接按钮有两个，各贴在自家「刷新」右边**（用户 2026-11-18 定案）：
  以太网 `ConnectButtonEthernet`（`RefreshNicButton` 右侧）、串口 `ConnectButtonSerial`
  （`RefreshSerialButton` 右侧）。实测两者相对位置完全一致（刷新 x=842 w=90 → 连接 x=944 w=120）。
  ⚠️ **两个按钮的三态（连接/断开/取消重连）与启用态必须成对设置** —— 统一走
  `SetConnectButtonText` / `SetConnectButtonEnabled` / `SetConnectButtonHeight` 三个方法，
  **不要直接碰按钮属性**，漏一个的表现是「切换连接方式后按钮文案停在旧状态」。
  历史教训：更早版本把按钮放在 `EthernetOptionsPanel` 里，串口模式折叠该面板后
  **按钮整个消失、串口根本点不到连接**（UIA 实测 `ConnectButton` 不在树）
- ⚠️ **与连接方式无关的控件，一律放在两个互斥面板之前**（`AutoConnectPlcToggle` 即此类）：
  放进互斥面板内 → 切到另一模式时**消失**；放在面板之后但仍是表单末行 → 因两种模式上方行数不同，
  **位置上下跳**（实测串口 y=678 / 以太网 y=867）。故统一下沉到「连接方式」上方（y=363 恒定）。
  自检：切换连接方式后，该控件 `BoundingRectangle` 应完全不变
- ⚠️ **协议类型由连接方式唯一决定，下拉里只列对应那一项**（`SyncProtocolOptions`）：
  以太网 → `ModbusTCP`，串口 → `ModbusRTU`。
  **不要退回「XAML 静态写两项 + 只改 SelectedIndex」**（2026-11-18 用户指出并修掉）：
  两项都在时用户能选中「以太网 + ModbusRTU」，那个组合不生效（连接仍按 TCP 走）却
  不报任何错 —— 属最难查的静默错配。只留一项**不是禁用**，仍可点开确认
  （同扫码器连接页 2026-11-04 定案「只有一项也应能点开」）。
  ⚠️ 条目是运行时填入的，故**启动时必须在构造函数里显式调一次** `SyncProtocolOptions()`
  —— 不能指望「XAML 的 SelectedIndex=0 触发 SelectionChanged」，那条路径已不存在；
  漏了的表现是**协议框空白 + 底栏缺协议段**（实测踩过）
- 程序改写协议框时罩 `_syncingProtocol`，否则会多刷一次底栏
- **串口下拉显示「端口 · 设备名」**（2026-11-18 用户要求），如 `COM4 · USB-Enhanced-SERIAL CH343`
  - 实现在 `Net/SerialPortScanner.cs`（无 WPF）：`SerialPortInfo { PortName, Description }`，
    `ToString()` 出显示文本；端口名来自 `SerialPort.GetPortNames()`，
    设备名来自注册表 `HKLM\SYSTEM\CurrentControlSet\Enum\**\Device Parameters\PortName` +
    同层 `FriendlyName`（实测 CH343 / CH340 / CP210x / ELTIMA 虚拟口全命中）
  - ⚠️ **刻意不用 WMI**（`Win32_PnPEntity`）：那要额外引 `System.Management` 包，
    而本项目只有一个依赖（串口驱动）。注册表遍历零依赖，拿到的是同一份 FriendlyName
  - ⚠️ **设备名只用于显示**：连接与持久化一律用 `PortName`。取来的是
    `USB-SERIAL CH340 (COM4)` 这种自带端口后缀的串，`StripPortSuffix` 会剥掉，
    否则下拉框里端口号重复两遍
  - ⚠️ **取不到设备名不影响可用性**：任何一步失败都退回「只显示端口名」，
    列表绝不为空
  - ⚠️ **只显示 `GetPortNames()` 返回的口**（当前真实存在）。注册表里还有一堆
    已拔设备的残留记录（实测本机 10 个注册、只有 3 个可用），不能拿注册表当端口来源
  - ⚠️ 下拉框装的是**对象**不是字符串 —— 取当前端口一律走窗口的 `SelectedPortName()`。
    曾经到处写 `SelectedItem as string`，改对象后那些地方会静默变 `null`
    （表现为「明明选了串口却说未选」）
- 串口下拉 `ScanSerialPorts` 整段罩 `_restoringSerialSelection`（同网卡那条坑：
  `Items.Clear()` 也触发 `SelectionChanged`，不挡会把用户选择抹掉），
  且**按数字排序**（默认字符串排序会让 COM10 排在 COM2 前面）
- **校验位 / 停止位的解析只此一处**（`ReadParityFromUi` / `ReadStopBitsFromUi`）：
  读写各写一份 switch，一旦漂移就是「改了但落盘的是旧值」，界面看不出异常
- 持久化 `ThemeManager.Serial*` + `StationNumber`；**默认值存空串 / 0**，
  旧 json 缺字段无缝升级（与 `TargetIp` / `TargetPort` 同一不变式）
- ⚠️ **`_restoringSerialSelection` 的初值必须是 `true`**（`private bool ... = true;`，2026-11-20 实测踩中）：
  `InitializeComponent()` 里 XAML 的 `SelectedIndex` 会触发 `SerialSetting_Changed`，
  **那时用户设置还没回填**（回填在构造函数后面的 `ApplyConnectionSettings`）。
  初值给 `false` 的话，这几次回调会把默认值（9600/8/None/One/1）写进
  `ThemeManager.SetSerialSettings`，**把刚加载的用户设置覆盖掉**，之后回填读到的自然是默认值。
  症状：**参数能正确落盘，但重启后完全不恢复，而且 json 里的用户值被改回默认** ——
  看起来像「没做持久化」，实际是持久化被自己的初始化流程抹掉了。
  生命周期：构造期恒 `true` → `ApplySerialSettings` 回填完置 `false` → 此后用户改动才落盘
- ⚠️ **站号 TCP 与 RTU 共用**：它表达「在总线上是谁」，不因传输方式而变；分两套存
  会让「同一个 PLC 换个连法就答不上」
- ✅ **连接方式本身也持久化**（2026-11-20 用户指出后补）：`app-settings.json` →
  `UseSerialConnection`（`false` = 以太网，默认；`true` = 串口）。
  「串口六项都记了、唯独选了哪种方式不记」说不通 —— 串口用户每次启动都要手动切一次。
  - 读写：`ThemeManager.UseSerialConnection` / `SetUseSerialConnection`（值没变不写盘）
  - 回填：`RestoreConnectionType()`，且**必须排在 `ApplySerialSettings()` 之前** ——
    串口面板要在选中「串口」后才扫描端口，`SerialPortComboBox` 为空时 `ApplySerialSettings`
    选不中任何项（顺序反了就是「连接方式记住了但串口号没恢复」）
  - ⚠️ 回填罩 `_restoringConnectionType`：赋值会触发 `SelectionChanged` → 回调尾部的
    `SetUseSerialConnection` 再写一遍盘
  - ⚠️ 默认值（以太网）不显式存：旧 json 缺字段 → 反序列化得 `false` → 停在以太网，
    与升级前一致，无需迁移
  - ⚠️ **切换连接方式时表单必须是可用的**：连接中 `SetInputsEnabled(false)` 会把它一起禁用，
    UIA 表现为 `IsKeyboardFocusable=False`、`SetFocus()` 抛「目标元素无法接收焦点」。
    这是**设计如此**（连接中锁定表单），不是 bug —— 做自动化验证前要先断开，否则会误判
- ⚠️ **`SerialPortComboBox` 必须挂 `SelectionChanged="SerialSetting_Changed"`**（2026-11-20 发现并修，**头号坑**）：
  串口参数落盘的唯一入口就是这个处理器（下拉框没有失焦语义，见上条）。端口下拉漏了它 =
  **只换端口 → 关程序 → 重启还是旧端口**；更阴的是「改波特率时会把当前端口一起存了」，
  所以「六项一起改 → 重启 → 全恢复」那种验法**验不出来**。验法必须：只改串口号 → 看 json 有没有变 → 重启。
- ⚠️ **上次记住的串口这次不在列表里时，不要落到第 0 项**：`ScanSerialPorts` 原先
  `index < 0` 就选第一项 —— 界面看着像「自动选好了」，实际指着另一个设备，配「自动连接」
  会直接连错（现场很难反应过来）。现在：**保持空选** + 运行日志提示一次（`_notifiedMissingSerialPort` 去重），
  点连接时由现成的「请选择串口号」兜住。只有「从没选过端口」才默认落到第一项。
- ⚠️ **停止位「1.5」已从下拉删除**（2026-11-20 实测）：Windows 只允许 1.5 停止位配 **5 数据位**，
  而本应用数据位只有 7/8 → 选了它连接必抛 `IOException`「参数错误。」，是永远用不了的选项。
  旧 `app-settings.json` 里若存着 `OnePointFive`，回填时 `SelectComboByContent` 找不到该项会保持默认（1），不会崩。
- ✅ **串口连接后做一次「探测读」**（`ProbeSerialAsync`，2026-11-20）：串口**打开成功不代表总线上有设备**
  （以太网连接成功则天然证明对端可达）。首次连接后读一次心跳线圈（地址取
  `ModbusHeartbeat.TryGetHeartbeatAddress`）：
  - 有应答（**含合法异常码** —— 那只是地址不认，链路是通的）→ 正常「已连接」；
  - 无应答 → **连接保留**、状态「已连接（设备未应答）」琥珀 + 日志警告，把「站号 / 接线 / 波特率」
    从「2 秒后超时才暴露」提前到点连接时点出来；之后第一次成功会自动变绿（日志规则 5）。
  - ⚠️ **只在首次连接探测**（重连不探测），否则每次退避重试都白多花一个请求的时间。
- ⚠️ **链路级失败容忍 3 次才断线**（`ModbusPoller.TransportFailureThreshold`，2026-11-20）：
  超时 / CRC 错 / IO 错不再「一次就断线 + 自动重连」（RS485 现场偶发干扰很常见，表现为「时不时掉线」）。
  判据是 **`client.IsConnected`**：TCP 请求失败时 `RequestAsync` 已 Dispose 客户端 → 立即断线（行为不变）；
  串口端口仍然开着 → 计数，连续 3 次才断。
  ⚠️ **心跳成功必须一起清零**（`ModbusHeartbeat.RunAsync` 的 `onResponded` → `NoteTransportSuccess`），
  否则「轮询偶发失败 + 心跳正常」的计数只增不减，攒够阈值就误断线。
  ⚠️ 这也是 §8.3.2c 残余风险的兜底：错值最多影响 3 拍就会通过重连归零。
  ⚠️ **两个配套前提，少一个这条容忍就是坏的**（2026-11-20 真机各踩一次）：
  1. `ModbusHeartbeat.RunAsync` 必须接 `tolerateFailure` 回调 —— 失败**不能上抛后退出循环**，
     否则「再也不心跳」，阈值永远凑不满，设备全哑了界面还停在已连接；
  2. 轮询 `PollLoopAsync` 只有在 **`_requestsThisTick > 0`**（这一拍真发过报文）时才清零计数 ——
     空拍什么也没证明，无条件清零会让失败计数永远归零。
  ⚠️ 重连也要走探测读（`isReconnect` 同样探测）：否则重连成功后先亮绿、几秒后再红，
  表现为**每 8 秒一次的假绿灯**，比一直红更误导。重连探测失败按「这次没连上」静默退避。
- ⚠️ **取消打断不了串口读**：实测 300 ms 取消、请求要 2000 ms 才返回（`SerialPort.Read` 不认 CancellationToken）。
  所以 `Disconnect` **必须等采集/心跳真正退出后再释放客户端**（`ReleaseClientAfterPollAsync`，注释已固化）——
  别改成「立刻释放」，那等于在正在读的端口上 Dispose。
- ⚠️ **`ValidateFrame` 先 CRC 后站号**（2026-11-20 调整）：帧被干扰时站号字节自己也可能被打坏，
  先比站号会把「CRC 错」误报成「站号不符」，把人引到改站号的歧路；站号真配错时从站回的帧 CRC 是**好的**，
  这时才该报站号不符。
- ⚠️ **断线原因归类补了串口类别**（`Logging/DisconnectReasonClassifier.cs`）：串口抛的是文件/路径措辞的英文
  （`Access to the path 'COM4' is denied.` / `Could not find file 'COM4'.`），原样透出等于没提示；
  另外 `Modbus ...` **不再一律压成「响应帧校验失败」** ——「站号不符（期望 1，实际 2）」「响应 CRC 校验失败」
  「响应数据长度不符」「写应答与请求不符」各自成句（`LoggingCheck` 有 10 条断言锁住）。
- **验证口径**：改值 → 看 json → **重启** → 看界面。⚠️ 只验「json 里有值」不够 ——
  上面那个 bug 正好是「json 有值但启动被覆盖」，必须走完整往返。
  ⚠️ 串口**要单独验「只改端口」**：混在「六项一起改」里会被别的字段顺带存上而假通过。

### 8.3.4 验证与未验

- `.tmp/RtuCheck` **56 项离线断言全通过** —— CRC 官方已知向量（`01 03 00 00 00 0A` → `0xCDC5`）、
  帧尾字节序、PDU 构造/解析、异常码、写回显校验、读字节数推算、整帧拼装、参数模型、两种目标
- **TCP 真机回归**：PDU 层重写后重跑 `.tmp/HeartbeatProbe`（信捷 `192.168.1.66:502`）**11 项全通过**
  —— 这是本次改动**最大的风险点**（动了所有 TCP 报文路径）
- ✅ **真机已验收**（2026-11-18，汇川 Easy @ **COM4 9600 8N1 站号 1**，`.tmp/RtuLive` **19 项全通过**）：
  FC01 读（35 ms）、FC05 写 ON/OFF 并回读确认、连续 20 次读稳定（555 ms）、
  **变长读 1/8/9/16/17 点**（验帧长推算跨字节边界）、FC06 写寄存器回读、FC16 写多寄存器回读、
  非法地址回异常码 2 且**连接保持**（异常后可继续正常读）。
  ⚠️ 验收中发现并修掉一个真 bug：**写应答被按读应答解析**，见 §8.3.2b
- ✅ **串口参数持久化**（2026-11-20）：串口号 / 波特率 / 数据位 / 校验位 / 停止位 / 站号
  **六项**改值 → 落盘 → 重启 → 全部正确恢复。
  ⚠️ 该 bug 的症状是「能落盘但重启不恢复、且 json 被改回默认」，见上条 `_restoringSerialSelection`
- ✅ **寄存器「类型语义层」**（2026-11-18，`.tmp/RegLive` **29 项**）：
  验「值 → `ValueCodec` 编码 → 真串口 → PLC → FC03 读回 → `ValueCodec` 解码」整条链路，
  覆盖 DINT/REAL/INT/UINT/STRING 全部类型 + 3 套字节顺序 + 16 位边界位模式 + DEC/HEX/BIN 显示
- ⚠️ **`DecodeInteger` 返回的是「位模式」，不是有符号值**：
  `INT` 的 `0x8000` 经它返回 **32768**，符号解释在格式化层（`FormatRegisters` → `Interpret`）。
  **验有符号性要验 `FormatRegisters`**，拿 `DecodeInteger` 跟 `-32768` 比会误报 bug
- ✅ **CRC 双向验证**（2026-11-18）：
  - **从站侧**（`.tmp/CrcLive`，真机 COM4）：CRC 低字节/高字节/两字节取反，三种坏帧**全被静默丢弃**；
    正确 CRC 正常应答；「改数据但 CRC 不动」同样丢弃；错误帧之后好帧仍能应答
  - **接收侧**（`.tmp/CrcRecv`，**ELTIMA 虚拟串口对 COM10↔COM11** 自建假从站）：回坏 CRC →
    抛「CRC 校验失败」而非当有效值；站号不符 → 抛「站号不符」；不回包 → 超时；坏帧后连接未被毒化
  - ⚠️ 接收侧**真机做不到**（PLC 从不发坏帧），只能用虚拟串口对。
    `CrcRecv <被测口> <假从站口> <站号>` 可重跑
  - ⚠️ 写这类测试要先自检「坏帧变体是否真的变了」：传错参数等于发的好帧，会得出相反结论
- ✅ **信捷 XD 串口**（2026-11-20，`.tmp/XinjeLive` **24 项**，COM4 9600 8N1 站号 1）：
  线圈 M/Y 区 FC05 写回读、FC01 多点读；寄存器 D 区 FC06/FC16、DINT、STRING
  - **八进制编号实测**：`Y7=0x6007` / `Y10=0x6008` / `Y17=0x600F` —— **Y10 只比 Y7 大 1**（八进制进位）
  - ⚠️ **本机信捷允许写只读区 X**（FC05 写 `X0` 未被拒）。设备行为，非 bug；
    应用仍按只读处理（`IsReadOnlyElement` 拦「写」不拦「映射」）。现场若依赖「X 写不进去」需注意
- **仍未验**：真实拔线（USB 转串口拔出）时的断线判定与自动重连（CRC 已双向验完，见上一条）；
  1200/2400 baud 的大块读（虚拟串口不模拟波特率时序，只能靠公式推）
- ✅ **2026-11-20 运行时探测**（`.tmp/RtuProbe`，**ELTIMA 虚拟串口对 COM10↔COM11** 自建假从站，
  可重跑 `RtuProbe.exe COM10 COM11`，`--classify` 只跑文案映射）：
  - 连接失败原文 + 归类 4 项（端口被占用 / 端口不存在 / 端口名非法 / 波特率 0）
  - 非法参数组合 3 项（**1.5 停止位 + 8 数据位抛 `IOException`「参数错误。」**、Mark 校验、7E1 对照）
  - 单次请求耗时：无应答 2005 ms、只回 1 字节后静默 2000 ms、正常应答 **1 ms**
  - **慢速对端**（每 1.5 s 吐 1 字节）：**改前 3001 ms → 改后 2001 ms**（截止时刻生效）
  - **取消延迟**：300 ms 取消、请求 2000 ms 才返回（确认「读打断不了」）
  - **迟到帧两种窗口**：晚 10 ms（改后挡掉、代价 19 ms）与晚 300 ms（挡不掉，见 §8.3.2c）
  - `SerialPortScanner.Scan()` 23–31 ms（UI 线程，可接受）
- ✅ **2026-11-20 修复后回归**：`.tmp/RtuCheck` **56 项**全通过；`LoggingCheck` **54 项**全通过
  （新增 10 条串口/Modbus 文案断言）；`.tmp/ExpressionCheck` 211 项全通过
- ✅ **2026-11-20 真机烟测**（信捷 XD @ COM4 9600 8N1 站号 1，UI Automation 实测）：
  启动自动连接 → **探测读有应答** → 状态「已连接」+ 底栏 `信捷XD/XL系列：串口  ModbusRTU  COM4 9600 8N1`；
  端口下拉改 COM11 → `app-settings.json` 的 `SerialPortName` **立刻变 COM11**（改前不会变，见 §8.3.3 头号坑）；
  改回 COM4 → 重连正常
- ✅ **2026-11-20 真机完整验证**（信捷 XD @ COM4 真机 + ELTIMA COM10↔COM11 假从站
  `.tmp/UnplugSlave`，UIA 逐秒采样状态栏，全程实测）：

  | 场景 | 怎么做 | 实测结果 |
  |---|---|---|
  | **站号配错**（站号 2 / 设备 1） | 改站号框 → 点连接 | 2.6 s 后 **「已连接（设备未应答）」琥珀**，日志 1 条「但设备未应答（请检查站号/接线/波特率）」；约 **8 s 后**才转「已断开：响应超时」 |
  | **重连时设备仍不应答** | 上一条之后继续观察 35 s | **不再出现假绿灯**：0.6 s 即进入「正在重连 …」，退避 1/2/4/5 s 稳定重试，日志**不刷屏** |
  | **掉电 / 拔线**（假从站置 `mute`） | 连接正常后中途停止应答 | **7 s 后**判「已断开：响应超时」→ 自动重连（1/2/4/5 s）→ 恢复应答后 **1.4 s 自动连回** |
  | **线路干扰**（假从站回坏 CRC） | 中途置 `crc` | 判「已断开：**响应 CRC 校验失败**」（文案细分生效，不再统一说「响应校验失败」）→ 恢复后 2.2 s 连回 |
  | **正常路径稳定性** | 正确站号连跑 60 s | **状态变化 0 次**（无误断线） |
  | **停止位选项** | 展开下拉 | 只剩 `1 / 2`（`1.5` 已删，见 §8.3.3） |
  | **串口号落盘** | 只改端口 → 看 json | 立即变（`#1` 修复生效） |
  | **设备真的不在**（管理员禁用 CH343 后点刷新） | 先断开应用 → `Disable-PnpDevice` 成功 → 点「刷新」 | 下拉**保持空选**、不回落第 0 项；日志一条「串口 COM4 当前不可用（已拔出或被系统改了端口号），请重新选择端口」；点连接被「请选择串口号」拦住（`#9` 真机确认） |
  | **端口被别的程序占用** | PowerShell `Open()` 占住 COM4 → 应用连 | 底栏「连接失败：**串口被占用（其他程序正在使用该端口）**」，日志同文案（`#6` 真机确认） |
  | **配对端瞬间消失**（杀假从站进程） | 连接中杀进程 | **5 s** 判断开 → 退避重连（1/2/4/5 s，7 次以上稳定）→ 恢复后 **7.7 s 自动连回**、日志不刷屏 |

  ⚠️ **真机暴露并已修掉的两个新 bug**（都是「改成容忍失败」之后才显形的）：
  1. **心跳循环失败一次就退出**（`ModbusHeartbeat.RunAsync` 原实现让异常上抛）——
     容忍后没人再心跳，阈值永远凑不满，设备彻底不应答却一直显示「已连接（设备未应答）」。
     改为 `tolerateFailure` 回调：可容忍就继续下一拍，不可容忍才上抛。
  2. **空拍被当成「链路正常」**（`PollLoopAsync` 每拍无条件 `NoteTransportSuccess`）——
     界面上没有可采的量时每拍都是空跑，失败计数被反复清零。改为只在
     `_requestsThisTick > 0` 时清零。
  3. ⚠️ **重连也要探测**：早先只在首次连接探测，站号错时会「每 8 秒亮一次假绿灯」再变红；
     现在重连探测失败直接按「这次没连上」静默退避（不写日志、不亮绿）。
- ⚠️ **真拔 USB 转串口**这一形态**仍未能直接复现**（2026-11-20 二次尝试，已提权到管理员）：
  - `Disable-PnpDevice` / WMI `Disable()` 在**端口被占用时必定失败**（报「常规故障」）——
    这是 Windows 的保护，不是本应用的问题；先断开应用再禁用则**能成功**（已验证：COM4 从端口列表消失、
    `Get-PnpDevice` 变 `Error`、下拉框正确保持空选并提示「串口 COM4 当前不可用」，见 §8.3.3 那条）。
  - `pnputil /remove-device` 会返回 `exit=3010`「需要重启完成」，**重启前设备仍可正常收发** ——
    实测移除后应用照样连上并通信正常（日志 `PLC已连接：COM4`），只是注册表描述查不到、
    下拉框条目退化成纯 `COM4`（正好印证「设备名只是锦上添花」这条不变式）。
  - `pnputil /restart-device` 被「待重启」标记阻塞（`This command is not supported on this OS product`）。
  - **结论**：软件层无法在不重启的前提下真正制造「句柄失效」。等效验证改用
    **关闭配对端句柄**（杀掉 `.tmp/UnplugSlave` 假从站进程）—— 总线瞬间没有任何应答，与拔线一致：
    实测 **5 s** 判断开 → 退避重连（1/2/4/5 s，7 次以上稳定）→ 从站恢复后 **7.7 s 自动连回**。
- ⚠️ **端口被其他程序占用**（真机：PowerShell 先 `Open()` 占住 COM4，再让应用连）：
  底栏「连接失败：**串口被占用（其他程序正在使用该端口）**」、日志同样文案 —— §8.3.3 的归类生效。
  ⚠️ 顺带实测到一条：**端口被占用时注册表查不到设备名**，下拉条目退化成纯 `COM4`。
  这是预期的降级行为（见 §8.3.3「取不到设备名不影响可用性」），但**排查时容易误以为「设备没识别」**。
- ⚠️ **发布产物变了**：多 `System.IO.Ports.dll`（34.7 KB），共 5 个文件。
  漏拷时**应用照常启动、以太网照常连**（CLR 惰性解析），只在点串口连接时抛
  `FileNotFoundException` —— 见 `AGENTS-发布.md` §12.2
