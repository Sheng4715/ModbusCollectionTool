using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Modbus采集工具.Scanners
{
    /// <summary>
    /// 扫码器 TCP 客户端：裸字节流（**无 MBAP**），用于「发触发文本 → 收一条结果帧」。
    /// <para>
    /// 与 <see cref="DSH_Modbus采集工具.Drivers.ModbusTcpClient"/> 同一套骨架：
    /// 单连接、请求串行（信号量）、连接超时、本地端点绑定、失败关连接防半帧失步。
    /// 差别在传输层：Modbus 走 MBAP 帧 + 功能码，这里只是 ASCII 字节流，
    /// 帧边界由「可配置结束符」或「字节静默间隔」判定（P4.5）。
    /// </para>
    /// </summary>
    public sealed class ScannerTcpClient : IDisposable
    {
        /// <summary>「无结束符」模式下判定收完的字节静默间隔。</summary>
        public static readonly TimeSpan SilenceInterval = TimeSpan.FromMilliseconds(100);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private TcpClient? _tcp;

        public bool IsConnected => _tcp?.Connected == true;

        /// <summary>
        /// 建立 TCP 连接；超时抛 OperationCanceledException。
        /// <paramref name="localEndPoint"/> 非空时先绑定本地源地址（与 PLC 连接同一套多网卡口径）。
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

        /// <summary>
        /// 原样发送一段字节，**不补结束符**（触发文本原样发送，见 P3.5）。
        /// </summary>
        public async Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                TcpClient tcp = _tcp ?? throw new IOException("连接未建立");
                await tcp.GetStream().WriteAsync(data, ct).ConfigureAwait(false);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 在发触发之前丢弃缓冲区里已到达的数据：扫码器可能主动吐数据，
        /// 非响应期的数据一律丢弃（P4.5「纯响应 + 主动数据共存」）。尽力而为。
        /// </summary>
        public async Task DiscardPendingAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_tcp is { Connected: true } tcp)
                {
                    int available = tcp.Available;
                    if (available > 0)
                    {
                        var buf = new byte[available];
                        await tcp.GetStream().ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 读取一条完整结果帧并返回「结束符之前的字节」（不含结束符本身）。
        /// <para>
        /// <paramref name="terminator"/> 非空：读到结束符即认为完整；
        /// 为空：退化为「字节静默间隔」收尾（收完一字节后 <see cref="SilenceInterval"/> 内无后续即判收完）。
        /// 两者都受 <paramref name="timeout"/> 整体封顶。
        /// </para>
        /// </summary>
        public async Task<byte[]> ReadFrameAsync(byte[] terminator, TimeSpan timeout, CancellationToken ct = default)
        {
            // ⚠️ 声明在 try 之外：catch 里要靠 payload.Count 判断「有没有收到半帧」，
            // 决定这次失败该不该关连接（见下方 catch 的注释）。
            var payload = new List<byte>();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                TcpClient tcp = _tcp ?? throw new IOException("连接未建立");
                NetworkStream stream = tcp.GetStream();

                using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                overall.CancelAfter(timeout);

                bool silenceMode = terminator.Length == 0;
                var one = new byte[1];
                var silenceElapsed = System.Diagnostics.Stopwatch.StartNew();

                // ⚠️ **本方法的铁律：绝不取消任何在途的 ReadAsync。**
                // 实测（.tmp/ScanProbe/CancelProbe）：取消 NetworkStream.ReadAsync 会让套接字
                // 进入断开态 —— 每成功收帧一次，client.Connected 立刻变 false，下一轮就发不出去，
                // 表现为「连上 → 收一次 → 断开 → 重连」无限循环。
                // 所以「有没有数据」全靠 DataAvailable 轮询，「等多久算超时」全靠计时器，
                // ReadAsync 只在确认 DataAvailable 之后调用（必有数据，立即返回），
                // 且**不传任何会取消的 token**。
                while (true)
                {
                    if (silenceMode && payload.Count > 0 && silenceElapsed.Elapsed >= SilenceInterval)
                    {
                        break; // 静默间隔到 → 帧完整
                    }

                    if (overall.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(overall.Token);
                    }

                    if (!stream.DataAvailable)
                    {
                        await Task.Delay(PollInterval, overall.Token).ConfigureAwait(false);
                        continue;
                    }

                    // 有数据了才读：这里读到的是已经到达缓冲区的字节，不会阻塞
                    int n = await stream.ReadAsync(one.AsMemory()).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new IOException("连接被对方关闭");
                    }

                    payload.Add(one[0]);
                    silenceElapsed.Restart();

                    if (!silenceMode && EndsWith(payload, terminator))
                    {
                        payload.RemoveRange(payload.Count - terminator.Length, terminator.Length);
                        break;
                    }
                }

                return payload.ToArray();
            }
            catch
            {
                // ⚠️ **只有「已经收到半帧」才关连接**（2026-11-11 修正）。
                //
                // 关的理由只有一个：流里剩着不完整的字节，下一轮会把旧帧的尾巴当成新帧开头
                // （失步）。**一个字节都没收到时流是干净的**，那只是一轮「对端没回话」。
                //
                // 旧写法无条件 Dispose()，后果是：TCP 明明一直是通的，只因为服务端这一轮
                // 没回包，就被判成断线 → 1 秒退避重连 → 日志里紧跟一条「已连接」。
                // 实测场景：服务端接受连接但故意不回包（联调时常见），日志看着像连接不稳，
                // 实际链路完好，纯属自找麻烦。
                //
                // 与上面那条「绝不取消在途 ReadAsync」是同一个道理的两面：
                // **超时 ≠ 断开**。链路真断会抛 IOException / 读到 0 字节，走的是另外两条路径。
                if (payload.Count > 0)
                {
                    Dispose();
                }

                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>静默模式下轮询是否有新数据的间隔（够短以免漏帧，够长以免空转）。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

        private static bool EndsWith(List<byte> data, byte[] suffix)
        {
            if (data.Count < suffix.Length)
            {
                return false;
            }

            for (int i = 0; i < suffix.Length; i++)
            {
                if (data[data.Count - suffix.Length + i] != suffix[i])
                {
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            _tcp?.Dispose();
            _tcp = null;
            // 不释放 _gate：可能仍有在途请求，SemaphoreSlim 无句柄资源可泄漏
        }
    }
}
