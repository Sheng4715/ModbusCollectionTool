using System.Globalization;

namespace DSH_Modbus采集工具.Expressions
{
    /// <summary>词法单元的种类。</summary>
    public enum ExpressionTokenKind
    {
        End,
        Identifier,
        Number,
        True,
        False,
        LeftParen,
        RightParen,
        Not,
        AndAlso,
        OrElse,
        Equal,
        NotEqual,
        Greater,
        Less,
        GreaterOrEqual,
        LessOrEqual,
    }

    /// <summary>一个词法单元。<see cref="Position"/> 是**从 1 开始**的字符位置，直接进报错文案。</summary>
    public readonly struct ExpressionToken
    {
        public ExpressionToken(ExpressionTokenKind kind, string text, int position)
        {
            Kind = kind;
            Text = text;
            Position = position;
        }

        public ExpressionTokenKind Kind { get; }

        /// <summary>原文片段（数字是原样文本，标识符保留大小写）。</summary>
        public string Text { get; }

        /// <summary>起始字符位置，从 1 开始。</summary>
        public int Position { get; }

        public override string ToString() => $"{Kind}@{Position}:{Text}";
    }

    /// <summary>
    /// 表达式的词法分析。
    /// <para>
    /// 只认一套很小的语法：标识符、数字、<c>TRUE</c>/<c>FALSE</c>、括号、
    /// <c>! &amp;&amp; || == != &gt; &lt; &gt;= &lt;=</c>。
    /// </para>
    /// <para>
    /// ⚠️ 对**写错的运算符**给出专门提示，而不是笼统的「无法识别」：
    /// 单写 <c>=</c>、<c>&amp;</c>、<c>|</c> 是现场最容易犯的错（尤其 <c>=</c>），
    /// 报「无法识别字符“=”」会让人去查是不是全角，实际上只是少打一个。
    /// </para>
    /// </summary>
    public static class ExpressionLexer
    {
        public static bool TryTokenize(
            string? text, out List<ExpressionToken> tokens, out string error)
        {
            tokens = new List<ExpressionToken>();
            error = string.Empty;

            string s = text ?? string.Empty;
            int i = 0;

            while (i < s.Length)
            {
                char c = s[i];

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    i++;
                    continue;
                }

                int start = i + 1;   // 报错用 1 基位置

                // 标识符 / 关键字（数组元素写成 `变量名[下标]`，整段算一个标识符）
                if (IsIdentifierStart(c))
                {
                    int j = i + 1;
                    while (j < s.Length && IsIdentifierPart(s[j]))
                    {
                        j++;
                    }

                    // 数组元素后缀：`[` + 非负整数 + `]`。
                    // ⚠️ 数组本身**不能被引用**（用户定案 2026-11-10），所以这里
                    // 把下标和变量名编成**同一个词法单元**：`var1[0]` 走到求值/校验层
                    // 时仍是「一个名字」，不必在语法树里再加一层下标节点。
                    if (j < s.Length && s[j] == '[')
                    {
                        int open = j;
                        int k = j + 1;
                        int digitsStart = k;
                        while (k < s.Length && s[k] >= '0' && s[k] <= '9')
                        {
                            k++;
                        }

                        if (k == digitsStart)
                        {
                            error = k < s.Length && s[k] == ']'
                                ? $"第 {open + 1} 个字符：数组元素下标不能为空，应写成 变量名[0]"
                                : $"第 {open + 1} 个字符：数组元素下标只能是非负整数";
                            return false;
                        }

                        if (k >= s.Length || s[k] != ']')
                        {
                            error = $"第 {open + 1} 个字符：数组元素引用缺少右方括号“]”（应写成 变量名[0]）";
                            return false;
                        }

                        j = k + 1;
                    }

                    string word = s.Substring(i, j - i);
                    ExpressionTokenKind kind = word switch
                    {
                        // 关键字**不区分大小写**：true / True / TRUE 都行。
                        // 变量名仍然区分大小写（见 ExpressionNameRules），两者互不影响。
                        _ when word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) => ExpressionTokenKind.True,
                        _ when word.Equals("FALSE", StringComparison.OrdinalIgnoreCase) => ExpressionTokenKind.False,
                        _ => ExpressionTokenKind.Identifier,
                    };

                    tokens.Add(new ExpressionToken(kind, word, start));
                    i = j;
                    continue;
                }

