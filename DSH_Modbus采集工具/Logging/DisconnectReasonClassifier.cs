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
    /// <para>
    /// ⚠️ **2026-11-20 起补了串口（P4.4）与 Modbus 报文细分**：串口的失败原文几乎全是
    /// .NET/Windows 的英文，且措辞与「通讯」毫无关系（端口被占用报的是
    /// <c>Access to the path 'COM4' is denied.</c>），原样透出等于没提示；
    /// 而「站号不符 / CRC / 数据字节数」三者原先被压成同一句「响应帧校验失败」，
    /// 现场排查方向（改站号 / 查线路 / 查地址表）完全不同的三条线索被抹平。
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
                return ClassifyModbusFrame(text);
            }

            string? serial = ClassifySerial(text);
            if (serial != null)
            {
                return serial;
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

        /// <summary>
        /// Modbus 报文层失败细分。**不要退回「一律响应帧校验失败」**：
        /// 站号不符 = 站号/接线配错，CRC 错 = 线路干扰或波特率不对，
        /// 数据字节数不符 = 地址表与设备不符，三者动作完全不同。
        /// </summary>
        private static string ClassifyModbusFrame(string text)
        {
            if (text.Contains("站号不符", StringComparison.Ordinal))
            {
                // 带上「期望 X，实际 Y」——现场就靠这两个数字判断是站号配错还是串了口
                int at = text.IndexOf("站号不符", StringComparison.Ordinal);
                string tail = text[(at + "站号不符".Length)..].Trim().TrimStart('：', ':').Trim();
                return tail.Length > 0 ? $"站号不符（{tail}）" : "站号不符";
            }

            if (text.Contains("CRC", StringComparison.Ordinal))
            {
                return "响应 CRC 校验失败";
            }

            if (text.Contains("数据字节数", StringComparison.Ordinal))
            {
                return "响应数据长度不符";
            }

            if (text.Contains("写响应", StringComparison.Ordinal))
            {
                return "写应答与请求不符";
            }

            if (text.Contains("功能码", StringComparison.Ordinal))
            {
                return "响应功能码不符";
            }

            return "响应帧校验失败";
        }

        /// <summary>
        /// 串口失败原文归类（.NET 的 SerialPort 抛的是文件/路径措辞的英文）。
        /// 未命中返回 <c>null</c>，交给后面的通用规则继续判。
        /// </summary>
        private static string? ClassifySerial(string text)
        {
            if (text.Contains("denied", StringComparison.OrdinalIgnoreCase))
            {
                // 典型：Access to the path 'COM4' is denied. —— 端口被别的程序/实例占着
                return "串口被占用（其他程序正在使用该端口）";
            }

            if (text.Contains("does not resolve to a valid serial port", StringComparison.OrdinalIgnoreCase))
            {
                return "串口名无效";
            }

            if (text.Contains("Could not find file", StringComparison.OrdinalIgnoreCase))
            {
                // 典型：Could not find file 'COM4'. —— 口没插好 / 已被拔出 / 被系统回收
                return "串口不存在（未接好或已被拔出）";
            }

            if (text.Contains("Positive number required", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Parameter 'BaudRate'", StringComparison.OrdinalIgnoreCase))
            {
                return "串口参数无效";
            }

            if (text.Contains("参数错误", StringComparison.Ordinal))
            {
                // 典型：1.5 停止位配 8 数据位（系统不支持该组合，见 MainWindow.xaml 停止位处注释）
                return "串口参数无效（当前组合不被系统支持）";
            }

            if (text.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase))
            {
                // ⚠️ 串口读**不能被 token 立刻打断**：超时是「下一次 Read 才发现已取消」，
                // 于是超时经常以「操作已取消」的形式冒上来。不能当成功，也不能透英文。
                return "响应超时";
            }

            if (text.Contains("port is closed", StringComparison.OrdinalIgnoreCase))
            {
                return "串口已关闭";
            }

            if (text.Contains("device is not connected", StringComparison.OrdinalIgnoreCase)
                || text.Contains("does not recognize the command", StringComparison.OrdinalIgnoreCase))
            {
                return "串口设备异常（USB 转串口可能已拔出）";
            }

            if (text.Contains("I/O operation has been aborted", StringComparison.OrdinalIgnoreCase))
            {
                return "串口通讯中断";
            }

            return null;
        }
    }
}
