using System;
using System.Net;
using System.Threading;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Polling
{
    /// <summary>保持连接所需的参数快照，供自动重连复用。</summary>
    public sealed record ReconnectTarget(string Ip, int Port, CommDriver Driver, IPEndPoint? LocalEndPoint)
    {
        public string Endpoint => $"{Ip}:{Port}";
    }

    /// <summary>
    /// 连接的「身份与生命周期」状态。
    /// <para>
    /// **只放同步的状态迁移，不放 async 编排**：连接与重连的等待循环（逐秒刷状态栏、
    /// 按钮文案、表单锁定）是界面驱动的东西，留在窗口才看得清；搬进来只会凭空多一层
    /// 接口，却不增加任何可断言性。这里每条规则都带不变量，能脱离窗口断言。
    /// </para>
    /// <para>
    /// 两个「断开」语义的区别在这里落了地：<see cref="ForgetTarget"/> = 用户主动断开
    /// （忘掉期望目标 → 此后不自动重连）；不调它而只 <see cref="ReleaseClient"/> = 异常断开
    /// （目标留着 → 之后才能自动重连）。
    /// </para>
    /// </summary>
    public sealed class ConnectionSession
    {
        private ModbusTcpClient? _client;
        private CancellationTokenSource? _connectCts;
        private CancellationTokenSource? _reconnectCts;
        private ReconnectTarget? _target;
        private int _attempt;

        /// <summary>当前客户端；未连接时为 null。</summary>
        public ModbusTcpClient? Client => _client;

        /// <summary>是否已连接。</summary>
        public bool IsConnected { get; private set; }

        /// <summary>是否有一段连接尝试正在途中。</summary>
        public bool IsConnecting => _connectCts != null;

        /// <summary>是否正在自动重连（退避等待中或正在尝试）。</summary>
        public bool IsReconnecting => _reconnectCts != null;

        /// <summary>期望保持的连接目标；null 表示「用户不想连着」。</summary>
        public ReconnectTarget? Target => _target;

        /// <summary>已连续重连失败次数（退避表按它取下标）。</summary>
        public int Attempt => _attempt;

        /// <summary>
        /// 这条连接是否仍是「当前连接」：未关窗 && token 未取消 && 仍连着 && 同一个客户端实例。
        /// 旧连接的后台回调靠它失效。
        /// </summary>
        public bool IsCurrent(ModbusTcpClient client, CancellationToken token, bool windowClosed) =>
            !windowClosed && !token.IsCancellationRequested && IsConnected
            && ReferenceEquals(_client, client);

        /// <summary>登记一段在途连接（与调用方的 token 联动），返回取消源供 <c>using</c> 释放。</summary>
        public CancellationTokenSource BeginConnect(CancellationToken token)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _connectCts = cts;
            return cts;
        }

        /// <summary>取消在途的连接尝试（若有）。</summary>
        public void CancelConnect() => _connectCts?.Cancel();

        /// <summary>连接尝试收尾：只清自己那一段，免得把后来者的取消源清掉。</summary>
        public void EndConnect(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }
        }

        /// <summary>连接成功：接管客户端、记住期望目标、清零退避计数。</summary>
        public void MarkConnected(ModbusTcpClient client, ReconnectTarget target)
        {
            _client = client;
            IsConnected = true;
            _target = target;
            _attempt = 0;
        }

        /// <summary>释放客户端并置为未连接。**不动期望目标** —— 异常断开正是靠它才能重连。</summary>
        public void ReleaseClient()
        {
            _client?.Dispose();
            _client = null;
            IsConnected = false;
        }

        /// <summary>用户主动断开的语义：忘掉期望目标（此后不会自动重连），并取消在途重连。</summary>
        public void ForgetTarget()
        {
            _target = null;
            CancelReconnect();
        }

        /// <summary>异常断开后的重连起点：登记取消源并清零尝试计数。</summary>
        public CancellationTokenSource BeginReconnect()
        {
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _attempt = 0;
            return cts;
        }

        /// <summary>重连循环收尾：只清自己那一段。</summary>
        public void EndReconnect(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_reconnectCts, cts))
            {
                _reconnectCts = null;
            }
        }

        /// <summary>取消在途重连（用户主动断开、点「取消重连」或关窗）。</summary>
        public void CancelReconnect() => _reconnectCts?.Cancel();

        /// <summary>计数一次失败的重连尝试。</summary>
        public void CountAttempt() => _attempt++;
    }
}
