using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 四个零 XAML 对话框的公共基类：统一外壳、居中归属、抢焦点、控件工厂。
    ///
    /// <para>
    /// 原来每个对话框各抄一遍 <c>DialogChrome.Apply</c> + <c>RequireStyle</c> +
    /// 居中/NoResize/ShowInTaskbar + <c>Ask</c> 工厂的 owner 解析与 Loaded 抢焦点。
    /// 抽出后子类只写「自己有几行、每行放什么」。
    /// </para>
    /// </summary>
    public abstract class DialogWindow : Window
    {
        protected DialogWindow(string title, double width)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            DialogChrome.Apply(this);
        }

        /// <summary>
        /// 把 <see cref="Window.Owner"/> 设成传入窗口（或其主窗口）；都不行则居中屏幕。
        /// <para>Owner 为 null 时 <c>ShowDialog</c> 仍可用，但会失去居中参照与模态归属。</para>
        /// </summary>
        protected void AttachOwner(Window? owner)
        {
            Window? effectiveOwner = owner ?? Application.Current?.MainWindow;
            if (effectiveOwner != null && !ReferenceEquals(effectiveOwner, this) && effectiveOwner.IsLoaded)
            {
                Owner = effectiveOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>
        /// 抢回焦点并 <see cref="Window.ShowDialog"/>；返回是否点了确定。
        /// <para>
        /// 弹框必须自己抢到焦点与前台：主窗口此时可能刚关掉下拉 Popup，
        /// 若不在 Loaded 里显式激活，键盘输入会落到主窗口（实测输入丢字）。
        /// </para>
        /// </summary>
        protected bool ShowOwned(Control initialFocus)
        {
            Loaded += (_, _) =>
            {
                Activate();
                initialFocus.Focus();
                if (initialFocus is TextBox tb)
                {
                    tb.SelectAll();
                }
            };

            return ShowDialog() == true;
        }

        /// <param name="bottomMargin">
        /// 标签下边距。默认 10 配「Auto 行高 + 行内 10 下边距」的老写法；
        /// 行高固定时传 0 —— 否则标签会被这 10 顶上去，比同行控件高半个边距。
        /// </param>
        /// <returns>创建的标签（P3.1b 起返回引用：上报类型要整行隐藏，调用方好拿它设 Visibility）。</returns>
        protected TextBlock AddLabel(
            Grid root, string text, int row, bool topAlign = false, double topMargin = 4,
            double bottomMargin = 10)
        {
            var label = new TextBlock
            {
                Text = text,
                Style = RequireStyle("Dialog.Label"),
                VerticalAlignment = topAlign ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = topAlign
                    ? new Thickness(0, topMargin, 0, bottomMargin)
                    : new Thickness(0, 0, 0, bottomMargin),
            };

            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            root.Children.Add(label);
            return label;
        }

        protected static void Place(Grid root, UIElement element, int row, Thickness margin, bool stretch = false)
        {
            if (element is FrameworkElement fe)
            {
                fe.Margin = margin;
                fe.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            }

            Grid.SetRow(element, row);
            Grid.SetColumn(element, 1);
            root.Children.Add(element);
        }

        // 不写死宽度：交给 Place(..., stretch: true) 随列宽拉伸，
        // 否则 150% DPI 下整列再拉伸，控件右边框会被裁掉。
        protected TextBox MakeTextBox(string? text) => new()
        {
            Text = text ?? string.Empty,
            Style = RequireStyle("Dialog.TextBox"),
        };

        protected ComboBox MakeCombo(double width) => new()
        {
            Width = width,
            Style = RequireStyle("Dialog.ComboBox"),
        };

        protected RadioButton MakeRadio(string text, string groupName) => new()
        {
            Content = text,
            Style = RequireStyle("Dialog.RadioButton"),
            VerticalAlignment = VerticalAlignment.Center,
            GroupName = groupName,
        };

        protected Button MakeButton(string text, bool primary) => new()
        {
            Content = text,
            Style = RequireStyle(primary ? "Dialog.Button.Primary" : "Dialog.Button.Secondary"),
        };

        /// <summary>
        /// 取统一控件样式。**取不到就抛**：样式名写错会静默退化成系统默认外观
        /// （深色主题下正好表现为「浅底白字」那种几乎看不见的控件），
        /// 宁可开窗就崩，也不要让人对着一个看不见的下拉框猜。
        /// </summary>
        protected Style RequireStyle(string key) =>
            TryFindResource(key) as Style
            ?? throw new InvalidOperationException(
                $"缺少样式资源 {key}，请检查 Themes/DialogControls.xaml 是否已合并进 App.xaml");

        protected Brush? TryFindBrush(string key) => TryFindResource(key) as Brush;

        protected static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>下拉框里的一项：显示文本 + 真实取值（不要靠字符串反解）。</summary>
        protected sealed class ComboItem<T>
        {
            public ComboItem(string text, T value)
            {
                Text = text;
                Value = value;
            }

            public string Text { get; }

            public T Value { get; }

            public override string ToString() => Text;
        }
    }
}
