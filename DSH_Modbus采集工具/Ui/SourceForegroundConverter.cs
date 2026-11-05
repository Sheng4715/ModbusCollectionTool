using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 「来源变量」框的**文字颜色**（P3.1，2026-11-05 用户定案：常量整段橙色）。
    /// <para>
    /// 为什么用转换器而不是两条 <c>DataTrigger</c>：样式里两个 <c>DataTrigger</c>
    /// 同时成立时，WPF 的优先级取决于声明顺序，且实测（2026-11-05）
    /// <c>#"ABC</c>（既是常量、又解析失败）**两条都调过顺序仍显示橙色**，
    /// 红色出不来。转换器把三种状态在一处算完、返回一个确定颜色，
    /// 没有多触发器互相覆盖的余地。
    /// </para>
    /// <para>
    /// 三种状态（**优先级从高到低**）：
    /// <list type="number">
    /// <item>有错（红）—— 变量名查不到，或常量的语法错；</item>
    /// <item>合法常量（橙）—— <c>#</c> 开头且解析成功；</item>
    /// <item>其它（正常字色）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 绑的是**整行对象**（不是文本框里的字符串）：只有行自己知道
    /// 「这个变量名在表里到底存不存在」，光看文本判不出来。
    /// </para>
    /// <para>
    /// ⚠️ 颜色必须走 <see cref="ThemeManager.GetBrush"/>：C# 侧赋画刷不经过
    /// <c>DynamicResource</c>，换主题时要让绑定重新求值（窗口重建列表即可）。
    /// </para>
    /// </summary>
    public sealed class SourceForegroundConverter : IValueConverter
    {
        /// <summary>正常状态的字色键（与主题里输入框的正文字色一致）。</summary>
        private const string NormalKey = "Brush.Text.Primary";

        /// <summary>合法常量 → 橙。</summary>
        private const string ConstantKey = "Brush.Warning";

        /// <summary>有错 → 红。</summary>
        private const string ErrorKey = "Brush.Danger";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 期望绑的是 AcquisitionItemRow；拿不到就按正常色
            if (value is not AcquisitionItemRow row)
            {
                return Get(NormalKey);
            }

            // ① 有错优先：变量名不存在、或常量语法错
            if (row.HasSourceError)
            {
                return Get(ErrorKey);
            }

            // ② 合法常量
            if (AcquisitionConstant.IsConstant(row.SourceVariableName))
            {
                return Get(ConstantKey);
            }

            // ③ 正常变量名
            return Get(NormalKey);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static Brush Get(string key) =>
            ThemeManager.GetBrush(key) ?? Brushes.Black;
    }
}
