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
/// <param name="IoPageActive">是否停在「状态信息」页（离开则三个分段全停，心跳不受影响）。</param>
/// <param name="FreeViewActive">是否显示「自由监控」分段。</param>
/// <param name="GlobalVarActive">是否停在采集配置页的「全局变量」分段（P3.3）。</param>
/// <param name="VisiblePage">当前显示分段「当前页」的 IO 点位快照；未导入 IO 表时为空数组。</param>
/// <param name="PollIntervalMs">本拍结束后到下一拍的目标间隔。</param>
internal readonly record struct PollSnapshot(
    bool IoPageActive,
    bool FreeViewActive,
    bool GlobalVarActive,
    IoPoint[] VisiblePage,
    int PollIntervalMs);
