using System;
using System.Collections.Generic;
using System.Globalization;
using DSH_Modbus采集工具.Expressions;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 全局变量改名后，把**引用它的地方**一起改名（2026-11-09 用户要求）。
    /// <para>
    /// 需求原文：「我想让全局变量名称变更后，相关引用也自动变更名称」。
    /// 变量的名字散落在六个持久化文件里：<c>data-points.json</c>（自由监控变量行）、
    /// <c>acquisition-tasks.json</c>（读取 / 写入 / 来源变量 + 触发表达式）、
    /// <c>machine-state.json</c>（状态框与指示灯表达式）、<c>scanner.json</c>
    /// （缓冲区 / 结果变量 + 触发表达式）。不改的后果不是「少写一点」，而是
    /// **改完立刻变配置错误**：表达式里的标识符找不到变量，采集组与扫码器整组停摆。
    /// </para>
    /// <para>
    /// ⚠️ **表达式必须按「标识符」替换，不能按子串替换**：把 <c>speed</c> 改成 <c>rate</c> 时，
    /// <c>speed2</c> / <c>my_speed</c> 里的 <c>speed</c> 一个字符都不许动；反过来，
    /// 用 <c>string.Replace</c> 还会把字符串里碰巧出现的同名片段一起改掉。
    /// 故这里复用表达式引擎自己的词法器 —— 它认得什么是标识符、什么是关键字与数字。
    /// </para>
    /// <para>
    /// 纯逻辑、不引 WPF，可链接进断言工程。
    /// </para>
    /// </summary>
    public static class VariableRenamer
    {
        /// <summary>
        /// 改写一个**纯变量名字段**（自由监控的变量行、采集的读取 / 写入 / 来源变量、
        /// 扫码器的缓冲区 / 结果变量）。
        /// <para>
        /// ⚠️ 精确比对（<see cref="StringComparison.Ordinal"/>）：变量名区分大小写，
        /// <c>speed</c> 与 <c>Speed</c> 是两条不同的变量，改名不能顺手把另一条也带走。
        /// </para>
        /// </summary>
        /// <param name="reference">字段当前值；null / 空串原样返回</param>
        /// <returns>改名后的值；不是这个变量时返回原值（引用相等时不产生新对象）</returns>
        public static string RenameReference(string? reference, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(reference)
                || string.IsNullOrEmpty(oldName)
                || string.IsNullOrEmpty(newName))
            {
                return reference ?? string.Empty;
            }

            // 数组元素引用 `var1[0]`：只换**基础变量名**，下标原样保留（2026-11-10）。
            // 否则改名后会把 `var1[0]` 当成一个整体去比，永远匹配不上。
            if (VariableName.TrySplit(reference, out string baseName, out int? index, out _))
            {
                if (!string.Equals(baseName, oldName, StringComparison.Ordinal))
                {
                    return reference;
                }

                return index is int i
                    ? newName + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"
                    : newName;
            }

            return string.Equals(reference, oldName, StringComparison.Ordinal) ? newName : reference;
        }

        /// <summary>
        /// 改写表达式文本里对该变量的引用（状态机 / 采集触发 / 扫码器触发三处共用）。
        /// </summary>
        /// <param name="expression">表达式原文</param>
        /// <param name="oldName">改之前的变量名</param>
        /// <param name="newName">改之后的变量名</param>
        /// <param name="result">改写后的文本；没有改动时等于原文</param>
        /// <returns>
        /// **是否真的改了**（调用方据它决定要不要重编译表达式、要不要落盘）。
        /// <para>
        /// ⚠️ 表达式本身有语法错误时返回 <c>false</c> 且原样返回：那种表达式当前
        /// 根本跑不起来，硬改只会把「用户的笔误」和「我们的替换」混成一处，
        /// 让他看不出原本错在哪。
        /// </para>
        /// </returns>
        public static bool TryRenameExpression(
            string? expression, string oldName, string newName, out string result)
        {
            result = expression ?? string.Empty;

            if (string.IsNullOrEmpty(expression)
                || string.IsNullOrEmpty(oldName)
                || string.IsNullOrEmpty(newName)
                || string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!ExpressionLexer.TryTokenize(expression, out List<ExpressionToken> tokens, out _))
            {
                return false;
            }

            // 从后往前替换：前面的位置不受后面长度变化影响，不必记偏移补偿
            var edits = new List<(ExpressionToken Token, string Replacement)>();
            foreach (ExpressionToken token in tokens)
            {
                if (token.Kind != ExpressionTokenKind.Identifier)
                {
                    continue;
                }

                // 数组元素引用只换基础变量名，`[下标]` 原样保留
                string baseName = token.Text;
                string suffix = string.Empty;

                if (VariableName.TrySplit(token.Text, out string parsed, out int? index, out _))
                {
                    baseName = parsed;
                    if (index is int i)
                    {
                        suffix = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    }
                }

                if (string.Equals(baseName, oldName, StringComparison.Ordinal))
                {
                    edits.Add((token, newName + suffix));
                }
            }

            if (edits.Count == 0)
            {
                return false;
            }

            string text = expression;
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                ExpressionToken token = edits[i].Token;
                int start = token.Position - 1;   // 词法器给的是 1 基位置

                text = text.Substring(0, start) + edits[i].Replacement
                    + text.Substring(start + token.Text.Length);
            }

            result = text;
            return true;
        }
    }
}
