using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DSH_Modbus采集工具.Monitoring;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>报文显示格式：HEX / TEXT。HTTP 目标强制 TEXT（见 P2.4）。</summary>
    internal enum TrafficFormat
    {
        Hex = 0,
        Text = 1,
    }

    /// <summary>
    /// 一行报文（供 ListBox 绑定）。文本在构造时按当前格式生成，切换格式时整表重建。
    /// </summary>
    public sealed class TrafficRow
    {
        public TrafficRow(TrafficFrame frame, string text, TextAlignment textAlignment)
        {
            Frame = frame;
            Text = text;
            TextAlignment = textAlignment;
        }

        public TrafficFrame Frame { get; }

        public string Text { get; }

        public TextAlignment TextAlignment { get; }
    }

    /// <summary>
    /// 设备监控页第五分段「通讯监控」的呈现与刷新（P2.4）。
    /// <para>
    /// 分工：抓包与环形缓冲在 <see cref="Monitoring.TrafficTrace"/>（不引 WPF，可独立断言）；
    /// 本类只负责「何时抓（可见性 + 选中目标）、每 200ms 取一次快照、生成行、跟随滚动、切格式」。
    /// </para>
    /// <para>
    /// ⚠️ 本类所有方法都必须在 UI 线程调用（DispatcherTimer 满足这点）；抓包线程只碰
    /// <see cref="TrafficBuffer"/>，不碰控件。
    /// </para>
    /// </summary>
    public sealed class TrafficMonitorPresenter
    {
        /// <summary>UI 刷新节拍：1000 条缓冲 + 100ms 轮询下，200ms 既跟得上又不至于每帧刷一次。</summary>
        public const int RefreshIntervalMs = 200;

        private readonly TrafficBuffer _buffer = new();
        private readonly ObservableCollection<TrafficRow> _rows = new();
        private readonly DispatcherTimer _timer;

        private ListBox? _list;
        private ComboBox? _targetCombo;
        private FrameworkElement? _formatPanel;
        private RadioButton? _hexButton;
        private RadioButton? _textButton;
        private Func<IReadOnlyList<TrafficTarget>>? _targetProvider;

        /// <summary>上一次装进下拉框的目标列表，用来判断「要不要重建」。</summary>
        private IReadOnlyList<TrafficTarget> _shownTargets = Array.Empty<TrafficTarget>();

        /// <summary>当前正在抓的目标身份（与 <see cref="TrafficTarget.Key"/> 引用相等比较）。</summary>
        private object? _activeKey;

        private bool _segmentVisible;
        private bool _pageActive;
        private bool _rebuilding;
        private bool _followTail = true;
        private TrafficFormat _format = TrafficFormat.Hex;
        private int _generation;
        private long _lastSequence;

        public TrafficMonitorPresenter()
        {
            TrafficTap.Attach(_buffer);
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs),
            };
            _timer.Tick += (_, _) => Refresh();
        }

        /// <summary>
        /// 绑定控件。控件都由窗口侧传入，本类不 <c>FindName</c>，便于窗口按页面拆分 partial。
        /// </summary>
        public void Attach(
            ListBox list,
            ComboBox targetCombo,
            FrameworkElement formatPanel,
            RadioButton hexButton,
            RadioButton textButton,
            Func<IReadOnlyList<TrafficTarget>> targetProvider)
        {
            _list = list;
            _targetCombo = targetCombo;
            _formatPanel = formatPanel;
            _hexButton = hexButton;
            _textButton = textButton;
            _targetProvider = targetProvider;

            _list.ItemsSource = _rows;

            // 跟随规则与运行日志同一套：只认用户滚动（追加内容引起的 ExtentHeightChange 不算）
            _list.AddHandler(
                ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnListScrollChanged));

            ApplyFormatControls(activeKind: null);
        }

        /// <summary>本分段是否显示（由 <c>IoView_Checked</c> 调）。</summary>
        public void SetSegmentVisible(bool visible)
        {
            if (_segmentVisible == visible)
            {
                return;
            }

            _segmentVisible = visible;
            ApplyVisibility();
        }

        /// <summary>设备监控页是否可见（由 <c>Nav_Checked</c> 调；离开页面时本分段也不可见）。</summary>
        public void SetPageActive(bool active)
        {
            if (_pageActive == active)
            {
                return;
            }

            _pageActive = active;
            ApplyVisibility();
        }

        /// <summary>下拉框选中项变化（重建期由本类自己挡住，不在这里判）。</summary>
        public void OnTargetSelectionChanged()
        {
            if (_rebuilding)
            {
                return;
            }

            SelectTarget(_targetCombo?.SelectedItem as TrafficTarget);
        }

        /// <summary>HEX / TEXT 页签切换：整表按新格式重建（最多 1000 行，用户动作即可接受）。</summary>
        public void OnFormatChanged()
        {
            TrafficFormat format = _hexButton?.IsChecked == true ? TrafficFormat.Hex : TrafficFormat.Text;
            if (format == _format)
            {
                return;
            }

            _format = format;
            RebuildRows();
        }

        /// <summary>清空：只清缓冲与列表，不清目标选择（继续抓同一个目标）。</summary>
        public void Clear()
        {
            _buffer.Clear();
            ResetRowsFromBuffer();
            _followTail = true;
        }

        private bool EffectiveVisible => _segmentVisible && _pageActive;

        private void ApplyVisibility()
        {
            if (EffectiveVisible)
            {
                // 每次进入本分段都从空开始（P2.4：看不到切走之前的报文，是从头记的）
                _activeKey = null;
                TrafficTap.Deactivate();
                _buffer.Clear();
                ResetRowsFromBuffer();
                _followTail = true;
                RefreshTargets();

                // ⚠️ 重新进入时目标列表可能**没有变化**，RefreshTargets 会直接返回，
                // 而 ApplyVisibility 刚把 _activeKey 置空 —— 不补这一次就会「下拉框选中了
                // 一个目标、但抓包开关没打开」，页面上永远没有新报文。
                if (_targetCombo?.SelectedItem is TrafficTarget current)
                {
                    SelectTarget(current);
                }
                else if (_targetCombo != null && _targetCombo.Items.Count > 0)
                {
                    _targetCombo.SelectedIndex = 0; // 触发 SelectionChanged → SelectTarget → 激活
                }

                _timer.Start();
            }
            else
            {
                _timer.Stop();
                TrafficTap.Deactivate();
                _activeKey = null;
                _buffer.Clear();
                ResetRowsFromBuffer();
            }
        }

        private void Refresh()
        {
            if (!EffectiveVisible)
            {
                return;
            }

            RefreshTargets();
            PullFrames();
        }

        /// <summary>
        /// 重建「监控目标」下拉框。**每拍都问一次提供方**，这样连接建立 / 断开 / 重连、
        /// 扫码器增删改名、MES URL 变更都会在 200ms 内自己反映出来，不必在窗口里散落 8 个调用点。
        /// </summary>
        private void RefreshTargets()
        {
            if (_targetCombo == null || _targetProvider == null)
            {
                return;
            }

            IReadOnlyList<TrafficTarget> desired = _targetProvider();
            if (SameTargets(desired, _shownTargets))
            {
                return;
            }

            object? selectedKey = (_targetCombo.SelectedItem as TrafficTarget)?.Key;
            _shownTargets = desired;

            int index = -1;
            if (selectedKey != null)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    if (ReferenceEquals(desired[i].Key, selectedKey))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0 && desired.Count > 0)
            {
                index = 0;
            }

            // 重建期挡 SelectionChanged：ItemsSource 重置会先触发「选中变 null」那一次
            _rebuilding = true;
            try
            {
                _targetCombo.ItemsSource = desired;
                _targetCombo.SelectedIndex = index;
            }
            finally
            {
                _rebuilding = false;
            }

            SelectTarget(index >= 0 ? desired[index] : null);
        }

        private void SelectTarget(TrafficTarget? target)
        {
            object? key = target?.Key;
            if (ReferenceEquals(key, _activeKey))
            {
                ApplyFormatControls(target?.Kind);
                return;
            }

            // 换目标 = 重新开始（只抓当前选中的那一个）
            _activeKey = key;
            _buffer.Clear();
            ResetRowsFromBuffer();
            _followTail = true;

            if (key == null)
            {
                TrafficTap.Deactivate();
            }
            else
            {
                TrafficTap.Activate(key);
            }

            ApplyFormatControls(target?.Kind);
        }

        /// <summary>HTTP 是文本协议：藏掉 HEX/TEXT 页签并强制 TEXT；其它目标恢复页签。</summary>
        private void ApplyFormatControls(TrafficTargetKind? activeKind)
        {
            if (_formatPanel == null)
            {
                return;
            }

            if (activeKind == TrafficTargetKind.Http)
            {
                _formatPanel.Visibility = Visibility.Collapsed;
                _format = TrafficFormat.Text;
                return;
            }

            _formatPanel.Visibility = Visibility.Visible;
            _format = _hexButton?.IsChecked == true ? TrafficFormat.Hex : TrafficFormat.Text;
        }

        private void PullFrames()
        {
            TrafficSnapshot snap = _buffer.Snapshot();

            if (_generation != snap.Generation)
            {
                _rows.Clear();
                _generation = snap.Generation;
                _lastSequence = snap.FirstSequence - 1;
            }
            else
            {
                // 缓冲满 1000 条后丢最老的：UI 上也同步把最老的行移除
                while (_rows.Count > 0 && _rows[0].Frame.Sequence < snap.FirstSequence)
                {
                    _rows.RemoveAt(0);
                }
            }

            if (snap.LastSequence <= _lastSequence)
            {
                return;
            }

            foreach (TrafficFrame frame in snap.Frames)
            {
                if (frame.Sequence > _lastSequence)
                {
                    _rows.Add(BuildRow(frame));
                }
            }

            _lastSequence = snap.LastSequence;

            if (_followTail)
            {
                ScrollToLatest();
            }
        }

        private void RebuildRows()
        {
            TrafficSnapshot snap = _buffer.Snapshot();
            _rows.Clear();
            _generation = snap.Generation;
            _lastSequence = snap.LastSequence;

            foreach (TrafficFrame frame in snap.Frames)
            {
                _rows.Add(BuildRow(frame));
            }

            if (_followTail)
            {
                ScrollToLatest();
            }
        }

        private void ResetRowsFromBuffer()
        {
            TrafficSnapshot snap = _buffer.Snapshot();
            _rows.Clear();
            _generation = snap.Generation;
            _lastSequence = snap.LastSequence;
        }

        private TrafficRow BuildRow(TrafficFrame frame)
        {
            string body = _format == TrafficFormat.Hex
                ? FormatHex(frame.Payload)
                : FormatText(frame);

            string time = "[" + frame.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "]";
            string text = time + "\n" + body;

            TextAlignment align = frame.Direction == TrafficDirection.Send
                ? TextAlignment.Right
                : TextAlignment.Left;

            return new TrafficRow(frame, text, align);
        }

        private static string FormatHex(byte[] payload)
        {
            if (payload.Length == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(payload.Length * 3);
            for (int i = 0; i < payload.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(payload[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static string FormatText(TrafficFrame frame)
        {
            string body = Encoding.UTF8.GetString(frame.Payload);
            if (frame.Caption == null)
            {
                return body;
            }

            return body.Length == 0 ? frame.Caption : frame.Caption + "\n" + body;
        }

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_list == null || !_list.IsVisible)
            {
                return;
            }

            // 追加内容会触发 ScrollChanged，但那是我们在长高、不是用户在翻；只认后者
            if (Math.Abs(e.ExtentHeightChange) > 0.5)
            {
                return;
            }

            _followTail = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1.0;
        }

        private void ScrollToLatest()
        {
            if (_list == null || _rows.Count == 0)
            {
                return;
            }

            _list.ScrollIntoView(_rows[^1]);
        }

        private static bool SameTargets(
            IReadOnlyList<TrafficTarget> left, IReadOnlyList<TrafficTarget> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!ReferenceEquals(left[i].Key, right[i].Key)
                    || left[i].Kind != right[i].Kind
                    || !string.Equals(left[i].DisplayName, right[i].DisplayName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
