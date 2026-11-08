using System;
using System.Collections.Generic;
using System.Globalization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 系统变量的取值（P3.5 定义 / P2.3 展示共用）。
    /// <para>
    /// 纯函数：**输入全部显式传进来**，不读 `DateTime.Now`、不读静态状态。
    /// 这样离线断言可以随便造时刻（跨秒 / 跨分 / 跨天 / 第 0 毫秒），
    /// 而不用等真时间走到那一点；P2.3 那边也只需一个定时器喂参数。
    /// </para>
    /// <para>
    /// 两类时间故意用不同的源：
    /// </para>
    /// <list type="bullet">
    /// <item><b>系统时钟</b>（`sys_DateTime_*`）用墙上时间 <paramref name="now"/> —— 用户改系统时间，它就该跟着变；</item>
    /// <item><b>运行时长与脉冲</b>（`sys_RunTime_*` / `sys_Pulse*`）用<b>单调的</b> <paramref name="uptime"/>（`Stopwatch`）——
    /// 系统时间被校准时，运行时长不能跳变、脉冲不能抖一下，否则下游会读到一次假跳变。</item>
    /// </list>
    /// </summary>
    public static class SystemVariableValues
    {
        /// <summary>
        /// 算出某一时刻全部系统变量的值（变量名 → 显示文本）。
        /// <para>
        /// <c>BOOL</c> 用 <c>"1"</c> / <c>"0"</c>，与界面里 BOOL 的 <c>BIN</c> 显示格式一致
        /// （不写 <c>"True"</c> / <c>"False"</c>：那一套在别处没有先例，只会让人怀疑值是不是字符串）。
        /// </para>
        /// </summary>
        /// <param name="now">墙上时间（系统时钟类变量的来源）。</param>
        /// <param name="uptime">自程序启动以来的单调时长；负数按 0 处理。</param>
        public static IReadOnlyDictionary<string, string> Capture(DateTime now, TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero)
            {
                uptime = TimeSpan.Zero;
            }

            var values = new Dictionary<string, string>(SystemVariable.All.Count, StringComparer.Ordinal);
            foreach (SystemVariable v in SystemVariable.All)
            {
                values[v.Name] = ValueOf(v.Kind, now, uptime);
            }

            return values;
        }

        /// <summary>
        /// 按 <see cref="SystemVariableKind"/> 取值。
        /// <para>
        /// ⚠️ **不写 <c>default</c> 分支**：新增一种 Kind 而忘了在这里加分支时，
        /// 编译器（CS8509）会当场报「未处理所有可能值」。加上 default 就等于把这层保护关掉了，
        /// 新变量会安静地返回上一次的值或抛异常，两者都难查。
        /// </para>
        /// <para>
        /// 这里只关掉 <b>CS8524</b>（「可能被强转成未命名的枚举值」）—— 那个告警对任何不写
        /// default 的枚举 switch 恒亮，属噪声；<c>Kind</c> 只来自 <see cref="SystemVariable.All"/>，
        /// 不存在强转出来的野值。**CS8509 必须留着**，它才是「加了 Kind 忘了分支」的哨兵。
        /// </para>
        /// </summary>
#pragma warning disable CS8524
        private static string ValueOf(SystemVariableKind kind, DateTime now, TimeSpan uptime)
        {
            long ms = (long)uptime.TotalMilliseconds;

            return kind switch
            {
                // 脉冲从「启动瞬间」起算，相位取 ON：程序一起来就亮，用户一眼看得出它在动
                SystemVariableKind.ConstTrue => "1",
                SystemVariableKind.ConstFalse => "0",
                SystemVariableKind.Pulse100ms => OnOff((ms / 50) % 2 == 0),
                SystemVariableKind.Pulse1s => OnOff((ms / 500) % 2 == 0),
                SystemVariableKind.Pulse2s => OnOff((ms / 1000) % 2 == 0),

                // 1min：30s ON / 30s OFF。用 30000ms 而不是「秒 × 30」——
                // 脉冲一律走毫秒口径，与上面三个保持同一种写法（少一处换算就少一处出错机会）。
                // 1000ms 窗口半周期 = 30000ms；启动瞬间 (0/30000)%2==0 → ON，与其余脉冲一致
                SystemVariableKind.Pulse1min => OnOff((ms / 30000) % 2 == 0),

                SystemVariableKind.DateTimeSecond => Int(now.Second),
                SystemVariableKind.DateTimeMinute => Int(now.Minute),
                SystemVariableKind.DateTimeHour => Int(now.Hour),
                SystemVariableKind.DateTimeWeek => Int(IsoWeekday(now.DayOfWeek)),
                SystemVariableKind.DateTimeDay => Int(now.Day),
                SystemVariableKind.DateTimeMonth => Int(now.Month),
                SystemVariableKind.DateTimeYear => Int(now.Year),

                // 秒 / 分 / 时按各自的进位取模，日累加不取模
                SystemVariableKind.RunTimeSecond => Int((int)(uptime.TotalSeconds % 60)),
                SystemVariableKind.RunTimeMinute => Int((int)(uptime.TotalMinutes % 60)),
                SystemVariableKind.RunTimeHour => Int((int)(uptime.TotalHours % 24)),
                SystemVariableKind.RunTimeDay => Int((int)uptime.TotalDays),
            };
        }
#pragma warning restore CS8524

        /// <summary>
        /// 星期取值 <b>1~7</b>（周一=1、周日=7），不是 .NET 原生的 0~6。
        /// <para>需求定稿：按中国人说「星期几」的习惯，周日是 7 而不是 0。</para>
        /// </summary>
        private static int IsoWeekday(DayOfWeek day) =>
            day == DayOfWeek.Sunday ? 7 : (int)day;

        private static string OnOff(bool on) => on ? "1" : "0";

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
