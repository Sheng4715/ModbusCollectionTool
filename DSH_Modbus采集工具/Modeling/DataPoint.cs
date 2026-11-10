using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSH_Modbus采集工具.Drivers;
namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 一条「用户声明的监控项」：某个元件地址按某种类型与格式解释。
    /// <para>
    /// 这是 P4-2（自由监控）与 P3-1（采集导出）共用的持久化单元 ——
    /// 两处要声明的东西完全一致，故只做一个模型。
    /// </para>
    /// </summary>
    public sealed class DataPoint
    {
        /// <summary>序号（仅用于界面排序与稳定标识，不参与语义）</summary>
        public int Id { get; set; }

        /// <summary>元件符号，如 <c>D</c>、<c>HD</c>、<c>M</c></summary>
        public string Element { get; set; } = string.Empty;

        /// <summary>元件编号（按该元件区的进制解释）；字符串形式以兼容八进制 X/Y</summary>
        public string Index { get; set; } = string.Empty;

        /// <summary>数据类型</summary>
        public DataType Type { get; set; } = DataType.Int;

        /// <summary>显示格式（须在 <see cref="DataTypes.Get"/> 的 AllowedFormats 内）</summary>
        public DisplayFormat Format { get; set; } = DisplayFormat.Dec;

        /// <summary>仅 <c>STRING</c> 使用：用户声明的字符数</summary>
        public int Length { get; set; }

        /// <summary>
        /// 仅 <c>ARRAY</c> 使用：**元素类型**（BOOL / INT / UINT / DINT / UDINT / REAL，不含 STRING）。
        /// <para>
        /// 与 <see cref="Type"/> 分开存：<see cref="Type"/> 是「ARRAY」这件事实，
        /// 元素怎么解释由本字段决定。合用一个字段就得靠字符串编码（如 <c>"ARRAY&lt;INT&gt;"</c>），
        /// json 一被手改就解析不出来了。
        /// </para>
        /// <para>
        /// 数组的 <see cref="Length"/> 解释为**元素个数**（1~128），不是字符数。
        /// </para>
        /// </summary>
        public DataType ElementType { get; set; } = DataType.Int;

        /// <summary>备注（可选，用户自定义）</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>解析后的 Modbus 绝对地址；未解析时为 −1。**不持久化**（由驱动重新解析）。</summary>
        [JsonIgnore]
        public int Address { get; set; } = -1;

        /// <summary>所属存储区；未解析时为 <see cref="ModbusArea.Coil"/>。**不持久化**。</summary>
        [JsonIgnore]
        public ModbusArea Area { get; set; } = ModbusArea.Coil;

        /// <summary>
        /// 全局变量名；非空 = 本项监控的是该变量（此时 <see cref="Element"/> /
        /// <see cref="Index"/> 不使用）。
        /// <para>
        /// 为什么不复用 <see cref="Element"/> 存变量名：元件与变量名是两个不同命名空间，
        /// 混在一个字段里，读代码的人无法判断「D100」是元件还是恰好叫这个名字的变量。
        /// </para>
        /// <para>
        /// 类型 / 长度 / 地址**不从这里取** —— 每次解析都从变量表现取，
        /// 变量改了类型，监控行自动跟着变，不会留一份过期副本。
        /// </para>
        /// </summary>
        public string VariableName { get; set; } = string.Empty;

        /// <summary>是否监控的是全局变量（而非直接的元件地址）。</summary>
        [JsonIgnore]
        public bool IsVariable => !string.IsNullOrWhiteSpace(VariableName);

        /// <summary>格式化的显示名，如 <c>HD100</c> / <c>D810</c></summary>
        [JsonIgnore]
        public string DisplayName => Element + Index;

        /// <summary>
        /// 类型显示名，如 <c>DINT</c>；字符串带长度，如 <c>STRING[9]</c>；
        /// 数组写成 <c>INT[20]</c>（用户 2026-11-09 定案：元素类型 + 元素个数，方括号）。
        /// <para>
        /// 与 <c>STRING[n]</c> 同一套方括号写法，一眼能看出「里面是几个」。
        /// </para>
        /// </summary>
        [JsonIgnore]
        public string TypeDisplayName => Type switch
        {
            DataType.String => $"STRING[{Length}]",
            DataType.Array => $"{DataTypes.Get(ElementType).DisplayName}[{Length}]",
            _ => DataTypes.Get(Type).DisplayName,
        };

        /// <summary>本项需要读取的寄存器（或线圈）个数。</summary>
        [JsonIgnore]
        public int RawCount => Type == DataType.Bool
            ? 1
            : Type == DataType.Array
                ? DataTypes.ArrayUnitsOf(ElementType, 0, Length)
                : DataTypes.Get(Type).RegistersForLength(Length);
    }

    /// <summary>监控项集合的持久化（<c>data-points.json</c>，与 <c>io-comments.json</c> 同模式）。</summary>
    public static class DataPointStore
    {
        private const string FileName = "data-points.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // 枚举写成可读名字而非数字：JSON 是给人看/手改的，
            // 数字下标在枚举顺序变化时会静默错位（如 Type=3 从 DINT 变成别的）
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<DataPoint> Load() => AtomicFile.ReadList<DataPoint>(FilePath, Options);

        public static bool Save(IEnumerable<DataPoint> points) =>
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(points, Options));

        /// <summary>
        /// 用驱动校验并解析一项的地址。成功时回填 <see cref="DataPoint.Address"/> /
        /// <see cref="DataPoint.Area"/> 并返回 true。
        /// <para>
        /// 校验内容：元件是否在驱动中定义、编号是否越界、类型与存储区是否匹配
        /// （<c>BOOL</c> ↔ 线圈，其余 ↔ 寄存器）、格式是否被该类型允许、
        /// <c>STRING</c> 长度是否为正。
        /// </para>
        /// </summary>
        public static bool TryResolve(DataPoint p, CommDriver driver, out string error)
        {
            error = string.Empty;

            if (p == null || driver == null)
            {
                error = "参数为空";
                return false;
            }

            DataTypeSpec spec = DataTypes.Get(p.Type);

            if (!spec.AllowsFormat(p.Format))
            {
                error = $"{spec.DisplayName} 不支持所选显示格式";
                return false;
            }

            if (spec.HasVariableLength && p.Length <= 0)
            {
                error = "STRING 长度必须大于 0";
                return false;
            }

            // 数组：元素类型与元素个数都要单独校验（长度对数组 = 元素个数）
            if (spec.IsArray
                && !ArrayRules.TryValidate(p.ElementType, p.Length, out error))
            {
                return false;
            }

            // BOOL 只能在线圈区，其余只能在寄存器区。
            // ⚠️ 数组要看**元素类型**：ARRAY<BOOL> 占 N 个线圈，ARRAY<INT> 占 N 个寄存器。
            bool coilOnly = spec.IsCoilOnlyFor(p.ElementType);
            ModbusArea[] candidates = coilOnly
                ? new[] { ModbusArea.Coil }
                : new[] { ModbusArea.HoldingRegister, ModbusArea.InputRegister };

            foreach (ModbusArea area in candidates)
            {
                if (!driver.TryResolve(p.DisplayName, area, out int addr, out string e))
                {
                    // ⚠️ 只在还没有更具体的原因时才记录。
                    // 候选区是「保持寄存器 → 输入寄存器」两个，同一元件通常只在前者里定义：
                    // 若后者失败就无条件覆盖，会把前者算出的「末地址超出区段」这类真原因
                    // 冲成「驱动未定义元件 D」——而 D 明明存在，用户会被引向错误方向。
                    if (string.IsNullOrEmpty(error))
                    {
                        error = e;
                    }

                    continue;
                }

                // 整项须完整落在同一地址区段内（跨段会在读取时踩到空隙）
                AddressArea? owner = driver.FindArea(area, addr);
                if (owner == null)
                {
                    error = $"{p.DisplayName} 不在任何地址区内";
                    continue;
                }

                // 线圈：BOOL 占 1 个，ARRAY<BOOL> 占 N 个（N = 元素个数）；
                // 寄存器：按类型 / 长度 / 元素个数算。
                int need = coilOnly
                    ? (spec.IsArray ? p.Length : 1)
                    : spec.UnitsFor(p.Length, p.ElementType);
                int lastAddr = addr + need - 1;
                if (owner.TryGetIndex(lastAddr, out _) == false)
                {
                    error = $"{p.DisplayName} 声明 {p.TypeDisplayName} 需占 {need} 个单元，末地址超出 {owner.Element} 区段";
                    continue;
                }

                p.Address = addr;
                p.Area = area;
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                error = $"{p.DisplayName} 无法解析";
            }
            else if (error.Contains("驱动未定义元件"))
            {
                // 元件确实存在、只是存储区与声明类型对不上时，TryResolve 只会说「未定义元件」——
                // 这个提示会把用户引向「符号打错了」，而真正的问题是类型选错了。
                // 方案 2 的元件是自由文本、类型由用户另选，出现这种组合的概率比方案 1 高得多。
                IReadOnlyList<ModbusArea> actual = driver.AreasOfElement(ExtractElement(p.DisplayName));
                if (actual.Count > 0)
                {
                    error = actual.Contains(ModbusArea.Coil)
                        ? $"{p.DisplayName} 是线圈元件，只能声明为 BOOL（BIN）；当前为 {spec.DisplayName}"
                        : $"{p.DisplayName} 是寄存器元件，不能声明为 {spec.DisplayName}；请改用 INT / DINT / REAL / STRING";
                }
            }

            return false;
        }

        /// <summary>从完整元件名里取出符号部分（"HD100" → "HD"）。取不出时返回原串。</summary>
        private static string ExtractElement(string displayName)
        {
            int i = 0;
            while (i < displayName.Length && char.IsLetter(displayName[i]))
            {
                i++;
            }

            return i == 0 ? displayName : displayName[..i];
        }
    }
}
