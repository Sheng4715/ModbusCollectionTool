using System;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 变量引用的**写法**解析：`var1` 还是 `var1[0]`（2026-11-10）。
    /// <para>
    /// 与 <see cref="VariableReference"/> 分开的理由：这里只认字符串形状，**零依赖**，
    /// 改名（<see cref="VariableRenamer"/>）与表达式断言工程都要用；
    /// 而「变量存不存在 / 下标越不越界」要查变量表，放另一个类。
    /// </para>
    /// <para><c>变量名[下标]</c>，下标 0 基；变量名规则不允许 <c>[</c>，两者永不歧义。</para>
    /// </summary>
    public static class VariableName
    {
        /// <summary>
        /// 拆出「基础变量名」与「元素下标」。
        /// <para>
        /// 不含方括号的文本一律视为整条变量（<c>elementIndex = null</c>）；
        /// 出现方括号就**必须**是合法写法，否则返回 <c>false</c> 并给出原因 ——
        /// 不能静默退回「整条变量」，否则 <c>var1[abc]</c> 会被当成一个叫
        /// <c>var1[abc]</c> 的变量去查，报「不存在」而看不出是写法错了。
        /// </para>
        /// </summary>
        /// <param name="text">用户填的引用文本（允许前后空白）</param>
        /// <param name="baseName">基础变量名（已去空白）；写法无效时为空串</param>
        /// <param name="elementIndex">元素下标（0 基）；整条变量时为 <c>null</c></param>
        /// <param name="error">写法无效的原因；合法时为空串</param>
        /// <returns>写法是否合法（注意：合法不等于变量存在或下标在范围内）</returns>
        public static bool TrySplit(
            string? text, out string baseName, out int? elementIndex, out string error)
        {
            baseName = string.Empty;
            elementIndex = null;
            error = string.Empty;

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            int open = trimmed.IndexOf('[');
            int close = trimmed.IndexOf(']');

            if (open < 0 && close < 0)
            {
                baseName = trimmed;
                return true;
            }

            // 有方括号就必须是 `名字[数字]` 这一种形状。
            // ⚠️ 变量名与 `[` 之间**不允许空白**（2026-11-11）：表达式词法器要求
            // `[` 紧跟标识符，若这里放行 `var1 [0]`，同一份配置会在表达式框里报错、
            // 在变量框里却能存 —— 两个入口对同一种写法给不同结论最难解释。
            if (open <= 0 || close != trimmed.Length - 1 || close <= open + 1)
            {
                error = $"「{trimmed}」不是有效的数组元素写法（应为 变量名[下标]，如 var1[0]）";
                return false;
            }

            string namePart = trimmed.Substring(0, open);
            if (namePart.Length != namePart.TrimEnd().Length)
            {
                error = $"「{trimmed}」的元素下标必须紧跟在变量名后面（中间不能有空格）";
                return false;
            }

            string body = trimmed.Substring(open + 1, close - open - 1);
            if (body.Length == 0)
            {
                error = $"「{trimmed}」缺少元素下标";
                return false;
            }

            int index = 0;
            foreach (char c in body)
            {
                if (c < '0' || c > '9')
                {
                    error = $"「{trimmed}」的元素下标只能是非负整数";
                    return false;
                }

                // 先挡超长：int.Parse 溢出会抛，而这里只是用户打字
                if (index > (int.MaxValue - 9) / 10)
                {
                    error = $"「{trimmed}」的元素下标过大";
                    return false;
                }

                index = (index * 10) + (c - '0');
            }

            baseName = trimmed.Substring(0, open).Trim();
            if (baseName.Length == 0)
            {
                error = $"「{trimmed}」缺少变量名";
                return false;
            }

            elementIndex = index;
            return true;
        }
    }
}
