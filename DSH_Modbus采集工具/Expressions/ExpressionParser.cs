namespace DSH_Modbus采集工具.Expressions
{
    /// <summary>
    /// 递归下降语法分析。
    /// <para>优先级从低到高：<c>||</c> → <c>&amp;&amp;</c> → 比较 → <c>!</c> → 括号/字面量/变量。</para>
    /// <para>
    /// 比较运算**不允许连写**（<c>a &lt; b &lt; c</c> 会报错）—— 
    /// 这种写法在 C 里也是错的，与其算出一个反直觉的结果，不如直接说清楚。
    /// </para>
    /// </summary>
    internal sealed class ExpressionParser
    {
        /// <summary>
        /// 递归深度上限。超过就报「表达式嵌套过深」而不是继续递归。
        /// <para>
        /// ⚠️ 递归下降分析器对 <c>((((((…))))))</c> 或 <c>!!!!…</c> 是**真递归**，
        /// 深度随输入线性增长。用户（或手改的 json）塞进去几千个左括号时，
        /// 栈会直接溢出 —— 而 **StackOverflowException 在 .NET 里不可捕获**，
        /// 整个进程当场消失，连日志都留不下。故必须在递归前主动设闸门。
        /// </para>
        /// <para>
        /// 取 64：正常表达式（状态机里最长的那条 <c>(sys_DataTime_Second == 0) || sys_Pulse2s</c>）
        /// 嵌套深度不到 5，64 已是它的十几倍，不可能误伤真实配置。
        /// </para>
        /// </summary>
        private const int MaxDepth = 64;

        private readonly List<ExpressionToken> _tokens;
        private int _index;
        private int _depth;
        private string _error = string.Empty;

        public ExpressionParser(List<ExpressionToken> tokens)
        {
            _tokens = tokens;
        }

        private ExpressionToken Current => _tokens[_index];

        private bool Failed => _error.Length > 0;

        public ExpressionNode? Parse(out string error)
        {
            ExpressionNode node = ParseOr();
            if (Failed)
            {
                error = _error;
                return null;
            }

            if (Current.Kind != ExpressionTokenKind.End)
            {
                error = Current.Kind == ExpressionTokenKind.RightParen
                    ? $"第 {Current.Position} 个字符：多余的右括号“)”（前面没有与之配对的“(”）"
                    : $"第 {Current.Position} 个字符：多余的内容“{Current.Text}”（两个操作数之间是不是少了运算符？）";
                return null;
            }

            error = string.Empty;
            return node;
        }

        private ExpressionNode ParseOr()
        {
            ExpressionNode left = ParseAnd();
            while (!Failed && Current.Kind == ExpressionTokenKind.OrElse)
            {
                ExpressionToken op = Current;
                Advance();
                ExpressionNode right = ParseAnd();
                left = new BinaryNode(ExpressionTokenKind.OrElse, left, right, op.Position);
            }

            return left;
        }

        private ExpressionNode ParseAnd()
        {
            ExpressionNode left = ParseComparison();
            while (!Failed && Current.Kind == ExpressionTokenKind.AndAlso)
            {
                ExpressionToken op = Current;
                Advance();
                ExpressionNode right = ParseComparison();
                left = new BinaryNode(ExpressionTokenKind.AndAlso, left, right, op.Position);
            }

            return left;
        }

        private ExpressionNode ParseComparison()
        {
            ExpressionNode left = ParseUnary();
            if (Failed || !IsComparison(Current.Kind))
            {
                return left;
            }

            ExpressionToken op = Current;
            Advance();
            ExpressionNode right = ParseUnary();
            if (Failed)
            {
                return left;
            }

            // 连写比较：a < b < c
            if (IsComparison(Current.Kind))
            {
                _error = $"第 {Current.Position} 个字符：比较不能连写，请用“&&”连接";
                return left;
            }

            return new BinaryNode(op.Kind, left, right, op.Position);
        }

        private ExpressionNode ParseUnary()
        {
            // 深度闸门：`!!!!…` 与嵌套括号都走这里/ParsePrimary，两边各计一次
            if (++_depth > MaxDepth)
            {
                _error = $"第 {Current.Position} 个字符：表达式嵌套过深（超过 {MaxDepth} 层）";
                _depth = 0;
                return new LiteralNode(ExpressionValue.FromBool(false), Current.Position);
            }

            try
            {
                if (Current.Kind == ExpressionTokenKind.Not)
                {
                    ExpressionToken op = Current;
                    Advance();
                    ExpressionNode operand = ParseUnary();
                    return new NotNode(operand, op.Position);
                }

                return ParsePrimary();
            }
            finally
            {
                _depth--;
            }
        }

        private ExpressionNode ParsePrimary()
        {
            ExpressionToken token = Current;

            switch (token.Kind)
            {
                case ExpressionTokenKind.Number:
                    Advance();
                    if (!ExpressionLexer.TryParseNumber(token, out double number))
                    {
                        _error = $"第 {token.Position} 个字符：数字“{token.Text}”无法解析";
                        return new LiteralNode(ExpressionValue.FromNumber(0), token.Position);
                    }

                    return new LiteralNode(ExpressionValue.FromNumber(number), token.Position);

                case ExpressionTokenKind.True:
                    Advance();
                    return new LiteralNode(ExpressionValue.FromBool(true), token.Position);

                case ExpressionTokenKind.False:
                    Advance();
                    return new LiteralNode(ExpressionValue.FromBool(false), token.Position);

                case ExpressionTokenKind.Identifier:
                    Advance();
                    return new VariableNode(token.Text, token.Position);

                case ExpressionTokenKind.LeftParen:
                {
                    Advance();

                    // 括号是另一条递归路径（ParsePrimary → ParseOr → … → ParsePrimary），
                    // 必须与 ParseUnary 共用同一个深度计数器，否则 `((((…))))` 仍会爆栈。
                    if (++_depth > MaxDepth)
                    {
                        _error = $"第 {token.Position} 个字符：表达式嵌套过深（超过 {MaxDepth} 层）";
                        _depth = 0;
                        return new LiteralNode(ExpressionValue.FromBool(false), token.Position);
                    }

                    ExpressionNode inner;
                    try
                    {
                        inner = ParseOr();
                    }
                    finally
                    {
                        _depth--;
                    }

                    if (Failed)
                    {
                        return inner;
                    }

                    if (Current.Kind != ExpressionTokenKind.RightParen)
                    {
                        _error = Current.Kind == ExpressionTokenKind.End
                            ? $"第 {Current.Position} 个字符：缺少右括号“)”（与第 {token.Position} 个字符的“(”配对）"
                            : $"第 {Current.Position} 个字符：这里应该是右括号“)”";
                        return inner;
                    }

                    Advance();
                    return inner;
                }

                case ExpressionTokenKind.End:
                    _error = $"第 {token.Position} 个字符：表达式意外结束";
                    return new LiteralNode(ExpressionValue.FromBool(false), token.Position);

                case ExpressionTokenKind.RightParen:
                    _error = $"第 {token.Position} 个字符：多余或无处匹配的右括号“)”";
                    return new LiteralNode(ExpressionValue.FromBool(false), token.Position);

                default:
                    _error = $"第 {token.Position} 个字符：“{token.Text}”前面缺少操作数";
                    return new LiteralNode(ExpressionValue.FromBool(false), token.Position);
            }
        }

        private static bool IsComparison(ExpressionTokenKind kind) => kind is
            ExpressionTokenKind.Equal or
            ExpressionTokenKind.NotEqual or
            ExpressionTokenKind.Greater or
            ExpressionTokenKind.Less or
            ExpressionTokenKind.GreaterOrEqual or
            ExpressionTokenKind.LessOrEqual;

        private void Advance()
        {
            // End 是哨兵，停在它上面，避免越界
            if (_index < _tokens.Count - 1)
            {
                _index++;
            }
        }
    }
}
