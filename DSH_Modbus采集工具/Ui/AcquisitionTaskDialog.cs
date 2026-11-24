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
            ("HTTP数据上报", AcquisitionTaskKind.Report),
        };

        /// <summary>
        /// 九个表单行（名称 / 类型 / 日志记录 / 数据记录 / 保存路径 / 执行方式 / 表达式 /
        /// 实时背景图 / 执行完成输出变量）的**统一行高**。
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

        /// <summary>
        /// 固定行高的表单行数：名称 / 类型 / 日志记录 / 数据记录 / 保存路径 / 执行方式 / 表达式 /
        /// 实时背景图 / 执行完成输出变量。
        /// <para>
        /// ⚠️ **加一个表单行就要把它 +1**：这个常量决定前几行用固定 <see cref="FormRowHeight"/>，
        /// 其余行（提示、错误、按钮）用 Auto。漏改的后果是新增那行被建成 Auto 高
        /// （内容多高就多高）、比其它行矮一截，且按钮行落到网格定义之外
        /// （实测「实时背景图」加进来时漏改过：那一行高 32 而非 40，按钮行越界）。
        /// </para>
        /// </summary>
        private const int FormRowCount = 9;

        /// <summary>
        /// 总行数：九个表单行 + 执行完成输出变量的说明行 + 错误提示行 + 按钮行。
        /// <para>后三行是自动高度 —— 说明行与非错误态的提示行不该被撑到 40。</para>
        /// </summary>
        private const int RowCount = FormRowCount + 3;

        private readonly Func<string, bool> _taskNameExists;
        private readonly Func<string, GlobalVariable?> _findVariable;
        private readonly Func<IReadOnlyList<CompletionCandidate>>? _completions;
        private readonly Func<IReadOnlyList<CompletionCandidate>>? _variableCompletions;
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

        /// <summary>实时背景图（相对 exe 的路径）；只读，走「选择…」改。</summary>
        private readonly TextBox _liveImageBox;

        /// <summary>执行完成输出变量框（留空 = 不写结果）。</summary>
        private readonly TextBox _completionBox;

        /// <summary>当前选中的实时背景图相对路径（空串 = 没配）。</summary>
        private string _liveImagePath = string.Empty;
        private readonly Button _browseButton;
        private readonly TextBlock _error;

        // 「数据记录 / 保存路径 / 实时背景图」三组：HTTP数据上报（Report）时要整行隐藏。
        // ⚠️ **必须能来回切**（2026-11-24 修）：原先只在构造时判一次，于是「对话框里把
        // 任务类型改成 HTTP数据上报」时三行不消失 —— 而需求写的是「选中它时整行隐藏」，
        // 下拉就在同一个对话框里，用户改完就该立刻看到变化。
        // 存行号 + 标签 + 内容控件，TypeChanged 时统一折叠 / 展开。
        private readonly int _recordRow;
        private readonly int _pathRow;
        private readonly int _imageRow;
        private readonly TextBlock _recordLabel;
        private readonly TextBlock _pathLabel;
        private readonly TextBlock _imageLabel;
        private readonly UIElement _recordContent;
        private readonly UIElement _pathContent;
        private readonly UIElement _imageContent;

        private AcquisitionTaskDialog(
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, GlobalVariable?> findVariable,
            Func<IReadOnlyList<CompletionCandidate>>? completions,
            Func<IReadOnlyList<CompletionCandidate>>? variableCompletions)
            : base("任务选项", 500)
        {
            _taskNameExists = taskNameExists;
            _findVariable = findVariable;
            _completions = completions;
            _variableCompletions = variableCompletions;
            _originalName = task.Name ?? string.Empty;

            var root = new Grid { Margin = new Thickness(16) };

            // 68 只够 5 个汉字；「执行完成输出变量」是 8 个字，110 起才不裁断
            // （TextBlock 没有 TextTrimming，裁了就是硬切）。
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 九个表单行**固定行高 40**，标签与控件都垂直居中。
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

            // P3.1b：HTTP数据上报不落 CSV、也不画实时背景图，所以「数据记录」
            // 「保存路径」「实时背景图」三组要**整行隐藏** —— 故这里留住标签、内容控件
            // 与行号，构造末尾按当前类型折叠一次，之后随下拉切换同步（见 SyncKindRows）。
            _recordRow = row;
            _recordLabel = AddLabel(root, "数据记录", row, bottomMargin: 0);

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
            _recordContent = recordLine;

            // 路径单独一行：路径本身很长，和单选挤在一行会把路径框压得只剩几十像素
            _pathRow = row;
            _pathLabel = AddLabel(root, "保存路径", row, bottomMargin: 0);

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
            _pathContent = pathLine;

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

            // 实时数据背景图（P1.4，2026-10-31）：**每个任务组一张**，随组切换。
            // 选了之后会**拷贝**进 exe 旁的 Images\ 并只存相对路径 ——
            // 存绝对路径的话对方机器换个盘符图就没了，而且用户完全不知道
            // （表现为实时页一片空白）。
            _imageRow = row;
            _imageLabel = AddLabel(root, "实时背景图", row, bottomMargin: 0);
            _liveImagePath = task.LiveImagePath ?? string.Empty;
            _liveImageBox = MakeTextBox(_liveImagePath)
            ;
            _liveImageBox.IsReadOnly = true;   // 只能通过「选择…」改，免得手打出一个不存在的路径
            _liveImageBox.Margin = new Thickness(0, 0, 8, 0);

            var imageLine = new Grid();
            imageLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            imageLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_liveImageBox, 0);
            imageLine.Children.Add(_liveImageBox);

            var browseImage = MakeButton("选择…", primary: false);
            browseImage.Margin = new Thickness(0);
            browseImage.Click += (_, _) => PickLiveImage();
            Grid.SetColumn(browseImage, 1);
            imageLine.Children.Add(browseImage);

            if ((task.LiveImagePath ?? string.Empty).Length > 0)
            {
                var clearImage = MakeButton("清除", primary: false);
                clearImage.Margin = new Thickness(8, 0, 0, 0);
                clearImage.Click += (_, _) =>
                {
                    _liveImagePath = string.Empty;
                    _liveImageBox.Text = string.Empty;
                };
                Grid.SetColumn(clearImage, 2);
                imageLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                imageLine.Children.Add(clearImage);
            }

            Place(root, imageLine, row++, new Thickness(0), stretch: true);
            _imageContent = imageLine;

            // 执行完成输出变量（2026-11-10）：口径照扫码器的「执行结果输出变量」——
            // 正常执行完成写 1、异常执行完成写 2，两个值固定不可配，框下用一行小字说明。
            AddLabel(root, "执行完成输出变量", row, bottomMargin: 0);
            _completionBox = MakeTextBox(task.CompletionVariableName);

            if (_variableCompletions != null)
            {
                // 与扫码器同一份候选：只补全局变量（系统变量没有地址，写不进去）。
                // 候选已由调用方按「只收整数（整条或元素）」过滤，见 2026-11-11。
                CompletionPopup.Attach(_completionBox, _variableCompletions);
            }

            Place(root, _completionBox, row++, new Thickness(0), stretch: true);

            // 说明行独立成 Auto 行：两行文字塞进 40 高的表单行会溢出压到错误行。
            var completionHint = new TextBlock
            {
                Text = AcquisitionTaskRules.ResultValueHint,
                Style = RequireStyle("Dialog.Hint"),
                Margin = new Thickness(0, 0, 0, 6),
            };

            Grid.SetRow(completionHint, row++);
            Grid.SetColumn(completionHint, 1);
            root.Children.Add(completionHint);

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
            _completionBox.TextChanged += (_, _) => ShowError(null);

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

            // P3.1b：HTTP数据上报（Report）只把「字段名 + 来源变量值」拼成 JSON 发走，
            // 不落 CSV、也没有实时背景图 —— 「数据记录」「保存路径」「实时背景图」三组
            // **整行隐藏**（标签与内容控件都折叠）。
            // ⚠️ 只折叠控件还不够：表单行是**固定行高 40** 的，空行会留下 40 高的洞，
            // 所以还要把这三行的 RowDefinition.Height 压成 0。
            // ⚠️ **必须跟着下拉实时切**（2026-11-24 修）：任务类型下拉就在本对话框里，
            // 用户改成「HTTP数据上报」的当场就该看到三行消失，而不是等到下次打开。
            _kindCombo.SelectionChanged += (_, _) => SyncKindRows(root);

            // 初始按传入的类型折叠一次（RecordEnabled 那组单选的事件已挂完，不会打架）
            SyncKindRows(root);
        }

        /// <summary>
        /// 按当前「任务类型」折叠 / 展开「数据记录 / 保存路径 / 实时背景图」三组。
        /// <para>Report 一律隐藏（它不落 CSV、没有背景图）；读取 / 写入显示。</para>
        /// </summary>
        private void SyncKindRows(Grid root)
        {
            bool report = KindValue == AcquisitionTaskKind.Report;

            SetRowVisible(root, _recordRow, _recordLabel, _recordContent, !report);
            SetRowVisible(root, _pathRow, _pathLabel, _pathContent, !report);
            SetRowVisible(root, _imageRow, _imageLabel, _imageContent, !report);
        }

        /// <summary>
        /// 显示 / 隐藏一个表单行：行高在 40 与 0 之间切 + 标签与内容控件切 Visibility。
        /// <para>只设 <c>Visibility</c> 不够 —— 固定行高的行会留下一个 40 高的空洞。</para>
        /// </summary>
        private static void SetRowVisible(
            Grid root, int row, TextBlock label, UIElement content, bool visible)
        {
            if (row >= 0 && row < root.RowDefinitions.Count)
            {
                root.RowDefinitions[row].Height = visible
                    ? new GridLength(FormRowHeight)
                    : new GridLength(0);
            }

            Visibility state = visible ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = state;
            content.Visibility = state;
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

        /// <summary>实时背景图相对路径（空串 = 没配图）。</summary>
        public string LiveImagePathValue => _liveImagePath;

        /// <summary>执行完成输出变量名（空串 = 不写结果）。</summary>
        public string CompletionVariableValue => _completionBox.Text.Trim();

        /// <summary>弹出对话框；用户取消返回 <c>null</c>。</summary>
        public static AcquisitionTaskDialog? Ask(
            Window owner,
            AcquisitionTask task,
            Func<string, bool> taskNameExists,
            Func<string, GlobalVariable?> findVariable,
            Func<IReadOnlyList<CompletionCandidate>>? completions = null,
            Func<IReadOnlyList<CompletionCandidate>>? variableCompletions = null)
        {
            var dlg = new AcquisitionTaskDialog(
                task, taskNameExists, findVariable, completions, variableCompletions);

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

        /// <summary>
        /// 选实时背景图：走 <see cref="LiveImagePicker"/> ——
        /// 两处入口（本对话框 / 总览页「导入图片」按钮）共用同一份拷贝规则。
        /// </summary>
        private void PickLiveImage()
        {
            try
            {
                // 已配过图就从那张图所在目录开始找，否则每次都要从头翻
                string? relative = LiveImagePicker.Pick(this, _liveImagePath);
                if (relative == null)
                {
                    return;   // 用户取消
                }

                _liveImagePath = relative;
                _liveImageBox.Text = relative;
                _error.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                // 内联报错（不用 MessageBox：抢焦点、不跟主题）
                _error.Text = "图片拷贝失败：" + ex.Message;
                _error.Visibility = Visibility.Visible;
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
            // P3.1b：HTTP数据上报没有「记录」概念（整行已隐藏），跳过路径检查。
            if (RecordEnabledValue && KindValue != AcquisitionTaskKind.Report)
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

            // 执行完成输出变量：留空合法，填了必须是能装下 1 / 2 的整数全局变量。
            // 与配置完整性检查共用同一处判据，避免「对话框放行、整组却报配置错误」。
            if (!AcquisitionTaskRules.TryValidateCompletionVariable(
                    _completionBox.Text, _findVariable, out _, out string completionError))
            {
                ShowError(completionError);
                _completionBox.Focus();
                _completionBox.SelectAll();
                return;
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

        /// <summary>
        /// 编译 + 变量存在性检查 + 结果类型检查（口径与 PLC 状态机一致）。
        /// <para>
        /// ⚠️ 顺序：语法 → 名字 → 结果类型。变量不存在时类型也推不出来，
        /// 先报「不存在」才指向正确的改法。
        /// </para>
        /// </summary>
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

            if (!compiled.TryValidateNames(_nameErrorOfVariable, out error))
            {
                return false;
            }

            // 2026-11-11：`var`（INT 变量）当触发条件时恒不触发、且原先一个字都不报
            return compiled.TryValidateResultType(
                name => VariableReference.DescribeType(name, _findVariable), out error);
        }

        /// <summary>
        /// 表达式里的名字**必须是变量**（系统变量或全局变量）。
        /// <para>
        /// ⚠️ 刻意与 <c>_taskNameExists</c>（任务名重名检查）分开成两个委托：
        /// 两者签名一样但语义完全不同，共用一个会写出「表达式里填任务名也算合法」的 bug。
        /// </para>
        /// </summary>
        /// <summary>
        /// 表达式名字校验：合法返回 <c>null</c>，否则返回原因。
        /// <para>数组必须带下标（<c>var1[0]</c>），整条数组在这里被拒。</para>
        /// </summary>
        private string? _nameErrorOfVariable(string name) =>
            VariableReference.Describe(name, _findVariable, allowWholeArray: false);

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
