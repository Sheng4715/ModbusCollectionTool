using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSH_Modbus采集工具.Scanners
{
    /// <summary>
    /// 扫码器结果帧结束符：判定「一条扫码结果收完」的依据（P4.5）。
    /// <para>
    /// 品牌不定、ASCII 不定长，所以结束符可配置。四种取值的字节序在
    /// <see cref="ScannerRules.TerminatorBytes"/> 里集中定义。
    /// </para>
    /// </summary>
    public enum ScannerFrameTerminator
    {
        /// <summary>遇 <c>\r\n</c> 即认为一条完整帧（默认）。</summary>
        CrLf = 0,

        /// <summary>遇 <c>\r</c> 即认为一条完整帧。</summary>
        Cr = 1,

        /// <summary>遇 <c>\n</c> 即认为一条完整帧。</summary>
        Lf = 2,

        /// <summary>无结束符：退化为「字节静默间隔」收尾（收完一字节后短间隔内无后续即判收完）。</summary>
        None = 3,
    }

    /// <summary>
    /// 一台扫码器的完整定义：连接参数（P4.5 配）+ 交互参数（P3.5 配）。
    /// <para>
    /// 与 <c>GlobalVariable</c> 同一约定：只有「定义」落盘（<c>scanner.json</c>），
    /// 运行时态（连接身份、收发缓冲、执行中标记）一律不在此类，也不落盘。
    /// </para>
    /// </summary>
    public sealed class ScannerDefinition
    {
        // ---- 连接参数（P4.5 设备连接页编辑） ----

        /// <summary>扫码器名称（唯一，P4.5 建/删/改名，P3.5 只读）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>IP 地址（空串 = 没保存过）。</summary>
        public string Ip { get; set; } = string.Empty;

        /// <summary>端口号（0 = 没保存过，有效范围 1~65535）。</summary>
        public int Port { get; set; }

        /// <summary>结果帧结束符（默认 <see cref="ScannerFrameTerminator.CrLf"/>）。</summary>
        public ScannerFrameTerminator Terminator { get; set; } = ScannerFrameTerminator.CrLf;

        // ---- 交互参数（P3.5 数据管理页编辑） ----

        /// <summary>触发条件表达式（上升沿有效；空 = 永不触发）。</summary>
        public string TriggerExpression { get; set; } = string.Empty;

        /// <summary>触发文本（ASCII、≤20 字符、非空；原样发送不补结束符）。</summary>
        public string TriggerText { get; set; } = string.Empty;

        /// <summary>接收缓冲区变量名（只能 STRING；可为空 = 不写缓冲区）。</summary>
        public string BufferVariableName { get; set; } = string.Empty;

        /// <summary>超时判定时间（秒，0.5~10，默认 3）。</summary>
        public double TimeoutSeconds { get; set; } = ScannerRules.DefaultTimeoutSeconds;

        /// <summary>
        /// 执行结果输出变量名（INT/UINT/DINT/UDINT；可为空 = 不写结果）。
        /// <para>
        /// 写入值不再可配：正常执行完成写 <see cref="ScannerRules.ResultSuccess"/>「1」、
        /// 异常（超时 / 链路异常）执行完成写 <see cref="ScannerRules.ResultFailure"/>「2」。
        /// </para>
        /// </summary>
        public string NotifyVariableName { get; set; } = string.Empty;

        /// <summary>是否启用：关闭时该扫码器不触发、不收发（连接仍在，仍自动重连）。</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// 「这台扫码器是哪一栏填错了」。配置校验只报**第一条**，界面靠它决定标红哪个输入框
    /// （2026-11-04 定案：不再在展开块顶部写一行红字，改成填错的框自己变红边框）。
    /// </summary>
    public enum ScannerField
    {
        /// <summary>没有错误。</summary>
        None = 0,

        /// <summary>名称框（空 / 重名）。</summary>
        Name,

        /// <summary>IP 地址框。</summary>
        Ip,

        /// <summary>端口号框。</summary>
        Port,

        /// <summary>触发条件表达式框。</summary>
        TriggerExpression,

        /// <summary>触发文本框。</summary>
        TriggerText,

        /// <summary>接收缓冲区变量框。</summary>
        BufferVariable,

        /// <summary>超时判定（秒）框。</summary>
        TimeoutSeconds,

        /// <summary>执行结果输出变量框。</summary>
        NotifyVariable,
    }

    /// <summary>
    /// 扫码器定义的口径规则（纯函数 / 纯数据，不碰 WPF、不读全局状态）。
    /// </summary>
    public static class ScannerRules
    {
        /// <summary>触发文本的最大 ASCII 字符数。</summary>
        public const int MaxTriggerTextLength = 20;

        /// <summary>
        /// 「正常执行完成」写进执行结果输出变量的值。
        /// <para>
        /// 2026-11-04 定案：这两个值**不再可配**（原先是一对「写入值 / 超时写入值」输入框），
        /// 固定为 1 / 2，界面在变量框下用一行小字说明。
        /// </para>
        /// </summary>
        public const string ResultSuccess = "1";

        /// <summary>「异常（如超时）执行完成」写进执行结果输出变量的值。</summary>
        public const string ResultFailure = "2";

        /// <summary>执行结果输出变量框下的说明文字（界面上那一行小字）。</summary>
        public const string ResultValueHint = "正常执行完成：1   异常执行完成：2";

        /// <summary>超时判定时间下限（秒）。</summary>
        public const double MinTimeoutSeconds = 0.5;

        /// <summary>超时判定时间上限（秒）。</summary>
        public const double MaxTimeoutSeconds = 10;

        /// <summary>超时判定时间默认值（秒）。</summary>
        public const double DefaultTimeoutSeconds = 3.0;

        /// <summary>最多连接的扫码器台数（P4.5）。</summary>
        public const int MaxScanners = 16;

        /// <summary>
        /// 配置不完整时行上那四个字。与采集任务组的
        /// <c>AcquisitionTaskCheck.ErrorText</c> 刻意同值：同一种状态在两个页面用同一句话，
        /// 界面里不再各写一份字面量。
        /// </summary>
        public const string ConfigErrorText = "配置错误";

        /// <summary>
        /// 校验「触发文本」：非空、纯 ASCII、不超过 <see cref="MaxTriggerTextLength"/> 字符。
        /// </summary>
        public static bool TryValidateTriggerText(string? text, out string error)
        {
            if (string.IsNullOrEmpty(text))
            {
                error = "触发文本不能为空";
                return false;
            }

            return TryValidateAsciiLength(text, MaxTriggerTextLength, "触发文本", out error);
        }

        private static bool TryValidateAsciiLength(string text, int max, string label, out string error)
        {
            if (text.Length > max)
            {
                error = $"{label}不能超过 {max} 个字符";
                return false;
            }

            foreach (char c in text)
            {
                if (c > 0x7F)
                {
                    error = $"{label}只能包含 ASCII 字符";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>校验超时判定时间是否落在 <c>0.5 ~ 10</c> 秒区间。</summary>
        public static bool TryValidateTimeout(double seconds, out string error)
        {
            if (seconds < MinTimeoutSeconds || seconds > MaxTimeoutSeconds)
            {
                error = $"超时判定时间需在 {MinTimeoutSeconds} ~ {MaxTimeoutSeconds} 秒之间";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>结束符对应的字节序列；<see cref="ScannerFrameTerminator.None"/> 返回空数组（走静默间隔）。</summary>
        public static byte[] TerminatorBytes(ScannerFrameTerminator terminator) => terminator switch
        {
            ScannerFrameTerminator.CrLf => new byte[] { 0x0D, 0x0A },
            ScannerFrameTerminator.Cr => new byte[] { 0x0D },
            ScannerFrameTerminator.Lf => new byte[] { 0x0A },
            _ => Array.Empty<byte>(),
        };

        /// <summary>结束符的下拉显示文本（UI 用，含字面转义）。</summary>
        public static string TerminatorDisplay(ScannerFrameTerminator terminator) => terminator switch
        {
            ScannerFrameTerminator.CrLf => "\\r\\n",
            ScannerFrameTerminator.Cr => "\\r",
            ScannerFrameTerminator.Lf => "\\n",
            _ => "无",
        };

        /// <summary>
        /// 统计「缓冲区变量 / 通知变量」被多少台扫码器引用，用于黄字提示「变量已在别处被引用」。
        /// <para>返回给定变量名在 <paramref name="scanners"/> 中（排除 <paramref name="exclude"/> 自身）的出现次数。</para>
        /// </summary>
        public static int CountReferences(IEnumerable<ScannerDefinition> scanners, ScannerDefinition exclude, string? variableName)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                return 0;
            }

            return scanners.Count(s => !ReferenceEquals(s, exclude)
                && (string.Equals(s.BufferVariableName, variableName, StringComparison.Ordinal)
                    || string.Equals(s.NotifyVariableName, variableName, StringComparison.Ordinal)));
        }

        /// <summary>生成下一台扫码器的默认名（scanner1 / scanner2 / …，跳过已占用名）。</summary>
        public static string NextName(IEnumerable<ScannerDefinition> scanners)
        {
            var taken = new HashSet<string>(scanners.Select(s => s.Name), StringComparer.Ordinal);
            for (int i = 1; ; i++)
            {
                string candidate = $"scanner{i}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// 扫码器定义的持久化（<c>scanner.json</c>），落 <see cref="AppContext.BaseDirectory"/>，
    /// 与 <c>app-settings.json</c> / <c>GlobalVar.json</c> 同级。口径照抄 <c>DataPointStore</c>。
    /// </summary>
    public static class ScannerStore
    {
        private const string FileName = "scanner.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 枚举写成名字（CrLf / None …）而非数字，避免枚举顺序变化后静默错位
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        /// <summary>读失败 / 文件不存在 / 空内容一律返回空列表，不阻断启动。</summary>
        public static List<ScannerDefinition> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return new List<ScannerDefinition>();
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ScannerDefinition>();
                }

                var list = JsonSerializer.Deserialize<List<ScannerDefinition>>(json, Options);
                return list ?? new List<ScannerDefinition>();
            }
            catch
            {
                return new List<ScannerDefinition>();
            }
        }

        public static bool Save(IEnumerable<ScannerDefinition> scanners)
        {
            try
            {
                string json = JsonSerializer.Serialize(scanners, Options);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
