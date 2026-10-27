using System;
using System.Collections.Generic;

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

                AcquisitionSource? source = AcquisitionSource.Resolve(sourceName, findVariable);
                if (source == null)
                {
                    issues.Add(prefix + "来源变量「" + sourceName + "」不存在");
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

                if (!AcquisitionTaskRules.WriteTypesMatch(source.Type, variable.Type))
                {
                    issues.Add(
                        prefix + "来源「" + source.Name + "」是 " + DataTypes.Get(source.Type).DisplayName
                        + "，写入变量「" + variable.Name + "」是 " + DataTypes.Get(variable.Type).DisplayName
                        + "，两者必须相同");
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
