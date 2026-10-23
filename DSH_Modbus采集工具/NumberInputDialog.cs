using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DSH_Modbus采集工具.Ui;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 单值整数输入对话框（用于 STRING 长度）。零依赖自绘，样式与主窗口一致。
    /// 只接受 <c>[Min, Max]</c> 内的整数；确定前不关闭。
    /// </summary>
    public sealed class NumberInputDialog : Window
    {
        private readonly TextBox _box;
        private readonly TextBlock _error;
        private int _value;

        public int Value => _value;

        private NumberInputDialog(string title, string prompt, int initial, int min, int max)
        {
            Min = min;
            Max = max;

            Title = title;
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            // 统一外壳：卡片底色 + 统一字体 + 深色主题下标题栏也跟着变深。
            // 早先这里是写死的 Background = Brushes.White，深色主题下会蹦出一整块白。
            DialogChrome.Apply(this);

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Style = RequireStyle("Dialog.Label"),
            });

            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            _box = new TextBox
            {
                Text = initial.ToString(CultureInfo.InvariantCulture),
                Style = RequireStyle("Dialog.TextBox"),
            };
            _box.SelectAll();
            _box.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;

            root.Children.Add(_box);
            root.Children.Add(_error);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };

            var cancel = new Button
            {
                Content = "取消",
                Style = RequireStyle("Dialog.Button.Secondary"),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };
            cancel.Click += (_, _) => { DialogResult = false; };

            var ok = new Button
            {
                Content = "确定",
                Style = RequireStyle("Dialog.Button.Primary"),
                IsDefault = true,
            };
            ok.Click += Ok_Click;

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = root;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string text = _box.Text.Trim();

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
            {
                ShowError($"请输入 {Min} ~ {Max} 之间的整数。");
                return;
            }

            if (v < Min || v > Max)
            {
                ShowError($"长度需在 {Min} ~ {Max} 之间");
                return;
            }

            _value = v;
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            _error.Text = message;
            _error.Visibility = Visibility.Visible;
            _box.Focus();
            _box.SelectAll();
        }

        /// <summary>
        /// 取统一控件样式。**取不到就抛**：样式名写错会静默退化成系统默认外观，
        /// 在深色主题下正好表现为「浅底浅字」那种几乎看不见的控件。
        /// </summary>
        private Style RequireStyle(string key) =>
            TryFindResource(key) as Style
            ?? throw new InvalidOperationException(
                $"缺少样式资源 {key}，请检查 Themes/DialogControls.xaml 是否已合并进 App.xaml");

        public int Min { get; }

        public int Max { get; }

        /// <summary>弹出对话框；用户取消或输入非法时返回 <c>null</c>。</summary>
        public static int? Ask(Window owner, string title, string prompt, int initial, int min, int max)
        {
            var dlg = new NumberInputDialog(title, prompt, initial, min, max);

            // Owner 为 null 时 ShowDialog 仍可用，但会失去居中参照与模态归属；
            // 这里兜底成当前主窗口，避免调用点传 null 时退化成非模态观感。
            Window? effectiveOwner = owner ?? Application.Current?.MainWindow;
            if (effectiveOwner != null && !ReferenceEquals(effectiveOwner, dlg) && effectiveOwner.IsLoaded)
            {
                dlg.Owner = effectiveOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // 弹框必须自己抢到焦点与前台：主窗口此时可能刚关掉下拉 Popup，
            // 若不在 Loaded 里显式激活，键盘输入会落到主窗口（实测输入丢字）。
            dlg.Loaded += (_, _) =>
            {
                dlg.Activate();
                dlg._box.Focus();
                dlg._box.SelectAll();
            };

            return dlg.ShowDialog() == true ? dlg.Value : null;
        }
    }
}
