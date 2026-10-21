using System;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 数据解释用的三套字节顺序：32 位整数 / 32 位浮点 / 字符串。
    /// <para>
    /// 三者<b>各自独立</b>：现场确实存在「DINT 是 3412、REAL 是 1234」这类组合。
    /// 早期把 REAL 与 DINT 绑在同一套字序上（<see cref="ValueCodec.FormatRegisters"/> 只收一个
    /// <see cref="ByteOrder32"/>），设备配置页那个「32位浮点数字节顺序」下拉框改了也不生效 ——
    /// 表现为界面显示 1234、数值仍按 3412 解，**不报错但全是错值**。
    /// </para>
    /// <para>
    /// 取值规则（用户 2026-10-21 定案）：<b>切换设备类型时一律重置为驱动默认</b>，
    /// 用户在同一次设备类型下的改动立即生效、不跨重启保留。
    /// 本类型只承载值，不读全局状态 —— 由窗口构造好再传给编解码器。
    /// </para>
    /// </summary>
    public readonly struct ByteOrderSettings : IEquatable<ByteOrderSettings>
    {
        public ByteOrderSettings(ByteOrder32 int32, ByteOrder32 floatOrder, StringByteOrder stringOrder)
        {
            Int32 = int32;
            Float = floatOrder;
            String = stringOrder;
        }

        /// <summary>32 位整数（<c>DINT</c> / <c>UDINT</c>）的字序。</summary>
        public ByteOrder32 Int32 { get; }

        /// <summary>32 位浮点（<c>REAL</c>）的字序，<b>与 <see cref="Int32"/> 独立</b>。</summary>
        public ByteOrder32 Float { get; }

        /// <summary>字符串在寄存器内的字节先后。</summary>
        public StringByteOrder String { get; }

        /// <summary>取某个品牌驱动的出厂默认值；未选驱动时用大端。</summary>
        public static ByteOrderSettings FromDriver(CommDriver? driver) => driver is null
            ? Default
            : new ByteOrderSettings(driver.Int32ByteOrder, driver.FloatByteOrder, driver.StringByteOrder);

        /// <summary>未选驱动时的兜底值：全部大端（与 <see cref="CommDriver"/> 属性的缺省语义一致）。</summary>
        public static ByteOrderSettings Default { get; } = new(
            ByteOrder32.Order1234, ByteOrder32.Order1234, StringByteOrder.Order12);

        public bool Equals(ByteOrderSettings other)
            => Int32 == other.Int32 && Float == other.Float && String == other.String;

        public override bool Equals(object? obj) => obj is ByteOrderSettings other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Int32, Float, String);

        public static bool operator ==(ByteOrderSettings left, ByteOrderSettings right) => left.Equals(right);

        public static bool operator !=(ByteOrderSettings left, ByteOrderSettings right) => !left.Equals(right);

        /// <summary>日志/断言里用的短写：<c>DINT=3412 REAL=3412 STR=12</c>。</summary>
        public override string ToString()
            => $"DINT={Int32} REAL={Float} STR={String}";
    }
}
