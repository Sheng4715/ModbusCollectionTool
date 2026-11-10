using System;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// <c>ARRAY</c> 的**声明规则**（纯逻辑、零依赖，2026-11-09 用户要求）。
    /// <para>
    /// 与 <see cref="ArrayValueCodec"/> 分开的理由：声明校验会被**地址校验**用到
    /// （<see cref="DataPoint.TryResolve"/> / <see cref="GlobalVariableStore.TryResolve"/>），
    /// 而那两处只需要「元素类型合法吗 / 元素个数在范围内吗」，完全不碰编解码。
    /// 合成一个类会让只做地址校验的断言工程被迫链接整个 <c>ValueCodec</c> 与 <c>ByteOrderSettings</c>。
    /// </para>
    /// <para>
    /// ⚠️ 元素类型**不能是 STRING**（长度可变 → 元素边界要靠除法推，用户填错一个数整段地址就错位）；
    /// 也不能是 ARRAY 自身（不支持嵌套）。
    /// </para>
    /// </summary>
    public static class ArrayRules
    {
        /// <summary>元素数量上限（需求：最大 128）。</summary>
        public const int MaxLength = 128;

        /// <summary>元素数量下限。</summary>
        public const int MinLength = 1;

        /// <summary>该类型能否当数组元素（除 STRING 与 ARRAY 外都可以，含 BOOL）。</summary>
        public static bool CanBeElement(DataType type) =>
            type is not (DataType.String or DataType.Array);

        /// <summary>
        /// 校验数组声明：元素类型合法 + 元素个数在 1~128。
        /// </summary>
        public static bool TryValidate(DataType elementType, int elementCount, out string error)
        {
            error = string.Empty;

            if (elementType == DataType.Array)
            {
                error = "数组元素不能还是数组（不支持嵌套）";
                return false;
            }

            if (elementType == DataType.String)
            {
                error = "数组元素不能是 STRING（长度可变，无法确定元素边界）";
                return false;
            }

            if (elementCount < MinLength || elementCount > MaxLength)
            {
                error = $"元素数量需在 {MinLength} ~ {MaxLength} 之间（当前 {elementCount}）";
                return false;
            }

            return true;
        }
    }
}
