using System;
using System.Net;
using System.Threading;

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
            _attempt = 0;
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
        }

        public CancellationTokenSource BeginReconnect()
        {
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _attempt = 0;
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

        public void CountAttempt() => _attempt++;
    }
}
