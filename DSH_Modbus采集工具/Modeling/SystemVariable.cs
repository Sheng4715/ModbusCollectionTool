using System.Collections.Generic;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一条「系统变量」（P3.5）。
    /// <para>
    /// 与 <see cref="GlobalVariable"/> 的区别有三处：
    /// </para>
    /// <list type="bullet">
    /// <item>**写死**在代码里 —— 不落盘、不可增删改，清单本身就是程序对外约定的一部分；</item>
    /// <item>**只读** —— 没有映射地址（不映射任何 PLC 区），也没有显示格式（值由程序按类型算出）；</item>
    /// <item>名字固定带 `sys_` 前缀，与用户变量一眼可分。</item>
    /// </list>
    /// <para>
    /// ⚠️ 采集配置页的「系统变量」表（P3.5）与状态信息页的「系统变量」监控表（P2.3）
    /// **共用这一份清单**，不要各写一遍 —— 两处各写一份，迟早会出现「定义页 16 项、
    /// 监控页 15 项」这种对不上的情况。
    /// </para>
    /// </summary>
    public sealed class SystemVariable
    {
        /// <summary>序号，1 起，与清单顺序一致。</summary>
        public int Id { get; init; }

        /// <summary>变量名（C 标识符规则；固定 <c>sys_</c> 前缀）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>数据类型。目前只用到 <c>BOOL</c>（时钟类）与 <c>INT</c>（时间类）。</summary>
        public DataType Type { get; init; } = DataType.Int;

        /// <summary>注释（仅给人看，不参与任何计算）。</summary>
        public string Comment { get; init; } = string.Empty;

        /// <summary>
        /// 数据类型的显示名（<c>BOOL</c> / <c>INT</c>），取自 <see cref="DataTypes"/> 的规格表。
        /// <para>
        /// 刻意不在这里另写字面量：类型显示名一旦有第二份来源，
        /// 以后加类型或改名就会两处不一致，而这种不一致在表格里几乎看不出来。
        /// </para>
        /// </summary>
        public string TypeName => DataTypes.Get(Type).DisplayName;

        /// <summary>
        /// 全部系统变量（顺序即界面顺序，序号 1..16）。
        /// <para>
        /// 逐项写死、不做任何计算：这份清单是对外承诺的接口，
        /// 改动它等于改协议，应当跟着需求文档（Brief.md P3.5）一起改。
        /// </para>
        /// </summary>
        public static IReadOnlyList<SystemVariable> All { get; } = new[]
        {
            Item(1, "sys_True", DataType.Bool, "始终为true"),
            Item(2, "sys_False", DataType.Bool, "始终为false"),
            Item(3, "sys_Pulse100ms", DataType.Bool, "100ms周期时钟：50ms ON，50ms OFF"),
            Item(4, "sys_Pulse1s", DataType.Bool, "1s周期时钟：0.5s ON，0.5s OFF"),
            Item(5, "sys_Pulse2s", DataType.Bool, "2s周期时钟：1s ON，1s OFF"),

            Item(6, "sys_DateTime_Second", DataType.Int, "系统当前时钟：秒"),
            Item(7, "sys_DateTime_Minute", DataType.Int, "系统当前时钟：分"),
            Item(8, "sys_DateTime_Hour", DataType.Int, "系统当前时钟：时"),
            Item(9, "sys_DateTime_Week", DataType.Int, "系统当前时钟：星期"),
            Item(10, "sys_DateTime_Day", DataType.Int, "系统当前时钟：日"),
            Item(11, "sys_DateTime_Month", DataType.Int, "系统当前时钟：月"),
            Item(12, "sys_DateTime_Year", DataType.Int, "系统当前时钟：年"),

            Item(13, "sys_RunTime_Second", DataType.Int, "系统已运行时间：秒（0~59）"),
            Item(14, "sys_RunTime_Minute", DataType.Int, "系统已运行时间：分（0~59）"),
            Item(15, "sys_RunTime_Hour", DataType.Int, "系统已运行时间：时（0~23）"),
            Item(16, "sys_RunTime_Day", DataType.Int, "系统已运行时间：日"),
        };

        private static SystemVariable Item(int id, string name, DataType type, string comment) =>
            new SystemVariable { Id = id, Name = name, Type = type, Comment = comment };
    }
}
