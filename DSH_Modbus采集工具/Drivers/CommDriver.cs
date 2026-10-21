using System;
using System.Collections.Generic;
using System.Globalization;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>Modbus 存储区类型</summary>
    public enum ModbusArea
    {
        /// <summary>线圈 0x01/0x05/0x0F</summary>
        Coil,
        /// <summary>离散量输入 0x02</summary>
        DiscreteInput,
        /// <summary>保持寄存器 0x03/0x06/0x10</summary>
        HoldingRegister,
        /// <summary>输入寄存器 0x04</summary>
        InputRegister,
    }

    /// <summary>元件编号进制</summary>
    public enum NumberBase
    {
        Decimal,
        /// <summary>X/Y 常用八进制（0-7）</summary>
        Octal,
    }

    /// <summary>
    /// 32 位值（<c>DINT</c> / <c>UDINT</c> / <c>REAL</c>）占两个连续寄存器时的字节顺序。
    /// <para>
    /// 记号：把该 32 位值的 4 个字节**按重要性**编号，1 = 最高字节（MSB）… 4 = 最低字节（LSB）；
    /// 名称表示这 4 个字节在**字节流里的排列顺序**。
    /// 例：<c>3412</c> = 先第 3 字节、再第 4、再第 1、再第 2 —— 即「低字在前、字内大端」。
    /// </para>
    /// </summary>
    public enum ByteOrder32
    {
        /// <summary>1234（ABCD）：大端，高字在前 —— 多数 Modbus 库的默认值</summary>
        Order1234 = 0,

        /// <summary>2143（BADC）：字内字节交换，高字在前</summary>
        Order2143 = 1,

        /// <summary>3412（CDAB）：字交换，低字在前 —— <b>信捷 XD 实测值</b></summary>
        Order3412 = 2,

        /// <summary>4321（DCBA）：小端，低字在前</summary>
        Order4321 = 3,
    }

    /// <summary>
    /// 字符串在 16 位寄存器内的字节顺序。
    /// <para>
    /// 记号：把寄存器内的 2 个字符**按先后**编号 1、2；名称表示它们在字节流里的顺序。
    /// <c>12</c> = 第 1 个字符在高字节（寄存器按 Modbus 惯例大端发送）。
    /// </para>
    /// </summary>
    public enum StringByteOrder
    {
        /// <summary>12：第 1 个字符在高字节 —— <b>信捷 XD 实测值</b></summary>
        Order12 = 0,

        /// <summary>21：第 1 个字符在低字节</summary>
        Order21 = 1,
    }

    /// <summary>
    /// 标准地址区域定义：一段连续的同类元件映射到一段连续的 Modbus 地址。
    /// 新增品牌时只需为每个元件区提供此结构。
    /// </summary>
    public sealed class AddressArea
    {
        /// <summary>元件符号，如 M、D、X、Y、HM、B、R</summary>
        public string Element { get; init; } = string.Empty;

        /// <summary>所属 Modbus 存储区</summary>
        public ModbusArea Area { get; init; }

        /// <summary>元件编号起点（按进制计），如 X 从 0</summary>
        public int StartIndex { get; init; }

        /// <summary>元件个数</summary>
        public int Count { get; init; }

        /// <summary>起始 Modbus 十进制地址（绝对地址）</summary>
        public int StartAddress { get; init; }

        /// <summary>元件编号进制，X/Y 一般为八进制</summary>
        public NumberBase IndexBase { get; init; } = NumberBase.Decimal;

        /// <summary>元件编号 -> Modbus 地址</summary>
        public bool TryGetAddress(int index, out int address)
        {
            address = 0;
            if (index < StartIndex || index >= StartIndex + Count)
            {
                return false;
            }

            address = StartAddress + (index - StartIndex);
            return true;
        }

        /// <summary>Modbus 地址 -> 元件编号</summary>
        public bool TryGetIndex(int address, out int index)
        {
            index = 0;
            int offset = address - StartAddress;
            if (offset < 0 || offset >= Count)
            {
                return false;
            }

            index = StartIndex + offset;
            return true;
        }

        /// <summary>元件编号 -> 显示名称（八进制编号按八进制显示）</summary>
        public string FormatName(int index)
        {
            return IndexBase == NumberBase.Octal
                ? Element + Convert.ToString(index, 8)
                : Element + index.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 标准通讯驱动：品牌名 + 地址区域表。
    /// 新增品牌 = 新增一个 CommDriver 实例。
    /// </summary>
    public sealed class CommDriver
    {
        /// <summary>唯一键，如 "xinje_xd"、"inovance_easy"</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>显示名称，如 "信捷XD/XL系列"</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>地址区域表</summary>
        public IReadOnlyList<AddressArea> Areas { get; init; } = Array.Empty<AddressArea>();

        /// <summary>
        /// 32 位整数的字节顺序（品牌约定）。
        /// 属性初值 = 信捷 XD 真机实测值（<c>Design.md</c> §10.3）；各品牌在 <see cref="DriverRegistry"/>
        /// 里**必须显式声明**，别依赖这里的初值 —— 汇川实测同样是 <c>3412</c>，但字符串相反（§10.16）。
        /// </summary>
        public ByteOrder32 Int32ByteOrder { get; init; } = ByteOrder32.Order3412;

        /// <summary>
        /// 32 位浮点数的字节顺序（品牌约定）。
        /// 属性初值为信捷 XD 真机实测值；各品牌应在 <see cref="DriverRegistry"/> 里显式声明。
        /// </summary>
        public ByteOrder32 FloatByteOrder { get; init; } = ByteOrder32.Order3412;

        /// <summary>
        /// 字符串在寄存器内的字节顺序（品牌约定）。
        /// 属性初值 = 信捷 XD 真机实测值 <c>12</c>（<c>Design.md</c> §10.4）；
        /// ⚠️ 汇川实测是 <c>21</c>（§10.16）—— 这是两品牌唯一不同的一项，务必显式声明。
        /// </summary>
        public StringByteOrder StringByteOrder { get; init; } = StringByteOrder.Order12;

        /// <summary>
        /// 按元件符号+编号解析 Modbus 地址。
        /// 例：TryResolve("X", 17, ModbusArea.Coil, out var addr)；八进制 X17 = 十进制 15。
        /// </summary>
        public bool TryResolve(string element, int index, ModbusArea area, out int address)
        {
            address = 0;
            foreach (AddressArea a in Areas)
            {
                if (a.Element == element && a.Area == area && a.TryGetAddress(index, out address))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 按 Modbus 地址反查所属地址区（未命中返回 null）。
        /// 用于批量读取分块：不同区之间的空隙地址从站通常不接受
        /// （实测信捷 XD：X 本体 20480–20543 与扩展 20736 起之间的空隙回异常码 <b>6</b>，而非标准的 2）。
        /// </summary>
        public AddressArea? FindArea(ModbusArea area, int address)
        {
            foreach (AddressArea a in Areas)
            {
                if (a.Area == area && a.TryGetIndex(address, out _))
                {
                    return a;
                }
            }

            return null;
        }

        /// <summary>
        /// 把 "HD100"、"X17" 这类完整元件名拆成「符号 + 编号」。
        /// <para>
        /// 拆分规则：开头连续字母为符号，其余必须是 ASCII 数字。
        /// 本方法是**唯一**的拆分实现 —— <see cref="TryResolve(string, ModbusArea, out int, out string)"/>
        /// 与「自由监控」的元件名称输入框（Brief 方案 2）都调用它，
        /// 避免两处各写一遍而慢慢走偏（方案 2 要靠它把用户输入写回 Element / Index 两个字段）。
        /// </para>
        /// <para>
        /// 校验刻意从严：方案 2 的输入框会把拆分结果**直接写进模型并落盘**，
        /// 若放行 <c>"D 100"</c>（符号里带空格）、<c>"D-1"</c>、<c>"-5"</c> 这类输入，
        /// 脏数据会先进 JSON，再被 <see cref="TryResolve"/> 报成「驱动未定义元件 D 」
        /// —— 报错点离真正的输入错误很远。故在此直接拒绝，文案也更贴切。
        /// </para>
        /// </summary>
        public static bool TrySplitName(string? token, out string element, out string number, out string error)
        {
            element = string.Empty;
            number = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "元件为空";
                return false;
            }

            token = token.Trim();

            int split = 0;
            while (split < token.Length && char.IsLetter(token[split]))
            {
                split++;
            }

            if (split == 0)
            {
                error = "格式应为 元件+编号，如 HD100";
                return false;
            }

            if (split == token.Length)
            {
                error = "缺少元件编号，如 HD100";
                return false;
            }

            // 编号只认 ASCII 数字：中文输入法下很容易打出全角数字，
            // 放进去会一路带到 int.Parse 才失败，报错点离输入点太远
            for (int i = split; i < token.Length; i++)
            {
                if (token[i] < '0' || token[i] > '9')
                {
                    error = "编号只能是数字，如 HD100";
                    return false;
                }
            }

            element = token[..split];
            number = token[split..];
            return true;
        }

        /// <summary>
        /// 元件符号在驱动中定义的全部存储区（去重，按 <see cref="Areas"/> 出现次序）。
        /// <para>
        /// 「元件名称」合并输入框靠它判断该元件属于线圈还是寄存器 ——
        /// <b>只看符号、不看编号</b>，故编号越界时仍能给出正确的类型候选与错误文案
        /// （若改用「TryResolve 是否成功」判断，越界会被误判成「不是这类元件」）。
        /// </para>
        /// </summary>
        public IReadOnlyList<ModbusArea> AreasOfElement(string? element)
        {
            var list = new List<ModbusArea>();
            if (string.IsNullOrEmpty(element))
            {
                return list;
            }

            foreach (AddressArea a in Areas)
            {
                if (a.Element == element && !list.Contains(a.Area))
                {
                    list.Add(a.Area);
                }
            }

            return list;
        }

        /// <summary>
        /// 解析字符串形式的元件（如 "M100"、"X17"、"D20"）为 Modbus 地址。
        /// 进制由该元件区的 IndexBase 决定。
        /// </summary>
        public bool TryResolve(string token, ModbusArea area, out int address, out string error)
        {
            address = 0;
            token = (token ?? string.Empty).Trim();

            if (!TrySplitName(token, out string element, out string number, out error))
            {
                return false;
            }

            // 同一元件符号可有多段地址区（如信捷 X 的「本体 / 扩展模块 / BD / ED」），
            // 故需遍历全部匹配区：任一段命中即成功，全部越界才报「超出范围」。
            bool elementDefined = false;
            string? rangeError = null;

            foreach (AddressArea a in Areas)
            {
                if (a.Element != element || a.Area != area)
                {
                    continue;
                }

                elementDefined = true;

                int index;
                try
                {
                    index = a.IndexBase == NumberBase.Octal
                        ? Convert.ToInt32(number, 8)
                        : int.Parse(number, CultureInfo.InvariantCulture);
                }
                catch
                {
                    error = a.IndexBase == NumberBase.Octal
                        ? $"{element} 为八进制编号（0-7）"
                        : "编号无效";
                    return false;
                }

                if (a.TryGetAddress(index, out address))
                {
                    return true;
                }

                rangeError = $"{token} 超出驱动定义范围";
            }

            error = elementDefined
                ? rangeError ?? $"{token} 超出驱动定义范围"
                : $"驱动未定义元件 {element}";
            return false;
        }
    }
}