                // 数字（整数或小数）
                if (c >= '0' && c <= '9')
                {
                    int j = i;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                    {
                        j++;
                    }

                    // 小数点必须**后面还有数字**，否则 "1." 不该被吞掉
                    if (j < s.Length && s[j] == '.' && j + 1 < s.Length
                        && s[j + 1] >= '0' && s[j + 1] <= '9')
                    {
                        j++;
                        while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                        {
                            j++;
                        }
                    }

                    string literal = s.Substring(i, j - i);
                    tokens.Add(new ExpressionToken(ExpressionTokenKind.Number, literal, start));
                    i = j;
                    continue;
                }

                // 双字符运算符
                if (i + 1 < s.Length)
                {
                    string two = s.Substring(i, 2);
                    ExpressionTokenKind? kind2 = two switch
                    {
                        "&&" => ExpressionTokenKind.AndAlso,
                        "||" => ExpressionTokenKind.OrElse,
                        "==" => ExpressionTokenKind.Equal,
                        "!=" => ExpressionTokenKind.NotEqual,
                        ">=" => ExpressionTokenKind.GreaterOrEqual,
                        "<=" => ExpressionTokenKind.LessOrEqual,
                        _ => null,
                    };

                    if (kind2.HasValue)
                    {
                        tokens.Add(new ExpressionToken(kind2.Value, two, start));
                        i += 2;
                        continue;
                    }
                }

                // 单字符
                ExpressionTokenKind? kind1 = c switch
                {
                    '(' => ExpressionTokenKind.LeftParen,
                    ')' => ExpressionTokenKind.RightParen,
                    '!' => ExpressionTokenKind.Not,
                    '>' => ExpressionTokenKind.Greater,
                    '<' => ExpressionTokenKind.Less,
                    _ => null,
                };

                if (kind1.HasValue)
                {
                    tokens.Add(new ExpressionToken(kind1.Value, c.ToString(), start));
                    i++;
                    continue;
                }

                // 常见的「少打一个」—— 单独给提示
                if (c == '=')
                {
                    error = $"第 {start} 个字符：“=”是赋值符号，比较相等请用“==”";
                    return false;
                }

                if (c == '&')
                {
                    error = $"第 {start} 个字符：逻辑与请用“&&”";
                    return false;
                }

                if (c == '|')
                {
                    error = $"第 {start} 个字符：逻辑或请用“||”";
                    return false;
                }

                // 全角符号是最常见的粘贴来源，单独提示一句
                if (IsFullWidth(c))
                {
                    error = $"第 {start} 个字符：“{c}”是全角符号，请改成半角";
                    return false;
                }

                if (c == '[')
                {
                    error = $"第 {start} 个字符：数组元素下标必须紧跟在变量名后面，如 var1[0]";
                    return false;
                }

                if (c == ']')
                {
                    error = $"第 {start} 个字符：多余的右方括号“]”（前面没有与之配对的“[”）";
                    return false;
                }

                error = $"第 {start} 个字符：无法识别的字符“{c}”";
                return false;
            }

            tokens.Add(new ExpressionToken(ExpressionTokenKind.End, string.Empty, s.Length + 1));
            return true;
        }

        /// <summary>标识符首字符：ASCII 字母或下划线（与变量命名规则一致）。</summary>
        private static bool IsIdentifierStart(char c) =>
            c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsIdentifierPart(char c) =>
            IsIdentifierStart(c) || (c >= '0' && c <= '9');

        /// <summary>全角 ASCII 区间（！＂＃…～）以及全角空格。</summary>
        private static bool IsFullWidth(char c) =>
            (c >= '\uFF01' && c <= '\uFF5E') || c == '\u3000';

        /// <summary>把词法单元的数字文本转成 <see cref="double"/>。</summary>
        public static bool TryParseNumber(ExpressionToken token, out double value) =>
            double.TryParse(
                token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
