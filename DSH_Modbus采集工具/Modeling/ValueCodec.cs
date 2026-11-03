using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 原始寄存器/线圈 ↔ 类型值 的编解码，以及值的显示格式化。
    /// <para>
    /// <b>信捷 XD 的真机定案值</b>（见 <c>Design.md</c> §10.3 / §10.4）：
    /// <list type="bullet">
    /// <item>32 位值占 2 个连续寄存器，<b>低字在低地址</b>；字节流顺序 <c>3412</c>（CDAB）。</item>
    /// <item>字符串每寄存器 2 个字符、<b>高字节在前</b>；<c>STRING[n]</c> 占 ⌈n/2⌉ 个寄存器。</item>
    /// </list>
    /// 但这只是**出厂默认**：设备连接页的三个下拉框可以把它们各自改掉，故本类<b>不硬编码任何字序</b>，
    /// 一律经 <see cref="ByteOrderSettings"/> 入参（<c>Int32</c> / <c>Float</c> / <c>String</c> 三套独立）。
    /// 本类不读全局状态，也不碰 WPF —— 可被独立工程链接源码断言。
    /// </para>
    /// <para>
    /// ⚠️ <c>REAL</c> 与 <c>DINT</c> 虽然都是「2 个寄存器拼成的 32 位值」，但<b>字序各用各的</b>：
    /// 两者混用一套是历史做法，已按用户定案（2026-10-21）拆开。
    /// </para>
    /// </summary>
    public static class ValueCodec
    {
        // ==================== 解码：原始 → 值 ====================

        /// <summary>
        /// 把寄存器原始值解码成「规范化的 64 位整数」。
        /// <para>
        /// 返回值刻意用 <see cref="long"/> 而非各自的窄类型：调用方拿到的是<b>位模式</b>，
        /// 位模式对 <c>INT</c>(−1) 与 <c>UINT</c>(65535) 是同一个 <c>0xFFFF</c>，
        /// 差异只在格式化时按声明类型解释（见 <see cref="Format"/>）。
        /// 这样避免在解码处就按错误类型截断，也便于 P4-2 写入时反向还原。
        /// </para>
        /// </summary>
        public static long DecodeInteger(DataType type, ReadOnlySpan<ushort> regs, ByteOrder32 byteOrder)
        {
            DataTypeSpec spec = DataTypes.Get(type);
            switch (type)
            {
                case DataType.Int:
                case DataType.UInt:
                    Require(regs, 1, type);
                    return regs[0];

                case DataType.DInt:
                case DataType.UDInt:
                    Require(regs, 2, type);
                    return Assemble32(regs, byteOrder);

                case DataType.Real:
                    // REAL 与 DINT 是同一个 32 位整数，只是解释方式不同
                    Require(regs, 2, type);
                    return Assemble32(regs, byteOrder);

                default:
                    throw new ArgumentException($"类型 {spec.DisplayName} 不是整数/浮点类型", nameof(type));
            }
        }

        /// <summary>把 2 个寄存器按给定字序拼成 32 位值（结果在 <see cref="long"/> 的低 32 位，未做符号扩展）。</summary>
        private static long Assemble32(ReadOnlySpan<ushort> regs, ByteOrder32 byteOrder)
        {
            ushort low, high;
            switch (byteOrder)
            {
                // 低字在低地址（信捷 XD 实测）
                case ByteOrder32.Order3412:
                case ByteOrder32.Order4321:
                    low = regs[0];
                    high = regs[1];
                    break;

                // 高字在低地址
                case ByteOrder32.Order1234:
                case ByteOrder32.Order2143:
                    high = regs[0];
                    low = regs[1];
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "未知字序");
            }

            // 2143 / 4321 还需在字内交换字节
            bool swapBytesInWord = byteOrder is ByteOrder32.Order2143 or ByteOrder32.Order4321;
            if (swapBytesInWord)
            {
                low = SwapBytes(low);
                high = SwapBytes(high);
            }

            return ((long)high << 16) | low;
        }

        private static ushort SwapBytes(ushort v) => (ushort)((v >> 8) | ((v & 0xFF) << 8));

        /// <summary>把原始 32 位位模式解释为 <see cref="float"/>（IEEE754）。</summary>
        public static float DecodeReal(ReadOnlySpan<ushort> regs, ByteOrder32 byteOrder)
        {
            long raw = Assemble32(regs, byteOrder);
            return BitConverter.Int32BitsToSingle(unchecked((int)raw));
        }

        /// <summary>
        /// 解码字符串。每寄存器 2 个字符；<paramref name="length"/> 为用户声明的字符数，
        /// 用于<b>截断</b>（末寄存器可能带填充字节）。
        /// </summary>
        public static string DecodeString(ReadOnlySpan<ushort> regs, int length, StringByteOrder order)
        {
            if (length <= 0)
            {
                return string.Empty;
            }

            int needed = (length + 1) / 2;
            if (regs.Length < needed)
            {
                throw new ArgumentException($"STRING[{length}] 需要 {needed} 个寄存器，实际只有 {regs.Length} 个", nameof(regs));
            }

            var sb = new StringBuilder(length);
            for (int i = 0; i < needed; i++)
            {
                ushort w = regs[i];
                char first = order == StringByteOrder.Order12 ? (char)(w >> 8) : (char)(w & 0xFF);
                char second = order == StringByteOrder.Order12 ? (char)(w & 0xFF) : (char)(w >> 8);

                sb.Append(first);
                if (sb.Length < length)
                {
                    sb.Append(second);
                }
            }

            // 常见约定：遇 NUL 截断（PLC 字符串常用 \0 填充）
            int nul = sb.ToString().IndexOf('\0');
            return nul >= 0 ? sb.ToString(0, nul) : sb.ToString();
        }

        private static void Require(ReadOnlySpan<ushort> regs, int count, DataType type)
        {
            if (regs.Length < count)
            {
                throw new ArgumentException(
                    $"{DataTypes.Get(type).DisplayName} 需要 {count} 个寄存器，实际只有 {regs.Length} 个",
                    nameof(regs));
            }
        }

        // ==================== 格式化：值 → 显示文本 ====================

        /// <summary>把寄存器原始值按类型与格式格式化成显示文本。</summary>
        /// <param name="orders">三套字序：整数取 <see cref="ByteOrderSettings.Int32"/>，
        /// 浮点取 <see cref="ByteOrderSettings.Float"/>，字符串取 <see cref="ByteOrderSettings.String"/>。</param>
        public static string FormatRegisters(DataType type, ReadOnlySpan<ushort> regs,
            DisplayFormat format, ByteOrderSettings orders, int stringLength)
        {
            DataTypeSpec spec = DataTypes.Get(type);
            if (!spec.AllowsFormat(format))
            {
                throw new ArgumentException($"{spec.DisplayName} 不支持 {format} 格式", nameof(format));
            }

            if (type == DataType.String)
            {
                return DecodeString(regs, stringLength, orders.String);
            }

            if (type == DataType.Real)
            {
                // 浮点用自己那套字序，不跟 32 位整数共用
                float f = DecodeReal(regs, orders.Float);
                return format == DisplayFormat.Hex
                    ? "0x" + unchecked((uint)BitConverter.SingleToInt32Bits(f)).ToString("X8", CultureInfo.InvariantCulture)
                    : FormatFloat(f);
            }

            long raw = DecodeInteger(type, regs, orders.Int32);
            return FormatInteger(type, raw, format);
        }

        /// <summary>
        /// 把寄存器原始值解成**数值**（与显示格式无关）。P1.4 实时数据用。
        /// <para>
        /// 与 <see cref="FormatRegisters"/> 分开：那个出的是给人看的文本（可能是 Hex / Bin），
        /// 实时卡片一律十进制显示，从文本反解会遇到「STRING 内容恰好是 2A」这种歧义。
        /// </para>
        /// </summary>
        /// <returns>数值；<c>STRING</c> 没有数值形态，返回 <c>null</c>。</returns>
        public static double? DecodeNumber(DataType type, ReadOnlySpan<ushort> regs, ByteOrderSettings orders)
        {
            if (type == DataType.String)
            {
                return null;
            }

            if (type == DataType.Real)
            {
                return DecodeReal(regs, orders.Float);
            }

            return DecodeInteger(type, regs, orders.Int32);
        }

        /// <summary>把 <see cref="DecodeInteger"/> 的位模式按声明类型解释并格式化。</summary>
        public static string FormatInteger(DataType type, long raw, DisplayFormat format)
        {
            switch (format)
            {
                case DisplayFormat.Bin:
                    return FormatBinary(type, raw);

                case DisplayFormat.Hex:
                    return FormatHex(type, raw);

                case DisplayFormat.Dec:
                    return FormatDecimal(type, raw);

                default:
                    throw new ArgumentException($"{DataTypes.Get(type).DisplayName} 不支持 {format} 格式", nameof(format));
            }
        }

        /// <summary>按声明类型把位模式解释为有符号/无符号十进制。严格按类型，不自动判断。</summary>
        private static long Interpret(DataType type, long raw) => type switch
        {
            DataType.Int => unchecked((short)raw),
            DataType.UInt => (ushort)raw,
            DataType.DInt => unchecked((int)raw),
            DataType.UDInt => (uint)raw,
            _ => raw,
        };

        private static string FormatDecimal(DataType type, long raw)
        {
            long v = Interpret(type, raw);
            return v.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatHex(DataType type, long raw)
        {
            // 按位宽补零：INT/UINT 4 位，DINT/UDINT 8 位
            DataTypeSpec spec = DataTypes.Get(type);
            int digits = spec.BitWidth / 4;
            ulong bits = spec.BitWidth == 16 ? (ushort)raw : (uint)raw;
            return "0x" + bits.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private static string FormatBinary(DataType type, long raw)
        {
            DataTypeSpec spec = DataTypes.Get(type);
            int width = spec.BitWidth;
            ulong bits = width == 16 ? (ushort)raw : (uint)raw;

            string s = Convert.ToString((long)bits, 2).PadLeft(width, '0');
            if (s.Length > width)
            {
                s = s[^width..];
            }

            // 每 4 位一组，便于读数
            var sb = new StringBuilder(width + width / 4);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && (width - i) % 4 == 0)
                {
                    sb.Append(' ');
                }

                sb.Append(s[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 浮点显示：7 位有效数字。
        /// <para>
        /// ⚠️ **为什么不是 <c>G9</c>（单精度唯一往返所需位数）**：
        /// <c>float</c> 存不下 <c>123.45</c> 这类十进制小数（二进制下 0.45 是无限循环），
        /// 实际存的是 <c>123.4499969482…</c>。用 <c>G9</c> 会把这个二进制误差**如实打印**
        /// 成 <c>123.449997</c>，与 PLC（XDPPro 显示 <c>123.45</c>）对不上，
        /// 用户会误以为通讯或字节序出了问题。**数据本身一直是对的**，只是打印位数不同。
        /// </para>
        /// <para>
        /// <c>G7</c> 与本项目工程量（0~9999.99 这类 6~7 位有效数字）匹配，且尾零自动省略。
        /// 代价：<c>123456.789</c> 会显示成 <c>123456.8</c>（丢失末位），
        /// 需要精确往返时请改用 <c>G9</c> —— 显示文本不参与回写，故此处取舍只影响可读性。
        /// </para>
        /// </summary>
        private static string FormatFloat(float f)
        {
            if (float.IsNaN(f))
            {
                return "NaN";
            }

            if (float.IsPositiveInfinity(f))
            {
                return "+∞";
            }

            if (float.IsNegativeInfinity(f))
            {
                return "−∞";
            }

            return f.ToString("G7", CultureInfo.InvariantCulture);
        }

        /// <summary>格式化线圈（BOOL，只能是 BIN）。</summary>
        public static string FormatCoil(bool on) => on ? "1" : "0";

        // ==================== 编码：值 → 原始（供 P4-2 写入使用） ====================

        /// <summary>
        /// 把 32 位值按字序拆成 2 个寄存器。与 <see cref="Assemble32"/> 严格互逆。
        /// </summary>
        public static ushort[] Split32(uint value, ByteOrder32 byteOrder)
        {
            ushort low = (ushort)(value & 0xFFFF);
            ushort high = (ushort)(value >> 16);

            bool swapBytesInWord = byteOrder is ByteOrder32.Order2143 or ByteOrder32.Order4321;
            if (swapBytesInWord)
            {
                low = SwapBytes(low);
                high = SwapBytes(high);
            }

            return byteOrder switch
            {
                ByteOrder32.Order3412 or ByteOrder32.Order4321 => new[] { low, high },
                ByteOrder32.Order1234 or ByteOrder32.Order2143 => new[] { high, low },
                _ => throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "未知字序"),
            };
        }

        /// <summary>把字符串按「每寄存器 2 字符、指定字节序」编码成寄存器序列（不足处补 0x00）。</summary>
        public static ushort[] EncodeString(string text, StringByteOrder order)
        {
            text ??= string.Empty;
            int regCount = (text.Length + 1) / 2;
            var regs = new ushort[regCount];

            for (int i = 0; i < regCount; i++)
            {
                char first = text[i * 2];
                char second = i * 2 + 1 < text.Length ? text[i * 2 + 1] : '\0';

                regs[i] = order == StringByteOrder.Order12
                    ? (ushort)((first << 8) | second)
                    : (ushort)((second << 8) | first);
            }

            return regs;
        }

        /// <summary>
        /// 把字符串按<b>声明的字符数</b>编码成寄存器序列：
        /// 超出则截断，不足则补 <c>0x00</c>，使寄存器数恰好等于 <c>RegistersForLength(length)</c>。
        /// <para>
        /// ⚠️ 不能直接用 <see cref="EncodeString"/>：它按<b>文本长度</b>自算寄存器数，
        /// 而用户声明的是 <c>STRING[9]</c>（占 5 个寄存器）。若输入 <c>"abc"</c>，
        /// <c>EncodeString</c> 只给 2 个寄存器，会把该项占用的后 3 个寄存器漏写
        /// （那些位置可能残留旧值），与读取侧的 <c>RawCount</c> 对不上。
        /// </para>
        /// </summary>
        public static ushort[] EncodeStringFixed(string text, int length, StringByteOrder order)
        {
            if (length <= 0)
            {
                throw new ArgumentException("STRING 长度必须大于 0", nameof(length));
            }

            text ??= string.Empty;
            int regCount = (length + 1) / 2;

            // 先按声明长度截断字符（避免先把超长文本编码再丢弃寄存器）
            if (text.Length > length)
            {
                text = text.Substring(0, length);
            }

            ushort[] packed = EncodeString(text, order);
            if (packed.Length == regCount)
            {
                return packed;
            }

            var regs = new ushort[regCount];
            Array.Copy(packed, regs, packed.Length);
            return regs;
        }

        // ==================== 文本 → 值（写入用） ====================

        /// <summary>
        /// 解析用户输入的整数字面量，按 <paramref name="format"/> 决定进制：
        /// <c>HEX</c> 接受 <c>0x1F</c> 或 <c>1F</c>；<c>BIN</c> 接受 <c>1010</c> 或 <c>0b1010</c>；
        /// <c>DEC</c> 接受可选负号（仅对有符号类型有意义）。
        /// <para>
        /// 返回值是<b>位模式</b>（未按类型做符号扩展）：如 <c>INT</c> 的 <c>-1</c> 得到 <c>0xFFFF</c>。
        /// 范围校验由 <see cref="TryParseInteger"/> 按类型完成。
        /// </para>
        /// </summary>
        public static bool TryParseIntegerLiteral(DataType type, DisplayFormat format, string? text, out long bits, out string error)
        {
            bits = 0;
            error = string.Empty;
            string s = (text ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                error = "请输入数值";
                return false;
            }

            DataTypeSpec spec = DataTypes.Get(type);
            if (spec.CoilOnly)
            {
                error = "线圈只能写 0 / 1";
                return false;
            }

            bool signed = type is DataType.Int or DataType.DInt;
            if (format is not (DisplayFormat.Hex or DisplayFormat.Bin or DisplayFormat.Dec))
            {
                error = $"{spec.DisplayName} 不支持 {format} 格式";
                return false;
            }

            switch (format)
            {
                case DisplayFormat.Hex:
                {
                    string body = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
                    if (body.Length == 0)
                    {
                        error = "请输入十六进制数值，如 1F 或 0x1F";
                        return false;
                    }

                    if (!ulong.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex))
                    {
                        error = $"「{s}」不是合法的十六进制数";
                        return false;
                    }

                    return CheckIntegerRange(type, hex, signed, out bits, out error);
                }

                case DisplayFormat.Bin:
                {
                    string body = s.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
                    if (body.Length == 0)
                    {
                        error = "请输入二进制数值，如 1010";
                        return false;
                    }

                    try
                    {
                        ulong bin = Convert.ToUInt64(body, 2);
                        return CheckIntegerRange(type, bin, signed, out bits, out error);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException)
                    {
                        error = $"「{s}」不是合法的二进制数";
                        return false;
                    }
                }

                default:
                {
                    if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long dec))
                    {
                        error = $"「{s}」不是合法的十进制整数";
                        return false;
                    }

                    // ⚠️ 必须在这里就按类型范围校验，**不能**交给 CheckIntegerRange：
                    // 后者会把值先截断到目标位宽，-32769 截成 0x7FFF 后看起来是合法的 32767。
                    DataTypeSpec decSpec = DataTypes.Get(type);
                    if (signed)
                    {
                        long min = -(1L << (decSpec.BitWidth - 1));
                        long max = (1L << (decSpec.BitWidth - 1)) - 1;
                        if (dec < min || dec > max)
                        {
                            error = $"{decSpec.DisplayName} 范围 {min} ~ {max}";
                            return false;
                        }
                    }
                    else if (dec < 0)
                    {
                        error = $"{decSpec.DisplayName} 不能为负";
                        return false;
                    }
                    else if ((ulong)dec > (decSpec.BitWidth == 16 ? ushort.MaxValue : uint.MaxValue))
                    {
                        error = $"{decSpec.DisplayName} 范围 0 ~ {(decSpec.BitWidth == 16 ? ushort.MaxValue : uint.MaxValue)}";
                        return false;
                    }

                    bits = dec;
                    return true;
                }
            }
        }

        /// <summary>
        /// 按类型位宽做范围校验（<b>字面量</b>输入，如 HEX/BIN）。
        /// <para>
        /// ⚠️ 判据是「按位宽截断后是否等于原值」+「重新解释符号后在范围内」：
        /// 只看后者会放过 <c>HEX 1FFFF</c>（截成 <c>0xFFFF</c> 看着合法）。
        /// 十进制输入不走这里 —— 它在解析时就按类型范围校验过（见 <c>TryParseIntegerLiteral</c>）。
        /// </para>
        /// </summary>
        private static bool CheckIntegerRange(DataType type, ulong magnitude, bool signed, out long bits, out string error)
        {
            DataTypeSpec spec = DataTypes.Get(type);
            int width = spec.BitWidth;
            error = string.Empty;
            bits = 0;

            // 位模式：16 位取低 16 位，32 位取低 32 位
            ulong masked = width == 16 ? (ushort)magnitude : (uint)magnitude;

            // 超出位宽能容纳的范围即越界（截断前后不等说明高位被丢掉了）
            if (masked != magnitude)
            {
                error = width == 16
                    ? $"{spec.DisplayName} 范围 0x0000 ~ 0xFFFF"
                    : $"{spec.DisplayName} 范围 0x00000000 ~ 0xFFFFFFFF";
                return false;
            }

            if (signed)
            {
                long asSigned = width == 16
                    ? unchecked((short)masked)
                    : unchecked((int)masked);

                long min = -(1L << (width - 1));
                long max = (1L << (width - 1)) - 1;

                if (asSigned < min || asSigned > max)
                {
                    error = $"{spec.DisplayName} 范围 {min} ~ {max}";
                    return false;
                }

                bits = asSigned;
                return true;
            }

            bits = unchecked((long)masked);
            return true;
        }

        /// <summary>
        /// 文本 → 寄存器数组（写入的统一入口）。按类型与显示格式解析，
        /// <c>STRING</c> 走 <see cref="EncodeStringFixed"/>，其余走整数/浮点解析。
        /// </summary>
        public static bool TryEncodeRegisters(
            DataType type,
            DisplayFormat format,
            string? text,
            int stringLength,
            ByteOrderSettings orders,
            out ushort[] registers,
            out string error)
        {
            registers = Array.Empty<ushort>();
            error = string.Empty;
            DataTypeSpec spec = DataTypes.Get(type);

            if (spec.CoilOnly)
            {
                error = "BOOL 只能写线圈（FC05），不能作为寄存器写入";
                return false;
            }

            if (type == DataType.String)
            {
                if (stringLength <= 0)
                {
                    error = "STRING 长度必须大于 0";
                    return false;
                }

                registers = EncodeStringFixed(text ?? string.Empty, stringLength, orders.String);
                return true;
            }

            if (type == DataType.Real)
            {
                string s = (text ?? string.Empty).Trim();
                if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    error = $"「{s}」不是合法的浮点数";
                    return false;
                }

                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    error = "不支持写入 NaN / 无穷大";
                    return false;
                }

                registers = Split32(unchecked((uint)BitConverter.SingleToInt32Bits(f)), orders.Float);
                return true;
            }

            if (!TryParseIntegerLiteral(type, format, text, out long bits, out error))
            {
                return false;
            }

            registers = spec.BitWidth == 16
                ? new[] { (ushort)bits }
                : Split32(unchecked((uint)bits), orders.Int32);
            return true;
        }
    }
}
