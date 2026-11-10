using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// <c>ARRAY</c> 类型声明对话框（2026-11-09 用户要求）。
    /// <para>
    /// 需求原文：「新增数组 ARRAY 类型，选择为 ARRAY 时，弹出对话框显示以下选项：
    /// 数据类型（所有数据类型列表，除了 STRING）/ 元素数量（最大 128）」。
    /// </para>
    /// <para>
    /// 「数据类型」这一项在对话框里是**元素类型** —— 用户选的 ARRAY 已经是外层类型，
    /// 再让他选一次「整体类型」没有意义。故标签直接写「元素数据类型」。
    /// </para>
    /// </summary>
    public sealed class ArrayDeclareDialog : DialogWindow
    {
        /// <summary>行高固定 40：三条表单行的内容高度最高 32，留 8 呼吸。</summary>
        private const double FormRowHeight = 40;

        private readonly TextBox _countBox;
        private readonly ComboBox _elementCombo;
        private readonly TextBlock _error;

        /// <summary>用户选定的元素类型。</summary>
        public DataType ElementType { get; private set; } = DataType.Int;

        /// <summary>用户填写的元素数量。</summary>
        public int ElementCount { get; private set; } = 1;

        private ArrayDeclareDialog(DataType initialType, int initialCount)
            : base("数组设置", 400)
        {
            var root = new Grid { Margin = new Thickness(16) };

            // 三行表单：元素类型 / 元素数量 / 错误行（Auto）
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FormRowHeight) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FormRowHeight) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddLabel(root, "元素数据类型", row: 0, bottomMargin: 0);

            _elementCombo = new ComboBox
            {
                Style = RequireStyle("Dialog.ComboBox"),
                ItemContainerStyle = RequireStyle("Dialog.ComboBoxItem"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 元素类型候选：除 STRING 外的全部（用户定案），顺序即类型表顺序
            var items = new List<ComboItem<DataType>>();
            foreach (DataTypeSpec spec in DataTypes.ArrayElementTypes)
            {
                items.Add(new ComboItem<DataType>(spec.DisplayName, spec.Type));
            }

            _elementCombo.ItemsSource = items;

            // 初始值找不到（例如旧配置里存了 STRING，理论上进不来）就退回第 0 项，
            // 与设备类型下拉框「取不到就回退第一项」同一口径。
            int selected = items.FindIndex(x => x.Value == initialType);
            _elementCombo.SelectedIndex = selected >= 0 ? selected : 0;

            Place(root, _elementCombo, row: 0, margin: new Thickness(0), stretch: true);

            AddLabel(root, "元素数量", row: 1, bottomMargin: 0);

            // ⚠️ 错误行必须**先建**：下面的 TextChanged 已引用它（「一改字就清错误」），
            // 后建就是空引用 —— 编译器只报 CS8602 警告，运行期点一下输入框才会崩。
            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(_error, 2);
            Grid.SetColumnSpan(_error, 2);
            root.Children.Add(_error);

            _countBox = MakeTextBox(initialCount.ToString(CultureInfo.InvariantCulture));
            _countBox.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;
            Place(root, _countBox, row: 1, margin: new Thickness(0), stretch: true);

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
            cancel.Click += (_, _) => DialogResult = false;

            var ok = new Button
            {
                Content = "确定",
                Style = RequireStyle("Dialog.Button.Primary"),
                IsDefault = true,
            };
            ok.Click += Ok_Click;

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            Grid.SetRow(buttons, 3);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (_elementCombo.SelectedItem is not ComboItem<DataType> picked)
            {
                ShowError("请选择元素数据类型。");
                return;
            }

            string text = _countBox.Text.Trim();

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                ShowError($"元素数量需为 {DataTypes.MinArrayLength} ~ {DataTypes.MaxArrayLength} 之间的整数。");
                return;
            }

            // 校验与模型层同一入口，避免界面与解析各写一套范围
            if (!ArrayValueCodec.TryValidateDeclaration(picked.Value, count, out string error))
            {
                ShowError(error);
                return;
            }

            ElementType = picked.Value;
            ElementCount = count;
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            _error.Text = message;
            _error.Visibility = Visibility.Visible;
            _countBox.Focus();
            _countBox.SelectAll();
        }

        /// <summary>
        /// 弹出对话框；取消时返回 <c>null</c>。
        /// </summary>
        /// <param name="initialType">当前元素类型（改已有数组时回填）</param>
        /// <param name="initialCount">当前元素个数（改已有数组时回填）</param>
        public static (DataType ElementType, int Count)? Ask(
            Window? owner, DataType initialType, int initialCount)
        {
            int count = initialCount is >= DataTypes.MinArrayLength and <= DataTypes.MaxArrayLength
                ? initialCount
                : DataTypes.MinArrayLength;

            var dlg = new ArrayDeclareDialog(initialType, count);
            dlg.AttachOwner(owner);

            return dlg.ShowOwned(dlg._countBox)
                ? (dlg.ElementType, dlg.ElementCount)
                : null;
        }
    }
}
