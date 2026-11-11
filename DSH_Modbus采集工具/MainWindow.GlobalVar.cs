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
        /// <summary>
        /// 全部数据类型的规格（线圈区 + 寄存器区合并，顺序同规格表）。**已缓存**。
        /// <para>
        /// <c>DataTypes.AllSpecs</c> 是私有的（对外只暴露 <c>CoilTypes</c> / <c>RegisterTypes</c>），
        /// 而变量表要在一个下拉框里同时给出两类，故在这里拼一次。
        /// </para>
        /// <para>
        /// ⚠️ 拼的结果**只与静态规格表有关**，与任何运行时状态无关，故可安全缓存在
        /// static readonly 里 —— 原实现是每次调用都 new 一个 List 再 ToList 一遍，
        /// 而它被每行的 Loaded 事件调用（行一多就很可观）。
        /// </para>
        /// </summary>
        private static readonly IReadOnlyList<DataTypeSpec> AllTypeSpecsCached = BuildAllTypeSpecs();

        private static IReadOnlyList<DataTypeSpec> BuildAllTypeSpecs()
        {
            var list = new List<DataTypeSpec>(DataTypes.CoilTypes.Count + DataTypes.RegisterTypes.Count);
            list.AddRange(DataTypes.CoilTypes);
            list.AddRange(DataTypes.RegisterTypes);
            return list;
        }

        private static IEnumerable<DataTypeSpec> AllTypeSpecs() => AllTypeSpecsCached;

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
                ThemeMessageDialog.Info(this, "新增变量失败", error);
                return;
            }

            RefreshGlobalVarView();
            GlobalVarItemsControl?.UpdateLayout();
            FocusGlobalVarRow(item);

            // 新增变量也可能让别处原本「变量不存在」的引用恢复 —— 与删除走同一条刷新链
            RefreshAfterVariableChange();
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

            if (!ThemeMessageDialog.Confirm(
                    this,
                    "删除全部变量",
                    $"确定删除全部 {_globalVarSet.Items.Count} 个变量？",
                    danger: true,
                    confirmText: "删除"))
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
        /// 全局变量改名后，把**所有引用它的地方**改成新名字（2026-11-09 用户要求）。
        /// <para>
        /// 五处引用，覆盖六份持久化文件里的四份（另两份按名字存的是变量表本身与系统变量清单）：
        /// 自由监控变量行（<c>data-points.json</c>）、采集的读取 / 写入 / 来源变量与触发表达式
        /// （<c>acquisition-tasks.json</c>）、状态机 9 框 2 灯的表达式（<c>machine-state.json</c>）、
        /// 扫码器的缓冲区 / 结果变量与触发表达式（<c>scanner.json</c>）。
        /// </para>
        /// <para>
        /// ⚠️ **表达式按标识符替换，纯名字字段按全等替换** —— 都不做子串替换，
        /// 否则 <c>speed</c> → <c>rate</c> 会把 <c>speed2</c>、<c>my_speed</c> 一起带走。
        /// 规则本体在 <see cref="VariableRenamer"/>（纯逻辑，可离线断言）。
        /// </para>
        /// <para>
        /// **静默执行、不弹框**（用户 2026-11-09 定案）：改名是「失焦即提交」的高频操作，
        /// 每次都弹一个「同步了 N 处」只会打断连续编辑。
        /// </para>
        /// </summary>
        private void RenameVariableReferencesEverywhere(string oldName, string newName)
        {
            // 自由监控：变量行存的是变量名（元件行不参与，见 MonitorItemSet 里的过滤）
            _monitorSet.RenameVariableReferences(oldName, newName);

            // 采集：三个变量字段 + 触发表达式；改完它自己落盘
            _acqTaskSet.RenameVariableReferences(oldName, newName);

            // 状态机：表达式文本被改写，**必须重编译** —— _machineSet 缓存的是
            // Compiled（按旧名字编译的），光改 Expression 文本不会让缓存跟着变，
            // 不重编译就是「文本看着对、求值仍报变量不存在」。
            _machineSet.RenameVariableReferences(oldName, newName);
            _machineSet.RecompileAll(ExpressionNameError, ExpressionVariableTypeOf);

            // 扫码器：两个变量框 + 触发表达式
            _scannerSet.RenameVariableReferences(oldName, newName);

            // 监控行的名称框是普通 TextBox（不走绑定），已生成的容器要手工同步
            SyncMonitorNameBoxes();
        }

        /// <summary>
        /// 把自由监控各行 <c>NameBox</c> 的文本同步成模型里的名称。
        /// <para>
        /// 名称框是模板里的 <c>TextBox</c>、文本由 <c>MonitorRow_Loaded</c> **一次性**写入
        /// （刻意不绑 <see cref="MonitorItem.Name"/>：那样打字途中就会把半截名字推给模型）。
        /// 变量改名后模型变了、容器还在，故这里遍历一遍已生成的行补上。
        /// </para>
        /// <para>不可见的分段没有容器，它们重新可见时由 <c>MonitorRow_Loaded</c> 填对，不需要这里管。</para>
        /// </summary>
        private void SyncMonitorNameBoxes()
        {
            if (MonitorItemsControl == null)
            {
                return;
            }

            foreach (FrameworkElement row in EnumerateRows(MonitorItemsControl))
            {
                if (row.Tag is not MonitorItem item)
                {
                    continue;
                }

                if (row.FindName("NameBox") is TextBox nameBox
                    && !string.Equals(nameBox.Text, item.Name, StringComparison.Ordinal))
                {
                    nameBox.Text = item.Name;
                }
            }
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

            // ⚠️ 变量**换了类型**（BOOL ↔ INT）时，状态机那条表达式可能从合法变非法：
            // `flag` 本来可以直接当条件，改成 INT 后就必须写成 `flag != 0`，
            // 否则求值恒失败、框永远不亮。故这里连同结果类型一起重编译（2026-11-11）。
            _machineSet.RecompileAll(ExpressionNameError, ExpressionVariableTypeOf);

            // ⚠️ 采集 / 扫码器的常驻清单必须在这里重算（2026-11-11）：
            // 两份清单里存的是 `GlobalVariable` 的**对象引用**。删掉变量后重建同名变量，
            // 清单里仍是旧对象 → 任务引用新对象、轮询却还在读旧对象，值永远不更新。
            RefreshAcquisitionGlobals();

            // 数据采集项的「数据类型」是从变量表反推的，变量改名/换类型后这里会变成旧值
            _acqTaskSet.RefreshVariables();

            // 扫码器的缓冲区/通知变量也是按名字解析的：变量改名、删掉、换类型后要重算
            // 「不存在」「类型不对」「已在别处被引用」三种提示（同样放在早退之前）
            RefreshScannerReferenceHints();

            // ⚠️ 上面那个只重算「变量已被引用」的黄字，**不重算配置错误**。
            // 变量被删 / 换类型后，行上的「配置错误」与红框必须一起刷新，
            // 否则用户改完变量表要等下次编辑某个框才看见（2026-11-11 补）。
            RefreshAllScannerItemStates();

            // ⚠️ **顺序**：必须先刷配置错误、再算常驻清单 ——
            // RefreshScannerGlobals 按 `EffectiveEnabled` 过滤，用旧的 Problem 会多采 / 少采。
            RefreshScannerGlobals();

            if (_monitorSet.Items.Count == 0)
            {
                return;
            }

            _monitorSet.ReResolveAll();

            // ⚠️ **数据层**先重建数组子行：这一步不依赖可视树，故监控页不可见时也有效。
            // 用户是在全局变量页改完定义、再切回监控表看的 —— 那时行容器还没生成，
            // 只靠下面那个视觉循环会一条都碰不到（= 用户报的「要重启才出现」）。
            _monitorSet.RebuildAllElementRows();

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

                    // 视觉侧只需保证样式/候选对；子行集合已在上面统一重建过
                    if (item.IsArray)
                    {
                        // 数组行：类型框与格式框都置灰（变量行也一样，见下）
                        ApplyArrayRowState(row, item);
                    }
                    else if (item.IsVariable)
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
            string previousName = item.Name;

            if (_globalVarSet.Rename(item, wanted, out string error))
            {
                tb.Text = item.Name;
                _globalVarSet.Save();

                // 改名成功：所有引用旧名字的地方一起改名（2026-11-09 用户要求）。
                // ⚠️ 必须在 RefreshAfterVariableChange **之前**做 —— 那一步按新名字
                // 重解析监控行 / 重编译表达式，引用还停在旧名字的话，重算结果就是
                // 「变量不存在」，整组被判成配置错误。
                if (!string.Equals(previousName, item.Name, StringComparison.Ordinal))
                {
                    RenameVariableReferencesEverywhere(previousName, item.Name);
                }

                // 改名会让引用旧名字的监控行失效，必须重解析一遍
                RefreshAfterVariableChange();
                return;
            }

            // 失败：先回退文本，再把规则说清楚。
            // 正文最前面是**用户刚输入的内容**（外加双引号，需求），一眼看出被拒的是哪一段；
            // 标题点出性质，正文后接具体规则。输入为空时不加引号。
            tb.Text = item.Name;
            string input = wanted.Length == 0 ? string.Empty : $"“{wanted}”";
            ThemeMessageDialog.Info(this, "变量名称不合法", $"{input}{error}");
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

            // 先记下改之前的地址：解析失败或撞车时都要回退到它
            string previousAddress = item.Address;

            string wanted = tb.Text.Trim();
            item.Address = wanted;
            _globalVarSet.ResolveOnly(item, out AddressErrorKind kind);

            if (kind != AddressErrorKind.None)
            {
                // 失败时保留用户原样输入，方便就地改。
                //
                // ⚠️ **但「类型不符」这一档要回填归一的地址**：TryResolve 在判定类型不符之前
                // 就已经把 `v.Address` 归一成大写了（y27 → Y27，见 GlobalVariable.cs 的
                // `element.ToUpperInvariant(); v.Address = element + index;`），
                // 而 `Save()` 落的是那个归一值。若这里仍显示 y27，就会出现
                // 「界面 y27 / json 里 Y27」的不一致 —— 且下次打开看到的是 Y27，
                // 用户会以为自己没改成功。其它失败档（InvalidElement / Configuration）
                // 不会走到归一那一步，故保持原样。
                if (kind == AddressErrorKind.TypeMismatch)
                {
                    tb.Text = item.Address;
                }

                ShowGlobalVarAddressError(item, wanted, kind);
            }
            else
            {
                // ⚠️ **地址冲突检查必须放在解析成功之后**（用户定案 2026-11-05）：
                // ResolveOnly 会把元件符号归一成大写在写回 item.Address（y27 → Y27），
                // 在它之前比会拿未归一的原文与已归一的旧值比，y27 与 Y27 这样同一元件的
                // 两种写法就漏判了。这里用归一后的 item.Address 去查，比的是规范形式。
                GlobalVariable? conflict = _globalVarSet.FindByAddress(item.Address, item.Model);

                // 再查**区间重叠**（2026-11-09，数组引入后必需）：
                // 全等比较在标量时代够用，但数组占的是一段连续单元 ——
                // D10 声明 ARRAY<INT>[3] 就占了 D10/D11/D12，此时另一条变量映射 D11
                // 会与它互相覆盖，而全等比较看不出任何问题。
                conflict ??= _globalVarSet.FindByAddressOverlap(item.Model);

                if (conflict != null)
                {
                    // 提示格式由用户定稿：「{地址}」已被使用。
                    // 区间冲突时说清是被**哪条变量**占的 —— 只说「D11 已被使用」，
                    // 用户会去变量表里找一条叫 D11 的，而实际占着它的是 D10 起的数组。
                    bool sameStart = string.Equals(
                        conflict.Address.Trim(), item.Address.Trim(), StringComparison.OrdinalIgnoreCase);

                    ThemeMessageDialog.Info(
                        this,
                        "地址重复",
                        sameStart
                            ? $"\"{item.Address}\"已被使用"
                            : $"\"{item.Address}\"与变量「{conflict.Name}」（{conflict.Address}，{conflict.TypeDisplayName}）占用的地址重叠");

                    // 回退到改之前的值（拿不出旧值时清空），避免界面上留着一条非法配置。
                    // ⚠️ 回退后要**重新解析一次**：ResolvedAddress / Area 还停在刚填的冲突地址上，
                    // 不清就会「界面显示旧地址、内部却按新地址读报文」。
                    item.Address = previousAddress;
                    _globalVarSet.ResolveOnly(item);
                    tb.Text = item.Address;
                }
                else if (tb.Text != item.Address)
                {
                    // 解析成功时元件符号已归一大写（y27 → Y27），把规范形式回填到框里，
                    // 与自由监控的名称框一致；不这么做，界面留着 y27、json 里却是 Y27
                    tb.Text = item.Address;
                }
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

            ThemeMessageDialog.Info(this, title, body);
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
            // ARRAY 问的是「元素类型 + 元素数量」，用的是自己那个声明对话框。
            if (matched == DataType.Array)
            {
                (DataType ElementType, int Count)? declared =
                    Ui.ArrayDeclareDialog.Ask(this, item.Model.ElementType, item.Model.Length);

                if (declared == null)
                {
                    // 取消：下拉框退回原类型，模型一个字不动
                    SyncGlobalVarTypeCombo(combo, item);
                    return;
                }

                item.Model.ElementType = declared.Value.ElementType;
                item.Model.Length = declared.Value.Count;
            }
            else if (matched == DataType.String)
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
