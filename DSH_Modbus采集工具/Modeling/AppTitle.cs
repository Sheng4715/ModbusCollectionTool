namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 应用标题的**纯逻辑**（无 WPF）：默认值、清洗、以及「存空串 = 用默认」的判定。
    ///
    /// <para>
    /// 设置页可改的两个位置**默认值本来就不同**（这是历史现状，本次刻意不改）：
    /// 窗口标题栏里那行字是「数据采集Demo」，而 <c>Window.Title</c>（任务栏 / Alt+Tab）
    /// 是「采集工具」。所以用户**没设置过**时，两边各用各自的默认值，外观与改动前完全一致；
    /// 一旦设了自定义标题，**两边都换成它** —— 用户改的是「应用标题」，不该只改一半。
    /// </para>
    /// <para>
    /// 抽在这里而不是塞进 <c>ThemeManager</c>：后者引 WPF，塞进去就没法离线断言了。
    /// 与 <c>IoPager</c> / <c>AcquisitionConstant</c> 等同一手法。
    /// </para>
    /// </summary>
    public static class AppTitle
    {
        /// <summary>窗口标题栏里那行字的默认值（未自定义时显示）。</summary>
        public const string DefaultCaption = "数据采集Demo";

        /// <summary><c>Window.Title</c> 的默认值（任务栏 / Alt+Tab 里显示的）。</summary>
        public const string DefaultWindowTitle = "采集工具";

        /// <summary>
        /// 自定义标题的长度上限。
        /// <para>
        /// 不是审美数字：标题栏左侧那块宽度有限，标题过长会顶到右边的窗口按钮上；
        /// 任务栏按钮同理。40 个汉字已远超实际需要，同时保证不会被截断成半截字。
        /// </para>
        /// </summary>
        public const int MaxLength = 40;

        /// <summary>
        /// 清洗用户输入：去首尾空白、**换行与制表符压成空格**、按 <see cref="MaxLength"/> 截断。
        ///
        /// <para>
        /// ⚠️ **必须处理换行**：这个值要写进 <c>TextBlock</c> 与窗口标题，其中若含 <c>\r\n</c>，
        /// 标题栏那行会变成两行、把 48px 的标题栏撑变形（粘贴多行文本就会触发）。
        /// </para>
        /// <para>
        /// 返回空串表示「没有自定义标题」—— 由调用方回落到各自默认值。
        /// </para>
        /// </summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                builder.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
            }

            string cleaned = builder.ToString().Trim();

            // 连续空格压成一个：换行被替换后容易留下「a   b」这种痕迹
            var compact = new System.Text.StringBuilder(cleaned.Length);
            bool lastWasSpace = false;
            foreach (char ch in cleaned)
            {
                bool isSpace = ch == ' ';
                if (isSpace && lastWasSpace)
                {
                    continue;
                }

                compact.Append(ch);
                lastWasSpace = isSpace;
            }

            string result = compact.ToString().Trim();
            return result.Length <= MaxLength ? result : result[..MaxLength].TrimEnd();
        }

        /// <summary>
        /// 是否等于默认值（等于就该**存空串**，与 <c>LogDirectory</c> 同一不变式）。
        /// <para>
        /// 两个默认值都算：用户在框里看到的是「数据采集Demo」，若他原样敲一遍并失焦，
        /// 不该因此在 json 里留下一条和默认值相同的自定义标题。
        /// </para>
        /// </summary>
        public static bool IsDefault(string? raw)
        {
            string value = Normalize(raw);
            return value.Length == 0
                || value == DefaultCaption
                || value == DefaultWindowTitle;
        }

        /// <summary>标题栏那行字该显示什么（未自定义 → 默认）。</summary>
        public static string CaptionOf(string? saved) =>
            Normalize(saved) is { Length: > 0 } custom ? custom : DefaultCaption;

        /// <summary><c>Window.Title</c> 该用什么（未自定义 → 默认）。</summary>
        public static string WindowTitleOf(string? saved) =>
            Normalize(saved) is { Length: > 0 } custom ? custom : DefaultWindowTitle;
    }
}
