using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Polling;

/// <summary>
/// 采集编排器：轮询循环 + 心跳循环 + 断线通知。
/// <para>
/// 本类**不持有任何 WPF 控件**，也不认识 <c>MainWindow</c>。与窗口的交互只有两条通道：
/// </para>
/// <list type="number">
/// <item><see cref="PollSnapshot"/>（窗口 → 引擎）：每拍读一次，告诉它该采什么；</item>
/// <item><see cref="ICollectorHost"/>（引擎 → 窗口）：需要回 UI 线程时调用，由窗口负责
/// <c>Dispatcher.InvokeAsync</c> 与「这条连接是否还是当前连接」的校验。</item>
/// </list>
/// <para>
/// 这样拆的用意：线程安全的判断（哪些字段跨线程、何时该丢弃过期回调）集中留在窗口层，
/// 而采集本身的编排（分块、切窗、功能码选择、失败策略）集中在这里，两边各自可读。
/// </para>
/// </summary>
internal sealed class ModbusPoller
{
    /// <summary>单次 FC01 最多读的点数；同区跨度超限时按此切窗。</summary>
    public const int MaxCoilsPerRead = 2000;

    private readonly ICollectorHost _host;

    public ModbusPoller(ICollectorHost host) => _host = host;

    /// <summary>
    /// 轮询主循环。异常分类与 IO / 自由监控两条支路都在这里收敛。
    /// </summary>
    public async Task PollLoopAsync(ModbusTcpClient client, CommDriver driver, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            DateTime start = DateTime.UtcNow;

            try
            {
                await PollOnceAsync(client, driver, token);
            }
            catch (OperationCanceledException)
            {
                // 我方取消 = 正常停止；读超时 = 设备无响应，按断线处理
                if (token.IsCancellationRequested)
                {
                    break;
                }

                NotifyConnectionLost(client, token, "读取超时");
                break;
            }
            catch (Exception ex)
            {
                NotifyConnectionLost(client, token, ex.Message);
                break;
            }

            // 采集周期每拍重读：用户在跑动中改档位要立即生效
            TimeSpan wait = TimeSpan.FromMilliseconds(_host.PollIntervalMs) - (DateTime.UtcNow - start);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 一拍采集。按可见性分流：自由监控走 <see cref="PollFreeMonitorAsync"/>，否则读 IO 当前页。
    /// </summary>
    public async Task PollOnceAsync(ModbusTcpClient modbus, CommDriver driver, CancellationToken token)
    {
        PollSnapshot snap = _host.CaptureSnapshot();

        // 离开状态信息页仅停采；独立心跳继续工作，各分段保留上次采集数据。
        if (!snap.IoPageActive)
        {
            return;
        }

        // 状态信息页的三个分段**都跟随可见性**：输入 X / 输出 Y 走 IO 点位集，
        // 自由监控走 _monitorSet —— 只有当前显示的那一个才读，其余不读也不清值。
        if (snap.FreeViewActive)
        {
            await PollFreeMonitorAsync(modbus, driver, token);
            return;
        }

        // 只读当前显示分段「当前页」的点位；点位集由 IO 表决定，未导入时为空 → 不发报文
        IoPoint[] points = snap.VisiblePage;
        if (points.Length == 0)
        {
            return;
        }

        // 本页点位可能跨多段地址区（如信捷 X 本体 + 扩展模块）：区与区之间的空隙地址从站不接受，
        // 跨区一次读会回异常响应而被判为断线。故按所属地址区分块，每块读本区最小..最大地址（FC01），再按偏移回填。
        // 连接期间设备类型下拉已置灰，驱动不会变化。
        var results = new List<(IoPoint[] Points, int Min, bool[] Coils)>();

        foreach (var group in points.GroupBy(p => driver.FindArea(ModbusArea.Coil, p.Address)))
        {
            // 单次 FC01 最多 2000 点；同区跨度超限时再按 2000 切分
            int baseAddress = group.Min(p => p.Address);
            foreach (var chunk in group.GroupBy(p => (p.Address - baseAddress) / MaxCoilsPerRead))
            {
                IoPoint[] chunkPoints = chunk.ToArray();
                int min = chunkPoints.Min(p => p.Address);
                int max = chunkPoints.Max(p => p.Address);
                bool[] coils = await modbus.ReadCoilsAsync((ushort)min, (ushort)(max - min + 1), token);
                results.Add((chunkPoints, min, coils));
            }
        }

        // 回 UI 线程刷新绑定（IoPoint 为 INPC 对象，IsOn 变更驱动灯色）
        await _host.RunOnUiAsync(() =>
        {
            if (!_host.IsCurrentConnection(modbus, token))
            {
                return;
            }

            foreach (var (chunkPoints, min, coils) in results)
            {
                foreach (IoPoint p in chunkPoints)
                {
                    int offset = p.Address - min;
                    if (offset >= 0 && offset < coils.Length)
                    {
                        p.IsOn = coils[offset];
                    }
                }
            }
        });
    }

    /// <summary>
    /// 读当前「自由监控」分段的所有监控项（跟随可见性：仅该分段显示时调用）。
    /// <para>
    /// 与 IO 轮询的两点差异：
    /// </para>
    /// <list type="bullet">
    /// <item>线圈区用 FC01、寄存器区按驱动区段选 FC03/FC04（IO 只有线圈，恒 FC01）；</item>
    /// <item>读取失败**不触发断线**，只把原因写到该行的「当前值」格 ——
    /// 一个配错的地址不该拖垮整条连接（与心跳遇异常码保持连接的做法一致）。</item>
    /// </list>
    /// </summary>
    public async Task PollFreeMonitorAsync(ModbusTcpClient modbus, CommDriver driver, CancellationToken token)
    {
        List<ReadBlock> plan = _host.MonitorSet.BuildReadPlan();
        if (plan.Count == 0)
        {
            return;
        }

        var results = new List<(ReadBlock Block, bool[]? Coils, ushort[]? Registers, string? Error)>();

        foreach (ReadBlock block in plan)
        {
            // 逐块复查可见性：用户切走分段后立即停发剩余报文，
            // 不必等本拍走完（块数可能不少，白白占总线）。
            PollSnapshot mid = _host.CaptureSnapshot();
            if (!mid.FreeViewActive || !mid.IoPageActive)
            {
                return;
            }

            try
            {
                if (block.Area is ModbusArea.Coil)
                {
                    bool[] coils = await modbus.ReadCoilsAsync(
                        (ushort)block.StartAddress, (ushort)block.Count, token);
                    results.Add((block, coils, null, null));
                }
                else if (block.Area == ModbusArea.DiscreteInput)
                {
                    bool[] inputs = await modbus.ReadDiscreteInputsAsync(
                        (ushort)block.StartAddress, (ushort)block.Count, token);
                    results.Add((block, inputs, null, null));
                }
                else if (block.Area == ModbusArea.HoldingRegister)
                {
                    ushort[] regs = await modbus.ReadHoldingRegistersAsync(
                        (ushort)block.StartAddress, (ushort)block.Count, token);
                    results.Add((block, null, regs, null));
                }
                else
                {
                    ushort[] regs = await modbus.ReadInputRegistersAsync(
                        (ushort)block.StartAddress, (ushort)block.Count, token);
                    results.Add((block, null, regs, null));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单块失败只标该块内的项，继续读下一块（其它地址可能正常）
                results.Add((block, null, null, _host.ClassifyDisconnectReason(ex.Message)));
            }
        }

        await _host.RunOnUiAsync(() =>
        {
            if (!_host.IsCurrentConnection(modbus, token))
            {
                return;
            }

            // 复查可见性：本拍可能跨越了用户的切换动作（可见性由 UI 线程改，
            // 而上面读计划的判断发生在切换**之前**）。已切走就丢弃本拍结果 ——
            // 值保持上一次的，符合「离开分段停读且保留上次值」。
            PollSnapshot after = _host.CaptureSnapshot();
            if (!after.FreeViewActive || !after.IoPageActive)
            {
                return;
            }

            foreach (var (block, coils, regs, error) in results)
            {
                if (error != null)
                {
                    foreach (ReadEntry e in block.Entries)
                    {
                        e.Item.ReadError = error;
                    }

                    continue;
                }

                MonitorItemSet.ApplyBlock(block, coils, regs, driver);
            }
        });
    }

    /// <summary>
    /// 异常断线：记日志 → 带参 <c>Disconnect</c>（保留重连目标）→ 启动自动重连。
    /// </summary>
    public void NotifyConnectionLost(ModbusTcpClient client, CancellationToken token, string reason)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        _host.NotifyConnectionLost(client, token, reason);
    }
}
