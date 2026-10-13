using System;
using System.Collections.Generic;
using System.Globalization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 用户可声明的数据类型。类型由**用户声明**，不做自动推断 ——
    /// 同一段寄存器按不同类型解释会得到完全不同的值，猜错比报错更危险。
    /// </summary>
    public enum DataType
    {
        /// <summary>位（1 位），线圈/离散输入的唯一合法类型</summary>
        Bool = 0,

        /// <summary>有符号 16 位，1 个寄存器，范围 −32768…32767</summary>
        Int = 1,

        /// <summary>无符号 16 位，1 个寄存器，范围 0…65535</summary>
        UInt = 2,

        /// <summary>有符号 32 位，2 个寄存器</summary>
        DInt = 3,

        /// <summary>无符号 32 位，2 个寄存器</summary>
        UDInt = 4,

        /// <summary>IEEE754 单精度，2 个寄存器</summary>
        Real = 5,

        /// <summary>字符串，每寄存器 2 字符，占 ⌈n/2⌉ 个寄存器（n 由 <see cref="DataPoint.Length"/> 声明）</summary>
        String = 6,
    }

    /// <summary>值的显示格式。</summary>
    public enum DisplayFormat
    {
        /// <summary>二进制，如 <c>0000 0000 0000 1010</c></summary>
        Bin = 0,

        /// <summary>十进制。整数按声明类型解释；<c>REAL</c> 输出小数</summary>
        Dec = 1,

        /// <summary>十六进制，如 <c>0x000A</c></summary>
        Hex = 2,

        /// <summary>ASCII 文本，仅 <c>STRING</c> 适用</summary>
        Ascii = 3,
    }

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

        /// <summary>是否为「寄存器区」类型（非 BOOL）</summary>
        public bool RegisterType => !CoilOnly;

        /// <summary>长度是否由用户声明（<c>STRING</c>）</summary>
        public bool HasVariableLength { get; init; }

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
    {
        private static readonly DisplayFormat[] IntFormats =
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
        };

        /// <summary>寄存器区可选的类型（顺序即 UI 下拉顺序）</summary>
        public static IReadOnlyList<DataTypeSpec> RegisterTypes { get; } =
            Filter(registerOnly: true);

        /// <summary>线圈区可选的类型（只有 BOOL）</summary>
        public static IReadOnlyList<DataTypeSpec> CoilTypes { get; } =
            Filter(registerOnly: false, coilOnly: true);

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

        private static IReadOnlyList<DataTypeSpec> Filter(bool registerOnly, bool coilOnly = false)
        {
            var list = new List<DataTypeSpec>();
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
