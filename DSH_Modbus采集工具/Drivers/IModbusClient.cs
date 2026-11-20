using System;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>
    /// Modbus 主站的**传输无关**契约：TCP 与 RTU 两种实现共用。
    /// <para>
    /// **为什么要有它**：采集引擎（<c>ModbusPoller</c>）、心跳、写入路径原先都写死
    /// <c>ModbusTcpClient</c>。加串口时若不抽接口，这些地方就要么按类型分支、
    /// 要么整份复制 —— 前者会让每处都长出一个 if，后者会让两套采集逻辑慢慢走偏。
    /// </para>
    /// <para>
    /// ⚠️ **连接方法刻意不在接口里**：TCP 要 IP + 端口 + 可选本地绑定端点，
    /// RTU 要 COM 口 + 波特率 + 数据位 + 校验 + 停止位。硬塞进一个签名只能靠
    /// 传一堆可空参数的「万能连接」，那种签名既看不懂也用不对。
    /// 两种连接各自留在实现类上，由窗口按连接方式分支调用。
    /// </para>
    /// </summary>
    public interface IModbusClient : IDisposable
    {
        /// <summary>从站单元号（RTU 是站号，TCP 多数为 1）。</summary>
        byte UnitId { get; set; }

        /// <summary>当前是否处于已连接状态。</summary>
        bool IsConnected { get; }

        /// <summary>0x01 读线圈状态</summary>
        Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort count, CancellationToken ct = default);

        /// <summary>0x02 读离散量输入</summary>
        Task<bool[]> ReadDiscreteInputsAsync(ushort startAddress, ushort count, CancellationToken ct = default);

        /// <summary>0x03 读保持寄存器</summary>
        Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default);

        /// <summary>0x04 读输入寄存器</summary>
        Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default);

        /// <summary>0x05 写单个线圈</summary>
        Task WriteSingleCoilAsync(ushort address, bool on, CancellationToken ct = default);

        /// <summary>0x06 写单个保持寄存器</summary>
        Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default);

        /// <summary>0x10 写多个保持寄存器</summary>
        Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default);

        /// <summary>
        /// 空闲心跳：距上次有效响应满 <paramref name="idleInterval"/> 时才读一个线圈。
        /// 返回 <c>false</c> 表示「刚刚有过响应、本次省略」，调用方无需当作失败。
        /// </summary>
        Task<bool> HeartbeatIfIdleAsync(ushort address, TimeSpan idleInterval, CancellationToken ct);
    }
}
