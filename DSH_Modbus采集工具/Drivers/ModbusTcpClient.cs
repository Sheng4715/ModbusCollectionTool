using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>
    /// Modbus TCP 主站（只读）：MBAP 报文封装 + 功能码 0x01/0x02/0x03/0x04。
    /// 单连接、请求串行（信号量），单次请求 2 秒超时。
    /// </summary>
    public sealed class ModbusTcpClient : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private TcpClient? _tcp;
        private ushort _tid;

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
                _tcp = tcp;
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

        // ========== 内部实现 ==========

        private async Task<bool[]> ReadBitsAsync(byte functionCode, ushort start, ushort count, CancellationToken ct)
        {
            byte[] resp = await RequestAsync(BuildReadPdu(functionCode, start, count), ct).ConfigureAwait(false);
            ThrowIfException(resp);
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
            ThrowIfException(resp);
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

        private static void ThrowIfException(byte[] resp)
        {
            if (resp.Length >= 1 && (resp[0] & 0x80) != 0)
            {
                byte code = resp.Length >= 2 ? resp[1] : (byte)0;
                throw new IOException($"Modbus 异常：{DescribeException(code)}（异常码 {code}）");
            }
        }

        /// <summary>
        /// Modbus 异常码的中文说明（GB/T 19582 / MODBUS Application Protocol V1.1b3）。
        /// 注意：部分 PLC 对越界或跨地址区的请求回非标准码 —— 实测信捷 XD 对跨本体/扩展空隙的
        /// 读取回 <c>06</c>（标准语义为「从站设备忙」，而非「非法数据地址」02）。
        /// </summary>
        private static string DescribeException(byte code) => code switch
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
        private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken ct)
        {
            TcpClient tcp = _tcp ?? throw new IOException("连接未建立");
            NetworkStream stream = tcp.GetStream();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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

                byte[] respPdu = new byte[respLength - 1];
                Buffer.BlockCopy(body, 1, respPdu, 0, respLength - 1);
                return respPdu;
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
