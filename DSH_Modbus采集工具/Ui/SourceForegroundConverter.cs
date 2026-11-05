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
    /// ⚠️ 这是 <see cref="IMultiValueConverter"/>（2026-11-05 由 <c>IValueConverter</c> 改来）：
    /// 早期绑的是**整行对象**（<c>DataContext</c>），整行对象的引用不变，<c>HasSourceError</c>
    /// 的 INPC 通知 WPF 不重算绑定 → 输入框颜色不跟随编辑实时变化，要导航离开再回来才刷新。
    /// 现在改成 <c>MultiBinding</c> 显式绑两条路径 <c>HasSourceError</c> 与 <c>SourceVariableName</c>，
    /// 两者都在 setter 里 Raise，WPF 收到通知即重算，颜色实时刷新。
    /// </para>
    /// <para>
    /// ⚠️ 颜色必须走 <see cref="ThemeManager.GetBrush"/>：C# 侧赋画刷不经过
    /// <c>DynamicResource</c>，换主题时要让绑定重新求值（窗口重建列表即可）。
    /// </para>
    /// </summary>
    public sealed class SourceForegroundConverter : IMultiValueConverter
    {
        /// <summary>正常状态的字色键（与主题里输入框的正文字色一致）。</summary>
        private const string NormalKey = "Brush.Text.Primary";

        /// <summary>合法常量 → 橙。</summary>
        private const string ConstantKey = "Brush.Warning";

        /// <summary>有错 → 红。</summary>
        private const string ErrorKey = "Brush.Danger";

        /// <summary>
        /// values 顺序与 <c>MainWindow.xaml</c> 里 <c>MultiBinding</c> 的子绑定一一对应：
        /// [0] = <c>HasSourceError</c>（bool），[1] = <c>SourceVariableName</c>（string）。
        /// 多绑一条就重算一次，两条都 Raise 顶多多算一拍，无正确性问题。
        /// </summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasError = values.Length > 0 && values[0] is bool b && b;
            string sourceName = values.Length > 1 ? values[1] as string ?? string.Empty : string.Empty;

            // ① 有错优先：变量名不存在、或常量语法错
            if (hasError)
            {
                return Get(ErrorKey);
            }

            // ② 合法常量（# 开头且解析成功 —— 有错已在前一拍拦下）
            if (AcquisitionConstant.IsConstant(sourceName))
            {
                return Get(ConstantKey);
            }

            // ③ 正常变量名
            return Get(NormalKey);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush Get(string key) =>
            ThemeManager.GetBrush(key) ?? Brushes.Black;
    }
}
