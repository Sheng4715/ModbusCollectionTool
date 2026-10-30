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
    public sealed class AcquisitionTaskDialog : DialogWindow
    {
        private static readonly (string Text, AcquisitionTaskKind Value)[] Kinds =
        {
            ("数据读取", AcquisitionTaskKind.Read),
            ("数据写入", AcquisitionTaskKind.Write),
        };

        /// <summary>
        /// 七个表单行（名称 / 类型 / 日志记录 / 数据记录 / 保存路径 / 执行方式 / 表达式）的**统一行高**。
        /// <para>
        /// ⚠️ 必须固定。原先用自动行高 + 每行 10 下边距，行距 = 内容高 + 10，于是
        /// 文本框行 40、单选行 26、路径行（按钮 32）42 —— 实测 150% DPI 下
        /// 行距是 60/49/39/51/62/60 px，肉眼看就是「每行间距都不一样」。
        /// 对齐要看的是**行与行等距**，不是每块内容各留一样的缝。
        /// </para>
        /// <para>
        /// 取 40 而不是更小：这一行里最高的内容就是 32 高的对话框按钮（保存路径行的「浏览…」），
        /// 再留 4 上下的呼吸。单选行 16、下拉 30、文本框 30 都在这个壳里垂直居中。
        /// </para>
        /// </summary>
        private const double FormRowHeight = 40;

        /// <summary>固定行高的行数（七项表单）。</summary>
        private const int FormRowCount = 7;

        /// <summary>
        /// 总行数：七个表单行 + 错误提示行 + 按钮行。
        /// <para>后两行仍是自动高度 —— 错误行大多数时候是折叠的，按钮行不该被撑到 40。</para>
        /// </summary>
        private const int RowCount = FormRowCount + 2;

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
        private readonly RadioButton _logOffRadio;
        private readonly RadioButton _logOnRadio;
        private readonly RadioButton _recordOffRadio;
        private readonly RadioButton _recordOnRadio;
        private readonly TextBox _pathBox;
        private readonly Button _browseButton;
        private readonly TextBlock _error;

        private AcquisitionTaskDialog(
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, bool> variableExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions)
            : base("任务选项", 442)
        {
            _taskNameExists = taskNameExists;
            _variableExists = variableExists;
            _completions = completions;
            _originalName = task.Name ?? string.Empty;

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 七个表单行**固定行高 40**，标签与控件都垂直居中。
            // ⚠️ 原来用自动行高 + 每行 10 下边距，于是行距 = 内容高 + 10：
            // 文本框 30 → 40、单选行 16 → 26、路径行的按钮 32 → 42、下拉框 30 → 40。
            // 实测（150% DPI）行距就是 60/49/39/51/62/60 px 这种参差 —— 而「对齐」
            // 靠的恰恰是**行与行等距**，不是每块内容自己留一样的缝。
            // 改成固定行高后，行距与内容高脱钩：单选行不再被压扁、路径行不再被撑高。
            for (int i = 0; i < RowCount; i++)
            {
                bool isFormRow = i < FormRowCount;
                root.RowDefinitions.Add(new RowDefinition
                {
                    Height = isFormRow ? new GridLength(FormRowHeight) : GridLength.Auto,
                });
            }

            int row = 0;

            AddLabel(root, "任务名称", row, bottomMargin: 0);
            _nameBox = MakeTextBox(task.Name);
            Place(root, _nameBox, row++, new Thickness(0), stretch: true);

            AddLabel(root, "任务类型", row, bottomMargin: 0);
            _kindCombo = MakeCombo(160);
            foreach ((string text, AcquisitionTaskKind value) in Kinds)
            {
                _kindCombo.Items.Add(new ComboItem<AcquisitionTaskKind>(text, value));
            }

            SelectKind(task.Kind);
            Place(root, _kindCombo, row++, new Thickness(0));

            AddLabel(root, "日志记录", row, bottomMargin: 0);

            // 与下面「数据记录」同一形态：两个单选，扫一眼就是二选一。
            // ⚠️ 两组单选的 GroupName 必须不同，否则会互相取消选中。
            _logOffRadio = MakeRadio("不启用", "AcqTaskLog");
            _logOnRadio = MakeRadio("启用", "AcqTaskLog");

            var logLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            logLine.Children.Add(_logOffRadio);
            _logOnRadio.Margin = new Thickness(20, 0, 0, 0);
            logLine.Children.Add(_logOnRadio);

            Place(root, logLine, row++, new Thickness(0), stretch: true);

            AddLabel(root, "数据记录", row, bottomMargin: 0);

            // 「记录不记录」用两个单选而不是一个开关：与下面「执行方式」的形态一致，
            // 用户扫一眼就是一组二选一，不用先判断这个开关是「开」还是「关」。
            _recordOffRadio = MakeRadio("不启用", "AcqTaskRecord");
            _recordOnRadio = MakeRadio("启用", "AcqTaskRecord");

            var recordLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            recordLine.Children.Add(_recordOffRadio);
            _recordOnRadio.Margin = new Thickness(20, 0, 0, 0);
            recordLine.Children.Add(_recordOnRadio);

            Place(root, recordLine, row++, new Thickness(0), stretch: true);

            // 路径单独一行：路径本身很长，和单选挤在一行会把路径框压得只剩几十像素
            AddLabel(root, "保存路径", row, bottomMargin: 0);

            var pathLine = new Grid();
            pathLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _pathBox = MakeTextBox(task.SavePath);
            _pathBox.ToolTip =
                "留空 = " + AcquisitionTaskRules.DefaultSavePath
                + "\n记录文件写在该目录下、以任务组名称命名的子文件夹里";
            Grid.SetColumn(_pathBox, 0);
            pathLine.Children.Add(_pathBox);

            // 空路径时给一句浅色占位：框里空空如也、又看不出默认值存到哪儿，
            // 用户只能去悬停猜。占位文字不参与取值（SavePathValue 读的是 Text）。
            var pathPlaceholder = new TextBlock
            {
                Text = "留空则存到 " + AcquisitionTaskRules.DefaultSavePath,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = task.SavePath.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            pathPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Muted");
            pathPlaceholder.SetResourceReference(TextBlock.FontSizeProperty, 12.0);
            Grid.SetColumn(pathPlaceholder, 0);
            pathLine.Children.Add(pathPlaceholder);

            _pathBox.TextChanged += (_, _) =>
            {
                pathPlaceholder.Visibility = _pathBox.Text.Length == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };

            _browseButton = MakeButton("浏览…", primary: false);
            _browseButton.Margin = new Thickness(8, 0, 0, 0);
            _browseButton.Click += Browse_Click;
            Grid.SetColumn(_browseButton, 1);
            pathLine.Children.Add(_browseButton);

            Place(root, pathLine, row++, new Thickness(0), stretch: true);

            AddLabel(root, "执行方式", row, bottomMargin: 0);

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

            _periodRadio = MakeRadio("周期（s）", "AcqTaskTrigger");
            _edgeRadio = MakeRadio("触发（上升沿有效）", "AcqTaskTrigger");

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

            Place(root, modeLine, row++, new Thickness(0), stretch: true);

            AddLabel(root, "表达式", row, bottomMargin: 0);
            _expressionBox = MakeTextBox(task.Expression);

            if (_completions != null)
            {
                CompletionPopup.Attach(_expressionBox, _completions);
            }

            Place(root, _expressionBox, row++, new Thickness(0), stretch: true);

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

            _recordOnRadio.Checked += (_, _) => SyncRecordEnabled();
            _recordOffRadio.Checked += (_, _) => SyncRecordEnabled();

            if (task.Trigger == AcquisitionTriggerMode.Edge)
            {
                _edgeRadio.IsChecked = true;
            }
            else
            {
                _periodRadio.IsChecked = true;
            }

            // 日志记录默认**开**：新建的任务组（LogEnabled 默认 true）进来就是「启用」
            if (task.LogEnabled)
            {
                _logOnRadio.IsChecked = true;
            }
            else
            {
                _logOffRadio.IsChecked = true;
            }

            if (task.RecordEnabled)
            {
                _recordOnRadio.IsChecked = true;
            }
            else
            {
                _recordOffRadio.IsChecked = true;
            }

            SyncModeEnabled();
            SyncRecordEnabled();
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

        /// <summary>日志记录是否启用（在运行日志里记一行）。默认开。</summary>
        public bool LogEnabledValue => _logOnRadio.IsChecked == true;

        /// <summary>数据记录是否启用</summary>
        public bool RecordEnabledValue => _recordOnRadio.IsChecked == true;

        /// <summary>保存路径（父目录）。空串 = 用默认的 DeviceData。</summary>
        public string SavePathValue => _pathBox.Text.Trim();

        /// <summary>弹出对话框；用户取消返回 <c>null</c>。</summary>
        public static AcquisitionTaskDialog? Ask(
            Window owner,
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, bool> variableExists,
            Func<IReadOnlyList<CompletionCandidate>>? completions = null)
        {
            var dlg = new AcquisitionTaskDialog(task, taskNameExists, variableExists, completions);

            dlg.AttachOwner(owner);

            return dlg.ShowOwned(dlg._nameBox) ? dlg : null;
        }

        /// <summary>
        /// 「不启用」时置灰路径框与浏览按钮。
        /// <para>置灰而非隐藏：与「执行方式」同一处理，避免对话框高度忽高忽低。</para>
        /// </summary>
        private void SyncRecordEnabled()
        {
            bool on = _recordOnRadio.IsChecked == true;

            _pathBox.IsEnabled = on;
            _browseButton.IsEnabled = on;
            _pathBox.Opacity = on ? 1.0 : 0.45;
        }

        /// <summary>
        /// 选保存目录。用 .NET 8 自带的 <see cref="Microsoft.Win32.OpenFolderDialog"/>，
        /// **不引 NuGet** —— 项目零依赖的约束对文件对话框同样成立。
        /// </summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择数据记录保存目录",
                Multiselect = false,
            };

            string current = _pathBox.Text.Trim();
            if (current.Length > 0 && System.IO.Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }

            if (dialog.ShowDialog(this) == true)
            {
                _pathBox.Text = dialog.FolderName;
            }
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

            // 路径留空是合法的（走默认 DeviceData），但填了就得是个能用的路径 ——
            // 等点「启用」之后才发现写不进去，那时人已经离开这个对话框了
            if (RecordEnabledValue)
            {
                string path = SavePathValue;
                if (path.Length > 0 && !IsUsableFolder(path, out string pathError))
                {
                    ShowError(pathError);
                    _pathBox.Focus();
                    _pathBox.SelectAll();
                    return;
                }
            }

            ShowError(null);
            DialogResult = true;
        }

        /// <summary>
        /// 路径能不能当保存目录用：非法字符、已存在但不是目录、父目录不存在且无法创建。
        /// <para>⚠️ 只**探测**不创建 —— 对话框里点「取消」不该在磁盘上留下空文件夹。</para>
        /// </summary>
        private static bool IsUsableFolder(string path, out string error)
        {
            error = string.Empty;

            try
            {
                if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                {
                    error = "保存路径含有非法字符";
                    return false;
                }

                if (System.IO.File.Exists(path))
                {
                    error = "保存路径指向的是一个文件，不是文件夹";
                    return false;
                }

                if (System.IO.Directory.Exists(path))
                {
                    return true;
                }

                // 不存在：看它的父目录在不在（父目录也不在就不再往下追，直接让人自己建）
                string? parent = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || !System.IO.Directory.Exists(parent))
                {
                    error = "保存路径的上级目录不存在：" + (parent ?? path);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "保存路径无效：" + ex.Message;
                return false;
            }
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

    }
}
