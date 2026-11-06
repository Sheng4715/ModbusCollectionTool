using System.Windows;
using System.Windows.Controls;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 账户登录对话框（2026-11-06 用户定案，P 权限）。
    ///
    /// <para>
    /// 两行表单 + 一行按钮：用户名（下拉，默认显示当前已登录的）+ 密码，
    /// 底部「注销」「登录」。用户给的 UI 就是这么两块，不多加字段。
    /// </para>
    /// <para>
    /// ⚠️ **「注销」在这里只代表「退回技术员」**（用户定案）：技术员本来就免登录，
    /// 所以注销不需要二次确认，也不会把界面锁起来 —— 按下去立刻生效并关窗。
    /// </para>
    /// </summary>
    public sealed class LoginDialog : DialogWindow
    {
        /// <summary>表单行高：40 = 内容 32 + 呼吸（与其余对话框同口径，§9.3 第 8 条）。</summary>
        private const double RowHeight = 40;

        private readonly ComboBox _roleCombo;

        /// <summary>
        /// 密码框。**必须是 <see cref="PasswordBox"/> 而不是 TextBox**：
        /// 它是唯一「输入内容不回显」的现成控件（掩码 ●），用户 2026-11-06 明确要求
        /// 「密码不要显示明文」。⚠️ 别为了取长度方便换成 TextBox 再手动打码 ——
        /// 那样窗口里会留着一份可读明文（截屏、Spy 都能看到）。
        /// </summary>
        private readonly PasswordBox _passwordBox;

        private readonly TextBlock _error;

        /// <summary>点「登录」成功后的角色；取消 / 注销时为当前角色不变。</summary>
        public UserRole ResultRole { get; private set; }

        /// <summary>用户是否点了「注销」。</summary>
        public bool LogoutRequested { get; private set; }

        private LoginDialog(UserRole current)
            : base("账户登录", 360)
        {
            ResultRole = current;

            var root = new Grid { Margin = new Thickness(16) };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 2; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            }

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 错误行
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮行

            // 用户名：下拉里就两档，默认选中当前已登录的那个。
            // ⚠️ **stretch: true**（而不是固定的 MakeCombo(150) 宽度）：
            // 按钮行要和输入框左右对齐，两者就得**吃同一个列宽**（2026-11-06 用户要求）。
            // 写死宽度的话，改一个字宽或换个语言就又会错位。
            AddLabel(root, "用户名", 0, bottomMargin: 0);
            _roleCombo = MakeCombo(150);
            _roleCombo.Width = double.NaN;
            _roleCombo.HorizontalAlignment = HorizontalAlignment.Stretch;

            foreach (UserRole role in UserRules.Roles)
            {
                _roleCombo.Items.Add(new ComboItem<UserRole>(UserRules.NameOf(role), role));
            }

            for (int i = 0; i < UserRules.Roles.Length; i++)
            {
                if (UserRules.Roles[i] == current)
                {
                    _roleCombo.SelectedIndex = i;
                    break;
                }
            }

            _roleCombo.SelectionChanged += (_, _) => SyncPasswordEnabled();
            Place(root, _roleCombo, 0, new Thickness(0), stretch: true);

            // 密码：只有工程师才需要，选技术员时置灰（那一档本来就免密码）
            AddLabel(root, "密码", 1, bottomMargin: 0);
            _passwordBox = new PasswordBox
            {
                Style = RequireStyle("Dialog.PasswordBox"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            Place(root, _passwordBox, 1, new Thickness(0), stretch: true);

            _error = new TextBlock
            {
                Style = RequireStyle("Dialog.Error"),
                Margin = new Thickness(68, 4, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            Grid.SetRow(_error, 2);
            Grid.SetColumnSpan(_error, 2);
            root.Children.Add(_error);

            // 一改字就撤掉上次的错误（与其它对话框同口径）。
            // ⚠️ PasswordBox 的事件是 PasswordChanged，**不是** TextChanged —— 抄错编译就过不了
            _passwordBox.PasswordChanged += (_, _) => HideError();

            // 按钮行：**只占第 1 列**（与两个输入框同一列），不再 ColumnSpan=2 ——
            // 跨两列右对齐时按钮组贴的是窗口右缘，与输入框的右边界对不上（2026-11-06 用户指出）。
            //
            // ⚠️ **两列都用 `*`（等分），不要 Auto**：用 Auto 时第一个 Star 列会把剩余空间
            // 全吃掉，两个按钮被挤到右边 —— 右缘齐了、**左缘却差 100 多 DIP**
            // （UIA 实测：输入框左 827 / 按钮组左 931）。等分后四个边都落在输入框的框线上。
            var buttons = new Grid
            {
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左边「注销」、右边「登录」—— 用户给的顺序就是这样，且注销是次要动作
            Button logout = MakeButton("注销", primary: false);
            logout.HorizontalAlignment = HorizontalAlignment.Stretch;
            logout.IsCancel = true;
            logout.Click += (_, _) =>
            {
                LogoutRequested = true;
                ResultRole = UserRules.DefaultRole;
                DialogResult = true;
            };

            Button login = MakeButton("登录", primary: true);
            login.HorizontalAlignment = HorizontalAlignment.Stretch;
            login.IsDefault = true;
            login.Click += Login_Click;

            Grid.SetColumn(logout, 0);
            Grid.SetColumn(login, 2);
            buttons.Children.Add(logout);
            buttons.Children.Add(login);

            Grid.SetRow(buttons, 3);
            Grid.SetColumn(buttons, 1);
            root.Children.Add(buttons);

            Content = root;

            SyncPasswordEnabled();
        }

        /// <summary>选技术员时密码框置灰 —— 那一档免密码，留着可输入只会让人以为要填。</summary>
        private void SyncPasswordEnabled()
        {
            bool engineer = (_roleCombo.SelectedItem as ComboItem<UserRole>)?.Value == UserRole.Engineer;

            if (_passwordBox != null)
            {
                _passwordBox.IsEnabled = engineer;
                if (!engineer)
                {
                    _passwordBox.Clear();
                }
            }
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            string roleName = (_roleCombo.SelectedItem as ComboItem<UserRole>) is { } item
                ? item.Text
                : string.Empty;

            // ⚠️ PasswordBox 的取值属性是 Password，**不是 Text**（也没有 Text）
            if (!UserRules.TryLogin(roleName, _passwordBox.Password, out UserRole role, out string error))
            {
                ShowError(error);
                if (!_passwordBox.IsEnabled)
                {
                    // 技术员那条支路不会走到这里（它恒成功），留作防御
                    _roleCombo.Focus();
                }
                else
                {
                    _passwordBox.Focus();
                    _passwordBox.SelectAll();
                }

                return;
            }

            ResultRole = role;
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            _error.Text = message;
            _error.Visibility = Visibility.Visible;
        }

        private void HideError() => _error.Visibility = Visibility.Collapsed;

        /// <summary>
        /// 打开登录框。返回是否要注销 / 新的角色。
        /// </summary>
        /// <param name="logout">用户点了「注销」= 退回技术员。</param>
        public static bool Ask(Window? owner, UserRole current, out UserRole role, out bool logout)
        {
            var dlg = new LoginDialog(current);
            dlg.AttachOwner(owner);

            bool ok = dlg.ShowOwned(dlg._passwordBox);

            logout = ok && dlg.LogoutRequested;
            role = ok ? (logout ? UserRules.DefaultRole : dlg.ResultRole) : current;
            return ok;
        }
    }
}
