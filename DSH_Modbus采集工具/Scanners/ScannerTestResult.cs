using System.Text;

namespace DSH_Modbus采集工具.Scanners
{
    /// <summary>
    /// 「触发测试」结果的文案拼装（P4.5，2026-11-08）。
    ///
    /// <para>
    /// 抽成纯逻辑的理由：这段文字要**在弹框里给人看**，而扫码结果常带
    /// <c>\r\n</c>、不可打印字节、超长内容 —— 直接拼进去会让对话框变形或丢失信息。
    /// 规则要能离线断言（见 <c>.tmp/ScannerTestCheck</c>）。
    /// </para>
    /// </summary>
    public static class ScannerTestResult
    {
        /// <summary>
        /// 弹框里展示的收到内容上限（字符）。超过就截断并在末尾标注总长。
        /// <para>
        /// 比日志的 80 字宽松（日志是一行，弹框是给现场调试看的一次性结果），
        /// 但仍要设上限：扫码器偶尔会吐一整屏内容，不截断会让弹框撑出屏幕。
        /// </para>
        /// </summary>
        public const int DisplayMaxChars = 500;

        /// <summary>
        /// 把收到的原始文本整理成**可显示**的形态：
        /// 控制字符转义（<c>\r</c> / <c>\n</c> / <c>\t</c> / <c>\xNN</c>），
        /// 超长截断。
        ///
        /// <para>
        /// ⚠️ **不能直接显示原文**：真实的扫描结果几乎都带 <c>\r\n</c> 结束符，
        /// 原样塞进 TextBlock 会让本来一行的结果变成两行，用户看不等宽、也分不清
        /// 「是内容里真有换行」还是「弹框自己折的行」。
        /// </para>
        /// </summary>
        public static string FormatReceived(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "（空）";
            }

            var builder = new StringBuilder(Math.Min(raw.Length, DisplayMaxChars) + 32);
            int shown = 0;

            foreach (char ch in raw)
            {
                if (shown >= DisplayMaxChars)
                {
                    break;
                }

                switch (ch)
                {
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(ch))
                        {
                            builder.Append($"\\x{(int)ch:X2}");
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }

                shown++;
            }

            if (raw.Length > DisplayMaxChars)
            {
                builder.Append($"…（共 {raw.Length} 字符，已截断）");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 成功时弹框的正文：收到的内容 + 实际字节数。
        /// <para>
        /// ⚠️ **不写「有没有写进变量」这一行**（2026-11-08 用户定案删除）：触发测试
        /// **永远不写变量**（ADR-43），所以那一行必然是「未写入变量」——
        /// 一条永远相同、且对使用者没有信息量的状态说明，只是噪音。
        /// </para>
        /// </summary>
        public static string BuildSuccessBody(string? raw, int byteCount)
        {
            var lines = new StringBuilder();
            lines.Append($"收到内容：{FormatReceived(raw)}\r\n");
            lines.Append($"字节数：{byteCount}");

            return lines.ToString();
        }

        /// <summary>
        /// 失败时弹框的正文。
        /// </summary>
        /// <param name="reason">已归一的失败原因（与日志同一套用词）。</param>
        /// <param name="timeoutSeconds">本次用的超时秒数；不适用时传 0。</param>
        public static string BuildFailureBody(string? reason, double timeoutSeconds)
        {
            string text = string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason;

            return timeoutSeconds > 0
                ? $"触发失败：{text}\r\n\r\n等待超时：{timeoutSeconds:0.#} 秒"
                : $"触发失败：{text}";
        }
    }
}
