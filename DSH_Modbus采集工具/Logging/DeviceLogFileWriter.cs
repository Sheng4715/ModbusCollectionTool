using System.IO;
using System.Text;

namespace DSH_Modbus采集工具.Logging
{
    /// <summary>落盘用的日志级别（与界面颜色语义一一对应）。</summary>
    public enum LogFileLevel
    {
        INFO,
        WARN,
        ERROR,
    }

    /// <summary>
    /// 把设备状态日志按天追加到纯文本文件（P5-2）。
    ///
    /// <para>
    /// **目录与文件名**：默认写到 exe 旁的 <c>Log\</c>，文件名是日期
    /// （<c>2026-10-15.log</c>）。一天一个文件，跨天自动换 —— 判断依据是**每次写入时
    /// 现取的本地日期**，不是进程启动时间，故应用常驻跨零点也能正确切分。
    /// </para>
    /// <para>
    /// **为什么持有打开的文件流而不是每次 <c>File.AppendAllText</c>**：AppendAllText 每次
    /// 都要 open/seek/close 三次系统调用。日志行是低频的（只在连接状态变化时写一条），
    /// 性能并非主因，真正的原因是**保持句柄可以避免与外部工具的抢占** —— 但代价是
    /// 需要自己处理跨天换文件。这里选择保留句柄 + 跨天重开。
    /// </para>
    /// <para>
    /// **为什么不抛异常**：日志是旁路功能。磁盘满、目录只读、文件被占都会失败，
    /// 但一个连不上设备的上位机**不应该因为写不了日志而崩**。故所有 IO 异常都在此
    /// 吞掉，仅把失败原因留在 <see cref="LastError"/> 供设置页展示。
    /// </para>
    /// </summary>
    public sealed class DeviceLogFileWriter : IDisposable
    {
        private readonly object _gate = new();

        private string _directory;
        private bool _enabled;
        private StreamWriter? _writer;
        private DateOnly _openDate;

        /// <summary>最近一次写入失败的原因；成功时为 <c>null</c>。供设置页显示。</summary>
        public string? LastError { get; private set; }

        /// <summary>本次进程内已成功写出的行数（用于设置页回显，不做持久化）。</summary>
        public int LinesWritten { get; private set; }

        public DeviceLogFileWriter(string directory, bool enabled)
        {
            _directory = directory;
            _enabled = enabled;
        }

        /// <summary>
        /// 存放日志的目录（可随时改，改后下次写入即生效）。
        /// 刻意**不叫** <c>Directory</c>：那会遮蔽 <see cref="System.IO.Directory"/>，
        /// 使本类内部所有 <c>Directory.CreateDirectory</c> 解析到实例属性上。
        /// </summary>
        public string DirectoryPath => _directory;

        /// <summary>日志保存是否启用（可随时改；关闭时释放已打开的句柄）。</summary>
        public bool Enabled => _enabled;

        /// <summary>改保存目录：换目录后旧句柄必须释放，否则会继续写进旧文件夹。</summary>
        public void SetDirectory(string directory)
        {
            lock (_gate)
            {
                if (string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _directory = directory;
                CloseWriter();
                LastError = null;
            }
        }

        /// <summary>开关日志保存：关闭时立即释放句柄（便于用户移动/删除文件夹）。</summary>
        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                _enabled = enabled;
                if (!enabled)
                {
                    CloseWriter();
                    LastError = null;
                }
            }
        }

        /// <summary>默认目录：exe 旁的 <c>Log</c> 文件夹。</summary>
        public static string DefaultDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Log");

        /// <summary>某个日期的日志文件完整路径。</summary>
        public static string FilePathFor(string directory, DateOnly date) =>
            Path.Combine(directory, date.ToString("yyyy-MM-dd") + ".log");

        /// <summary>当前正在写的文件（未写过任何行时为 <c>null</c>）。</summary>
        public string? CurrentFilePath => _writer == null ? null : FilePathFor(_directory, _openDate);

        /// <summary>
        /// 追加一行。时间戳由调用方给出（与界面显示的同一条日志共用同一时刻，
        /// 不做二次取时，避免界面与文件相差一秒而对不上）。
        /// </summary>
        public void Write(DateTime timestamp, LogFileLevel level, string message)
        {
            if (!_enabled)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    EnsureWriter(DateOnly.FromDateTime(timestamp));

                    // [2026-10-15 11:16:54] [INFO] 已连接：192.168.1.66:502
                    _writer!.Write('[');
                    _writer.Write(timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                    _writer.Write("] [");
                    _writer.Write(level.ToString());
                    _writer.Write("] ");
                    _writer.WriteLine(message);

                    // 立刻 Flush：日志的价值在崩溃后复盘，缓冲着没落盘等于没写
                    _writer.Flush();

                    LinesWritten++;
                    LastError = null;
                }
                catch (Exception ex)
                {
                    // 记住原因并放弃句柄；下一次写入会尝试重新打开
                    LastError = ex.Message;
                    CloseWriter();
                }
            }
        }

        /// <summary>打开（或跨天切换）到指定日期的文件。</summary>
        private void EnsureWriter(DateOnly date)
        {
            if (_writer != null && _openDate == date)
            {
                return;
            }

            CloseWriter();

            Directory.CreateDirectory(_directory);

            // Append + UTF8(无 BOM)：跨进程/跨次启动续写同一文件；BOM 会让
            // 每行 grep 的结果首行带一个不可见字符，纯文本日志不必要
            //
            // FileShare.ReadWrite（而非只给 Read）：日志是给人看的，用户很可能在应用
            // 运行期间用记事本/VS Code/tail 打开当前文件。只共享 Read 时这些工具
            // 会因为"文件被占用"而打不开或只读，而这恰恰是排障时最自然的动作。
            var stream = new FileStream(
                FilePathFor(_directory, date),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
            _openDate = date;
        }

        private void CloseWriter()
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 关闭失败无所谓，句柄随进程退出释放
            }

            _writer = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                CloseWriter();
            }
        }
    }
}
