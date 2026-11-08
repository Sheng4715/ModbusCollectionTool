using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Polling;
using DSH_Modbus采集工具.Scanners;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

// ============================================================
// 扫码器（P4.5 连接 + P3.5 交互配置）
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- 字段 ----------

        /// <summary>扫码器定义集合（P4.5 与 P3.5 **共享一份**，见 Brief P3.5）。</summary>
        private readonly ScannerItemSet _scannerSet = new();

        /// <summary>每台扫码器各持一个连接会话 —— 16 台互不影响（P4.5）。</summary>
        private readonly Dictionary<ScannerDefinition, ScannerSession> _scannerSessions = new();

        /// <summary>每台扫码器的运行时态（上升沿记忆 / 编译结果 / 执行中互斥）。</summary>
        private readonly Dictionary<ScannerDefinition, ScannerRuntimeState> _scannerRuntime = new();

        /// <summary>
        /// 触发判定节拍：50ms 常驻，**不随页面可见性停**（与采集任务同一个口径，
        /// 见 <c>RunAcquisitionTick</c> 的注释）。
        /// </summary>
        private readonly DispatcherTimer _scannerTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        // 表单不再是「右列一份共享表单」，而是每行自带一份展开块（见 ScannerItemTemplate），
        // 故没有 _selectedScanner / _scannerFormSyncing —— 每个处理器都从 sender 的
        // DataContext 取自己那一台扫码器。

        // ---------- 初始化 / 收尾 ----------

        /// <summary>
        /// 构造期初始化：载入定义、绑定列表、起判定节拍。
        /// 由构造函数在 <c>_acqTimer</c> 附近调用（见 MainWindow.xaml.cs）。
        /// <para>
        /// ⚠️ 2026-11-04 起**只有一个列表**：原「数据管理 → 扫码器」那一页已合并进
        /// 「设备连接 → 扫码器连接」，连接参数与交互参数同处一行的展开块里。
        /// </para>
        /// </summary>
        private void InitializeScanners()
        {
            _scannerSet.Load();

            if (ScannerItemsControl != null)
            {
                ScannerItemsControl.ItemsSource = _scannerSet.Items;
            }

            // 连接方式 / 协议 / 结束符三个下拉框的候选项挂在 ScannerItem 上
            // （它们长在行内展开块里，模板内取不到 x:Name，只能靠绑定）

            RefreshScannerView();

            // 载入的那些行也要先算一遍「还缺什么」，否则展开后红字要等第一次失焦才出现
            RefreshAllScannerItemStates();

            _scannerTimer.Tick += (_, _) => RunScannerTick();
            _scannerTimer.Start();
        }

        /// <summary>关窗收尾：停节拍并释放全部连接（每台独立，见 Brief P4.5）。</summary>
        private void ShutdownScanners()
        {
            _scannerTimer.Stop();

            foreach (KeyValuePair<ScannerDefinition, ScannerSession> pair in _scannerSessions)
            {
                pair.Value.CancelConnect();
                pair.Value.CancelReconnect();
                pair.Value.ReleaseClient();
                pair.Value.ForgetTarget();
            }

            _scannerSessions.Clear();
            _scannerRuntime.Clear();
        }

        /// <summary>
        /// 启动时自动连接**全部已启用**的扫码器。
        /// <para>
        /// 「启用」就是那张行上的开关（<see cref="ScannerDefinition.Enabled"/>）。
        /// ⚠️ 2026-11-05 定案：**不设全局的「扫码器自动连接」开关** ——
        /// 一度在设置页加过一个，但它与「启动连不连」和「运行期要不要连」两种语义纠缠不清：
        /// 关掉后触发沿仍会走 <c>EnsureScannerConnecting</c> 把连接建起来，开关形同虚设。
        /// 每台自己的「启用」开关语义干净（关掉 = 这台不触发、不收发、也不自动连），
        /// 且能按台区分，比一个全局开关更贴合现场。
        /// </para>
        /// </summary>
        private void TryAutoConnectScanners()
        {
            if (_windowClosed)
            {
                return;
            }

            foreach (ScannerItem item in _scannerSet.Items)
            {
                // 判「用户意图」而不是 EffectiveEnabled：连接不等于触发，
                // 与「关掉启用只挡触发与收发，连接仍在」同口径。
                if (!item.Model.Enabled || !HasUsableScannerEndpoint(item.Model))
                {
                    continue;
                }

                // ⚠️ 只补「还没连上」的缺口：50ms 节拍里的 EnsureScannerConnecting
                // （每拍都跑，不等触发沿）可能已经先连上了，这里再连一次就成了双重连接 ——
                // 日志里「已连接」连打两行、旧的 client 被 MarkConnected 覆盖后泄漏，
                // 而服务端（如 Bittly）对同一台扫码器只认一条连接，第二条会挤掉第一条，
                // 最终会话里那根连接已被对端关闭，下一次触发就报
                // 「The operation is not allowed on non-connected sockets」。
                ScannerSession session = GetOrCreateScannerSession(item.Model);
                if (session.Client != null || session.IsConnecting || session.IsReconnecting)
                {
                    continue;
                }

                // 50ms 节拍可能已经试过并失败：仍受退避约束，别在这里绕过闸门重连
                if (!session.CanAttemptNow(DateTime.UtcNow))
                {
                    continue;
                }

                // 16 台各自连接，互不等待（一台慢不该拖住其它台）
                _ = TryConnectScannerAsync(item, isReconnect: false, CancellationToken.None);
            }
        }

        private static bool HasUsableScannerEndpoint(ScannerDefinition def) =>
            !string.IsNullOrWhiteSpace(def.Ip) && def.Port is > 0 and <= 65535;

        // ---------- P4.5：PLC 连接 / 扫码器连接 两档页签 ----------

        private void DeviceView_Checked(object sender, RoutedEventArgs e)
        {
            if (PlcConnectionSegment == null || ScannerConnectionSegment == null
                || PlcConnectionPanel == null || ScannerConnectionPanel == null)
            {
                return;
            }

            bool scannerView = ScannerConnectionSegment.IsChecked == true;

            PlcConnectionPanel.Visibility = scannerView ? Visibility.Collapsed : Visibility.Visible;
            ScannerConnectionPanel.Visibility = scannerView ? Visibility.Visible : Visibility.Collapsed;

            if (scannerView)
            {
                RefreshScannerView();
            }
        }

        // ---------- P4.5：扫码器列表 ----------

        /// <summary>
        /// 新增一台扫码器：**不弹对话框**，直接在列表末尾落一行并展开，参数就在行里填。
        /// <para>
        /// 名字先给一个不重名的默认名（scanner1 / scanner2 …）：行里要显示它、日志里也会用到它。
        /// 先落一行再让用户就地改名，比「先弹框输名字」顺手（弹框里那几项参数反正都要在行里再填一遍）。
        /// </para>
        /// </summary>
        private void AddScanner_Click(object sender, RoutedEventArgs e)
        {
            if (_scannerSet.Items.Count >= ScannerRules.MaxScanners)
            {
                ThemeMessageDialog.Info(
                    this,
                    "无法新增",
                    $"最多只能添加 {ScannerRules.MaxScanners} 台扫码器。");
                return;
            }

            var model = new ScannerDefinition
            {
                Name = ScannerRules.NextName(_scannerSet.Items.Select(x => x.Model)),
            };

            ScannerItem item = _scannerSet.Add(model);

            // 新增的这台直接展开（下一步就是填 IP / 端口，再往右把交互配完 ——
            // 两页合并后 13 项都在同一个展开块里，一次展开就够）
            item.IsExpanded = true;
            RefreshScannerItemState(item);
            RefreshScannerView();

            ScannerItemsControl?.UpdateLayout();
            FocusScannerName(item);
        }

        private void RemoveScanner_Click(object sender, RoutedEventArgs e)
        {
            if (ScannerRowOf(sender) is ScannerItem item)
            {
                DeleteScanner(item);
            }
        }

        /// <summary>
        /// 删除一台扫码器：先断连接、清运行时态，再删定义。
        /// P3.5 的交互配置与定义是**同一份**，故一并消失（Brief P4.5）。
        /// </summary>
        private void DeleteScanner(ScannerItem item)
        {
            DropScannerSession(item.Model);

            _scannerSet.Remove(item);
            RefreshScannerView();
        }

        private void ScannerRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScannerItem item)
            {
                fe.Tag = item;
            }
        }

        /// <summary>名称框失焦：交给唯一的提交点。</summary>
        private void ScannerName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                CommitScannerName(box);
            }
        }

        /// <summary>名称框按回车：等同失焦，立刻校验。</summary>
        private void ScannerName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box)
            {
                return;
            }

            e.Handled = true;
            CommitScannerName(box);
        }

        /// <summary>
        /// 名称失焦（回车同效）：**唯一提交点**。
        /// <para>
        /// 不做对话框：名字和其它连接参数一样在行里改。校验只有两条
        /// （非空、不与别的扫码器重名），失败就回退到上次有效名并把原因写进
        /// <see cref="ScannerItem.Problem"/> —— 与端口那条同一套口径。
        /// </para>
        /// </summary>
        private void CommitScannerName(TextBox box)
        {
            if (ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            string wanted = box.Text.Trim();

            if (string.Equals(item.Model.Name, wanted, StringComparison.Ordinal))
            {
                // 只是过了一遍焦点，没有变化：把可能残留的空格修掉即可
                box.Text = item.Model.Name;
                return;
            }

            if (wanted.Length == 0)
            {
                RejectScannerField(item, box, item.Model.Name, "扫码器名称不能为空", ScannerField.Name);
                return;
            }

            if (_scannerSet.IsNameTaken(wanted, item))
            {
                RejectScannerField(
                    item, box, item.Model.Name, $"名称「{wanted}」已被别的扫码器使用", ScannerField.Name);
                return;
            }

            _scannerSet.Rename(item, wanted);
            box.Text = item.Model.Name;

            // 名字变了：配置页那行、日志前缀、引用提示都跟着换
            item.RefreshFields();
            RefreshScannerItemState(item);
            RefreshScannerView();
        }

        /// <summary>把焦点移到某台的名称框（新增后连续录入用）。</summary>
        private void FocusScannerName(ScannerItem item)
        {
            if (ScannerItemsControl == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < ScannerItemsControl.Items.Count; i++)
                {
                    if (ScannerItemsControl.ItemContainerGenerator.ContainerFromIndex(i)
                            is not DependencyObject container)
                    {
                        continue;
                    }

                    if (container is FrameworkElement fe && fe.DataContext is ScannerItem rowItem
                        && ReferenceEquals(rowItem, item))
                    {
                        FindScannerBox(container, "ScannerNameBox")?.Focus();
                        return;
                    }
                }
            }));
        }

        /// <summary>在模板里按 x:Name 找输入框（行模板取不到名字，只能遍历可视树）。</summary>
        private static TextBox? FindScannerBox(DependencyObject root, string name)
        {
            if (root is TextBox tb && tb.Name == name)
            {
                return tb;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                TextBox? found = FindScannerBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static ScannerItem? ScannerRowOf(object sender)
        {
            if (sender is not FrameworkElement fe)
            {
                return null;
            }

            return fe.DataContext as ScannerItem ?? fe.Tag as ScannerItem;
        }

        // ---------- 列表（每行自带展开块：左连接 / 右交互） ----------

        private void RefreshScannerView()
        {
            if (ScannerItemsControl == null)
            {
                return;
            }

            int n = _scannerSet.Items.Count;

            // 表头不随空列表隐藏 —— 它是列说明，也是「+」的落点
            if (ScannerEmptyHint != null)
            {
                ScannerEmptyHint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AddScannerButton != null)
            {
                AddScannerButton.IsEnabled = n < ScannerRules.MaxScanners;
            }

            // 表单早已搬进每行的展开块，这里只剩「空提示 + 还能不能加」两件事
            RefreshScannerReferenceHints();
        }

        /// <summary>展开 / 收起（行尾那枚箭头）：连接参数与交互参数同一个展开块，一起开合。</summary>
        private void ToggleScannerExpand_Click(object sender, RoutedEventArgs e)
        {
            if (ScannerRowOf(sender) is ScannerItem item)
            {
                item.IsExpanded = !item.IsExpanded;
            }
        }

        /// <summary>
        /// IP 失焦落盘（口径同设备连接页：**失焦才写**，逐字符写盘会把半截地址存进 json）。
        /// </summary>
        private void ScannerIp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string text = box.Text.Trim();

            if (string.Equals(def.Ip, text, StringComparison.Ordinal))
            {
                item.RefreshFields();
                return;
            }

            def.Ip = text;
            _scannerSet.Save();
            ResetScannerBackoff(def);
            item.RefreshFields();
            RefreshScannerItemState(item);
        }

        /// <summary>端口失焦落盘：非法值**保留上次的有效值**，不拿垃圾覆盖。</summary>
        private void ScannerPort_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string text = box.Text.Trim();

            if (text.Length == 0)
            {
                if (def.Port != 0)
                {
                    def.Port = 0;
                    _scannerSet.Save();
                    ResetScannerBackoff(def);
                }

                item.RefreshFields();
                RefreshScannerItemState(item);
                return;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                || port is < 1 or > 65535)
            {
                // 回退到上次的有效值（而不是清空），并把这个框标红
                box.Text = def.Port > 0 ? def.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
                item.Problem = "端口号需在 1 ~ 65535 之间";
                item.ProblemField = ScannerField.Port;
                return;
            }

            if (def.Port != port)
            {
                def.Port = port;
                _scannerSet.Save();
                ResetScannerBackoff(def);
            }

            item.RefreshFields();
            RefreshScannerItemState(item);
        }

        private void ScannerTerminator_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox combo || ScannerRowOf(combo) is not ScannerItem item)
            {
                return;
            }

            int index = combo.SelectedIndex;
            if (index < 0 || index > (int)ScannerFrameTerminator.None)
            {
                return;
            }

            var terminator = (ScannerFrameTerminator)index;
            if (item.Model.Terminator == terminator)
            {
                return;
            }

            item.Model.Terminator = terminator;
            _scannerSet.Save();
        }

        /// <summary>
        /// 把「这台扫码器还缺什么」写回它自己那一行：错在哪一栏（<see cref="ScannerItem.ProblemField"/>，
        /// 驱动该输入框标红）+ 那句话（<see cref="ScannerItem.Problem"/>，供行上「配置错误」判空与实际原因）。
        /// </summary>
        private void RefreshScannerItemState(ScannerItem item)
        {
            string? error = DescribeScannerProblems(item.Model, out ScannerField field);
            item.Problem = error;
            item.ProblemField = error == null ? ScannerField.None : field;
        }

        /// <summary>整个列表重算一遍（改名 / 变量增删 / 批量改动后调）。</summary>
        private void RefreshAllScannerItemStates()
        {
            foreach (ScannerItem item in _scannerSet.Items)
            {
                RefreshScannerItemState(item);
            }
        }

        /// <summary>
        /// 配置完整性判据：返回**第一条**待改项及其所属栏位（null + <see cref="ScannerField.None"/> = 可以正常工作）。
        /// 只做「一看就知道要改」的检查，发送期真正会失败的情况仍由执行路径兜底。
        /// <para>
        /// ⚠️ 栏位与那句话必须**同源**：界面只在栏位对应的那个框上标红，
        /// 若只回文案不回栏位，就会退化成「整行红」而定位不到框。
        /// </para>
        /// </summary>
        private string? DescribeScannerProblems(ScannerDefinition def, out ScannerField field)
        {
            if (string.IsNullOrWhiteSpace(def.Ip))
            {
                field = ScannerField.Ip;
                return "尚未设置 IP 地址";
            }

            if (def.Port is < 1 or > 65535)
            {
                field = ScannerField.Port;
                return "尚未设置端口号（1 ~ 65535）";
            }

            if (string.IsNullOrWhiteSpace(def.TriggerExpression))
            {
                field = ScannerField.TriggerExpression;
                return "尚未设置触发条件表达式";
            }

            if (!CompiledExpression.TryParse(def.TriggerExpression, out CompiledExpression? compiled,
                    out string parseError))
            {
                field = ScannerField.TriggerExpression;
                return parseError;
            }

            if (compiled != null && !compiled.TryValidateNames(ExpressionNameExists, out string nameError))
            {
                field = ScannerField.TriggerExpression;
                return nameError;
            }

            if (!ScannerRules.TryValidateTriggerText(def.TriggerText, out string triggerError))
            {
                field = ScannerField.TriggerText;
                return triggerError;
            }

            if (!ScannerRules.TryValidateTimeout(def.TimeoutSeconds, out string timeoutError))
            {
                field = ScannerField.TimeoutSeconds;
                return timeoutError;
            }

            string? bufferError = ValidateScannerBufferVariable(def.BufferVariableName);
            if (bufferError != null)
            {
                field = ScannerField.BufferVariable;
                return bufferError;
            }

            string? notifyError = ValidateScannerNotifyVariable(def.NotifyVariableName, out _);
            if (notifyError != null)
            {
                field = ScannerField.NotifyVariable;
                return notifyError;
            }

            field = ScannerField.None;
            return null;
        }

        private string? ValidateScannerBufferVariable(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                // 允许留空（结果只进内存也不会报错），但留空就没有接收方
                return null;
            }

            GlobalVariable? variable = _globalVarSet.FindByName(name.Trim());
            if (variable == null)
            {
                return $"接收缓冲区变量「{name.Trim()}」不存在";
            }

            return variable.Type == DataType.String
                ? null
                : $"接收缓冲区变量只能是 STRING，当前是 {variable.TypeDisplayName}";
        }

        /// <summary>
        /// 执行结果输出变量的校验：只能是整数类型（写入值固定为 1 / 2，非整数类型装不下）。
        /// </summary>
        private string? ValidateScannerNotifyVariable(string? name, out GlobalVariable? variable)
        {
            variable = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            GlobalVariable? found = _globalVarSet.FindByName(name.Trim());
            if (found == null)
            {
                return $"执行结果输出变量「{name.Trim()}」不存在";
            }

            if (found.Type is not (DataType.Int or DataType.UInt or DataType.DInt or DataType.UDInt))
            {
                return $"执行结果输出变量只能是 INT / UINT / DINT / UDINT，当前是 {found.TypeDisplayName}";
            }

            variable = found;
            return null;
        }

        // ---------- P3.5：变量名补全 ----------

        /// <summary>
        /// 触发条件表达式框：挂表达式补全（系统变量 + 全局变量），
        /// 与状态机 / 采集任务的表达式框同一份候选表。
        /// </summary>
        private void ScannerTriggerExpressionBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || _nameCompletions.TryGetValue(box, out _))
            {
                return;
            }

            Ui.CompletionPopup popup = Ui.CompletionPopup.Attach(
                box, () => CompletionSource.ForExpression(GlobalVarModels));

            _nameCompletions.Add(box, popup);
        }

        /// <summary>
        /// 接收缓冲区 / 通知变量框：只补全局变量（这两个字段只能填全局变量，
        /// 系统变量写不进去 —— 用 <c>ForMonitorName</c> 而不是 <c>ForExpression</c>）。
        /// </summary>
        private void ScannerBufferVariableBox_Loaded(object sender, RoutedEventArgs e) =>
            AttachScannerVariableCompletion(sender);

        private void ScannerNotifyVariableBox_Loaded(object sender, RoutedEventArgs e) =>
            AttachScannerVariableCompletion(sender);

        private void AttachScannerVariableCompletion(object sender)
        {
            if (sender is not TextBox box || _nameCompletions.TryGetValue(box, out _))
            {
                return;
            }

            Ui.CompletionPopup popup = Ui.CompletionPopup.Attach(
                box, () => CompletionSource.ForMonitorName(GlobalVarModels));

            _nameCompletions.Add(box, popup);
        }

        // ---------- P3.5：行内开关 ----------

        /// <summary>
        /// 行上「启用」开关（2026-11-05 起它是**扫码器唯一的自动连接开关**）。
        /// <para>
        /// 关掉 = 这台**不触发、不收发、也不自动连**：除写模型外还要
        /// ① 停掉在跑的重连、② 放掉现有连接并忘掉目标（<see cref="ScannerSession.ForgetTarget"/>）。
        /// 不这么做的话「关掉」只挡了触发，连接仍挂着 —— 用户看到的那一行写着「关」，
        /// 底下的 TCP 却还连着，属于界面在骗人。
        /// </para>
        /// <para>
        /// ⚠️ **不要用 <c>box.IsChecked</c> 判断意图，要读 <see cref="ScannerItem.Enabled"/>**：
        /// 那个复选柜绑的是 <c>EffectiveEnabled</c>（TwoWay），而 `Click` 事件
        /// **先于**绑定的源更新 —— 此时 `IsChecked` 可能还是旧值，读它会把「关掉」当成「打开」，
        /// 反而去连一次（实测踩过：点关后连接数不降反增）。
        /// </para>
        /// <para>
        /// ⚠️ 用 `ForgetTarget` 而不是只 `ReleaseClient`：后者会保留目标、下一拍
        /// <c>EnsureScannerConnecting</c> 又把它连回来（那是「异常断开后自动重连」的路径）。
        /// </para>
        /// </summary>
        private void ScannerEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            // ⚠️ 显式把控件状态写回模型，**不依赖 TwoWay 绑定的时序**：
            // 绑定目标是 `EffectiveEnabled`（`Model.Enabled && !HasConfigError`），
            // 而 `Click` 触发时源可能还没更新；若此时读 `item.Enabled` 拿到的是旧值，
            // 「关掉」会被当成「打开」，反而去连一次（实测：点关后连接数不增反降不了）。
            item.Enabled = box.IsChecked == true;
            _scannerSet.Save();

            bool enabled = item.Enabled;

            if (!enabled)
            {
                // 关掉：断开并忘掉目标，免得被自动重连接管
                if (_scannerSessions.TryGetValue(item.Model, out ScannerSession? session))
                {
                    session.CancelReconnect();
                    session.ReleaseClient();
                    session.ForgetTarget();
                }
            }
            else if (!_windowClosed && HasUsableScannerEndpoint(item.Model))
            {
                // 打开：立刻连一次（无须等下一拍；下一拍的 EnsureScannerConnecting 也会兜底）
                EnsureScannerConnecting(item);
            }
        }

        // ---------- P3.5：交互参数提交（失焦 / 回车） ----------

        private void ScannerTriggerExpression_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string text = box.Text.Trim();

            if (text.Length == 0)
            {
                RejectScannerField(item, box, def.TriggerExpression, "触发条件表达式不能为空",
                    ScannerField.TriggerExpression);
                return;
            }

            if (!CompiledExpression.TryParse(text, out CompiledExpression? compiled, out string parseError))
            {
                RejectScannerField(item, box, def.TriggerExpression, parseError, ScannerField.TriggerExpression);
                return;
            }

            if (compiled != null && !compiled.TryValidateNames(ExpressionNameExists, out string nameError))
            {
                RejectScannerField(item, box, def.TriggerExpression, nameError, ScannerField.TriggerExpression);
                return;
            }

            if (!string.Equals(def.TriggerExpression, text, StringComparison.Ordinal))
            {
                def.TriggerExpression = text;
                _scannerSet.Save();
            }

            RefreshScannerItemState(item);
        }

        private void ScannerTriggerText_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string text = box.Text;

            if (!ScannerRules.TryValidateTriggerText(text, out string error))
            {
                RejectScannerField(item, box, def.TriggerText, error, ScannerField.TriggerText);
                return;
            }

            if (!string.Equals(def.TriggerText, text, StringComparison.Ordinal))
            {
                def.TriggerText = text;
                _scannerSet.Save();
            }

            RefreshScannerItemState(item);
        }

        private void ScannerBufferVariable_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string name = box.Text.Trim();

            string? error = ValidateScannerBufferVariable(name);
            if (error != null)
            {
                RejectScannerField(item, box, def.BufferVariableName, error, ScannerField.BufferVariable);
                return;
            }

            if (!string.Equals(def.BufferVariableName, name, StringComparison.Ordinal))
            {
                def.BufferVariableName = name;
                _scannerSet.Save();
            }

            RefreshScannerReferenceHints();
            RefreshScannerItemState(item);
        }

        private void ScannerTimeout_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string text = box.Text.Trim();

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !ScannerRules.TryValidateTimeout(seconds, out string error))
            {
                ScannerRules.TryValidateTimeout(0, out string rangeError);
                RejectScannerField(
                    item,
                    box,
                    def.TimeoutSeconds.ToString("0.0#", CultureInfo.InvariantCulture),
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? rangeError
                        : "超时判定时间需填写数字",
                    ScannerField.TimeoutSeconds);
                return;
            }

            if (Math.Abs(def.TimeoutSeconds - seconds) > double.Epsilon)
            {
                def.TimeoutSeconds = seconds;
                _scannerSet.Save();
            }

            RefreshScannerItemState(item);
        }

        private void ScannerNotifyVariable_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || ScannerRowOf(box) is not ScannerItem item)
            {
                return;
            }

            ScannerDefinition def = item.Model;
            string name = box.Text.Trim();

            string? error = ValidateScannerNotifyVariable(name, out _);
            if (error != null)
            {
                RejectScannerField(item, box, def.NotifyVariableName, error, ScannerField.NotifyVariable);
                return;
            }

            if (!string.Equals(def.NotifyVariableName, name, StringComparison.Ordinal))
            {
                def.NotifyVariableName = name;
                _scannerSet.Save();
            }

            RefreshScannerReferenceHints();
            RefreshScannerItemState(item);
        }

        /// <summary>
        /// 字段校验失败：把框里的文本回退到模型旧值，并记下「错的是这一栏」——
        /// 该框边框随即变红（<see cref="ScannerItem.ProblemField"/>），顶部不再有红字。
        /// 展开块是每行一份，故这里必须带上 <paramref name="item"/>（改造前靠共享的
        /// <c>_selectedScanner</c> + 唯一的错误行）。
        /// </summary>
        private static void RejectScannerField(
            ScannerItem item, TextBox box, string? oldValue, string? error, ScannerField field)
        {
            box.Text = oldValue ?? string.Empty;
            item.Problem = error;
            item.ProblemField = field;
        }

        // ---------- P3.5：变量被别处引用的黄字提示 ----------

        /// <summary>
        /// 重算每台的「变量已在别处被引用」。改造前它只刷共享表单上的两个 TextBlock，
        /// 现在每行各有一份，故必须遍历全表（<c>MainWindow.GlobalVar.cs</c> 的
        /// <c>RefreshAfterVariableChange</c> 也调这一个）。
        /// </summary>
        private void RefreshScannerReferenceHints()
        {
            foreach (ScannerItem item in _scannerSet.Items)
            {
                ScannerDefinition def = item.Model;

                item.BufferShared = !string.IsNullOrWhiteSpace(def.BufferVariableName)
                    && ScannerRules.CountReferences(
                        _scannerSet.Items.Select(i => i.Model), def, def.BufferVariableName) > 0;

                item.NotifyShared = !string.IsNullOrWhiteSpace(def.NotifyVariableName)
                    && ScannerRules.CountReferences(
                        _scannerSet.Items.Select(i => i.Model), def, def.NotifyVariableName) > 0;

                item.RefreshFields();
            }
        }

        // ---------- 判定节拍（50ms） ----------

        /// <summary>
        /// 触发判定：**不要求 PLC 已连接**（未连照样触发/收发，只是结果写不回 PLC，见 Brief 口径 B）。
        /// <para>
        /// 与 <c>RunAcquisitionTick</c> 同一套范式：常驻节拍、逐台独立、fire-and-forget。
        /// </para>
        /// </summary>
        private void RunScannerTick()
        {
            if (_windowClosed || _scannerSet.Items.Count == 0)
            {
                return;
            }

            // 触发表达式可能引用系统变量（脉冲 / 时钟 / 运行时长），先刷一次快照。
            // ⚠️ 用带 break 的早退扫描（原来 Items.Any 会走完整表）：该判断每 50ms 跑一次。
            bool needsSystemValues = false;
            foreach (ScannerItem probe in _scannerSet.Items)
            {
                if (probe.EffectiveEnabled && !string.IsNullOrWhiteSpace(probe.Model.TriggerExpression))
                {
                    needsSystemValues = true;
                    break;
                }
            }

            if (needsSystemValues)
            {
                _machineVariables.UpdateSystemValues(
                    SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed));
            }

            foreach (ScannerItem item in _scannerSet.Items)
            {
                // 连接维护：**不等触发沿**（2026-11-05 方案 B）。启用的设备只要没连上、
                // 退避到期就补一次 —— 早先只由触发沿驱动，遇到「常真」表达式（如 sys_True）
                // 只有一个上升沿，首连失败后就再也不会重试。
                // 判 Model.Enabled 而不是 EffectiveEnabled：连接不等于触发，
                // 与启动的 TryAutoConnectScanners 同口径。
                EnsureScannerConnecting(item);

                ScannerDefinition def = item.Model;

                // 关掉启用的只挡触发与收发，**连接仍在**（Brief P3.5）；
                // 配置错误时同样不出手（与界面「配置错误不能启用」同口径）
                if (!item.EffectiveEnabled)
                {
                    continue;
                }

                ScannerRuntimeState state = GetOrCreateScannerRuntime(def);

                bool condition = EvaluateScannerTrigger(def, state);

                // WasConditionTrue 是自动属性，不能直接 ref（CS0206）—— 过一手局部变量再写回。
                // ⚠️ **每拍都必须写回**，不能只在「触发那一拍」写：ShouldFire 的副作用就是
                // 「记住这一拍的条件值」，只在触发时记会让状态永远停在最后一次的 true，
                // 之后每拍都算成「上拍真、这拍也真」，**第二个上升沿永远出不来**
                // （实测：脉冲触发在整段会话里只发一次，之后彻底静默）。
                // 与采集任务同一口径 —— 那边把推进写在 AcquisitionTrigger.ShouldFire 里面，
                // 因为它是「传 state 进去」而不是「传 ref 出来」，没这个坑。
                bool wasTrue = state.WasConditionTrue;
                bool rising = ScannerTrigger.ShouldFire(condition, ref wasTrue);
                state.WasConditionTrue = wasTrue;

                // 上一轮没跑完就忽略本次上升沿 —— 不排队、不堆积（Brief P3.5）。
                // ⚠️ 位置在状态推进**之后**：边沿这一次可以丢（这就是「不排队」），
                // 但「这一拍条件是什么」必须记住，否则执行期间漏记一次假值，
                // 本轮结束之后就再也认不出下一个上升沿了。
                if (state.InFlight)
                {
                    continue;
                }

                if (!rising)
                {
                    continue;
                }

                state.InFlight = true;

                // fire-and-forget：await 会卡住 50ms 节拍
                _ = RunScannerExchangeAsync(item, state);
            }
        }

        private ScannerRuntimeState GetOrCreateScannerRuntime(ScannerDefinition def)
        {
            if (!_scannerRuntime.TryGetValue(def, out ScannerRuntimeState? state))
            {
                state = new ScannerRuntimeState();
                _scannerRuntime.Add(def, state);
            }

            return state;
        }

        /// <summary>求值触发表达式（编译结果按源文本缓存，源变了才重编）。</summary>
        private bool EvaluateScannerTrigger(ScannerDefinition def, ScannerRuntimeState state)
        {
            string source = def.TriggerExpression ?? string.Empty;

            if (!string.Equals(state.CompiledSource, source, StringComparison.Ordinal))
            {
                state.CompiledSource = source;
                state.Compiled = CompiledExpression.TryParse(source, out CompiledExpression? compiled, out _)
                                 && compiled != null
                    ? compiled
                    : CompiledExpression.Empty;
            }

            if (state.Compiled == null || state.Compiled.IsEmpty)
            {
                return false;
            }

            return state.Compiled.TryEvaluate(_machineVariables, out bool value, out _) && value;
        }

        /// <summary>
        /// 一轮完整交互：丢弃残留 → 原样发触发文本 → 等结果帧 → 回填。
        /// 超时 / 未连接 / 链路异常一律走「超时档」（Brief P3.5 数据通路）。
        /// </summary>
        private async Task RunScannerExchangeAsync(ScannerItem item, ScannerRuntimeState state)
        {
            ScannerDefinition def = item.Model;

            try
            {
                ScannerSession session = GetOrCreateScannerSession(def);
                ScannerTcpClient? client = session.Client;

                // 以**客户端自己的实际套接字状态**为准，而不是只看 session 的标志：
                // ScannerTcpClient 在超时/异常时会自己 Dispose 掉 _tcp（防半帧失步），
                // 此时 session.Client 仍非空、session.IsConnected 仍为 true ——
                // 只用 session 判断会拿一根失效连接去发报文，报
                // 「The operation is not allowed on non-connected sockets」并误判成通讯中断。
                if (client == null || !client.IsConnected)
                {
                if (client == null || !client.IsConnected)
                {
                    // 曾连上后失效 → 清引用并走退避重连；从未连上 → 起首次连接
                    if (client != null)
                    {
                        // 这条分支的「连接层面后果」由 NotifyScannerLost 记「通讯中断」，
                        // **不要再补一条「读取失败」** —— 同一次故障记两条只是噪音
                        // （实测：连接失败 ERROR 紧跟一条 读取失败 WARN，用户以为出了两个问题）。
                        NotifyScannerLost(item, client);
                    }
                    else
                    {
                        EnsureScannerConnecting(item);

                        // 「从未连上」这一档没有人记过连接层日志（连接还在异步建立中，
                        // 失败由重连循环在退避到期后记「连接失败」），故这里补一条读取失败。
                        // 否则「触发条件成立了、但设备压根没连上」在日志里完全看不出来。
                        if (!_windowClosed)
                        {
                            AppendDeviceLog(
                                $"扫码器（{def.Name}）读取失败：{ScannerRules.NotConnectedReason}",
                                DeviceLogLevel.Warning);
                        }
                    }

                    await ApplyScannerTimeoutAsync(item).ConfigureAwait(true);
                    return;
                }
                }
                byte[] trigger = Encoding.ASCII.GetBytes(def.TriggerText ?? string.Empty);
                byte[] terminator = ScannerRules.TerminatorBytes(def.Terminator);
                TimeSpan timeout = TimeSpan.FromSeconds(
                    def.TimeoutSeconds > 0 ? def.TimeoutSeconds : ScannerRules.DefaultTimeoutSeconds);

                try
                {
                    // 非响应期的主动数据一律丢弃：只有发触发之后收到的才归入本次结果
                    await client.DiscardPendingAsync().ConfigureAwait(true);
                    await client.SendAsync(trigger).ConfigureAwait(true);

                    byte[] frame = await client.ReadFrameAsync(terminator, timeout).ConfigureAwait(true);

                    string received = Encoding.ASCII.GetString(frame);

                    // 正常读到一帧：记一条中性日志（与「连接 / 断开」同级的运行记录）。
                    // 用户定稿格式：「扫码器（名称）读取完成：<收到内容>」
                    // ⚠️ 内容必须经 FormatLogContent 清洗：扫码结果常带 \r\n 结束符，
                    // 原样拼进去会把一条日志劈成多行、落盘文件的行格式也跟着坏掉。
                    AppendDeviceLog(
                        $"扫码器（{def.Name}）读取完成：{ScannerRules.FormatLogContent(received)}",
                        DeviceLogLevel.Neutral);

                    await ApplyScannerSuccessAsync(item, received).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // 超时窗口内没收到完整帧 = 本轮超时（含关窗时的取消）。
                    // 关窗引起的取消不记日志 —— 那不是「设备没响应」，只是程序在退出。
                    if (!_windowClosed)
                    {
                        AppendDeviceLog(
                            $"扫码器（{def.Name}）读取失败：服务端响应超时",
                            DeviceLogLevel.Warning);
                    }

                    await ApplyScannerTimeoutAsync(item).ConfigureAwait(true);

                    // 超时那一刻 ReadFrameAsync 已 Dispose 掉连接（防半帧失步）。
                    // 只有客户端此刻确实断了才通知断线 → 起退避重连；若只是外部取消
                    // （关窗），连接可能还活着，不要误断。
                    if (!_windowClosed && !client.IsConnected)
                    {
                        NotifyScannerLost(item, client);
                    }
                }
                catch (Exception ex)
                {
                    // 链路异常：ScannerTcpClient 内部已关连接，这里负责记账 + 起重连。
                    // 只有当 session 仍认为这根连接是「当前连接」时才记日志/重连；
                    // ReferenceEquals 挡住「已经被更替的旧 client」重复上报。
                    if (!_windowClosed && ReferenceEquals(session.Client, client))
                    {
                        // 链路异常对「这次读取」而言就是失败，按用户定稿的格式记一条。
                        // ⚠️ **只记这一条**：NotifyScannerLost 本身不写日志（只做断引 + 起重连），
                        // 所以这里不要额外再记「通讯中断」—— 同一次故障两条日志属于噪音，
                        // 用户会以为设备同时出了两个问题。
                        // 原因复用 ClassifyDisconnectReason，与底栏/PLC 侧同一套中文归一。
                        AppendDeviceLog(
                            $"扫码器（{def.Name}）读取失败：{ClassifyDisconnectReason(ex.Message)}",
                            DeviceLogLevel.Warning);

                        NotifyScannerLost(item, client);
                    }

                    await ApplyScannerTimeoutAsync(item).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                if (!_windowClosed)
                {
                    AppendDeviceLog($"扫码器「{def.Name}」执行异常：{ex.Message}", DeviceLogLevel.Warning);
                }
            }
            finally
            {
                // 无论成败都要放开互斥，否则这台扫码器再也不会触发
                state.InFlight = false;
            }
        }

        /// <summary>
        /// 正常返回（**空串也算**）：缓冲区写结果，执行结果输出变量写
        /// <see cref="ScannerRules.ResultSuccess"/>（固定 1）。
        /// </summary>
        private async Task ApplyScannerSuccessAsync(ScannerItem item, string result)
        {
            ScannerDefinition def = item.Model;

            await ApplyScannerVariableAsync(
                FindScannerVariable(def.BufferVariableName),
                result,
                "接收缓冲区").ConfigureAwait(true);

            await ApplyScannerVariableAsync(
                FindScannerVariable(def.NotifyVariableName),
                ScannerRules.ResultSuccess,
                "执行结果输出变量").ConfigureAwait(true);
        }

        /// <summary>
        /// 异常档（超时 / 链路异常）：**缓冲区保留原值，一个字节都不动**，
        /// 只把执行结果输出变量写成 <see cref="ScannerRules.ResultFailure"/>（固定 2）。
        /// <para>
        /// 2026-11-04 定：原先超时会往缓冲区写「超时写入文本」，那个框已删除。
        /// 超时时上一轮收到的结果仍然有效，拿空串或占位文本覆盖只会丢信息。
        /// </para>
        /// </summary>
        private async Task ApplyScannerTimeoutAsync(ScannerItem item)
        {
            ScannerDefinition def = item.Model;

            await ApplyScannerVariableAsync(
                FindScannerVariable(def.NotifyVariableName),
                ScannerRules.ResultFailure,
                "执行结果输出变量").ConfigureAwait(true);
        }

        private GlobalVariable? FindScannerVariable(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null : _globalVarSet.FindByName(name.Trim());

        /// <summary>
        /// 一次「写变量」：内存值无条件更新（PLC 未连时结果照样进内存），
        /// 有映射地址且 PLC 已连时才下发 FC06 / FC16。
        /// </summary>
        private async Task ApplyScannerVariableAsync(GlobalVariable? variable, string? text, string label)
        {
            if (variable == null || text == null)
            {
                return;
            }

            try
            {
                // 先编码：同时得到「按声明长度截断后的规范文本」与数值形态
                if (!ValueCodec.TryEncodeRegisters(
                        variable.Type, variable.Format, text, variable.Length, _byteOrders,
                        out ushort[] registers, out string encodeError))
                {
                    AppendDeviceLog(
                        $"{label}：写入「{variable.Name}」失败：{encodeError}", DeviceLogLevel.Warning);
                    return;
                }

                variable.Value = ValueCodec.FormatRegisters(
                    variable.Type, registers, variable.Format, _byteOrders, variable.Length);

                // 实时卡片一律十进制显示，不能从 Value 反解（见 GlobalReadPlan.Apply 的注释）
                variable.Numeric = ValueCodec.DecodeNumber(variable.Type, registers, _byteOrders);

                if (!variable.HasAddress)
                {
                    // 内存变量：没有寄存器可写，也不需要连接
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendDeviceLog($"{label}：写入「{variable.Name}」失败：{ex.Message}", DeviceLogLevel.Warning);
                return;
            }

            ModbusTcpClient? modbus = _session.Client;
            if (modbus == null || !_session.IsConnected)
            {
                // 口径 B：PLC 未连时只进内存、不报错（Brief P4.5）
                return;
            }

            AcquisitionWriteStatus status = AcquisitionWrite.TryBuild(
                variable, text, variable.Format, _byteOrders, out WritePlan plan, out string buildError);

            if (status == AcquisitionWriteStatus.CannotAddress)
            {
                // 没有映射地址：上面的内存回填已经做过了（Value + Numeric 都设了），
                // 到此为止 —— 没有寄存器可发。沿用原口径：不算失败、不记日志。
                return;
            }

            if (status == AcquisitionWriteStatus.Failed)
            {
                AppendDeviceLog(
                    $"{label}：写入「{variable.Name}」失败：{buildError}", DeviceLogLevel.Warning);
                return;
            }

            try
            {
                await WriteToDeviceAsync(modbus, plan).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 关窗 / 断开途中的取消，不必打扰用户
            }
            catch (Exception ex)
            {
                AppendDeviceLog(
                    $"{label}：写入「{variable.Name}」失败：{ClassifyWriteError(ex)}", DeviceLogLevel.Warning);
            }
        }

        // ---------- 连接生命周期（每台独立） ----------

        /// <summary>
        /// 端点（IP / 端口）改了就清掉失败计数与退避等待。
        /// 不重置的话，把 IP 改对之后还要白等最多 10 秒才重试，看起来像「改了也没用」。
        /// </summary>
        private void ResetScannerBackoff(ScannerDefinition def)
        {
            if (_scannerSessions.TryGetValue(def, out ScannerSession? session))
            {
                session.ResetBackoff();
            }
        }

        private ScannerSession GetOrCreateScannerSession(ScannerDefinition def)
        {
            if (!_scannerSessions.TryGetValue(def, out ScannerSession? session))
            {
                session = new ScannerSession();
                _scannerSessions.Add(def, session);
            }

            return session;
        }

        /// <summary>丢掉一台扫码器的连接与运行时态（删除时用）。</summary>
        private void DropScannerSession(ScannerDefinition def)
        {
            if (_scannerSessions.TryGetValue(def, out ScannerSession? session))
            {
                session.CancelConnect();
                session.CancelReconnect();
                session.ReleaseClient();
                session.ForgetTarget();
                _scannerSessions.Remove(def);
            }

            _scannerRuntime.Remove(def);
        }

        /// <summary>
        /// 补一次连接（**不阻塞调用方**）。
        /// <para>
        /// ⚠️ **停用的一律不连**（2026-11-05）：行上的「启用」开关是扫码器唯一的
        /// 自动连接开关，关掉就该彻底不连。触发判定那边虽然已按
        /// <c>EffectiveEnabled</c> 早退，但这里再加一道，免得将来有别的调用点
        /// 绕过判定把停用的扫码器连起来。
        /// </para>
        /// <para>
        /// ⚠️ **「从未连上」也要受退避约束**（2026-11-05 方案 B）：早先只有
        /// <c>Target != null</c>（曾经连上过）才进退避，首次失败后 <c>Target</c> 仍是
        /// <c>null</c>，于是下一拍立刻再连 —— 间隔只等于 3 秒连接超时，日志里
        /// 「连接超时」一行接一行。现在两条路共用
        /// <see cref="ScannerSession.CanAttemptNow"/> 这一道闸门，节奏由
        /// <see cref="ScannerSession.RegisterConnectFailure"/> 铺（1→2→4→5s）。
        /// </para>
        /// </summary>
        private void EnsureScannerConnecting(ScannerItem item)
        {
            if (_windowClosed || !item.Model.Enabled || !HasUsableScannerEndpoint(item.Model))
            {
                return;
            }

            ScannerSession session = GetOrCreateScannerSession(item.Model);

            if (session.IsConnecting || session.IsReconnecting)
            {
                return;
            }

            if (session.Client is { IsConnected: true })
            {
                return;
            }

            // 客户端还在但套接字已经断了（对端关闭 / 超时自关）：先放掉，
            // 否则新连接 MarkConnected 会把旧 client 盖住，旧的再没人 Dispose。
            if (session.Client != null)
            {
                session.ReleaseClient();
            }

            if (!session.CanAttemptNow(DateTime.UtcNow))
            {
                return;
            }

            if (session.Target != null)
            {
                // 曾经连上过：走退避重连，理由与 PLC 一致（现场拔线/上电顺序）
                StartScannerReconnect(item);
                return;
            }

            _ = TryConnectScannerAsync(item, isReconnect: false, CancellationToken.None);
        }

        private async Task<bool> TryConnectScannerAsync(ScannerItem item, bool isReconnect, CancellationToken token)
        {
            ScannerDefinition def = item.Model;

            if (!HasUsableScannerEndpoint(def))
            {
                return false;
            }

            ScannerSession session = GetOrCreateScannerSession(def);

            // 「本轮第一次失败」= 自上次连上以来还没失败过。只有这一次记日志，
            // 之后的重试全部静默（方案 B，2026-11-05）。
            bool firstFailure = session.Attempt == 0;

            var client = new ScannerTcpClient();
            using CancellationTokenSource connectCts = session.BeginConnect(token);

            try
            {
                await client.ConnectAsync(
                    def.Ip.Trim(), def.Port, TimeSpan.FromSeconds(3), null, connectCts.Token)
                    .ConfigureAwait(true);

                if (_windowClosed || !item.Model.Enabled)
                {
                    // 关窗，或用户在连接途中把这一台停用了：这次连接作废
                    client.Dispose();
                    return false;
                }

                session.MarkConnected(client, new ScannerTarget(def.Ip.Trim(), def.Port, null));

                // 连接状态**只在日志显示**，不动底栏（底栏是 PLC 的，Brief P4.5）
                AppendDeviceLog(
                    $"扫码器「{def.Name}」已连接：{def.Ip.Trim()}:{def.Port}", DeviceLogLevel.Connected);
                return true;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();

                if (!_windowClosed && !token.IsCancellationRequested)
                {
                    session.RegisterConnectFailure(DateTime.UtcNow);

                    // ⚠️ 重连尝试不写日志（2026-11-05 对齐 PLC 的 ADR-14）：
                    // 断线那一刻已有「通讯中断」、连上时有「已连接」，中间每 1~10 秒
                    // 记一行只会在 16 台并发时把运行日志冲干净。
                    if (!isReconnect && firstFailure)
                    {
                        AppendDeviceLog(
                            $"扫码器「{def.Name}」连接超时",
                            DeviceLogLevel.Disconnected);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                client.Dispose();

                if (!_windowClosed && !token.IsCancellationRequested)
                {
                    session.RegisterConnectFailure(DateTime.UtcNow);

                    if (!isReconnect && firstFailure)
                    {
                        AppendDeviceLog(
                            $"扫码器「{def.Name}」连接失败：{ClassifyDisconnectReason(ex.Message)}",
                            DeviceLogLevel.Disconnected);
                    }
                }

                return false;
            }
            finally
            {
                session.EndConnect(connectCts);
            }
        }

        /// <summary>连接掉了：清客户端（**目标留着**）并起重连，与 PLC 连接互不影响。</summary>
        private void NotifyScannerLost(ScannerItem item, ScannerTcpClient client)
        {
            ScannerSession session = GetOrCreateScannerSession(item.Model);

            if (!ReferenceEquals(session.Client, client))
            {
                return;
            }

            session.ReleaseClient();
            StartScannerReconnect(item);
        }

        private void StartScannerReconnect(ScannerItem item)
        {
            if (_windowClosed)
            {
                return;
            }

            ScannerSession session = GetOrCreateScannerSession(item.Model);

            if (session.IsReconnecting || session.IsConnecting)
            {
                return;
            }

            CancellationTokenSource cts = session.BeginReconnect();
            _ = ReconnectScannerLoopAsync(item, cts);
        }

        /// <summary>
        /// 断线重连：退避表沿用 <see cref="ReconnectPolicy"/>（1→2→4→5s，封顶），连上即清零。
        /// <para>
        /// 与 PLC 的 <c>ReconnectLoopAsync</c> 同一套逻辑，但不是同一个循环 ——
        /// 16 台各自一条，一台卡住不影响别人（Brief P4.5）。
        /// </para>
        /// <para>
        /// ⚠️ **重连尝试不写日志**（2026-11-05 方案 B，对齐 PLC 的 ADR-14）：
        /// 断线那一刻已有「通讯中断」一行、连上时记「已连接」；原先每次尝试都记
        /// 「正在重连 … Ns」在 16 台并发时会反复刷屏，会把运行日志冲干净。
        /// </para>
        /// </summary>
        private async Task ReconnectScannerLoopAsync(ScannerItem item, CancellationTokenSource cts)
        {
            ScannerSession session = GetOrCreateScannerSession(item.Model);
            CancellationToken token = cts.Token;

            try
            {
                while (!token.IsCancellationRequested && !_windowClosed)
                {
                    // 失败计数由 TryConnectScannerAsync → RegisterConnectFailure 维护，
                    // 故这里第 1 轮等 1s、第 2 轮 2s……与 PLC 的节奏完全一致
                    int delay = ReconnectPolicy.DelayForAttempt(session.Attempt);

                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(true);

                    if (token.IsCancellationRequested || _windowClosed)
                    {
                        return;
                    }

                    // 端点被清掉（用户删了 IP / 端口）：停这条循环，等改回来再由节拍拉起
                    if (!HasUsableScannerEndpoint(item.Model))
                    {
                        return;
                    }

                    if (await TryConnectScannerAsync(item, isReconnect: true, token).ConfigureAwait(true))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消是正常路径（删扫码器 / 关窗）
            }
            finally
            {
                session.EndReconnect(cts);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 扫码器的界面包装：模型做 json 往返（<see cref="ScannerDefinition"/>），
    /// 界面态（选中 / 序号）留在这里，不落盘。
    /// <para>
    /// 与 <c>GlobalVarItem</c> 同一套做法：包装类只做「模型到界面」的投影与通知。
    /// </para>
    /// </summary>
    public sealed class ScannerItem : INotifyPropertyChanged
    {
        /// <summary>连接方式只有一档（Brief 只要求以太网），但样式仍按下拉框呈现。</summary>
        private static readonly string[] ConnectionTypeOptions = { "以太网" };

        private static readonly string[] ProtocolOptions = { "TCP/IP 客户端" };

        /// <summary>
        /// 结果帧结束符的候选文本：**顺序必须与 <see cref="ScannerFrameTerminator"/> 的枚举值一致**，
        /// 因为代码直接拿 <c>SelectedIndex</c> 当枚举值用。
        /// </summary>
        private static readonly string[] TerminatorOptions = BuildTerminatorOptions();

        private int _id;
        private bool _isExpanded;
        private string? _problem;
        private ScannerField _problemField;
        private bool _bufferShared;
        private bool _notifyShared;

        public ScannerItem(ScannerDefinition model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public ScannerDefinition Model { get; }

        /// <summary>扫码器名称（只读投影，供配置页那行显示 —— 改名只在连接页那一处）。</summary>
        public string Name => Model.Name;

        /// <summary>
        /// 名称框的文本（连接页那行，直接在行里改）。
        /// <para>
        /// 与 <see cref="Name"/> 分开：名称框要接受用户乱输入（含重名），
        /// 失焦/回车时才由 <c>CommitScannerName</c> 校验并回写到模型。
        /// </para>
        /// </summary>
        public string NameText => Model.Name;

        /// <summary>启用：关闭只挡触发与收发，连接仍在（Brief P3.5）。</summary>
        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value)
                {
                    return;
                }

                Model.Enabled = value;
                Raise();
                Raise(nameof(EffectiveEnabled));
                Raise(nameof(CanToggleEnabled));
            }
        }

        /// <summary>
        /// 实际是否会去触发：用户意图 × 配置完整（与采集任务组 <c>AcquisitionTaskItem</c> 同一口径）。
        /// <para>
        /// ⚠️ 必须**可写**：开关是 <c>CheckBox</c>，绑只读属性会在模板实例化时抛
        /// 「无法对只读属性进行 TwoWay 绑定」，整页打不开。set 只把用户意图转交 <see cref="Enabled"/>。
        /// </para>
        /// </summary>
        public bool EffectiveEnabled
        {
            get => Model.Enabled && !HasConfigError;
            set => Enabled = value;
        }

        /// <summary>配置有缺项时开关锁死（与采集任务组一致：配置错误不能启用）。</summary>
        public bool CanToggleEnabled => !HasConfigError;

        /// <summary>配置是否有缺项。显示「配置错误」四个字，明细仍在展开块顶部。</summary>
        public bool HasConfigError => HasProblem;

        /// <summary>行上那四个字。取值集中在 <see cref="ScannerRules.ConfigErrorText"/>。</summary>
        public string ConfigErrorText => ScannerRules.ConfigErrorText;

        /// <summary>
        /// 行号（1 起）。⚠️ 必须通知：<c>Reindex</c> 改完不通知画面上的 # 会停在旧值。
        /// </summary>
        public int Id
        {
            get => _id;
            set
            {
                if (_id == value)
                {
                    return;
                }

                _id = value;
                Raise();
            }
        }

        /// <summary>连接页展开态（纯界面态，不落盘）。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                Raise();
                Raise(nameof(ExpandGlyph));
                Raise(nameof(ExpandToolTip));
            }
        }

        /// <summary>展开 / 收起箭头（Segoe 图标：E70E 上箭头 = 收起，E70D 下箭头 = 展开）。</summary>
        public string ExpandGlyph => _isExpanded ? "\uE70E" : "\uE70D";

        public string ExpandToolTip => _isExpanded ? "收起连接与交互参数" : "展开连接与交互参数";

        /// <summary>
        /// 这台扫码器当前「还缺什么」（行上「配置错误」判空 + 日志 / 提示用）。
        /// 改造前它只属于被选中的那一台、显示在右侧共享表单上；现在每行各记一份。
        /// </summary>
        public string? Problem
        {
            get => _problem;
            set
            {
                if (string.Equals(_problem, value, StringComparison.Ordinal))
                {
                    return;
                }

                _problem = value;
                Raise();
                Raise(nameof(HasProblem));
                Raise(nameof(HasConfigError));
                Raise(nameof(EffectiveEnabled));
                Raise(nameof(CanToggleEnabled));
            }
        }

        public bool HasProblem => !string.IsNullOrEmpty(_problem);

        /// <summary>
        /// 错在哪一栏 —— **驱动那个输入框的边框变红**（2026-11-04 定案：展开块顶部不再写红字，
        /// 错在哪个格子哪个格子自己红）。
        /// </summary>
        public ScannerField ProblemField
        {
            get => _problemField;
            set
            {
                if (_problemField == value)
                {
                    return;
                }

                _problemField = value;

                // 九个投影一起刷：漏一个就会出现「改了但红框不亮/不灭」
                Raise(nameof(NameError));
                Raise(nameof(IpError));
                Raise(nameof(PortError));
                Raise(nameof(TriggerExpressionError));
                Raise(nameof(TriggerTextError));
                Raise(nameof(BufferVariableError));
                Raise(nameof(TimeoutSecondsError));
                Raise(nameof(NotifyVariableError));
            }
        }

        public bool NameError => _problemField == ScannerField.Name;

        public bool IpError => _problemField == ScannerField.Ip;

        public bool PortError => _problemField == ScannerField.Port;

        public bool TriggerExpressionError => _problemField == ScannerField.TriggerExpression;

        public bool TriggerTextError => _problemField == ScannerField.TriggerText;

        public bool BufferVariableError => _problemField == ScannerField.BufferVariable;

        public bool TimeoutSecondsError => _problemField == ScannerField.TimeoutSeconds;

        public bool NotifyVariableError => _problemField == ScannerField.NotifyVariable;

        /// <summary>接收缓冲区变量被别处引用（黄字提示，不拦）。</summary>
        public bool BufferShared
        {
            get => _bufferShared;
            set
            {
                if (_bufferShared == value)
                {
                    return;
                }

                _bufferShared = value;
                Raise();
            }
        }

        /// <summary>通知变量被别处引用（黄字提示，不拦）。</summary>
        public bool NotifyShared
        {
            get => _notifyShared;
            set
            {
                if (_notifyShared == value)
                {
                    return;
                }

                _notifyShared = value;
                Raise();
            }
        }

        // ---------- 连接参数（展开块里的控件投影，见 ScannerItemTemplate） ----------

        public IReadOnlyList<string> ConnectionTypes => ConnectionTypeOptions;

        public IReadOnlyList<string> Protocols => ProtocolOptions;

        public IReadOnlyList<string> Terminators => TerminatorOptions;

        /// <summary>
        /// 连接方式 / 协议两个下拉框的选中项。候选项目前各只有一项（可展开、可确认，
        /// 但改不了），所以恒返回 0。必须给选中项：只写 `ItemsSource` 的 ComboBox
        /// 会显示成空框，截图里一看就像「没配」（2026-11-04 实测）。
        /// </summary>
        public int ConnectionTypeIndex => 0;

        public int ProtocolIndex => 0;

        public string IpText => Model.Ip ?? string.Empty;

        public string PortText => Model.Port > 0
            ? Model.Port.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        /// <summary>结束符下拉框的选中项（值即枚举值，见 <see cref="TerminatorOptions"/>）。</summary>
        public int TerminatorIndex
        {
            get
            {
                int index = (int)Model.Terminator;
                return index >= 0 && index < TerminatorOptions.Length ? index : 0;
            }
        }

        // ---------- 交互配置（同上） ----------

        public string TriggerExpressionText => Model.TriggerExpression ?? string.Empty;

        public string TriggerText => Model.TriggerText ?? string.Empty;

        public string BufferVariableText => Model.BufferVariableName ?? string.Empty;

        public string TimeoutSecondsText => Model.TimeoutSeconds.ToString("0.0#", CultureInfo.InvariantCulture);

        public string NotifyVariableText => Model.NotifyVariableName ?? string.Empty;

        /// <summary>
        /// 执行结果输出变量框下那行小字（取值口径 1 / 2）。
        /// 挂在 <see cref="ScannerItem"/> 上而不是写死在 XAML 里，口径就只有一份。
        /// </summary>
        public string ResultValueHint => ScannerRules.ResultValueHint;

        /// <summary>
        /// 模型改过之后把全部投影通知一遍（改参数 / 改名回写 / 变量增删后调）。
        /// 只读投影 + 显式刷新，是「模型无 INPC」时的替代方案，与 <c>GlobalVarItem</c> 同一套。
        /// </summary>
        public void RefreshFields()
        {
            Raise(nameof(NameText));
            Raise(nameof(IpText));
            Raise(nameof(PortText));
            Raise(nameof(TerminatorIndex));
            Raise(nameof(TriggerExpressionText));
            Raise(nameof(TriggerText));
            Raise(nameof(BufferVariableText));
            Raise(nameof(TimeoutSecondsText));
            Raise(nameof(NotifyVariableText));
        }

        public void RaiseNameChanged() => Raise(nameof(Name));

        private static string[] BuildTerminatorOptions()
        {
            int count = (int)ScannerFrameTerminator.None + 1;
            var options = new string[count];

            for (int i = 0; i < count; i++)
            {
                options[i] = ScannerRules.TerminatorDisplay((ScannerFrameTerminator)i);
            }

            return options;
        }

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 扫码器集合：包装 <see cref="ScannerStore"/> 的读写与增删改序。
    /// <para>
    /// 结构照 <c>GlobalVarItemSet</c>，但模型侧没有独立集合类 ——
    /// 落盘顺序就是 <see cref="Items"/> 的顺序，故只需维护一个 ObservableCollection。
    /// </para>
    /// </summary>
    public sealed class ScannerItemSet
    {
        private readonly ObservableCollection<ScannerItem> _items = new();

        public ObservableCollection<ScannerItem> Items => _items;

        /// <summary>读 scanner.json 并重建列表（读失败为空表，不阻断启动）。</summary>
        public void Load()
        {
            _items.Clear();

            foreach (ScannerDefinition model in ScannerStore.Load())
            {
                _items.Add(new ScannerItem(model));
            }

            Reindex();
        }

        public ScannerItem Add(ScannerDefinition model)
        {
            var item = new ScannerItem(model);
            _items.Add(item);
            Reindex();
            Save();
            return item;
        }

        public void Remove(ScannerItem item)
        {
            if (!_items.Remove(item))
            {
                return;
            }

            Reindex();
            Save();
        }

        /// <summary>改名（唯一性由调用方用 <see cref="IsNameTaken"/> 先行校验）。</summary>
        public void Rename(ScannerItem item, string newName)
        {
            if (string.Equals(item.Model.Name, newName, StringComparison.Ordinal))
            {
                return;
            }

            item.Model.Name = newName;
            item.RaiseNameChanged();
            Save();
        }

        /// <summary>名字是否已被**别的**扫码器占用（<paramref name="self"/> 为 null 表示新增）。</summary>
        public bool IsNameTaken(string name, ScannerItem? self) =>
            _items.Any(x => !ReferenceEquals(x, self)
                            && string.Equals(x.Model.Name, name, StringComparison.Ordinal));

        public bool Save() => ScannerStore.Save(_items.Select(x => x.Model));

        private void Reindex()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Id = i + 1;
            }
        }
    }
}
