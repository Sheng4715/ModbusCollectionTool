using System;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 变量引用解析：区分**整条变量**与**数组元素**（<c>var1[0]</c>，2026-11-10）。
    /// <para>
    /// 背景：数组占一段连续单元，用户明确「只能对元素读写，不可以读写整个数组」。
    /// 但采集任务组的数据项输入框、来源变量框、写入变量框都是**自由文本**，
    /// 用户自然会写 <c>var1[0]</c> —— 原先按整名查变量表必然查不到，
    /// 输入框全标红，功能其实还没接上。
    /// </para>
    /// <para>
    /// 语法与自由监控的元素子行一致：<c>变量名[下标]</c>，**下标 0 基**；
    /// 变量名规则不允许 <c>[</c>，故两者永不歧义。
    /// </para>
    /// <para>
    /// 纯逻辑、不引 WPF，可链接进断言工程。
    /// </para>
    /// </summary>
    public static class VariableReference
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
            string? text, out string baseName, out int? elementIndex, out string error) =>
            VariableName.TrySplit(text, out baseName, out elementIndex, out error);

        /// <summary>
        /// 描述一个引用**能不能用**：合法返回 <c>null</c>，否则返回给用户看的一句话。
        /// <para>
        /// 顺序：写法 → 系统变量 → 全局变量（含数组元素）。系统变量不是数组，
        /// 写成 <c>sys_True[0]</c> 要单独说清，不能报成「sys_True 不存在」。
        /// </para>
        /// </summary>
        public static string? Describe(
            string? text,
            Func<string, GlobalVariable?>? findVariable,
            bool allowWholeArray = false)
        {
            if (!TrySplit(text, out string baseName, out int? elementIndex, out string splitError))
            {
                return splitError.Length > 0 ? splitError : "变量名为空";
            }

            if (SystemVariable.Find(baseName) != null)
            {
                return elementIndex is int index
                    ? $"系统变量「{baseName}」不是数组，不能写成「{baseName}[{index}]」"
                    : null;
            }

            return TryResolve(text, findVariable, allowWholeArray, out _, out _, out string error)
                ? null
                : error;
        }

        /// <summary>
        /// 解析并校验一个引用：变量必须存在；数组**只能取元素**（<paramref name="allowWholeArray"/>
        /// 为 <c>false</c> 时整条数组算错）；下标必须在 <c>0 ~ Length-1</c>。
        /// </summary>
        /// <param name="text">引用文本</param>
        /// <param name="findVariable">全局变量查表；<c>null</c> 时所有名字都算查不到</param>
        /// <param name="allowWholeArray">
        /// 是否允许「整条数组」这种引用。**业务侧一律传 <c>false</c>**；
        /// 只有为了让提示能说清「是数组、只能取元素」才传 <c>true</c>。
        /// </param>
        /// <param name="variable">解析出的基础变量（数组元素时是数组本身）</param>
        /// <param name="elementIndex">元素下标；整条变量时为 <c>null</c></param>
        /// <param name="error">失败原因（已带变量名，可直接拼在「读取变量」后面）</param>
        public static bool TryResolve(
            string? text,
            Func<string, GlobalVariable?>? findVariable,
            bool allowWholeArray,
            out GlobalVariable? variable,
            out int? elementIndex,
            out string error)
        {
            variable = null;
            elementIndex = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "变量名为空";
                return false;
            }

            if (!TrySplit(text, out string baseName, out int? index, out error))
            {
                return false;
            }

            GlobalVariable? found = findVariable?.Invoke(baseName);
            if (found == null)
            {
                error = $"「{baseName}」不存在";
                return false;
            }

            // 变量自身的解析 / 声明问题必须先拦住（2026-11-11）：
            // 地址解析失败、数组声明非法（元素类型 STRING/ARRAY、个数越界）时，
            // GlobalReadPlan.Build 会把它过滤掉 —— 界面若仍报「配置完整」，
            // 用户会以为在采，实际一个报文都没发。
            if (found.ResolveError.Length > 0)
            {
                error = $"「{baseName}」配置有误：{found.ResolveError}";
                return false;
            }

            if (found.Type == DataType.Array
                && !ArrayRules.TryValidate(found.ElementType, found.Length, out string arrayError))
            {
                error = $"「{baseName}」数组声明无效：{arrayError}";
                return false;
            }

            if (index is int i)
            {
                if (found.Type != DataType.Array)
                {
                    error = $"「{baseName}」不是数组，不能写成「{baseName}[{i}]」";
                    return false;
                }

                if (i < 0 || i >= found.Length)
                {
                    error = found.Length <= 0
                        ? $"「{baseName}」的元素个数为 0，不能取元素"
                        : $"「{baseName}[{i}]」元素下标超出范围（0 ~ {found.Length - 1}）";
                    return false;
                }

                variable = found;
                elementIndex = i;
                return true;
            }

            if (found.Type == DataType.Array && !allowWholeArray)
            {
                // 用户定案 2026-11-10：数组只能对元素读写，不能整组读写
                error = $"「{baseName}」是数组，只能对元素读写（如 {baseName}[0]）";
                return false;
            }

            variable = found;
            return true;
        }

        /// <summary>业务侧常用的默认重载：**不允许整条数组**。</summary>
        public static bool TryResolve(
            string? text,
            Func<string, GlobalVariable?>? findVariable,
            out GlobalVariable? variable,
            out int? elementIndex,
            out string error) =>
            TryResolve(text, findVariable, allowWholeArray: false, out variable, out elementIndex, out error);

        /// <summary>
        /// 读取一个引用当前的值（显示文本）。
        /// <para>
        /// 整条标量取 <see cref="GlobalVariable.Value"/>；数组元素取
        /// <see cref="GlobalVariable.GetElementValue"/>。引用无效或整条数组返回 <c>null</c> ——
        /// 调用方（CSV、实时卡片）据此显示空 / 占位符，**不要**拿空串冒充「值是空」。
        /// </para>
        /// </summary>
        public static string? ReadValue(
            string? text, Func<string, GlobalVariable?>? findVariable)
        {
            if (!TryResolve(text, findVariable, allowWholeArray: false,
                    out GlobalVariable? variable, out int? elementIndex, out _))
            {
                return null;
            }

            return elementIndex is int i
                ? variable!.GetElementValue(i)
                : variable!.Value;
        }
    }
}
