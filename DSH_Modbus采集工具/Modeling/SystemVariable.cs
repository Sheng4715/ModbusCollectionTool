using System.Collections.Generic;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 系统变量的「行为身份」。
    /// <para>
    /// ⚠️ 取值必须按 <b>Kind</b> 分派，**不能按变量名**：名字是对外约定，改过不止一次
    /// （`sys_true` → `sys_True`），而行为不该跟着名字变 —— 按名字写 `switch` 的话，
    /// 每次改名都要同步改一处看不见的取值逻辑，改漏了就是「名字还在、值突然变成 0」。
    /// </para>
    /// </summary>
    public enum SystemVariableKind
    {
        /// <summary>常量，值恒为 1</summary>
        ConstTrue = 0,

        /// <summary>常量，值恒为 0</summary>
        ConstFalse = 1,

        /// <summary>100ms 周期时钟（50ms ON / 50ms OFF）</summary>
        Pulse100ms = 2,

        /// <summary>1s 周期时钟（0.5s ON / 0.5s OFF）</summary>
        Pulse1s = 3,

        /// <summary>2s 周期时钟（1s ON / 1s OFF）</summary>
        Pulse2s = 4,

        /// <summary>系统时钟：秒</summary>
        DateTimeSecond = 5,

        /// <summary>系统时钟：分</summary>
        DateTimeMinute = 6,

        /// <summary>系统时钟：时</summary>
        DateTimeHour = 7,

        /// <summary>系统时钟：星期（1~7，周一=1、周日=7）</summary>
        DateTimeWeek = 8,

        /// <summary>系统时钟：日</summary>
        DateTimeDay = 9,

        /// <summary>系统时钟：月</summary>
        DateTimeMonth = 10,

        /// <summary>系统时钟：年</summary>
        DateTimeYear = 11,

        /// <summary>运行时长：秒（0~59）</summary>
        RunTimeSecond = 12,

        /// <summary>运行时长：分（0~59）</summary>
        RunTimeMinute = 13,

        /// <summary>运行时长：时（0~23）</summary>
        RunTimeHour = 14,

        /// <summary>运行时长：日（累加，不取模）</summary>
        RunTimeDay = 15,

        /// <summary>
        /// 1min 周期时钟（30s ON / 30s OFF）。
        /// <para>
        /// ⚠️ **追加在末尾（= 16），不是插在第 5 位**：枚举是显式数值，
        /// 插在中间会把 <c>DateTimeSecond</c> 及其后全部 +1。虽然 <c>Kind</c> 不落盘
        /// （见下方清单注释），但「改了既有值」总要多查一遍才敢放心；
        /// 追加到末尾则零风险。清单里的**序号**另行按显示顺序写死，与枚举值无关。
        /// </para>
        /// </summary>
        Pulse1min = 16,
    }

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
    /// ⚠️ 数据管理页的「系统变量」表（P3.5）与设备监控页的「系统变量」监控表（P2.3）
    /// **共用这一份清单**，不要各写一遍 —— 两处各写一份，迟早会出现「定义页 16 项、
    /// 监控页 15 项」这种对不上的情况。
    /// </para>
    /// </summary>
    public sealed class SystemVariable
    {
        /// <summary>序号，1 起，与清单顺序一致。</summary>
        public int Id { get; init; }

        /// <summary>
        /// 行为身份 —— 取值逻辑按它分派（见 <see cref="SystemVariableValues"/>）。
        /// <para>与 <see cref="Name"/> 分开，是为了让**改名不影响行为**。</para>
        /// </summary>
        public SystemVariableKind Kind { get; init; }

        /// <summary>变量名（C 标识符规则；固定 <c>sys_</c> 前缀）。这是对外约定，改它等于改协议。</summary>
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
        /// 全部系统变量（顺序即界面顺序，序号 1..17）。
        /// <para>
        /// 逐项写死、不做任何计算：这份清单是对外承诺的接口，
        /// 改动它等于改协议，应当跟着需求文档（Brief.md P3.5）一起改。
        /// </para>
        /// </summary>
        public static IReadOnlyList<SystemVariable> All { get; } = new[]
        {
            Item(1, SystemVariableKind.ConstTrue, "sys_True", DataType.Bool, "始终为true"),
            Item(2, SystemVariableKind.ConstFalse, "sys_False", DataType.Bool, "始终为false"),
            Item(3, SystemVariableKind.Pulse100ms, "sys_Pulse100ms", DataType.Bool, "100ms周期时钟：50ms ON，50ms OFF"),
            Item(4, SystemVariableKind.Pulse1s, "sys_Pulse1s", DataType.Bool, "1s周期时钟：0.5s ON，0.5s OFF"),
            Item(5, SystemVariableKind.Pulse2s, "sys_Pulse2s", DataType.Bool, "2s周期时钟：1s ON，1s OFF"),

            Item(6, SystemVariableKind.Pulse1min, "sys_Pulse1min", DataType.Bool, "1min周期时钟：30s ON，30s OFF"),

            Item(7, SystemVariableKind.DateTimeSecond, "sys_DateTime_Second", DataType.Int, "系统当前时钟：秒"),
            Item(8, SystemVariableKind.DateTimeMinute, "sys_DateTime_Minute", DataType.Int, "系统当前时钟：分"),
            Item(9, SystemVariableKind.DateTimeHour, "sys_DateTime_Hour", DataType.Int, "系统当前时钟：时"),
            Item(10, SystemVariableKind.DateTimeWeek, "sys_DateTime_Week", DataType.Int, "系统当前时钟：星期（1~7）"),
            Item(11, SystemVariableKind.DateTimeDay, "sys_DateTime_Day", DataType.Int, "系统当前时钟：日"),
            Item(12, SystemVariableKind.DateTimeMonth, "sys_DateTime_Month", DataType.Int, "系统当前时钟：月"),
            Item(13, SystemVariableKind.DateTimeYear, "sys_DateTime_Year", DataType.Int, "系统当前时钟：年"),

            Item(14, SystemVariableKind.RunTimeSecond, "sys_RunTime_Second", DataType.Int, "系统已运行时间：秒（0~59）"),
            Item(15, SystemVariableKind.RunTimeMinute, "sys_RunTime_Minute", DataType.Int, "系统已运行时间：分（0~59）"),
            Item(16, SystemVariableKind.RunTimeHour, "sys_RunTime_Hour", DataType.Int, "系统已运行时间：时（0~23）"),
            Item(17, SystemVariableKind.RunTimeDay, "sys_RunTime_Day", DataType.Int, "系统已运行时间：日"),
        };

        /// <summary>
        /// 按名字查系统变量（**区分大小写**，与变量名规则一致）。
        /// <para>
        /// 用于「自由监控里不许添加系统变量」这条限制（P2.3）：
        /// 系统变量不在全局变量表里，不拦的话它会一路掉到元件解析，
        /// 报成「不是有效软元件或变量」—— 原因不对，用户会去改元件名。
        /// </para>
        /// </summary>
        public static SystemVariable? Find(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (SystemVariable v in All)
            {
                if (string.Equals(v.Name, name, System.StringComparison.Ordinal))
                {
                    return v;
                }
            }

            return null;
        }

        private static SystemVariable Item(
            int id, SystemVariableKind kind, string name, DataType type, string comment) =>
            new SystemVariable { Id = id, Kind = kind, Name = name, Type = type, Comment = comment };
    }
}
