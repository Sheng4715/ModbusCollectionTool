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
// 自由监控（P4-2）：行编辑/值写回/设备写入
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- 自由监控（P4-2） ----------

        /// <summary>刷新自由监控的计数与按钮可用性（增删后调用）。</summary>
        private void RefreshMonitorView()
        {
            if (MonitorItemsControl == null)
            {
                return;
            }

            int n = _monitorSet.Items.Count;

            // ⚠️ 表头**不随空列表隐藏**：它是这一页的列说明，也是「+」按钮的落点 ——
            // 藏起来会让人以为此页坏掉了，且没有地方能加第一项。
            if (MonitorEmptyHint != null)
            {
                MonitorEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 「全部删除」在空列表下无意义，置灰防空点（Style 里 IsEnabled=False 会一并去掉手型光标）
            if (ClearMonitorItemsButton != null)
            {
                ClearMonitorItemsButton.IsEnabled = n > 0;
            }

            // 行数变了（增/删/清空）也要重算 ↑↓ 的可用性
            UpdateMonitorMoveButtons();
        }

        // ---------- 排序（2026-10-31）：表头 ↑↓ 作用于选中行 ----------

        /// <summary>当前选中的监控行（null = 没选）。</summary>
        private MonitorItem? _selectedMonitorItem;

        /// <summary>点行内任意处即选中该行（点输入框时也会走到这里，但不影响编辑）。</summary>
        private void MonitorRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is MonitorItem item)
            {
                SelectMonitorItem(item);
            }
        }

        /// <summary>设置选中行并刷新按钮可用性（选中态不进模型、不落盘）。</summary>
        private void SelectMonitorItem(MonitorItem? item)
        {
            if (ReferenceEquals(_selectedMonitorItem, item))
            {
                return;
            }

            MonitorItem? previous = _selectedMonitorItem;
            _selectedMonitorItem = item;

            // 只动「旧选中」与「新选中」两个对象，不再遍历全表：
            // 行数多时每次点击都 O(n) 重设是白费，而且 IsSelected 的 setter 自身有同值短路，
            // 遍历只会产生大量无意义的属性读。
            if (previous != null)
            {
                previous.IsSelected = false;
            }

            if (item != null)
            {
                item.IsSelected = true;
            }

            UpdateMonitorMoveButtons();
        }

        /// <summary>
        /// 刷新 ↑↓ 的可用性：没选中 → 都灰；首行 → ↑ 灰；末行 → ↓ 灰。
        /// <para>置灰与点击拦截必须同源，否则会出现「按钮亮着但点了没反应」。</para>
        /// </summary>
        private void UpdateMonitorMoveButtons()
        {
            int count = _monitorSet?.Items?.Count ?? 0;

            int index = _selectedMonitorItem == null || _monitorSet?.Items == null
                ? -1
                : _monitorSet.Items.IndexOf(_selectedMonitorItem);

            bool has = index >= 0;

            if (MoveMonitorUpButton != null)
            {
                MoveMonitorUpButton.IsEnabled = has && index > 0;
            }

            if (MoveMonitorDownButton != null)
            {
                MoveMonitorDownButton.IsEnabled = has && index < count - 1;
            }
        }

        private void MoveMonitorUp_Click(object sender, RoutedEventArgs e) => MoveSelectedMonitor(-1);

        private void MoveMonitorDown_Click(object sender, RoutedEventArgs e) => MoveSelectedMonitor(+1);

        /// <summary>
        /// 把选中行上移 / 下移一格。
        /// <para>
        /// ⚠️ 除了顺序，还要让**轮询计划**跟着重算：`MonitorItemSet.Items` 的顺序
        /// 就是读块拼接的顺序，且行号变了序号也要重排（`Move` 内部已做）。
        /// </para>
        /// </summary>
        private void MoveSelectedMonitor(int delta)
        {
            if (_selectedMonitorItem == null || _monitorSet?.Items == null)
            {
                return;
            }

            int from = _monitorSet.Items.IndexOf(_selectedMonitorItem);
            if (from < 0)
            {
                return;
            }

            int to = from + delta;
            if (to < 0 || to > _monitorSet.Items.Count - 1)
            {
                return;
            }

            MonitorItem moving = _selectedMonitorItem;

            if (_monitorSet.Move(moving, to))
            {
                UpdateMonitorMoveButtons();
                MonitorItemsControl?.UpdateLayout();
                FocusMonitorRowName(moving);
            }
        }

        /// <summary>
        /// 删除全部监控项。此操作会**立即清空并落盘**，误点代价是整份配置，
        /// 故先弹确认框（统一走 <see cref="ThemeMessageDialog"/>，与其余对话框同一套外观）。
        /// </summary>
        private void ClearMonitorItems_Click(object sender, RoutedEventArgs e)
        {
            int n = _monitorSet.Items.Count;
            if (n == 0)
            {
                return;
            }

            if (!ThemeMessageDialog.Confirm(
                    this,
                    "删除全部监控项",
                    $"确定要删除全部 {n} 个监控项吗？此操作无法撤销。",
                    danger: true,
                    confirmText: "删除"))
            {
                return;
            }

            _monitorSet.Clear();
            _selectedMonitorItem = null;
            RefreshMonitorView();
        }

        private void AddMonitorItemButton_Click(object sender, RoutedEventArgs e)
        {
            // 新建项默认：当前驱动第一个寄存器元件 + 起始编号 + INT + DEC。
            // 直接给一个「能解析成功」的初值，避免一新增就标红。
            string element = "D";
            string index = "0";

            if (_currentDriver != null)
            {
                foreach (AddressArea a in _currentDriver.Areas)
                {
                    if (a.Area is ModbusArea.HoldingRegister or ModbusArea.InputRegister)
                    {
                        element = a.Element;
                        // 用 FormatName 取编号：X/Y 是八进制元件，直接 ToString 会给出十进制值
                        index = a.FormatName(a.StartIndex)[element.Length..];
                        break;
                    }
                }
            }

            var model = new DataPoint
            {
                Element = element,
                Index = index,
                Type = DataType.Int,
                Format = DisplayFormat.Dec,
                Comment = string.Empty,
            };

            MonitorItem item = _monitorSet.Add(model);
            RefreshMonitorView();

            // 新增后滚到底部并聚焦元件名称框，便于连续录入
            MonitorItemsControl?.UpdateLayout();
            FocusMonitorRowName(item);
        }

        /// <summary>把焦点移到指定行的元件名称框（新增项后连续录入用）。</summary>
        private void FocusMonitorRowName(MonitorItem item)
        {
            if (MonitorItemsControl == null)
            {
                return;
            }

            // 行容器是 ItemsControl 生成的，需等布局完成后再找
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < MonitorItemsControl.Items.Count; i++)
                {
                    if (MonitorItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                            is not DependencyObject container)
                    {
                        continue;
                    }

                    if (container is FrameworkElement fe && fe.DataContext is MonitorItem rowItem
                        && ReferenceEquals(rowItem, item))
                    {
                        FindNameBox(container)?.Focus();
                        return;
                    }
                }
            }));
        }

        /// <summary>在可视树里找元件名称输入框（DataTemplate 里的控件不能按名字直接取）。</summary>
        private static TextBox? FindNameBox(DependencyObject root)
        {
            if (root is TextBox tb && tb.Name == "NameBox")
            {
                return tb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                TextBox? found = FindNameBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void RemoveMonitorItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is MonitorItem item)
            {
                // 删掉的正好是选中行 → 清掉选中，免得 ↑↓ 作用在一个已不在列表里的对象上
                if (ReferenceEquals(_selectedMonitorItem, item))
                {
                    _selectedMonitorItem = null;
                }

                _monitorSet.Remove(item);
                RefreshMonitorView();
            }
        }

        /// <summary>
        /// 行内控件与行数据的桥接：DataTemplate 里的控件拿不到行对象，
        /// 统一用 Tag 承载行引用（在 <see cref="MonitorRow_Loaded"/> 里挂上）。
        /// </summary>
        private static MonitorItem? RowOf(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.DataContext is MonitorItem fromContext)
                {
                    return fromContext;
                }

                if (fe.Tag is MonitorItem fromTag)
                {
                    return fromTag;
                }
            }

            return null;
        }

        /// <summary>
        /// 元件名称输入框**失焦**（回车同效）：**这是唯一提交点** —— 归一文本、重解析、落盘。
        /// <para>
        /// 打字途中**不校验**（需求）：用户不必在输入 <c>D</c>、<c>D1</c> 时被红字打断，
        /// 一次打完再检查。检查时机是失焦或回车。
        /// </para>
        /// <para>
        /// 三种结果：
        /// ① 合法且能解析 → 提交、落盘，下一拍轮询开始采集；
        /// ② 名称不合法（拆不出元件 / 元件不在该驱动 / 编号越界）→
        /// <b>弹窗「不是有效软元件或变量」</b>，且**不提交**（保持上次有效值）；
        /// ③ 是合法变量但不能采集（没有映射地址 / 地址无效）→ 弹窗说明具体原因。
        /// </para>
        /// <para>
        /// 不合法的配置**绝不落盘**：否则下次启动会带着一个永远读不到的项。
        /// </para>
        /// </summary>
        private void CommitMonitorName(TextBox tb)
        {
            if (RowOf(tb) is not MonitorItem item)
            {
                return;
            }

            string previous = item.Name;
            string typed = tb.Text;

            // 系统变量（P2.3）：不在全局变量表里，不拦的话它会一路掉到元件解析，
            // 报成「不是有效软元件或变量」—— 原因不对，用户会去改元件名。
            // 这里单独说清「不能用」，而不是「不存在」。
            if (SystemVariable.Find(typed.Trim()) != null)
            {
                tb.Text = previous;
                ThemeMessageDialog.Info(
                    this,
                    "系统变量",
                    $"“{typed.Trim()}”是系统变量，不能在自由监控中使用。");
                return;
            }

            if (!item.SetName(typed, out _))
            {
                // ②-1 拆不出「元件 + 编号」，且不是已定义的变量名
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);
                ShowInvalidMonitorName(typed);
                return;
            }

            // 归一（元件符号大写 / 变量名原样）后回填输入框
            string canonical = item.Name;
            if (tb.Text != canonical)
            {
                tb.Text = canonical;
            }

            // 名称归一后与落盘值一致、且当前没有错误态 → 真的什么都没变，不必提交。
            // 注意判据用 previous（SetName 之前的值）而不是"模型当前值"，
            // 否则一旦模型被别处改过就会误判"没变化"而漏掉落盘。
            if (canonical == previous && !item.HasError)
            {
                return;
            }

            // 变量行：类型由变量表决定，**不能**按元件反推类型（那会把变量声明改掉）
            if (item.IsVariable)
            {
                item.Resolve(_currentDriver!, out string varError);
                if (item.HasError)
                {
                    // ③ 变量本身合法，只是**不能用于采集**
                    ThemeMessageDialog.Info(
                        this,
                        "变量无法采集",
                        $"变量「{canonical}」不可用于监控：\n\n{varError}");

                    tb.Text = previous;
                    item.SetName(previous, out _);
                    _monitorSet.ResolveOnly(item);
                    return;
                }

                _monitorSet.AfterChange(item);
                if (FindMonitorRowRoot(tb) is FrameworkElement varRow)
                {
                    ApplyVariableRowState(varRow, item);
                }

                return;
            }

            // 元件变了 → 线圈/寄存器归属可能变，类型候选与当前类型要跟着走
            if (tb.Parent is FrameworkElement row && canonical != previous)
            {
                SyncMonitorTypeWithElement(row, item);
            }

            // ②-2 语法成立、但软元件本身无效（符号不在驱动 / 编号越界 / 八进制非法）
            item.Resolve(_currentDriver!, out _);
            if (item.HasError)
            {
                tb.Text = previous;
                item.SetName(previous, out _);
                _monitorSet.ResolveOnly(item);
                ShowInvalidMonitorName(typed);
                return;
            }

            // ① 提交：落盘，下一拍轮询即开始采集该地址
            _monitorSet.AfterChange(item);
        }

        /// <summary>
        /// 名称无效的统一提示（需求定稿文案）。
        /// <para>
        /// 正文最前面是**用户刚输入的内容**（外加双引号），后接定稿文案。
        /// 输入为空时不加引号 —— 空的 <c>“”</c> 只是噪音。
        /// </para>
        /// </summary>
        private void ShowInvalidMonitorName(string? input)
        {
            string text = (input ?? string.Empty).Trim();
            string prefix = text.Length == 0 ? string.Empty : $"“{text}”";

            ThemeMessageDialog.Info(this, "输入无效", $"{prefix}不是有效软元件或变量");
        }

        /// <summary>
        /// 元件名称框加载：挂变量名补全（P7.1）。
        /// <para>
        /// 挂在 <c>Loaded</c> 而不是 XAML 里直接写，是因为这一行在 <c>DataTemplate</c> 里 ——
        /// 每个监控项各自一份，没有可以一次性写死的那个文本框。
        /// </para>
        /// <para>
        /// 用 <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
        /// 记住「这个框已经挂过了」：模板复用/虚拟化会让同一个框多次 Loaded，
        /// 重复挂载会让同一次按键被处理两遍（表现是补全连跳两格）。
        /// </para>
        /// </summary>
        private void MonitorName_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb)
            {
                return;
            }

            if (_nameCompletions.TryGetValue(tb, out _))
            {
                return;
            }

            // 候选现算：变量表随时可变，挂载时抓快照会联想出已删掉的变量
            var popup = Ui.CompletionPopup.Attach(tb, () => CompletionSource.ForMonitorName(GlobalVarModels));
            _nameCompletions.Add(tb, popup);
        }

        /// <summary>元件名称框失焦：交给唯一的提交点。</summary>
        private void MonitorName_LostFocus(object sender, RoutedEventArgs e)
        {
            // 补全列表开着时的失焦，是「用户正在点候选」，不是「输入结束」——
            // 此刻提交会把半截名字（如 "sys_"）当成错误弹窗。
            // 弹窗本身用 WS_EX_NOACTIVATE 不抢焦点，正常情况走不到这里；
            // 这一句是兜底：万一某些输入法/主题下焦点还是丢了，也不至于误报。
            if (sender is TextBox tb && _nameCompletions.TryGetValue(tb, out Ui.CompletionPopup? popup) && popup.IsOpen)
            {
                return;
            }

            if (sender is TextBox box)
            {
                CommitMonitorName(box);
            }
        }

        /// <summary>
        /// 元件名称框按回车：等同失焦，立刻校验。
        /// <para>
        /// 回车不会自己移走焦点，故直接调提交逻辑；之后真正的 LostFocus 再来一次时，
        /// 名称已归一且无错误，会在"没变化"处提前返回，不会重复落盘。
        /// </para>
        /// </summary>
        private void MonitorName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb)
            {
                return;
            }

            e.Handled = true;
            CommitMonitorName(tb);
        }

        /// <summary>
        /// 变量行：类型下拉框**置灰**并显示变量声明的类型。
        /// <para>
        /// 类型来自变量表，改它等于绕过变量表另立一份定义，两边立刻不一致 ——
        /// 故这里只展示、不开放编辑。要改类型请去「全局变量」页。
        /// </para>
        /// </summary>
        private static void ApplyVariableRowState(FrameworkElement row, MonitorItem item)
        {
            if (row.FindName("TypeCombo") is not ComboBox typeCombo)
            {
                return;
            }

            // 候选集只放「变量声明的这一个类型」：置灰后用户点不开，
            // 但闭合态仍要显示正确文字（模板绑的是数据上下文的 TypeDisplayName）
            typeCombo.ItemsSource = new[] { DataTypes.Get(item.Model.Type) };
            typeCombo.SelectedIndex = 0;
            typeCombo.IsEnabled = item.IsTypeEditable;
        }

        /// <summary>深度遍历可视树，取出所有「Tag 上挂着 MonitorItem」的行根。</summary>
        private static IEnumerable<FrameworkElement> EnumerateRows(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Tag is MonitorItem)
                {
                    yield return fe;
                }

                foreach (FrameworkElement nested in EnumerateRows(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// 从行内某个控件往上找该行的模板根（<c>MonitorRow_Loaded</c> 把行对象挂在它的 Tag 上）。
        /// </summary>
        private static FrameworkElement? FindMonitorRowRoot(DependencyObject from)
        {
            DependencyObject? node = from;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.Tag is MonitorItem)
                {
                    return fe;
                }

                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        /// <summary>
        /// 按「元件名称」决定该行可选的数据类型。
        /// <para>
        /// 方案 2 里元件是自由文本，故<b>由元件反推类型</b>（方案 1 是反过来的：
        /// 先选类型、元件下拉只列该类型的元件）。不纠正的话，把 <c>D100</c> 改成 <c>X0</c>
        /// 会得到「驱动未定义元件 X」这种驴唇不对马嘴的报错。
        /// </para>
        /// </summary>
        /// <param name="autoCorrect">
        /// true = 用户刚改过元件，类型与元件不符时自动纠正；
        /// false = 载入持久化数据，只摆好候选、不替用户改声明（不匹配就标红让人看见）。
        /// </param>
        private void SyncMonitorTypeWithElement(FrameworkElement row, MonitorItem item, bool autoCorrect = true)
        {
            IReadOnlyList<ModbusArea> areas = _currentDriver?.AreasOfElement(item.Model.Element)
                ?? Array.Empty<ModbusArea>();

            bool coilElement = areas.Contains(ModbusArea.Coil);
            bool known = areas.Count > 0;

            DataTypeSpec spec = DataTypes.Get(item.Model.Type);

            // 元件不在驱动里（打了一半的输入）→ 保留当前类型所在的候选集，别让下拉框突然空掉
            if (known && autoCorrect && spec.CoilOnly != coilElement)
            {
                item.Model.Type = coilElement ? DataType.Bool : DataType.Int;
                spec = DataTypes.Get(item.Model.Type);
                item.Model.Format = spec.DefaultFormat;
            }

            if (row.FindName("TypeCombo") is ComboBox typeCombo)
            {
                // 从变量行改回元件行时，类型框要恢复可用（变量行里它是灰的）
                typeCombo.IsEnabled = item.IsTypeEditable;

                // 候选集跟随「元件」；但元件未知（输入未完成）或载入时声明与元件不符时跟随「当前类型」——
                // 保证下拉框里始终能看到并选中已声明的类型，不会显示成空白
                bool useCoil = known && autoCorrect ? coilElement : spec.CoilOnly;
                typeCombo.ItemsSource = useCoil ? DataTypes.CoilTypes : DataTypes.RegisterTypes;
                typeCombo.SelectedItem = spec;
            }

            ApplyMonitorRowState(row, item);
        }

        private void MonitorType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cb || cb.SelectedItem is not DataTypeSpec spec)
            {
                return;
            }

            MonitorItem? item = RowOf(cb);
            if (item == null || item.Model.Type == spec.Type)
            {
                return;
            }

            // ⚠️ 不要在 SelectionChanged 的调用栈里直接 ShowDialog：
            // 此刻下拉 Popup 尚未关闭，模态窗口会在「应用仍在处理 Popup 关闭」的
            // 派发上下文里创建，窗口拿不到显示/激活，表现为弹框不按预期出现。
            //
            // 解法：先让 Popup 关掉、并等一轮渲染真正落定，再在**渲染之后**的
            // 独立派发上下文里弹框。用 Render 优先级 + 再转一次 Normal，
            // 确保完全脱离 Popup 的生命周期。
            //
            // ⚠️ 也不要在延后之前动 cb.IsDropDownOpen / cb.SelectedItem：
            // 那会把本次选择撤销，延后执行时类型已经一致，于是整件事被跳过
            // （实测：加了 IsDropDownOpen=false 之后完全不弹了）。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() =>
                {
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Normal,
                        new Action(() => ApplyTypeChangeWithPrompt(cb, item, spec)));
                }));
        }

        /// <summary>
        /// 换类型：选 STRING 时先弹框问长度，取消则整件事作废（类型与格式都不动）。
        /// 由 <see cref="MonitorType_SelectionChanged"/> 用 BeginInvoke 延后调用。
        /// </summary>
        private void ApplyTypeChangeWithPrompt(ComboBox cb, MonitorItem item, DataTypeSpec spec)
        {
            // 延后执行期间用户可能又改了；类型已一致就不用再问
            if (item.Model.Type == spec.Type)
            {
                return;
            }

            DataType previousType = item.Model.Type;

            if (spec.HasVariableLength)
            {
                // 与全局变量共用同一个入口，参数不在这里重复定义
                int? len = AskStringLength(item.Model.Length);

                if (len == null)
                {
                    // 作废：把下拉框退回原类型
                    cb.SelectedItem = DataTypes.Get(previousType);
                    return;
                }

                item.Model.Length = len.Value;
            }

            item.Model.Type = spec.Type;

            // 换类型后原格式可能不再合法（如 INT 的 DEC 换到 STRING），
            // 自动落到该类型的默认格式，避免留下一个「非法组合」的项
            if (!spec.AllowsFormat(item.Model.Format))
            {
                item.Model.Format = spec.DefaultFormat;
            }

            _monitorSet.AfterChange(item);

            // 换类型会改变「格式候选」，就地更新本行控件，
            // 不重建整行 —— 重建会打断用户正在进行的输入
            if (cb.Parent is FrameworkElement row)
            {
                ApplyMonitorRowState(row, item);
            }

            RefreshMonitorView();
        }

        private void MonitorFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is DisplayFormat fmt)
            {
                MonitorItem? item = RowOf(cb);
                if (item == null || item.Model.Format == fmt)
                {
                    return;
                }

                item.Model.Format = fmt;
                _monitorSet.AfterChange(item);
            }
        }

        /// <summary>
        /// 每个「当前值」输入框最近一次**已提交**的文本（键为控件本身）。
        /// 用途：回车与失焦都会触发提交，靠它识别「同一次编辑」避免重复写。
        /// <para>
        /// ⚠️ **必须用弱表，不能用普通 Dictionary**：键是 <c>DataTemplate</c> 里每行各自的
        /// TextBox，删行 / 清空后该 TextBox 会离开可视树，但普通字典仍**强引用**它 ——
        /// 而 <c>TextBox.DataContext → MonitorItem → DataPoint</c> 是一条完整的引用链，
        /// 整条链都会因此无法回收，直到关窗。同文件的 <c>_nameCompletions</c> 正是
        /// 为规避同一类问题才用弱表，这里当初漏了。
        /// </para>
        /// </summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, string>
            _lastCommittedValue = new();

        /// <summary>
        /// 「当前值」格获得焦点：标记为编辑中，轮询回填会跳过该行，
        /// 否则用户输入到一半就被下一拍读值覆盖。
        /// </summary>
        private void MonitorValue_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box && RowOf(box) is MonitorItem item)
            {
                item.IsEditing = true;

                // 新一轮编辑：清掉上一轮的去重记录。
                // 没有这一步，用户写入 100 成功后再想写一次 100 会被当成重复而静默跳过。
                // 放在 GotFocus（而非写入成功后）是因为：要重新输入必然先聚焦，
                // 而同一轮编辑内的「回车后失焦」不会再次触发 GotFocus，去重仍然有效。
                _lastCommittedValue.Remove(box);

                // 选中全部，便于直接覆盖输入（与常见 PLC 上位机的一致做法）
                box.SelectAll();
            }
        }

        /// <summary>「当前值」格失去焦点：解除编辑标记并提交写入。</summary>
        private void MonitorValue_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box && RowOf(box) is MonitorItem item)
            {
                item.IsEditing = false;
                _ = CommitMonitorValueAsync(box, item);
            }
        }

        /// <summary>「当前值」格按键：回车提交写入（与失焦提交等价，但不需先点别处）。</summary>
        private void MonitorValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box || RowOf(box) is not MonitorItem item)
            {
                return;
            }

            e.Handled = true;
            _ = CommitMonitorValueAsync(box, item);
        }

        /// <summary>
        /// 提交「当前值」格的写入。
        /// <para>
        /// 流程：文本 → 编码（<see cref="MonitorItemSet.TryBuildWrite"/>）→ 按区选 FC05/06/16 → 写。
        /// **不回读**：成功后由下一拍轮询自然刷新（需求选择），故只清错误标记，
        /// 显示值保持不变 —— 不做「假装成功」的本地改写。
        /// </para>
        /// <para>
        /// 与读取不同，写失败**不改动显示值**：让用户还能看到自己输入的内容并修正。
        /// </para>
        /// </summary>
        private async Task CommitMonitorValueAsync(TextBox box, MonitorItem item)
        {
            ModbusTcpClient? modbus = _session.Client;
            CommDriver? driver = _currentDriver;

            string text = box.Text;

            // 回车与失焦是两条路径，会先后各触发一次提交（回车后焦点转移会连带引得失焦）。
            // 同一次编辑只写一次：值与上次提交的相同就跳过。
            if (_lastCommittedValue.TryGetValue(box, out string? last) && last == text)
            {
                return;
            }
            // 内存变量：没有映射地址，没有寄存器可写，**也不需要连接** ——
            // 值直接存进变量本身，一个报文都不发。
            // 放在连接检查之前，否则没连 PLC 时连内存变量都改不了。
            if (item.IsMemoryVariable)
            {
                if (!item.TrySetMemoryValue(text, _byteOrders, out string memoryError))
                {
                    item.WriteError = memoryError;
                    item.IsEditing = true;
                    _lastCommittedValue.AddOrUpdate(box, text); // 同一份非法内容不重复报告
                    return;
                }

                _lastCommittedValue.AddOrUpdate(box, text);
                item.WriteError = null;
                item.ReadError = null;
                item.IsEditing = false;
                return;
            }

            // 按需求：建立连接后即可写入，不要求自由监控分段可见。
            if (!_session.IsConnected || modbus == null || driver == null)
            {
                item.WriteError = "未连接，无法写入";
                return;
            }

            if (!MonitorItemSet.TryBuildWrite(item, text, _byteOrders, out WritePlan plan, out string error))
            {
                // 编码失败（类型不匹配、越界、非法字面量）——本地就能判定，不发报文。
                //
                // ⚠️ 此时**保持编辑态**：否则下一拍轮询立刻用 PLC 的真实值覆盖，
                // 用户还没看清错误提示，自己输的内容就没了。
                // 保持编辑态 = 值留在界面上、错误提示留在 ToolTip 里，用户可继续改。
                item.WriteError = error;
                item.IsEditing = true;
                _lastCommittedValue.AddOrUpdate(box, text); // 同一份非法内容不重复报告
                return;
            }

            // 记下本次提交的文本：回车与失焦会先后各触发一次，靠它去重。
            // ⚠️ 必须在**发报文之前**记下（而不是成功之后）——
            // 否则第一次成功后清掉记录，紧接着的失焦路径会当作新提交又写一次。
            _lastCommittedValue.AddOrUpdate(box, text);

            try
            {
                await WriteToDeviceAsync(modbus, plan).ConfigureAwait(true);

                // 成功：清掉错误标记、**解除编辑态**，
                // 之后由下一拍轮询自然刷成 PLC 的真实值（不回读，按需求）。
                item.WriteError = null;
                item.ReadError = null;
                item.IsEditing = false;
            }
            catch (OperationCanceledException)
            {
                item.WriteError = "写入已取消";
                item.IsEditing = true;
            }
            catch (Exception ex)
            {
                // 只读区（FC02/FC04）会走到这里 —— 从站回异常码，原样展示原因。
                // 同样保持编辑态，让用户看到自己输的内容与失败原因。
                item.WriteError = ClassifyWriteError(ex);
                item.IsEditing = true;
                _lastCommittedValue.AddOrUpdate(box, text); // 同一份内容不重复发失败请求
            }
        }

        /// <summary>按存储区选择写功能码：线圈 FC05，寄存器按长度选 FC06（单）或 FC16（多）。</summary>
        private static Task WriteToDeviceAsync(ModbusTcpClient modbus, WritePlan plan)
        {
            if (plan.Address < 0)
            {
                throw new IOException("地址无效，无法写入");
            }

            ushort address = (ushort)plan.Address;

            if (plan.IsCoil)
            {
                return modbus.WriteSingleCoilAsync(address, plan.CoilValue);
            }

            ushort[] regs = plan.Registers ?? throw new IOException("写入内容为空");
            return regs.Length == 1
                ? modbus.WriteSingleRegisterAsync(address, regs[0])
                : modbus.WriteMultipleRegistersAsync(address, regs);
        }

        /// <summary>
        /// 写入失败的原因文案。与 <see cref="ClassifyDisconnectReason"/> 不同：
        /// 写失败多为「从站拒绝」（异常码），不涉及断线归类，故单独处理。
        /// </summary>
        private static string ClassifyWriteError(Exception ex)
        {
            if (ex is ModbusResponseException mre)
            {
                // 码 2 出现在只读区是最常见的情况，给出可操作的解释
                return mre.ExceptionCode == 2
                    ? "该地址不可写（输入寄存器 / 离散输入为只读区）"
                    : mre.Message;
            }

            return ClassifyDisconnectReason(ex.Message);
        }

        /// <summary>
        /// 行初始化：把行对象挂到 Tag、填入元件名称、按元件推导类型候选与格式候选。
        /// 候选项按行构造（随元件与类型变），故不放在 XAML 里静态定义。
        /// </summary>
        private void MonitorRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement row || row.DataContext is not MonitorItem item)
            {
                return;
            }

            row.Tag = item;

            // 输入框文本：变量行是变量名，元件行是 HD100 这样的元件名。
            // 文本已一致时不写，避免触发无谓的落盘
            if (row.FindName("NameBox") is TextBox nameBox && nameBox.Text != item.Name)
            {
                nameBox.Text = item.Name;
            }

            // 变量行：类型来自变量表，下拉框置灰只展示，不再按元件反推候选
            if (item.IsVariable)
            {
                ApplyVariableRowState(row, item);
                ApplyMonitorRowState(row, item);
                return;
            }

            // 载入时不自动纠正类型：持久化里的「元件↔类型」不匹配要留给用户看见（标红），
            // 悄悄改掉用户的声明比报错更糟
            SyncMonitorTypeWithElement(row, item, autoCorrect: false);
        }

        /// <summary>
        /// 让行内控件的可见性/候选项与该行当前类型保持一致（初始化时与换类型后都调）。
        /// 集中在一处，避免「换类型」路径漏掉某个控件的联动。
        /// </summary>
        private static void ApplyMonitorRowState(FrameworkElement row, MonitorItem item)
        {
            DataTypeSpec spec = DataTypes.Get(item.Model.Type);

            if (row.FindName("FormatCombo") is ComboBox formatCombo)
            {
                formatCombo.ItemsSource = spec.AllowedFormats;
                formatCombo.SelectedItem = item.Model.Format;
            }
        }

    }
}
