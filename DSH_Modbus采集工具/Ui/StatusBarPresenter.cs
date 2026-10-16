using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 全局底栏的呈现：左侧「状态点 + 状态文字」，右侧「连接参数」。
    /// <para>
    /// 集中在这里的三条规则：**状态色一律传浅色字面值、由本类映射到主题键**（调用点无需关心主题）、
    /// **换主题后按记住的文字重设颜色**、**每次设状态顺带刷新右侧参数**。
    /// 这三条以前散在窗口的 <c>SetStatus</c> / <c>StatusBrushKey</c> / <c>UpdateOverviewStatus</c>
    /// 三个方法里，正是「底栏文案两次漂移」的土壤。
    /// </para>
    /// <para>
    /// 右侧参数串的来源由构造时传入的 <c>detailText</c> 决定（窗口那边读表单控件），
    /// 本类因此不认识任何表单控件，也不认识连接状态机。
    /// </para>
    /// </summary>
    public sealed class StatusBarPresenter
    {
        private const string InitialText = "未连接";
        private const string InitialColorHex = "#9A9A9A";

        private readonly Ellipse _dot;
        private readonly TextBlock _text;
        private readonly TextBlock _detail;
        private readonly Func<string?> _detailText;

        private string _lastText = InitialText;
        private string _lastColorHex = InitialColorHex;

        public StatusBarPresenter(Ellipse dot, TextBlock text, TextBlock detail, Func<string?> detailText)
        {
            _dot = dot;
            _text = text;
            _detail = detail;
            _detailText = detailText;
        }

        /// <summary>
        /// 设置状态文字与状态点颜色。<paramref name="colorHex"/> 是**浅色主题下的字面值**，
        /// 由 <see cref="BrushKey"/> 映射到语义画刷键 —— 这样调用点无需关心主题，
        /// 深色主题下也不会拿到浅色主题的固定色。
        /// </summary>
        public void Set(string text, string colorHex)
        {
            _text.Text = text;

            var brush = ThemeManager.GetBrush(BrushKey(colorHex))
                        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!);
            _text.Foreground = brush;
            _dot.Fill = brush;

            _lastText = text;
            _lastColorHex = colorHex;

            RefreshDetail();
        }

        /// <summary>换主题后按记住的状态重设颜色（文字不变）。</summary>
        public void ReapplyTheme() => Set(_lastText, _lastColorHex);

        /// <summary>
        /// 刷新右侧连接参数。<c>detailText</c> 返回 <c>null</c> 表示"控件尚未就绪"，
        /// 此时保持原样而不是清空 —— 否则设计器/初始化早期会闪一下空白。
        /// </summary>
        public void RefreshDetail()
        {
            string? detail = _detailText();
            if (detail != null)
            {
                _detail.Text = detail;
            }
        }

        /// <summary>
        /// 状态色字面值 → 语义画刷键。三个出口与颜色语义一致：
        /// 红＝配置要改、琥珀＝通讯侧的事、绿＝正常、灰＝未连接。
        /// </summary>
        private static string BrushKey(string colorHex) => colorHex switch
        {
            "#0B7A0B" => "Brush.Success",
            "#C42B1C" => "Brush.Danger",
            "#B26A00" => "Brush.Warning",
            _ => "Brush.Indicator.Off",
        };
    }
}
