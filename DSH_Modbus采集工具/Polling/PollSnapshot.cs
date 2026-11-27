using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Polling;

/// <summary>
/// 一拍采集所依据的界面可见性快照。
/// <para>
/// 存在的理由：轮询跑在后台线程、可见性由 UI 线程改。原来的写法是直接读窗口上的
/// <c>volatile</c> 字段，字段一多就没人说得清「哪几个是跨线程的」。改成每拍由 UI 线程
/// 拼一个不可变快照交给采集引擎，跨线程共享面从一堆散字段收成一个值。
/// </para>
/// <para>
/// ⚠️ 快照是**每拍重建**的，不是启动时缓存 —— 用户切分段、翻页必须下一拍就生效。
/// </para>
/// </summary>
/// <param name="IoPageActive">是否停在「设备监控」页（离开则各分段全停，心跳不受影响）。</param>
/// <param name="FreeViewActive">是否显示「自由监控」分段。</param>
/// <param name="TrafficViewActive">
/// 是否显示「通讯监控」分段（P2.4）。**选中它时 IO / 自由监控一律不采** ——
/// 它把前四个分段全遮住了，与既有的「跟随可见性」口径一致；采集任务组 / 扫码器触发 /
/// 心跳 / 扫码器收发 / HTTP 上报这些本就不看可见性的流量照旧。
/// </param>
/// <param name="VisiblePage">当前显示分段「当前页」的 IO 点位快照；未导入 IO 表时为空数组。</param>
/// <param name="PollIntervalMs">本拍结束后到下一拍的目标间隔。</param>
/// <param name="MachineViewActive">
/// 是否停在总览页的「PLC状态机」分段。状态机表达式会引用全局变量，
/// 而这些变量的值**只在它可见时才采**（与 IO 监控同一逻辑）。
/// </param>
/// <param name="MachineGlobals">
/// 状态机表达式**实际引用到**的全局变量（已去重、已剔除系统变量与不存在的名字）。
/// 由 UI 线程在表达式或变量定义变动时重算一次，本字段只是交引用，不在采集线程里算。
/// </param>
/// <param name="AcquisitionGlobals">
/// 采集任务组引用的全局变量 —— **常驻读取，与页面可见性无关**。
/// <para>
/// ⚠️ 这是本快照里唯一**不看可见性**的一项，与上面几个正好相反，理由见
/// <see cref="ModbusPoller.PollAcquisitionGlobalsAsync"/>：用户切到设备连接页调参数时，
/// 采集任务不能跟着停 —— 停了记录里会凭空缺一段，事后完全看不出来。
/// </para>
/// </param>
/// <param name="ScannerGlobals">
/// 扫码器**触发表达式**引用到的全局变量 —— 同样**常驻读取**（2026-11-11 用户定案）。
/// <para>
/// 扫码器节拍本来就常驻（50ms 判定），触发条件里的全局变量若只在某个页面可见时才采，
/// 用户切走页面后触发沿就永远等不到，而界面上没有任何提示。
/// </para>
/// </param>
internal readonly record struct PollSnapshot(
    bool IoPageActive,
    bool FreeViewActive,
    bool TrafficViewActive,
    IoPoint[] VisiblePage,
    int PollIntervalMs,
    bool MachineViewActive,
    IReadOnlyList<GlobalVariable> MachineGlobals,
    IReadOnlyList<GlobalVariable> AcquisitionGlobals,
    IReadOnlyList<GlobalVariable> ScannerGlobals);
