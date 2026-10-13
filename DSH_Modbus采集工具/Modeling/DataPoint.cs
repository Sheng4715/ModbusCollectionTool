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

        /// <summary>备注（可选，用户自定义）</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>解析后的 Modbus 绝对地址；未解析时为 −1。**不持久化**（由驱动重新解析）。</summary>
        [JsonIgnore]
        public int Address { get; set; } = -1;

        /// <summary>所属存储区；未解析时为 <see cref="ModbusArea.Coil"/>。**不持久化**。</summary>
        [JsonIgnore]
        public ModbusArea Area { get; set; } = ModbusArea.Coil;

        /// <summary>格式化的显示名，如 <c>HD100</c> / <c>D810</c></summary>
        [JsonIgnore]
        public string DisplayName => Element + Index;

        /// <summary>类型显示名，如 <c>DINT</c>；字符串带长度，如 <c>STRING[9]</c></summary>
        [JsonIgnore]
        public string TypeDisplayName => Type == DataType.String
            ? $"STRING[{Length}]"
            : DataTypes.Get(Type).DisplayName;

        /// <summary>本项需要读取的寄存器（或线圈）个数。</summary>
        [JsonIgnore]
        public int RawCount => Type == DataType.Bool
            ? 1
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

        public static List<DataPoint> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return new List<DataPoint>();
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<DataPoint>();
                }

                var list = JsonSerializer.Deserialize<List<DataPoint>>(json, Options);
                return list ?? new List<DataPoint>();
            }
            catch
            {
                // 与 LoadComments 一致：读失败不阻断启动，视为空集合
                return new List<DataPoint>();
            }
        }

        public static bool Save(IEnumerable<DataPoint> points)
        {
            try
            {
                string json = JsonSerializer.Serialize(points, Options);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

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

            // BOOL 只能在线圈区，其余只能在寄存器区
            ModbusArea[] candidates = spec.CoilOnly
                ? new[] { ModbusArea.Coil }
                : new[] { ModbusArea.HoldingRegister, ModbusArea.InputRegister };

            foreach (ModbusArea area in candidates)
            {
                if (!driver.TryResolve(p.DisplayName, area, out int addr, out string e))
                {
                    error = e;
                    continue;
                }

                // 整项须完整落在同一地址区段内（跨段会在读取时踩到空隙）
                AddressArea? owner = driver.FindArea(area, addr);
                if (owner == null)
                {
                    error = $"{p.DisplayName} 不在任何地址区内";
                    continue;
                }

                int need = spec.CoilOnly ? 1 : spec.RegistersForLength(p.Length);
                int lastAddr = addr + need - 1;
                if (owner.TryGetIndex(lastAddr, out _) == false)
                {
                    error = $"{p.DisplayName} 声明 {spec.DisplayName} 需占 {need} 个单元，末地址超出 {owner.Element} 区段";
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

            return false;
        }
    }
}
