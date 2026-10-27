using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using DSH_Modbus采集工具.Logging;
using DSH_Modbus采集工具.Themes;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>设备状态日志的级别，决定日志行的颜色（沿用状态栏既有配色）。</summary>
    public enum DeviceLogLevel
    {
        /// <summary>中性灰：如用户主动断开。</summary>
        Neutral,

        /// <summary>绿色：连接成功、心跳恢复。</summary>
        Connected,

        /// <summary>红色：连接失败、通讯异常断开。</summary>
        Disconnected,

        /// <summary>琥珀色：告警，如心跳响应异常。</summary>
        Warning,
    }

    /// <summary>设备状态日志的一行，供总览页底部的 ListBox 绑定。</summary>
    public sealed class LogEntry
    {
        private readonly DateTime _time;

        public LogEntry(DateTime time, string message, DeviceLogLevel level)
        {
            _time = time;
            TimeText = $"[{time:yyyy-MM-dd HH:mm:ss}]";
            Message = message;
            Level = level;
            LevelBrush = MakeLevelBrush(level);
        }

        /// <summary>形如 "[2026-10-08 10:23:45]"，固定灰色显示。</summary>
        public string TimeText { get; }

        public string Message { get; }

        /// <summary>级别（换主题时按它重算颜色）。</summary>
        public DeviceLogLevel Level { get; }

        public Brush LevelBrush { get; }

        /// <summary>
        /// 按当前主题取该级别的画刷。走 <see cref="ThemeManager.GetBrush"/>，
        /// 取不到时回退到浅色字面值，保证任何时候都有颜色。
        /// </summary>
        private static Brush MakeLevelBrush(DeviceLogLevel level)
        {
            string key = level switch
            {
                DeviceLogLevel.Connected => "Brush.Success",
                DeviceLogLevel.Disconnected => "Brush.Danger",
                DeviceLogLevel.Warning => "Brush.Warning",
                _ => "Brush.Text.Secondary",
            };

            return ThemeManager.GetBrush(key) ?? Fallback(level);
        }

        /// <summary>换主题后重建一行（颜色随主题变，其它字段不变）。</summary>
        public LogEntry WithThemeColors() => new(_time, Message, Level);

        /// <summary>主题资源不可用时的兜底色（= 浅色主题的对应值）。</summary>
        private static Brush Fallback(DeviceLogLevel level)
        {
            Color color = level switch
            {
                DeviceLogLevel.Connected => Color.FromRgb(0x0B, 0x7A, 0x0B),
                DeviceLogLevel.Disconnected => Color.FromRgb(0xC4, 0x2B, 0x1C),
                DeviceLogLevel.Warning => Color.FromRgb(0xB2, 0x6A, 0x00),
                _ => Color.FromRgb(0x5D, 0x5D, 0x5D),
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// 设备状态日志的编排：内存集合（上限裁剪）、落盘旁路、界面级别到落盘级别的映射。
    /// <para>
    /// 集中在这里的三条规则：**界面与文件共用同一个时间戳**、**超出上限丢弃最旧一条**、
    /// **级别按颜色语义映射到 INFO/WARN/ERROR**。这三条以前散在窗口的
    /// <c>AppendDeviceLog</c> / <c>FileLevelOf</c> / <c>RefreshDeviceLogColors</c> 里。
    /// </para>
    /// <para>
    /// 刻意**不碰任何控件**：滚动到最新一行（<c>ScrollIntoView</c>）与设置页失败提示的刷新
    /// 留在窗口 —— 那是界面驱动的东西，与本类的规则无关。
    /// </para>
    /// <para>
    /// 落盘写入器由调用方传入而不是本类持有：它的生命周期（设置页改目录 / 开关、关窗释放）
    /// 由窗口掌握，本类只负责"写入时用哪个时间戳与哪个级别"。
    /// </para>
    /// </summary>
    public sealed class DeviceLogPresenter
    {
        /// <summary>
        /// 默认内存条数上限（超出后丢弃最旧一条，仅内存、不落盘）。
        /// <para>
        /// 从 200 提到 1000（用户定案，2026-10-26）：采集任务每执行一次就记一条日志，
        /// 5s 周期下 200 条只够 16 分钟，真出事时「连接失败」那行早被挤掉了。
        /// 1000 条在 5s 周期下约 83 分钟、1s 周期下约 16 分钟。
        /// </para>
        /// <para>
        /// ⚠️ 只影响**界面**，不影响落盘：日志文件按天写，不受这个上限约束。
        /// </para>
        /// </summary>
        public const int DefaultMaxEntries = 1000;

        private readonly int _maxEntries;

        public DeviceLogPresenter(int maxEntries = DefaultMaxEntries)
        {
            _maxEntries = maxEntries;
        }

        /// <summary>供总览页 ListBox 绑定的日志行（只增删、不整体替换）。</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new();

        /// <summary>
        /// 追加一条日志：入内存（含上限裁剪）并落盘，返回新行供调用方滚动到它。
        /// <para>
        /// ⚠️ 时间戳在这里**只取一次**，界面与文件共用 —— 分成两次取时，跨秒时写下的时间
        /// 会与界面差一秒，事后对照两边对不上（P5-2 的实测教训）。
        /// </para>
        /// </summary>
        public LogEntry Append(string message, DeviceLogLevel level, DeviceLogFileWriter? writer)
        {
            var now = DateTime.Now;

            var entry = new LogEntry(now, message, level);
            Entries.Add(entry);
            while (Entries.Count > _maxEntries)
            {
                Entries.RemoveAt(0);
            }

            writer?.Write(now, ToFileLevel(level), message);
            return entry;
        }

        /// <summary>清空界面日志（不影响已落盘的文件）。</summary>
        public void Clear() => Entries.Clear();

        /// <summary>
        /// 按新主题重建各行的颜色。日志行色在 <see cref="LogEntry"/> 构造时定死，
        /// 换主题后必须整表重建 —— 用「原样替换」而不是 Clear + Add，避免滚动位置跳动。
        /// </summary>
        public void RefreshThemeColors()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entries[i] = Entries[i].WithThemeColors();
            }
        }

        /// <summary>
        /// 界面级别 → 落盘级别的映射（P5-2，用户定稿按**颜色语义**分档）：
        /// 绿（已连接 / 通讯恢复）= INFO，琥珀（通讯异常）= WARN，红（已断开 / 连接失败）= ERROR。
        /// <c>Neutral</c> 是默认灰，按 INFO 处理。
        /// </summary>
        public static LogFileLevel ToFileLevel(DeviceLogLevel level) => level switch
        {
            DeviceLogLevel.Warning => LogFileLevel.WARN,
            DeviceLogLevel.Disconnected => LogFileLevel.ERROR,
            _ => LogFileLevel.INFO,
        };
    }
}
