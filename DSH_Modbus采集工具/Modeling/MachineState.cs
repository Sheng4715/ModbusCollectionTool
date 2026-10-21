using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSH_Modbus采集工具.Expressions;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>状态框里文字的字形。</summary>
    public enum MachineStateFontStyle
    {
        Normal = 0,
        Bold = 1,
        Italic = 2,
        BoldItalic = 3,
    }

    /// <summary>设计坐标系里的一个矩形（左上角 + 尺寸），单位与画布一致。</summary>
    public readonly record struct DesignRect(double X, double Y, double Width, double Height);

    /// <summary>设计坐标系里的一个点。</summary>
    public readonly record struct DesignPoint(double X, double Y);

    /// <summary>
    /// 状态机画布上的一个状态（9 个之一）。
    /// <para>
    /// <see cref="Id"/> 是**稳定键**（<c>Stopped</c> …），落盘、查值都靠它；
    /// <see cref="Text"/> 才是用户能改的显示文字。改文字不该影响任何逻辑。
    /// </para>
    /// </summary>
    public sealed class MachineStateItem : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private MachineStateFontStyle _fontStyle = MachineStateFontStyle.Normal;
        private double _fontSize = 15;
        private string _color = "#F8CECC";
        private string _expression = string.Empty;
        private bool _isActive;

        public MachineStateItem(string id)
        {
            Id = id;
        }

        /// <summary>稳定键，不落盘也不允许改。</summary>
        public string Id { get; }

        /// <summary>状态框里显示的文字。</summary>
        public string Text
        {
            get => _text;
            set => Set(ref _text, value ?? string.Empty);
        }

        public MachineStateFontStyle FontStyle
        {
            get => _fontStyle;
            set => Set(ref _fontStyle, value);
        }

        /// <summary>字号（设计坐标单位，画布整体缩放时由 Viewbox 一并缩放）。</summary>
        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, Math.Clamp(value, 8, 72));
        }

        /// <summary>值为真时的填充色，<c>#RRGGBB</c>。值为假时一律画成浅灰，与本值无关。</summary>
        public string Color
        {
            get => _color;
            set => Set(ref _color, NormalizeColor(value));
        }

        /// <summary>BOOLEAN 表达式。空串 = 恒为假。</summary>
        public string Expression
        {
            get => _expression;
            set => Set(ref _expression, value ?? string.Empty);
        }

        /// <summary>
        /// 当前值（表达式算出来的）。**运行期字段，不落盘**。
        /// <para>只有真的变了才通知，避免 50ms 一拍把绑定刷爆。</para>
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }

        /// <summary>编译后的表达式（运行期缓存，不落盘）。</summary>
        [JsonIgnore]
        public CompiledExpression? Compiled { get; set; }

        /// <summary>最近一次求值失败的原因；空串表示没问题（含「表达式为空」）。</summary>
        [JsonIgnore]
        public string ExpressionError { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>把任意写法归一成 <c>#RRGGBB</c>；认不出就用默认的浅红。</summary>
        public static string NormalizeColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "#F8CECC";
            }

            string s = value.Trim();
            if (!s.StartsWith('#'))
            {
                s = "#" + s;
            }

            if (s.Length != 7)
            {
                return "#F8CECC";
            }

            for (int i = 1; i < 7; i++)
            {
                if (!Uri.IsHexDigit(s[i]))
                {
                    return "#F8CECC";
                }
            }

            return s.ToUpperInvariant();
        }
    }

    /// <summary>画布上的一条转移连线（含标签）。</summary>
    public sealed class MachineStateEdge
    {
        public MachineStateEdge(
            string label,
            bool doubleArrow,
            int labelLines,
            DesignPoint labelPoint,
            params DesignPoint[] points)
        {
            Label = label;
            DoubleArrow = doubleArrow;
            LabelLines = labelLines;
            LabelPoint = labelPoint;
            Points = points;
        }

        /// <summary>连线上的文字，如 <c>Reset Done</c>。</summary>
        public string Label { get; }

        /// <summary>两端都画箭头（目前只有 <c>Toggle</c>）。</summary>
        public bool DoubleArrow { get; }

        /// <summary>标签折几行：2 表示按 drawio 那样折成两行。</summary>
        public int LabelLines { get; }

        /// <summary>标签中心点（压在连线上，用画布底色盖住线）。</summary>
        public DesignPoint LabelPoint { get; }

        /// <summary>折线经过的点，首尾是要连的两个端点。</summary>
        public IReadOnlyList<DesignPoint> Points { get; }
    }

    /// <summary>
    /// 状态机画布的**固定布局**：9 个框的位置、13 条连线、灰底容器。
    /// <para>
    /// 这些坐标全部由项目根目录的 <c>PLC_MachineState.drawio</c> 换算而来
    /// （横向 ×1.5 拉开列距，纵向原样），**不要在代码里手调** ——
    /// 要改布局请改 drawio 再重新换算，否则图和连线会对不上。
    /// </para>
    /// <para>
    /// ⚠️ 这里的单位是「设计坐标」，画布用 <c>Viewbox</c> 等比缩放到实际大小，
    /// 所以字号、线宽都按设计坐标写，不需要关心窗口多大。
    /// </para>
    /// </summary>
    public static class MachineStateLayout
    {
        /// <summary>设计画布宽（与 <see cref="DesignHeight"/> 一起决定宽高比）。</summary>
        public const double DesignWidth = 1095;

        /// <summary>设计画布高。</summary>
        public const double DesignHeight = 446;

        /// <summary>框内文字默认字号。</summary>
        public const double DefaultFontSize = 15;

        /// <summary>值为假时的填充色（浅灰）。</summary>
        public const string InactiveColor = "#E9E9E9";

        /// <summary>值为假时的描边色。</summary>
        public const string InactiveBorderColor = "#C4C4C4";

        /// <summary>值为假时的文字色。</summary>
        public const string InactiveTextColor = "#969696";

        /// <summary>灰底容器（把中间那 6 个状态框在一起）。</summary>
        public static DesignRect Container { get; } = new(0, 8, 1095, 306);

        /// <summary>9 个状态框的位置与尺寸，键是稳定键。</summary>
        public static IReadOnlyDictionary<string, DesignRect> Boxes { get; } =
            new Dictionary<string, DesignRect>(StringComparer.Ordinal)
            {
                ["Stopped"] = new(54, 358, 150, 50),
                ["Resetting"] = new(54, 137, 150, 50),
                ["Idle"] = new(339, 137, 150, 50),
                ["Running"] = new(624, 137, 150, 50),
                ["Held"] = new(624, 247, 150, 50),
                ["Suspended"] = new(624, 38, 150, 50),
                ["Completing"] = new(879, 137, 150, 50),
                ["Aborted"] = new(879, 358, 150, 50),
                ["ManualControl"] = new(339, 358, 150, 50),
            };

        /// <summary>13 条转移连线（顺序即绘制顺序）。</summary>
        public static IReadOnlyList<MachineStateEdge> Edges { get; } = new MachineStateEdge[]
        {
            new("Reset", false, 1, new(129, 270), new(129, 358), new(129, 187)),
            new("Toggle", true, 1, new(271.5, 383), new(204, 383), new(339, 383)),
            new("Clear or ImmediateStop", false, 1, new(541.5, 438),
                new(954, 408), new(954, 438), new(129, 438), new(129, 408)),
            new("Reset Done", false, 2, new(271.5, 162), new(204, 162), new(339, 162)),
            new("Start", false, 1, new(556.5, 162), new(489, 162), new(624, 162)),
            new("Stop", false, 1, new(826.5, 162), new(774, 162), new(879, 162)),
            new("Suspend", false, 1, new(736.5, 112), new(736.5, 137), new(736.5, 88)),
            new("Un Suspend", false, 1, new(607.5, 108),
                new(624, 63), new(555, 63), new(555, 108), new(661.5, 108), new(661.5, 137)),
            new("Hold or Error", false, 1, new(699, 218), new(699, 187), new(699, 247)),
            new("No Error", false, 1, new(555, 230), new(624, 272), new(555, 272), new(555, 177)),
            new("End Complete", false, 2, new(954, 84),
                new(954, 137), new(954, 28), new(414, 28), new(414, 137)),
            new("Fault", false, 1, new(954, 336), new(954, 314), new(954, 358)),
            new("Immediate Stop", false, 2, new(186, 335), new(166.5, 314), new(166.5, 358)),
        };

        /// <summary>
        /// 9 个状态的**出厂值**：稳定键 → (显示文字, 填充色)。
        /// <para>文字默认就是状态名；颜色取自 drawio 原图的填充色。</para>
        /// </summary>
        public static IReadOnlyList<(string Id, string Text, string Color)> Defaults { get; } =
            new (string, string, string)[]
            {
                ("Stopped", "Stopped", "#F8CECC"),
                ("Resetting", "Resetting", "#FFCD28"),
                ("Idle", "Idle", "#FFF2CC"),
                ("Running", "Running", "#D5E8D4"),
                ("Suspended", "Suspended", "#FFF2CC"),
                ("Held", "Held", "#FFF2CC"),
                ("Completing", "Completing", "#D5E8D4"),
                ("Aborted", "Aborted", "#F8CECC"),
                ("ManualControl", "Manual Control", "#DAE8FC"),
            };

        /// <summary>按出厂值造一整套状态（不读盘）。</summary>
        public static List<MachineStateItem> CreateDefault()
        {
            var list = new List<MachineStateItem>(Defaults.Count);
            foreach ((string id, string text, string color) in Defaults)
            {
                list.Add(new MachineStateItem(id)
                {
                    Text = text,
                    Color = color,
                    FontStyle = MachineStateFontStyle.Normal,
                    FontSize = DefaultFontSize,
                    Expression = string.Empty,
                });
            }

            return list;
        }
    }

    /// <summary>
    /// 状态机配置的持久化（<c>machine-state.json</c>，与 <c>GlobalVar.json</c> 同模式）。
    /// <para>
    /// ⚠️ 只存**用户改过的属性**（文字 / 字形 / 字号 / 颜色 / 表达式），不存坐标 ——
    /// 坐标是画死的，跟着 <see cref="MachineStateLayout"/> 走。
    /// </para>
    /// <para>
    /// 读盘时按 <see cref="MachineStateItem.Id"/> 合并进出厂值：
    /// 文件里少了某个 id 就用出厂值，多了（旧版本留下的）就忽略，
    /// 这样加状态、改默认值都不必写迁移代码。
    /// </para>
    /// </summary>
    public static class MachineStateStore
    {
        private const string FileName = "machine-state.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // ⚠️ 必须忽略大小写：System.Text.Json 默认是**区分大小写**的，
            // 而这份文件很可能被人手改（"id" / "Id" 都可能出现）。
            // 不加这一行，手写的小写键会静默匹配不上 —— 不报错、只是所有值退回默认，
            // 排查起来非常费劲（实测踩过：图一直是灰的）。
            PropertyNameCaseInsensitive = true,
        };

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<MachineStateItem> Load()
        {
            List<MachineStateItem> items = MachineStateLayout.CreateDefault();

            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return items;
                }

                MachineStateFile? file =
                    JsonSerializer.Deserialize<MachineStateFile>(File.ReadAllText(path), Options);
                if (file?.States == null)
                {
                    return items;
                }

                var byId = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
                foreach (MachineStateEntry entry in file.States)
                {
                    if (string.IsNullOrEmpty(entry.Id) || !byId.TryGetValue(entry.Id, out var item))
                    {
                        continue;   // 认不出的 id 直接忽略，不阻断启动
                    }

                    item.Text = string.IsNullOrWhiteSpace(entry.Text) ? item.Text : entry.Text;
                    item.FontStyle = entry.FontStyle;
                    item.FontSize = entry.FontSize > 0 ? entry.FontSize : item.FontSize;
                    item.Color = MachineStateItem.NormalizeColor(entry.Color);
                    item.Expression = entry.Expression ?? string.Empty;
                }
            }
            catch
            {
                // 与 DataPointStore / GlobalVariableStore 一致：读失败不阻断启动，退回出厂值
                return MachineStateLayout.CreateDefault();
            }

            return items;
        }

        public static bool Save(IEnumerable<MachineStateItem> items)
        {
            try
            {
                var file = new MachineStateFile
                {
                    States = items.Select(x => new MachineStateEntry
                    {
                        Id = x.Id,
                        Text = x.Text,
                        FontStyle = x.FontStyle,
                        FontSize = x.FontSize,
                        Color = x.Color,
                        Expression = x.Expression,
                    }).ToList(),
                };

                File.WriteAllText(FilePath, JsonSerializer.Serialize(file, Options));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class MachineStateFile
        {
            public List<MachineStateEntry> States { get; set; } = new();
        }

        private sealed class MachineStateEntry
        {
            public string? Id { get; set; }

            public string? Text { get; set; }

            public MachineStateFontStyle FontStyle { get; set; }

            public double FontSize { get; set; }

            public string? Color { get; set; }

            public string? Expression { get; set; }
        }
    }

    /// <summary>9 个状态 + 它们的表达式求值。</summary>
    public sealed class MachineStateSet
    {
        private readonly List<MachineStateItem> _items;

        public MachineStateSet()
        {
            _items = MachineStateLayout.CreateDefault();
        }

        public IReadOnlyList<MachineStateItem> Items => _items;

        /// <summary>从磁盘读，并把每条表达式编译好。</summary>
        public void Load(Func<string, bool> nameExists)
        {
            List<MachineStateItem> loaded = MachineStateStore.Load();
            _items.Clear();
            _items.AddRange(loaded);

            foreach (MachineStateItem item in _items)
            {
                CompileQuietly(item, nameExists);
            }
        }

        public bool Save() => MachineStateStore.Save(_items);

        /// <summary>
        /// 改一条表达式：编译 + 校验变量存在性，成功才写回。
        /// <para>失败时**不动模型**（调用方负责把输入框退回旧值），与自由监控改名同一套规矩。</para>
        /// </summary>
        public bool TrySetExpression(
            MachineStateItem item, string? text, Func<string, bool> nameExists, out string error)
        {
            if (!CompiledExpression.TryParse(text, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            if (!compiled!.TryValidateNames(nameExists, out error))
            {
                return false;
            }

            item.Compiled = compiled;
            item.ExpressionError = string.Empty;
            item.Expression = compiled.Source;
            return true;
        }

        /// <summary>启动 / 变量表变动时重建全部表达式缓存（失败的不报错，标红留给界面）。</summary>
        public void RecompileAll(Func<string, bool> nameExists)
        {
            foreach (MachineStateItem item in _items)
            {
                CompileQuietly(item, nameExists);
            }
        }

        /// <summary>按当前变量值刷新 9 个状态的真假。</summary>
        public void EvaluateAll(IExpressionVariables variables)
        {
            foreach (MachineStateItem item in _items)
            {
                if (item.Compiled == null)
                {
                    item.IsActive = false;
                    continue;
                }

                if (item.Compiled.TryEvaluate(variables, out bool value, out string error))
                {
                    item.IsActive = value;
                    item.ExpressionError = string.Empty;
                }
                else
                {
                    item.IsActive = false;
                    item.ExpressionError = error;
                }
            }
        }

        private static void CompileQuietly(MachineStateItem item, Func<string, bool> nameExists)
        {
            if (!CompiledExpression.TryParse(item.Expression, out CompiledExpression? compiled, out string error))
            {
                item.Compiled = null;
                item.ExpressionError = error;
                return;
            }

            if (!compiled!.TryValidateNames(nameExists, out error))
            {
                item.Compiled = compiled;
                item.ExpressionError = error;
                return;
            }

            item.Compiled = compiled;
            item.ExpressionError = string.Empty;
        }
    }
}
