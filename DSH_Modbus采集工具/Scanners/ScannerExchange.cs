using DSH_Modbus采集工具.Expressions;

namespace DSH_Modbus采集工具.Scanners
{
    /// <summary>
    /// 单台扫码器的**运行时状态**（P3.5 交互执行）。
    /// <para>
    /// 与 <see cref="ScannerDefinition"/> 分开：那个是配置、要落盘；这个只活在内存里，
    /// 记录「表达式上一拍是真是假」「本轮是否还在执行」等。重启一律重来。
    /// </para>
    /// </summary>
    public sealed class ScannerRuntimeState
    {
        /// <summary>触发表达式上一拍的取值（上升沿判定用）。</summary>
        public bool WasConditionTrue { get; set; }

        /// <summary>编译好的触发表达式（每拍复用，不重复解析）。</summary>
        public CompiledExpression? Compiled { get; set; }

        /// <summary>上面那份编译结果对应的原文 —— 表达式被改过就要重编。</summary>
        public string CompiledSource { get; set; } = string.Empty;

        /// <summary>
        /// 本轮触发是否仍在执行中（从发出触发到收到结果或超时）。
        /// <para>执行中互斥：上一次还没执行完时，新上升沿忽略（P3.5）。</para>
        /// </summary>
        public bool InFlight { get; set; }
    }

    /// <summary>
    /// 扫码器触发判定：上升沿有效（P3.5）。
    /// <para>纯函数，可离线断言。</para>
    /// </summary>
    public static class ScannerTrigger
    {
        /// <summary>
        /// 判定本拍是否触发，并推进 <paramref name="wasTrue"/>。
        /// <para>首次求值时 <paramref name="wasTrue"/> 为 <c>false</c>，故「程序启动时条件已成立」
        /// 会立刻触发一拍 —— 与采集任务上升沿同口径（当作启动前为假）。</para>
        /// </summary>
        public static bool ShouldFire(bool conditionTrue, ref bool wasTrue)
        {
            bool rising = conditionTrue && !wasTrue;
            wasTrue = conditionTrue;
            return rising;
        }
    }
}
