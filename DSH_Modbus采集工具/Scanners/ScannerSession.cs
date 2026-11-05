using System;
using System.Net;
using System.Threading;
using DSH_Modbus采集工具.Polling;

namespace DSH_Modbus采集工具.Scanners
{
    /// <summary>扫码器连接所需的参数快照，供自动重连复用。</summary>
    public sealed record ScannerTarget(string Ip, int Port, IPEndPoint? LocalEndPoint)
    {
        public string Endpoint => $"{Ip}:{Port}";
    }

    /// <summary>
    /// 单台扫码器连接的「身份与生命周期」状态。
    /// <para>
    /// 结构与 <see cref="DSH_Modbus采集工具.Polling.ConnectionSession"/> 一致，只是
    /// 客户端类型换成 <see cref="ScannerTcpClient"/>、目标换成 <see cref="ScannerTarget"/>。
    /// 每台扫码器各自持有一个本类实例，连接/重连**相互独立、互不影响**（P4.5）。
    /// </para>
    /// <para>
    /// 两个「断开」语义同样在此落地：<see cref="ForgetTarget"/> = 主动删除/停用（不再重连）；
    /// 只 <see cref="ReleaseClient"/> = 异常断开（目标留着 → 之后自动重连）。
    /// </para>
    /// </summary>
    public sealed class ScannerSession
    {
        private ScannerTcpClient? _client;
        private CancellationTokenSource? _connectCts;
        private CancellationTokenSource? _reconnectCts;
        private ScannerTarget? _target;
        private int _attempt;

        /// <summary>
        /// 下一次允许发起连接的时刻（UTC）。**首次连接失败**也用它铺退避
        /// （2026-11-05 方案 B，见 §5.9.7）：没有它时「从未连上」的设备会被下一拍
        /// 立刻重连，间隔只等于 3 秒连接超时，失败日志跟着刷屏。
        /// </summary>
        private DateTime _nextAttemptAtUtc = DateTime.MinValue;

        /// <summary>当前客户端；未连接时为 null。</summary>
        public ScannerTcpClient? Client => _client;

        public bool IsConnected { get; private set; }

        public bool IsConnecting => _connectCts != null;

        public bool IsReconnecting => _reconnectCts != null;

        public ScannerTarget? Target => _target;

        public int Attempt => _attempt;

        /// <summary>这条连接是否仍是「当前连接」。</summary>
        public bool IsCurrent(ScannerTcpClient client, CancellationToken token, bool windowClosed) =>
            !windowClosed && !token.IsCancellationRequested && IsConnected
            && ReferenceEquals(_client, client);

        public CancellationTokenSource BeginConnect(CancellationToken token)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _connectCts = cts;
            return cts;
        }

        public void CancelConnect() => _connectCts?.Cancel();

        public void EndConnect(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }
        }

        public void MarkConnected(ScannerTcpClient client, ScannerTarget target)
        {
            _client = client;
            IsConnected = true;
            _target = target;
            ResetBackoff();
        }

        public void ReleaseClient()
        {
            _client?.Dispose();
            _client = null;
            IsConnected = false;
        }

        public void ForgetTarget()
        {
            _target = null;
            CancelReconnect();
            ResetBackoff();
        }

        /// <summary>退避已到期？首次连接与断线重连共用这一道闸门。</summary>
        public bool CanAttemptNow(DateTime utcNow) => utcNow >= _nextAttemptAtUtc;

        /// <summary>
        /// 记一次连接失败：失败次数加一，并把下一次允许尝试的时刻推到退避之后。
        /// <para>
        /// 退避表与 PLC 共用 <see cref="ReconnectPolicy.DelaysSeconds"/>（1→2→4→5s，封顶 5s）。
        /// 第一次失败后等 1s、第二次 2s…… 与 PLC <c>ReconnectLoopAsync</c> 的节奏一致
        /// （那边是先 <c>DelayForAttempt(Attempt)</c> 再尝试，这里在失败时就把等待算好）。
        /// </para>
        /// </summary>
        public void RegisterConnectFailure(DateTime utcNow)
        {
            _attempt++;
            _nextAttemptAtUtc = utcNow.AddSeconds(ReconnectPolicy.DelayForAttempt(_attempt - 1));
        }

        /// <summary>
        /// 清零失败计数与等待。**连上 / 用户重新启用 / 端点改了**都要调：
        /// 否则改对 IP 之后还要白等最多 10 秒，看起来像「改了也没用」。
        /// </summary>
        public void ResetBackoff()
        {
            _attempt = 0;
            _nextAttemptAtUtc = DateTime.MinValue;
        }

        public CancellationTokenSource BeginReconnect()
        {
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            ResetBackoff();
            return cts;
        }

        public void EndReconnect(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_reconnectCts, cts))
            {
                _reconnectCts = null;
            }
        }

        public void CancelReconnect() => _reconnectCts?.Cancel();
    }
}
