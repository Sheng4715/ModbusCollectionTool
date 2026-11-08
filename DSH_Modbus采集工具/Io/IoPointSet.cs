using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DSH_Modbus采集工具.Drivers;

namespace DSH_Modbus采集工具.Io
{
    /// <summary>
    /// IO 点位集：注释表（键 = 点位名如 "X17"、值 = 注释）与由它派生的 X / Y 点位列表，
    /// 以及注释表的落盘 / 读取。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 点位集**完全由导入的 IO 表驱动**（不再固定 64 点）：注释表是唯一权威，
    /// <see cref="Rebuild"/> 按当前通讯驱动把每个名称换算成 Modbus 地址并排序。
    /// </para>
    /// <para>
    /// 本类不含 WPF 类型，可脱离界面单测；界面绑定与可见页快照仍由窗口负责。
    /// </para>
    /// </remarks>
    internal sealed class IoPointSet
    {
        private Dictionary<string, string> _comments = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>输入（X）点位列表，按 Modbus 地址升序。</summary>
        public List<IoPoint> XPoints { get; } = new();

        /// <summary>输出（Y）点位列表，按 Modbus 地址升序。</summary>
        public List<IoPoint> YPoints { get; } = new();

        /// <summary>当前注释表（只读视图；整体替换请用 <see cref="Replace"/>）。</summary>
        public IReadOnlyDictionary<string, string> Comments => _comments;

        /// <summary>按分段取点位列表：true=输出 Y，false=输入 X。</summary>
        public List<IoPoint> PointsFor(bool yViewActive) => yViewActive ? YPoints : XPoints;

        /// <summary>io-comments.json 的完整路径（exe 旁，随程序目录）。</summary>
        public static string CommentsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "io-comments.json");

        /// <summary>
        /// 按当前注释表重建 X / Y 点位列表：归一化点位名、用当前通讯驱动换算出 Modbus 地址、
        /// 按地址升序排序。未通过驱动校验的条目忽略（导入时已过滤，此处兜底）。
        /// </summary>
        /// <param name="driver">当前通讯驱动；为 null 时结果为空列表。</param>
        public void Rebuild(CommDriver? driver)
        {
            XPoints.Clear();
            YPoints.Clear();

            foreach (string name in _comments.Keys)
            {
                if (TryCreatePoint(driver, name, out IoPoint? point))
                {
                    (point!.Name[0] == 'Y' ? YPoints : XPoints).Add(point);
                }
            }

            // 地址升序：八进制编号解析后即数值序，与轮询回填时的偏移一致
            XPoints.Sort(static (a, b) => a.Address.CompareTo(b.Address));
            YPoints.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        }

        /// <summary>整体替换注释表（导入 IO 表后调用）。</summary>
        public void Replace(Dictionary<string, string> comments) => _comments = comments;

        /// <summary>
        /// 清空整份 IO 表并落盘（切换设备类型前用，2026-11-08 用户定案）。
        /// <para>
        /// 为什么必须清：注释表是按**当前品牌**的地址表校验过的产物 ——
        /// 换品牌后同一批点位名可能越界、甚至元件根本不存在（如信捷的 X0–X77 对汇川的 X0–X1777）。
        /// 留着不删只会得到一堆静默失效的点位。
        /// </para>
        /// <returns>清掉了几个点位（0 = 本来就没导入过，不必落盘）。</returns>
        /// </summary>
        public int ClearAll()
        {
            int n = _comments.Count;
            if (n == 0)
            {
                return 0;
            }

            _comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XPoints.Clear();
            YPoints.Clear();

            Save();
            return n;
        }

