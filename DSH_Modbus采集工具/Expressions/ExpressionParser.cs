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
        private readonly List<ExpressionToken> _tokens;
        private int _index;
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
            if (Current.Kind == ExpressionTokenKind.Not)
            {
                ExpressionToken op = Current;
                Advance();
                ExpressionNode operand = ParseUnary();
                return new NotNode(operand, op.Position);
            }

            return ParsePrimary();
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
                    ExpressionNode inner = ParseOr();
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
