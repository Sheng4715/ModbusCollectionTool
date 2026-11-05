using System;
using System.Collections.Generic;
using DSH_Modbus采集工具.Expressions;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 任务组的**配置完整性检查**（P3.1，用户定案 2026-10-27）。
    /// <para>
    /// 起因：一个任务组里可以躺着一堆什么都没填的数据项，而它照样每 5 秒「执行完成」一次 ——
    /// 日志说完成了，实际一个字节都没采；CSV 多出一列永远空白，下游分不清「没配」与「采不到」。
    /// 与其让它静默空跑，不如在界面上直说「配置错误」，并**整组拦下**：
    /// 不采集、不存文件、不计日志。
    /// </para>
    /// <para>
    /// 纯逻辑、不引用 WPF：界面拿它出开关右侧的琥珀色「配置错误」，并据此把启用开关关掉且锁住，
    /// 离线断言也能直接链进来 —— **判定只有一处**，界面说「错」而执行照跑这种分裂不可能发生。
    /// </para>
    /// <para>
    /// 执行侧**不再单独判它**：配置错误时「启用开关」本身就是关的（
    /// <c>AcquisitionTaskItem.EffectiveEnabled</c>），走的是「停用的组不执行」那条既有路径。
    /// 少一个判定点，也少一处可能忘改的地方。
    /// </para>
    /// </summary>
    public static class AcquisitionTaskCheck
    {
        /// <summary>任务组行上「启用」开关右侧的提示文字（琥珀色）。</summary>
        public const string ErrorText = "配置错误";

        /// <summary>
        /// 列出该任务组当前的配置问题；**空列表 = 配置完整，可以执行**。
        /// <para>
        /// 只查「跑不跑得起来」这一层：缺变量、变量查不到、读写类型对不上。
        /// ⚠️ 写入变量**未映射地址不算错**（用户定案允许先建纯内存变量，地址以后再补）。
        /// </para>
        /// </summary>
        /// <param name="task">待检查的任务组；<c>null</c> 视为配置错误。</param>
        /// <param name="findVariable">全局变量查表；<c>null</c> 时所有变量都算查不到。</param>
        public static List<string> Describe(
            AcquisitionTask? task,
            Func<string, GlobalVariable?>? findVariable)
        {
            var issues = new List<string>();

            if (task == null)
            {
                issues.Add("任务组不存在");
                return issues;
            }

            if (task.Items == null || task.Items.Count == 0)
            {
                // 一个项都没有的组，跑起来连表头都只有「时间,任务类型」——
                // 这不是「采到了空」，是「没什么可采」。
                issues.Add("没有数据项");
                return issues;
            }

            // 组级问题先报：表达式坏了的组，逐项查完也跑不起来
            DescribeTrigger(task, findVariable, issues);

            for (int i = 0; i < task.Items.Count; i++)
            {
                AcquisitionItem item = task.Items[i];
                if (item == null)
                {
                    issues.Add("项" + (i + 1) + "：数据项为空");
                    continue;
                }

                DescribeItem(task.Kind, i + 1, item, findVariable, issues);
            }

            return issues;
        }

        /// <summary>
        /// 触发式任务的**表达式**还能不能用（P3.1 收尾，2026-10-27）。
        /// <para>
        /// 起因：表达式引用的变量被删或改名后，表达式永远求不出 TRUE，任务从此静默不跑 ——
        /// 不采集、不存文件、日志里一个字都没有，界面上也看不出来。与「数据项没填」是同一类坑。
        /// </para>
        /// <para>
        /// 口径与任务选项对话框、PLC 状态机**完全一致**（同一个 <see cref="CompiledExpression"/>）：
        /// 先语法分析，再校验用到的名字都是变量（系统变量或全局变量）。
        /// 周期式任务不写表达式，这一整段跳过。
        /// </para>
        /// </summary>
        private static void DescribeTrigger(
            AcquisitionTask task,
            Func<string, GlobalVariable?>? findVariable,
            List<string> issues)
        {
            if (task.Trigger != AcquisitionTriggerMode.Edge)
            {
                return;
            }

            string source = task.Expression ?? string.Empty;

            if (!CompiledExpression.TryParse(source, out CompiledExpression? compiled, out string error))
            {
                issues.Add("触发表达式无法解析（" + error + "）");
                return;
            }

            if (compiled == null || compiled.IsEmpty)
            {
                // 空表达式恒为假 = 永不触发。对话框允许存空，所以这里必须报
                issues.Add("触发式任务没有填触发表达式");
                return;
            }

            if (!compiled.TryValidateNames(NameExists, out error))
            {
                // error 自带「第 N 个字符」与变量名，直接转达
                issues.Add("触发表达式无效（" + error + "）");
            }

            // 表达式里的名字必须是变量：系统变量或全局变量，口径同任务选项对话框；
            // 刻意不复用「任务名」那套判断（同名不同义，混用会写出「填任务名也算合法」）
            bool NameExists(string name) =>
                SystemVariable.Find(name) != null || findVariable?.Invoke(name) != null;
        }

        private static void DescribeItem(
            AcquisitionTaskKind kind,
            int index,
            AcquisitionItem item,
            Func<string, GlobalVariable?>? findVariable,
            List<string> issues)
        {
            string prefix = DescribeItemPrefix(index, item.Name);
            string variableName = item.VariableName ?? string.Empty;
            string sourceName = item.SourceVariableName ?? string.Empty;

            if (kind == AcquisitionTaskKind.Write)
            {
                // 顺序照着**填写的先后**：来源定类型，写入变量定落的地址。
                // 先报「来源没填」比先报「写入变量类型不对」更接近用户的下一步动作。
                if (sourceName.Trim().Length == 0)
                {
                    issues.Add(prefix + "未填来源变量");
                    return;
                }

                // 常量（# 开头）：类型由写入变量决定，故**不能**在这里就要求类型匹配 ——
                // 先解析出字面量，等拿到写入变量再用它的类型做范围与类型校验。
                bool isConstant = AcquisitionConstant.IsConstant(sourceName);

                AcquisitionSource? source = AcquisitionSource.Resolve(sourceName, findVariable);
                if (source == null)
                {
                    // 常量解析失败要把「为什么」说出来，不能笼统报「不存在」
                    issues.Add(prefix + DescribeSourceUnresolved(sourceName, isConstant));
                    return;
                }

                if (variableName.Trim().Length == 0)
                {
                    issues.Add(prefix + "未填写入变量");
                    return;
                }

                GlobalVariable? variable = findVariable?.Invoke(variableName);
                if (variable == null)
                {
                    issues.Add(prefix + "写入变量「" + variableName + "」不存在");
                    return;
                }

                if (isConstant)
                {
                    // 常量的类型由写入变量定：只校验「这个字面量能不能按它的类型编出来」
                    string? constantError = DescribeConstantProblem(sourceName, variable);
                    if (constantError != null)
                    {
                        issues.Add(prefix + constantError);
                        return;
                    }
                }
                else if (!AcquisitionTaskRules.WriteTypesMatch(source.Type, variable.Type))
                {
                    issues.Add(
                        prefix + "来源「" + source.Name + "」是 " + DataTypes.Get(source.Type).DisplayName
                        + "，写入变量「" + variable.Name + "」是 " + DataTypes.Get(variable.Type).DisplayName
                        + "，两者必须相同");
                    return;
                }

                // 只读区（X 等输入元件）不能当写入目标 —— 用户定案 2026-10-29。
                //
                // 全局变量**允许**映射 X（那条限制已经撤掉），所以「读得到、写不了」这件事
                // 只能在这里报：放进「配置错误」整组拦下，比等到运行期才写失败、日志里
                // 一句「从站回异常码 2」查不出来要好得多。
                if (variable.IsReadOnly)
                {
                    issues.Add(
                        prefix + "写入变量「" + variable.Name + "」映射到 " + variable.Address
                        + "，属于只读区（输入元件），不能作为写入目标");
                }

                return;
            }

            if (variableName.Trim().Length == 0)
            {
                issues.Add(prefix + "未填读取变量");
                return;
            }

            if (findVariable?.Invoke(variableName) == null)
            {
                issues.Add(prefix + "读取变量「" + variableName + "」不存在");
            }
        }

        /// <summary>
        /// 来源解析不出来时给用户看的话。
        /// <para>
        /// ⚠️ 常量与变量名**必须分开措辞**：常量写错时若也报「来源变量「#12x」不存在」，
        /// 用户会去变量表里找一个根本不存在的东西。
        /// </para>
        /// </summary>
        private static string DescribeSourceUnresolved(string sourceName, bool isConstant)
        {
            if (!isConstant)
            {
                return "来源变量「" + sourceName + "」不存在";
            }

            // 进入本函数的前提是常量解析失败（解析成功则 Resolve 已返回非 null，
            // 走不到这里）。TryParse 失败时给出含原因的错误；万一意外成功（调用链改变），
            // 退回到不带原因的通用文案 —— 条件按「解析失败才报错」的语义来，不再倒置。
            return !AcquisitionConstant.TryParse(sourceName, out _, out _, out _, out string error)
                ? "来源常量无效：" + error
                : "来源常量「" + sourceName + "」无法解析";
        }

        /// <summary>
        /// 常量写进给定写入变量时的校验（P3.1 方案 B，2026-11-05）。
        /// <para>
        /// 类型**由写入变量决定**，故这里只问一件事：这个字面量能不能按写入变量的类型编出来？
        /// 直接复用编码层（<see cref="ValueCodec.TryEncodeRegisters"/>），
        /// 与运行时真正下发走同一套判定 —— 界面说「能写」而后台写失败的分裂不可能发生。
        /// </para>
        /// <para>
        /// ⚠️ BOOL 要单独判：它是线圈类型（<c>CoilOnly</c>），不进 <c>TryEncodeRegisters</c>
        /// 那条寄存器路径，只接受 <c>0</c> / <c>1</c>（<c>#TRUE</c> / <c>#FALSE</c> 已在解析时归一）。
        /// </para>
        /// </summary>
        private static string? DescribeConstantProblem(string sourceName, GlobalVariable variable)
        {
            // 进入本函数的前提是来源已成功解析为常量（Resolve 已保证 TryParse 成功），
            // 故这里直接用解析结果，不再重复一道「解析失败」的防御 ——
            // 那条支路在此调用链上永远不会触发，留着只会误导维护者以为它会跑。
            AcquisitionConstant.TryParse(
                sourceName, out string literal, out bool isQuoted, out _, out _);

            DataTypeSpec spec = DataTypes.Get(variable.Type);

            if (spec.CoilOnly)
            {
                // 带引号的字符串常量写进 BOOL 显然不对；数值/TRUE/FALSE 归一后只认 0 / 1
                if (isQuoted)
                {
                    return "字符串常量不能写入 BOOL 变量「" + variable.Name + "」";
                }

                if (literal != "0" && literal != "1")
                {
                    return "BOOL 只能写 0 / 1 / TRUE / FALSE，来源常量给的是「" + literal + "」";
                }

                return null;
            }

            if (isQuoted && variable.Type != DataType.String)
            {
                return "字符串常量不能写入 "
                    + spec.DisplayName + " 变量「" + variable.Name + "」";
            }

            if (!isQuoted && variable.Type == DataType.String)
            {
                return "写入 STRING 变量「" + variable.Name + "」要用带引号的常量，如 #\"ABCD\"";
            }

            // 复用编码层：进制自标定、范围校验、REAL 解析全在那一处
            DisplayFormat format = AcquisitionConstant.FormatFor(literal, variable.Type);
            return ValueCodec.TryEncodeRegisters(
                variable.Type, format, literal, variable.Length, ByteOrderSettings.Default,
                out _, out string encodeError)
                ? null
                : "来源常量「" + sourceName + "」" + encodeError;
        }

        /// <summary>
        /// 「项3（压力）：」这种定位前缀。
        /// <para>
        /// 序号是**主要**定位手段（用户改完第一项回头看提示，靠的是位置）；
        /// 名称只是附带的第二线索 —— 项名可以重复，光靠名字指不准是哪一行的。
        /// </para>
        /// <para>
        /// 公开是因为写入失败的日志文案（<see cref="AcquisitionWrite.DescribeItemFailure"/>）
        /// 要用同一套前缀，两处规则必须一样，不能各写一份。
        /// </para>
        /// </summary>
        public static string DescribeItemPrefix(int index, string? name)
        {
            string number = "项" + index;
            string trimmed = (name ?? string.Empty).Trim();

            // 默认项名本来就叫「项N」，与序号重了。再括一遍会变成「项1（项1）：」，像个 bug
            return trimmed.Length == 0 || trimmed == number
                ? number + "："
                : number + "（" + trimmed + "）：";
        }
    }
}
