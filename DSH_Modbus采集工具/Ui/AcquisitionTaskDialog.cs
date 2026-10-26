using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 「任务选项」对话框（P3.1，点任务组行上的「…」打开）。
    /// <para>
    /// 四项：任务名称 / 任务类型 / 执行方式 / 表达式。按需求样例：
    /// 任务类型默认**数据读取**、执行方式默认**周期（s）**。
    /// </para>
    /// <para>
    /// 与 <see cref="MachineStateEditDialog"/> 一样零 XAML 自绘、外观走 Dialog.* 主题资源。
    /// </para>
    /// </summary>
    public sealed class AcquisitionTaskDialog : Window
    {
        private static readonly (string Text, AcquisitionTaskKind Value)[] Kinds =
        {
            ("数据读取", AcquisitionTaskKind.Read),
            ("数据写入", AcquisitionTaskKind.Write),
        };

        private readonly Func<string, bool> _taskNameExists;
        private readonly Func<string, bool> _variableExists;
        private readonly Func<IReadOnlyList<CompletionCandidate>>? _completions;
        private readonly string _originalName;

        private readonly TextBox _nameBox;
        private readonly ComboBox _kindCombo;
        private readonly RadioButton _periodRadio;
        private readonly RadioButton _edgeRadio;
        private readonly TextBox _periodBox;
        private readonly TextBlock _periodSuffix;
        private readonly TextBox _expressionBox;
        private readonly TextBlock _error;

        private AcquisitionTaskDialog(
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, bool> variableExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions)
        {
            _taskNameExists = taskNameExists;
            _variableExists = variableExists;
            _completions = completions;
            _originalName = task.Name ?? string.Empty;

            Title = "任务选项";
            Width = 442;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            DialogChrome.Apply(this);

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 8; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            int row = 0;

            AddLabel(root, "任务名称", row);
            _nameBox = MakeTextBox(task.Name);
            Place(root, _nameBox, row++, new Thickness(0, 0, 0, 10), stretch: true);

            AddLabel(root, "任务类型", row);
            _kindCombo = MakeCombo(160);
            foreach ((string text, AcquisitionTaskKind value) in Kinds)
            {
                _kindCombo.Items.Add(new ComboItem<AcquisitionTaskKind>(text, value));
            }

            SelectKind(task.Kind);
            Place(root, _kindCombo, row++, new Thickness(0, 0, 0, 10));

            AddLabel(root, "执行方式", row);

            // 周期秒数输入框**贴着周期单选框**放：需求样例里「周期（s）」的「(s)」本身就是
            // 告诉人这里要填时间，把输入框放到别处（比如另起一行）就得再补一句说明。
            _periodBox = MakeTextBox(AcquisitionTaskRules.FormatSeconds(task.PeriodSeconds));
            _periodBox.Width = 64;
            _periodBox.Margin = new Thickness(6, 0, 0, 0);
            _periodBox.TextAlignment = TextAlignment.Center;

            _periodSuffix = new TextBlock
            {
                Text = "秒",
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            _periodSuffix.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            _periodSuffix.SetResourceReference(TextBlock.FontSizeProperty, 12.0);

            _periodRadio = MakeRadio("周期（s）");
            _edgeRadio = MakeRadio("触发（上升沿有效）");

            var periodLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            periodLine.Children.Add(_periodRadio);
            periodLine.Children.Add(_periodBox);
            periodLine.Children.Add(_periodSuffix);

            var modeLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            modeLine.Children.Add(periodLine);
            _edgeRadio.Margin = new Thickness(20, 0, 0, 0);
            modeLine.Children.Add(_edgeRadio);

            Place(root, modeLine, row++, new Thickness(0, 0, 0, 10), stretch: true);

            AddLabel(root, "表达式", row);
            _expressionBox = MakeTextBox(task.Expression);

            if (_completions != null)
            {
                CompletionPopup.Attach(_expressionBox, _completions);
            }

            Place(root, _expressionBox, row++, new Thickness(0, 0, 0, 10), stretch: true);

            // 只在「触发」下可填的两项：置灰而不是隐藏 —— 隐藏会让对话框在两档之间
            // 忽高忽低，用户点一下就得重新找控件在哪。
            _periodRadio.Checked += (_, _) => SyncModeEnabled();
            _edgeRadio.Checked += (_, _) => SyncModeEnabled();

            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed,
            };

            Grid.SetRow(_error, row++);
            Grid.SetColumn(_error, 1);
            root.Children.Add(_error);

            // 一改字就把上次的错误提示撤掉（对话框里没有程序回填，不必像状态编辑窗那样加闸门）
            _expressionBox.TextChanged += (_, _) => ShowError(null);

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

            if (task.Trigger == AcquisitionTriggerMode.Edge)
            {
                _edgeRadio.IsChecked = true;
            }
            else
            {
                _periodRadio.IsChecked = true;
            }

            SyncModeEnabled();
        }

        public string TaskName => _nameBox.Text;

        public AcquisitionTaskKind KindValue =>
            _kindCombo.SelectedItem is ComboItem<AcquisitionTaskKind> c
                ? c.Value
                : AcquisitionTaskKind.Read;

        public AcquisitionTriggerMode TriggerValue =>
            _edgeRadio.IsChecked == true ? AcquisitionTriggerMode.Edge : AcquisitionTriggerMode.Periodic;

        public double PeriodSecondsValue =>
            AcquisitionTaskRules.TryParsePeriod(_periodBox.Text, out double seconds, out _)
                ? seconds
                : AcquisitionTaskRules.DefaultPeriodSeconds;

        public string ExpressionText => _expressionBox.Text;

        /// <summary>弹出对话框；用户取消返回 <c>null</c>。</summary>
        public static AcquisitionTaskDialog? Ask(
            Window owner,
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, bool> variableExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions = null)
        {
            var dlg = new AcquisitionTaskDialog(task, taskNameExists, variableExists, completions);

            Window? effectiveOwner = owner ?? Application.Current?.MainWindow;
            if (effectiveOwner != null && !ReferenceEquals(effectiveOwner, dlg) && effectiveOwner.IsLoaded)
            {
                dlg.Owner = effectiveOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Loaded += (_, _) =>
            {
                dlg.Activate();
                dlg._nameBox.Focus();
                dlg._nameBox.SelectAll();
            };

            return dlg.ShowDialog() == true ? dlg : null;
        }

        /// <summary>按当前执行方式置灰另一档的参数框。</summary>
        private void SyncModeEnabled()
        {
            bool periodic = _periodRadio.IsChecked == true;

            _periodBox.IsEnabled = periodic;
            _periodSuffix.Opacity = periodic ? 1.0 : 0.45;
            _expressionBox.IsEnabled = !periodic;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!AcquisitionTaskRules.TryValidateName(_nameBox.Text, out string nameError))
            {
                ShowError(nameError);
                return;
            }

            // 重名检查要把「自己」排除掉，否则改别的字段也会被自己挡下来
            if (!string.Equals(_nameBox.Text, _originalName, StringComparison.Ordinal)
                && _taskNameExists(_nameBox.Text))
            {
                ShowError("任务名称 " + _nameBox.Text + " 已存在");
                return;
            }

            if (TriggerValue == AcquisitionTriggerMode.Periodic)
            {
                if (!AcquisitionTaskRules.TryParsePeriod(_periodBox.Text, out _, out string periodError))
                {
                    ShowError(periodError);
                    _periodBox.Focus();
                    _periodBox.SelectAll();
                    return;
                }
            }
            else if (!TryValidateExpression(_expressionBox.Text, out string expressionError))
            {
                ShowError(expressionError);
                _expressionBox.Focus();
                _expressionBox.SelectAll();
                return;
            }

            ShowError(null);
            DialogResult = true;
        }

        /// <summary>编译 + 变量存在性检查（口径与 PLC 状态机一致）。</summary>
        private bool TryValidateExpression(string text, out string error)
        {
            if (!CompiledExpression.TryParse(text, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            // 空表达式合法（等于「永不触发」），此时没有名字要查
            if (compiled == null)
            {
                return true;
            }

            return compiled.TryValidateNames(_nameExistsOfVariable, out error);
        }

        /// <summary>
        /// 表达式里的名字**必须是变量**（系统变量或全局变量）。
        /// <para>
        /// ⚠️ 刻意与 <c>_taskNameExists</c>（任务名重名检查）分开成两个委托：
        /// 两者签名一样但语义完全不同，共用一个会写出「表达式里填任务名也算合法」的 bug。
        /// </para>
        /// </summary>
        private bool _nameExistsOfVariable(string name) =>
            SystemVariable.Find(name) != null || _variableExists(name);

        private void ShowError(string? message)
        {
            _error.Text = message ?? string.Empty;
            _error.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SelectKind(AcquisitionTaskKind kind)
        {
            foreach (object entry in _kindCombo.Items)
            {
                if (entry is ComboItem<AcquisitionTaskKind> c && c.Value == kind)
                {
                    _kindCombo.SelectedItem = entry;
                    return;
                }
            }

            _kindCombo.SelectedIndex = 0;
        }

        private void AddLabel(Grid root, string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                Style = RequireStyle("Dialog.Label"),
                Margin = new Thickness(0, 0, 0, 10),
            };

            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            root.Children.Add(label);
        }

        private static void Place(
            Grid root, UIElement element, int row, Thickness margin, bool stretch = false)
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

        private TextBox MakeTextBox(string? text) => new TextBox
        {
            Text = text ?? string.Empty,
            Style = RequireStyle("Dialog.TextBox"),
        };

        private ComboBox MakeCombo(double width) => new ComboBox
        {
            Width = width,
            Style = RequireStyle("Dialog.ComboBox"),
        };

        private RadioButton MakeRadio(string text) => new RadioButton
        {
            Content = text,
            Style = RequireStyle("Dialog.RadioButton"),
            VerticalAlignment = VerticalAlignment.Center,

            // ⚠️ **GroupName 必须显式给**：两个单选按钮分别装在两个 StackPanel 里
            // （「周期」那档还要跟着一个秒数输入框），而 WPF 默认按**逻辑父容器**分组 ——
            // 不给名字它们就各成一伙，可以同时是选中的（真机截图里两个蓝圈同亮，实测踩到）。
            GroupName = "AcqTaskTrigger",
        };

        private Button MakeButton(string text, bool primary) => new Button
        {
            Content = text,
            Style = RequireStyle(primary ? "Dialog.Button.Primary" : "Dialog.Button.Secondary"),
        };

        private Style RequireStyle(string key) =>
            TryFindResource(key) as Style
            ?? throw new InvalidOperationException(
                "缺少样式资源 " + key + "，请检查 Themes/DialogControls.xaml 是否已合并进 App.xaml");

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
