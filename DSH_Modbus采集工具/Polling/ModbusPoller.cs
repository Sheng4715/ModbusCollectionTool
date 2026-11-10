using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

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

    /// <summary>
    /// 同一块**连续**收到几次合法异常码才记一条日志。
    /// 不触发断开：异常响应本身证明链路是通的（ADR-11 对心跳同理）。
    /// </summary>
    public const int ConsecutiveExceptionThreshold = 5;

    private readonly ICollectorHost _host;

    /// <summary>记下计数所属的客户端；重连/换设备时据此清空下面的计数。</summary>
    private ModbusTcpClient? _streakOwner;

    /// <summary>每块的连续异常码计数，键 = 块的地址范围（如 "20480-20543"）。读成功一次即清零。</summary>
    private readonly Dictionary<string, int> _blockExceptionStreaks = new();

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

        // 总览页「PLC状态机」：表达式引用的全局变量跟随可见性采集。
        // 与下面的 IO / 自由监控**互斥**（总览与设备监控是两个页面），故各走各的、不叠加。
        if (snap.MachineViewActive)
        {
            await PollMachineGlobalsAsync(modbus, snap.MachineGlobals, token);
        }

        // 采集任务组引用的变量：**不看可见性，每拍都读**。
        // 放在 return 之前 —— 用户切走页面时下面的 IO / 自由监控全停，唯独这里要继续。
        if (snap.AcquisitionGlobals.Count > 0)
        {
            await PollAcquisitionGlobalsAsync(modbus, snap.AcquisitionGlobals, token);
        }

        // 扫码器触发表达式引用的变量：同样**不看可见性**
        // （扫码器节拍常驻，触发沿不能因为切页面而停）。2026-11-11 用户定案。
        if (snap.ScannerGlobals.Count > 0)
        {
            await PollScannerGlobalsAsync(modbus, snap.ScannerGlobals, token);
        }

        // 离开设备监控页仅停采；独立心跳继续工作，各分段保留上次采集数据。
        if (!snap.IoPageActive)
        {
            return;
        }

        // 设备监控页的三个分段**都跟随可见性**：输入 X / 输出 Y 走 IO 点位集，
        // 自由监控走 _monitorSet —— 只有当前显示的那一个才读，其余不读也不清值。
        if (snap.FreeViewActive)
        {
            await PollFreeMonitorAsync(modbus, token);
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

        // 换客户端（重连、或连到别的设备）就清空计数，避免上一段连接的失败次数被继承
        if (!ReferenceEquals(_streakOwner, modbus))
        {
            _streakOwner = modbus;
            _blockExceptionStreaks.Clear();
        }

        foreach (var group in points.GroupBy(p => driver.FindArea(ModbusArea.Coil, p.Address)))
        {
            // 单次 FC01 最多 2000 点；同区跨度超限时再按 2000 切分
            int baseAddress = group.Min(p => p.Address);
            foreach (var chunk in group.GroupBy(p => (p.Address - baseAddress) / MaxCoilsPerRead))
            {
                IoPoint[] chunkPoints = chunk.ToArray();
                int min = chunkPoints.Min(p => p.Address);
                int max = chunkPoints.Max(p => p.Address);
                string key = $"{min}-{max}";

                try
                {
                    bool[] coils = await modbus.ReadCoilsAsync((ushort)min, (ushort)(max - min + 1), token);
                    _blockExceptionStreaks.Remove(key);
                    results.Add((chunkPoints, min, coils));
                }
                catch (ModbusResponseException ex)
                {
                    // 合法异常码 = 链路正常、从站拒绝了这个请求（例如 IO 表里有、现场没接的点位回异常码 2），
                    // 不能当断线：跳过本块、其余块照读，本块的点保持上次值（results 里没有它）。
                    // 只有**连续**凑满阈值才记一条日志 —— 100ms 一拍，挨拍就记会瞬间刷满日志。
                    _blockExceptionStreaks.TryGetValue(key, out int streak);
                    streak++;
                    _blockExceptionStreaks[key] = streak;
                    if (streak == ConsecutiveExceptionThreshold)
                    {
                        // 措辞：原因（地址 xxx 异常码 N）
                        _host.ReportCommunicationWarning(modbus, token,
                            $"{ModbusTcpClient.DescribeException(ex.ExceptionCode)}（地址 {min}-{max} 异常码 {ex.ExceptionCode}）");
                    }
                }
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
    /// <para>
    /// 字节顺序不从这里传：它由用户在设备连接页改，跟着 <see cref="ICollectorHost.MonitorSet"/>
    /// 上的 <c>ByteOrders</c> 走，回填时直接取那份快照。
    /// </para>
    /// </summary>
    public async Task PollFreeMonitorAsync(ModbusTcpClient modbus, CancellationToken token)
    {
        // 读计划涉及遍历 _monitorSet（UI 线程维护的 ObservableCollection），
        // 不能在采集线程裸枚举 —— 用户此刻增删行 / 改字节顺序会抛
        // "Collection was modified"，再被 PollLoopAsync 误判成断线。
        // 故回 UI 线程把计划算成不可变结果，采集线程只拿回来用。
        List<ReadBlock> plan = await _host.RunOnUiAsync(() => _host.MonitorSet.BuildReadPlan());
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
            catch (ModbusResponseException ex)
            {
                // **只有合法异常码**才按「单块失败」处理：链路是通的，从站只是拒绝了这一条请求
                // （例如地址配错、跨地址区）。跳过本块、继续读下一块，连接保持。
                //
                // ⚠️ **不能在这里 catch (Exception)**：那会把 IOException（"连接被对方关闭"、
                // 事务标识不匹配 …）也吞成行内错误。而 RequestAsync 遇到这类异常时**已经
                // Dispose 了客户端**，连接已死 —— 吞掉它只会让界面一直停在「已连接」，
                // 后续每块都撞「连接未建立」，真正的断线只能等心跳循环来兜底。
                // 让它上抛到 PollLoopAsync 的统一断线分支，才能正确记日志 + 自动重连。
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

                MonitorItemSet.ApplyBlock(block, coils, regs, _host.MonitorSet.ByteOrders);
            }
        });
    }

    /// <summary>
    /// 读「PLC状态机」表达式引用到的全局变量，写回 <c>GlobalVariable.Value</c>，
    /// 供 <c>ExpressionVariables</c> 求值（P1.2，2026-10-21）。
    /// <para>
    /// 与 <see cref="PollFreeMonitorAsync"/> 同一套规矩：跟随可见性、逐块复查、
    /// 单块失败**不触发断线**（一个配错的地址不该拖垮整条连接）。
    /// </para>
    /// <para>
    /// ⚠️ 单块失败时**保留上一次的值**，不把变量清零：清零会让 <c>SysState == 0</c>
    /// 这类表达式算出「Stopped 亮」，看起来像是设备真的停了 —— 比不刷新更误导。
    /// 连接级的失败另有底栏与日志兜底。
    /// </para>
    /// </summary>
    public async Task PollMachineGlobalsAsync(
        ModbusTcpClient modbus, IReadOnlyList<GlobalVariable> variables, CancellationToken token)
    {
        List<GlobalReadBlock> plan = GlobalReadPlan.Build(variables);
        if (plan.Count == 0)
        {
            return;
        }

        // 逐块复查可见性：用户切走后立即停发剩余报文
        var results = await ReadGlobalBlocksAsync(
            modbus, plan, token, () => _host.CaptureSnapshot().MachineViewActive);

        if (results.Count == 0)
        {
            return;
        }

        await _host.RunOnUiAsync(() =>
        {
            if (!_host.IsCurrentConnection(modbus, token))
            {
                return;
            }

            // 复查可见性：本拍可能跨越了用户的切换动作，已切走就丢弃本拍结果
            PollSnapshot after = _host.CaptureSnapshot();
            if (!after.MachineViewActive)
            {
                return;
            }

            ApplyGlobalBlocks(results);
        });
    }

    /// <summary>
    /// 读采集任务组引用的全局变量，写回 <c>GlobalVariable.Value</c>（P3.1）。
    /// <para>
    /// ⚠️ **与上面那个最大的区别：不看页面可见性**。用户切到设备连接页调参数时，
    /// 采集任务照样要跑 —— 一停记录里就会凭空缺一段，而缺的那段事后完全看不出来
    /// （文件里只有「11:04:22」这样的时间戳，没有「这段没采」的标记）。
    /// </para>
    /// <para>
    /// 代价是只要有任何启用的采集任务，这条连接就一直在读那几个变量，不再有
    /// 「切走即静默」的省电效果。这是采集功能本身的语义，不是实现疏忽。
    /// </para>
    /// </summary>
    public async Task PollAcquisitionGlobalsAsync(
        ModbusTcpClient modbus, IReadOnlyList<GlobalVariable> variables, CancellationToken token)
    {
        List<GlobalReadBlock> plan = GlobalReadPlan.Build(variables);
        if (plan.Count == 0)
        {
            return;
        }

        // stillWanted 恒为 true：唯一的退出条件是最外层的 token 取消
        var results = await ReadGlobalBlocksAsync(modbus, plan, token, () => true);

        if (results.Count == 0)
        {
            return;
        }

        await _host.RunOnUiAsync(() =>
        {
            if (!_host.IsCurrentConnection(modbus, token))
            {
                return;
            }

            ApplyGlobalBlocks(results);
        });
    }

    /// <summary>
    /// 读扫码器**触发表达式**引用的全局变量，写回 <c>GlobalVariable.Value</c>（2026-11-11）。
    /// <para>
    /// 与 <see cref="PollAcquisitionGlobalsAsync"/> 同一口径：**不看页面可见性、每拍都读**。
    /// 扫码器判定节拍常驻，触发条件里的全局变量若跟着页面停采，
    /// 表达式会一直拿初始值求值，触发沿永远等不到，而界面不报任何错。
    /// </para>
    /// <para>
    /// 单块失败不断线、保留上次值 —— 与另外两个入口一致（见 <see cref="ReadGlobalBlocksAsync"/>）。
    /// </para>
    /// </summary>
    public async Task PollScannerGlobalsAsync(
        ModbusTcpClient modbus, IReadOnlyList<GlobalVariable> variables, CancellationToken token)
    {
        List<GlobalReadBlock> plan = GlobalReadPlan.Build(variables);
        if (plan.Count == 0)
        {
            return;
        }

        // stillWanted 恒为 true：唯一退出条件是外层 token 取消
        var results = await ReadGlobalBlocksAsync(modbus, plan, token, () => true);

        if (results.Count == 0)
        {
            return;
        }

        await _host.RunOnUiAsync(() =>
        {
            if (!_host.IsCurrentConnection(modbus, token))
            {
                return;
            }

            ApplyGlobalBlocks(results);
        });
    }

    /// <summary>
    /// 逐块读报文。三个「采全局变量」的入口共用，差异只在
    /// <paramref name="stillWanted"/>：状态机那路要每块复查页面，另外两路不用。
    /// </summary>
    private async Task<List<(GlobalReadBlock Block, bool[]? Coils, ushort[]? Registers, string? Error)>>
        ReadGlobalBlocksAsync(
            ModbusTcpClient modbus,
            List<GlobalReadBlock> plan,
            CancellationToken token,
            Func<bool> stillWanted)
    {
        var results = new List<(GlobalReadBlock Block, bool[]? Coils, ushort[]? Registers, string? Error)>();

        foreach (GlobalReadBlock block in plan)
        {
            if (!stillWanted())
            {
                // 返回已读到的部分：已经发出去的报文不必白读，但不再继续发新的
                return results;
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
            catch (ModbusResponseException ex)
            {
                // 合法异常码 = 链路仍在、从站拒绝这一条请求。跳过本块、继续读下一块。
                // ⚠️ 这里**只捕 ModbusResponseException**，理由同 PollFreeMonitorAsync：
                // catch (Exception) 会把 IOException（客户端已被 Dispose 的断线）吞成行内错误，
                // 让连接已死却不断线。断线必须上抛到 PollLoopAsync 的统一分支。
                results.Add((block, null, null, _host.ClassifyDisconnectReason(ex.Message)));
            }
        }

        return results;
    }

    /// <summary>回填已读到的块。出错的那块跳过 —— 保留上次值，见 <see cref="PollMachineGlobalsAsync"/>。</summary>
    private void ApplyGlobalBlocks(
        List<(GlobalReadBlock Block, bool[]? Coils, ushort[]? Registers, string? Error)> results)
    {
        foreach (var (block, coils, regs, error) in results)
        {
            if (error != null)
            {
                continue;
            }

            GlobalReadPlan.Apply(block, coils, regs, _host.MonitorSet.ByteOrders);
        }
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
