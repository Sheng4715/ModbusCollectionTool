using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一条「全局变量」（P3.3）。
    /// <para>
    /// 与 <see cref="DataPoint"/>（自由监控 / 采集）的区别只有两点：
    /// </para>
    /// <list type="bullet">
    /// <item>变量有**名字**（C 标识符规则），可作为后续数据管理 / MES 上报的引用键；
    /// <see cref="DataPoint"/> 只用元件地址标识自己。</item>
    /// <item>变量的映射地址**可以为空** —— 此时它是一只纯内存变量，值由用户手工改写，
    /// 不与任何 PLC 地址联动。<see cref="DataPoint"/> 必须能解析出地址。</item>
    /// </list>
    /// <para>
    /// ⚠️ 值**不持久化**：按需求，每次启动所有变量的值初始化为 0 / 空串，
    /// 只有**定义**（名称 / 类型 / 映射地址 / 注释）落盘到 <c>GlobalVar.json</c>。
    /// </para>
    /// </summary>
    public sealed class GlobalVariable
    {
        /// <summary>变量名。C 标识符规则，最长 <see cref="GlobalVariableRules.MaxNameLength"/> 字符。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>数据类型。决定可映射的地址区：BOOL → 线圈区，其余 → 寄存器区。</summary>
        public DataType Type { get; set; } = DataType.Int;

        /// <summary>显示格式（须在该类型的 AllowedFormats 内）</summary>
        public DisplayFormat Format { get; set; } = DisplayFormat.Dec;

        /// <summary>
        /// 按类型取默认显示格式（<c>BOOL</c> → <c>BIN</c>、<c>STRING</c> → <c>ASCII</c>、其余 → <c>DEC</c>）。
        /// <para>⚠️ 不要写死 <c>Dec</c>：线圈只允许 <c>BIN</c>，写死会让新建的 BOOL 变量立刻校验失败。</para>
        /// </summary>
        public static DisplayFormat DefaultFormatFor(DataType type) =>
            DataTypes.Get(type).DefaultFormat;

        /// <summary>仅 <c>STRING</c> 使用：用户声明的字符数</summary>
        public int Length { get; set; }

        /// <summary>
        /// 映射地址，如 <c>HD100</c>；**空串 = 不关联地址**（纯内存变量）。
        /// <para>⚠️ 这里存的是**元件名**，不是 Modbus 绝对地址 —— 绝对地址由驱动按当前设备类型
        /// 重新解析，换设备类型后仍然有效。</para>
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>注释。仅给人看，不参与读写、不上报。</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>
        /// 当前值（显示格式下的文本）。**不持久化**：每次启动由
        /// <see cref="GlobalVariableSet.ResetValues"/> 重置为「0」/ 空串。
        /// </summary>
        [JsonIgnore]
        public string Value { get; set; } = "0";

        /// <summary>解析后的 Modbus 绝对地址；未关联或解析失败时为 −1。**不持久化**。</summary>
        [JsonIgnore]
        public int ResolvedAddress { get; set; } = -1;

        /// <summary>解析后的存储区。**不持久化**。</summary>
        [JsonIgnore]
        public ModbusArea Area { get; set; } = ModbusArea.Coil;

        /// <summary>该映射区是否只读（方案 C：X 等输入元件只读）。**不持久化**。</summary>
        [JsonIgnore]
        public bool IsReadOnly { get; set; }

        /// <summary>解析 / 校验失败原因；正常时为空。**不持久化**。</summary>
        [JsonIgnore]
        public string ResolveError { get; set; } = string.Empty;

        /// <summary>是否关联了地址（空地址 = 内存变量）</summary>
        [JsonIgnore]
        public bool HasAddress => !string.IsNullOrWhiteSpace(Address);

        /// <summary>本项占用的寄存器（或线圈）个数</summary>
        [JsonIgnore]
        public int RawCount => Type == DataType.Bool
            ? 1
            : DataTypes.Get(Type).RegistersForLength(Length);

        /// <summary>类型显示名，如 <c>DINT</c> / <c>STRING[9]</c></summary>
        [JsonIgnore]
        public string TypeDisplayName => Type == DataType.String
            ? $"STRING[{Length}]"
            : DataTypes.Get(Type).DisplayName;
    }

    /// <summary>
    /// 映射地址解析失败的原因类别 —— 决定界面弹哪一句提示。
    /// <para>
    /// 分类的原因是**用户的改法完全不同**：类型不符要改数据类型或换元件，
    /// 软元件无效要改地址本身，只读区要换一个可写的元件。笼统报一句「地址无效」
    /// 会把人引向错误方向。
    /// </para>
    /// </summary>
    public enum AddressErrorKind
    {
        /// <summary>解析通过（或空地址 = 内存变量）。</summary>
        None,

        /// <summary>软元件与当前数据类型不符（如 BOOL 配 D100、INT 配 M10）。</summary>
        TypeMismatch,

        /// <summary>软元件无效：符号不存在、编号越界、格式不对、超出地址范围。</summary>
        InvalidElement,

        /// <summary>
        /// 软元件属于**只读区**（X 等输入元件 / 离散输入 / 输入寄存器）。
        /// <para>
        /// 读得到、写不了 —— 见 <c>GlobalVariable.IsReadOnly</c>。
        /// </para>
        /// </summary>
        ReadOnlyArea,

        /// <summary>与地址无关的配置问题（显示格式、STRING 长度、未选设备类型）。</summary>
        Configuration,
    }

    /// <summary>变量名与初始值、映射地址的规则（纯函数，可独立断言）。</summary>
    public static class GlobalVariableRules
    {
        /// <summary>变量名最大长度（需求：限制 32 个字符）。</summary>
        public const int MaxNameLength = 32;

        /// <summary>
        /// 校验变量名：**C 语言标识符规则** —— 首字符为字母或下划线，
        /// 其后为字母 / 数字 / 下划线；最长 32 字符。
        /// <para>
        /// 中文、空格、减号、点号一律拒绝。这样变量名才能被后续的数据管理、
        /// MES 上报当作字段名直接使用，不必再做转义。
        /// </para>
        /// </summary>
        public static bool TryValidateName(string? name, out string error)
        {
            error = string.Empty;
            name ??= string.Empty;

            if (name.Length == 0)
            {
                error = "变量名称不能为空";
                return false;
            }

            if (name.Length > MaxNameLength)
            {
                error = $"变量名称最多 {MaxNameLength} 个字符（当前 {name.Length}）";
                return false;
            }

            char first = name[0];
            if (!(first == '_' || IsAsciiLetter(first)))
            {
                error = "变量名称必须以字母或下划线开头";
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(c == '_' || IsAsciiLetter(c) || (c >= '0' && c <= '9')))
                {
                    error = "变量名称只能包含字母、数字和下划线";
                    return false;
                }
            }

            // 系统变量也算全局变量：名字被它占着，用户不能再定义一个同名的。
            // 否则表达式里的标识符就有歧义（虽然求值顺序定死了「系统变量优先」，
            // 但那种「看起来一样、行为不一样」的配置最难排查）。
            // ⚠️ 用 Ordinal 精确比对：变量名本身区分大小写，不搞前缀保留那一套。
            if (SystemVariable.Find(name) != null)
            {
                error = $"“{name}”是系统变量，不能重复定义";
                return false;
            }

            return true;
        }

        private static bool IsAsciiLetter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        /// <summary>
        /// 该类型的变量允许映射到哪些元件（方案 C）。
        /// <para>
        /// <c>BOOL</c> → 线圈区元件（X / Y / M / S / SM / HM …）；
        /// 其余 → 寄存器区元件（D / SD / HD / HSD / SFD / R / B …）。
        /// </para>
        /// <para>⚠️ 是否**只读**由元件决定（X 只读），不在这里判断。</para>
        /// </summary>
        public static bool CanMapTo(DataType type, ModbusArea area)
        {
            bool coilOnly = DataTypes.Get(type).CoilOnly;
            return coilOnly
                ? area is ModbusArea.Coil or ModbusArea.DiscreteInput
                : area is ModbusArea.HoldingRegister or ModbusArea.InputRegister;
        }

        /// <summary>
        /// 换数据类型后，原映射地址是否**必然作废**（决定界面要不要清空地址框）。
        /// <para>
        /// 判据只看**存储区**：<c>BOOL</c> 走线圈、其余走寄存器。
        /// </para>
        /// <list type="bullet">
        /// <item><c>INT → REAL</c>（同在寄存器区）→ <c>false</c>，地址仍然有效，保留；</item>
        /// <item><c>BOOL → INT</c>（线圈 ↔ 寄存器）→ <c>true</c>，地址必然作废，必须清。</item>
        /// </list>
        /// <para>
        /// ⚠️ 不要退化成「只要换类型就清」：同区换类型时地址完全可用，
        /// 清掉只是让用户重打一遍。也不要退化成「不清」：跨区换类型留下的地址
        /// 必然校验失败（<see cref="AddressErrorKind.TypeMismatch"/>），等于留一条坏配置。
        /// </para>
        /// </summary>
        public static bool AddressInvalidatedBy(DataType oldType, DataType newType) =>
            DataTypes.Get(oldType).CoilOnly != DataTypes.Get(newType).CoilOnly;

        /// <summary>
        /// 该变量的初始值（需求：每次启动初始化为 0）。
        /// <para>按显示格式给出：<c>BOOL</c> → <c>0</c>；<c>STRING</c> → 空串；其余 → <c>0</c>。</para>
        /// </summary>
        public static string InitialValue(DataType type) =>
            type == DataType.String ? string.Empty : "0";

        /// <summary>
        /// 映射地址校验失败时给用户看的**标题**与**正文**（纯函数，可独立断言）。
        /// <para>
        /// 文案由用户定稿：类型不符 → 「软元件与当前数据类型不符合。」；
        /// 软元件无效 → 「{输入内容}不是有效软元件」。放在这里而不是窗口里，
        /// 是为了让离线断言能直接核对这两句，不必跑起界面。
        /// </para>
        /// </summary>
        /// <param name="kind">解析失败类别</param>
        /// <param name="input">用户输入的地址原文（回显到「不是有效软元件」里）</param>
        /// <param name="detail">解析器的详细原因（配置类问题原样转达）</param>
        /// <param name="title">提示框标题</param>
        /// <param name="body">提示框正文</param>
        public static void DescribeAddressError(
            AddressErrorKind kind, string? input, string? detail, out string title, out string body)
        {
            input ??= string.Empty;
            detail ??= string.Empty;

            switch (kind)
            {
                case AddressErrorKind.TypeMismatch:
                    title = "数据类型不符";
                    body = "软元件与当前数据类型不符合。";
                    return;

                case AddressErrorKind.InvalidElement:
                    // 与另两处名称弹窗同一排版：正文最前面是**用户刚输入的内容**（外加双引号）
                    title = "软元件无效";
                    body = input.Length == 0 ? "不是有效软元件" : $"“{input}”不是有效软元件";
                    return;

                case AddressErrorKind.ReadOnlyArea:
                    // 说清「为什么不行」+「还能怎么用」，否则用户只会反复换编号重试
                    title = "只读区";
                    body = input.Length == 0
                        ? "该元件属于只读区，不能作为映射地址。"
                        : $"“{input}”属于只读区（输入元件），读得到但写不了，不能作为全局变量的映射地址。";
                    return;

                default:
                    // 与地址无关的配置问题（显示格式 / STRING 长度 / 未选设备类型）
                    title = "映射地址无法解析";
                    body = detail.Length > 0 ? detail : "映射地址无法解析，请检查设备类型与地址格式。";
                    return;
            }
        }
    }

    /// <summary>
    /// 全局变量集合的持久化与校验（<c>GlobalVar.json</c>，与 <c>data-points.json</c> 同模式）。
    /// <para>⚠️ 只存**定义**，不存值。</para>
    /// </summary>
    public static class GlobalVariableStore
    {
        private const string FileName = "GlobalVar.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 与 data-points.json 一致：枚举写成名字，避免枚举顺序变化后静默错位
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<GlobalVariable> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return new List<GlobalVariable>();
                }

                string json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<GlobalVariable>>(json, Options);
                return list ?? new List<GlobalVariable>();
            }
            catch
            {
                // 与 DataPointStore.Load 一致：读失败不阻断启动，视为空集合
                return new List<GlobalVariable>();
            }
        }

        public static bool Save(IEnumerable<GlobalVariable> variables)
        {
            try
            {
                string json = JsonSerializer.Serialize(variables, Options);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 用驱动解析一条变量的映射地址。空地址视为合法（内存变量），直接返回 true。
        /// </summary>
        /// <param name="v">要解析的变量，成功时回填 ResolvedAddress / Area / IsReadOnly</param>
        /// <param name="driver">当前设备类型的驱动</param>
        /// <param name="error">失败原因（详细，供行内标红与日志）</param>
        public static bool TryResolve(GlobalVariable v, CommDriver driver, out string error)
            => TryResolve(v, driver, out error, out _);

        /// <summary>
        /// 同上，额外给出失败**类别** —— 界面按类别决定弹哪一句提示。
        /// </summary>
        /// <param name="kind">
        /// <see cref="AddressErrorKind.TypeMismatch"/> = 软元件与数据类型不符；
        /// <see cref="AddressErrorKind.InvalidElement"/> = 软元件无效（符号/编号/范围）。
        /// </param>
        public static bool TryResolve(
            GlobalVariable v, CommDriver driver, out string error, out AddressErrorKind kind)
        {
            error = string.Empty;
            kind = AddressErrorKind.None;

            if (v == null || driver == null)
            {
                error = "参数为空";
                kind = AddressErrorKind.Configuration;
                return false;
            }

            v.ResolvedAddress = -1;
            v.ResolveError = string.Empty;
            v.IsReadOnly = false;

            // 不关联地址 = 合法的内存变量，无需解析
            if (!v.HasAddress)
            {
                return true;
            }

            DataTypeSpec spec = DataTypes.Get(v.Type);
            if (!spec.AllowsFormat(v.Format))
            {
                error = $"{spec.DisplayName} 不支持所选显示格式";
                kind = AddressErrorKind.Configuration;
                v.ResolveError = error;
                return false;
            }

            if (spec.HasVariableLength && v.Length <= 0)
            {
                error = "STRING 长度必须大于 0";
                kind = AddressErrorKind.Configuration;
                v.ResolveError = error;
                return false;
            }

            // 拆不出「符号 + 编号」= 这个软元件根本不存在，与类型无关
            if (!CommDriver.TrySplitName(v.Address, out string element, out string index, out string splitError))
            {
                error = splitError;
                kind = AddressErrorKind.InvalidElement;
                v.ResolveError = error;
                return false;
            }

            // 元件符号统一大写，与自由监控的名称框（MonitorItem.SetName）一致：
            // y27 与 Y27 是同一个元件。归一后写回 Address，界面与 json 都存规范形式。
            element = element.ToUpperInvariant();
            v.Address = element + index;

            // 方案 C：类型决定可映射的地址区
            IReadOnlyList<ModbusArea> candidates = driver.AreasOfElement(element);
            if (candidates.Count == 0)
            {
                error = $"元件 {element} 不在当前设备类型的地址表内";
                kind = AddressErrorKind.InvalidElement;
                v.ResolveError = error;
                return false;
            }

            ModbusArea? matched = null;
            foreach (ModbusArea area in candidates)
            {
                if (GlobalVariableRules.CanMapTo(v.Type, area))
                {
                    matched = area;
                    break;
                }
            }

            // 元件存在、但存储区与类型对不上 → 这是「类型不符」，不是「元件无效」
            if (matched == null)
            {
                error = spec.CoilOnly
                    ? $"{spec.DisplayName} 只能映射线圈区元件"
                    : $"{spec.DisplayName} 只能映射寄存器区元件";
                kind = AddressErrorKind.TypeMismatch;
                v.ResolveError = error;
                return false;
            }

            // 元件与类型都说得通，只是编号越界 / 超出地址范围
            if (!driver.TryResolve(v.Address, matched.Value, out int address, out string resolveError))
            {
                error = resolveError;
                kind = AddressErrorKind.InvalidElement;
                v.ResolveError = error;
                return false;
            }

            // 只读区（X 等输入元件 / 离散输入 / 输入寄存器）：读得到、写不了。
            //
            // 用户定案（2026-10-27）：在**映射地址这一步**就拦住，而不是等数据管理里再报错 ——
            // 一个注定写不进去的地址，配进来只会让写入任务在运行期失败，还查不出为什么。
            //
            // ⚠️ 代价：全局变量表里**不能再映射 X**。X 仍然可以在自由监控里直接读
            // （那条路不走全局变量）；若将来需要把 X 读进变量，得把这条拦法改成「仅警告」。
            if (IsReadOnlyElement(driver, element, matched.Value))
            {
                v.IsReadOnly = true;
                v.ResolvedAddress = -1;
                error = $"{v.Address} 属于只读区（输入元件），不能作为全局变量的映射地址";
                kind = AddressErrorKind.ReadOnlyArea;
                v.ResolveError = error;
                return false;
            }

            v.IsReadOnly = false;
            v.ResolvedAddress = address;
            v.Area = matched.Value;
            return true;
        }

        /// <summary>
        /// 该元件区是否只读（方案 C）。
        /// <para>
        /// 判据：同一元件若存在**线圈区**定义 → 它是输出/内部继电器，可读写；
        /// 若只落在离散输入区 → 只读。信捷的 X 在本驱动里登记为 Coil，
        /// 但按业务语义它是**输入点**，故额外按元件名判定。
        /// </para>
        /// </summary>
        private static bool IsReadOnlyElement(CommDriver driver, string element, ModbusArea area)
        {
            // 离散输入区天然只读
            if (area is ModbusArea.DiscreteInput or ModbusArea.InputRegister)
            {
                return true;
            }

            // 信捷 / 汇川的输入点元件：X（汇川另有 X 输入）
            // 输出 / 内部继电器 Y、M、S、SM、HM 均可写
            return string.Equals(element, "X", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 运行时变量集合：持有全部变量、维护当前值、负责增删改与落盘。
    /// <para>⚠️ 本类**不碰 WPF**，与 <c>MonitorItemSet</c> 同一层次。</para>
    /// </summary>
    public sealed class GlobalVariableSet
    {
        private readonly List<GlobalVariable> _items = new();

        /// <summary>全部变量（按添加顺序）</summary>
        public IReadOnlyList<GlobalVariable> Items => _items;

        /// <summary>当前设备驱动；为空时不解析地址</summary>
        public CommDriver? Driver { get; private set; }

        public void SetDriver(CommDriver? driver)
        {
            Driver = driver;
            ReResolveAll();
        }

        /// <summary>
        /// 载入定义并把**所有值重置为初始值**（需求：每次启动默认初始化）。
        /// </summary>
        public void Load()
        {
            _items.Clear();
            foreach (GlobalVariable v in GlobalVariableStore.Load())
            {
                v.Value = GlobalVariableRules.InitialValue(v.Type);
                _items.Add(v);
            }

            ReResolveAll();
        }

        /// <summary>把所有变量的值重置为初始值（0 / 空串）。</summary>
        public void ResetValues()
        {
            foreach (GlobalVariable v in _items)
            {
                v.Value = GlobalVariableRules.InitialValue(v.Type);
            }
        }

        /// <summary>新增一条变量；名称非法或重名时返回 null。</summary>
        public GlobalVariable? Add(GlobalVariable v, out string error)
        {
            error = string.Empty;

            if (!GlobalVariableRules.TryValidateName(v.Name, out error))
            {
                return null;
            }

            if (IndexOfName(v.Name) >= 0)
            {
                error = $"变量名称 {v.Name} 已存在";
                return null;
            }

            // 格式必须在该类型的允许集内：调用方可能没设（默认 Dec），
            // 而 BOOL / STRING 都不接受 Dec，故在这里按类型纠正一次。
            DataTypeSpec spec = DataTypes.Get(v.Type);
            if (!spec.AllowsFormat(v.Format))
            {
                v.Format = spec.DefaultFormat;
            }

            v.Value = GlobalVariableRules.InitialValue(v.Type);
            _items.Add(v);
            ResolveOnly(v);
            return v;
        }

        public void Remove(GlobalVariable v)
        {
            _items.Remove(v);
        }

        public void Clear()
        {
            _items.Clear();
        }

        /// <summary>改名（含重名与合法性检查）</summary>
        public bool Rename(GlobalVariable v, string newName, out string error)
        {
            error = string.Empty;
            newName ??= string.Empty;

            if (string.Equals(v.Name, newName, StringComparison.Ordinal))
            {
                return true;
            }

            if (!GlobalVariableRules.TryValidateName(newName, out error))
            {
                return false;
            }

            int existing = IndexOfName(newName);
            if (existing >= 0 && !ReferenceEquals(_items[existing], v))
            {
                error = $"变量名称 {newName} 已存在";
                return false;
            }

            v.Name = newName;
            return true;
        }

        /// <summary>按名称查找（大小写敏感 —— C 标识符语义）</summary>
        public GlobalVariable? FindByName(string name) =>
            _items.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));

        private int IndexOfName(string name) =>
            _items.FindIndex(v => string.Equals(v.Name, name, StringComparison.Ordinal));

        /// <summary>重新解析一条变量的地址（改动类型 / 地址后调用）</summary>
        public void ResolveOnly(GlobalVariable v) => ResolveOnly(v, out _);

        /// <summary>
        /// 同上，并把失败**类别**交给调用方（界面据此决定弹哪句提示）。
        /// <para>未选设备类型时返回 false + <see cref="AddressErrorKind.Configuration"/>。</para>
        /// </summary>
        public bool ResolveOnly(GlobalVariable v, out AddressErrorKind kind)
        {
            kind = AddressErrorKind.None;

            if (Driver == null)
            {
                // 没选设备类型就无从解析；此时不该报「软元件无效」，那是配置没做完
                kind = AddressErrorKind.Configuration;
                return false;
            }

            return GlobalVariableStore.TryResolve(v, Driver, out _, out kind);
        }

        /// <summary>重新解析全部（换设备类型后调用）</summary>
        public void ReResolveAll()
        {
            if (Driver == null)
            {
                return;
            }

            foreach (GlobalVariable v in _items)
            {
                GlobalVariableStore.TryResolve(v, Driver, out _);
            }
        }

        /// <summary>落盘（只写定义，不含值）</summary>
        public bool Save() => GlobalVariableStore.Save(_items);
    }
}
