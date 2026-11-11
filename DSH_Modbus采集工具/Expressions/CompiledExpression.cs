namespace DSH_Modbus采集工具.Expressions
{
    /// <summary>
    /// 一条编译好的 BOOL 表达式。
    /// <para>
    /// 用法分三步，对应界面上的两个时机：
    /// </para>
    /// <list type="number">
    /// <item><see cref="TryParse"/> —— 输入框失焦时跑，只有语法错误会在这里暴露；</item>
    /// <item><see cref="TryValidateNames"/> —— 同上，把「变量不存在」也一起报掉；</item>
    /// <item><see cref="TryEvaluate"/> —— 定时器每拍跑，只做求值，不再解析。</item>
    /// </list>
    /// <para>
    /// ⚠️ 解析结果**必须缓存**：状态机 9 条表达式、50ms 一拍，
    /// 每拍重新词法+语法分析纯属浪费（而且会持续产生垃圾）。
    /// </para>
    /// </summary>
    public sealed class CompiledExpression
    {
        private static readonly IReadOnlyList<(string Name, int Position)> NoNames =
            Array.Empty<(string, int)>();

        private readonly ExpressionNode? _root;
        private readonly IReadOnlyList<(string Name, int Position)> _usedNames;

        private CompiledExpression(
            string source, ExpressionNode? root, IReadOnlyList<(string Name, int Position)> usedNames)
        {
            Source = source;
            _root = root;
            _usedNames = usedNames;
        }

        /// <summary>空表达式：恒为 <c>FALSE</c>。</summary>
        public static CompiledExpression Empty { get; } =
            new(string.Empty, null, NoNames);

        /// <summary>原文。</summary>
        public string Source { get; }

        /// <summary>空表达式（没填）—— 求值恒为假，不算错误。</summary>
        public bool IsEmpty => _root == null;

        /// <summary>用到的变量名及位置（同名只出现一次）。</summary>
        public IReadOnlyList<(string Name, int Position)> UsedNames => _usedNames;

        /// <summary>
        /// 语法分析。空串合法（视为空表达式）。
        /// </summary>
        /// <param name="text">表达式原文</param>
        /// <param name="expression">成功时的结果</param>
        /// <param name="error">失败原因，已经带「第 N 个字符」前缀</param>
        public static bool TryParse(
            string? text, out CompiledExpression? expression, out string error)
        {
            expression = null;
            string source = text ?? string.Empty;

            // ⚠️ 这里**不 Trim**：位置要按用户实际输入的字符数算，
            // 词法分析本来就会跳过空白。Trim 掉的话 "  abc" 会报成「第 1 个字符」，
            // 用户在输入框里数到的是第 3 个，对不上。
            if (source.Trim().Length == 0)
            {
                expression = Empty;
                error = string.Empty;
                return true;
            }

            if (!ExpressionLexer.TryTokenize(source, out List<ExpressionToken> tokens, out error))
            {
                return false;
            }

            ExpressionNode? root = new ExpressionParser(tokens).Parse(out error);
            if (root == null)
            {
                return false;
            }

            // 常见笔误：整条就是一个数字（比如把 BOOL 表达式写成了 "1"）。
            // 这种在求值期才发现的话要等到下一拍，不如现在就说清楚。
            if (root is LiteralNode { IsNumber: true })
            {
                error = "表达式的结果必须是 BOOL，但这里是一个数字";
                return false;
            }

            var names = new List<(string Name, int Position)>();
            root.CollectNames(names);
            names = Deduplicate(names);

            expression = new CompiledExpression(source, root, names);
            return true;
        }

        /// <summary>
        /// 校验表达式里用到的变量**都存在**。
        /// <para>同名变量只报一次，且报第一次出现的位置。</para>
        /// </summary>
        /// <param name="nameError">
        /// 名字校验器：合法返回 <c>null</c>，否则返回给用户看的原因（如「是数组，只能对元素读写」）。
        /// <para>⚠️ 用返回原因而不是 <c>bool</c>：数组的下标越界、整条数组被引用、
        /// 变量不存在，这三种的改法完全不同，笼统报「不存在」会把人引向错误方向。</para>
        /// </param>
        public bool TryValidateNames(Func<string, string?> nameError, out string error)
        {
            foreach ((string name, int position) in _usedNames)
            {
                string? reason = nameError(name);
                if (!string.IsNullOrEmpty(reason))
                {
                    error = $"第 {position} 个字符：{reason}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 兼容旧调用点：只判存在性，报「变量不存在」。
        /// <para>⚠️ **不能用于数组**：它不知道变量是不是数组，整条数组引用会被放行。
        /// 新代码一律用 <see cref="TryValidateNames(Func{string,string?},out string)"/>。</para>
        /// </summary>
        [Obsolete("只判存在性，认不出数组（var1 会被放行）。业务侧请用 TryValidateNames(Func<string,string?>)")]
        public bool TryValidateNames(Func<string, bool> exists, out string error) =>
            TryValidateNames(
                name => exists(name) ? null : $"变量“{name}”不存在",
                out error);

        /// <summary>
        /// 求值。
        /// <para>空表达式直接返回 <c>FALSE</c>，不碰 <paramref name="variables"/>。</para>
        /// </summary>
        public bool TryEvaluate(
            IExpressionVariables variables, out bool value, out string error)
        {
            value = false;

            if (_root == null)
            {
                error = string.Empty;
                return true;
            }

            try
            {
                ExpressionValue result = _root.Evaluate(variables);
                if (!result.IsBool)
                {
                    error = $"表达式的结果必须是 BOOL，当前算出来是 {result.Display}";
                    return false;
                }

                value = result.AsBool;
                error = string.Empty;
                return true;
            }
            catch (ExpressionEvaluationException ex)
            {
                error = ex.Position > 0
                    ? $"第 {ex.Position} 个字符：{ex.Message}"
                    : ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 校验表达式的**结果类型是 BOOL**（2026-11-11）。
        /// <para>
        /// 补的是一条静默失效的路：只看语法与名字时，把 <c>INT</c> 变量单独当条件（<c>var</c>）
        /// 是能存进 json 的，可运行期求值必然失败 —— 状态框永远不亮、触发式任务永远不触发，
        /// 而错误被吞掉，界面上一个字都没有。这里按**变量声明的类型**静态推一遍，保存时就能说清。
        /// </para>
        /// <para>
        /// ⚠️ 顺序必须在 <see cref="TryValidateNames(Func{string,string?},out string)"/> **之后**：
        /// 变量不存在时类型也推不出来，先报「不存在」才指向正确方向。
        /// </para>
        /// <para>
        /// ⚠️ 推不出来的（<see cref="ExpressionValueKind.Unknown"/>）**一律放行**，不越权。
        /// </para>
        /// </summary>
        /// <param name="typeOf">名字 → 静态类型；不认识的返回 <see cref="ExpressionVariableType.UnknownType"/></param>
        /// <param name="error">失败原因，已带「第 N 个字符」前缀</param>
        public bool TryValidateResultType(
            Func<string, ExpressionVariableType> typeOf, out string error)
        {
            // 空表达式恒为 FALSE，天然是 BOOL
            if (_root == null)
            {
                error = string.Empty;
                return true;
            }

            ExpressionValueKind kind = _root.InferKind(typeOf, out error);
            if (error.Length > 0)
            {
                return false;
            }

            switch (kind)
            {
                case ExpressionValueKind.Bool:
                case ExpressionValueKind.Unknown:
                    error = string.Empty;
                    return true;

                default:
                    // 2026-11-11 用户定案：一句话即可，不再拼变量名 / 类型 / 改法示例。
                    error = NotBoolean;
                    return false;
            }
        }

        /// <summary>
        /// 「结果不是 BOOL」的统一文案（2026-11-11 用户定案）。
        /// <para>
        /// ⚠️ 原文案会拼成「表达式的结果必须是 BOOL，但「SysState」是 INT 变量
        /// （可写成「SysState != 0」或「SysState == 1」）」，在对话框里折成两行、盖住按钮区。
        /// 用户要求直接一句「表达式不合法」—— **不要**再往里加变量名、类型或位置，
        /// 那三样都是「已经知道是哪个框错了」之后才需要的细节，而红字出现在正是那个框旁边。
        /// </para>
        /// </summary>
        public const string NotBoolean = "表达式不合法";

        private static List<(string Name, int Position)> Deduplicate(
            List<(string Name, int Position)> names)
        {
            if (names.Count <= 1)
            {
                return names;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<(string Name, int Position)>(names.Count);
            foreach ((string name, int position) in names)
            {
                if (seen.Add(name))
                {
                    unique.Add((name, position));
                }
            }

            return unique;
        }
    }
}
