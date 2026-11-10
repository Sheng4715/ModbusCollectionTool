using System;
using System.Collections.Generic;
using System.Globalization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// <c>ARRAY</c> 类型的**逐元素**读写规则（2026-11-09 用户要求）。
    /// <para>
    /// 需求口径：数组在界面上**展开成 N 行**，每行一个元素、可单独写入。
    /// 故这里不提供「整组格式化成 <c>[1, 2, 3]</c>」的入口 —— 用户明确不要整组显示。
    /// </para>
    /// <para>
    /// 纯逻辑、不引 WPF，可链接进断言工程。
    /// </para>
    /// </summary>
    public static class ArrayValueCodec
    {
        /// <summary>
        /// 数组项某个**元素**在整段原始数据里的起始偏移与占用单元数。
        /// <para>
        /// 元素是**定长连续**排布的：第 i 个元素从 <c>i × 单元素单元数</c> 开始。
        /// 这正是「元素类型不能是 STRING」的原因 —— 定长才能靠乘法算出边界。
        /// </para>
        /// </summary>
        /// <param name="elementType">元素类型（不含 STRING）</param>
        /// <param name="index">元素下标（0 基）</param>
        /// <returns>单元偏移；下标越界返回 −1</returns>
        public static int OffsetOf(DataType elementType, int index)
        {
            if (index < 0)
            {
                return -1;
            }

            return index * DataTypes.ElementUnitsOf(elementType, 0);
        }

        /// <summary>
        /// 把一段原始数据按指定元素切片。
        /// </summary>
        /// <param name="elementType">元素类型</param>
        /// <param name="raw">该项占用的全部单元（来自一次块读）</param>
        /// <param name="index">元素下标（0 基）</param>
        /// <param name="slice">该元素的切片</param>
        /// <returns>切片是否成功（越界 / 长度不足返回 false）</returns>
        public static bool TrySliceElement(
            DataType elementType, ReadOnlySpan<ushort> raw, int index, out ReadOnlySpan<ushort> slice)
        {
            int units = DataTypes.ElementUnitsOf(elementType, 0);
            int offset = OffsetOf(elementType, index);

            if (units <= 0 || offset < 0 || offset + units > raw.Length)
            {
                slice = default;
                return false;
            }

            slice = raw.Slice(offset, units);
            return true;
        }

        /// <summary>
        /// 数组项**线圈元素**的取值（<c>ARRAY&lt;BOOL&gt;</c>）。
        /// <para>⚠️ BOOL 元素走线圈区，与寄存器元素是两条完全不同的路径 —— 调用方按 Area 选。</para>
        /// </summary>
        public static bool TryGetCoilElement(ReadOnlySpan<bool> raw, int index, out bool value)
        {
            value = false;

            if (index < 0 || index >= raw.Length)
            {
                return false;
            }

            value = raw[index];
            return true;
        }

        /// <summary>
        /// 把一个元素格式化成显示文本。
        /// <para>
        /// 元素**一律用各自类型的默认格式**（用户 2026-11-09 定案：不做整组显示，
        /// 元素就是普通的单值行）：整数 DEC、浮点 DEC、BOOL 用 <c>1</c>/<c>0</c>。
        /// </para>
        /// </summary>
        public static string FormatElement(
            DataType elementType, ReadOnlySpan<ushort> slice, ByteOrderSettings orders)
        {
            if (elementType == DataType.Bool)
            {
                // 理论上 BOOL 不会走寄存器路径，这里兜底成 0/1
                return ValueCodec.FormatCoil(slice.Length > 0 && slice[0] != 0);
            }

            return ValueCodec.FormatRegisters(
                elementType, slice, DisplayFormat.Dec, orders, 0);
        }

        /// <summary>
        /// 把一个元素的输入文本编码成寄存器（写入用）。
        /// <para>
        /// 与整项写入的唯一差别：**只用本元素的类型**，不涉及数组本身；
        /// 写下去的地址由调用方按 <see cref="OffsetOf"/> 加上去。
        /// </para>
        /// </summary>
        /// <param name="elementType">元素类型</param>
        /// <param name="text">用户输入的文本</param>
        /// <param name="orders">三套字节顺序</param>
        /// <param name="registers">编码结果</param>
        /// <param name="error">失败原因</param>
        public static bool TryEncodeElement(
            DataType elementType,
            string? text,
            ByteOrderSettings orders,
            out ushort[] registers,
            out string error)
        {
            registers = Array.Empty<ushort>();

            if (elementType == DataType.Bool)
            {
                error = "BOOL 元素应走线圈写入（FC05），不能编码成寄存器";
                return false;
            }

            int expected = DataTypes.ElementUnitsOf(elementType, 0);
            if (!ValueCodec.TryEncodeRegisters(
                    elementType, DisplayFormat.Dec, text, 0, orders, out ushort[] encoded, out error))
            {
                return false;
            }

            // 元素必须是**定长**的：编码结果与元素占用不符说明调用方把类型传错了
            // （例如拿数组本身当元素类型），此时写下去会越过本元素、覆盖邻居。
            if (encoded.Length != expected)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"元素编码长度不符：应为 {expected} 个寄存器，实际 {encoded.Length}");
                return false;
            }

            registers = encoded;
            return true;
        }

        /// <summary>
        /// 校验数组声明是否可用（元素类型合法 + 元素个数在 1~128）。
        /// <para>⚠️ 规则本体在 <see cref="ArrayRules"/>（零依赖，地址校验也要用）；这里转发一次，
        /// 只为让「编解码相关」的调用点不必同时引用两个类。</para>
        /// </summary>
        public static bool TryValidateDeclaration(
            DataType elementType, int elementCount, out string error) =>
            ArrayRules.TryValidate(elementType, elementCount, out error);

        /// <summary>
        /// 数组元素个数 → 元素名（界面上子行的显示名，如 <c>D100[0]</c>）。
        /// <para>
        /// 下标**从 0 开始**：与 PLC 侧「基址 + 偏移」的直觉一致，
        /// 且与 <see cref="OffsetOf"/> 的入参同口径，不额外做 1 基换算。
        /// </para>
        /// </summary>
        public static string ElementName(string baseName, int index) =>
            string.IsNullOrEmpty(baseName) ? $"[{index}]" : $"{baseName}[{index}]";

        /// <summary>列出全部元素下标（0..count-1），供界面建子行。</summary>
        public static IEnumerable<int> ElementIndices(int elementCount)
        {
            for (int i = 0; i < elementCount; i++)
            {
                yield return i;
            }
        }
    }
}
