using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 单个状态的属性编辑窗（双击状态框打开）。
    /// <para>五项：状态文本 / 字体样式 / 字体大小 / 状态颜色 / 表达式；底部取消、确定。</para>
    /// <para>
    /// 与 <see cref="NumberInputDialog"/> 一样**零 XAML 自绘**，但外观走主题资源，
    /// 深色主题下不会突然蹦出一块白的。
    /// </para>
    /// </summary>
    public sealed class MachineStateEditDialog : Window
    {
        /// <summary>色盘：drawio 调色板。9 个状态的出厂色全在第二行，保证选得回来。</summary>
        private static readonly string[] Palette =
        {
            "#FFFFFF", "#E6E6E6", "#CCCCCC", "#B3B3B3", "#999999",
            "#808080", "#666666", "#4D4D4D", "#333333", "#000000",
            "#F8CECC", "#EA6B66", "#B85450", "#D5E8D4", "#82B366",
            "#DAE8FC", "#6C8EBF", "#FFF2CC", "#FFCD28", "#FFE6CC",
            "#D79B00", "#E1D5E7", "#9673A6", "#A20025", "#1BA1E2",
            "#0050EF", "#60A917", "#008A00", "#F0A30A", "#E51400",
        };

        /// <summary>字号下拉的可选值（<c>FontSize</c> 本身在 8~72 之间夹取）。</summary>
        private static readonly double[] FontSizes =
            { 8, 10, 12, 14, 15, 16, 18, 20, 24, 28 };

        private static readonly (string Text, MachineStateFontStyle Value)[] FontStyles =
        {
            ("常规", MachineStateFontStyle.Normal),
            ("粗体", MachineStateFontStyle.Bold),
            ("斜体", MachineStateFontStyle.Italic),
            ("粗斜体", MachineStateFontStyle.BoldItalic),
        };

        private readonly Func<string, bool> _nameExists;
        private readonly TextBox _textBox;
        private readonly ComboBox _styleCombo;
        private readonly ComboBox _sizeCombo;
        private readonly TextBox _expressionBox;
        private readonly Border _currentSwatch;
        private readonly TextBlock _currentHex;
        private readonly List<(string Hex, Border Cell)> _swatches = new();

        private string _colorHex;
        private string _lastValidExpression;

        private MachineStateEditDialog(
            MachineStateItem item, Func<string, bool> nameExists)
        {
            _nameExists = nameExists;
            _colorHex = MachineStateItem.NormalizeColor(item.Color);
            _lastValidExpression = item.Expression;

            Title = "状态编辑";
            Width = 442;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            FontSize = 12;
            WindowStyle = WindowStyle.SingleBorderWindow;
            SetResourceReference(BackgroundProperty, "Brush.Card.Background");

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 9; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            int row = 0;

            AddLabel(root, "状态文本", row);
            _textBox = MakeTextBox(item.Text);
            Place(root, _textBox, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "字体样式", row);
            _styleCombo = MakeCombo(150);
            foreach ((string text, MachineStateFontStyle value) in FontStyles)
            {
                _styleCombo.Items.Add(new ComboItem<MachineStateFontStyle>(text, value));
            }

            SelectStyle(item.FontStyle);
            Place(root, _styleCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "字体大小", row);
            _sizeCombo = MakeCombo(88);
            foreach (double size in FontSizes)
            {
                _sizeCombo.Items.Add(new ComboItem<double>(
                    size.ToString(CultureInfo.InvariantCulture), size));
            }

            SelectSize(item.FontSize);
            Place(root, _sizeCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "状态颜色", row, topAlign: true);
            var colorBlock = new StackPanel { Orientation = Orientation.Vertical };

            var current = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
            };

            _currentSwatch = new Border
            {
                Width = 30,
                Height = 24,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            };

            _currentHex = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _currentHex.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Muted");
            current.Children.Add(_currentSwatch);
            current.Children.Add(_currentHex);
            colorBlock.Children.Add(current);
            colorBlock.Children.Add(BuildPalette());
            Place(root, colorBlock, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "表达式", row);
            _expressionBox = MakeTextBox(item.Expression);
            _expressionBox.LostFocus += ExpressionBox_LostFocus;
            Place(root, _expressionBox, row++, new Thickness(0, 0, 0, 12));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
            };
            Grid.SetRow(buttons, row);
            Grid.SetColumnSpan(buttons, 2);

            Button cancel = MakeButton("取消", primary: false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (_, _) => DialogResult = false;

            Button ok = MakeButton("确定", primary: true);
            ok.IsDefault = true;
            ok.Click += Ok_Click;

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            Content = root;

            UpdateColorUi();
        }

        public string StateText => _textBox.Text;

        public MachineStateFontStyle FontStyleValue =>
            _styleCombo.SelectedItem is ComboItem<MachineStateFontStyle> s
                ? s.Value
                : MachineStateFontStyle.Normal;

        public double FontSizeValue =>
            _sizeCombo.SelectedItem is ComboItem<double> s ? s.Value : 15;

        public string ColorHex => _colorHex;

        public string ExpressionText => _expressionBox.Text;

        /// <summary>
        /// 弹出编辑窗；用户取消返回 <c>null</c>。
        /// <para>表达式在**关窗之前**就已经校验并通过，调用方拿到的值可以直接落盘。</para>
        /// </summary>
        public static MachineStateEditDialog? Ask(
            Window owner, MachineStateItem item, Func<string, bool> nameExists)
        {
            var dlg = new MachineStateEditDialog(item, nameExists);

            Window? effectiveOwner = owner ?? Application.Current?.MainWindow;
            if (effectiveOwner != null && !ReferenceEquals(effectiveOwner, dlg) && effectiveOwner.IsLoaded)
            {
                dlg.Owner = effectiveOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // 与 NumberInputDialog 同理：不显式激活的话，键盘输入会落到主窗口
            dlg.Loaded += (_, _) =>
            {
                dlg.Activate();
                dlg._textBox.Focus();
                dlg._textBox.SelectAll();
            };

            return dlg.ShowDialog() == true ? dlg : null;
        }

        /// <summary>
        /// 表达式失焦即校验：**不合法就弹窗，并把输入框退回上次有效值**。
        /// <para>与自由监控改名、全局变量改名同一套节奏（失焦校验 + 弹窗 + 回退）。</para>
        /// </summary>
        private void ExpressionBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string text = _expressionBox.Text;

            if (!TryValidateExpression(text, out string error))
            {
                MessageBox.Show(
                    this, error, "表达式无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                _expressionBox.Text = _lastValidExpression;
                return;
            }

            _lastValidExpression = text;
        }

        /// <summary>编译 + 变量存在性检查，一次做完。</summary>
        private bool TryValidateExpression(string text, out string error)
        {
            if (!CompiledExpression.TryParse(text, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            return compiled!.TryValidateNames(_nameExists, out error);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            // 失焦时已经校验过；这里再兜一次（例如用户没离开表达式框就按了回车）
            if (!TryValidateExpression(_expressionBox.Text, out string error))
            {
                MessageBox.Show(
                    this, error, "表达式无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                _expressionBox.Focus();
                return;
            }

            DialogResult = true;
        }

        private Grid BuildPalette()
        {
            var grid = new Grid();

            for (int i = 0; i < 10; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            for (int r = 0; r < 3; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < Palette.Length; i++)
            {
                string hex = Palette[i];
                var cell = new Border
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 4, 4),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF)),
                    Background = new SolidColorBrush(ParseColor(hex)),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                };

                cell.MouseLeftButtonDown += (_, _) =>
                {
                    _colorHex = hex;
                    UpdateColorUi();
                };

                Grid.SetRow(cell, i / 10);
                Grid.SetColumn(cell, i % 10);
                grid.Children.Add(cell);
                _swatches.Add((hex, cell));
            }

            return grid;
        }

        /// <summary>刷新「当前色」预览 + 色盘里那一圈蓝色选中框。</summary>
        private void UpdateColorUi()
        {
            _currentSwatch.Background = new SolidColorBrush(ParseColor(_colorHex));
            _currentHex.Text = _colorHex.ToUpperInvariant();

            foreach ((string hex, Border cell) in _swatches)
            {
                bool selected = string.Equals(hex, _colorHex, StringComparison.OrdinalIgnoreCase);
                cell.BorderThickness = new Thickness(selected ? 2 : 1);
                cell.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0))
                    : new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF));
            }
        }

        private void SelectStyle(MachineStateFontStyle style)
        {
            foreach (object entry in _styleCombo.Items)
            {
                if (entry is ComboItem<MachineStateFontStyle> c && c.Value == style)
                {
                    _styleCombo.SelectedItem = entry;
                    return;
                }
            }

            _styleCombo.SelectedIndex = 0;
        }

        /// <summary>当前字号不在预设列表里时**插进去**，别悄悄改成别的值。</summary>
        private void SelectSize(double size)
        {
            foreach (object entry in _sizeCombo.Items)
            {
                if (entry is ComboItem<double> c && Math.Abs(c.Value - size) < 0.01)
                {
                    _sizeCombo.SelectedItem = entry;
                    return;
                }
            }

            var item = new ComboItem<double>(
                size.ToString("0.#", CultureInfo.InvariantCulture), size);

            int index = 0;
            while (index < _sizeCombo.Items.Count
                   && _sizeCombo.Items[index] is ComboItem<double> exist
                   && exist.Value < size)
            {
                index++;
            }

            _sizeCombo.Items.Insert(index, item);
            _sizeCombo.SelectedIndex = index;
        }

        private static void AddLabel(Grid root, string text, int row, bool topAlign = false)
        {
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = topAlign ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = topAlign ? new Thickness(0, 4, 0, 10) : new Thickness(0, 0, 0, 10),
            };

            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            root.Children.Add(label);
        }

        private static void Place(Grid root, UIElement element, int row, Thickness margin)
        {
            if (element is FrameworkElement fe)
            {
                fe.Margin = margin;
                fe.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Grid.SetRow(element, row);
            Grid.SetColumn(element, 1);
            root.Children.Add(element);
        }

        private TextBox MakeTextBox(string text)
        {
            var box = new TextBox
            {
                Text = text,
                Height = 28,
                Width = 330,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };

            box.SetResourceReference(TextBox.BackgroundProperty, "Brush.Input.Background");
            box.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text.Primary");
            box.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Input.Border");
            return box;
        }

        private ComboBox MakeCombo(double width)
        {
            var combo = new ComboBox
            {
                Width = width,
                Height = 28,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            combo.SetResourceReference(ComboBox.BackgroundProperty, "Brush.Input.Background");
            combo.SetResourceReference(ComboBox.ForegroundProperty, "Brush.Text.Primary");
            combo.SetResourceReference(ComboBox.BorderBrushProperty, "Brush.Input.Border");
            return combo;
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Content = text,
                Width = 72,
                Height = 32,
                FontSize = 12,
            };

            if (primary)
            {
                button.SetResourceReference(Button.BackgroundProperty, "Brush.Accent.Fill");
                button.SetResourceReference(Button.ForegroundProperty, "Brush.Text.OnAccent");
            }
            else
            {
                button.SetResourceReference(Button.BackgroundProperty, "Brush.Control.Background");
                button.SetResourceReference(Button.ForegroundProperty, "Brush.Text.Primary");
            }

            return button;
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return Colors.White;
            }
        }

        /// <summary>下拉框里的一项：显示文本 + 真实取值（不要靠字符串反解）。</summary>
        private sealed class ComboItem<T>
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
