using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DSH_Modbus采集工具.Expressions;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一个任务组的**运行时状态**（P3.1 采集执行）。
    /// <para>
    /// 与 <see cref="AcquisitionTask"/> 分开：那个是**配置**、要落盘；这个只活在内存里，
    /// 记录「上次什么时候采的」「表达式上一拍是真是假」。重启后一律重来，没有保存的意义。
    /// </para>
    /// </summary>
    public sealed class AcquisitionTaskState
    {
        /// <summary>下次该采的时刻（周期式用）。默认 <see cref="DateTime.MinValue"/> = 立刻采第一拍。</summary>
        public DateTime NextFireLocal { get; set; } = DateTime.MinValue;

        /// <summary>表达式上一拍的取值（触发式用）。</summary>
        public bool WasConditionTrue { get; set; }

        /// <summary>编译好的触发表达式（每拍复用，不重复解析）。</summary>
        public CompiledExpression? Compiled { get; set; }

        /// <summary>上面那份编译结果对应的原文 —— 表达式被改过就要重编。</summary>
        public string CompiledSource { get; set; } = string.Empty;

        /// <summary>
        /// 当前正在追加的记录文件全路径。空串 = 还没定过，下一次追加时解析。
        /// <para>
        /// ⚠️ 缓存它是为了**每拍不用扫目录**（周期可以小到 0.1s）。失效条件有两个：
        /// 换了天、或列集合变了（<see cref="RecordHeaderKey"/> 对不上）。
        /// </para>
        /// </summary>
        public string RecordPath { get; set; } = string.Empty;

        /// <summary>当前文件表头的「签名」（即表头那一行的原文），列一变就不相等。</summary>
        public string RecordHeaderKey { get; set; } = string.Empty;

        /// <summary>当前文件属于哪一天（<see cref="RecordPath"/> 是那天定的）。</summary>
        public DateOnly RecordDay { get; set; } = DateOnly.MinValue;

        /// <summary>
        /// 这一组**还有一次写入正在路上**（写入任务专用）。
        /// <para>
        /// 采样的节拍是 50ms，而写入要等一次完整的 Modbus 往返；周期配到 0.1s 时，
        /// 上一笔还没回来下一拍就到了，不挡住就会把同一项连着排一串报文出去。
        /// </para>
        /// <para>
        /// ⚠️ 挡掉的那一拍**不补采**：调度已经推到了「本次 + 周期」，漏一拍不影响周期节奏。
        /// </para>
        /// </summary>
        public bool WriteInFlight { get; set; }
    }

    /// <summary>
    /// 采集执行的判定：**这一拍该不该采**（P3.1）。
    /// <para>纯函数 + 一个状态对象，不碰 Modbus、不碰 WPF，可离线断言。</para>
    /// </summary>
    public static class AcquisitionTrigger
    {
        /// <summary>
        /// 判定本拍是否触发，并推进 <paramref name="state"/>。
        /// </summary>
        /// <param name="task">任务组配置</param>
        /// <param name="state">该组的运行时状态</param>
        /// <param name="now">本拍的时刻（本地时间）</param>
        /// <param name="conditionTrue">触发表达式的本拍取值（周期式忽略）</param>
        /// <remarks>
        /// <para>
        /// **周期式**：到点就采，采完把下次时刻推到「本次 + 周期」。
        /// 用「下次时刻」而不是「上次时刻 + 间隔」，是为了让周期不随定时器抖动累积漂移。
        /// </para>
        /// <para>
        /// **触发式**：只认**上升沿** —— 上一拍假、这一拍真才采。
        /// 上一拍真、这一拍仍真**不重复采**（需求：触发式为表达式成立上升沿有效）。
        /// </para>
        /// <para>
        /// ⚠️ 首次求值时 <see cref="AcquisitionTaskState.WasConditionTrue"/> 是 <c>false</c>，
        /// 所以「程序启动时条件已经成立」会**立刻采一拍**。这是有意的：当作启动前为假，
        /// 否则程序重启一次就永远等不到那个沿了。
        /// </para>
        /// </remarks>
        public static bool ShouldFire(
            AcquisitionTask task, AcquisitionTaskState state, DateTime now, bool conditionTrue)
        {
            if (task == null || state == null)
            {
                return false;
            }

            if (task.Trigger == AcquisitionTriggerMode.Periodic)
            {
                // 周期小于定时器节拍时（如 0.1s 周期、50ms 节拍），每拍都到点，
                // 于是「到点就采」自然退化成「每拍采一次」，不会因为推后而漏采。
                if (now < state.NextFireLocal)
                {
                    return false;
                }

                double seconds = task.PeriodSeconds;
                if (double.IsNaN(seconds) || double.IsInfinity(seconds)
                    || seconds < AcquisitionTaskRules.MinPeriodSeconds)
                {
                    seconds = AcquisitionTaskRules.DefaultPeriodSeconds;
                }

                // 以「本次」为基准推下一次，而不是以旧的下次时刻 —— 后者在长时间停采
                // （比如断线重连）后会连发一串补采，把文件写满重复数据
                state.NextFireLocal = now.AddSeconds(seconds);
                return true;
            }

            bool rising = conditionTrue && !state.WasConditionTrue;
            state.WasConditionTrue = conditionTrue;
            return rising;
        }
    }

    /// <summary>
    /// 数据记录表格（CSV）的列与内容（P3.1）。
    /// <para>
    /// 表头：<c>时间,任务类型,{项1名称},{项2名称},…</c>
    /// 数据行：<c>11:04:22,读取,123.45,abcd</c>
    /// </para>
    /// <para>
    /// ⚠️ **时间列只有时分秒**，日期由文件名承担 —— 所以跨天必须换文件，
    /// 否则第二天追加进同一份文件，日期信息就丢了（见 <see cref="Append"/>）。
    /// </para>
    /// </summary>
    public static class AcquisitionCsv
    {
        /// <summary>时间列的格式</summary>
        public const string TimeFormat = "HH:mm:ss";

        /// <summary>任务类型列的文字。需求样例里是「读取」/「写入」，不是界面上的「数据读取」。</summary>
        public static string KindText(AcquisitionTaskKind kind) =>
            kind == AcquisitionTaskKind.Write ? "写入" : "读取";

        /// <summary>
        /// 表头。**项名称重复不拦**（用户定案）：两列同名照样写，CSV 本身允许。
        /// </summary>
        public static List<string> BuildHeader(AcquisitionTask task)
        {
            var fields = new List<string> { "时间", "任务类型" };

            if (task?.Items != null)
            {
                for (int i = 0; i < task.Items.Count; i++)
                {
                    string name = task.Items[i].Name ?? string.Empty;
                    // 项名称允许为空（用户还没填），空表头会让整列没法辨认，故兜一个占位名
                    fields.Add(name.Length > 0
                        ? name
                        : "项" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }

            return fields;
        }

        /// <summary>
        /// 数据行。
        /// <para>
        /// **读取类型**记「读取变量」的值；**写入类型**记「写入变量」的值（用户定案：
        /// 只记写入变量的值，不记来源）。两档取的都是变量当前的
        /// <see cref="GlobalVariable.Value"/>，也就是已经按显示格式转好的文本。
        /// </para>
        /// </summary>
        public static List<string> BuildRow(
            AcquisitionTask task, DateTime now, Func<string, GlobalVariable?> findVariable)
        {
            var fields = new List<string>
            {
                now.ToString(TimeFormat, CultureInfo.InvariantCulture),
                KindText(task.Kind),
            };

            if (task?.Items != null)
            {
                foreach (AcquisitionItem item in task.Items)
                {
                    string variableName = item.VariableName ?? string.Empty;
                    GlobalVariable? variable =
                        variableName.Length == 0 ? null : findVariable(variableName);

                    // 变量没填 / 找不到 → 空单元格。**不写占位符**：
                    // 表格是给下游（Excel、MES）读的，混进「—」会变成脏数据
                    fields.Add(variable?.Value ?? string.Empty);
                }
            }

            return fields;
        }

        /// <summary>
        /// 把一行追加到该任务组当天的文件里；文件不存在时先写表头。
        /// </summary>
        /// <param name="task">任务组配置</param>
        /// <param name="state">该组的运行时状态（缓存当前文件路径，避免每拍扫目录）</param>
        /// <param name="now">本次记录的时刻</param>
        /// <param name="findVariable">按名称查全局变量</param>
        /// <returns>成功返回空串，失败返回原因（由调用方决定怎么提示）。</returns>
        /// <remarks>
        /// 路径形如 <c>{保存路径}\{任务组名称}\{yyyy-MM-dd}.csv</c>。两种情况会换新文件：
        /// <list type="bullet">
        /// <item>**跨天**：时间列只有时分秒，日期靠文件名承担</item>
        /// <item>**列集合变了**（增删数据项、改项名称）：表头只在建文件时写一次，
        /// 硬往老文件追加会让表头与数据行**列数不等**。Excel 不报错、只是列错位，
        /// 属于「发现时已攒了一周脏数据」的那类问题，所以宁可多一个文件</item>
        /// </list>
        /// </remarks>
        public static string Append(
            AcquisitionTask task, AcquisitionTaskState state, DateTime now,
            Func<string, GlobalVariable?> findVariable)
        {
            if (task == null)
            {
                return "任务组为空";
            }

            if (state == null)
            {
                return "运行态为空";
            }

            try
            {
                List<string> header = BuildHeader(task);
                string headerKey = FormatLine(header);
                DateOnly today = DateOnly.FromDateTime(now);

                // 缓存失效的两种情形：换了天、列变了。任一成立就重新解析目标文件
                string path = state.RecordPath;
                if (path.Length == 0
                    || state.RecordDay != today
                    || !string.Equals(state.RecordHeaderKey, headerKey, StringComparison.Ordinal))
                {
                    path = ResolveFilePath(task, now, headerKey);
                    state.RecordPath = path;
                    state.RecordDay = today;
                    state.RecordHeaderKey = headerKey;
                }

                string? folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder))
                {
                    return "保存路径无效";
                }

                Directory.CreateDirectory(folder);

                var text = new StringBuilder();
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    text.AppendLine(headerKey);
                }

                text.AppendLine(FormatLine(BuildRow(task, now, findVariable)));

                // UTF-8 **带 BOM**：不带的话 Excel 双击打开会把中文表头读成乱码，
                // 这是 CSV 交付环节最常见的投诉
                File.AppendAllText(path, text.ToString(), new UTF8Encoding(true));
                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 找出「当前列集合」该写进哪个文件：当天**最后一个**文件表头一致就续写它，
        /// 否则接一个新序号。
        /// </summary>
        /// <remarks>
        /// 只跟**最后一个**比，不回头找表头相同的老文件：那样 A,B → A,B,C → A,B 会写回第一个文件，
        /// 同一份文件里的时间戳就出现**回跳**，下游按时间排序直接乱掉。
        /// 多一个文件是可见的代价，时间戳乱序是隐蔽的代价。
        /// </remarks>
        public static string ResolveFilePath(AcquisitionTask task, DateTime now, string headerKey)
        {
            string folder = AcquisitionTaskRules.ResolveRecordDirectory(task);
            string stem = AcquisitionTaskRules.DateStem(now);
            Directory.CreateDirectory(folder);

            string? latest = null;
            int latestIndex = 0;

            foreach (string file in Directory.EnumerateFiles(folder, stem + "*" + AcquisitionTaskRules.RecordExtension))
            {
                int index = AcquisitionTaskRules.ParseRecordFileIndex(stem, file);
                if (index > latestIndex)
                {
                    latestIndex = index;
                    latest = file;
                }
            }

            if (latest != null && HeaderMatches(latest, headerKey))
            {
                return latest;
            }

            return Path.Combine(folder, AcquisitionTaskRules.RecordFileName(now, latestIndex + 1));
        }

        /// <summary>文件首行是否就是这份表头。空文件（或只剩 BOM）视为不匹配，交给调用方新建。</summary>
        private static bool HeaderMatches(string path, string headerKey)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    return false;
                }

                // File.ReadLines 默认按 BOM 探测编码，读出来的首行不含 BOM 字符
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? first = reader.ReadLine();
                return first != null && string.Equals(first, headerKey, StringComparison.Ordinal);
            }
            catch
            {
                // 读不了就当不匹配：宁可新开一个文件，也不要往一个读不懂的文件里追加
                return false;
            }
        }

        /// <summary>
        /// 一行字段拼成 CSV 文本。
        /// <para>
        /// 含逗号 / 引号 / 换行的字段**必须**加引号，内部引号翻倍 ——
        /// 变量值里出现一个逗号（STRING 类型的注释、带千分位的数）就会把整行错位，
        /// 而且错得很隐蔽：Excel 打开只是列对不上，不报错。
        /// </para>
        /// </summary>
        public static string FormatLine(IReadOnlyList<string> fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(FormatField(fields[i]));
            }

            return sb.ToString();
        }

        /// <summary>单个字段的 CSV 转义。</summary>
        public static string FormatField(string? value)
        {
            string text = value ?? string.Empty;

            bool needQuote = text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needQuote)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        // ================= 读取（总览页「数据采集」视图的历史表格） =================

        /// <summary>
        /// 把一行 CSV 文本拆回字段，是 <see cref="FormatLine"/> 的逆运算。
        /// <para>
        /// ⚠️ 必须处理**引号内的逗号**：变量值里出现一个逗号（STRING 注释、带千分位的数）
        /// 就会把整行错位，而错得很隐蔽 —— Excel 打开只是列对不上、不报错。
        /// 所以这里不能用 <c>Split(',')</c>。
        /// </para>
        /// </summary>
        public static List<string> ParseLine(string? line)
        {
            var fields = new List<string>();
            if (line == null)
            {
                fields.Add(string.Empty);
                return fields;
            }

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // 连续两个引号 = 一个字面引号；单个引号 = 引号段结束
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>
        /// 该任务组在 <paramref name="now"/> 当天**序号最大**的记录文件；没有就 <c>null</c>。
        /// <para>
        /// 与写入侧的 <see cref="ResolveFilePath"/> 刻意不同：那边要拿**当前表头**去比对、
        /// 决定续写还是开新文件；这边只是「把最新那份拿出来看」，
        /// 表头变了本来就会出现 <c>_2.csv</c> 且序号更大 —— 直接取最大序号即可，
        /// 不必（也不该）因为表头对不上就退回旧文件。
        /// </para>
        /// </summary>
        public static string? FindLatestRecordFile(AcquisitionTask? task, DateTime now)
        {
            if (task == null)
            {
                return null;
            }

            try
            {
                string folder = AcquisitionTaskRules.ResolveRecordDirectory(task);
                if (!Directory.Exists(folder))
                {
                    return null;
                }

                string stem = AcquisitionTaskRules.DateStem(now);
                string? latest = null;
                int latestIndex = 0;

                foreach (string file in Directory.EnumerateFiles(
                             folder, stem + "*" + AcquisitionTaskRules.RecordExtension))
                {
                    int index = AcquisitionTaskRules.ParseRecordFileIndex(stem, file);
                    if (index > latestIndex)
                    {
                        latestIndex = index;
                        latest = file;
                    }
                }

                return latest;
            }
            catch
            {
                // 目录读不了就当没有：视图显示空状态，比抛异常砸到 UI 线程好
                return null;
            }
        }
    }
}
