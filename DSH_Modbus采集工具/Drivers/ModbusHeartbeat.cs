namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>与页面无关的健康检查，复用连接内的串行请求锁。</summary>
    public static class ModbusHeartbeat
    {
        public static async Task RunAsync(ModbusTcpClient client, CommDriver driver,
            Func<string?, Task> reportWarning, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(driver.HeartbeatCoil)
                || !driver.TryResolve(driver.HeartbeatCoil, ModbusArea.Coil, out int address, out _)
                || address is < 0 or > ushort.MaxValue)
            {
                await reportWarning("未启用心跳：请在驱动中配置有效的 HeartbeatCoil 可读线圈");
                return;
            }

            string? previousWarning = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string? warning = null;
                try
                {
                    bool sent = await client.HeartbeatIfIdleAsync((ushort)address,
                        TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    if (!sent)
                    {
                        continue;
                    }
                }
                catch (ModbusResponseException ex)
                {
                    warning = ex.ExceptionCode is 1 or 2
                        ? $"心跳配置无效（{driver.HeartbeatCoil}）：{ex.Message}，请调整驱动心跳地址/功能码"
                        : $"心跳响应异常（{driver.HeartbeatCoil}）：{ex.Message}";
                }

                if (warning != previousWarning)
                {
                    token.ThrowIfCancellationRequested();
                    await reportWarning(warning).ConfigureAwait(false);
                    previousWarning = warning;
                }
            }
        }
    }
}
