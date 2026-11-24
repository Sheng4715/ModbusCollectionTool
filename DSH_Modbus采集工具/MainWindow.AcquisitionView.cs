using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Modeling;

// ============================================================
// 总览页「数据采集」视图（P1.4）
//   左边 [实时数据][历史数据]（+ 文件名）  ←→  右边 任务组下拉框 → 分隔线 → 内容
//   实时数据本期只有「规划中」；历史数据真读 CSV，1 秒增量刷新。
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        /// <summary>历史表格最多留多少行（倒序，最新的在最上面）。</summary>
        /// <remarks>
        /// 5s 周期一天就有 1.7 万行，0.1s 周期还要多 50 倍。表格是「看最近发生了什么」，
        /// 不是归档浏览器 —— 全量载入既没人看得完，也会把这个视图拖垮。
        /// </remarks>
        private const int AcqHistoryMaxRows = 500;

        /// <summary>行高，与 <c>AcqHistoryDataGrid</c> 样式里的 <c>RowHeight</c> 必须一致。</summary>
        /// <remarks>
        /// 它不只是外观：往下翻着看的时候来了新行，要靠「插入行数 × 行高」补偿滚动偏移，
        /// 才能让屏幕上的内容**原地不动**。两处对不上就会每次刷新跳一下。
        /// </remarks>
        private const double AcqHistoryRowHeight = 28;

        private static readonly TimeSpan AcqHistoryRefreshInterval = TimeSpan.FromSeconds(1);

        private readonly ObservableCollection<AcqHistoryRow> _acqHistoryRows = new();

        /// <summary>1 秒定时器：只有建过一次才有实例（没进过这个视图就没有）。</summary>
        private DispatcherTimer? _acqHistoryTimer;

        /// <summary>当前表格显示的是哪个任务组（空串 = 还没选）。</summary>
        private string _acqHistoryGroup = string.Empty;

        /// <summary>当前读的是哪个文件（空串 = 还没有文件）。</summary>
        private string _acqHistoryPath = string.Empty;

        /// <summary>已消费到文件的第几个字节（增量解析的游标）。</summary>
        private long _acqHistoryOffset;

        /// <summary>表头行的原文，用来在增量读时识别「这一行其实是表头」。</summary>
        private string _acqHistoryHeaderKey = string.Empty;

        /// <summary>本轮连续失败是否已经提示过（R5：只在首次提示，别每秒刷屏）。</summary>
        private bool _acqHistoryErrorShown;

        /// <summary>防重入：刷新过程中要动控件，被 UI 事件重入会乱。</summary>
        private bool _acqHistoryRefreshing;

        /// <summary>重填任务组下拉框期间屏蔽 SelectionChanged —— 清空再逐项 Add 会连打一串事件。</summary>
        private bool _acqComboRebuilding;

        /// <summary>当前视图 + 子页签是否满足「该刷历史表格」的条件。</summary>
        private bool IsAcqHistoryActive =>
            _overviewPageActive
            && IsAcquisitionViewSelected
            && AcqHistorySegment?.IsChecked == true;

        /// <summary>
        /// 历史表格定时器的启停。
        /// <para>
        /// ⚠️ 三个条件缺一不可：总览页可见、当前视图是「数据采集」、子页签停在「历史数据」。
        /// 少判一个就会出现「人已经切走了，还在每秒读文件」—— 磁盘没感觉，但笔记本上很费电，
        /// 而且会让文件一直被占用着。
        /// </para>
        /// </summary>
        private void SyncAcquisitionViewTimer()
        {
            bool shouldRun = IsAcqHistoryActive && !_windowClosed;

            if (shouldRun)
            {
                _acqHistoryTimer ??= CreateAcqHistoryTimer();

                if (!_acqHistoryTimer.IsEnabled)
                {
                    _acqHistoryTimer.Start();
                }

                // 立刻刷一拍，不让用户盯着空表等 1 秒
                RefreshAcqHistoryTable(force: false);
            }
            else if (_acqHistoryTimer != null && _acqHistoryTimer.IsEnabled)
            {
                _acqHistoryTimer.Stop();
            }
        }

        private DispatcherTimer CreateAcqHistoryTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = AcqHistoryRefreshInterval,
            };

            timer.Tick += (_, _) => RefreshAcqHistoryTable(force: false);
            return timer;
        }

        /// <summary>切到「数据采集」视图时调一次：把任务组下拉框按当前配置重填。</summary>
        private void RefreshAcqOverviewView()
        {
            BindAcqHistoryGridSource();
            RefreshAcqGroupCombo();
        }

        private void BindAcqHistoryGridSource()
        {
            if (AcqHistoryGrid == null)
            {
                return;
            }

            if (!ReferenceEquals(AcqHistoryGrid.ItemsSource, _acqHistoryRows))
            {
                AcqHistoryGrid.ItemsSource = _acqHistoryRows;
            }
        }

        /// <summary>
        /// 「没有可用任务组」时的提示语。
        /// <para>
        /// ⚠️ 三种情况必须分开说（2026-11-11）：本视图只列**启用 + 无配置错误 +
        /// 开了数据记录**的组，若只报一句「没有可用的任务组」，用户会去查启用开关，
        /// 而真实原因可能是「数据记录」没开 —— 那个开关在任务选项对话框里，压根不在这一页。
        /// </para>
        /// </summary>
        private static string DescribeNoUsableGroup(
            int total, ObservableCollection<AcquisitionTaskItem>? items)
        {
            if (total == 0)
            {
                return "尚未添加任务组，请到数据管理页新增";
            }

            // 有没有「能跑但没开数据记录」的组：只有这种情况值得单独提示，
            // 否则（全是停用 / 配置错误）原提示仍然准确。
            for (int i = 0; i < total; i++)
            {
                AcquisitionTaskItem item = items![i];
                if (item.EffectiveEnabled && !item.Model.RecordEnabled)
                {
                    return "没有可用的任务组（需启用、无配置错误，且打开「数据记录启用」）";
                }
            }

            return "没有可用的任务组（需启用且无配置错误）";
        }

        /// <summary>
        /// 按 <c>_acqTaskSet</c> 重填任务组下拉框（2026-10-31 由胶囊条改来）。
        /// <para>
        /// **只列能采的组**（2026-10-31 用户定案）：停用、配置错误、一个数据项都没有的组都不出现。
        /// 理由是这两类组根本不会有数据 —— 列出来点进去只会看到一片占位，
        /// 用户会以为「功能坏了」，而真正的原因是组没开。
        /// </para>
        /// <para>
        /// **没开「数据记录」的组也不列**（2026-11-11 用户要求）：本视图展示的是落盘 CSV，
        /// 不记录的组在这里既没有历史文件、也不会有可看的实时内容 —— 选中它只会得到
        /// 「该任务组还没有保存的数据」这种说了等于没说的空状态。
        /// </para>
        /// <para>
        /// **HTTP数据上报（Report）类型的组一律不列**（2026-11-24）：上报不落 CSV
        /// （报文由 <c>MesReport</c> 拼成平铺 JSON 直接发给 MES 服务端），
        /// 来源变量也不在实时卡片上展示 —— 它在本视图里既没有历史文件、也没有实时内容，
        /// 列出来点进去只会是一片空占位，让用户以为功能坏了。
        /// 注意<b>不能</b>只靠 <c>RecordEnabled</c> 挡：上报类型在对话框里没有
        /// 「数据记录」这一行，那个值可能是真，必须显式按类型排除。
        /// </para>
        /// <para>
        /// ⚠️ 与历史数据页签**共用**这个下拉框，所以历史数据也只能看能采的组 ——
        /// 这是同一处改动的副作用，已确认可接受（停用的组不产新数据，看旧文件的诉求走文件系统）。
        /// </para>
        /// </summary>
        private void RefreshAcqGroupCombo()
        {
            if (AcqOverviewGroupCombo == null)
            {
                return;
            }

            // 先取一次集合：本方法在初始化期就可能被调用，用局部变量 + 空判兜住，
            // 免得后面每次下标访问都要再空判一遍
            ObservableCollection<AcquisitionTaskItem>? items = _acqTaskSet?.Items;

            int total = items?.Count ?? 0;
            string previous = _acqHistoryGroup;

            // 可采集的组：启用且无配置错误（EffectiveEnabled 同时覆盖两者），
            // **且打开了「数据记录」**（2026-11-11 用户要求），
            // **且不是 HTTP数据上报类型**（2026-11-24）。
            // 理由：本视图展示的是**落盘的 CSV**（实时卡片读变量、历史表格读文件），
            // 不记录数据的组在这里既没有历史文件、也不该占一个下拉项 ——
            // 选中它只会得到「该任务组还没有保存的数据」这种说了等于没说的空状态。
            // 上报类型更彻底：它根本不产生 CSV，也没有实时卡片。
            var usable = new List<AcquisitionTaskItem>();
            for (int i = 0; i < total; i++)
            {
                AcquisitionTaskItem candidate = items![i];
                if (candidate.EffectiveEnabled
                    && candidate.Model.RecordEnabled
                    && candidate.Model.Kind != AcquisitionTaskKind.Report)
                {
                    usable.Add(candidate);
                }
            }

            int count = usable.Count;

            // 重填期间必须挡住 SelectionChanged：Items.Clear() 与逐项 Add 都会触发它，
            // 会被当成「用户换了组」，于是在重建的中途就去读文件（读到空表又要再清一次）
            _acqComboRebuilding = true;
            try
            {
                AcqOverviewGroupCombo.Items.Clear();

                for (int i = 0; i < count; i++)
                {
                    AcquisitionTask task = usable[i].Model;
                    string name = task.Name ?? string.Empty;

                    // Tag 存**真名** —— 回查任务组用真名
                    AcqOverviewGroupCombo.Items.Add(new ComboBoxItem
                    {
                        Content = name,
                        Tag = name,
                    });
                }

                bool hasGroup = count > 0;

                // 没得选的时，页签与内容整块收起，只留一句提示
                SetVisibility(AcqOverviewTopBar, hasGroup);
                SetVisibility(AcqOverviewDivider, hasGroup);

                if (!hasGroup)
                {
                    // 提示语要区分三种情况 —— 用户要做的事完全不同（2026-11-11 加了第三种）：
                    //   ① 一个组都没建        → 去数据管理页新增
                    //   ② 建了但启用/配置不对 → 去启用、或修配置
                    //   ③ 能跑但没开数据记录  → 去任务选项里打开「数据记录启用」
                    SetVisibility(AcqNoGroupHint, true);
                    if (AcqNoGroupHint != null)
                    {
                        AcqNoGroupHint.Text = DescribeNoUsableGroup(total, items);
                    }

                    _acqHistoryGroup = string.Empty;
                    ResetAcqHistory();
                    RebuildAcqLiveCanvas();
                    return;
                }

                SetVisibility(AcqNoGroupHint, false);

                // 尽量停在原来那一组；组被删/禁用/改错后就退回第一组
                int pick = 0;
                for (int i = 0; i < count; i++)
                {
                    if (string.Equals(usable[i].Model.Name, previous, StringComparison.Ordinal))
                    {
                        pick = i;
                        break;
                    }
                }

                AcqOverviewGroupCombo.SelectedIndex = pick;

                // ⚠️ 重建期间事件是被挡住的，所以「当前选中组」必须在这里**手工同步**。
                // 漏了这步的后果：下拉框明明显示「心跳」，表格却说「请先选择一个任务组」——
                // 因为 _acqHistoryGroup 还停在空串上（实测踩到过）。
                string picked = usable[pick].Model.Name ?? string.Empty;
                if (!string.Equals(_acqHistoryGroup, picked, StringComparison.Ordinal))
                {
                    // 原组被删/改名 → 落到别的组上，旧表格的行和列都不能留着
                    _acqHistoryGroup = picked;
                    ResetAcqHistory();
                }

                RebuildAcqLiveCanvas();
            }
            finally
            {
                _acqComboRebuilding = false;
            }
        }

        private static void SetVisibility(UIElement? element, bool visible)
        {
            if (element != null)
            {
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 任务组下拉框换选中项：**两个页签都要跟着换**。
        /// <para>
        /// ⚠️ 2026-11-06 修：早先这里只重置历史表 + 重读 CSV，**漏了实时画布** ——
        /// 停在「实时数据」页签时换组，下拉框变了、画布还是上一组的卡片，
        /// 看着就像「切换没生效」。两套内容必须一起换，因为它们是同一个「当前组」的两种呈现。
        /// </para>
        /// </summary>
        private void AcqGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_acqComboRebuilding)
            {
                return;
            }

            string name = (AcqOverviewGroupCombo?.SelectedItem as ComboBoxItem)?.Tag as string
                ?? string.Empty;

            if (name.Length == 0 || string.Equals(_acqHistoryGroup, name, StringComparison.Ordinal))
            {
                return;
            }

            _acqHistoryGroup = name;

            // 历史表：清空后强制整表重载（换组了，不能走「只追加尾部」那条路）
            ResetAcqHistory();
            RefreshAcqHistoryTable(force: true);

            // 实时画布：卡片、背景图、布局全部按新组重建，并立刻刷一次值
            // （不等下一拍定时器，否则那一瞬间显示的是占位符）
            RebuildAcqLiveCanvas();
            RefreshAcqLiveValues();
        }

        /// <summary>二级页签：实时数据 / 历史数据。</summary>
        private void AcqSubView_Checked(object sender, RoutedEventArgs e)
        {
            if (AcqLivePanel == null || AcqHistoryPanel == null)
            {
                // 初始化期间就会触发一次（XAML 里 IsChecked="True"），那时控件还没造全
                return;
            }

            bool history = AcqHistorySegment?.IsChecked == true;

            SetVisibility(AcqLivePanel, !history);
            SetVisibility(AcqHistoryPanel, history);

            // 文件名只跟着「历史数据」走：它说明的是「表里这些行来自哪个文件」，
            // 摆在实时数据页签下会让人以为实时数据也出自该文件
            SyncAcqHistoryFileTextVisibility();

            SyncAcquisitionViewTimer();

            if (history)
            {
                RefreshAcqHistoryTable(force: false);
            }
            else
            {
                // 切回实时数据：卡片是按需重建的，这里补一次（期间数据项可能变过）
                RebuildAcqLiveCanvas();
                RefreshAcqLiveValues();
            }
        }

        /// <summary>文件名文本：仅当停在「历史数据」且确实有文件时才显示。</summary>
        private void SyncAcqHistoryFileTextVisibility()
        {
            if (AcqHistoryFileText == null)
            {
                return;
            }

            bool show = AcqHistorySegment?.IsChecked == true
                && AcqHistoryFileText.Text.Length > 0;

            AcqHistoryFileText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- 历史表格 ----------

        private AcquisitionTask? FindAcqTaskByName(string name)
        {
            if (name.Length == 0 || _acqTaskSet?.Items == null)
            {
                return null;
            }

            foreach (AcquisitionTaskItem item in _acqTaskSet.Items)
            {
                if (string.Equals(item.Model.Name, name, StringComparison.Ordinal))
                {
                    return item.Model;
                }
            }

            return null;
        }

        private void ResetAcqHistory()
        {
            _acqHistoryPath = string.Empty;
            _acqHistoryOffset = 0;
            _acqHistoryHeaderKey = string.Empty;
            _acqHistoryRows.Clear();

            if (AcqHistoryGrid != null)
            {
                AcqHistoryGrid.Columns.Clear();
            }

            if (AcqHistoryFileText != null)
            {
                AcqHistoryFileText.Text = string.Empty;
                SyncAcqHistoryFileTextVisibility();
            }
        }

        /// <summary>
        /// 刷一拍历史表格。
        /// <para>
        /// 三个层次：没选组 / 没有文件 → 空状态；文件换了（跨天、表头变）→ 整表重载；
        /// 同一个文件 → 只解析新增的字节。**先比长度**，长度没变就连读都不读。
        /// </para>
        /// </summary>
        private void RefreshAcqHistoryTable(bool force)
        {
            if (_windowClosed || _acqHistoryRefreshing || AcqHistoryGrid == null)
            {
                return;
            }

            AcquisitionTask? task = FindAcqTaskByName(_acqHistoryGroup);
            if (task == null)
            {
                ResetAcqHistory();
                ShowAcqHistoryHint("请先选择一个任务组");
                return;
            }

            string? path = AcquisitionCsv.FindLatestRecordFile(task, DateTime.Now);
            if (string.IsNullOrEmpty(path))
            {
                ResetAcqHistory();
                ShowAcqHistoryHint("该任务组还没有保存的数据");
                return;
            }

            _acqHistoryRefreshing = true;
            try
            {
                bool switched = !string.Equals(path, _acqHistoryPath, StringComparison.Ordinal);
                if (force || switched)
                {
                    LoadAcqHistoryFile(path!);
                }
                else
                {
                    AppendAcqHistoryTail(path!);
                }
            }
            catch (Exception ex)
            {
                // R5：同一轮连续失败只提示一次，恢复后由成功路径清掉
                if (!_acqHistoryErrorShown)
                {
                    _acqHistoryErrorShown = true;
                    ShowAcqHistoryHint("读取失败：" + ex.Message, error: true);
                }
            }
            finally
            {
                _acqHistoryRefreshing = false;
            }
        }

        /// <summary>整表重载：读完整文件、按首行表头建列、取最后 500 行倒序。</summary>
        private void LoadAcqHistoryFile(string path)
        {
            string text = ReadAcqHistoryComplete(path, 0, out long consumed);

            if (text.Length == 0)
            {
                ResetAcqHistory();
                _acqHistoryPath = path;
                _acqHistoryOffset = consumed;
                ShowAcqHistoryHint("该任务组还没有保存的数据");
                SetAcqHistoryFileName(path);
                return;
            }

            List<string> lines = SplitAcqHistoryLines(text);
            if (lines.Count == 0)
            {
                ResetAcqHistory();
                _acqHistoryPath = path;
                ShowAcqHistoryHint("该任务组还没有保存的数据");
                SetAcqHistoryFileName(path);
                return;
            }

            _acqHistoryHeaderKey = lines[0];
            BuildAcqHistoryColumns(AcquisitionCsv.ParseLine(lines[0]));

            _acqHistoryRows.Clear();

            // 倒序：文件里最后一行是最新的，放进列表最上面
            int from = Math.Max(1, lines.Count - AcqHistoryMaxRows);
            for (int i = lines.Count - 1; i >= from; i--)
            {
                _acqHistoryRows.Add(new AcqHistoryRow(AcquisitionCsv.ParseLine(lines[i])));
            }

            _acqHistoryPath = path;
            _acqHistoryOffset = consumed;
            _acqHistoryErrorShown = false;

            SetAcqHistoryFileName(path);

            if (_acqHistoryRows.Count == 0)
            {
                ShowAcqHistoryHint("该任务组还没有保存的数据");
            }
            else
            {
                HideAcqHistoryHint();
            }
        }

        /// <summary>增量：只读 <c>_acqHistoryOffset</c> 之后的新字节，新行插到顶部。</summary>
        private void AppendAcqHistoryTail(string path)
        {
            // 文件被换掉 / 被截短（例如用户手动删了重建）→ 已经对不上了，整表重来
            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch
            {
                return;
            }

            if (length < _acqHistoryOffset)
            {
                LoadAcqHistoryFile(path);
                return;
            }

            if (length == _acqHistoryOffset)
            {
                return; // 没有新数据，连读都不用读
            }

            string text = ReadAcqHistoryComplete(path, _acqHistoryOffset, out long consumed);
            if (text.Length == 0 || consumed == _acqHistoryOffset)
            {
                return; // 只多出半行（写入过程中），等下一拍
            }

            List<string> lines = SplitAcqHistoryLines(text);

            // 极端情形：这一批里混进了表头原文（新文件刚好在两次读之间建好）
            if (lines.Count > 0 && string.Equals(lines[0], _acqHistoryHeaderKey, StringComparison.Ordinal))
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0)
            {
                // 插入前先记住「视口顶部那一条」，插完再把它滚回顶部 ——
                // 比算术补偿可靠：DataGrid 内部是按条滚动的，偏移单位不是像素。
                ScrollViewer? viewer = FindAcqHistoryScrollViewer();
                bool atTop = viewer == null || viewer.VerticalOffset <= 0.5;
                object? anchor = GetAcqHistoryAnchorItem(viewer);

                for (int i = 0; i < lines.Count; i++)
                {
                    _acqHistoryRows.Insert(0, new AcqHistoryRow(AcquisitionCsv.ParseLine(lines[i])));
                }

                TrimAcqHistoryRows();
                HideAcqHistoryHint();

                // 用户已经往下翻了：把原来顶部那条滚回顶部，让屏幕上的内容原地不动
                if (!atTop)
                {
                    RestoreAcqHistoryAnchor(viewer, anchor, lines.Count);
                }
            }

            _acqHistoryOffset = consumed;
            _acqHistoryErrorShown = false;
        }

        private void TrimAcqHistoryRows()
        {
            while (_acqHistoryRows.Count > AcqHistoryMaxRows)
            {
                _acqHistoryRows.RemoveAt(_acqHistoryRows.Count - 1);
            }
        }

        private void BuildAcqHistoryColumns(List<string> header)
        {
            if (AcqHistoryGrid == null)
            {
                return;
            }

            AcqHistoryGrid.Columns.Clear();

            for (int i = 0; i < header.Count; i++)
            {
                var column = new DataGridTextColumn
                {
                    Header = header[i],
                    Binding = new Binding("Fields[" + i.ToString(CultureInfo.InvariantCulture) + "]"),
                    Width = DataGridLength.Auto,
                    MinWidth = 80,
                };

                AcqHistoryGrid.Columns.Add(column);
            }
        }

        /// <summary>
        /// 从 <paramref name="offset"/> 起读到文件末尾，只返回**完整的行**（以 <c>\n</c> 结尾）。
        /// <para>
        /// ⚠️ 末尾那半行**不能吃**：采集侧正在往文件里写，读到一半很正常，
        /// 把半行解析了既会多出一行垃圾，也会让游标停在行中间 —— 下一拍接着读就全错位了。
        /// </para>
        /// </summary>
        private static string ReadAcqHistoryComplete(string path, long offset, out long consumedTotal)
        {
            consumedTotal = offset;

            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            long length = fs.Length;
            if (length <= offset)
            {
                return string.Empty;
            }

            int count = (int)Math.Min(length - offset, int.MaxValue);
            byte[] buffer = new byte[count];

            fs.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read == 0)
            {
                return string.Empty;
            }

            int start = 0;

            // UTF-8 BOM 只在文件开头出现。写盘侧带 BOM（Excel 双击中文不乱码），
            // 不跳过的话首列名会多出三个不可见字节，表头就成了「\uFEFF时间」。
            if (offset == 0 && read >= 3
                && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                start = 3;
            }

            int lastNewLine = -1;
            for (int i = read - 1; i >= start; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    lastNewLine = i;
                    break;
                }
            }

            if (lastNewLine < 0)
            {
                return string.Empty;
            }

            consumedTotal = offset + lastNewLine + 1;
            return Encoding.UTF8.GetString(buffer, start, lastNewLine + 1 - start);
        }

        /// <summary>
        /// 把 CSV 文本切成记录。
        /// <para>
        /// ⚠️ **必须走 <see cref="AcquisitionCsv.SplitRecords"/>，不能用 <c>text.Split('\n')</c>**：
        /// STRING 字段里若有换行，写侧按 RFC-4180 加引号但保留真实换行，
        /// 朴素按 <c>'\n'</c> 切会把一条记录劈成两半、整列错位（见该方法的注释）。
        /// </para>
        /// </summary>
        private static List<string> SplitAcqHistoryLines(string text) =>
            AcquisitionCsv.SplitRecords(text);

        private void SetAcqHistoryFileName(string path)
        {
            if (AcqHistoryFileText != null)
            {
                // 显示**完整路径**（2026-11-03 用户要求改回全路径，早前只显示文件名）：
                // 同一任务组的历史数据按日期分文件、还可能落在自定义保存目录里，
                // 光看文件名分不清「哪个目录下的哪一天」。
                // 路径太长时由 TextTrimming 出省略号，不会把布局撑开。
                AcqHistoryFileText.Text = path;
            }

            // 文本变了要顺带重算可见性：切到「历史数据」时文件可能还没解析出来
            SyncAcqHistoryFileTextVisibility();
        }

        private void ShowAcqHistoryHint(string message, bool error = false)
        {
            if (AcqHistoryHint == null)
            {
                return;
            }

            AcqHistoryHint.Text = message;
            AcqHistoryHint.SetResourceReference(
                TextBlock.ForegroundProperty, error ? "Brush.Danger" : "Brush.Text.Muted");
            AcqHistoryHint.Visibility = Visibility.Visible;

            // 空/出错时把表格收起：没有列的时候它仍会画一条表头下边框，
            // 悬在提示文字上方像一条多余的横线
            SetVisibility(AcqHistoryGrid, false);
        }

        private void HideAcqHistoryHint()
        {
            if (AcqHistoryHint != null)
            {
                AcqHistoryHint.Visibility = Visibility.Collapsed;
            }

            SetVisibility(AcqHistoryGrid, true);
        }

        // ---------- 滚动位置 ----------

        /// <summary>拿到 DataGrid 内部那个 ScrollViewer（模板里的 <c>DG_ScrollViewer</c>）。</summary>
        private ScrollViewer? FindAcqHistoryScrollViewer()
        {
            if (AcqHistoryGrid == null)
            {
                return null;
            }

            AcqHistoryGrid.ApplyTemplate();
            return AcqHistoryGrid.Template?.FindName("DG_ScrollViewer", AcqHistoryGrid) as ScrollViewer;
        }

        /// <summary>
        /// 视口顶部那一行。启用虚拟化时内部 ScrollViewer 是**按条滚动**（
        /// <c>CanContentScroll=True</c>），<c>VerticalOffset</c> 就是行索引，可以直接取；
        /// 否则退回 null，由 <see cref="RestoreAcqHistoryAnchor"/> 按像素估算。
        /// </summary>
        private object? GetAcqHistoryAnchorItem(ScrollViewer? viewer)
        {
            if (viewer == null || !viewer.CanContentScroll)
            {
                return null;
            }

            int index = (int)Math.Floor(viewer.VerticalOffset);
            return index >= 0 && index < _acqHistoryRows.Count
                ? _acqHistoryRows[index]
                : null;
        }

        /// <summary>
        /// 把插入前位于顶部的 <paramref name="anchor"/> 滚回顶部。
        /// <para>
        /// ⚠️ 这里**不能**用「偏移 + 插入行数 × 行高」：按条滚动时偏移单位是行，
        /// 乘行高会让视图直接甩出去几十行（实测往下跳了约 57 行）。
        /// </para>
        /// </summary>
        private void RestoreAcqHistoryAnchor(ScrollViewer? viewer, object? anchor, int insertedRows)
        {
            if (viewer == null)
            {
                return;
            }

            if (viewer.CanContentScroll)
            {
                if (anchor is AcqHistoryRow row)
                {
                    int index = _acqHistoryRows.IndexOf(row);
                    if (index >= 0)
                    {
                        viewer.ScrollToVerticalOffset(index);
                        return;
                    }
                }

                // 锚点被 500 行上限裁掉了（翻到了很下面又大量进数据）→ 退回按行数平移
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + insertedRows);
                return;
            }

            // 像素滚动（理论上不会走到，留个正确退路）
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + insertedRows * AcqHistoryRowHeight);
        }
    }

    /// <summary>
    /// 历史表格的一行：<see cref="Fields"/> 就是 CSV 那一行的字段。
    /// <para>
    /// 列是运行时按文件表头建的，所以这里用**下标绑定**（<c>Fields[i]</c>）而不是具名属性 ——
    /// 数据项叫什么、有几个，只有打开文件才知道。
    /// </para>
    /// </summary>
    public sealed class AcqHistoryRow
    {
        public AcqHistoryRow(IReadOnlyList<string> fields)
        {
            Fields = fields;
        }

        /// <summary>去掉了 <c>System.Windows.Controls.DataGrid</c> 的需要，纯数据。</summary>
        public IReadOnlyList<string> Fields { get; }
    }
}
