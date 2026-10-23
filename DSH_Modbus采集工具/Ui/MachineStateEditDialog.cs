using System.Collections.Generic;
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
        private readonly Func<IReadOnlyList<CompletionCandidate>>? _completions;
        private readonly TextBox _textBox;
        private readonly ComboBox _styleCombo;
        private readonly ComboBox _sizeCombo;
        private readonly TextBox _expressionBox;
        private readonly Border _currentSwatch;
        private readonly TextBlock _currentHex;
        private readonly Border _preview;
        private readonly TextBlock _previewText;
        private readonly TextBlock _error;
        private readonly List<(string Hex, Border Cell)> _swatches = new();

        private string _colorHex;
        private string _lastValidExpression;

        /// <summary>
        /// 正在由程序回填表达式框（校验失败后恢复旧值）。
        /// <para>
        /// 回填会触发 <c>TextChanged</c>，而 TextChanged 的职责是「用户一改字就撤掉错误提示」。
        /// 不挡住的话，刚显示出来的错误会被自己这次回填立刻擦掉 —— 实测踩过，
        /// 表现为「校验明明失败了，但界面上一个字都不显示」。
        /// </para>
        /// </summary>
        private bool _revertingExpression;

        private MachineStateEditDialog(
            MachineStateItem item,
            Func<string, bool> nameExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions)
        {
            _nameExists = nameExists;
            _completions = completions;
            _colorHex = MachineStateItem.NormalizeColor(item.Color);
            _lastValidExpression = item.Expression;

            Title = "状态编辑";
            Width = 442;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            // 统一外壳：卡片底色 + 统一字体 + 深色主题下标题栏也跟着变深
            DialogChrome.Apply(this);

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 11; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            int row = 0;

            // ---------- 实时预览 ----------
            // 早先改完字体/字号/颜色要关窗才看得到效果，选错了得重开一遍。
            // 这里放一个和画布上一样的框（圆角 6、边框 1px、深色字），改哪个都立刻反映。
            _previewText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            };

            _preview = new Border
            {
                Height = 52,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(10, 0, 10, 0),
                Child = _previewText,
            };

            Grid.SetRow(_preview, row++);
            Grid.SetColumnSpan(_preview, 2);
            root.Children.Add(_preview);

            AddLabel(root, "状态文本", row);
            _textBox = MakeTextBox(item.Text);
            _textBox.TextChanged += (_, _) => UpdatePreview();
            Place(root, _textBox, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "字体样式", row);
            _styleCombo = MakeCombo(150);
            foreach ((string text, MachineStateFontStyle value) in FontStyles)
            {
                _styleCombo.Items.Add(new ComboItem<MachineStateFontStyle>(text, value));
            }

            SelectStyle(item.FontStyle);
            _styleCombo.SelectionChanged += (_, _) => UpdatePreview();
            Place(root, _styleCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "字体大小", row);
            _sizeCombo = MakeCombo(88);
            foreach (double size in FontSizes)
            {
                _sizeCombo.Items.Add(new ComboItem<double>(
                    size.ToString(CultureInfo.InvariantCulture), size));
            }

            SelectSize(item.FontSize);
            _sizeCombo.SelectionChanged += (_, _) => UpdatePreview();
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

            // 表达式框：变量名补全（P7.1）。候选口径与 ExpressionNameExists 一致 ——
            // 校验认什么，联想就该给什么，否则会出现「联想得出来、一确定就报不存在」。
            if (_completions != null)
            {
                CompletionPopup.Attach(_expressionBox, _completions);
            }
            Place(root, _expressionBox, row++, new Thickness(0, 0, 0, 12));

            // 表达式错误**就地显示**，不再弹 MessageBox —— 系统消息框不吃主题，
            // 深色下会蹦出一块纯白的框，而且它还会抢焦点、打断「改到对为止」的节奏。
            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed,
            };

            Grid.SetRow(_error, row++);
            Grid.SetColumn(_error, 1);
            root.Children.Add(_error);

            // 挂在 _error 建好之后：一改字就把上次的错误提示撤掉（但程序回填不算）
            _expressionBox.TextChanged += (_, _) =>
            {
                if (!_revertingExpression)
                {
                    ShowError(null);
                }
            };

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

            // 注意顺序：UpdateColorUi 内部会调 UpdatePreview，
            // 所以必须在 _preview/_previewText 都构造完之后再调。
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
            Window owner,
            MachineStateItem item,
            Func<string, bool> nameExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions = null)
        {
            var dlg = new MachineStateEditDialog(item, nameExists, completions);

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
            // 补全弹窗正开着时不算真的失焦（点候选会走这里），别误判成「填完了」去校验
            string text = _expressionBox.Text;

            if (!TryValidateExpression(text, out string error))
            {
                ShowError(error);

                _revertingExpression = true;
                try
                {
                    _expressionBox.Text = _lastValidExpression;
                    _expressionBox.CaretIndex = _expressionBox.Text.Length;
                }
                finally
                {
                    _revertingExpression = false;
                }

                return;
            }

            ShowError(null);
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
                ShowError(error);
                _expressionBox.Focus();
                _expressionBox.SelectAll();
                return;
            }

            ShowError(null);
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

            UpdatePreview();
        }

        /// <summary>
        /// 把当前五项设置画到预览框上。**与画布上状态框的规则保持一致**：
        /// 圆角 6、1px 边框、填充取所选色、边框是该色压暗到 72%、框内字用深色。
        /// 预览和画布不一致的话，这个预览就只是在骗人。
        /// </summary>
        private void UpdatePreview()
        {
            Color fill = ParseColor(_colorHex);

            _preview.Background = new SolidColorBrush(fill);
            _preview.BorderBrush = new SolidColorBrush(Color.FromRgb(
                (byte)(fill.R * 0.72),
                (byte)(fill.G * 0.72),
                (byte)(fill.B * 0.72)));

            _previewText.Text = _textBox.Text;
            _previewText.FontSize = Math.Clamp(FontSizeValue, 8, 72);

            bool bold = FontStyleValue is MachineStateFontStyle.Bold or MachineStateFontStyle.BoldItalic;
            bool italic = FontStyleValue is MachineStateFontStyle.Italic or MachineStateFontStyle.BoldItalic;

            // 画布上「粗体」用的是 SemiBold 而不是 Bold（见 MachineStateView.BoxVisual）
            _previewText.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;

            // 注意：本类的 FontStyles 静态字段（下拉候选）会挡住 System.Windows.FontStyles 类型，
            // 这里必须写全名，否则编译器会去找字段上的 Normal / Italic 成员。
            _previewText.FontStyle = italic
                ? System.Windows.FontStyles.Italic
                : System.Windows.FontStyles.Normal;
        }

        /// <summary>显示/清除内联错误。</summary>
        private void ShowError(string? message)
        {
            _error.Text = message ?? string.Empty;
            _error.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
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

        private void AddLabel(Grid root, string text, int row, bool topAlign = false)
        {
            var label = new TextBlock
            {
                Text = text,
                Style = RequireStyle("Dialog.Label"),
                VerticalAlignment = topAlign ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = topAlign ? new Thickness(0, 4, 0, 10) : new Thickness(0, 0, 0, 10),
            };

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

        private TextBox MakeTextBox(string text) => new()
        {
            Text = text,
            Width = 330,
            Style = RequireStyle("Dialog.TextBox"),
        };

        private ComboBox MakeCombo(double width) => new()
        {
            Width = width,
            Style = RequireStyle("Dialog.ComboBox"),
        };

        private Button MakeButton(string text, bool primary) => new()
        {
            Content = text,
            Style = RequireStyle(primary ? "Dialog.Button.Primary" : "Dialog.Button.Secondary"),
        };

        /// <summary>
        /// 取统一控件样式。**取不到就抛**：样式名写错会静默退化成系统默认外观
        /// （深色主题下正好表现为「浅灰底白字」那种几乎看不见的控件），
        /// 宁可开窗就崩，也不要让人对着一个看不见的下拉框猜。
        /// </summary>
        private Style RequireStyle(string key) =>
            TryFindResource(key) as Style
            ?? throw new InvalidOperationException(
                $"缺少样式资源 {key}，请检查 Themes/DialogControls.xaml 是否已合并进 App.xaml");

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
