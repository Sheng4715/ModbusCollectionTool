using System;
using System.Threading;
using System.Threading.Tasks;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Polling;

/// <summary>
/// 采集引擎与宿主窗口之间的契约。
/// <para>
/// 刻意保持窄小：引擎只回 UI 线程执行回调，**是否还是当前连接**、**Dispatcher 是否已关**、
/// **哪些字段是跨线程的** 全部由宿主判断。引擎因此不需要知道任何 WPF 类型。
/// </para>
/// </summary>
internal interface ICollectorHost
{
    /// <summary>当前采集周期（毫秒）。每拍重读，支持跑动中改档位。</summary>
    int PollIntervalMs { get; }

    /// <summary>自由监控项集合（读取计划的来源、回填的目标）。</summary>
    MonitorItemSet MonitorSet { get; }

    /// <summary>
    /// 拍下当前界面可见性快照。**必须在 UI 线程调用**（读的都是 UI 线程维护的字段）。
    /// </summary>
    PollSnapshot CaptureSnapshot();

    /// <summary>
    /// 这条连接是否仍然是「当前连接」：未关窗 && token 未取消 && 仍连着 && 是同一个客户端实例。
    /// 旧连接的后台回调靠它失效。
    /// </summary>
    bool IsCurrentConnection(IModbusClient client, CancellationToken token);

    /// <summary>回 UI 线程执行（内部即 <c>Dispatcher.InvokeAsync</c>）。</summary>
    Task RunOnUiAsync(Action action);

    /// <summary>
    /// 回 UI 线程执行并取回结果（内部即 <c>Dispatcher.InvokeAsync(Func&lt;T&gt;)</c>）。
    /// 用于把「必须碰 UI 维护集合」的工作（如自由监控读计划）搬到 UI 线程上算，
    /// 后台线程只拿结果，避免跨线程枚举集合。
    /// </summary>
    Task<T> RunOnUiAsync<T>(Func<T> func);

    /// <summary>异常断线：记日志 → 保留重连目标地断开 → 启动自动重连。</summary>
    void NotifyConnectionLost(IModbusClient client, CancellationToken token, string reason);

    /// <summary>把 Socket / Modbus 的异常原文归一为简短中文。</summary>
    string ClassifyDisconnectReason(string raw);

    /// <summary>
    /// 连续多次收到**合法异常码**时的告警（链路仍在，只是从站拒绝了这次请求）。
    /// 由宿主回 UI 线程写「通讯异常」日志 —— 引擎层碰不到日志，也**不断开**连接。
    /// </summary>
    void ReportCommunicationWarning(IModbusClient client, CancellationToken token, string reason);
}
