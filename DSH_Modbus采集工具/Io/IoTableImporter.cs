using System;
using System.Collections.Generic;
using System.Linq;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Io
{
    /// <summary>
    /// IO 表导入的**解析与校验**结果：可直接替换的注释表，以及逐条失败明细。
    /// </summary>
    /// <remarks>
    /// 不含任何 UI 类型 —— 弹窗文案的组装与展示留在窗口层，本层只负责「表 → 数据」的判定。
    /// </remarks>
    internal sealed class IoImportResult
    {
        /// <summary>通过校验的注释表（键 = 归一化点位名，值 = 注释）。</summary>
        public Dictionary<string, string> Comments { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>失败明细，形如 <c>"X999 —— 编号超出…"</c>。</summary>
        public List<string> Failures { get; } = new();

        /// <summary>源表里识别到的数据行数（未去重、未校验前）。</summary>
        public int SourceRowCount { get; init; }

        /// <summary>成功项数。</summary>
        public int OkCount => Comments.Count;

        /// <summary>失败项数。</summary>
        public int FailureCount => Failures.Count;
    }

    /// <summary>
    /// 导入 IO 点位表：读 xlsx → 逐行按当前驱动校验 → 产出注释表或失败明细。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 全量替换语义：以表为权威重建注释表。每行先按当前通讯驱动校验地址是否落在驱动定义范围内，
    /// 再确认是 IO 监控页上的点位；任一环节不通过都计入「失败」，不写入注释表。
    /// </para>
    /// <para>
    /// 解析失败（读文件异常）直接抛出，由调用方决定如何提示；「0 项有效」不抛，交调用方判定。
    /// </para>
    /// </remarks>
    internal static class IoTableImporter
    {
        /// <summary>
        /// 读取并校验 <paramref name="path"/> 指向的 xlsx，产出导入结果。
        /// </summary>
        /// <exception cref="Exception">读取 / 解析 xlsx 失败时抛出（消息可直接展示）。</exception>
        public static IoImportResult Read(CommDriver? driver, string path)
        {
            List<(string Address, string Comment)> rows = XlsxIoTableReader.Read(path);
            var result = new IoImportResult { SourceRowCount = rows.Count };

            foreach ((string address, string comment) in rows)
            {
                string name = address.Trim();
                if (!IoPointSet.TryValidatePoint(driver, name, out string reason))
                {
                    result.Failures.Add($"{name} —— {reason}");
                    continue;
                }

                // 键沿用表里的原始写法（仅去首尾空白）；大小写由字典的 OrdinalIgnoreCase 兜住，
                // 与「导入即整体替换」前的行为一致。
                result.Comments[name] = comment.Trim();
            }

            return result;
        }

        /// <summary>失败明细文本：最多列前 12 条，其余折叠为一行汇总。</summary>
        public static string FormatFailures(List<string> failures)
        {
            const int maxShown = 12;
            var lines = failures.Take(maxShown).ToList();
            if (failures.Count > maxShown)
            {
                lines.Add($"…… 其余 {failures.Count - maxShown} 项从略");
            }

            return string.Join("\n", lines);
        }
    }
}
