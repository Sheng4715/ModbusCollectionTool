using System;

namespace DSH_Modbus采集工具.Logging
{
    /// <summary>
    /// 把底层异常 / 状态原文归类为简短的中文原因。
    /// <para>
    /// **底栏与日志共用这一份规则**（2026-10-16 起）：在此之前底栏直传原始异常，
    /// 会闪出整串英文 Socket 消息（Brief §7-2）。规则集中到这里之后，两处不可能再各写一套。
    /// </para>
    /// <para>
    /// 刻意不引用 WPF、无任何状态：可被 <c>LoggingCheck</c> 链接源码直接断言整张映射表。
    /// </para>
    /// </summary>
    public static class DisconnectReasonClassifier
    {
        /// <summary>
        /// 命中已知模式返回中文短语，未命中则**原样返回**（可能含英文），保证不吞掉信息。
        /// </summary>
        public static string Classify(string raw)
        {
            // `raw` 声明为非空，但异常消息确实可能是 null（自定义异常 / Message 被置空）。
            // 直接 .Trim() 会抛 NRE —— 这个类在断线路径上被调用，
            // 在最需要它出文案的时候崩掉，代价远比多判一次空大。
            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return "未知原因";
            }

            const string heartbeatPrefix = "心跳检查失败：";
            if (text.StartsWith(heartbeatPrefix, StringComparison.Ordinal))
            {
                text = text[heartbeatPrefix.Length..].Trim();
            }

            // 已是简短中文原因的（含「心跳」这类上下文）直接沿用，不做压缩
            if (text is "读取超时" or "心跳读取超时" or "连接被对方关闭")
            {
                return text;
            }

            if (text.StartsWith("Modbus 异常：", StringComparison.Ordinal))
            {
                return text["Modbus 异常：".Length..]; // 例如「从站设备忙（异常码 6）」
            }

            if (text.StartsWith("Modbus ", StringComparison.Ordinal))
            {
                return "响应帧校验失败";
            }

            string cause;
            if (text.Contains("无法连接的网络", StringComparison.Ordinal)
                || text.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase))
            {
                cause = "网络不可达";
            }
            else if (text.Contains("无法访问目标主机", StringComparison.Ordinal)
                     || text.Contains("host is unreachable", StringComparison.OrdinalIgnoreCase))
            {
                cause = "主机不可达";
            }
            else if (text.Contains("积极拒绝", StringComparison.Ordinal)
                     || text.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                cause = "目标拒绝连接";
            }
            else if (text.Contains("强迫关闭", StringComparison.Ordinal)
                     || text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
            {
                cause = "对端关闭连接";
            }
            else if (text.Contains("超时", StringComparison.Ordinal)
                     || text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                cause = "响应超时";
            }
            else
            {
                cause = text; // 未归类：原样保留，不吞信息
            }

            // 不再给心跳来源加「心跳：」前缀 —— 会出现「已断开：心跳：对端关闭连接」这种双冒号
            return cause;
        }
    }
}
