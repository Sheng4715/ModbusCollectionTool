using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 实时卡片选项框（P1.4）：**项名称字号 / 数值字号 / X / Y 坐标 / 重置位置**。
    /// <para>
    /// 用户定案只给这几项，**不做颜色、不做显示格式** —— 实时卡片是只读展示，
    /// 显示格式统一按十进制（见 <see cref="AcquisitionLiveText"/>）。
    /// </para>
    /// <para>
    /// **X / Y 坐标**（2026-11-03 用户要求加回）：
    /// 曾经做过、后来去掉（当时只有拖动），现在加回 —— 两者互补：
    /// 拖动适合「大致摆好」，坐标框适合「两张卡片对齐」「按数字复现某次布局」。
    /// </para>
    /// <para>
    /// ⚠️ **坐标只到整数，且从输入上就挡住非法字符**（2026-11-03 用户定案，
    /// 取代原先「先让输、点确定时再红字报错」）：
    /// 非数字字符**根本打不进去**（见 <see cref="CoordinateBox_PreviewTextInput"/>），
    /// 于是不需要「请填整数」这类提示 —— 用户看到的结果本身就是约束，
    /// 空串按 0 处理、`-` 只允许出现在开头。
    /// </para>
    /// <para>
    /// ⚠️ 拖动产生的小数在本框里被**四舍五入**显示，这是有意的：输入框既然只接受整数，
    /// 就不该显示 <c>-180.54</c> 让人以为要保留精度。且只有值真的变了才写回模型
    /// （见调用方），避免「打开看一眼就点确定」把精确位置白白取整。
    /// </para>
    /// </summary>
    public sealed class AcquireLiveCardOptionDialog : DialogWindow
    {
        /// <summary>
        /// 表单控件统一宽度。**下拉框与输入框必须一样宽**（2026-11-03 用户要求）——
        /// 不统一时右侧参差不齐，看着像没对齐。
        /// </summary>
        private const double FieldWidth = 120;

        /// <summary>标签列宽度。够放「项名称字号」六个字（含 150% DPI 下的字宽）。</summary>
        private const double LabelColumnWidth = 88;

        /// <summary>表单行高：40 = 该行最高的内容（32） + 呼吸。</summary>
        private const double RowHeight = 40;

        private readonly ComboBox _nameSizeBox;
        private readonly ComboBox _valueSizeBox;

        private readonly TextBox _xBox;
        private readonly TextBox _yBox;

        private readonly Action _resetPosition;

        /// <summary>ShowOwned 要的初始焦点控件（基类没有这个字段，各子类自己持有）。</summary>
        private readonly Control _firstFocusable;

        private AcquireLiveCardOptionDialog(AcquisitionItem item, Action resetPosition)
            : base("实时数据选项", 360)
        {
            _resetPosition = resetPosition;

            // ⚠️ Margin 16 是这个对话框唯一的「不贴窗口边」保障。
            // 2026-11-03 之前漏了它，实测截图里**内容直接贴着窗口边缘**。
            var root = new Grid { Margin = new Thickness(16) };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 四行表单固定行高（AGENTS.md §9.3 第 8 条：行距对齐 = 行高固定，
            // 不是每行留一样的下边距）；按钮行留 Auto
            for (int i = 0; i < 4; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            }

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮行

            _nameSizeBox = BuildFontSizeCombo(item.LiveNameFontSize);
            _valueSizeBox = BuildFontSizeCombo(item.LiveValueFontSize);

            AddLabel(root, "项名称字号", 0, bottomMargin: 0);
            Place(root, _nameSizeBox, 0, new Thickness(0), stretch: false);

            AddLabel(root, "数值字号", 1, bottomMargin: 0);
            Place(root, _valueSizeBox, 1, new Thickness(0), stretch: false);

            _xBox = BuildCoordinateBox(item.LiveX);
            AddLabel(root, "X 坐标", 2, bottomMargin: 0);
            Place(root, _xBox, 2, new Thickness(0), stretch: false);

            _yBox = BuildCoordinateBox(item.LiveY);
            AddLabel(root, "Y 坐标", 3, bottomMargin: 0);
            Place(root, _yBox, 3, new Thickness(0), stretch: false);

            // 按钮行：左边「重置位置」，右边 [取消][确定]。
            // 「重置位置」放左边而不是单独占一行 —— 它是个**动作**，不是表单字段，
            // 单独占一行会让对话框无谓地高出一截，且左侧大片空白看着像漏了什么。
            var buttonRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button reset = MakeButton("重置位置", primary: false);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0);
            reset.Click += (_, _) => ResetPosition();
            Grid.SetColumn(reset, 0);
            buttonRow.Children.Add(reset);

            var okCancel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            Button cancel = MakeButton("取消", primary: false);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (_, _) => DialogResult = false;

            Button ok = MakeButton("确定", primary: true);
            ok.Click += (_, _) => Commit();

            okCancel.Children.Add(cancel);
            okCancel.Children.Add(ok);

            Grid.SetColumn(okCancel, 2);
            buttonRow.Children.Add(okCancel);

            Grid.SetRow(buttonRow, 4);
            Grid.SetColumn(buttonRow, 0);
            Grid.SetColumnSpan(buttonRow, 2);
            root.Children.Add(buttonRow);

            Content = root;
            _firstFocusable = _nameSizeBox;
        }

        /// <summary>
        /// 造一个**只接受整数**的坐标框。
        /// <para>
        /// 光标停在「-」后面、或全选后再输入，替换后仍要合法，所以判定依据是
        /// **输入之后整串是否合法**，而不是「这个字符是不是数字」——
        /// 单选一个 <c>-</c> 本身也是合法的中间态。
        /// </para>
        /// </summary>
        private TextBox BuildCoordinateBox(double? value)
        {
            TextBox box = MakeTextBox(RoundToIntText(value));

            // 统一宽度：与下拉框一致（MakeTextBox 默认不设宽度，交给列拉伸；
            // 但这里显式指定，才能和 MakeCombo(FieldWidth) 精确对齐）
            box.Width = FieldWidth;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            box.TextAlignment = TextAlignment.Left;

            // 拦输入：非数字 / 多余的负号直接不放行（打不进去就没有「格式错误」可言）
            box.PreviewTextInput += CoordinateBox_PreviewTextInput;
            DataObject.AddPastingHandler(box, CoordinateBox_Pasting);

            return box;
        }

        /// <summary>
        /// 只放行「输入之后整串仍是合法整数串」的字符。
        /// <para>
        /// 规则本体在 <see cref="LiveCoordinateText.IsAcceptableIntegerText"/>（纯函数、可离线断言）——
        /// 这里只负责把控件的当前状态喂给它。
        /// </para>
        /// </summary>
        private static void CoordinateBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox box)
            {
                return;
            }

            e.Handled = !LiveCoordinateText.IsAcceptableIntegerText(
                LiveCoordinateText.SimulateInput(box.Text, box.SelectionStart, box.SelectionLength, e.Text));
        }

        /// <summary>粘贴也要过一遍：直接贴一大段带小数点的文字不该钻空子。</summary>
        private static void CoordinateBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is not TextBox box)
            {
                return;
            }

            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }

            string pasted = (string)e.DataObject.GetData(typeof(string))!;

            if (!LiveCoordinateText.IsAcceptableIntegerText(
                    LiveCoordinateText.SimulateInput(box.Text, box.SelectionStart, box.SelectionLength, pasted)))
            {
                e.CancelCommand();
            }
        }

        /// <summary>把可空坐标转成整数文本；未放置过（null）显示 0。</summary>
        private static string RoundToIntText(double? value) => LiveCoordinateText.Format(value);

        private ComboBox BuildFontSizeCombo(double current)
        {
            ComboBox combo = MakeCombo(FieldWidth);

            int selected = 0;
            for (int i = 0; i < AcquisitionTaskRules.LiveFontSizes.Length; i++)
            {
                double size = AcquisitionTaskRules.LiveFontSizes[i];

                combo.Items.Add(new ComboItem<double>(
                    size.ToString("0", CultureInfo.InvariantCulture), size));

                if (Math.Abs(size - current) < 0.01)
                {
                    selected = i;
                }
            }

            combo.SelectedIndex = selected;
            return combo;
        }

        /// <summary>「重置位置」：把卡片挪回自动摆放位；字号不动。</summary>
        private void ResetPosition() => _resetPosition();

        private void Commit()
        {
            _nameSize = SelectedSize(_nameSizeBox, AcquisitionTaskRules.DefaultLiveNameFontSize);
            _valueSize = SelectedSize(_valueSizeBox, AcquisitionTaskRules.DefaultLiveValueFontSize);

            // 输入阶段已经把非法字符挡住了，这里只需把空串 / 纯「-」当 0。
            // 规则本体在 LiveCoordinateText.Parse（纯函数、可离线断言）。
            _x = LiveCoordinateText.Parse(_xBox.Text);
            _y = LiveCoordinateText.Parse(_yBox.Text);

            DialogResult = true;
        }


        /// <summary>用户点「确定」时选定的字号（调用方据此写回模型）。</summary>
        public double _nameSize;

        /// <summary>用户点「确定」时选定的数值字号。</summary>
        public double _valueSize;

        /// <summary>用户点「确定」时选定的 X 坐标（整数）。</summary>
        public double _x;

        /// <summary>用户点「确定」时选定的 Y 坐标（整数）。</summary>
        public double _y;

        private static double SelectedSize(ComboBox combo, double fallback) =>
            combo.SelectedItem is ComboItem<double> item ? item.Value : fallback;

        /// <summary>打开对话框。点了确定才改字号与坐标；取消则原样不动。</summary>
        public static bool Ask(
            Window owner, AcquisitionItem item, Action resetPosition,
            out double nameSize, out double valueSize, out double x, out double y)
        {
            var dlg = new AcquireLiveCardOptionDialog(item, resetPosition);
            dlg.AttachOwner(owner);

            bool ok = dlg.ShowOwned(dlg._firstFocusable);

            nameSize = ok ? dlg._nameSize : item.LiveNameFontSize;
            valueSize = ok ? dlg._valueSize : item.LiveValueFontSize;

            // 取消时返回原坐标（「从未放置过」的 null → 0，调用方自行判断要不要写）
            x = ok ? dlg._x : item.LiveX.GetValueOrDefault();
            y = ok ? dlg._y : item.LiveY.GetValueOrDefault();

            return ok;
        }
    }
}
