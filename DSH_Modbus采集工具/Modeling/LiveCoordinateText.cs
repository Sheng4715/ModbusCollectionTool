using System;
using System.Globalization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 实时卡片坐标输入框的文本规则（P1.4，2026-11-03 从 UI 里抽出来）。
    /// <para>
    /// **纯逻辑、不含任何 WPF 类型** —— 因此能被独立工程链接源码做断言。
    /// 抽出来的原因：这两条规则（「哪些字符可以打进去」「空串怎么解析」）
    /// 直接决定用户能不能输入非法值，只有能离线跑断言才验证得了 ——
    /// 而验证它们需要的键盘交互在自动化环境里做不到。
    /// </para>
    /// <para>
    /// 用户定案：坐标**只到整数**，且从**输入上就挡住非法字符**
    /// （取代「先让输、点确定时再红字报错」）。
    /// </para>
    /// </summary>
    public static class LiveCoordinateText
    {
        /// <summary>
        /// 该文本是否「可以继续编辑的整数串」。
        /// <para>
        /// 合法形态只有三种：**空串**（清空重打）、**单独的 `-`**（准备打负数）、
        /// **可选负号 + 纯数字**。小数点、字母、空格、多个负号一律不合法。
        /// </para>
        /// <para>
        /// ⚠️ 不要写成「这个字符是不是数字」—— 单独的 `-` 也必须放行，
        /// 否则用户永远打不出负数（先打 `-` 就被拦掉了）。
        /// </para>
        /// </summary>
        public static bool IsAcceptableIntegerText(string? text)
        {
            string value = text ?? string.Empty;

            if (value.Length == 0)
            {
                return true;
            }

            int index = value[0] == '-' ? 1 : 0;

            if (index == value.Length)
            {
                // 只有一个负号：合法的中间状态，等用户继续打数字
                return true;
            }

            for (int i = index; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 把「用户可能留在框里的文本」解析成坐标值。
        /// <para>
        /// 空串 / 只有负号 / 解析失败 → 0。**不报错**：输入阶段已经把非法字符挡住了，
        /// 走到这里还失败说明是代码问题，静默按 0 处理比给用户看一句技术性红字更合适。
        /// </para>
        /// </summary>
        public static double Parse(string? text)
        {
            string trimmed = (text ?? string.Empty).Trim();

            if (trimmed.Length == 0 || trimmed == "-")
            {
                return 0;
            }

            return int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// 把模型里的坐标（可能是拖动留下的 <c>-180.54</c>）转成框里显示的文本。
        /// <para>
        /// **四舍五入到整数**：输入框只接受整数，显示小数会让人以为要保留精度。
        /// 未放置过（null）显示 0。
        /// </para>
        /// </summary>
        public static string Format(double? value) =>
            Math.Round(value.GetValueOrDefault()).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// 模拟「把 <paramref name="input"/> 插到当前选区的 [start, start+length) 之后」
        /// 得到的整串文本。供预览输入事件判断「替换后是否仍合法」。
        /// </summary>
        public static string SimulateInput(
            string? current, int selectionStart, int selectionLength, string? input)
        {
            string text = current ?? string.Empty;
            string inserted = input ?? string.Empty;

            int start = selectionStart;
            if (start < 0 || start > text.Length)
            {
                start = text.Length;
            }

            int length = selectionLength;
            if (length < 0 || start + length > text.Length)
            {
                length = 0;
            }

            return text.Substring(0, start) + inserted + text.Substring(start + length);
        }
    }
}
