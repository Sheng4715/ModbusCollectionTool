using System;
using System.Collections.Generic;
using System.Linq;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一条补全候选（P7.1）。
    /// <para>纯数据，不含任何 WPF 类型 —— 候选怎么算与怎么画是两件事，分开才好离线断言。</para>
    /// </summary>
    public sealed class CompletionCandidate
    {
        /// <summary>插入文本框的内容（变量名）。</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>
        /// 数据类型显示名，如 <c>INT</c> / <c>STRING[20]</c> / <c>INT[5]</c>（数组）。
        /// <para>候选行上显示的那一栏。系统变量与全局变量都有；常量示例为空。</para>
        /// </summary>
        public string TypeName { get; init; } = string.Empty;

        /// <summary>
        /// 用户写的**注释**（全局变量的「注释」列 / 系统变量写死的说明）。
        /// <para>候选行上显示在类型后面；没有就留空、不留分隔符。</para>
        /// </summary>
        public string Comment { get; init; } = string.Empty;

        /// <summary>
        /// 旧的一栏合并说明（类型 + 注释，或数组下标提示）。
        /// <para>
        /// ⚠️ **不上屏**（2026-11-11）：候选行改用 <see cref="TypeName"/> +
        /// <see cref="Comment"/> 两栏分别显示 —— 合并成一串时「类型 · 数组请填元素下标 · 注释」
        /// 太长，且数组那句提示在自由监控那个框里是错的（那里要整条数组名）。
        /// 保留它是为了让 <c>ModelingCheck</c> 的既有断言继续有效。
        /// </para>
        /// </summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>来源分类（"系统变量" / "全局变量" / "常量"），用于列表右侧的分组小字。</summary>
        public string Kind { get; init; } = string.Empty;
    }

    /// <summary>
    /// 变量名补全的**纯逻辑**部分（P7.1）：找出光标处的词 → 在候选表里筛。
    /// <para>
    /// 刻意不碰 WPF：候选算法可以链接源码到独立工程里断言（与 <c>ValueCodec</c> 同一做法），
    /// 不用为了测「打 <c>pu</c> 能不能找到 <c>sys_Pulse1s</c>」去启动一个窗口。
    /// </para>
    /// <para>
    /// ⚠️ 标识符规则与 <c>ExpressionLexer</c> 保持一致（ASCII 字母或下划线开头，后接字母/数字/下划线）。
    /// 两处各写一套迟早会漂移 —— 补全给出 <c>abc</c>、表达式却不认，是最难解释的一种不一致。
    /// </para>
    /// </summary>
    public static class CompletionSource
    {
        /// <summary>一次最多显示多少条：候选再多，列表滚起来也没人看。</summary>
        public const int MaxCandidates = 50;

        /// <summary>标识符首字符：ASCII 字母或下划线（与 <c>ExpressionLexer</c> 一致）。</summary>
        public static bool IsIdentifierStart(char c) =>
            c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        /// <summary>标识符后续字符：首字符 + 数字。</summary>
        public static bool IsIdentifierPart(char c) =>
            IsIdentifierStart(c) || (c >= '0' && c <= '9');

        /// <summary>
        /// 取光标左侧正在输入的那个词。
        /// <para>
        /// 返回 <c>false</c> 的三种情形 —— 这三种都**不该弹补全**：
        /// 光标在最前、左边是空格/运算符（没有词）、或这个词以数字开头
        /// （<c>123</c> 是数字字面量，不是变量名；否则在表达式里敲数字会弹出一堆变量）。
        /// </para>
        /// </summary>
        /// <param name="start">词的起始下标（从 0 算）。</param>
        /// <param name="word">词本身（不含光标右侧的内容）。</param>
        public static bool TryGetWord(string? text, int caret, out int start, out string word)
        {
            start = 0;
            word = string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (caret < 0)
            {
                caret = 0;
            }
            else if (caret > text.Length)
            {
                caret = text.Length;
            }

            int i = caret;
            while (i > 0 && IsIdentifierPart(text[i - 1]))
            {
                i--;
            }

            // 词以数字开头（或整段都是数字）：这是数字字面量，不是变量
            if (i == caret || !IsIdentifierStart(text[i]))
            {
                return false;
            }

            start = i;
            word = text.Substring(i, caret - i);
            return true;
        }

        /// <summary>
        /// 按词筛选候选。**前缀命中排在前，子串命中排在后** ——
        /// 敲 <c>sys_</c> 时想要的几乎总是 <c>sys_*</c>，而不是「名字中间恰好含 sys_」的那些。
        /// <para>词为空返回全部（供 Ctrl+Space 手动唤出）；大小写不敏感匹配，但**插入原样大小写**。</para>
        /// </summary>
        public static List<CompletionCandidate> Filter(IReadOnlyList<CompletionCandidate>? all, string? word)
        {
            var result = new List<CompletionCandidate>();
            if (all == null || all.Count == 0)
            {
                return result;
            }

            string w = word ?? string.Empty;

            if (w.Length == 0)
            {
                result.AddRange(all.Take(MaxCandidates));
                return result;
            }

            var scored = new List<(CompletionCandidate Item, int Rank, int Length)>();
            foreach (CompletionCandidate c in all)
            {
                int idx = c.Text.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                // 0 = 前缀命中，1 = 词中间命中
                scored.Add((c, idx == 0 ? 0 : 1, c.Text.Length));
            }

            // 前缀优先 → 名字短的优先（越短越可能是想敲的那个）→ 字母序兜底，保证结果稳定
            foreach (var s in scored
                         .OrderBy(s => s.Rank)
                         .ThenBy(s => s.Length)
                         .ThenBy(s => s.Item.Text, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxCandidates))
            {
                result.Add(s.Item);
            }

            return result;
        }

        /// <summary>
        /// 表达式框的候选：**系统变量 + 全局变量**。
        /// <para>与 <c>MainWindow.ExpressionNameExists</c> 的口径一致 —— 那里认什么，这里就该联想什么。</para>
        /// </summary>
        public static List<CompletionCandidate> ForExpression(
            IEnumerable<GlobalVariable>? globals,
            CompletionFilter filter = CompletionFilter.Any)
        {
            var list = new List<CompletionCandidate>();

            foreach (SystemVariable v in SystemVariable.All)
            {
                list.Add(new CompletionCandidate
                {
                    Text = v.Name,
                    TypeName = DataTypes.Get(v.Type).DisplayName,
                    Comment = v.Comment,
                    Detail = string.IsNullOrEmpty(v.Comment) ? v.Type.ToString() : v.Comment,
                    Kind = "系统变量",
                });
            }

            AppendGlobals(list, globals, filter);
            return list;
        }

        /// <summary>
        /// 采集数据「来源变量」框的候选：**系统变量 + 全局变量**。
        /// <para>
        /// 与 <see cref="ForExpression"/> 同一口径，而不是 <see cref="ForMonitorName"/>：
        /// 来源变量只是「值从哪来」，不落 PLC 地址，系统变量（<c>sys_True</c>、
        /// <c>sys_DateTime_Second</c>…）完全可用（用户定案，2026-10-26）。
        /// </para>
        /// <para>
        /// ⚠️ **写入变量**那一列不能共用这个 —— 它要映射到 PLC 地址，
        /// 系统变量没有地址，混进去只会让人填了才发现写不出去。
        /// </para>
        /// <para>
        /// 另附几条**常量写法示例**（2026-11-05 方案 B）：常量本身不必联想，
        /// 但用户未必知道有这回事，列在末尾当语法提示用。它们插入后仍需改成自己的值。
        /// </para>
        /// </summary>
        public static List<CompletionCandidate> ForSourceVariable(IEnumerable<GlobalVariable>? globals)
        {
            var list = ForExpression(globals);

            // 放在最后：候选按前缀命中优先排序，常量示例从不抢变量名的位置。
            // ⚠️ 常量**没有声明类型**（类型由写入变量定），故 TypeName 留空、写进口 Comment 栏。
            list.Add(new CompletionCandidate
            {
                Text = "#123",
                Comment = "十进制整数",
                Detail = "常量 · 十进制整数",
                Kind = "常量",
            });
            list.Add(new CompletionCandidate
            {
                Text = "#0x1F",
                Comment = "十六进制",
                Detail = "常量 · 十六进制",
                Kind = "常量",
            });
            list.Add(new CompletionCandidate
            {
                Text = "#\"ABCD\"",
                Comment = "字符串（双引号必填）",
                Detail = "常量 · 字符串（双引号必填）",
                Kind = "常量",
            });
            return list;
        }

        /// <summary>候选过滤：只保留**能装进该字段**的变量（2026-11-11）。</summary>
        /// <remarks>
        /// 原先所有全局变量框共用一份候选，于是「接收内容输出变量」（只收整条 STRING）、
        /// 「执行完成输出变量」（只收整数）也会联想出 BOOL / REAL / 数组元素 ——
        /// 选了才报错，等于让用户先做一遍注定要改的选择。
        /// </remarks>
        public enum CompletionFilter
        {
            /// <summary>什么都可以（表达式 / 来源变量框）。</summary>
            Any,

            /// <summary>只要**整条 STRING**（扫码器接收内容输出变量）。</summary>
            StringScalarOnly,

            /// <summary>
            /// 只要能装下 1 / 2 的**整数**：整条整数变量或整数元素
            /// （执行完成输出变量、扫码器执行结果输出变量）。
            /// </summary>
            IntegerScalarOrElement,

            /// <summary>
            /// 只要**全局变量**、数组给**元素**写法 <c>var1[0]</c>
            /// （采集读取 / 写入变量、表达式框、来源变量框）。
            /// </summary>
            AnyGlobal,

            /// <summary>
            /// 只要**全局变量**、数组给**整条名字** <c>var1</c>（自由监控「元件或变量名称」）。
            /// <para>
            /// ⚠️ 这个档位是补一个**既有不一致**（2026-11-11）：自由监控的数组是
            /// 「占一行、展开成 N 个元素子行」，那一栏**必须填整条数组名** ——
            /// <c>MonitorItem.SetName</c> 拿文本去查变量表（整名匹配），填 <c>var1[0]</c>
            /// 查不到、退回元件解析，直接报「不是有效的软元件」。
            /// 而采集 / 扫码器的框反过来只收元素。同一个 <c>ForMonitorName</c>
            /// 原先两边共用一份候选，等于让自由监控的补全**给出一个填了必然报错的写法**。
            /// </para>
            /// </summary>
            WholeArrayGlobal,
        }

        /// <summary>
        /// 自由监控「元件名称」框的候选：**只有全局变量**。
        /// <para>
        /// 系统变量不进这里：它们是程序算出来的只读值，不映射任何 PLC 地址，
        /// 写进监控行也读不出东西（<c>MonitorItem.SetName</c> 走的是变量表 + 元件地址两条路）。
        /// </para>
        /// <para>⚠️ 按用户要求（2026-10-21）**不做元件地址联想**（不补 <c>HD100</c> 这类），只补变量名。</para>
        /// </summary>
        public static List<CompletionCandidate> ForMonitorName(
            IEnumerable<GlobalVariable>? globals, CompletionFilter filter = CompletionFilter.AnyGlobal)
        {
            var list = new List<CompletionCandidate>();
            AppendGlobals(list, globals, filter);
            return list;
        }

        /// <summary>按过滤器给出一条变量该进候选的形式；<c>null</c> = 这个字段用不上它。</summary>
        private static CompletionCandidate? BuildCandidate(GlobalVariable v, CompletionFilter filter)
        {
            bool isArray = v.Type == DataType.Array;
            DataType valueType = isArray ? v.ElementType : v.Type;

            switch (filter)
            {
                case CompletionFilter.StringScalarOnly:
                    // 接收内容输出变量只能是整条 STRING；数组元素不可能是 STRING（ArrayRules 禁止）
                    return v.Type == DataType.String
                        ? new CompletionCandidate
                        {
                            Text = v.Name,
                            TypeName = v.TypeDisplayName,
                            Comment = v.Comment,
                            Detail = BuildGlobalDetail(v),
                            Kind = "全局变量",
                        }
                        : null;

                case CompletionFilter.IntegerScalarOrElement:
                    if (!AcquisitionTaskRules.IsCompletionVariableType(valueType))
                    {
                        return null;
                    }

                    break;
            }

            // 数组分两种写法，取决于**这个框要整条还是元素**（2026-11-11）：
            //   · 自由监控「元件或变量名称」→ 整条 `var1`（那一栏按整名查变量表，
            //     数组是「占一行、展开成 N 个元素子行」）；
            //   · 采集 / 扫码器 / 表达式 → 元素 `var1[0]`（用户定案 2026-11-10：
            //     只能对元素读写，整条数组在那些框里一律被拒）。
            if (isArray)
            {
                if (filter == CompletionFilter.WholeArrayGlobal)
                {
                    return new CompletionCandidate
                    {
                        Text = v.Name,
                        TypeName = v.TypeDisplayName,
                        Comment = v.Comment,
                        Detail = BuildGlobalDetail(v),
                        Kind = "全局变量",
                    };
                }

                return new CompletionCandidate
                {
                    Text = v.Name + "[0]",
                    TypeName = v.TypeDisplayName,
                    Comment = v.Comment,
                    Detail = v.TypeDisplayName + "  ·  数组，请填元素下标（0 ~ "
                        + Math.Max(0, v.Length - 1) + "）",
                    Kind = "全局变量",
                };
            }

            return new CompletionCandidate
            {
                Text = v.Name,
                TypeName = v.TypeDisplayName,
                Comment = v.Comment,
                Detail = BuildGlobalDetail(v),
                Kind = "全局变量",
            };
        }

        private static void AppendGlobals(
            List<CompletionCandidate> list,
            IEnumerable<GlobalVariable>? globals,
            CompletionFilter filter = CompletionFilter.AnyGlobal)
        {
            if (globals == null)
            {
                return;
            }

            foreach (GlobalVariable v in globals)
            {
                CompletionCandidate? candidate = BuildCandidate(v, filter);
                if (candidate != null)
                {
                    list.Add(candidate);
                }
            }
        }

        private static string BuildGlobalDetail(GlobalVariable v)
        {
            // 类型是必有的；注释有就附上，没有就只显示类型（不留一个孤零零的分隔符）
            string type = v.TypeDisplayName;
            return string.IsNullOrWhiteSpace(v.Comment)
                ? type
                : type + "  ·  " + v.Comment;
        }
    }
}
