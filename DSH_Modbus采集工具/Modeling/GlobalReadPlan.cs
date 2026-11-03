using System;
using System.Collections.Generic;
using System.Linq;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Modeling;

/// <summary>
/// 全局变量读取计划里的一个变量：在所属块内的偏移与占用的读取单元数。
/// <para>与 <c>ReadEntry</c>（自由监控）同构，但载荷是 <see cref="GlobalVariable"/>，
/// 因此能放进本层做离线断言（自由监控的 <c>MonitorItem</c> 是界面包装，引 WPF）。</para>
/// </summary>
public readonly struct GlobalReadEntry
{
    public GlobalReadEntry(GlobalVariable variable, int offset, int units)
    {
        Variable = variable;
        Offset = offset;
        Units = units;
    }

    public GlobalVariable Variable { get; }

    /// <summary>相对 <see cref="GlobalReadBlock.StartAddress"/> 的偏移。</summary>
    public int Offset { get; }

    /// <summary>本变量占用的单元数：线圈恒 1，寄存器按类型与 STRING 长度算。</summary>
    public int Units { get; }
}

/// <summary>一块可以一次报文读完的全局变量：同存储区、地址连续、不超单次上限。</summary>
public sealed class GlobalReadBlock
{
    public ModbusArea Area { get; init; }

    public int StartAddress { get; init; }

    public int Count { get; init; }

    public IReadOnlyList<GlobalReadEntry> Entries { get; init; } = Array.Empty<GlobalReadEntry>();
}

/// <summary>
/// 「按可见性采集全局变量」的读计划与回填（P1.2 / P3.3，2026-10-21）。
/// <para>
/// 背景：状态机表达式可以引用全局变量，但全局变量原本**没有任何采集** ——
/// <c>GlobalVariable.Value</c> 只在启动/新增时被重置为初始值，此后永不更新。
/// 于是 <c>SysState == 0</c> 这类表达式恒真、其余恒假，而界面不报任何错。
/// </para>
/// <para>
/// 采集策略与 IO 监控**同一套**：只采当前可见页面需要的那几个变量，离开即停。
/// 所以本类不负责「什么时候采」，只负责「采哪些、怎么拼报文、怎么回填」。
/// </para>
/// <para>
/// 不引用 WPF：回填目标是 <see cref="GlobalVariable.Value"/>，与自由监控的
/// <c>MonitorItem</c> 无关，因此可链接进 <c>ModelingCheck</c> 做离线断言。
/// </para>
/// </summary>
public static class GlobalReadPlan
{
    /// <summary>单次报文最多读取的单元数（线圈/寄存器分别有 2000 上限，取小者统一约束）。</summary>
    public const int MaxUnitsPerRead = 2000;

    /// <summary>
    /// 按存储区分组、按地址排序，把**紧邻或重叠**的变量并成一块。
    /// <para>
    /// 只收<b>能读</b>的变量：关联了地址、解析成功、没有解析错误。
    /// 内存变量（无地址）与解析失败的变量不产生报文 —— 地址未知，发了也没意义。
    /// </para>
    /// </summary>
    public static List<GlobalReadBlock> Build(IEnumerable<GlobalVariable>? variables)
    {
        var plan = new List<GlobalReadBlock>();
        if (variables == null)
        {
            return plan;
        }

        List<GlobalVariable> readable = variables
            .Where(v => v is not null)
            .Where(v => v.HasAddress && v.ResolvedAddress >= 0 && v.ResolveError.Length == 0)
            .ToList();

        if (readable.Count == 0)
        {
            return plan;
        }

        foreach (var areaGroup in readable.GroupBy(v => v.Area))
        {
            // 区内按地址排序后顺序扫描，地址不连续的断开成新块
            List<GlobalVariable> ordered = areaGroup.OrderBy(v => v.ResolvedAddress).ToList();
            var current = new List<(GlobalVariable Variable, int Units)>();

            foreach (GlobalVariable v in ordered)
            {
                int start = v.ResolvedAddress;
                int units = Math.Max(1, v.RawCount);

                if (current.Count > 0)
                {
                    int curEnd = EndOf(current);
                    int blockStart = current.Min(c => c.Variable.ResolvedAddress);
                    bool adjacent = start <= curEnd;
                    bool withinLimit = (Math.Max(curEnd, start + units) - blockStart) <= MaxUnitsPerRead;

                    if (!adjacent || !withinLimit)
                    {
                        plan.Add(MakeBlock(areaGroup.Key, blockStart, current));
                        current = new List<(GlobalVariable, int)>();
                    }
                }

                current.Add((v, units));
            }

            if (current.Count > 0)
            {
                plan.Add(MakeBlock(areaGroup.Key, current.Min(c => c.Variable.ResolvedAddress), current));
            }
        }

        return plan;
    }

    /// <summary>
    /// 把一次读到的原始单元回填到块内各变量的 <see cref="GlobalVariable.Value"/>。
    /// <para>
    /// 逐项 try/catch：某一项解码失败只跳过该项、保留它上一次的值 ——
    /// 宁可显示旧值，也不要把变量悄悄清成 0（那会让表达式算出一个看起来正常的结果）。
    /// </para>
    /// </summary>
    public static void Apply(
        GlobalReadBlock block, bool[]? coils, ushort[]? registers, ByteOrderSettings orders)
    {
        foreach (GlobalReadEntry e in block.Entries)
        {
            try
            {
                if (block.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
                {
                    if (coils == null || e.Offset >= coils.Length)
                    {
                        continue;
                    }

                    bool on = coils[e.Offset];
                    e.Variable.Value = ValueCodec.FormatCoil(on);

                    // 数值形态：实时卡片显示 TRUE/FALSE 靠它（见 AcquisitionLiveText）
                    e.Variable.Numeric = on ? 1d : 0d;
                }
                else
                {
                    if (registers == null || e.Offset + e.Units > registers.Length)
                    {
                        continue;
                    }

                    ReadOnlySpan<ushort> slice = registers.AsSpan(e.Offset, e.Units);
                    e.Variable.Value = ValueCodec.FormatRegisters(
                        e.Variable.Type, slice, e.Variable.Format, orders, e.Variable.Length);

                    // 顺手存一份数值：实时卡片一律十进制，不能从 Value 反解（见 Numeric 的注释）
                    e.Variable.Numeric = ValueCodec.DecodeNumber(e.Variable.Type, slice, orders);
                }
            }
            catch (Exception)
            {
                // 解码失败保留上次值（见方法注释）
            }
        }
    }

    private static int EndOf(List<(GlobalVariable Variable, int Units)> entries)
    {
        int end = 0;
        foreach (var e in entries)
        {
            int e2 = e.Variable.ResolvedAddress + e.Units;
            if (e2 > end)
            {
                end = e2;
            }
        }

        return end;
    }

    private static GlobalReadBlock MakeBlock(
        ModbusArea area, int start, List<(GlobalVariable Variable, int Units)> entries)
    {
        int end = start;
        foreach (var e in entries)
        {
            int e2 = e.Variable.ResolvedAddress + e.Units;
            if (e2 > end)
            {
                end = e2;
            }
        }

        return new GlobalReadBlock
        {
            Area = area,
            StartAddress = start,
            Count = end - start,
            Entries = entries
                .Select(e => new GlobalReadEntry(e.Variable, e.Variable.ResolvedAddress - start, e.Units))
                .ToList(),
        };
    }
}
