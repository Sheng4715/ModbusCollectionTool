using System.Globalization;

namespace DSH_Modbus采集工具.Expressions
{
    /// <summary>
    /// 表达式求值过程中的一个值：要么是 <c>BOOL</c>，要么是数字。
    /// <para>
    /// 只做两种类型 —— 表达式是给「状态值」用的，不需要字符串、不需要数组。
    /// 内部统一存成 <see cref="double"/>，<see cref="IsBool"/> 只用来区分**来源**，
    /// 这样 `sys_True == 1` 这种写法能按 0/1 比较，不必报「类型不符」。
    /// </para>
    /// </summary>
    public readonly struct ExpressionValue
    {
        private ExpressionValue(double number, bool isBool)
        {
            Number = number;
            IsBool = isBool;
        }

        /// <summary>数值。BOOL 时是 0 或 1。</summary>
        public double Number { get; }

        /// <summary>这个值是不是 BOOL 来源（变量类型为 BOOL，或比较运算的结果）。</summary>
        public bool IsBool { get; }

        /// <summary>按 BOOL 解读（非 0 即真）。</summary>
        public bool AsBool => Number != 0;

        public static ExpressionValue FromBool(bool value) => new(value ? 1 : 0, true);

        public static ExpressionValue FromNumber(double value) => new(value, false);

        /// <summary>写进报错文案时的样子（<c>TRUE</c> / <c>FALSE</c> / 数字）。</summary>
        public string Display => IsBool
            ? (AsBool ? "TRUE" : "FALSE")
            : Number.ToString(CultureInfo.InvariantCulture);

        public override string ToString() => Display;
    }
}
