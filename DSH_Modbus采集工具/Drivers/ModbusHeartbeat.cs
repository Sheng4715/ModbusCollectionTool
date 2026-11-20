namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>与页面无关的健康检查，复用连接内的串行请求锁。</summary>
    public static class ModbusHeartbeat
    {
        /// <summary>
        /// 心跳主循环：每 100 ms 醒一次，距上次有效响应满 1 s 才真正发一条读请求。
        /// <para>
        /// ⚠️ **单次链路失败不能让本循环退出**（2026-11-20 真机踩中）：调用方允许「连续几次失败才断线」，
        /// 若这里失败一次就 <c>throw</c> 出去，循环任务当场结束 —— 之后再也不会心跳，
        /// 失败次数永远凑不满阈值，表现为「设备彻底不应答，界面却一直停在已连接」。
        /// 故**由 <paramref name="tolerateFailure"/> 决定**：返回 true 就继续下一拍，返回 false 才上抛。
        /// </para>
        /// </summary>
        /// <param name="onResponded">真正收到应答时的回调（用于清零「链路级失败」计数）。</param>
        /// <param name="tolerateFailure">
        /// 可恢复失败的判定：入参是这次异常，返回 <c>true</c> 表示「这一拍失败可容忍，继续心跳」。
        /// 传 <c>null</c> 等于不容忍（第一次失败即上抛），与早期行为一致。
        /// </param>
        public static async Task RunAsync(IModbusClient client, CommDriver driver,
            Func<string?, Task> reportWarning, CancellationToken token, Action? onResponded = null,
            Func<Exception, bool>? tolerateFailure = null)
        {
            if (!TryGetHeartbeatAddress(driver, out ushort address, out string target))
            {
                // 地址表异常时明确提示，而不是让 First() 抛异常 —— 抛出去会被上层当成通讯故障并断开连接。
                await reportWarning("未启用心跳：驱动地址表里没有线圈区");
                return;
            }

            string? previousWarning = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string? warning = null;
                try
                {
                    bool sent = await client.HeartbeatIfIdleAsync(address,
                        TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    if (!sent)
                    {
                        continue;
                    }

                    // 真的发出去并收到了应答：通知调用方清零「链路级失败」计数。
                    // 少了这一步，轮询与心跳各自数失败次数会互相抵消不了 ——
                    // 曾经表现为「偶发失败累计到阈值就断线」，而每次其实都恢复了。
                    onResponded?.Invoke();
                }
                catch (ModbusResponseException ex)
                {
                    // 合法异常码：链路是通的，只是这个地址它不认 —— 不算链路失败
                    warning = ex.ExceptionCode is 1 or 2
                        ? $"心跳无效（{target}）：{ex.Message}，请检查驱动地址表"
                        : $"心跳响应异常（{target}）：{ex.Message}";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw; // 用户断开 / 关窗：正常收尾
                }
                catch (Exception ex)
                {
                    // ⚠️ 串口读超时也会以 OperationCanceledException 的形式冒出来（内部链接的 CT 先到），
                    // 所以不能用 `catch (OperationCanceledException)` 一律上抛，必须落到这里走容忍判定。
                    if (tolerateFailure != null && tolerateFailure(ex))
                    {
                        continue; // 容忍：下一拍继续心跳，计数由调用方累加
                    }

                    throw;
                }

                if (warning != previousWarning)
                {
                    token.ThrowIfCancellationRequested();
                    await reportWarning(warning).ConfigureAwait(false);
                    previousWarning = warning;
                }
            }
        }

        /// <summary>
        /// 心跳地址（也是串口连接后那次「探测读」用的地址）。
        /// <para>
        /// 心跳地址不单独配置，直接取自驱动地址表：**地址最小**的线圈区起始地址。
        /// 现有驱动（信捷 M 区 / 汇川 M 区）均为地址 0，即原先硬编码的 M0 —— 报文与行为完全一致。
        /// 好处：新增品牌零配置；未导入 IO 表时同样有地址可用（原先会退化为「未启用心跳」）。
        /// </para>
        /// </summary>
        public static bool TryGetHeartbeatAddress(CommDriver driver, out ushort address, out string describe)
        {
            AddressArea? coil = driver.Areas
                .Where(a => a.Area == ModbusArea.Coil)
                .OrderBy(a => a.StartAddress)
                .FirstOrDefault();

            if (coil == null)
            {
                address = 0;
                describe = "驱动地址表里没有线圈区";
                return false;
            }

            // StartAddress 本身就是 Modbus 十进制地址，按定义落在 ushort 范围内。
            address = (ushort)coil.StartAddress;
            describe = $"{coil.Element} 区起始地址 {address}";
            return true;
        }
    }
}
