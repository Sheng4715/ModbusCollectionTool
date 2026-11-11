namespace DSH_Modbus采集工具.Expressions
{
    /// <summary>求值时用来取变量值的东西（由调用方实现：系统变量 + 全局变量）。</summary>
    public interface IExpressionVariables
    {
        /// <param name="name">变量名，区分大小写</param>
        /// <param name="value">取到的值</param>
        /// <param name="error">取不到时的原因（给用户看）</param>
        bool TryGetValue(string name, out ExpressionValue value, out string error);
    }

    /// <summary>
    /// 表达式求值期才会暴露的错误（类型不符、变量查不到 …）。
    /// <para>
    /// 语法错误在**编译期**就已经拦下并带位置了；能走到这里的只有
    /// 「语法没问题但值算不出来」的情况，故只需要一句人话 + 位置。
    /// </para>
    /// </summary>
    public sealed class ExpressionEvaluationException : Exception
    {
        public ExpressionEvaluationException(string message, int position)
            : base(message)
        {
            Position = position;
        }

        /// <summary>出错位置，从 1 开始。</summary>
        public int Position { get; }
    }

    /// <summary>表达式语法树的一个节点。</summary>
    public abstract class ExpressionNode
    {
        protected ExpressionNode(int position)
        {
            Position = position;
        }

        /// <summary>本节点在原文里的起始位置（从 1 开始），报错用。</summary>
        public int Position { get; }

        public abstract ExpressionValue Evaluate(IExpressionVariables variables);

        /// <summary>
        /// 静态推断本节点的值类型（编译期，不取值）。
        /// <para>
        /// 与 <see cref="Evaluate"/> 的分工：那个要真值、能发现「运行期才算得出来」的问题；
        /// 这个只看**声明类型**，用于保存表达式时当场拦下「拿 INT 变量当条件」这类错误。
        /// </para>
        /// <para>
        /// ⚠️ 推不出来一律返回 <see cref="ExpressionValueKind.Unknown"/> 且**不报错**：
        /// 变量不存在走 <c>TryValidateNames</c> 那条独立通道，这里误报会把能用的配置判成坏的。
        /// </para>
        /// </summary>
        /// <param name="typeOf">名字 → 静态类型；调用方不认识的名字会被返回 <c>Unknown</c></param>
        /// <param name="error">失败原因（**已带「第 N 个字符」前缀**，可直接展示）</param>
        public abstract ExpressionValueKind InferKind(
            Func<string, ExpressionVariableType> typeOf, out string error);

        /// <summary>把用到的变量名（连同位置）收集起来，供编译期做存在性校验。</summary>
        public abstract void CollectNames(ICollection<(string Name, int Position)> names);
    }

    /// <summary>常量：<c>TRUE</c> / <c>FALSE</c> / 数字。</summary>
    public sealed class LiteralNode : ExpressionNode
    {
        private readonly ExpressionValue _value;

        public LiteralNode(ExpressionValue value, int position)
            : base(position)
        {
            _value = value;
        }

        /// <summary>这个常量是不是数字（用于编译期「结果必须是 BOOL」的提示）。</summary>
        public bool IsNumber => !_value.IsBool;

        public override ExpressionValue Evaluate(IExpressionVariables variables) => _value;

        public override ExpressionValueKind InferKind(
            Func<string, ExpressionVariableType> typeOf, out string error)
        {
            error = string.Empty;
            return _value.IsBool ? ExpressionValueKind.Bool : ExpressionValueKind.Number;
        }

        public override void CollectNames(ICollection<(string Name, int Position)> names)
        {
        }
    }

    /// <summary>变量引用。</summary>
    public sealed class VariableNode : ExpressionNode
    {
        public VariableNode(string name, int position)
            : base(position)
        {
            Name = name;
        }

        public string Name { get; }

        public override ExpressionValue Evaluate(IExpressionVariables variables)
        {
            if (!variables.TryGetValue(Name, out ExpressionValue value, out string error))
            {
                throw new ExpressionEvaluationException(
                    error.Length > 0 ? error : $"变量“{Name}”不存在", Position);
            }

            return value;
        }

        public override ExpressionValueKind InferKind(
            Func<string, ExpressionVariableType> typeOf, out string error)
        {
            ExpressionVariableType type = typeOf(Name);

            // STRING 在表达式里**没有任何合法用法**（既不能当 BOOL，也比不出数值），
            // 故在这一处就报掉，省得每个上层节点各写一遍 —— 单独写 `strVar` 也走这里。
            if (type.Kind == ExpressionValueKind.Text)
            {
                error = CompiledExpression.NotBoolean;
                return ExpressionValueKind.Unknown;
            }

            error = string.Empty;
            return type.Kind;
        }

        public override void CollectNames(ICollection<(string Name, int Position)> names) =>
            names.Add((Name, Position));
    }

    /// <summary>逻辑非。</summary>
    public sealed class NotNode : ExpressionNode
    {
        private readonly ExpressionNode _operand;

        public NotNode(ExpressionNode operand, int position)
            : base(position)
        {
            _operand = operand;
        }

        public override ExpressionValue Evaluate(IExpressionVariables variables)
        {
            ExpressionValue value = _operand.Evaluate(variables);
            if (!value.IsBool)
            {
                throw new ExpressionEvaluationException(
                    $"“!”只能用于 BOOL，左边是数字 {value.Display}", Position);
            }

            return ExpressionValue.FromBool(!value.AsBool);
        }

        public override ExpressionValueKind InferKind(
            Func<string, ExpressionVariableType> typeOf, out string error)
        {
            ExpressionValueKind kind = _operand.InferKind(typeOf, out error);
            if (error.Length > 0)
            {
                return ExpressionValueKind.Unknown;
            }

            // Unknown / Text 都交给下面两条：Text 在操作数自己的推断里已经报过错了
            if (kind == ExpressionValueKind.Number)
            {
                error = CompiledExpression.NotBoolean;
                return ExpressionValueKind.Unknown;
            }

            error = string.Empty;
            return ExpressionValueKind.Bool;
        }

        public override void CollectNames(ICollection<(string Name, int Position)> names) =>
            _operand.CollectNames(names);
    }

    /// <summary>二元运算：比较 / 逻辑与 / 逻辑或。</summary>
    public sealed class BinaryNode : ExpressionNode
    {
        private readonly ExpressionTokenKind _op;
        private readonly ExpressionNode _left;
        private readonly ExpressionNode _right;

        public BinaryNode(
            ExpressionTokenKind op, ExpressionNode left, ExpressionNode right, int position)
            : base(position)
        {
            _op = op;
            _left = left;
            _right = right;
        }

        public override ExpressionValue Evaluate(IExpressionVariables variables)
        {
            // 逻辑与/或允许短路：右边写了个越界的东西，只要左边已经决定结果就不该炸。
            if (_op == ExpressionTokenKind.AndAlso)
            {
                ExpressionValue leftBool = RequireBool(_left.Evaluate(variables), "&&");
                if (!leftBool.AsBool)
                {
                    return ExpressionValue.FromBool(false);
                }

                return ExpressionValue.FromBool(
                    RequireBool(_right.Evaluate(variables), "&&").AsBool);
            }

            if (_op == ExpressionTokenKind.OrElse)
            {
                ExpressionValue leftBool = RequireBool(_left.Evaluate(variables), "||");
                if (leftBool.AsBool)
                {
                    return ExpressionValue.FromBool(true);
                }

                return ExpressionValue.FromBool(
                    RequireBool(_right.Evaluate(variables), "||").AsBool);
            }

            ExpressionValue left = _left.Evaluate(variables);
            ExpressionValue right = _right.Evaluate(variables);

            // 比较一律按**数值**比：BOOL 就是 0/1。
            // 这样 sys_True == 1、sys_False == 0 都能写，不必强求两侧类型一致。
            double a = left.Number;
            double b = right.Number;

            bool result = _op switch
            {
                ExpressionTokenKind.Equal => a == b,
                ExpressionTokenKind.NotEqual => a != b,
                ExpressionTokenKind.Greater => a > b,
                ExpressionTokenKind.Less => a < b,
                ExpressionTokenKind.GreaterOrEqual => a >= b,
                ExpressionTokenKind.LessOrEqual => a <= b,
                _ => throw new ExpressionEvaluationException("未知运算符", Position),
            };

            return ExpressionValue.FromBool(result);
        }

        private ExpressionValue RequireBool(ExpressionValue value, string op)
        {
            if (!value.IsBool)
            {
                throw new ExpressionEvaluationException(
                    $"“{op}”两侧必须都是 BOOL，这里出现了数字 {value.Display}", Position);
            }

            return value;
        }

        public override ExpressionValueKind InferKind(
            Func<string, ExpressionVariableType> typeOf, out string error)
        {
            ExpressionValueKind left = _left.InferKind(typeOf, out error);
            if (error.Length > 0)
            {
                return ExpressionValueKind.Unknown;
            }

            ExpressionValueKind right = _right.InferKind(typeOf, out error);
            if (error.Length > 0)
            {
                return ExpressionValueKind.Unknown;
            }

            // 比较：两侧按**数值**比（运行期就是这么做的，BOOL 就是 0/1），
            // 故任意数值 / BOOL 组合都合法，结果恒为 BOOL。Unknown 一并放行。
            if (_op is ExpressionTokenKind.Equal or ExpressionTokenKind.NotEqual
                or ExpressionTokenKind.Greater or ExpressionTokenKind.Less
                or ExpressionTokenKind.GreaterOrEqual or ExpressionTokenKind.LessOrEqual)
            {
                error = string.Empty;
                return ExpressionValueKind.Bool;
            }

            // 逻辑与 / 或：两侧必须都是 BOOL。
            // ⚠️ Unknown 放行 —— 推不出来的名字由名字校验那条通道报，这里不越权。
            if (left == ExpressionValueKind.Number || right == ExpressionValueKind.Number)
            {
                error = CompiledExpression.NotBoolean;
                return ExpressionValueKind.Unknown;
            }

            error = string.Empty;
            return ExpressionValueKind.Bool;
        }

        public override void CollectNames(ICollection<(string Name, int Position)> names)
        {
            _left.CollectNames(names);
            _right.CollectNames(names);
        }
    }
}
