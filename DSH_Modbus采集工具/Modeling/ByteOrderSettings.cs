using System;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 数据解释用的三套字节顺序：32 位整数 / 32 位浮点 / 字符串。
    /// <para>
    /// 三者<b>各自独立</b>：现场确实存在「DINT 是 3412、REAL 是 1234」这类组合。
    /// 早期把 REAL 与 DINT 绑在同一套字序上（<see cref="ValueCodec.FormatRegisters"/> 只收一个
    /// <see cref="ByteOrder32"/>），设备连接页那个「32位浮点数字节顺序」下拉框改了也不生效 ——
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

        /// <summary>取某个品牌驱动的出厂默认值；未选驱动时用 <see cref="Default"/>。</summary>
        public static ByteOrderSettings FromDriver(CommDriver? driver) => driver is null
            ? Default
            : new ByteOrderSettings(driver.Int32ByteOrder, driver.FloatByteOrder, driver.StringByteOrder);

        /// <summary>
        /// 未选驱动时的兜底值 = <b>信捷 XD 的取值</b>（<c>3412</c> / <c>3412</c> / <c>21</c>）。
        /// <para>
        /// 原先这里填的是「大端 <c>1234</c>」—— 那是照通用 Modbus 惯例猜的，本项目两台验收机
        /// （信捷 XD、汇川 Easy）实测**都不是大端**，猜错只会给出看着正常的错值。
        /// 兜底取主力品牌（信捷）的实测值，至少落在「本项目真机成立」的一侧。
        /// 实际运行中这条几乎不会走到：设备类型下拉恒有选中项，构造期就 <c>SetDriver</c> 了。
        /// </para>
        /// <para>
        /// ⚠️ 字符串序随信捷改为 <c>21</c>（2026-11-20，用户实测：信捷新增了真正的 STRING 类型，
        /// 旧 <c>12</c> 是针对 byte 数组模拟的写法）。见 <c>DriverRegistry</c> 同处注释。
        /// </para>
        /// </summary>
        public static ByteOrderSettings Default { get; } = new(
            ByteOrder32.Order3412, ByteOrder32.Order3412, StringByteOrder.Order21);

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
