using System;
using System.Collections.Generic;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 采集数据「写入任务」里一个数据项的落盘判定结果。
    /// </summary>
    public enum AcquisitionWriteStatus
    {
        /// <summary>可以写，<c>plan</c> 有效。</summary>
        Ready = 0,

        /// <summary>该写但不用写（写入变量没有映射地址）—— **不算失败**，静默跳过。</summary>
        Skipped = 1,

        /// <summary>写不了，<c>error</c> 是给用户看的原因。</summary>
        Failed = 2,
    }

    /// <summary>
    /// 采集数据「写入任务」的编码层（P3.1）：把「来源变量的文本 + 写入变量的地址」
    /// 变成一份 <see cref="WritePlan"/>，交给 <c>WriteToDeviceAsync</c> 下发。
    /// <para>
    /// 纯逻辑、不引用 WPF：界面与执行侧共用同一套判定，离线断言可以直接链进来。
    /// </para>
    /// <para>
    /// ⚠️ 编码规则（这一条错了会静默写错值）：**用「来源变量」的显示格式解析文本，
    /// 用「写入变量」的类型与长度决定位宽、范围与寄存器数**。两者的**类型**已由
    /// <see cref="AcquisitionTaskRules.WriteTypesMatch"/> 强制相同，唯一可能不同的是
    /// 显示格式 —— 一个 INT 变量的值是 <c>"2A"</c>（Hex）还是 <c>"42"</c>（Dec）取决于它自己的配置，
    /// 按十进制去解析 <c>"2A"</c> 会直接失败。
    /// </para>
    /// </summary>
    public static class AcquisitionWrite
    {
        /// <summary>
        /// 判定一个数据项能不能写、写什么。
        /// </summary>
        /// <param name="target">写入变量（全局变量；没映射地址时为 <c>null</c> 属正常）</param>
        /// <param name="sourceText">来源变量的当前值文本（显示格式下）</param>
        /// <param name="sourceFormat">来源变量的显示格式，用来解析 <paramref name="sourceText"/></param>
        /// <param name="orders">字节序（寄存器编码用）</param>
        /// <param name="plan">成功时的报文参数</param>
        /// <param name="error">失败原因（可直接拼进日志）</param>
        public static AcquisitionWriteStatus TryBuild(
            GlobalVariable? target,
            string? sourceText,
            DisplayFormat sourceFormat,
            ByteOrderSettings orders,
            out WritePlan plan,
            out string error)
        {
            plan = default;
            error = string.Empty;

            if (target == null)
            {
                error = "写入变量不存在";
                return AcquisitionWriteStatus.Failed;
            }

            // 用户定案（2026-10-27）：未映射地址的写入变量**不算配置错误**，
            // 只是没有地方可写 —— 静默跳过，既不报错也不写。
            if (!target.HasAddress)
            {
                return AcquisitionWriteStatus.Skipped;
            }

            if (target.ResolvedAddress < 0)
            {
                error = target.ResolveError.Length > 0 ? target.ResolveError : "映射地址无法解析";
                return AcquisitionWriteStatus.Failed;
            }

            // 只读区兜底：新版全局变量表已经在映射地址时拦掉了只读元件，
            // 这里再挡一次是为了旧数据（改版前存的 X 变量）不被打到总线上。
            if (target.IsReadOnly)
            {
                error = $"{target.Address} 属于只读区，不能写入";
                return AcquisitionWriteStatus.Failed;
            }

            DataTypeSpec spec = DataTypes.Get(target.Type);

            if (spec.CoilOnly)
            {
                string coilText = (sourceText ?? string.Empty).Trim();
                if (coilText != "0" && coilText != "1")
                {
                    error = coilText.Length == 0
                        ? "来源变量没有值"
                        : $"线圈只能写 0 或 1，来源变量给的是「{coilText}」";
                    return AcquisitionWriteStatus.Failed;
                }

                plan = new WritePlan
                {
                    Area = target.Area,
                    Address = target.ResolvedAddress,
                    IsCoil = true,
                    CoilValue = coilText == "1",
                };
                return AcquisitionWriteStatus.Ready;
            }

            // 寄存器区：STRING 按目标长度补齐，整数按目标类型校验范围
            if (!ValueCodec.TryEncodeRegisters(
                    target.Type, sourceFormat, sourceText, target.Length, orders,
                    out ushort[] registers, out error))
            {
                return AcquisitionWriteStatus.Failed;
            }

            plan = new WritePlan
            {
                Area = target.Area,
                Address = target.ResolvedAddress,
                IsCoil = false,
                Registers = registers,
            };
            return AcquisitionWriteStatus.Ready;
        }

        /// <summary>
        /// 一项失败时拼给日志看的短句：<c>项1（压力）：线圈只能写 0 或 1</c>。
        /// <para>序号永远给（当天定位用），名称也带上（事后翻日志好认）；两者重复时只给一个。</para>
        /// </summary>
        public static string DescribeItemFailure(int index, string? itemName, string error) =>
            AcquisitionTaskCheck.DescribeItemPrefix(index, itemName) + error;

        /// <summary>
        /// 一轮写入里若干项失败时的汇总：<c>2/3 项写入失败：项1（压力）：…；项3：…</c>。
        /// <para>
        /// 空清单返回空串（调用方据此判断这一轮算成功）。**不降噪**：周期式任务失败一次记一条，
        /// 用户定案（2026-10-27）先不做聚合节流 —— 少了这条就查不出什么时候开始坏的。
        /// </para>
        /// </summary>
        public static string DescribeFailure(IReadOnlyList<string>? itemFailures, int totalItems)
        {
            if (itemFailures == null || itemFailures.Count == 0)
            {
                return string.Empty;
            }

            int total = totalItems > 0 ? totalItems : itemFailures.Count;
            return $"{itemFailures.Count}/{total} 项写入失败：" + string.Join("；", itemFailures);
        }
    }
}
