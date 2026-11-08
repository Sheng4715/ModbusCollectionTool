using System.IO;

namespace DSH_Modbus采集工具.Startup
{
    /// <summary>
    /// 开机自启动的**纯逻辑**（无 WPF、无注册表）：命令行怎么拼、什么样的命令算「指向本程序」、
    /// 以及系统禁用记录里那几个字节怎么读。
    ///
    /// <para>
    /// 之所以把判断拆出来：这几条规则正是最容易写错的地方 —— 引号、大小写、短路径（8.3）、
    /// 以及「同一个 exe 从调试输出目录跑起来」这几种情形，都要能离线断言，
    /// 而不是靠开一次机看效果。注册表读写留在 <see cref="AutoStartRegistry"/>。
    /// </para>
    /// </summary>
    public static class AutoStartCommand
    {
        /// <summary>
        /// <c>HKCU\...\CurrentVersion\Run</c> 下的值名。**用产品名**：任务管理器的「启动应用」页、
        /// Autoruns 这类工具显示的就是它，和程序名一致才不会在现场那台机器上认成陌生项。
        /// </summary>
        public const string ValueName = "DSH_Modbus采集工具";

        /// <summary>
        /// 资源管理器「启动应用」页勾选框写入的禁用记录（HKCU，按值名一一对应）。
        /// 里面是 12 字节：首字节 2/6 = 已启用，3/7 = 已禁用（首字节 &lt; 3 为启用），
        /// 后 8 字节是 FILETIME 形式的「何时改的」，仅供界面排序用。
        /// </summary>
        public const string StartupApprovedRunKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>
        /// 拼命令行：<c>"exe 完整路径"</c>（带引号，路径含空格时不带引号会被拆成两段）。
        ///
        /// <para>
        /// **刻意不加任何参数**。尤其不要加 <c>/background</c> 之类 —— 那要么得改启动逻辑，
        /// 要么会被当成未知参数忽略，两种都不是现在需要的。开机自启与手动双击的差别
        /// 只应在于「谁把它拉起来」。
        /// </para>
        /// </summary>
        public static string BuildCommand(string executablePath) =>
            $"\"{executablePath}\"";

        /// <summary>
        /// 从注册表里读到的命令串中取出 exe 路径并规范化（去掉外引号与首尾空白）。
        /// 取不出时返回空串。
        /// </summary>
        public static string ExtractPath(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return string.Empty;
            }

            string text = commandLine.Trim();

            // 形如 "C:\a b\app.exe" /x  的命令：取第一对引号里的内容
            if (text[0] == '"')
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text[1..end].Trim() : string.Empty;
            }

            // 没引号时不能按空格切：路径本身可能含空格。整体当路径，由调用方比对。
            return text;
        }

        /// <summary>
        /// 判断注册表里的命令是否**指向这一个可执行文件**。
        ///
        /// <para>
        /// 比较口径：先按 <see cref="ExtractPath"/> 取路径，再用 <see cref="Path.GetFullPath(string)"/>
        /// 规范化，最后**忽略大小写**比较（Windows 路径不区分大小写）。
        /// 不要求文件此刻存在 —— exe 被移到别处时，这里必须**认出来是旧的、该覆盖**，
        /// 而不是因为「文件不存在」判成不匹配从而留下一条指向空气的自启项。
        /// </para>
        /// </summary>
        public static bool IsSameExecutable(string? commandLine, string executablePath)
        {
            string registered = ExtractPath(commandLine);
            if (registered.Length == 0 || string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(registered),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 非法路径（含通配符等）→ 一律当作不匹配，让调用方覆盖成正确值
                return false;
            }
        }

        /// <summary>
        /// 读「系统层面是否被禁用」。
        ///
        /// <para>
        /// 这是本功能最容易漏掉的一条：任务管理器 / 资源管理器的「启动应用」页可以**单独禁用**
        /// 某一项，而禁用**不会删掉** <c>Run</c> 里的值，只会往 <c>StartupApproved\Run</c>
        /// 写一条首字节带「已禁用」标记的记录。结果就是「注册表里有、开关也是开的，
        /// 但开机就是不起来」。所以界面显示的勾选态必须是「已注册 **且** 未被禁用」，
        /// 否则开关会撒谎。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 判据是**低一位**（<c>first &amp; 1</c>），不是「首字节 ≥ 3」。实际出现的四个值：
        /// <c>0x02</c> / <c>0x06</c> 为启用，<c>0x03</c> / <c>0x07</c> 为禁用 ——
        /// 高低两档（2/3 与 6/7）差别在别的位上，按数值大小比较会把 <c>0x06</c>
        /// 这个**启用**态误判成禁用。该错误由 <c>.tmp/AutoStartCheck</c> 断言抓出，
        /// 别改回「≥3」。
        /// </para>
        ///
        /// <para>
        /// 记录缺失 / 类型不对 / 长度不足 一律当**未禁用**（老系统没有这个键）。
        /// </para>
        /// </summary>
        public static bool IsBlockedBySystem(byte[]? approvedValue) =>
            approvedValue is { Length: > 0 } && (approvedValue[0] & 0x01) != 0;

        /// <summary>生成一条「已启用」的 StartupApproved 记录（首字节 2）。</summary>
        public static byte[] BuildEnabledApprovedValue()
        {
            var bytes = new byte[12];
            bytes[0] = 0x02;
            return bytes;
        }
    }
}
