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

    /// <summary>
    /// 表达式的**静态**类型（编译期推断出来的，与运行期的 <see cref="ExpressionValue"/> 对应）。
    /// <para>
    /// 为什么要它：运行期求值发现「结果不是 BOOL」时**已经晚了** —— 状态框那条表达式
    /// 早存进 json 了，界面上只表现为「框一直不亮」，采集任务表现为「再也不触发」，
    /// 而错误被 <c>out _</c> 吞掉，一个字都看不到。故顺手在保存时按**变量声明的类型**
    /// 先推一遍，能把「填了个 INT 变量当条件」当场说出来。
    /// </para>
    /// <para>
    /// ⚠️ <see cref="Unknown"/> 一律**放行**：推不出来（变量不存在、调用方没给类型表）
    /// 时绝不能报错 —— 名字不存在由 <c>TryValidateNames</c> 那条独立通道负责，
    /// 这里误报只会把「能用的配置」判成坏的。
    /// </para>
    /// </summary>
    public enum ExpressionValueKind
    {
        /// <summary>推不出来 —— **放行**，交由运行期或名字校验处理。</summary>
        Unknown = 0,

        /// <summary>BOOL（<c>DataType.Bool</c>、比较运算与逻辑运算的结果、<c>TRUE</c>/<c>FALSE</c>）</summary>
        Bool = 1,

        /// <summary>数值（<c>INT</c>/<c>UINT</c>/<c>DINT</c>/<c>UDINT</c>/<c>REAL</c> 与数字字面量）</summary>
        Number = 2,

        /// <summary>字符串（<c>STRING</c>）：表达式里**任何位置都不能用**，没有可比较的数值含义。</summary>
        Text = 3,
    }

    /// <summary>
    /// 一个变量引用的静态类型：种类 + 显示名（<c>INT</c> / <c>STRING</c> …）。
    /// <para>
    /// 显示名只用于报错文案（「「var」是 INT」比「非 BOOL」有用得多），
    /// 故允许为空 —— 调用方不知道该类型叫什么时留空即可，文案会退化成通用说法。
    /// </para>
    /// </summary>
    public readonly struct ExpressionVariableType
    {
        /// <summary>推不出来：**放行**。</summary>
        public static ExpressionVariableType UnknownType { get; } =
            new(ExpressionValueKind.Unknown, string.Empty);

        public ExpressionVariableType(ExpressionValueKind kind, string typeName)
        {
            Kind = kind;
            TypeName = string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName;
        }

        public ExpressionValueKind Kind { get; }

        /// <summary>类型显示名，如 <c>INT</c>；空串表示调用方没提供。</summary>
        public string TypeName { get; }
    }
}
