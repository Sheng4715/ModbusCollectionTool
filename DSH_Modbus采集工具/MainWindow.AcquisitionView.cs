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
//   任务组胶囊条 → 分隔线 → [实时数据][历史数据] → 内容
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

        /// <summary>重建胶囊条期间屏蔽 Checked —— 清空再新建会连打一串事件。</summary>
        private bool _acqChipRebuilding;

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

        /// <summary>切到「数据采集」视图时调一次：把任务组胶囊条按当前配置重画。</summary>
        private void RefreshAcqOverviewView()
        {
            BindAcqHistoryGridSource();
            RefreshAcqGroupChips();
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
        /// 按 <c>_acqTaskSet</c> 重建任务组胶囊条。
        /// <para>
        /// 列**全部**任务组（读取 + 写入都列），禁用的也照列、名称后加「（停用）」——
        /// 藏起来会让「我在数据管理页明明建了这一组」变成悬案。
        /// </para>
        /// </summary>
        private void RefreshAcqGroupChips()
        {
            if (AcqOverviewGroupChips == null)
            {
                return;
            }

            // 先取一次集合：本方法在初始化期就可能被调用，用局部变量 + 空判兜住，
            // 免得后面每次下标访问都要再空判一遍
            ObservableCollection<AcquisitionTaskItem>? items = _acqTaskSet?.Items;

            int count = items?.Count ?? 0;
            string previous = _acqHistoryGroup;

            _acqChipRebuilding = true;
            try
            {
                AcqOverviewGroupChips.Children.Clear();

                for (int i = 0; i < count; i++)
                {
                    AcquisitionTask task = items![i].Model;
                    string name = task.Name ?? string.Empty;

                    var chip = new RadioButton
                    {
                        Content = task.Enabled ? name : name + "（停用）",
                        GroupName = "AcqOverviewGroup",
                        Tag = name,
                        // 贴在灰底轨道里、彼此不留缝 —— 与设备监控那排分段同一个观感
                    };

                    chip.SetResourceReference(StyleProperty, "AcqGroupChipRadioButton");
                    chip.Checked += AcqGroupChip_Checked;
                    AcqOverviewGroupChips.Children.Add(chip);
                }
            }
            finally
            {
                _acqChipRebuilding = false;
            }

            bool hasGroup = count > 0;

            // 没有任务组时，二级分段与表格整块收起，只留一句提示
            SetVisibility(AcqOverviewChipScroller, hasGroup);
            SetVisibility(AcqOverviewDivider, hasGroup);
            SetVisibility(AcqOverviewSubBar, hasGroup);
            SetVisibility(AcqNoGroupHint, !hasGroup);

            if (!hasGroup)
            {
                _acqHistoryGroup = string.Empty;
                ResetAcqHistory();
                return;
            }

            // 尽量停在原来那一组；组被删了就退回第一组
            int pick = 0;
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(items![i].Model.Name, previous, StringComparison.Ordinal))
                {
                    pick = i;
                    break;
                }
            }

            if (AcqOverviewGroupChips.Children[pick] is RadioButton target)
            {
                target.IsChecked = true;
            }
        }

        private static void SetVisibility(UIElement? element, bool visible)
        {
            if (element != null)
            {
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AcqGroupChip_Checked(object sender, RoutedEventArgs e)
        {
            if (_acqChipRebuilding || sender is not RadioButton chip)
            {
                return;
            }

            string name = chip.Tag as string ?? string.Empty;
            if (string.Equals(_acqHistoryGroup, name, StringComparison.Ordinal))
            {
                return;
            }

            _acqHistoryGroup = name;
            ResetAcqHistory();
            RefreshAcqHistoryTable(force: true);
        }

        /// <summary>二级页签：实时数据 / 历史数据。</summary>
        private void AcqSubView_Checked(object sender, RoutedEventArgs e)
        {
            if (AcqLiveHint == null || AcqHistoryPanel == null)
            {
                // 初始化期间就会触发一次（XAML 里 IsChecked="True"），那时控件还没造全
                return;
            }

            bool history = AcqHistorySegment?.IsChecked == true;

            SetVisibility(AcqLiveHint, !history);
            SetVisibility(AcqHistoryPanel, history);

            SyncAcquisitionViewTimer();

            if (history)
            {
                RefreshAcqHistoryTable(force: false);
            }
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

        private static List<string> SplitAcqHistoryLines(string text) =>
            text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToList();

        private void SetAcqHistoryFileName(string path)
        {
            if (AcqHistoryFileText != null)
            {
                AcqHistoryFileText.Text = Path.GetFileName(path);
            }
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
