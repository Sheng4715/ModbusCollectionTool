using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>串口参数（P4.4）。默认值取 Modbus 行业通行的 <b>9600 8N1</b>。</summary>
    public sealed record SerialSettings(
        string PortName,
        int BaudRate = 9600,
        int DataBits = 8,
        Parity Parity = Parity.None,
        StopBits StopBits = StopBits.One)
    {
        /// <summary>底栏与日志里显示的一行摘要，如 <c>COM3 9600 8N1</c>。</summary>
        public string Describe()
        {
            string parity = Parity switch
            {
                Parity.None => "N",
                Parity.Odd => "O",
                Parity.Even => "E",
                Parity.Mark => "M",
                Parity.Space => "S",
                _ => "?",
            };

            string stop = StopBits switch
            {
                StopBits.One => "1",
                StopBits.OnePointFive => "1.5",
                StopBits.Two => "2",
                _ => "?",
            };

            return $"{PortName} {BaudRate} {DataBits}{parity}{stop}";
        }
    }

    /// <summary>
    /// Modbus RTU 主站（P4.4）：串口传输 + CRC16 + 按帧长切分。
    /// <para>
    /// **与 TCP 的分工**：PDU 的构造与解析全部复用 <see cref="ModbusPdu"/>，
    /// 本类只负责 RTU 特有的三件事 —— 装订（站号 + PDU + CRC）、
    /// 收帧（**没有长度字段**，只能靠功能码推算）、以及串口本身。
    /// </para>
    /// <para>
    /// ⚠️ **RTU 没有 MBAP 的长度字段**，所以收帧必须按「已经读到的功能码」推算本帧还差多少字节。
    /// 这是 RTU 实现最容易出错的地方：功能码决定后续结构，猜错就整条流失去同步。
    /// 故 <see cref="ReadFrame"/> 做成 <c>static</c>，只吃「串口 + 请求功能码 + 期望站号 + 截止时刻」，
    /// 切帧逻辑可脱离连接状态推敲。
    /// </para>
    /// </summary>
    public sealed class ModbusRtuClient : IModbusClient
    {
        /// <summary>
        /// 单次请求超时的**下限**。实际上限按波特率推算（见 <see cref="TimeoutFor"/>）：
        /// 1200 baud 读 125 个寄存器时响应有 255 字节、光上线就要 2.1 s，
        /// 固定 2 s 会「每次必超时」，而现象与「设备没响应」一模一样，极难归因。
        /// </summary>
        private static readonly TimeSpan MinRequestTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 失败后到下一条请求之间的**静默保护**下限（见 <see cref="ResyncGuard"/>）。
        /// RTU 没有事务标识：一条请求超时后它的应答可能迟到，若紧接着发下一条，
        /// 那条迟到帧会被当成新请求的应答 —— **CRC 完全正确、静默错值**。
        /// </summary>
        private static readonly TimeSpan MinResyncGuard = TimeSpan.FromMilliseconds(20);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _portLock = new();

        /// <summary>
        /// 串口实例。<b>跨线程</b>：UI 线程建连/释放，采集线程读写。
        /// 与 TCP 那侧同理用 <c>volatile</c>，避免后台长时间读到 null 或已释放的实例。
        /// </summary>
        private volatile SerialPort? _port;

        private long _lastResponseTimestamp = Stopwatch.GetTimestamp();

        /// <summary>
        /// 下次允许发送的最早时刻（<see cref="Stopwatch"/> 刻度）。请求**失败**后置为
        /// 「现在 + 静默保护」，期间到达的迟到帧会在发送前被清掉。只在 <see cref="_gate"/> 内读写。
        /// </summary>
        private long _quietUntilTimestamp;

        /// <summary>从站地址（1~247）。RTU 用它区分总线上是谁在应答。</summary>
        public byte UnitId { get; set; } = 1;

        /// <summary>本实例使用的串口参数（重连时复用这份快照）。</summary>
        public SerialSettings Settings { get; }

        public ModbusRtuClient(SerialSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool IsConnected
        {
            get
            {
                SerialPort? port = _port;
                return port != null && port.IsOpen;
            }
        }

        /// <summary>
        /// 打开串口。参数非法 / 端口被占用 / 端口不存在都会抛异常，由上层提示。
        /// <para>
        /// 超时策略：<c>ReadTimeout</c> / <c>WriteTimeout</c> 这里只给**下限**，
        /// 每次请求会按波特率与字节数重新算（见 <see cref="TimeoutFor"/>）——
        /// 低波特率下 255 字节的响应光上线就超过 2 s，用固定值必然误判超时。
        /// </para>
        /// </summary>
        public void Connect()
        {
            var port = new SerialPort(Settings.PortName, Settings.BaudRate, Settings.Parity,
                Settings.DataBits, Settings.StopBits)
            {
                ReadTimeout = (int)MinRequestTimeout.TotalMilliseconds,
                WriteTimeout = (int)MinRequestTimeout.TotalMilliseconds,
            };

            try
            {
                port.Open();

                // 打开时清掉可能残留的输入字节（上一次连接 / 总线噪声），
                // 否则第一条请求会先读到一段垃圾、直接判定帧校验失败
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                SerialPort? previous;
                lock (_portLock)
                {
                    previous = _port;
                    _port = port;
                }

                previous?.Dispose();
                Interlocked.Exchange(ref _lastResponseTimestamp, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _quietUntilTimestamp, 0);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }

        public Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadBitsAsync(0x01, startAddress, count, ct);

        public Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadBitsAsync(0x02, startAddress, count, ct);

        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadRegistersAsync(0x03, startAddress, count, ct);

        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadRegistersAsync(0x04, startAddress, count, ct);

        public Task WriteSingleCoilAsync(ushort address, bool on, CancellationToken ct = default)
        {
            ushort raw = on ? (ushort)0xFF00 : (ushort)0x0000;
            return WriteAsync(ModbusPdu.BuildWriteSingle(0x05, address, raw), 0x05, ct);
        }

        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
            => WriteAsync(ModbusPdu.BuildWriteSingle(0x06, address, value), 0x06, ct);

        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default)
            => WriteAsync(ModbusPdu.BuildWriteMultiple(startAddress, values), 0x10, ct, isMultiple: true);

        public async Task<bool> HeartbeatIfIdleAsync(ushort address, TimeSpan idleInterval, CancellationToken ct)
        {
            byte[] response = await RequestAsync(ModbusPdu.BuildRead(0x01, address, 1), ct, idleInterval)
                .ConfigureAwait(false);
            return response.Length != 0;
        }

        private async Task<bool[]> ReadBitsAsync(byte functionCode, ushort start, ushort count, CancellationToken ct)
        {
            byte[] resp = await RequestAsync(ModbusPdu.BuildRead(functionCode, start, count), ct).ConfigureAwait(false);
            return ModbusPdu.ParseBits(resp, count);
        }

        private async Task<ushort[]> ReadRegistersAsync(byte functionCode, ushort start, ushort count, CancellationToken ct)
        {
            byte[] resp = await RequestAsync(ModbusPdu.BuildRead(functionCode, start, count), ct).ConfigureAwait(false);
            return ModbusPdu.ParseRegisters(resp, count);
        }

        private async Task WriteAsync(byte[] pdu, byte functionCode, CancellationToken ct, bool isMultiple = false)
        {
            byte[] resp = await RequestAsync(pdu, ct, isWrite: true).ConfigureAwait(false);
            ModbusPdu.ValidateWriteResponse(resp, pdu, isMultiple);
        }

        /// <summary>
        /// 一次完整的 RTU 往返：装订 → 写 → 收帧 → 校验 CRC → 剥离异常响应。
        /// 返回响应 **PDU**（不含站号与 CRC），与 TCP 侧同一口径。
        /// <para>
        /// 并发：全程持有 <see cref="_gate"/> —— RTU 是单主站总线，采集 / 心跳 / 写入共用一条串口。
        /// </para>
        /// <para>
        /// ⚠️ 超时是**一个请求一个截止时刻**，不是「每次 Read 各 2 秒」（见 <see cref="ReadExact"/>）；
        /// 发送前**必清接收缓冲**，失败后还要等一段静默（见 <see cref="MarkFailure"/>）——
        /// 这两条共同防的是「迟到帧被当成下一条请求的应答」这个静默错值（§8.3.2c）。
        /// </para>
        /// </summary>
        private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken ct, TimeSpan? idleInterval = null, bool isWrite = false)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                SerialPort port = _port ?? throw new IOException("串口未打开");
                if (!port.IsOpen)
                {
                    throw new IOException("串口未打开");
                }

                if (idleInterval.HasValue
                    && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastResponseTimestamp)) < idleInterval.Value)
                {
                    return Array.Empty<byte>();
                }

                // 装订：站号 + PDU + CRC（低字节在前，见 Crc16 注释）
                byte[] body = new byte[pdu.Length + 1];
                body[0] = UnitId;
                Buffer.BlockCopy(pdu, 0, body, 1, pdu.Length);
                byte[] frame = Crc16.Append(body);

                TimeSpan requestTimeout = TimeoutFor(frame.Length, ExpectedResponseBytes(pdu, isWrite));

                // ① 失败后的静默保护：等够再发。晚到的应答若被当成这一条的应答，
                //    CRC 仍会通过（它本来就是合法帧），只是数据属于上一条 —— 静默错值。
                await WaitForQuietAsync(ct).ConfigureAwait(false);

                // ② 发送前清一次接收缓冲。⚠️ 必须在静默等待**之后**做：
                //    等待期间到达的字节正是要丢掉的迟到帧。只在失败后清（早期实现）
                //    挡不住「清完之后、下一条请求窗口内」到达的那一帧（2026-11-20 实测复现）。
                port.DiscardInBuffer();
                port.WriteTimeout = Math.Max(1, (int)requestTimeout.TotalMilliseconds);

                long deadline = Stopwatch.GetTimestamp()
                                + (long)(requestTimeout.TotalSeconds * Stopwatch.Frequency);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(requestTimeout);

                // SerialPort 的同步 API 在超时前会阻塞线程；放到线程池，避免卡住调用方
                await Task.Run(() =>
                {
                    port.Write(frame, 0, frame.Length);
                    port.BaseStream.Flush();
                }, timeout.Token).ConfigureAwait(false);

                byte[] response = await Task.Run(
                    () => ReadFrame(port, pdu[0], UnitId, isWrite, deadline, timeout.Token), timeout.Token)
                    .ConfigureAwait(false);

                Interlocked.Exchange(ref _lastResponseTimestamp, Stopwatch.GetTimestamp());

                // 响应帧 = 站号 + PDU + CRC(2)；校验通过后剥掉站号与 CRC，交回 PDU
                byte[] respPdu = new byte[response.Length - 3];
                Buffer.BlockCopy(response, 1, respPdu, 0, response.Length - 3);

                if (!isWrite)
                {
                    int count = (pdu[3] << 8) | pdu[4];
                    if (!ModbusPdu.IsException(respPdu, pdu[0], out _))
                    {
                        ModbusPdu.ValidateReadByteCount(pdu[0], count, respPdu[1]);
                    }
                }

                if (ModbusPdu.IsException(respPdu, pdu[0], out byte code))
                {
                    throw ModbusPdu.CreateException(code);
                }

                return respPdu;
            }
            catch (ModbusResponseException)
            {
                // 合法异常响应：帧完整、CRC 已过，串口仍可复用，也不需要静默保护
                throw;
            }
            catch (OperationCanceledException)
            {
                // 超时 / 取消：清缓冲 + 记静默截止时刻
                MarkFailure();
                throw;
            }
            catch (TimeoutException)
            {
                MarkFailure();
                throw;
            }
            catch
            {
                // 其余（帧校验失败、串口 IO 错误）同样清缓冲 + 静默后再上抛
                MarkFailure();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 按「波特率 × 收发字节数」推算本次请求的超时，下限 <see cref="MinRequestTimeout"/>。
        /// <para>
        /// ⚠️ 为什么必须按波特率算：1200 baud、读 125 个寄存器时响应有 255 字节，
        /// 光上线就要 2.1 s，固定 2 s 会「每次必超时」；而那个现象与「设备根本没接」
        /// 一模一样（都是 timeout），现场只会觉得驱动不支持这个设备。
        /// </para>
        /// </summary>
        private TimeSpan TimeoutFor(int requestBytes, int responseBytes)
        {
            int baud = Settings.BaudRate > 0 ? Settings.BaudRate : 9600;
            double wireMs = (requestBytes + responseBytes) * BitsPerByte() * 1000.0 / baud;

            // 另留 500 ms 给从站的内部处理时间（写操作要「先执行再回显」）
            double ms = Math.Max(MinRequestTimeout.TotalMilliseconds, wireMs + 500);
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>按请求推算响应**整帧**字节数（含站号与 CRC），只用于估算超时。</summary>
        private static int ExpectedResponseBytes(byte[] pdu, bool isWrite)
        {
            if (isWrite)
            {
                // 回显：站号 + 功能码 + 地址(2) + 值/数量(2) + CRC(2)
                return 8;
            }

            int count = (pdu[3] << 8) | pdu[4];
            return 3 + ModbusPdu.ExpectedReadBytes(pdu[0], count) + 2;
        }

        /// <summary>一个字节在线上占多少位：起始位 + 数据位 + 校验位 + 停止位。</summary>
        private double BitsPerByte()
        {
            // ⚠️ 必须走 Settings.xxx：类里 `using System.IO.Ports` 之后，
            // 裸写 StopBits / Parity 会解析成**类型名**而不是属性（CS0119）。
            double stopBits = Settings.StopBits switch
            {
                System.IO.Ports.StopBits.OnePointFive => 1.5,
                System.IO.Ports.StopBits.Two => 2,
                _ => 1,
            };

            int parityBits = Settings.Parity == System.IO.Ports.Parity.None ? 0 : 1;
            return 1 + Settings.DataBits + parityBits + stopBits;
        }

        /// <summary>失败后的静默保护：3.5 个字符时间（RTU 规定的帧间隔），不低于 <see cref="MinResyncGuard"/>。</summary>
        private TimeSpan ResyncGuard()
        {
            int baud = Settings.BaudRate > 0 ? Settings.BaudRate : 9600;
            double ms = 3.5 * BitsPerByte() * 1000.0 / baud;
            return TimeSpan.FromMilliseconds(Math.Max(MinResyncGuard.TotalMilliseconds, ms));
        }

        /// <summary>等到「允许发送」的时刻；没有静默要求时立即返回。</summary>
        private async Task WaitForQuietAsync(CancellationToken ct)
        {
            long until = Interlocked.Read(ref _quietUntilTimestamp);
            if (until == 0)
            {
                return;
            }

            long remain = until - Stopwatch.GetTimestamp();
            if (remain <= 0)
            {
                return;
            }

            var wait = TimeSpan.FromMilliseconds(remain * 1000.0 / Stopwatch.Frequency);
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        /// <summary>登记一次失败：丢弃已到达的残帧，并把下一条请求推到静默期之后。</summary>
        private void MarkFailure()
        {
            FlushInputQuietly();

            long until = Stopwatch.GetTimestamp()
                         + (long)(ResyncGuard().TotalSeconds * Stopwatch.Frequency);
            Interlocked.Exchange(ref _quietUntilTimestamp, until);
        }

        /// <summary>
        /// 按功能码推算并读出一整帧 RTU 响应，校验站号与 CRC 后返回**完整帧**（含 CRC）。
        /// <para>
        /// ⚠️ 先读 2 字节（站号 + 功能码）再推算剩余长度 —— RTU 没有长度字段，
        /// 而且异常响应（5 字节）、写应答（8 字节回显）、读应答（变长）三者结构完全不同，
        /// 必须先看到功能码才知道该读多少。
        /// </para>
        /// </summary>
        /// <param name="isWrite">
        /// ⚠️ **写请求必须传 true**。RTU 的写应答是「站号 + 功能码 + 地址 + 值」的**原样回显**，
        /// 恒 8 字节、**没有「字节数」字段**。若按读应答的规则去解析，会把地址高字节当成
        /// 「数据字节数」—— 实测汇川 Easy：写 M0（地址 0x0000）时按 0 字节读，只凑到 5 字节
        /// 就校验 CRC → `IOException`；写 Y7（地址 0xFC07，高字节 0xFC=252）时去等 252+2 字节
        /// → `TimeoutException`。**写入其实已经成功**，只是应答被误解析（2026-11-18 真机实测）。
        /// </param>
        /// <param name="deadline">
        /// 整条请求的收帧截止时刻（<see cref="Stopwatch"/> 刻度）。
        /// ⚠️ 传截止时刻而不是「每次 Read 都用 ReadTimeout」：后者在慢速对端上会累加
        /// （读头 2 字节 2 s + 读数据 2 s …），实测一个请求能拖到 3 s 以上。
        /// </param>
        private static byte[] ReadFrame(
            SerialPort port, byte requestFunctionCode, byte expectedUnit, bool isWrite,
            long deadline, CancellationToken ct)
        {
            // 站号 + 功能码
            byte[] head = ReadExact(port, 2, deadline, ct);
            byte unit = head[0];
            byte function = head[1];

            bool isException = (function & 0x80) != 0;
            if (isException && function != (requestFunctionCode | 0x80))
            {
                throw new IOException("Modbus 响应功能码与请求不匹配");
            }

            // 异常响应：站号 + 功能码 + 异常码 + CRC(2) = 5 字节，已读 2，还差 3
            if (isException)
            {
                byte[] rest = ReadExact(port, 3, deadline, ct);
                byte[] exceptionFrame = { unit, function, rest[0], rest[1], rest[2] };
                ValidateFrame(exceptionFrame, unit, expectedUnit);
                return exceptionFrame;
            }

            if (function != requestFunctionCode)
            {
                throw new IOException("Modbus 响应功能码与请求不匹配");
            }

            // ⚠️ 写应答：站号 + 功能码 + 地址(2) + 值/数量(2) + CRC(2) = 8 字节，已读 2，还差 6。
            // 与读应答的关键差别是**没有「字节数」字段**，故不能走下面的变长分支（见参数说明）。
            if (isWrite)
            {
                byte[] echoTail = ReadExact(port, 6, deadline, ct);
                byte[] echoFrame = new byte[8];
                echoFrame[0] = unit;
                echoFrame[1] = function;
                Buffer.BlockCopy(echoTail, 0, echoFrame, 2, 6);
                ValidateFrame(echoFrame, unit, expectedUnit);
                return echoFrame;
            }

            // 读应答：第 3 字节是「后续数据字节数」
            byte[] countByte = ReadExact(port, 1, deadline, ct);
            int dataBytes = countByte[0];

            // 还有 countByte(已读) 之外的 dataBytes 个数据 + CRC(2)
            byte[] tail = ReadExact(port, dataBytes + 2, deadline, ct);

            byte[] full = new byte[3 + dataBytes + 2];
            full[0] = unit;
            full[1] = function;
            full[2] = countByte[0];
            Buffer.BlockCopy(tail, 0, full, 3, dataBytes + 2);

            ValidateFrame(full, unit, expectedUnit);
            return full;
        }

        /// <summary>
        /// 校验 CRC 与站号。两项都不通过时给不同文案 —— 现场排查方向完全不同。
        /// <para>
        /// ⚠️ **先查 CRC 再查站号**（2026-11-20 调整）：帧被干扰时站号字节本身就可能被打坏，
        /// 先比站号会把「CRC 错」误报成「站号不符」，把现场引到改站号的歧路。
        /// 反过来：站号配错时从站回的帧 CRC 是**好的**，所以这时才该报站号不符。
        /// </para>
        /// <para>
        /// 刻意做成静态 + 显式传期望站号：本方法与 <see cref="ReadFrame"/> 是 RTU 里
        /// 最该独立断言的两段（切帧长度、站号、CRC 顺序），做成实例方法就只能靠真串口才能测。
        /// </para>
        /// </summary>
        private static void ValidateFrame(byte[] frame, byte unit, byte expectedUnit)
        {
            if (!Crc16.Validate(frame))
            {
                throw new IOException("Modbus 响应 CRC 校验失败");
            }

            if (unit != expectedUnit)
            {
                throw new IOException($"Modbus 响应站号不符：期望 {expectedUnit}，实际 {unit}");
            }
        }

        /// <summary>
        /// 精确读 <paramref name="count"/> 字节。串口可能分多次返回，故必须循环。
        /// <para>
        /// ⚠️ 每次 <c>Read</c> 的 <c>ReadTimeout</c> 按**剩余时间**设置，不是固定 2 s：
        /// 一个请求最多分 3 段读（头 2 字节 / 字节数 / 数据+CRC），固定值会让总耗时累加到 3–6 s，
        /// 期间串口被整条占住，写入与心跳都只能排队（2026-11-20 实测：慢速对端 3001 ms）。
        /// </para>
        /// </summary>
        private static byte[] ReadExact(SerialPort port, int count, long deadline, CancellationToken ct)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                ct.ThrowIfCancellationRequested();

                long remain = deadline - Stopwatch.GetTimestamp();
                if (remain <= 0)
                {
                    // 文案里带「超时」，与 SerialPort 自己抛的那句同归「响应超时」
                    throw new TimeoutException("串口响应超时");
                }

                int remainMs = (int)Math.Ceiling(remain * 1000.0 / Stopwatch.Frequency);
                port.ReadTimeout = Math.Max(1, remainMs);

                int read = port.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new IOException("串口读取返回 0 字节");
                }

                offset += read;
            }

            return buffer;
        }

        private void FlushInputQuietly()
        {
            try
            {
                _port?.DiscardInBuffer();
            }
            catch
            {
                // 串口已关闭或被拔掉：此处不再抛，真正的原因已由上层那次异常携带
            }
        }

        public void Dispose()
        {
            SerialPort? port;
            lock (_portLock)
            {
                port = _port;
                _port = null;
            }

            try
            {
                if (port != null)
                {
                    if (port.IsOpen)
                    {
                        port.Close();
                    }

                    port.Dispose();
                }
            }
            catch
            {
                // 释放途中的 IO 异常不该冒泡到断线逻辑上
            }

            // 不释放 _gate：可能仍有在途请求，SemaphoreSlim 无句柄资源可泄漏
        }
    }
}
