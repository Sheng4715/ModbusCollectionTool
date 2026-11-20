namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>与页面无关的健康检查，复用连接内的串行请求锁。</summary>
    public static class ModbusHeartbeat
    {
        public static async Task RunAsync(IModbusClient client, CommDriver driver,
            Func<string?, Task> reportWarning, CancellationToken token)
        {
            // 心跳地址不单独配置，直接取自驱动地址表：**地址最小**的线圈区起始地址。
            // 现有驱动（信捷 M 区 / 汇川 M 区）均为地址 0，即原先硬编码的 M0 —— 报文与行为完全一致。
            // 好处：新增品牌零配置；未导入 IO 表时同样有地址可用（原先会退化为「未启用心跳」）。
            AddressArea? coil = driver.Areas
                .Where(a => a.Area == ModbusArea.Coil)
                .OrderBy(a => a.StartAddress)
                .FirstOrDefault();

            if (coil == null)
            {
                // 地址表异常时明确提示，而不是让 First() 抛异常 —— 抛出去会被上层当成通讯故障并断开连接。
                await reportWarning("未启用心跳：驱动地址表里没有线圈区");
                return;
            }

            // StartAddress 本身就是 Modbus 十进制地址，按定义落在 ushort 范围内。
            ushort address = (ushort)coil.StartAddress;
            string target = $"{coil.Element} 区起始地址 {address}";

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
                }
                catch (ModbusResponseException ex)
                {
                    warning = ex.ExceptionCode is 1 or 2
                        ? $"心跳无效（{target}）：{ex.Message}，请检查驱动地址表"
                        : $"心跳响应异常（{target}）：{ex.Message}";
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
