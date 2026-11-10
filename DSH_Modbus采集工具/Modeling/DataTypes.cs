using System;
using System.Collections.Generic;
using System.Globalization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一种数据类型的静态规格（位宽 / 占用 / 可用性约束）。
    /// 把「哪种类型能配哪种格式」这类规则集中在这里，UI 与解码器共用同一份判断，
    /// 避免两处各写一遍而慢慢走偏。
    /// </summary>
    public sealed class DataTypeSpec
    {
        public DataType Type { get; init; }

        /// <summary>显示名，如 <c>DINT</c>、<c>REAL</c>、<c>STRING</c></summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>位宽；<see cref="DataType.String"/> 为 0（长度由用户声明，不固定）</summary>
        public int BitWidth { get; init; }

        /// <summary>占用的连续寄存器个数；<see cref="DataType.Bool"/> 为 0（线圈不是寄存器）</summary>
        public int RegisterCount { get; init; }

        /// <summary>是否只能用于线圈区（<c>BOOL</c>）</summary>
        public bool CoilOnly { get; init; }

        /// <summary>
        /// 判定某个**具体声明**是否只能落在线圈区。
        /// <para>
        /// <c>BOOL</c> 恒为 true；<c>ARRAY</c> 则要看**元素类型** ——
        /// <c>ARRAY&lt;BOOL&gt;</c> 占 N 个线圈，<c>ARRAY&lt;INT&gt;</c> 占 N 个寄存器。
        /// 故 <see cref="DataTypeSpec.CoilOnly"/> 对数组**不足以判断**，一律走本方法。
        /// </para>
        /// </summary>
        /// <param name="elementType">元素类型；非数组时忽略</param>
        public bool IsCoilOnlyFor(DataType elementType) =>
            Type == DataType.Array ? DataTypes.Get(elementType).CoilOnly : CoilOnly;

        /// <summary>
        /// 判定某个**具体声明**占用的单元数（线圈或寄存器都按「单元」算）。
        /// <para><c>STRING</c> 按 ⌈n/2⌉；<c>ARRAY</c> 按「元素个数 × 单元素单元数」；其余按定值。</para>
        /// </summary>
        /// <param name="length">STRING 的字符数 / ARRAY 的元素个数</param>
        /// <param name="elementType">元素类型（仅 ARRAY 有意义）</param>
        public int UnitsFor(int length, DataType elementType = DataType.Int) =>
            Type == DataType.Array
                ? DataTypes.ArrayUnitsOf(elementType, 0, length)
                : RegistersForLength(length);

        /// <summary>是否为「寄存器区」类型（非 BOOL）</summary>
        public bool RegisterType => !CoilOnly;

        /// <summary>长度是否由用户声明（<c>STRING</c>）</summary>
        public bool HasVariableLength { get; init; }

        /// <summary>
        /// 是否数组类型（<see cref="DataType.Array"/>）。
        /// <para>数组的「长度」是**元素个数**，不是字符数 —— 故与 <see cref="HasVariableLength"/> 分开：</para>
        /// <para>混用一个标志会让 <see cref="RegistersForLength"/> 按 ⌈n/2⌉ 算，数组就会少占一半。</para>
        /// </summary>
        public bool IsArray { get; init; }

        /// <summary>允许的显示格式（顺序即 UI 下拉顺序）</summary>
        public IReadOnlyList<DisplayFormat> AllowedFormats { get; init; } = Array.Empty<DisplayFormat>();

        /// <summary>默认显示格式</summary>
        public DisplayFormat DefaultFormat { get; init; }

        public bool AllowsFormat(DisplayFormat f) => AllowedFormats.Contains(f);

        /// <summary>
        /// 给定用户声明的字符数，返回实际占用的寄存器个数。
        /// <c>STRING[n]</c> 每寄存器 2 字符 → <b>⌈n/2⌉</b>（真机实测，见 Design.md §10.4）。
        /// </summary>
        public int RegistersForLength(int length)
        {
            if (!HasVariableLength)
            {
                return RegisterCount;
            }

            return length <= 0 ? 0 : (length + 1) / 2;
        }
    }

    /// <summary>数据类型规格表。</summary>
    public static class DataTypes
    {        private static readonly DisplayFormat[] IntFormats =
            { DisplayFormat.Bin, DisplayFormat.Dec, DisplayFormat.Hex };

        private static readonly DisplayFormat[] FloatFormats =
            { DisplayFormat.Dec, DisplayFormat.Hex };

        private static readonly IReadOnlyList<DataTypeSpec> AllSpecs = new[]
        {
            new DataTypeSpec
            {
                Type = DataType.Bool,
                DisplayName = "BOOL",
                BitWidth = 1,
                RegisterCount = 0,
                CoilOnly = true,
                AllowedFormats = new[] { DisplayFormat.Bin },   // 约束：线圈只能 BIN
                DefaultFormat = DisplayFormat.Bin,
            },
            new DataTypeSpec
            {
                Type = DataType.Int,
                DisplayName = "INT",
                BitWidth = 16,
                RegisterCount = 1,
                AllowedFormats = IntFormats,
                DefaultFormat = DisplayFormat.Dec,
            },
            new DataTypeSpec
            {
                Type = DataType.UInt,
                DisplayName = "UINT",
                BitWidth = 16,
                RegisterCount = 1,
                AllowedFormats = IntFormats,
                DefaultFormat = DisplayFormat.Dec,
            },
            new DataTypeSpec
            {
                Type = DataType.DInt,
                DisplayName = "DINT",
                BitWidth = 32,
                RegisterCount = 2,
                AllowedFormats = IntFormats,
                DefaultFormat = DisplayFormat.Dec,
            },
            new DataTypeSpec
            {
                Type = DataType.UDInt,
                DisplayName = "UDINT",
                BitWidth = 32,
                RegisterCount = 2,
                AllowedFormats = IntFormats,
                DefaultFormat = DisplayFormat.Dec,
            },
            new DataTypeSpec
            {
                Type = DataType.Real,
                DisplayName = "REAL",
                BitWidth = 32,
                RegisterCount = 2,
                AllowedFormats = FloatFormats,
                DefaultFormat = DisplayFormat.Dec,
            },
            new DataTypeSpec
            {
                Type = DataType.String,
                DisplayName = "STRING",
                BitWidth = 8,          // 每字符 8 位
                RegisterCount = 0,     // 由长度决定
                HasVariableLength = true,
                AllowedFormats = new[] { DisplayFormat.Ascii },  // 约束：字符串只能 ASCII
                DefaultFormat = DisplayFormat.Ascii,
            },
            new DataTypeSpec
            {
                Type = DataType.Array,
                // 下拉框里就显示 ARRAY（此时还没选元素类型）；选定后行上显示 INT[] 这种。
                DisplayName = "ARRAY",
                BitWidth = 0,          // 由元素类型决定
                RegisterCount = 0,     // 由「元素类型 × 元素个数」决定
                HasVariableLength = false,  // ⚠️ 不是 STRING 那种「字符数 → ⌈n/2⌉」
                IsArray = true,
                // 元素一律按各自类型的默认格式显示（用户 2026-11-09 定案：
                // 「整组元素列表」）。故数组本身不给格式选择。
                AllowedFormats = new[] { DisplayFormat.Dec },
                DefaultFormat = DisplayFormat.Dec,
            },
        };

        /// <summary>寄存器区可选的类型（顺序即 UI 下拉顺序）</summary>
        public static IReadOnlyList<DataTypeSpec> RegisterTypes { get; } =
            Filter(registerOnly: true);

        /// <summary>线圈区可选的类型（只有 BOOL）</summary>
        public static IReadOnlyList<DataTypeSpec> CoilTypes { get; } =
            Filter(registerOnly: false, coilOnly: true);

        /// <summary>
        /// **自由监控**的寄存器区候选类型：<see cref="RegisterTypes"/> **去掉 ARRAY**
        /// （2026-11-10 用户定案：「监控表里软元件不能以 ARRAY 类型监控」）。
        /// <para>
        /// 理由：监控表的一行对应**一个元件地址**，而 ARRAY 占的是一段连续单元 ——
        /// 在监控表里选 ARRAY 等于把「一行 = 一个值」这条前提打破，
        /// 行上的「当前值」格也就没有一个确定的值可显示。
        /// 数组应当走**全局变量**（那边声明一次 BOOL[80]，监控表里按变量名监控）。
        /// </para>
        /// <para>
        /// ⚠️ 全局变量页仍用 <see cref="RegisterTypes"/>（含 ARRAY）—— 两处候选不同是**故意的**，
        /// 不要为了「统一」把数组从全局变量也删掉，那样 ARRAY 就彻底没入口了。
        /// </para>
        /// </summary>
        public static IReadOnlyList<DataTypeSpec> MonitorRegisterTypes { get; } =
            FilterMonitorTypes();

        private static IReadOnlyList<DataTypeSpec> FilterMonitorTypes()
        {
            var list = new List<DataTypeSpec>(RegisterTypes.Count);
            foreach (DataTypeSpec s in RegisterTypes)
            {
                if (!s.IsArray)
                {
                    list.Add(s);
                }
            }

            return list;
        }

        public static DataTypeSpec Get(DataType t)
        {
            foreach (DataTypeSpec s in AllSpecs)
            {
                if (s.Type == t)
                {
                    return s;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(t), t, "未知数据类型");
        }

        /// <summary>
        /// 数组元素的**可选类型**（2026-11-09 用户定案：除 <c>STRING</c> 外的全部类型，含 BOOL）。
        /// <para>
        /// 排除 STRING：数组要「N 个同类型元素连续排布」，而 STRING 本身长度可变、
        /// 占 ⌈n/2⌉ 个寄存器 —— 再套一层数组，元素边界就成了两个可变长度的乘积，
        /// 用户填错一个数字整段地址就错位，而界面上看不出来。
        /// </para>
        /// </summary>
        public static IReadOnlyList<DataTypeSpec> ArrayElementTypes { get; } =
            BuildArrayElementTypes();

        /// <summary>元素数量上限（需求：最大 128）。</summary>
        public const int MaxArrayLength = ArrayRules.MaxLength;

        /// <summary>元素数量下限。</summary>
        public const int MinArrayLength = ArrayRules.MinLength;

        /// <summary>该类型能否当数组元素（除 STRING 与 ARRAY 外都可以）。</summary>
        public static bool CanBeArrayElement(DataType t) => ArrayRules.CanBeElement(t);

        /// <summary>
        /// 一个数组元素占用的**单元数**：线圈元素占 1 个线圈，其余按寄存器类型算。
        /// <para>⚠️ 返回的是「单元数」，线圈与寄存器都按这口径 —— 调用方按 Area 决定语义。</para>
        /// </summary>
        public static int ElementUnitsOf(DataType elementType, int elementLength)
        {
            DataTypeSpec spec = Get(elementType);
            return spec.CoilOnly ? 1 : spec.RegistersForLength(elementLength);
        }

        /// <summary>
        /// 数组占用的**总单元数** = 元素个数 × 单元素单元数。
        /// <para>
        /// ⚠️ 不能用 <see cref="DataTypeSpec.RegistersForLength"/> 算 ——
        /// 那个是按 STRING 的「每寄存器 2 字符」做的除法（⌈n/2⌉），
        /// 套到数组上会把 3 元素 INT 算成占 2 个寄存器（实为 3）。
        /// </para>
        /// </summary>
        public static int ArrayUnitsOf(DataType elementType, int elementLength, int elementCount)
        {
            if (elementCount <= 0)
            {
                return 0;
            }

            return elementCount * ElementUnitsOf(elementType, elementLength);
        }

        private static IReadOnlyList<DataTypeSpec> BuildArrayElementTypes()
        {
            var list = new List<DataTypeSpec>();
            foreach (DataTypeSpec s in AllSpecs)
            {
                // 排除 STRING 与 ARRAY 自身（嵌套数组不在需求内）
                if (s.Type is DataType.String or DataType.Array)
                {
                    continue;
                }

                list.Add(s);
            }

            return list;
        }

        private static IReadOnlyList<DataTypeSpec> Filter(bool registerOnly, bool coilOnly = false)
        {            var list = new List<DataTypeSpec>();
            foreach (DataTypeSpec s in AllSpecs)
            {
                if (registerOnly && s.CoilOnly)
                {
                    continue;
                }

                if (coilOnly && !s.CoilOnly)
                {
                    continue;
                }

                list.Add(s);
            }

            return list;
        }
    }
}
