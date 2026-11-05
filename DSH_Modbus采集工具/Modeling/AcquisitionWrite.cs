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

        /// <summary>该写但**只写内存**（写入变量没有映射地址）—— 不算失败，照常更新变量值。</summary>
        CannotAddress = 1,

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

            // 用户定案（2026-10-27，2026-11-05 修订）：未映射地址的写入变量**不算配置错误**，
            // 也不该被忽略 —— 它照样有「值」，只是没有寄存器可发。所以这里**继续往下编码**，
            // 由调用方按 CannotAddress 只更新内存值、不发报文。
            // ⚠️ 早期这里是「直接 return Skipped」、连编码都不做，导致
            // 「写入变量没地址 → 整项静默跳过 → 变量值永远不变」，
            // 表面上日志写着「执行完成」、实际什么都没发生（实测踩过）。
            bool hasAddress = target.HasAddress;

            if (hasAddress && target.ResolvedAddress < 0)
            {
                error = target.ResolveError.Length > 0 ? target.ResolveError : "映射地址无法解析";
                return AcquisitionWriteStatus.Failed;
            }

            // 只读区兜底：配置检查（AcquisitionTaskCheck）已经在界面上把这种组拦成
            // 「配置错误」，正常走不到这里。留着是给旧数据 / 手改 json 的情况兜底 ——
            // 绝不能把写报文打到只读区去。
            // ⚠️ 只对**有地址**的变量生效：内存变量不落 PLC，谈不上只读区。
            if (hasAddress && target.IsReadOnly)
            {
                error = $"{target.Address} 属于只读区（输入元件），不能写入";
                return AcquisitionWriteStatus.Failed;
            }

            AcquisitionWriteStatus ready = hasAddress
                ? AcquisitionWriteStatus.Ready
                : AcquisitionWriteStatus.CannotAddress;

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
                return ready;
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
            return ready;
        }

        /// <summary>
        /// 把一次编码结果回填到变量的**内存值**（不碰 PLC）。
        /// <para>
        /// 用在两处：① 写入变量**没有映射地址**时（<see cref="AcquisitionWriteStatus.CannotAddress"/>），
        /// 只有内存可写；② 内存变量在 PLC 未连接时的兜底。
        /// </para>
        /// <para>
        /// ⚠️ **`Value` 与 `Numeric` 必须一起设**：`Value` 是按显示格式转好的文本
        /// （INT/Hex 的变量值是 <c>"2A"</c> 而非 <c>"42"</c>），实时卡片与表达式都读它；
        /// `Numeric` 是十进制数值形态，实时卡片显示数字时**不能**从 `Value` 反解
        /// （见 <c>GlobalReadPlan.Apply</c> 的同款注释）。
        /// </para>
        /// </summary>
        /// <param name="target">要回填的变量</param>
        /// <param name="plan">已编码好的报文参数（<c>Registers</c> 或 <c>CoilValue</c> 有效）</param>
        /// <param name="orders">字节序</param>
        public static void ApplyToMemory(GlobalVariable target, WritePlan plan, ByteOrderSettings orders)
        {
            if (target == null)
            {
                return;
            }

            if (plan.IsCoil)
            {
                target.Value = ValueCodec.FormatCoil(plan.CoilValue);
                target.Numeric = plan.CoilValue ? 1d : 0d;
                return;
            }

            ushort[] registers = plan.Registers ?? Array.Empty<ushort>();
            target.Value = ValueCodec.FormatRegisters(
                target.Type, registers, target.Format, orders, target.Length);
            target.Numeric = ValueCodec.DecodeNumber(target.Type, registers, orders);
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
