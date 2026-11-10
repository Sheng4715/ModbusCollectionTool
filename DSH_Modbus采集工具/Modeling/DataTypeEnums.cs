namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 用户可声明的数据类型。类型由**用户声明**，不做自动推断 ——
    /// 同一段寄存器按不同类型解释会得到完全不同的值，猜错比报错更危险。
    /// <para>
    /// ⚠️ 单独成文件（原在 <c>DataTypes.cs</c> 顶部）：枚举本身**零依赖**，
    /// 而 <c>DataTypes.cs</c> 的规格表要用到驱动层常量。拆开后
    /// <see cref="ArrayRules"/> 这类只判类型的纯逻辑才能被最小断言工程单独链接。
    /// </para>
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

        /// <summary>字符串，每寄存器 2 字符，占 ⌈n/2⌉ 个寄存器（n 由 <c>DataPoint.Length</c> 声明）</summary>
        String = 6,

        /// <summary>
        /// 数组（2026-11-09 用户要求）：N 个**同类型**元素连续排布。
        /// <para>
        /// 元素类型存 <c>DataPoint.ElementType</c>（**不能是 STRING**）、
        /// 元素个数存 <c>DataPoint.Length</c>（1~128）。
        /// 占用的单元数 = 元素个数 × 单个元素的单元数。
        /// </para>
        /// <para>
        /// ⚠️ 枚举值追加在**末尾**（= 7）：落盘时是名字而非数字，
        /// 但断言工程与旧代码里有按数值比较的地方，追加比插中间安全。
        /// </para>
        /// </summary>
        Array = 7,
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
}
