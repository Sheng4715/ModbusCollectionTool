using System;
using System.Collections.Generic;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 采集数据项「来源变量」的解析结果（P3.1）。
    /// <para>
    /// 来源变量（数据写入任务里「值从哪来」）**既可以是全局变量，也可以是系统变量**
    /// （用户定案，2026-10-26）。而「写入变量」只能是全局变量 —— 它要落到 PLC 地址上，
    /// 系统变量根本没有地址。两列口径不同，故各走各的解析。
    /// </para>
    /// <para>
    /// 纯逻辑、不引用 WPF：界面用它出提示与红字，执行侧用它取值，
    /// 离线断言也能直接链进来。
    /// </para>
    /// </summary>
    public sealed class AcquisitionSource
    {
        /// <summary>变量名（系统变量带 <c>sys_</c> 前缀）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>数据类型。</summary>
        public DataType Type { get; init; } = DataType.Int;

        /// <summary>类型显示名（<c>BOOL</c> / <c>INT</c> / <c>STRING[9]</c> …）。</summary>
        public string TypeDisplayName { get; init; } = string.Empty;

        /// <summary>注释（系统变量取清单里的说明，全局变量取用户填的注释）。</summary>
        public string Comment { get; init; } = string.Empty;

        /// <summary>
        /// 值的**显示格式**（决定文本怎么读回数值）。
        /// <para>
        /// 全局变量用它自己配的格式（可能是 Hex / Bin），系统变量没有格式配置，
        /// 取该类型的默认格式（<see cref="DataTypes"/>）。写入时必须按**这个**格式
        /// 解析文本 —— 一个 INT/Hex 的变量，它的值是 <c>"2A"</c> 而不是 <c>42</c>，
        /// 按十进制读会解析失败。
        /// </para>
        /// </summary>
        public DisplayFormat Format { get; init; } = DisplayFormat.Dec;

        /// <summary>
        /// 是不是系统变量。
        /// <para>界面要据此区分提示语（「全局变量表里没有」对系统变量是误导）。</para>
        /// </summary>
        public bool IsSystemVariable { get; init; }

        /// <summary>
        /// 按名字解析来源变量：**先查系统变量，再查全局变量**。
        /// </summary>
        /// <remarks>
        /// 顺序不是随意的：系统变量名固定 <c>sys_</c> 前缀，而全局变量名同样允许下划线，
        /// 理论上可以造出一个叫 <c>sys_True</c> 的全局变量。**同名时以系统变量为准** ——
        /// 系统变量是程序算出来的确定值，让它输给用户变量会让「sys_True 恒为真」这条
        /// 对外承诺失效。全局变量那边另有名称校验，两套清单不该撞车。
        /// </remarks>
        /// <returns>解析成功返回实例；名字为空或两处都找不到返回 <c>null</c>。</returns>
        public static AcquisitionSource? Resolve(
            string? name,
            Func<string, GlobalVariable?>? findGlobal)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (SystemVariable.Find(name) is { } system)
            {
                return new AcquisitionSource
                {
                    Name = system.Name,
                    Type = system.Type,
                    TypeDisplayName = system.TypeName,
                    Comment = system.Comment,
                    Format = DataTypes.Get(system.Type).DefaultFormat,
                    IsSystemVariable = true,
                };
            }

            GlobalVariable? variable = findGlobal?.Invoke(name);
            if (variable == null)
            {
                return null;
            }

            return new AcquisitionSource
            {
                Name = variable.Name,
                Type = variable.Type,
                TypeDisplayName = variable.TypeDisplayName,
                Comment = variable.Comment,
                Format = variable.Format,
                IsSystemVariable = false,
            };
        }

        /// <summary>
        /// 取来源变量的**当前值**（显示格式下的文本）。
        /// <para>
        /// 两类变量的值来自完全不同的地方：全局变量的值由采集引擎写进
        /// <see cref="GlobalVariable.Value"/>；系统变量的值是**算出来的**，
        /// 要由调用方传入当时的快照（<see cref="SystemVariableValues.Capture"/>）。
        /// </para>
        /// </summary>
        /// <returns>取不到返回 <c>null</c>（调用方决定写成空串还是报错）。</returns>
        public static string? ValueOf(
            string? name,
            Func<string, GlobalVariable?>? findGlobal,
            IReadOnlyDictionary<string, string>? systemValues)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (SystemVariable.Find(name) != null)
            {
                return systemValues != null && systemValues.TryGetValue(name, out string? value)
                    ? value
                    : null;
            }

            return findGlobal?.Invoke(name)?.Value;
        }
    }
}