        /// <summary>
        /// 把 "X17" 这类点位名转成 <see cref="IoPoint"/>：仅接受 X / Y 元件，
        /// 且地址须在当前驱动定义范围内。
        /// </summary>
        public bool TryCreatePoint(CommDriver? driver, string name, out IoPoint? point)
        {
            point = null;
            if (driver == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string normalized = NormalizePointName(name);
            if (normalized.Length < 2 || (normalized[0] != 'X' && normalized[0] != 'Y'))
            {
                return false;
            }

            if (!driver.TryResolve(normalized, ModbusArea.Coil, out int address, out _))
            {
                return false;
            }

            _comments.TryGetValue(normalized, out string? comment);
            point = new IoPoint
            {
                Name = normalized,
                Address = address,
                Comment = comment ?? string.Empty,
                IsOutput = normalized[0] == 'Y',
            };
            return true;
        }

        /// <summary>归一化点位名：元件符号转大写（"x17" → "X17"、"sm5" → "SM5"），编号部分原样保留。</summary>
        public static string NormalizePointName(string name)
        {
            int i = 0;
            while (i < name.Length && char.IsLetter(name[i]))
            {
                i++;
            }

            return name[..i].ToUpperInvariant() + name[i..];
        }

        /// <summary>形如 X0 / Y37：字母开头、其后全为数字（表头「地址」、说明文字在此被排除）。</summary>
        public static bool IsPointName(string name)
        {
            if (name.Length < 2 || !char.IsLetter(name[0]))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>读取 exe 旁的 io-comments.json；文件不存在或损坏时保持内置默认，不阻断启动。</summary>
        public void Load()
        {
            try
            {
                if (!File.Exists(CommentsFilePath))
                {
                    return;
                }

                var loaded = JsonSerializer
                    .Deserialize<Dictionary<string, string>>(File.ReadAllText(CommentsFilePath));
                if (loaded != null)
                {
                    // ⚠️ **逐条重建，不要 new Dictionary(loaded, OrdinalIgnoreCase)**：
                    // 那个构造函数在遇到「仅大小写不同」的两个键时抛 ArgumentException ——
                    // 而 json 是人手可改的，写成 {"x17":"a","X17":"b"} 就会命中。
                    // 被外层 catch 吞掉后整表回退为空，用户看到的是「导入的 IO 表全没了」。
                    // 这里后者覆盖前者（与 json 里后出现的键生效一致），并按忽略大小写比较。
                    var rebuilt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, string> pair in loaded)
                    {
                        rebuilt[pair.Key] = pair.Value;
                    }

                    _comments = rebuilt;
                }
            }
            catch
            {
                // 损坏/不可读 → 回退内置注释
            }
        }

        /// <summary>整表写回 io-comments.json（导入后调用）。失败仅意味着下次启动不记得本次导入，不提示打断。</summary>
        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(
                    _comments,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CommentsFilePath, json);
            }
            catch
            {
                // 忽略写入失败
            }
        }

        /// <summary>
        /// 校验点位能否生效：① 形如 X0/Y37；② 元件在当前通讯驱动中已定义、且编号未超出该区范围；
        /// ③ 属于 IO 监控页上的点位（仅线圈）。失败时输出中文原因。
        /// </summary>
        public static bool TryValidatePoint(CommDriver? driver, string name, out string reason)
        {
            reason = string.Empty;

            if (!IsPointName(name))
            {
                reason = "格式无效，应为「元件+编号」如 X0";
                return false;
            }

            string normalized = NormalizePointName(name);
            if (normalized[0] != 'X' && normalized[0] != 'Y')
            {
                reason = "非 IO 监控点位（IO 监控仅支持输入 X / 输出 Y）";
                return false;
            }

            if (driver == null)
            {
                reason = "尚未选择设备类型（通讯驱动）";
                return false;
            }

            // IO 监控页采集的是线圈（FC01），故按 Coil 区校验地址范围
            if (!driver.TryResolve(normalized, ModbusArea.Coil, out _, out string error))
            {
                reason = DescribeResolveFailure(driver, normalized, error);
                return false;
            }

            return true;
        }

        /// <summary>把驱动层的解析失败翻译得更具体：未定义元件 / 非线圈元件 / 编号越界或进制非法。</summary>
        private static string DescribeResolveFailure(CommDriver driver, string name, string error)
        {
            int split = 0;
            while (split < name.Length && char.IsLetter(name[split]))
            {
                split++;
            }

            string element = name[..split];
            var areas = driver.Areas.Where(a => a.Element == element).ToList();

            if (areas.Count == 0)
            {
                return $"驱动「{driver.DisplayName}」未定义元件 {element}";
            }

            if (areas.All(a => a.Area != ModbusArea.Coil))
            {
                return $"元件 {element} 不是线圈，IO 监控仅支持线圈点位";
            }

            return error;
        }
    }
}
