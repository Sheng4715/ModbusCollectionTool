using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            FontSize = 12;

            // 主窗口本身是 WindowChrome 自绘标题栏；这里用系统标题栏，
            // 保证在任务栏/Alt+Tab 里可见、也便于自动化与键盘操作。
            WindowStyle = WindowStyle.SingleBorderWindow;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            });

            _error = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            _box = new TextBox
            {
                Text = initial.ToString(CultureInfo.InvariantCulture),
                FontSize = 13,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
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
                MinWidth = 76,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };
            cancel.Click += (_, _) => { DialogResult = false; };

            var ok = new Button
            {
                Content = "确定",
                MinWidth = 76,
                Padding = new Thickness(10, 5, 10, 5),
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
