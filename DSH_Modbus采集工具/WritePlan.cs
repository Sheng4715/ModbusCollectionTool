using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 一次写入的报文参数：线圈写 <see cref="CoilValue"/>（FC05），
    /// 寄存器写 <see cref="Registers"/>（长度 1 走 FC06，否则 FC16）。
    /// <para>
    /// 独立成文件（原先嵌在 <c>MonitorItem.cs</c> 末尾）的理由：它是**纯数据**、
    /// 不碰 WPF，而现在有两条写入路径共用它 —— 自由监控
    /// （<c>MonitorItemSet.TryBuildWrite</c>）与采集数据写入任务
    /// （<c>AcquisitionWrite.TryBuild</c>）。
    /// 留在 WPF 文件里会让离线断言工程无法链接。
    /// </para>
    /// </summary>
    public struct WritePlan
    {
        public ModbusArea Area { get; init; }

        public int Address { get; init; }

        public bool IsCoil { get; init; }

        /// <summary>线圈目标值（仅 <see cref="IsCoil"/> 为 true 时有效）。</summary>
        public bool CoilValue { get; init; }

        /// <summary>寄存器值（仅 <see cref="IsCoil"/> 为 false 时有效）；长度 1 走 FC06，否则 FC16。</summary>
        public ushort[]? Registers { get; init; }
    }
}
