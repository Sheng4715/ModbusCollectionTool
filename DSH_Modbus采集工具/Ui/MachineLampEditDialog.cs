using System;
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
    /// 单个指示灯的属性编辑窗（双击画布上的灯打开）。
    /// <para>
    /// 五项：显示文字 / 文字大小 / 灯大小 / 灯颜色 / 表达式；底部取消、确定。
    /// </para>
    /// <para>
    /// 与 <see cref="MachineStateEditDialog"/> 同一套做法：**零 XAML 自绘**、
    /// 外观走 <c>Dialog.*</c> 主题资源、错误就地显示（不弹 MessageBox）、
    /// 确定之前表达式已经校验通过。
    /// </para>
    /// <para>
    /// 为什么单独一个窗而不是复用状态编辑窗：灯没有字体样式（永远常规体），
    /// 也**只有点亮这一种颜色** —— 灭态一律主题灰，不给配色余地，
    /// 否则「灭」会被配得像「亮」。把用不上的那几项摆在那里只会让人以为能改。
    /// </para>
    /// <para>
    /// 点亮色**必须是一个具体颜色**，没有「跟随主题」这一档（用户 2026-10-28 要求删掉）：
    /// 用户改过的颜色换主题就变，会让人以为设置没生效。
    /// </para>
    /// </summary>
    public sealed class MachineLampEditDialog : DialogWindow
    {

        /// <summary>圆点直径候选（模型允许 6..28，这里取整数步长）。</summary>
        private static readonly double[] DotSizes = { 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28 };

        private readonly Func<string, string?> _nameError;
        private readonly Func<string, ExpressionVariableType>? _typeOf;
        private readonly Func<IReadOnlyList<CompletionCandidate>>? _completions;
        private readonly TextBox _textBox;
        private readonly TextBox _expressionBox;
        private readonly TextBlock _error;

        private readonly Border _previewDot;
        private readonly TextBlock _previewText;

        private readonly ComboBox _fontSizeCombo;
        private readonly ComboBox _dotSizeCombo;

        private readonly Border _currentSwatch;
        private readonly TextBlock _currentHex;
        private readonly List<(string Hex, Border Cell)> _swatches = new();

        /// <summary>当前选中的点亮色；永远是一个具体的 <c>#RRGGBB</c>。</summary>
        private string _lampColor;

        private string _lastValidExpression;
        private bool _revertingExpression;

        private MachineLampEditDialog(
            MachineStateLamp lamp,
            Func<string, string?> nameError,
            Func<IReadOnlyList<CompletionCandidate>>? completions,
            Func<string, ExpressionVariableType>? typeOf)
            : base("指示灯编辑", 442)
        {
            _nameError = nameError;
            _typeOf = typeOf;
            _completions = completions;
            _lastValidExpression = lamp.Expression;
            _lampColor = MachineStateLamp.NormalizeColor(lamp.Color);

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 8; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            int row = 0;

            // ---------- 实时预览：与画布上一样的一颗灯 ----------
            _previewDot = new Border
            {
                Width = lamp.DotSize,
                Height = lamp.DotSize,
                CornerRadius = new CornerRadius(lamp.DotSize / 2),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            _previewText = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = lamp.FontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _previewText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");

            var previewRow = new StackPanel { Orientation = Orientation.Horizontal };
            previewRow.Children.Add(_previewDot);
            previewRow.Children.Add(_previewText);

            var preview = new Border
            {
                Height = 52,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(14, 0, 10, 0),
                Background = TryFindBrush("Brush.Card.Background"),
                BorderBrush = TryFindBrush("Brush.MachineState.Inactive.Border") ?? Brushes.Gray,
                Child = previewRow,
            };

            Grid.SetRow(preview, row++);
            Grid.SetColumnSpan(preview, 2);
            root.Children.Add(preview);

            AddLabel(root, "显示文字", row);
            _textBox = MakeTextBox(lamp.Text);
            _textBox.TextChanged += (_, _) => UpdatePreview();
            Place(root, _textBox, row++, new Thickness(0, 0, 0, 10), stretch: true);

            AddLabel(root, "文字大小", row);
            _fontSizeCombo = MakeCombo(88);
            foreach (double size in ColorPalette.FontSizes)
            {
                _fontSizeCombo.Items.Add(new ComboItem<double>(
                    size.ToString(CultureInfo.InvariantCulture), size));
            }

            SelectSize(_fontSizeCombo, lamp.FontSize);
            _fontSizeCombo.SelectionChanged += (_, _) => UpdatePreview();
            Place(root, _fontSizeCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "灯大小", row);
            _dotSizeCombo = MakeCombo(88);
            foreach (double size in DotSizes)
            {
                _dotSizeCombo.Items.Add(new ComboItem<double>(
                    size.ToString(CultureInfo.InvariantCulture), size));
            }

            SelectSize(_dotSizeCombo, lamp.DotSize);
            _dotSizeCombo.SelectionChanged += (_, _) => UpdatePreview();
            Place(root, _dotSizeCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "灯颜色", row, topAlign: true, topMargin: 6);
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
            _expressionBox = MakeTextBox(lamp.Expression);
            _expressionBox.LostFocus += ExpressionBox_LostFocus;

            if (_completions != null)
            {
                CompletionPopup.Attach(_expressionBox, _completions);
            }

            Place(root, _expressionBox, row++, new Thickness(0, 0, 0, 12), stretch: true);

            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed,
            };

            Grid.SetRow(_error, row++);
            Grid.SetColumn(_error, 1);
            root.Children.Add(_error);

            // 与状态编辑窗同一个坑：程序回填表达式时要挡住 TextChanged 擦掉错误提示
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
            UpdateColorUi();
        }

        /// <summary>灯右边显示的文字。</summary>
        public string LampText => _textBox.Text;

        public string ExpressionText => _expressionBox.Text;

        /// <summary>点亮色；永远是一个具体的 <c>#RRGGBB</c>。</summary>
        public string LampColor => _lampColor;

        public double DotSizeValue =>
            _dotSizeCombo.SelectedItem is ComboItem<double> s ? s.Value : MachineStateLayout.LampDotSize;

        public double FontSizeValue =>
            _fontSizeCombo.SelectedItem is ComboItem<double> s ? s.Value : MachineStateLayout.LampFontSize;

        /// <summary>弹出编辑窗；用户取消返回 <c>null</c>。</summary>
        public static MachineLampEditDialog? Ask(
            Window owner,
            MachineStateLamp lamp,
            Func<string, string?> nameError,
            Func<IReadOnlyList<CompletionCandidate>>? completions = null,
            Func<string, ExpressionVariableType>? typeOf = null)
        {
            var dlg = new MachineLampEditDialog(lamp, nameError, completions, typeOf);

            dlg.AttachOwner(owner);

            return dlg.ShowOwned(dlg._textBox) ? dlg : null;
        }

        private void ExpressionBox_LostFocus(object sender, RoutedEventArgs e)
        {
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

        /// <summary>编译 + 变量存在性检查，一次做完（口径与状态框完全一致）。</summary>
        /// <summary>编译 + 变量存在性检查，一次做完（口径与状态编辑窗完全一致）。</summary>
        private bool TryValidateExpression(string text, out string error)
        {
            if (!CompiledExpression.TryParse(text, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            if (!compiled!.TryValidateNames(_nameError, out error))
            {
                return false;
            }

            // 2026-11-11：结果类型也必须能推成 BOOL（详见 CompiledExpression.TryValidateResultType）
            return _typeOf == null || compiled.TryValidateResultType(_typeOf, out error);
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

        /// <summary>
        /// 把当前四项外观画到预览框上。
        /// <para>
        /// **预览永远画「点亮」的样子** —— 颜色本来就只在点亮时可见，画成灭态就看不见改的效果了。
        /// 点亮色与画布上完全一致（同一套 <see cref="ParseColor"/>）。
        /// </para>
        /// </summary>
        private void UpdatePreview()
        {
            _previewText.Text = _textBox.Text;
            _previewText.FontSize = FontSizeValue;

            double dot = DotSizeValue;
            _previewDot.Width = dot;
            _previewDot.Height = dot;
            _previewDot.CornerRadius = new CornerRadius(dot / 2);

            _previewDot.Background = new SolidColorBrush(ParseColor(_lampColor, Color.FromRgb(0x0B, 0x7A, 0x0B)));
        }

        private void ShowError(string? message)
        {
            _error.Text = message ?? string.Empty;
            _error.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>色盘：30 个 drawio 原色，10 列 × 3 行。</summary>
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

            for (int i = 0; i < ColorPalette.Colors.Length; i++)
            {
                string hex = ColorPalette.Colors[i];
                var cell = new Border
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 4, 4),
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF)),
                    Background = new SolidColorBrush(ParseColor(hex, Color.FromRgb(0x0B, 0x7A, 0x0B))),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                };

                cell.MouseLeftButtonDown += (_, _) =>
                {
                    _lampColor = hex;
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
            _currentSwatch.Background = new SolidColorBrush(ParseColor(_lampColor, Color.FromRgb(0x0B, 0x7A, 0x0B)));
            _currentHex.Text = _lampColor.ToUpperInvariant();

            foreach ((string hex, Border cell) in _swatches)
            {
                bool selected = string.Equals(hex, _lampColor, StringComparison.OrdinalIgnoreCase);
                cell.BorderThickness = new Thickness(selected ? 2 : 1);
                cell.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0))
                    : new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF));
            }

            UpdatePreview();
        }

        /// <summary>
        /// 把模型里的尺寸放进下拉框；**找不到就插进去**（按值升序），
        /// 不要静默改成别的值 —— 那等于悄悄改了用户的设置。
        /// </summary>
        private static void SelectSize(ComboBox combo, double value)
        {
            int insertAt = 0;

            foreach (object obj in combo.Items)
            {
                if (obj is not ComboItem<double> item)
                {
                    continue;
                }

                if (Math.Abs(item.Value - value) < 0.001)
                {
                    combo.SelectedItem = item;
                    return;
                }

                if (item.Value < value)
                {
                    insertAt = combo.Items.IndexOf(item) + 1;
                }
            }

            var extra = new ComboItem<double>(
                value.ToString("0.##", CultureInfo.InvariantCulture), value);
            combo.Items.Insert(insertAt, extra);
            combo.SelectedItem = extra;
        }

    }
}
