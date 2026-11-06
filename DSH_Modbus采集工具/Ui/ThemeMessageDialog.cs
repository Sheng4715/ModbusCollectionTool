using System.Windows;
using System.Windows.Controls;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 全应用**唯一的提示 / 确认弹框**（2026-11-06 用户定案：所有弹出对话框风格统一）。
    ///
    /// <para>
    /// 在此之前各处都用 <c>MessageBox.Show</c> —— 那是系统画的窗，**不吃主题**：
    /// 深色主题下弹一块纯白板，字体、按钮、圆角、边框与 <c>Dialog.*</c> 那套完全不是一回事。
    /// 本类把「一句话 + 一两个按钮」这类最简单的交互收编成自绘窗，外观与
    /// <see cref="MachineStateEditDialog"/> / <see cref="AcquisitionTaskDialog"/> 等
    /// 复杂对话框**共用同一套外壳与样式**。
    /// </para>
    /// <para>
    /// **两种形态**：
    /// <list type="bullet">
    /// <item><see cref="Info"/> / <see cref="Warn"/> / <see cref="Error"/> —— 一个「确定」，主色按钮；</item>
    /// <item><see cref="Confirm"/> —— 「取消」+「确定」，破坏性操作用 <c>danger: true</c> 把确定钮换成实心红。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ **不要再用 <c>MessageBox.Show</c>**：它既跟不了主题，也无法与其余对话框对齐
    /// （见 AGENTS-细节.md §9.3）。真正**需要输入**的场合也不该用它 —— 那要走
    /// <see cref="DialogWindow"/> 的子类（如 <see cref="NumberInputDialog"/>）。
    /// </para>
    /// </summary>
    public sealed class ThemeMessageDialog : DialogWindow
    {
        /// <summary>
        /// 破坏性操作的确认按钮：确定键默认**不**抢焦点（默认落在「取消」上）。
        /// <para>
        /// 与 <c>MessageBox</c> 的 <c>MessageBoxResult.Cancel</c> 默认项同义 ——
        /// 「删除全部」这种操作，误按一次回车就没了，必须让人多走一步。
        /// </para>
        /// </summary>
        private readonly Button _defaultButton;

        private ThemeMessageDialog(
            string title, string message, bool confirm, bool danger, string confirmText, double width)
            : base(title, width)
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var body = new TextBlock
            {
                Text = message,
                Style = RequireStyle("Dialog.Body"),
            };

            Grid.SetRow(body, 0);
            Grid.SetColumn(body, 0);
            root.Children.Add(body);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            Button ok = MakeButton(confirmText, primary: !danger);

            if (danger)
            {
                // 「确定」在破坏性场景里换成实心红 —— 颜色即语义（见 AGENTS.md §9.1）
                ok.Style = RequireStyle("Dialog.Button.Danger");
            }

            if (!confirm)
            {
                ok.IsDefault = true;
                ok.Click += (_, _) => DialogResult = true;
                buttons.Children.Add(ok);
                _defaultButton = ok;
            }
            else
            {
                Button cancel = MakeButton("取消", primary: false);
                cancel.Margin = new Thickness(0, 0, 8, 0);
                cancel.IsCancel = true;
                cancel.Click += (_, _) => DialogResult = false;

                ok.Click += (_, _) => DialogResult = true;

                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);

                if (danger)
                {
                    // 破坏性：回车落在「取消」，确认要显式点
                    cancel.IsDefault = true;
                    _defaultButton = cancel;
                }
                else
                {
                    ok.IsDefault = true;
                    _defaultButton = ok;
                }
            }

            Grid.SetRow(buttons, 1);
            Grid.SetColumn(buttons, 0);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>纯提示（信息 / 警告 / 错误都走它，颜色语义由调用方在文案里说清）。</summary>
        public static void Info(Window? owner, string title, string message) =>
            ShowCore(owner, title, message, confirm: false, danger: false, "确定", 400);

        /// <summary>确认框；返回用户是否点了确定。</summary>
        /// <param name="danger">
        /// 破坏性操作（删除 / 清空）。为 true 时确定钮是实心红、且默认焦点落在「取消」。
        /// </param>
        public static bool Confirm(
            Window? owner, string title, string message, bool danger = false, string confirmText = "确定")
        {
            var dlg = new ThemeMessageDialog(title, message, confirm: true, danger, confirmText, 420);
            dlg.AttachOwner(owner);
            return dlg.ShowOwned(dlg._defaultButton);
        }

        private static void ShowCore(
            Window? owner, string title, string message, bool confirm, bool danger,
            string confirmText, double width)
        {
            var dlg = new ThemeMessageDialog(title, message, confirm, danger, confirmText, width);
            dlg.AttachOwner(owner);
            dlg.ShowOwned(dlg._defaultButton);
        }
    }
}
