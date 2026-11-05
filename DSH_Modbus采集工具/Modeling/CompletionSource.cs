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

        /// <summary>右侧说明：类型 / 注释，只给人看，不参与插入。</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>来源分类（"系统变量" / "全局变量"），用于列表里的分组小字。</summary>
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
        public static List<CompletionCandidate> ForExpression(IEnumerable<GlobalVariable>? globals)
        {
            var list = new List<CompletionCandidate>();

            foreach (SystemVariable v in SystemVariable.All)
            {
                list.Add(new CompletionCandidate
                {
                    Text = v.Name,
                    Detail = string.IsNullOrEmpty(v.Comment) ? v.Type.ToString() : v.Comment,
                    Kind = "系统变量",
                });
            }

            AppendGlobals(list, globals);
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

            // 放在最后：候选按前缀命中优先排序，常量示例从不抢变量名的位置
            list.Add(new CompletionCandidate
            {
                Text = "#123",
                Detail = "常量 · 十进制整数",
                Kind = "常量",
            });
            list.Add(new CompletionCandidate
            {
                Text = "#0x1F",
                Detail = "常量 · 十六进制",
                Kind = "常量",
            });
            list.Add(new CompletionCandidate
            {
                Text = "#\"ABCD\"",
                Detail = "常量 · 字符串（双引号必填）",
                Kind = "常量",
            });

            return list;
        }

        /// <summary>
        /// 自由监控「元件名称」框的候选：**只有全局变量**。
        /// <para>
        /// 系统变量不进这里：它们是程序算出来的只读值，不映射任何 PLC 地址，
        /// 写进监控行也读不出东西（<c>MonitorItem.SetName</c> 走的是变量表 + 元件地址两条路）。
        /// </para>
        /// <para>⚠️ 按用户要求（2026-10-21）**不做元件地址联想**（不补 <c>HD100</c> 这类），只补变量名。</para>
        /// </summary>
        public static List<CompletionCandidate> ForMonitorName(IEnumerable<GlobalVariable>? globals)
        {
            var list = new List<CompletionCandidate>();
            AppendGlobals(list, globals);
            return list;
        }

        private static void AppendGlobals(List<CompletionCandidate> list, IEnumerable<GlobalVariable>? globals)
        {
            if (globals == null)
            {
                return;
            }

            foreach (GlobalVariable v in globals)
            {
                list.Add(new CompletionCandidate
                {
                    Text = v.Name,
                    Detail = BuildGlobalDetail(v),
                    Kind = "全局变量",
                });
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
