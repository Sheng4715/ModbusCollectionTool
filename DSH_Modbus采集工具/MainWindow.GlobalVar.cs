using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Io;
using DSH_Modbus采集工具.Logging;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Net;
using DSH_Modbus采集工具.Polling;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

// ============================================================
// 全局变量（P3.3）：列表/类型/地址/注释编辑
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- 全局变量（P3.3） ----------

        /// <summary>
        /// 全部数据类型的规格（线圈区 + 寄存器区合并，顺序同规格表）。
        /// <para>
        /// <c>DataTypes.AllSpecs</c> 是私有的（对外只暴露 <c>CoilTypes</c> / <c>RegisterTypes</c>），
        /// 而变量表要在一个下拉框里同时给出两类，故在这里拼一次。
        /// </para>
        /// </summary>
        private static IEnumerable<DataTypeSpec> AllTypeSpecs()
        {
            foreach (DataTypeSpec spec in DataTypes.CoilTypes)
            {
                yield return spec;
            }

            foreach (DataTypeSpec spec in DataTypes.RegisterTypes)
            {
                yield return spec;
            }
        }

        /// <summary>刷新全局变量的计数、空提示与按钮可用性。</summary>
        private void RefreshGlobalVarView()
        {
            if (GlobalVarItemsControl == null)
            {
                return;
            }

            int n = _globalVarSet.Items.Count;

            // 表头不随空列表隐藏 —— 它是列说明，也是「+」的落点
            if (GlobalVarEmptyHint != null)
            {
                GlobalVarEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ClearGlobalVarsButton != null)
            {
                ClearGlobalVarsButton.IsEnabled = n > 0;
            }

            // 行数变了（增/删/清空）也要重算 ↑↓ 的可用性：
            // 删掉末行后原来那条「不能下移」的判断就过期了
            UpdateGlobalVarMoveButtons();
        }

        private void AddGlobalVar_Click(object sender, RoutedEventArgs e)
        {
            // 生成一个默认不重名的名字：var1、var2 …
            string name = "var1";
            for (int i = 2; _globalVarSet.Items.Any(x => x.Name == name); i++)
            {
                name = "var" + i;
            }

            GlobalVarItem? item = _globalVarSet.Add(name, out string error);
            if (item == null)
            {
                MessageBox.Show(this, error, "新增变量失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshGlobalVarView();
            GlobalVarItemsControl?.UpdateLayout();
            FocusGlobalVarRow(item);
        }

        private void RemoveGlobalVar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GlobalVarItem item)
            {
                // 删掉的正好是选中行 → 清掉选中，免得 ↑↓ 作用在一个已经不在列表里的对象上
                if (ReferenceEquals(_selectedGlobalVar, item))
                {
                    _selectedGlobalVar = null;
                }

                _globalVarSet.Remove(item);
                RefreshGlobalVarView();

                // 被删的变量可能正被监控行引用，那些行现在应标红「变量不存在」
                RefreshAfterVariableChange();
            }
        }

        private void ClearGlobalVars_Click(object sender, RoutedEventArgs e)
        {
            if (_globalVarSet.Items.Count == 0)
            {
                return;
            }

            if (MessageBox.Show(this, $"确定删除全部 {_globalVarSet.Items.Count} 个变量？",
                    "删除全部变量", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK)
            {
                return;
            }

            _globalVarSet.Clear();
            _selectedGlobalVar = null;
            RefreshGlobalVarView();

            // 全部删完，所有监控变量行都会变成「变量不存在」
            RefreshAfterVariableChange();
        }

        /// <summary>行容器加载：把行对象挂到 Tag，供事件处理器取用。</summary>
        private void GlobalVarRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not GlobalVarItem item)
            {
                return;
            }

            fe.Tag = item;

            // 回填各控件初值（DataTemplate 里不能直接双向绑定全部字段：
            // 名称 / 地址 / 注释都要走「失焦提交」而不是逐字符提交）
            if (FindGlobalVarBox(fe, "VarNameBox") is TextBox nameBox)
            {
                nameBox.Text = item.Name;
            }

            if (FindGlobalVarBox(fe, "VarAddressBox") is TextBox addrBox)
            {
                addrBox.Text = item.Address;
            }

            if (FindGlobalVarBox(fe, "VarCommentBox") is TextBox commentBox)
            {
                commentBox.Text = item.Comment;
            }

            SyncGlobalVarTypeCombo(fe, item);
            SyncGlobalVarFormatCombo(fe, item);
        }

        private static TextBox? FindGlobalVarBox(DependencyObject root, string name)
        {
            if (root is TextBox tb && tb.Name == name)
            {
                return tb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                TextBox? found = FindGlobalVarBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static ComboBox? FindGlobalVarCombo(DependencyObject root, string name)
        {
            if (root is ComboBox cb && cb.Name == name)
            {
                return cb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ComboBox? found = FindGlobalVarCombo(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void SyncGlobalVarTypeCombo(DependencyObject root, GlobalVarItem item)
        {
            if (FindGlobalVarCombo(root, "VarTypeCombo") is not ComboBox combo)
            {
                return;
            }

            // ⚠️ 必须放 DataTypeSpec **对象**，不能放 DisplayName 字符串：
            // MonitorTypeItemTemplate 绑的是 {Binding DisplayName}，字符串没这个属性，
            // 展开列表会整列空白（只看到选中条，没有文字）。
            List<DataTypeSpec> specs = AllTypeSpecs().ToList();
            combo.ItemsSource = specs;

            DataTypeSpec current = DataTypes.Get(item.Model.Type);
            DataTypeSpec? hit = specs.FirstOrDefault(sp => sp.Type == current.Type);
            combo.SelectedItem = hit ?? specs[0];
        }

        private static void SyncGlobalVarFormatCombo(DependencyObject root, GlobalVarItem item)
        {
            if (FindGlobalVarCombo(root, "VarFormatCombo") is not ComboBox combo)
            {
                return;
            }

            // 格式下拉放枚举值本身，由 EnumUpperTemplate 转成大写名
            IReadOnlyList<DisplayFormat> formats = DataTypes.Get(item.Model.Type).AllowedFormats;
            combo.ItemsSource = formats;

            bool known = formats.Contains(item.Model.Format);
            combo.SelectedItem = known ? item.Model.Format : formats[0];
        }

        /// <summary>
        /// 从行内某个控件往上找该行的模板根（<c>GlobalVarRow_Loaded</c> 把行对象挂在它的 Tag 上）。
        /// <para>
        /// 用于「改了本行某一格，要顺带改同一行另一格」的场景 —— 如切换数据类型时清空映射地址。
        /// </para>
        /// </summary>
        private static FrameworkElement? FindGlobalVarRowRoot(DependencyObject from)
        {
            DependencyObject? node = from;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.Tag is GlobalVarItem)
                {
                    return fe;
                }

                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        // ---------- 排序（2026-10-31）：表头 ↑↓ 作用于选中行 ----------

        /// <summary>当前选中的行（null = 没选）。排序按钮就作用于它。</summary>
        private GlobalVarItem? _selectedGlobalVar;

        /// <summary>点行内任意处即选中该行（点输入框时也会走到这里，但不影响编辑）。</summary>
        private void GlobalVarRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GlobalVarItem item)
            {
                SelectGlobalVar(item);
            }
        }

        /// <summary>
        /// 设置选中行并刷新按钮可用性。
        /// <para>
        /// 选中态只是**界面态**，不进模型、不落盘 —— 重启后没有选中行，
        /// 这不是缺陷：排序按钮本来就该在「没选东西」时置灰。
        /// </para>
        /// </summary>
        private void SelectGlobalVar(GlobalVarItem? item)
        {
            if (ReferenceEquals(_selectedGlobalVar, item))
            {
                return;
            }

            _selectedGlobalVar = item;

            // 行底色绑的是 IsSelected（见 GlobalVarItemTemplate 的 DataTrigger），
            // 故这里只需把所有行的该标志重算一遍
            if (_globalVarSet?.Items != null)
            {
                foreach (GlobalVarItem row in _globalVarSet.Items)
                {
                    row.IsSelected = ReferenceEquals(row, item);
                }
            }

            UpdateGlobalVarMoveButtons();
        }

        /// <summary>
        /// 刷新 ↑↓ 的可用性：没选中 → 都灰；已在首行 → ↑ 灰；已在末行 → ↓ 灰。
        /// <para>
        /// 三个条件只在这里判一次，而不是散到 Click 里 —— 置灰与点击拦截必须同源，
        /// 否则会出现「按钮亮着但点了没反应」。
        /// </para>
        /// </summary>
        private void UpdateGlobalVarMoveButtons()
        {
            int count = _globalVarSet?.Items?.Count ?? 0;

            int index = _selectedGlobalVar == null || _globalVarSet?.Items == null
                ? -1
                : _globalVarSet.Items.IndexOf(_selectedGlobalVar);

            bool has = index >= 0;

            if (MoveGlobalVarUpButton != null)
            {
                MoveGlobalVarUpButton.IsEnabled = has && index > 0;
            }

            if (MoveGlobalVarDownButton != null)
            {
                MoveGlobalVarDownButton.IsEnabled = has && index < count - 1;
            }
        }

        private void MoveGlobalVarUp_Click(object sender, RoutedEventArgs e) => MoveSelectedGlobalVar(-1);

        private void MoveGlobalVarDown_Click(object sender, RoutedEventArgs e) => MoveSelectedGlobalVar(+1);

        /// <summary>把选中行上移 / 下移一格，移完保持选中并让它留在视野里。</summary>
        private void MoveSelectedGlobalVar(int delta)
        {
            if (_selectedGlobalVar == null || _globalVarSet?.Items == null)
            {
                return;
            }

            int from = _globalVarSet.Items.IndexOf(_selectedGlobalVar);
            if (from < 0)
            {
                return;
            }

            int to = from + delta;
            if (to < 0 || to > _globalVarSet.Items.Count - 1)
            {
                return;
            }

            GlobalVarItem moving = _selectedGlobalVar;

            // Move 同时改 ObservableCollection（画面重排）、模型 List（落盘顺序）并 Reindex
            _globalVarSet.Move(moving, to);

            UpdateGlobalVarMoveButtons();

            GlobalVarItemsControl?.UpdateLayout();
            FocusGlobalVarRow(moving);
        }

        /// <summary>
        /// 切换数据类型后清空该行的映射地址（模型与控件一起清）。
        /// <para>
        /// 地址框不是双向绑定（失焦才提交），所以改 <c>Model.Address</c> 不会自动刷控件，
        /// 必须顺着触发控件往上找到本行模板根，再显式把文本清空。
        /// </para>
        /// </summary>
        /// <param name="from">触发切换的控件（用来定位所在行）</param>
        /// <param name="item">该行对应的变量</param>
        private static void ClearGlobalVarAddress(DependencyObject from, GlobalVarItem item)
        {
            item.Address = string.Empty;

            if (FindGlobalVarRowRoot(from) is FrameworkElement root
                && FindGlobalVarBox(root, "VarAddressBox") is TextBox addrBox)
            {
                addrBox.Text = string.Empty;
            }
        }

        private static GlobalVarItem? VarRowOf(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.DataContext is GlobalVarItem fromContext)
                {
                    return fromContext;
                }

                if (fe.Tag is GlobalVarItem fromTag)
                {
                    return fromTag;
                }
            }

            return null;
        }

        /// <summary>
        /// 变量定义变了（改名 / 改类型 / 改地址 / 删除）之后要做的两件事：
        /// 让自由监控里引用变量的行重新解析，并重算状态机的采集清单。
        /// <para>
        /// 监控行的类型与地址都是从变量表现取的，变量一改它就得跟着重算 ——
        /// 不刷新的话，界面上会继续显示旧类型、继续按旧地址采集。
        /// </para>
        /// </summary>
        private void RefreshAfterVariableChange()
        {
            // 放在下面的早退之前：没有监控行时也要更新状态机的清单与采集项的类型列
            RefreshMachineGlobals();

            // 数据采集项的「数据类型」是从变量表反推的，变量改名/换类型后这里会变成旧值
            _acqTaskSet.RefreshVariables();

            // 扫码器的缓冲区/通知变量也是按名字解析的：变量改名、删掉、换类型后要重算
            // 「不存在」「类型不对」「已在别处被引用」三种提示（同样放在早退之前）
            RefreshScannerReferenceHints();

            if (_monitorSet.Items.Count == 0)
            {
                return;
            }

            _monitorSet.ReResolveAll();

            // 已生成的行容器要同步类型框（置灰与否、显示的类型名都可能变了）。
            // 遍历**可视树**而不是 ItemContainerGenerator：容器未必已生成，
            // 且这里要的是模板根（Tag 上挂着 MonitorItem 的那个），容器层级并不保证。
            if (MonitorItemsControl != null)
            {
                foreach (FrameworkElement row in EnumerateRows(MonitorItemsControl))
                {
                    if (row.Tag is not MonitorItem item)
                    {
                        continue;
                    }

                    if (item.IsVariable)
                    {
                        ApplyVariableRowState(row, item);
                    }
                    else
                    {
                        SyncMonitorTypeWithElement(row, item, autoCorrect: false);
                    }
                }
            }

            // 不落盘：变量定义变了但监控行的持久化字段（变量名 / 元件）没变，
            // 类型与地址都是解析时从变量表现取的临时值，本来就不进 json
        }

        /// <summary>
        /// 变量名失焦（回车同效）：**唯一提交点**。
        /// <para>
        /// 打字途中**不校验**（需求）：一次打完再检查。不合法直接弹窗
        /// 「变量名称不合法」+ 具体规则，随后回退到上次有效名称 ——
        /// 模型与磁盘里都不留非法变量名。
        /// </para>
        /// </summary>
        private void CommitGlobalVarName(TextBox tb)
        {
            if (VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            string wanted = tb.Text.Trim();

            if (_globalVarSet.Rename(item, wanted, out string error))
            {
                tb.Text = item.Name;
                _globalVarSet.Save();

                // 改名会让引用旧名字的监控行失效，必须重解析一遍
                RefreshAfterVariableChange();
                return;
            }

            // 失败：先回退文本，再把规则说清楚。
            // 正文最前面是**用户刚输入的内容**（外加双引号，需求），一眼看出被拒的是哪一段；
            // 标题点出性质，正文后接具体规则。输入为空时不加引号。
            tb.Text = item.Name;
            string input = wanted.Length == 0 ? string.Empty : $"“{wanted}”";
            MessageBox.Show(
                this,
                $"{input}{error}",
                "变量名称不合法",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>变量名框失焦：交给唯一的提交点。</summary>
        private void GlobalVarName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                CommitGlobalVarName(tb);
            }
        }

        /// <summary>变量名框按回车：等同失焦，立刻校验。</summary>
        private void GlobalVarName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb)
            {
                return;
            }

            e.Handled = true;
            CommitGlobalVarName(tb);
        }

        /// <summary>
        /// 映射地址失焦：空 = 内存变量；非空则按驱动解析。
        /// <para>
        /// 解析失败**弹框告知**，因为地址是「元件 + 编号」的自由文本，用户看不出自己错在哪：
        /// 类型不符要说清「这个元件不能配这种类型」，软元件无效要说清「这个地址不存在」。
        /// </para>
        /// </summary>
        private void GlobalVarAddress_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            string wanted = tb.Text.Trim();
            item.Address = wanted;
            _globalVarSet.ResolveOnly(item, out AddressErrorKind kind);

            if (kind != AddressErrorKind.None)
            {
                // 失败时保留用户原样输入，方便就地改
                ShowGlobalVarAddressError(item, wanted, kind);
            }
            else if (tb.Text != item.Address)
            {
                // 解析成功时元件符号已归一大写（y27 → Y27），把规范形式回填到框里，
                // 与自由监控的名称框一致；不这么做，界面留着 y27、json 里却是 Y27
                tb.Text = item.Address;
            }

            _globalVarSet.Save();

            // 地址变了，引用该变量的监控行要跟着换地址
            RefreshAfterVariableChange();
        }

        /// <summary>
        /// 映射地址校验失败的提示框。
        /// <para>
        /// 文案本身在 <see cref="GlobalVariableRules.DescribeAddressError"/> —— 那是纯函数，
        /// 离线断言能直接核对，这里只负责弹出来。
        /// </para>
        /// </summary>
        private void ShowGlobalVarAddressError(GlobalVarItem item, string input, AddressErrorKind kind)
        {
            GlobalVariableRules.DescribeAddressError(
                kind, input, item.ResolveError, out string title, out string body);

            MessageBox.Show(this, body, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void GlobalVarComment_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || VarRowOf(tb) is not GlobalVarItem item)
            {
                return;
            }

            item.Comment = tb.Text;
            _globalVarSet.Save();
        }

        private void GlobalVarType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo || VarRowOf(combo) is not GlobalVarItem item)
            {
                return;
            }

            // 下拉项是 DataTypeSpec 对象（见 SyncGlobalVarTypeCombo），不是显示名字符串
            if (combo.SelectedItem is not DataTypeSpec picked)
            {
                return;
            }

            DataType matched = picked.Type;

            if (item.Model.Type == matched)
            {
                return;
            }

            // STRING 需要用户声明长度。
            // 与自由监控走**同一个**对话框与同一套参数（见 AskStringLength），
            // 不在这里另写一份文案与范围 —— 两处各写一份，改一处就悄悄不一致了。
            if (matched == DataType.String)
            {
                int? len = AskStringLength(item.Model.Length);

                if (len == null)
                {
                    SyncGlobalVarTypeCombo(combo, item);
                    return;
                }

                item.Model.Length = len.Value;
            }

            DataType oldType = item.Model.Type;
            item.Model.Type = matched;
            item.RaiseTypeChanged();

            // 只在**存储区真的变了**时才清空映射地址：
            // BOOL 走线圈、其余走寄存器，跨区换类型后旧地址必然校验失败；
            // 而 INT → REAL 这类同区换类型地址完全可用，清掉只是让用户重打一遍
            if (GlobalVariableRules.AddressInvalidatedBy(oldType, matched))
            {
                ClearGlobalVarAddress(combo, item);
            }

            // 格式必须随类型纠正（BOOL 只允许 BIN、STRING 只允许 ASCII）——
            // 本页不显示格式列，但模型仍要合法，否则读取/后续导出会拿到非法组合
            DataTypeSpec newSpec = DataTypes.Get(matched);
            if (!newSpec.AllowsFormat(item.Model.Format))
            {
                item.Model.Format = newSpec.DefaultFormat;
            }

            _globalVarSet.ResolveOnly(item);
            _globalVarSet.Save();
            RefreshGlobalVarView();

            // 类型变了，引用该变量的监控行要跟着换类型与读取长度
            RefreshAfterVariableChange();
        }

        /// <summary>把焦点移到指定变量的名称框（新增后连续录入用）。</summary>
        private void FocusGlobalVarRow(GlobalVarItem item)
        {
            if (GlobalVarItemsControl == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < GlobalVarItemsControl.Items.Count; i++)
                {
                    if (GlobalVarItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                            is not DependencyObject container)
                    {
                        continue;
                    }

                    if (container is FrameworkElement fe && fe.DataContext is GlobalVarItem rowItem
                        && ReferenceEquals(rowItem, item))
                    {
                        FindGlobalVarBox(container, "VarNameBox")?.Focus();
                        return;
                    }
                }
            }));
        }

    }
}
