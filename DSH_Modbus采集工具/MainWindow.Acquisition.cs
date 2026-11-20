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
// 数据管理页分段 + 采集任务组（P3.1）+ 采集执行（刷新/触发/写入）
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- 数据管理页分段（P3.1 / P3.3） ----------

        /// <summary>
        /// 数据管理页分段切换：数据采集 / 全局变量 / 系统变量。
        /// <para>
        /// ⚠️ 2026-11-04 起**只有三档**：原第四档「扫码器」已并入
        /// 「设备连接 → 扫码器连接」（连接与交互参数同处一行的展开块，见
        /// <c>MainWindow.Scanner.cs</c>）。不要再把第四档加回来。
        /// </para>
        /// </summary>
        private void AcqView_Checked(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 期间这个处理器会被 IsChecked 触发，
            // 那时靠后的面板还没创建 —— 全部判空，缺一个就整体跳过（下面各控件也都有兜底）
            if (AcquisitionDataPanel == null || GlobalVarPanel == null || SystemVarPanel == null
                || AcquisitionDataSegment == null)
            {
                return;
            }

            bool varView = GlobalVarSegment?.IsChecked == true;
            bool sysView = SystemVarSegment?.IsChecked == true;

            // 本页只管理变量定义，不采集值（值/格式的读写属后续阶段）；
            // 系统变量连定义都不能改 —— 它是写死的清单，这里只负责摆出来。
            AcquisitionDataPanel.Visibility = varView || sysView
                ? Visibility.Collapsed
                : Visibility.Visible;
            GlobalVarPanel.Visibility = varView ? Visibility.Visible : Visibility.Collapsed;
            SystemVarPanel.Visibility = sysView ? Visibility.Visible : Visibility.Collapsed;

            if (varView)
            {
                RefreshGlobalVarView();
            }
        }

        // ---------- 采集任务组（P3.1，本版只做 UI） ----------

        /// <summary>刷新任务组计数、空提示与「全部删除」可用性。</summary>
        private void RefreshAcquisitionView()
        {
            if (AcquisitionTaskItemsControl == null)
            {
                return;
            }

            int n = _acqTaskSet.Items.Count;

            // 表头不随空列表隐藏 —— 它是列说明，也是「+」的落点
            if (AcquisitionEmptyHint != null)
            {
                AcquisitionEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ClearAcquisitionTasksButton != null)
            {
                ClearAcquisitionTasksButton.IsEnabled = n > 0;
            }

            // 数据项 ↑↓ 的可用性也要在这里刷：XAML 里默认 IsEnabled 是 true，
            // 列表载入 / 增删后不重算就会「没选任何项、两个箭头却都亮着」。
            // ⚠️ 这里只覆盖「增删**任务组**」这条路；增删**数据项**走另外三个处理器，
            // 它们不经本方法，必须各自调 SyncAcquisitionItemSelection()。
            UpdateAcquisitionItemMoveButtons();

            // 任务组增删后要重算「哪些变量需要常驻采集」—— 漏掉这里的话，
            // 新加的组要等到下次改表达式才真正开始采
            RefreshAcquisitionGlobals();

            // 总览页「数据采集」视图的任务组下拉框也按当前配置重填（改名/停用/增删即时反映）
            //
            // ⚠️ **不要加「视图可见才刷新」的判断**（2026-11-03 修）：用户正是在
            // **数据管理页**改任务组，那一刻 AcquisitionDataView 一定是 Collapsed，
            // 加了判断等于永不生效 —— 表现为「改完名称要切一次当前视图才刷新」。
            // 重填只是把若干 ComboBoxItem 换一遍，且内部已经用 _acqComboRebuilding
            // 挡住了 SelectionChanged 的副作用，开销可以忽略。
            RefreshAcqGroupCombo();
        }

        private void AddAcquisitionTask_Click(object sender, RoutedEventArgs e)
        {
            _acqTaskSet.Add();
            RefreshAcquisitionView();
            AcquisitionTaskItemsControl?.UpdateLayout();
        }

        private void ClearAcquisitionTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_acqTaskSet.Items.Count == 0)
            {
                return;
            }

            if (!ThemeMessageDialog.Confirm(
                    this,
                    "删除全部任务组",
                    $"确定删除全部 {_acqTaskSet.Items.Count} 个任务组？",
                    danger: true,
                    confirmText: "删除"))
            {
                return;
            }

            _acqTaskSet.Clear();
            RefreshAcquisitionView();
        }

        // ---------- 数据项手动排序（2026-11-11）：子表头 ↑↓ 作用于选中项 ----------
        // 口径与「全局变量」表完全一致：选中态只是界面态、不落盘；顺序即时落盘
        // （json 数组次序就是顺序本身，不需要额外排序字段）。
        // ⚠️ 排的是**任务组展开后的数据项**，不是任务组本身。

        /// <summary>当前选中的数据项行（null = 没有选中）。</summary>
        private AcquisitionItemRow? _selectedAcquisitionItem;

        /// <summary>点击数据项行 → 选中它（行底色由模板里的 DataTrigger 绑 IsSelected）。</summary>
        private void AcquisitionItemRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AcquisitionItemRow row)
            {
                SelectAcquisitionItem(row);
            }
        }

        /// <summary>
        /// 设置选中数据项并刷新 ↑↓ 可用性。
        /// <para>
        /// ⚠️ 选中不能影响行内的编辑框与删除按钮：只改 <c>IsSelected</c>，
        /// 不拦事件、不设 <c>e.Handled</c>。
        /// </para>
        /// </summary>
        private void SelectAcquisitionItem(AcquisitionItemRow? row)
        {
            if (ReferenceEquals(_selectedAcquisitionItem, row))
            {
                return;
            }

            _selectedAcquisitionItem = row;

            // 行底色绑 IsSelected，故把所有组的所有子行重算一遍（切换任务组时旧组的行也要清掉）
            if (_acqTaskSet?.Items != null)
            {
                foreach (AcquisitionTaskItem task in _acqTaskSet.Items)
                {
                    foreach (AcquisitionItemRow child in task.Items)
                    {
                        child.IsSelected = ReferenceEquals(child, row);
                    }
                }
            }

            UpdateAcquisitionItemMoveButtons();
        }

        /// <summary>
        /// 刷新 ↑↓ 可用性：没选中 → 都灰；首项 → ↑ 灰；末项 → ↓ 灰。
        /// <para>
        /// ⚠️ 按钮在 <c>DataTemplate</c> 里（**每展开一个任务组就有一对**），
        /// 所以没有窗口级字段可用，只能遍历已生成的行容器去找那一对
        /// （见 <see cref="UpdateMoveButtonsForTask"/>）。
        /// ⚠️ 容器未必已生成（组没展开、或行被虚拟化掉）—— 那种情况找不到按钮，
        /// 靠 XAML 里的 <c>IsEnabled="False"</c> 默认值兜住（见 MainWindow.xaml）。
        /// </para>
        /// </summary>
        private void UpdateAcquisitionItemMoveButtons() =>
            UpdateMoveButtonsForTask(OwnerOfSelectedItem(out _, out _));

        /// <summary>刷新某个任务组子表表头上那一对 ↑↓ 的可用性。</summary>
        private void UpdateMoveButtonsForTask(AcquisitionTaskItem? owner)
        {
            if (AcquisitionTaskItemsControl == null)
            {
                return;
            }

            for (int i = 0; i < AcquisitionTaskItemsControl.Items.Count; i++)
            {
                if (AcquisitionTaskItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                        is not DependencyObject container
                    || container is not FrameworkElement fe
                    || fe.DataContext is not AcquisitionTaskItem task)
                {
                    continue;
                }

                Button? up = FindItemHeaderButton(fe, "MoveAcquisitionItemUpButton");
                Button? down = FindItemHeaderButton(fe, "MoveAcquisitionItemDownButton");
                if (up == null && down == null)
                {
                    continue;
                }

                bool isOwner = ReferenceEquals(task, owner);
                int index = isOwner ? task.Items.IndexOf(_selectedAcquisitionItem!) : -1;
                int count = task.Items.Count;

                if (up != null)
                {
                    up.IsEnabled = isOwner && index > 0;
                }

                if (down != null)
                {
                    down.IsEnabled = isOwner && index >= 0 && index < count - 1;
                }
            }
        }

        /// <summary>在一个任务组的模板实例里按名字找控件（子表表头上的 ↑↓）。</summary>
        private static Button? FindItemHeaderButton(DependencyObject root, string name) =>
            FindByName(root, name) as Button;

        /// <summary>按 <c>x:Name</c> 在模板实例的视觉树里向下找（模板不是命名作用域，只能遍历）。</summary>
        private static FrameworkElement? FindByName(DependencyObject root, string name)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Name == name)
                {
                    return fe;
                }

                FrameworkElement? found = child is DependencyObject d ? FindByName(d, name) : null;
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 找到选中数据项所属的任务组，并给出它在那个组里的下标与该组总项数。
        /// <para>
        /// 选中项可能已经被删 / 所属组已经被删 —— 那时返回 <c>null</c>，两个箭头随之变灰。
        /// </para>
        /// </summary>
        private AcquisitionTaskItem? OwnerOfSelectedItem(out int index, out int count)
        {
            index = -1;
            count = 0;

            if (_selectedAcquisitionItem == null || _acqTaskSet?.Items == null)
            {
                return null;
            }

            foreach (AcquisitionTaskItem task in _acqTaskSet.Items)
            {
                int i = task.Items.IndexOf(_selectedAcquisitionItem);
                if (i >= 0)
                {
                    index = i;
                    count = task.Items.Count;
                    return task;
                }
            }

            return null;
        }

        private void MoveAcquisitionItemUp_Click(object sender, RoutedEventArgs e) =>
            MoveSelectedAcquisitionItem(-1);

        private void MoveAcquisitionItemDown_Click(object sender, RoutedEventArgs e) =>
            MoveSelectedAcquisitionItem(+1);

        /// <summary>
        /// 把选中的数据项上移 / 下移一格，移完保持选中。
        /// <para>
        /// 按钮在模板里，所属任务组从**当前选中项**反查（而不是从发送者）：
        /// ↑↓ 只会作用于选中项，而选中项一定属于某个组，比沿视觉树往上找更直接。
        /// </para>
        /// </summary>
        private void MoveSelectedAcquisitionItem(int delta)
        {
            AcquisitionTaskItem? owner = OwnerOfSelectedItem(out int from, out int count);
            if (owner == null || from < 0)
            {
                return;
            }

            int to = from + delta;
            if (to < 0 || to > count - 1)
            {
                return;
            }

            AcquisitionItemRow moving = _selectedAcquisitionItem!;

            // Move 同时改 ObservableCollection（画面重排）与模型 List（落盘顺序）并重排 # 号
            owner.MoveItem(moving, to);

            // ⚠️ **只落盘，不刷总览视图**（2026-11-11 优化）：早先这里调 RefreshAcquisitionView()，
            // 它链式做三件重活 —— 重建整个实时画布（销毁并重建全部卡片视觉树，即使当前
            // 不在总览页）、清空重填任务组下拉框、重扫所有组的变量引用。连点 5 次下移就重建 5 遍。
            //
            // 而调序**此刻**对总览没有任何可见影响：
            //   · 下拉框只列组名 / 启用 / 数据记录，与项顺序无关；
            //   · 历史表格读的是文件名，列序变化要等下一次写入生成 `_2.csv` 才体现——
            //     那是采集侧的 Append 自己会切文件，与这里无关；
            //   · 实时画布按 Id 存位置、按绝对坐标摆放，卡片顺序不影响呈现，
            //     切到总览时本来就会重建一次。
            _acqTaskSet.Save();

            UpdateAcquisitionItemMoveButtons();
        }

        /// <summary>
        /// 删除单个任务组（行内 ×，在「…」与「↑↓」之间）。
        /// <para>
        /// 只有**含数据项**的组才弹确认：空组删了没有损失，每次都问只是噪音；
        /// 有数据项时删掉就连子表一起没了，值得拦一下。
        /// （全局变量行、监控行的行尾 × 都是直接删 —— 那两处一行只丢一条，
        /// 这里丢的可能是一整张子表，故差别对待。）
        /// </para>
        /// </summary>
        private void RemoveAcquisitionTask_Click(object sender, RoutedEventArgs e)
        {
            if (TaskOfRow(sender) is not AcquisitionTaskItem task)
            {
                return;
            }

            if (task.Items.Count > 0
                && !ThemeMessageDialog.Confirm(
                    this,
                    "删除任务组",
                    $"确定删除任务组「{task.Name}」及其 {task.Items.Count} 个数据项？",
                    danger: true,
                    confirmText: "删除"))
            {
                return;
            }

            // Remove 内部已重排序号并落盘
            _acqTaskSet.Remove(task);

            // 删掉的组里若有过选中项，选中对象已随组消失 —— 清掉，
            // 免得 ↑↓ 作用在一个不在任何组里的对象上。
            // 判据用「还在不在任何组里」，而不是「在不在刚删的这组里」：
            // 删除一个组不影响别的组的选中项，那种情况必须保留。
            SyncAcquisitionItemSelection();

            // ⚠️ RefreshAcquisitionView 内部已经会刷一次 ↑↓（见其注释），
            // 这里再调一次是幂等的，代价只有一次遍历已展开的行容器，保留以便阅读。
            RefreshAcquisitionView();
        }

        private void AddAcquisitionItem_Click(object sender, RoutedEventArgs e)
        {
            if (TaskOfRow(sender) is not AcquisitionTaskItem task)
            {
                return;
            }

            task.AddItem();
            _acqTaskSet.Save();
            RefreshAcquisitionGlobals();

            // 新增会让「原末项」不再是末项，↓ 的可用性要跟着变
            SyncAcquisitionItemSelection();
            AcquisitionTaskItemsControl?.UpdateLayout();
        }

        private void ClearAcquisitionItems_Click(object sender, RoutedEventArgs e)
        {
            if (TaskOfRow(sender) is not AcquisitionTaskItem task || task.Items.Count == 0)
            {
                return;
            }

            // 破坏性操作一律先问一句（同「删除全部任务组」「删除全部变量」）
            if (!ThemeMessageDialog.Confirm(
                    this,
                    "清空数据项",
                    $"确定清空任务组「{task.Name}」的 {task.Items.Count} 个数据项？",
                    danger: true,
                    confirmText: "清空"))
            {
                return;
            }

            task.ClearItems();
            _acqTaskSet.Save();
            RefreshAcquisitionGlobals();

            // 选中的那一项多半已被清掉 → 必须重算，否则箭头亮着却点不动
            SyncAcquisitionItemSelection();
        }

        private void RemoveAcquisitionItem_Click(object sender, RoutedEventArgs e)
        {
            // 按钮的 DataContext 是**数据项**，而增删入口在任务组对象上 —— 故要往上找一层
            if (sender is not FrameworkElement fe || fe.DataContext is not AcquisitionItemRow row)
            {
                return;
            }

            if (TaskOfRow(fe) is not AcquisitionTaskItem task)
            {
                return;
            }

            task.RemoveItem(row);
            _acqTaskSet.Save();
            RefreshAcquisitionGlobals();

            // ⚠️ 删掉的若正是选中项，`_selectedAcquisitionItem` 就指向一个不在任何组里的对象：
            // 箭头会停留在删除前的可用状态（亮着），点下去却因找不到所属组直接 return。
            // 这条路径**不走 RefreshAcquisitionView**，所以必须单独同步（实测踩过）。
            SyncAcquisitionItemSelection();
        }

        /// <summary>
        /// 增删数据项之后同步「选中态 + ↑↓ 可用性」。
        /// <para>
        /// 两件事：① 选中项已不在任何组里时清掉引用（否则它一直被窗口字段持有，
        /// 且 ↑↓ 会作用在一个已经不可见、也不可移动的对象上）；
        /// ② 重算箭头可用性 —— 置灰条件全部由 <see cref="UpdateAcquisitionItemMoveButtons"/> 判。
        /// </para>
        /// <para>
        /// ⚠️ 增删数据项的三个处理器都在数据管理页，**不经过**
        /// <see cref="RefreshAcquisitionView"/>（那是增删**任务组**才走的），
        /// 所以不能指望它顺手刷新。
        /// </para>
        /// </summary>
        private void SyncAcquisitionItemSelection()
        {
            if (_selectedAcquisitionItem != null && OwnerOfSelectedItem(out _, out _) == null)
            {
                _selectedAcquisitionItem = null;
            }

            UpdateAcquisitionItemMoveButtons();
        }

        private void ToggleAcquisitionTaskExpand_Click(object sender, RoutedEventArgs e)
        {
            if (TaskOfRow(sender) is not AcquisitionTaskItem task)
            {
                return;
            }

            task.IsExpanded = !task.IsExpanded;

            // 展开后才生成子表表头（含 ↑↓），此刻必须按当前选中项重算一次，
            // 否则箭头会停在 XAML 的默认「灰」（2026-11-11）。
            // 用 Loaded 优先级排到布局之后 —— 展开当下容器还没建好，立刻找必然找不到。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateAcquisitionItemMoveButtons));
        }

        private void AcquisitionTaskEnable_Click(object sender, RoutedEventArgs e)
        {
            // IsChecked 双向绑定到 EffectiveEnabled（它的 setter 把用户意图写回 Model.Enabled），
            // 这里只负责落盘。配置错误时开关 IsEnabled=false，Click 根本不会触发，
            // 所以落盘的永远是用户的真实意图，不会被「配置错误自动关」污染。
            _acqTaskSet.Save();

            // 启停会改变「要不要采这个组引用的变量」，必须重算
            RefreshAcquisitionGlobals();

            // 启停也决定这个组能不能出现在总览页的下拉框里（只列 EffectiveEnabled 的组），
            // 所以下拉框要跟着重填。以前漏了：停用一组后，下拉框里它还在，
            // 切过去只显示「没有可用的任务组」而看不出原因。
            RefreshAcqGroupCombo();
        }

        private void OpenAcquisitionTaskDialog_Click(object sender, RoutedEventArgs e)
        {
            if (TaskOfRow(sender) is not AcquisitionTaskItem task)
            {
                return;
            }

            Ui.AcquisitionTaskDialog? dialog = Ui.AcquisitionTaskDialog.Ask(
                this,
                task.Model,
                name => IsTaskNameTaken(task, name),
                _globalVarSet.FindByName,
                () => CompletionSource.ForExpression(GlobalVarModels),
                () => CompletionSource.ForMonitorName(
                    GlobalVarModels, CompletionSource.CompletionFilter.IntegerScalarOrElement));

            if (dialog == null)
            {
                return;
            }

            // 改名走集合的唯一入口（含重名校验），其余各项走包装的方法（含刷新）
            string oldName = task.Name;
            _acqTaskSet.Rename(task, dialog.TaskName, out _);
            task.ApplyDialogResult(
                dialog.KindValue, dialog.TriggerValue, dialog.PeriodSecondsValue, dialog.ExpressionText,
                dialog.LogEnabledValue, dialog.RecordEnabledValue, dialog.SavePathValue,
                dialog.LiveImagePathValue, dialog.CompletionVariableValue);

            // 改名不搬家：文件夹按名称建，改完新数据进新文件夹，旧的原样留着。
            // 不提示一句的话，用户会以为历史数据也跟着过去了
            if (!string.Equals(oldName, task.Name, StringComparison.Ordinal)
                && task.Model.RecordEnabled)
            {
                ThemeMessageDialog.Info(
                    this,
                    "改名提示",
                    $"任务组已改名为「{task.Name}」。\n\n"
                    + $"新数据将写入新文件夹：{task.Name}\\n"
                    + $"原文件夹「{oldName}」保持不动，历史数据仍在里面。");
            }

            _acqTaskSet.Save();

            // 类型 / 启用 / 表达式 / 数据项引用都可能变，常驻清单与运行态都要重算
            RefreshAcquisitionGlobals();
            _acqRuntime.Clear();

            // 总览页「数据采集」的任务组下拉框要跟着刷新（2026-11-03 用户反馈修）。
            // 以前漏了这一步：改完名称必须切一次「当前视图」才刷新，因为下拉框只在
            // RefreshAcqGroupCombo() 里重填，而它只被切视图 / 切页签时调用。
            // 改的不只是名称：启用状态、配置错误也会决定这组能否出现在下拉框里。
            //
            // ⚠️ 必须先同步 _acqHistoryGroup：RefreshAcqGroupCombo 靠它「尽量停在原来那一组」，
            // 而它还记着**旧名字**；不同步的话会匹配失败、退回第 0 组 ——
            // 多组时表现为「改个名，下拉框自己跳到第一组去了」。
            if (!string.Equals(oldName, task.Name, StringComparison.Ordinal)
                && string.Equals(_acqHistoryGroup, oldName, StringComparison.Ordinal))
            {
                _acqHistoryGroup = task.Name;
            }

            RefreshAcqGroupCombo();
        }

        // ---------- 采集执行（P3.1） ----------

        /// <summary>
        /// 采集执行的一拍：逐个启用的任务组判定该不该采，该采的写一行 CSV。
        /// <para>
        /// ⚠️ 这里只做**判定与记录**，不负责取值 —— 变量的值由采集引擎每拍
        /// 常驻读进 <see cref="GlobalVariable.Value"/>（见 <see cref="RefreshAcquisitionGlobals"/>），
        /// 本方法直接拿来用。这样切换页面不会影响记录，也不会为了记录多发一遍报文。
        /// </para>
        /// </summary>
        private void RunAcquisitionTick()
        {
            if (_windowClosed || _acqTaskSet.Items.Count == 0)
            {
                return;
            }

            // ⚠️ 未连接时**不记录**。断开期间 GlobalVariable.Value 停在最后一次读到的值，
            // 照记的话 CSV 会连续出现同一个数（甚至初始值 0），事后看与「设备真的稳定输出 0」
            // 完全无法区分 —— 这比缺一段记录更糟，因为它是**假的**。
            // 断线本身另有日志与底栏提示，用户知道没在采。
            if (!_session.IsConnected)
            {
                return;
            }

            DateTime now = DateTime.Now;

            // 触发式表达式可能引用系统变量（如 SysRun），而 _machineVariables 里的系统快照
            // 平时只由状态机定时器刷新 —— 那个定时器在别的页面是停的。不在这里补一次，
            // 切走页面后表达式就会拿**过期的值**求值，触发沿永远等不到（或乱触发）。
            // 只在确实有触发式任务时才付这份开销。
            //
            // ⚠️ 用带 **break 的早退扫描**，而不是原来的 `Items.Any(...)`：
            // Any 会把整表走完；这里一旦命中第一个触发式任务就停，绝大多数情况只走一两项。
            // 判据用 EffectiveEnabled（= 启用 且 无配置错误）而不是 Model.Enabled ——
            // 配置错误的组下面根本不会执行，为它算系统变量纯属浪费，也与下面的循环同口径。
            bool needsSystemValues = false;
            foreach (AcquisitionTaskItem probe in _acqTaskSet.Items)
            {
                if (probe.EffectiveEnabled && probe.Model.Trigger == AcquisitionTriggerMode.Edge)
                {
                    needsSystemValues = true;
                    break;
                }
            }

            if (needsSystemValues)
            {
                _machineVariables.UpdateSystemValues(
                    SystemVariableValues.Capture(now, _runClock.Elapsed));
            }

            foreach (AcquisitionTaskItem item in _acqTaskSet.Items)
            {
                AcquisitionTask task = item.Model;

                // 一个门：停用与「配置错误」都算不启用（用户定案 2026-10-27）。
                // 位置刻意在 GetOrCreateRuntime **之前**：调度不推进，等配置修好后仍从
                // 「现在 + 一个周期」开始，不会立刻补写一拍本该早就采过的数据。
                if (!item.EffectiveEnabled)
                {
                    continue;
                }

                AcquisitionTaskState state = GetOrCreateRuntime(task);

                // 写入任务：上一笔还没回来就不排队（节拍 50ms，一次 Modbus 往返可能更久）。
                // 位置在 ShouldFire **之前** —— 挡掉这一拍不能顺手把调度推后，
                // 否则一次慢写入会把整个周期往后挤。
                if (task.Kind == AcquisitionTaskKind.Write && state.WriteInFlight)
                {
                    continue;
                }

                bool conditionTrue = EvaluateTrigger(task, state);

                if (!AcquisitionTrigger.ShouldFire(task, state, now, conditionTrue))
                {
                    continue;
                }

                // 写入任务的记录开关只决定**记不记文件**，下发本身照跑，
                // 所以这个分支必须在下面的 RecordEnabled 判断之前。
                if (task.Kind == AcquisitionTaskKind.Write)
                {
                    BeginWriteTask(item, task, state, now);
                    continue;
                }

                // 没开记录 = 只跑不记。仍要推进 state（上面已经推进过），
                // 否则一旦打开记录会立刻补采一拍，那一拍的时间戳是打开那一刻、不是本该采的时刻。
                // 但日志照样记一条 —— 「任务在跑、只是不落文件」与「任务没跑」是两回事，
                // 日志里必须分得开（用户定案：写成「执行完成（未启用数据记录）」）
                if (!task.RecordEnabled)
                {
                    LogTaskActivity(
                        task,
                        AcquisitionTaskRules.DescribeLogSuccess(task, item.Index),
                        DeviceLogLevel.Neutral);

                    // 没开记录也算一次执行完成 —— 执行完成输出变量照写（口径 B）
                    QueueCompletionWrite(task, AcquisitionTaskRules.ResultSuccess);
                    continue;
                }

                string error = AcquisitionCsv.Append(task, state, now, _globalVarSet.FindByName);

                // 每次执行都记一条（用户定案 C）：成功不带原因、失败必带。
                // 写盘失败不弹窗（50ms 一拍会弹到用户崩溃），只走日志
                LogTaskActivity(
                    task,
                    error.Length == 0
                        ? AcquisitionTaskRules.DescribeLogSuccess(task, item.Index)
                        : AcquisitionTaskRules.DescribeLogFailure(task, item.Index, error),
                    error.Length == 0 ? DeviceLogLevel.Neutral : DeviceLogLevel.Warning);

                // 执行完成输出变量（2026-11-10）：判据与日志同源 ——
                // CSV 写盘失败算「异常执行完成」，其余算「正常执行完成」。
                QueueCompletionWrite(
                    task,
                    error.Length == 0
                        ? AcquisitionTaskRules.ResultSuccess
                        : AcquisitionTaskRules.ResultFailure);
            }

            // 实时卡片跟着采集节拍刷新数值（P1.4）。
            // ⚠️ 放在循环外：这次读的是**共享的** GlobalVariable 值，与本拍哪个组该采无关，
            // 所以不必按组刷；而且只更新文本，不重建控件（重建会打断正在拖的卡片）
            if (IsAcqLiveVisible)
            {
                RefreshAcqLiveValues();
            }
        }

        /// <summary>
        /// 起一次写入任务的下发（fire-and-forget）。
        /// <para>
        /// ⚠️ **不能在这里 await**：本方法是 50ms 定时器的回调，一 await 就把整个采集节拍
        /// 卡在一次 Modbus 往返上，别的任务组跟着停。改用「起一个后台流程 + 用
        /// <see cref="AcquisitionTaskState.WriteInFlight"/> 挡住重入」。
        /// </para>
        /// </summary>
        private void BeginWriteTask(
            AcquisitionTaskItem item, AcquisitionTask task, AcquisitionTaskState state, DateTime now)
        {
            state.WriteInFlight = true;
            _ = RunWriteTaskAsync(item, task, state, now);
        }

        /// <summary>
        /// 执行一次写入任务：把每个数据项的**来源变量值**编码后写到**写入变量**的地址上。
        /// <para>
        /// 口径（用户定案 2026-10-27）：
        /// 一项失败**不短路**，其余项照写 —— Modbus 本来就没有事务，停下来也换不来原子性，
        /// 只会让「5 项里坏了 1 项」变成「5 项一项都没写」。
        /// 没有映射地址的项**静默跳过**（用户定案：不算配置错误）。
        /// </para>
        /// <para>
        /// 未连接时**什么都不做**：重连后也不补发（陈年的一笔写下去比不写更危险）。
        /// </para>
        /// </summary>
        private async Task RunWriteTaskAsync(
            AcquisitionTaskItem item, AcquisitionTask task, AcquisitionTaskState state, DateTime now)
        {
            // 异常兜底只写一次结果：正常分支已经写过、或已经排进队列之后又抛异常时，
            // 不能再补一个 2 把 1 盖掉。
            bool completionQueued = false;

            try
            {
                IModbusClient? modbus = _session.Client;
                bool plcConnected = modbus != null && _session.IsConnected;

                // ⚠️ **未连接不能整轮 return**（2026-11-05 修订）：写入变量可能全是
                // 「没有映射地址」的内存变量，那种项根本不发报文，PLC 连没连都不影响它。
                // 整轮 return 会让「只写内存」的任务组在 PLC 掉线时悄悄停摆。
                // 真正需要发报文的项在下面按项判断（未连接就跳过那一项）。
                int total = task.Items.Count;
                if (total == 0)
                {
                    return;
                }

                // 来源变量可能是系统变量（如 sys_True / sys_DateTime_Second），
                // 它的值要现算。只在真的遇到系统变量时才付这份开销。
                IReadOnlyDictionary<string, string>? systemValues = null;

                List<string> failures = new List<string>();

                for (int i = 0; i < total; i++)
                {
                    AcquisitionItem row = task.Items[i];
                    int index = i + 1;

                    AcquisitionSource? source =
                        AcquisitionSource.Resolve(row.SourceVariableName, _globalVarSet.FindByName);
                    if (source == null)
                    {
                        // 正常情况下走不到：配置错误已经被开关挡在门外了
                        failures.Add(AcquisitionWrite.DescribeItemFailure(
                            index, row.Name,
                            AcquisitionSource.DescribeResolveError(
                                row.SourceVariableName, _globalVarSet.FindByName)));
                        continue;
                    }

                    if (source.IsSystemVariable && systemValues == null)
                    {
                        systemValues = SystemVariableValues.Capture(now, _runClock.Elapsed);
                    }

                    string? sourceText = AcquisitionSource.ValueOf(
                        row.SourceVariableName,
                        _globalVarSet.FindByName,
                        systemValues);

                    if (sourceText == null)
                    {
                        failures.Add(AcquisitionWrite.DescribeItemFailure(
                            index, row.Name, "取不到来源变量的值"));
                        continue;
                    }

                    // 写入变量支持数组元素（var1[0]）：解析成基础变量 + 元素下标，
                    // 地址由 AcquisitionWrite.TryBuild 按偏移算。
                    if (!VariableReference.TryResolve(
                            row.VariableName, _globalVarSet.FindByName, allowWholeArray: false,
                            out GlobalVariable? target, out int? targetIndex, out string targetError))
                    {
                        failures.Add(AcquisitionWrite.DescribeItemFailure(index, row.Name, targetError));
                        continue;
                    }

                    // ⚠️ **常量的格式必须自己算**，不能沿用来源的 Format：
                    // 变量的 Format 是「给人看的显示格式」（可能是 Hex / Bin），
                    // 而常量是用户直书的字面量 —— `#123` 就是十进制 123，
                    // 若拿一个 INT/Hex 变量的格式去解析，会当成 0x123 = 291，**静默写错值**。
                    // 进制由字面量自己的 0x / 0b 前缀标定，见 AcquisitionConstant.FormatFor。
                    DataType targetType = targetIndex is int
                        ? target!.ElementType
                        : target!.Type;

                    DisplayFormat sourceFormat = source.IsConstant
                        ? AcquisitionConstant.FormatFor(source.Literal ?? string.Empty, targetType)
                        : source.Format;

                    AcquisitionWriteStatus status = AcquisitionWrite.TryBuild(
                        target,
                        sourceText,
                        sourceFormat,
                        _byteOrders,
                        targetIndex,
                        out WritePlan plan,
                        out string buildError);

                    if (status == AcquisitionWriteStatus.CannotAddress)
                    {
                        // 写入变量**没有映射地址**：只更新内存值，不发报文（2026-11-05 修订）。
                        // ⚠️ 这里**不能 continue 掉**：早期是「静默跳过、连编码都不做」，
                        // 结果是「写入变量没地址 → 变量值永远不变」，日志却写着「执行完成」。
                        // 现在按用户的定案：无地址也要能写入，只是写进内存。
                        AcquisitionWrite.ApplyToMemory(target!, plan, _byteOrders, targetIndex);
                        continue;
                    }

                    if (status == AcquisitionWriteStatus.Failed)
                    {
                        failures.Add(AcquisitionWrite.DescribeItemFailure(index, row.Name, buildError));
                        continue;
                    }

                    // 到这里的都是**有映射地址**、真要发报文的项：PLC 没连就跳过这一项。
                    // 不记失败 —— 掉线是整条链路的事，逐项刷「写入失败」只会淹掉真正的原因。
                    if (!plcConnected)
                    {
                        continue;
                    }

                    try
                    {
                        await WriteToDeviceAsync(modbus!, plan).ConfigureAwait(true);

                        // 报文发出去了，内存值也要同步 —— 否则「写成功了但界面上还是旧值」，
                        // 要等下一轮采集/读回才更新。与扫码器写变量同一个口径。
                        AcquisitionWrite.ApplyToMemory(target!, plan, _byteOrders, targetIndex);
                    }
                    catch (OperationCanceledException)
                    {
                        failures.Add(AcquisitionWrite.DescribeItemFailure(index, row.Name, "写入已取消"));
                        if (!_session.IsConnected)
                        {
                            break; // 已经断了，后面的项没必要再试
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(AcquisitionWrite.DescribeItemFailure(index, row.Name, ClassifyWriteError(ex)));
                        if (!_session.IsConnected)
                        {
                            break;
                        }
                    }
                }

                string reason = AcquisitionWrite.DescribeFailure(failures, total);

                // 记录口径不变：记的是**写入变量的回读值**（来源变量不记，用户定案）。
                // 写失败也照记 —— 文件反映的是 PLC 上的真实状态，读回值本身就是真的。
                if (task.RecordEnabled)
                {
                    string csvError = AcquisitionCsv.Append(task, state, now, _globalVarSet.FindByName);
                    if (csvError.Length > 0)
                    {
                        reason = reason.Length == 0 ? csvError : reason + "；" + csvError;
                    }
                }

                LogTaskActivity(
                    task,
                    reason.Length == 0
                        ? AcquisitionTaskRules.DescribeLogSuccess(task, item.Index)
                        : AcquisitionTaskRules.DescribeLogFailure(task, item.Index, reason),
                    reason.Length == 0 ? DeviceLogLevel.Neutral : DeviceLogLevel.Warning);

                // 执行完成输出变量（2026-11-10）：本轮写入有任一项失败、或 CSV 写盘失败，
                // 都算「异常执行完成」。判据与上面这条日志**同源**（都看 reason 是否为空）。
                QueueCompletionWrite(
                    task,
                    reason.Length == 0
                        ? AcquisitionTaskRules.ResultSuccess
                        : AcquisitionTaskRules.ResultFailure);
                completionQueued = true;
            }
            catch (Exception ex)
            {
                // fire-and-forget 里漏出来的异常没人接，必须自己收掉，否则进程会静默炸掉
                LogTaskActivity(
                    task,
                    AcquisitionTaskRules.DescribeLogFailure(task, item.Index, ex.Message),
                    DeviceLogLevel.Warning);

                if (!completionQueued)
                {
                    QueueCompletionWrite(task, AcquisitionTaskRules.ResultFailure);
                }
            }
            finally
            {
                state.WriteInFlight = false;
            }
        }

        /// <summary>
        /// 写「执行完成输出变量」（2026-11-10，口径照扫码器的「执行结果输出变量」）。
        /// <para>
        /// 三件事按顺序做：
        /// ① 先无条件回填**内存值**（无映射地址、PLC 未连接时界面照样能看到最新结果）；
        /// ② 有映射地址且 PLC 已连接时，再经 FC06 / FC16 回写设备；
        /// ③ 下发用「只保留最新一个」的合并队列（<see cref="AcquisitionTaskState.PendingCompletionValue"/>）——
        /// 周期可能只有 0.1s，而 Modbus 往返更慢，排一队旧值只会越积越多。
        /// </para>
        /// <para>
        /// ⚠️ **不能在这里 await**：读取任务的调用点是 50ms 定时器回调，
        /// 写入任务的调用点也刚跑完一轮、不该为结果变量再拖住一个周期。
        /// 故起后台流程，用 <see cref="AcquisitionTaskState.CompletionWriteInFlight"/> 挡重入。
        /// </para>
        /// <para>
        /// 未配置（空串）直接 return：这是合法常态，不是错误。
        /// </para>
        /// </summary>
        private void QueueCompletionWrite(AcquisitionTask task, string value)
        {
            if (task == null)
            {
                return;
            }

            // 入队也在 50ms 采集节拍里跑：变量被手改成非法格式 / 非法数组声明时，
            // TryBuild / ApplyToMemory 可能抛异常。这里必须自己收掉，
            // 否则异常会从 DispatcherTimer 回调逃出去、打断整拍采集（2026-11-11）。
            try
            {
                QueueCompletionWriteCore(task, value);
            }
            catch (Exception ex)
            {
                LogTaskActivity(
                    task,
                    "执行完成输出变量：写入失败：" + ex.Message,
                    DeviceLogLevel.Warning);
            }
        }

        private void QueueCompletionWriteCore(AcquisitionTask task, string value)
        {
            string name = (task.CompletionVariableName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return;
            }

            // 配置检查已经把「查不到 / 类型不对」判成配置错误、整组拦下了；
            // 这里仍要兜一次（例如刚删掉变量、配置检查还没来得及刷新的那一拍）。
            // 数组元素（var1[0]）按元素下标写，整条数组会被 VariableReference 拒掉。
            if (!VariableReference.TryResolve(
                    name, _globalVarSet.FindByName, allowWholeArray: false,
                    out GlobalVariable? variable, out int? elementIndex, out _)
                || variable == null)
            {
                return;
            }

            AcquisitionWriteStatus status = AcquisitionWrite.TryBuild(
                variable, value, DisplayFormat.Dec, _byteOrders, elementIndex,
                out WritePlan plan, out string error);

            if (status == AcquisitionWriteStatus.Failed)
            {
                LogTaskActivity(
                    task,
                    "执行完成输出变量：写入「" + name + "」失败：" + error,
                    DeviceLogLevel.Warning);
                return;
            }

            // 内存先落：与扫码器写变量同一口径（PLC 未连时结果只进内存）
            AcquisitionWrite.ApplyToMemory(variable, plan, _byteOrders, elementIndex);

            if (status == AcquisitionWriteStatus.CannotAddress)
            {
                // 没有寄存器可发：内存已经更新完，到此为止
                return;
            }

            IModbusClient? modbus = _session.Client;
            if (modbus == null || !_session.IsConnected)
            {
                return;
            }

            if (!_acqRuntime.TryGetValue(task, out AcquisitionTaskState? state))
            {
                // 理论上不会发生：调用点都在 GetOrCreateRuntime 之后
                return;
            }

            state.PendingCompletionValue = value;

            if (state.CompletionWriteInFlight)
            {
                // 在途：让当前这一轮收尾时把最新值写出去，不另起并发流程
                return;
            }

            _ = DrainCompletionWritesAsync(task, state);
        }

        /// <summary>
        /// 把「执行完成输出变量」攒下的最新值逐个下发（每次只取当前最新一个）。
        /// <para>
        /// fire-and-forget，异常必须自己收掉 —— 与 <see cref="RunWriteTaskAsync"/> 同一纪律。
        /// 断开连接时**丢掉待写值**：重连后补写一个陈年结果比不写更危险，也与采集任务
        /// 「断线不补采」的口径一致。
        /// </para>
        /// </summary>
        private async Task DrainCompletionWritesAsync(AcquisitionTask task, AcquisitionTaskState state)
        {
            state.CompletionWriteInFlight = true;

            try
            {
                while (state.PendingCompletionValue.Length > 0)
                {
                    string value = state.PendingCompletionValue;
                    state.PendingCompletionValue = string.Empty;

                    string name = (task.CompletionVariableName ?? string.Empty).Trim();

                    if (!VariableReference.TryResolve(
                            name, _globalVarSet.FindByName, allowWholeArray: false,
                            out GlobalVariable? variable, out int? elementIndex, out _)
                        || variable == null)
                    {
                        // 变量被删 / 改名（改名会同步改引用，正常走不到这里）
                        continue;
                    }

                    AcquisitionWriteStatus status = AcquisitionWrite.TryBuild(
                        variable, value, DisplayFormat.Dec, _byteOrders, elementIndex,
                        out WritePlan plan, out string buildError);

                    if (status == AcquisitionWriteStatus.Failed)
                    {
                        LogTaskActivity(
                            task,
                            "执行完成输出变量：写入「" + name + "」失败：" + buildError,
                            DeviceLogLevel.Warning);
                        continue;
                    }

                    if (status == AcquisitionWriteStatus.CannotAddress)
                    {
                        // 无地址：内存值在 QueueCompletionWrite 里已经写过了
                        continue;
                    }

                    IModbusClient? modbus = _session.Client;
                    if (modbus == null || !_session.IsConnected)
                    {
                        state.PendingCompletionValue = string.Empty;
                        break;
                    }

                    try
                    {
                        await WriteToDeviceAsync(modbus, plan).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // 关窗 / 断开途中的取消，不打扰用户
                    }
                    catch (Exception ex)
                    {
                        LogTaskActivity(
                            task,
                            "执行完成输出变量：写入「" + name + "」失败：" + ClassifyWriteError(ex),
                            DeviceLogLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                LogTaskActivity(
                    task,
                    "执行完成输出变量：写入失败：" + ex.Message,
                    DeviceLogLevel.Warning);
            }
            finally
            {
                state.CompletionWriteInFlight = false;
            }
        }

        /// <summary>
        /// 任务组的活动日志入口：**组级「日志记录」关掉就整组不记**。
        /// <para>
        /// 不用在调用点逐个判 <c>task.LogEnabled</c>，是因为日志有 4 个出口
        /// （只跑不记 / 记录成功 / 记录失败 / 写入异常兜底），逐个判漏一个
        /// 就会出现「关掉了却还在刷」的诡异现象。
        /// </para>
        /// <para>
        /// ⚠️ 只管**任务组自身活动**。断线 / 连接失败这类系统级日志不归它管 ——
        /// 那是设备状态，不是「这个任务组跑没跑」。
        /// </para>
        /// </summary>
        private void LogTaskActivity(
            AcquisitionTask task, string message, DeviceLogLevel level)
        {
            if (task == null || !task.LogEnabled)
            {
                return;
            }

            AppendDeviceLog(message, level);
        }

        /// <summary>取该组的运行态；没有就建一个。</summary>
        private AcquisitionTaskState GetOrCreateRuntime(AcquisitionTask task)
        {
            if (!_acqRuntime.TryGetValue(task, out AcquisitionTaskState? state))
            {
                state = new AcquisitionTaskState();

                // 首拍**延后一个周期**：本定时器与采集引擎是两个独立的 DispatcherTimer，
                // 启动瞬间的先后没有保证。若首拍立刻记，多半会赶在第一次轮询之前，
                // 把变量的**初始值**（0 / 空串）写进文件第一行 —— 实测确实如此。
                // 延后一拍，第一行就是真值了。
                double seconds = task.PeriodSeconds;
                if (double.IsNaN(seconds) || seconds < AcquisitionTaskRules.MinPeriodSeconds)
                {
                    seconds = AcquisitionTaskRules.DefaultPeriodSeconds;
                }

                state.NextFireLocal = DateTime.Now.AddSeconds(seconds);
                _acqRuntime[task] = state;
            }

            return state;
        }

        /// <summary>
        /// 求触发表达式的本拍取值。
        /// <para>
        /// 周期式不需要表达式，直接返回 <c>false</c>（那个值只有触发式才看）。
        /// </para>
        /// <para>
        /// 表达式**编译结果缓存**在运行态里：50ms 一拍，每拍重新词法+语法分析纯属浪费。
        /// 原文变了才重编 —— 用户在对话框里改了表达式，下一拍就生效。
        /// </para>
        /// </summary>
        private bool EvaluateTrigger(AcquisitionTask task, AcquisitionTaskState state)
        {
            if (task.Trigger != AcquisitionTriggerMode.Edge)
            {
                return false;
            }

            string source = task.Expression ?? string.Empty;

            if (!string.Equals(state.CompiledSource, source, StringComparison.Ordinal)
                || state.Compiled == null)
            {
                state.CompiledSource = source;
                state.Compiled = CompiledExpression.TryParse(source, out CompiledExpression? compiled, out _)
                    ? compiled ?? CompiledExpression.Empty
                    : CompiledExpression.Empty;
            }

            if (state.Compiled.IsEmpty)
            {
                // 空表达式恒为假 —— 与状态机同一口径，等于「永不触发」
                return false;
            }

            return state.Compiled.TryEvaluate(_machineVariables, out bool value, out _) && value;
        }

        /// <summary>任务名是否已被人占用（把「自己」排除掉，否则改别的字段也会被自己挡下来）。</summary>
        private bool IsTaskNameTaken(AcquisitionTaskItem self, string name) =>
            _acqTaskSet.Items.Any(x => !ReferenceEquals(x, self)
                                       && string.Equals(x.Name, name, StringComparison.Ordinal));

        /// <summary>
        /// 数据项「变量名称」框加载：挂变量名补全（P6.5）。
        /// <para>
        /// 需求写的是「能填变量名的地方」都要有 —— 这里就是新增的第三处。
        /// 去重仍用 <see cref="_nameCompletions"/> 那张弱表：模板复用会让同一个框多次 Loaded，
        /// 重复挂载会让一次按键被处理两遍。
        /// </para>
        /// </summary>
        private void AcquisitionItemVariableBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || _nameCompletions.TryGetValue(tb, out _))
            {
                return;
            }

            // ⚠️ 这里用默认档 **AnyGlobal**（数组给元素写法 `var1[0]`，2026-11-11 显式写出来）：
            // 采集的读取 / 写入变量引用的是**元素**（用户定案 2026-11-10：整条数组一律被拒）。
            // 与自由监控「元件或变量名称」相反 —— 那一栏要整条名字，用的是 WholeArrayGlobal。
            var popup = Ui.CompletionPopup.Attach(
                tb,
                () => CompletionSource.ForMonitorName(
                    GlobalVarModels, CompletionSource.CompletionFilter.AnyGlobal));
            _nameCompletions.Add(tb, popup);
        }

        /// <summary>
        /// 数据项「来源变量」框加载：挂**系统变量 + 全局变量**补全。
        /// <para>
        /// ⚠️ 与旁边的「写入变量」框**不共用**候选清单：来源变量只是「值从哪来」，
        /// 系统变量完全可用；写入变量要落到 PLC 地址上，系统变量没有地址，
        /// 混进候选等于让人填完才发现写不出去。
        /// </para>
        /// </summary>
        private void AcquisitionSourceBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || _nameCompletions.TryGetValue(tb, out _))
            {
                return;
            }

            var popup = Ui.CompletionPopup.Attach(tb, () => CompletionSource.ForSourceVariable(GlobalVarModels));
            _nameCompletions.Add(tb, popup);
        }

        /// <summary>
        /// 数据项输入框失焦：落盘。
        /// <para>
        /// 项名称与变量名称都是<b>逐字符进模型</b>的（否则右列「数据类型」不会跟着变），
        /// 但**写盘只在失焦** —— 逐字符写文件没有意义。
        /// </para>
        /// </summary>
        private void AcquisitionItemField_LostFocus(object sender, RoutedEventArgs e)
        {
            _acqTaskSet.Save();

            // 变量名改了 → 该组引用的变量集合变了 → 常驻采集清单要跟着变
            RefreshAcquisitionGlobals();

            // 项名称 / 变量名也会影响实时卡片显示的文字（名称，以及名称为空时的回退变量名），
            // 而卡片文字是**建的时候一次性写进去**的，不重建就一直是旧值。
            //
            // ⚠️ 这里**不能**加 `IsAcqLiveVisible` 之类的可见性判断（2026-11-03）：
            // 用户此时正在「数据管理」页编辑，实时视图一定是不可见的 ——
            // 加了判断等于永不生效，于是改完名称要切一次「当前视图」才刷新（用户反馈的正是这个）。
            // 重建的代价很小（卡片只有几张，不碰网络、不读盘），见下方注释。
            RebuildAcqLiveCanvas();
        }

        /// <summary>
        /// 从行内控件往上找所属的<b>任务组</b>。
        /// <para>
        /// 任务组行的模板根 DataContext 就是 <see cref="AcquisitionTaskItem"/>；
        /// 数据项行里按钮的 DataContext 是 <c>AcquisitionItemRow</c>，
        /// 再往上一层（子表 ItemsControl）才拿到任务组 —— 故统一走可视树上溯。
        /// </para>
        /// </summary>
        private static AcquisitionTaskItem? TaskOfRow(object sender)
        {
            if (sender is not DependencyObject node)
            {
                return null;
            }

            while (node != null)
            {
                if (node is FrameworkElement fe && fe.DataContext is AcquisitionTaskItem task)
                {
                    return task;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }

    }
}
