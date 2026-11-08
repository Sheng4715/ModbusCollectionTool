using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Modbus采集工具.Drivers
{
    public sealed class ModbusResponseException : IOException
    {
        public byte ExceptionCode { get; }

        public ModbusResponseException(byte exceptionCode, string message) : base(message)
        {
            ExceptionCode = exceptionCode;
        }
    }

    /// <summary>
    /// Modbus TCP 主站：MBAP 报文封装 + 功能码 0x01/0x02/0x03/0x04（读）
    /// 与 0x05/0x06/0x10（写）。
    /// 单连接、请求串行（信号量），单次请求 2 秒超时。
    /// </summary>
    public sealed class ModbusTcpClient : IDisposable
    {
        /// <summary>单次 FC16 最多写的寄存器数（协议上限 123）。</summary>
        public const int MaxRegistersPerWrite = 123;

        /// <summary>
        /// 单次 FC03 / FC04 最多读的寄存器数（协议上限 125）。
        /// <para>
        /// ⚠️ **寄存器区的分块上限必须用它，不能用线圈那些 2000**：2000 是 FC01/FC02
        /// 的线圈上限，寄存器侧超 125 从站会回异常码 3 甚至直接断链。
        /// 分块在 <c>GlobalReadPlan</c> / <c>MonitorItem.BuildReadPlan</c> 里做，
        /// 本类只把常量集中在这里供它们引用。
        /// </para>
        /// </summary>
        public const int MaxRegistersPerRead = 125;

        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// 当前 TCP 连接。<b>跨线程读写</b>：UI 线程在 <see cref="ConnectAsync"/> / <see cref="Dispose"/>
        /// 里赋值，轮询线程在 <see cref="RequestAsync"/> 里读、<see cref="IsConnected"/> 也可能被任意线程读。
        /// 故必须 <c>volatile</c> —— 与同类的 <see cref="_lastResponseTimestamp"/> 用
        /// <see cref="Interlocked"/> 保持一致的可见性口径。漏了不会立刻出错（引用读写本身原子），
        /// 但后台可能长时间读到 null（停在「连接未建立」）或读到已 Dispose 的实例。
        /// </summary>
        private volatile TcpClient? _tcp;

        private ushort _tid;
        private long _lastResponseTimestamp = Stopwatch.GetTimestamp();

        /// <summary>Modbus 从站单元号，默认 1</summary>
        public byte UnitId { get; set; } = 1;

        public bool IsConnected => _tcp?.Connected == true;

        /// <summary>
        /// 建立 TCP 连接；超时抛 OperationCanceledException。
        /// localEndPoint 非空时先绑定本地源地址（强制从指定网卡发出，应对多网卡/子网冲突场景）；
        /// 绑定失败（网卡已拔出等）同样抛异常，由上层提示。
        /// </summary>
        public async Task ConnectAsync(string ip, int port, TimeSpan timeout, IPEndPoint? localEndPoint = null, CancellationToken ct = default)
        {
            var tcp = new TcpClient();
            try
            {
                if (localEndPoint != null)
                {
                    tcp.Client.Bind(localEndPoint);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);

                // ⚠️ 覆盖 `_tcp` 前先释放旧连接：本类不强制「只连一次」，
                // 若调用方复用同一实例重连（或上一轮 ConnectAsync 已成功但上层没 Dispose），
                // 直接赋值会把旧 TcpClient 的 socket 句柄**静默泄漏**，且旧流可能仍在后台读。
                TcpClient? previous = _tcp;
                _tcp = tcp;
                previous?.Dispose();

                Interlocked.Exchange(ref _lastResponseTimestamp, Stopwatch.GetTimestamp());
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>0x01 读线圈状态</summary>
        public Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadBitsAsync(0x01, startAddress, count, ct);

        /// <summary>0x02 读离散量输入</summary>
        public Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadBitsAsync(0x02, startAddress, count, ct);

        /// <summary>0x03 读保持寄存器</summary>
        public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadRegistersAsync(0x03, startAddress, count, ct);

        /// <summary>0x04 读输入寄存器</summary>
        public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
            => ReadRegistersAsync(0x04, startAddress, count, ct);

        /// <summary>0x05 写单个线圈。响应为回显请求（地址 + 值），成功无返回值。</summary>
        public Task WriteSingleCoilAsync(ushort address, bool on, CancellationToken ct = default)
        {
            // 线圈值在报文里是 0xFF00（合）/ 0x0000（分），不是 0/1
            ushort raw = on ? (ushort)0xFF00 : (ushort)0x0000;
            return WriteAsync(BuildWriteSinglePdu(0x05, address, raw), 0x05, ct);
        }

        /// <summary>0x06 写单个保持寄存器。响应为回显请求（地址 + 值），成功无返回值。</summary>
        public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
            => WriteAsync(BuildWriteSinglePdu(0x06, address, value), 0x06, ct);

        /// <summary>
        /// 0x10 写多个保持寄存器。响应只含「起始地址 + 寄存器数」（无数据），
        /// 故成功校验与单写不同，见 <see cref="WriteAsync"/>。
        /// </summary>
        public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default)
        {
            if (values == null || values.Length == 0)
            {
                throw new ArgumentException("写入内容不能为空", nameof(values));
            }

            if (values.Length > MaxRegistersPerWrite)
            {
                throw new ArgumentException(
                    $"单次最多写 {MaxRegistersPerWrite} 个寄存器，实际 {values.Length} 个", nameof(values));
            }

            int count = values.Length;
            byte[] pdu = new byte[6 + count * 2];
            pdu[0] = 0x10;
            pdu[1] = (byte)(startAddress >> 8);
            pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(count >> 8);
            pdu[4] = (byte)count;
            pdu[5] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
            {
                pdu[6 + i * 2] = (byte)(values[i] >> 8);
                pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
            }

            return WriteAsync(pdu, 0x10, ct, isMultiple: true);
        }

        /// <summary>
        /// 在请求锁内检查空闲时间，只有缺少近期有效响应时才读取一个心跳线圈。
        /// 正常采集和合法异常响应都能证明对端仍在回应；返回 false 表示省略了心跳。
        /// </summary>
        public async Task<bool> HeartbeatIfIdleAsync(ushort address, TimeSpan idleInterval, CancellationToken ct)
        {
            byte[] response = await RequestAsync(BuildReadPdu(0x01, address, 1), ct, idleInterval)
                .ConfigureAwait(false);
            return response.Length != 0;
        }

        // ========== 内部实现 ==========

        private async Task<bool[]> ReadBitsAsync(byte functionCode, ushort start, ushort count, CancellationToken ct)
        {
            byte[] resp = await RequestAsync(BuildReadPdu(functionCode, start, count), ct).ConfigureAwait(false);
            if (resp.Length < 2)
            {
                throw new IOException("Modbus 响应报文过短");
            }

            int byteCount = resp[1];
            if (resp.Length < 2 + byteCount || byteCount < (count + 7) / 8)
            {
                throw new IOException("Modbus 响应数据不完整");
            }

            bool[] bits = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bits[i] = (resp[2 + i / 8] & (1 << (i % 8))) != 0;
            }

            return bits;
        }

        private async Task<ushort[]> ReadRegistersAsync(byte functionCode, ushort start, ushort count, CancellationToken ct)
        {
            byte[] resp = await RequestAsync(BuildReadPdu(functionCode, start, count), ct).ConfigureAwait(false);
            if (resp.Length < 2)
            {
                throw new IOException("Modbus 响应报文过短");
            }

            int byteCount = resp[1];
            if (byteCount != count * 2 || resp.Length < 2 + byteCount)
            {
                throw new IOException("Modbus 响应寄存器数不匹配");
            }

            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
            }

            return values;
        }

        private static byte[] BuildReadPdu(byte functionCode, ushort start, ushort count) => new[]
        {
            functionCode,
            (byte)(start >> 8), (byte)start,
            (byte)(count >> 8), (byte)count,
        };

        private static byte[] BuildWriteSinglePdu(byte functionCode, ushort address, ushort value) => new[]
        {
            functionCode,
            (byte)(address >> 8), (byte)address,
            (byte)(value >> 8), (byte)value,
        };

        /// <summary>
        /// 三个写功能码的公共路径。写入响应与读响应**格式完全不同**：
        /// 读回「字节数 + 数据」，写回「功能码 + 地址 + 值/数量」的**回显**（恒 4 字节 PDU），
        /// 故不能走读的那套长度校验，这里自行校验。
        /// </summary>
        private async Task WriteAsync(byte[] pdu, byte functionCode, CancellationToken ct, bool isMultiple = false)
        {
            byte[] resp = await RequestAsync(pdu, ct, isWrite: true).ConfigureAwait(false);

            // 响应长度：0x05/0x06 回显地址 + 值；0x10 回显地址 + 寄存器数
            if (resp.Length != 5)
            {
                throw new IOException($"Modbus 写响应长度异常：{resp.Length}");
            }

            // 回显的地址必须与请求一致（0x10 还要比对数量）——防止应答错位时误判成功
            if (resp[1] != pdu[1] || resp[2] != pdu[2])
            {
                throw new IOException("Modbus 写响应地址与请求不符");
            }

            if (isMultiple)
            {
                if (resp[3] != pdu[3] || resp[4] != pdu[4])
                {
                    throw new IOException("Modbus 写响应寄存器数与请求不符");
                }
            }
            else if (resp[3] != pdu[3] || resp[4] != pdu[4])
            {
                throw new IOException("Modbus 写响应值与请求不符");
            }
        }

        private static void ThrowIfException(byte[] resp)
        {
            if (resp.Length >= 1 && (resp[0] & 0x80) != 0)
            {
                byte code = resp.Length >= 2 ? resp[1] : (byte)0;
                throw new ModbusResponseException(code, $"Modbus 异常：{DescribeException(code)}（异常码 {code}）");
            }
        }

        /// <summary>
        /// Modbus 异常码的中文说明（GB/T 19582 / MODBUS Application Protocol V1.1b3）。
        /// 注意：部分 PLC 对越界或跨地址区的请求回非标准码 —— 实测信捷 XD 对跨本体/扩展空隙的
        /// 读取回 <c>06</c>（标准语义为「从站设备忙」，而非「非法数据地址」02）。
        /// </summary>
        internal static string DescribeException(byte code) => code switch
        {
            1 => "非法功能码",
            2 => "非法数据地址",
            3 => "非法数据值",
            4 => "从站设备故障",
            5 => "确认（从站已受理，需稍后查询结果）",
            6 => "从站设备忙",
            7 => "否认",
            8 => "存储奇偶校验错误",
            10 => "网关路径不可用",
            11 => "网关目标设备无响应",
            _ => "未知异常",
        };

        /// <summary>发送请求并返回响应 PDU（不含 MBAP/单元号）。请求串行，2 秒超时。</summary>
        /// <param name="isWrite">
        /// true 表示这是写请求，响应是「回显」而非「字节数 + 数据」，
        /// 故跳过读专用的长度校验（由调用方自行校验）。
        /// </param>
        private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken ct, TimeSpan? idleInterval = null, bool isWrite = false)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                TcpClient tcp = _tcp ?? throw new IOException("连接未建立");
                if (idleInterval.HasValue
                    && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastResponseTimestamp)) < idleInterval.Value)
                {
                    return Array.Empty<byte>();
                }

                NetworkStream stream = tcp.GetStream();
                ushort tid = ++_tid;
                int length = pdu.Length + 1; // 单元号 + PDU
                byte[] frame = new byte[6 + length];
                frame[0] = (byte)(tid >> 8);
                frame[1] = (byte)tid;
                frame[2] = 0; // 协议标识 = 0（Modbus）
                frame[3] = 0;
                frame[4] = (byte)(length >> 8);
                frame[5] = (byte)length;
                frame[6] = UnitId;
                Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);

                // MBAP 头 6 字节：tid(2) + pid(2) + length(2)；length 后面还有 length 字节（单元号 + PDU）
                byte[] header = new byte[6];
                await ReadExactAsync(stream, header, timeout.Token).ConfigureAwait(false);

                ushort respTid = (ushort)((header[0] << 8) | header[1]);
                if (header[2] != 0 || header[3] != 0)
                {
                    throw new IOException("Modbus 协议标识异常");
                }

                if (respTid != tid)
                {
                    throw new IOException("Modbus 事务标识不匹配");
                }

                int respLength = (header[4] << 8) | header[5]; // 含单元号
                if (respLength < 2 || respLength > 260)
                {
                    throw new IOException($"Modbus 响应长度异常：{respLength}");
                }

                byte[] body = new byte[respLength];
                await ReadExactAsync(stream, body, timeout.Token).ConfigureAwait(false);

                if (body[0] != frame[6])
                {
                    throw new IOException("Modbus 单元号不匹配");
                }

                byte[] respPdu = new byte[respLength - 1];
                Buffer.BlockCopy(body, 1, respPdu, 0, respLength - 1);
                bool exception = respPdu[0] == (pdu[0] | 0x80);
                if (exception)
                {
                    if (respPdu.Length != 2)
                    {
                        throw new IOException("Modbus 异常响应长度无效");
                    }
                }
                else if (!isWrite)
                {
                    int count = (pdu[3] << 8) | pdu[4];
                    int expectedBytes = pdu[0] is 0x01 or 0x02 ? (count + 7) / 8 : count * 2;
                    if (respPdu[0] != pdu[0] || respPdu.Length != expectedBytes + 2
                        || respPdu[1] != expectedBytes)
                    {
                        throw new IOException("Modbus 响应功能码或数据长度不匹配");
                    }
                }
                else
                {
                    // 写响应是「回显」：功能码 + 地址 + 值/数量，恒 5 字节 PDU。
                    // 只在这里校验功能码；地址与值的一致性由 WriteAsync 比对。
                    if (respPdu[0] != pdu[0] || respPdu.Length != 5)
                    {
                        throw new IOException("Modbus 写响应功能码或长度不匹配");
                    }
                }

                Interlocked.Exchange(ref _lastResponseTimestamp, Stopwatch.GetTimestamp());
                ThrowIfException(respPdu);
                return respPdu;
            }
            catch (ModbusResponseException)
            {
                // 合法异常响应：帧已完整读完，连接仍可复用。
                throw;
            }
            catch
            {
                // 超时/取消可能留下半帧，关闭连接，阻止排队请求继续使用失去同步的流。
                Dispose();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("连接被对方关闭");
                }

                offset += read;
            }
        }

        public void Dispose()
        {
            _tcp?.Dispose();
            _tcp = null;
            // 不释放 _gate：可能仍有在途请求，SemaphoreSlim 无句柄资源可泄漏
        }
    }
}
