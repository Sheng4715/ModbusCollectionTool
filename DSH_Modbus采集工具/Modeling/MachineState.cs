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

    /// <summary>
    /// 画布上的一条转移连线。**几何写死**，只有标签文字可由用户编辑。
    /// </summary>
    public sealed class MachineStateEdge
    {
        public MachineStateEdge(
            string id,
            string defaultText,
            bool doubleArrow,
            DesignPoint labelPoint,
            params DesignPoint[] points)
        {
            Id = id;
            DefaultText = defaultText;
            DoubleArrow = doubleArrow;
            LabelPoint = labelPoint;
            Points = points;
        }

        /// <summary>稳定键（<c>Reset</c> / <c>ResetDone</c> …）—— 落盘按它合并，改文字不影响。</summary>
        public string Id { get; }

        /// <summary>
        /// 出厂文字。需要折行的直接带 <c>\n</c>（如 <c>Reset\nDone</c>）——
        /// 早先按空格硬折的做法已废弃：现在文字可编辑，再自动折会让用户输入的空格凭空消失。
        /// </summary>
        public string DefaultText { get; }

        /// <summary>两端都画箭头（目前只有 <c>Toggle</c>）。</summary>
        public bool DoubleArrow { get; }

        /// <summary>标签中心点（压在连线上，用画布底色盖住线）。</summary>
        public DesignPoint LabelPoint { get; }

        /// <summary>折线经过的点，首尾是要连的两个端点。</summary>
        public IReadOnlyList<DesignPoint> Points { get; }
    }

    /// <summary>一条转移连线上、用户可编辑的那段文字（状态机切换条件，SC）。</summary>
    public sealed class MachineEdgeLabel : INotifyPropertyChanged
    {
        private string _text;

        public MachineEdgeLabel(string id, string text)
        {
            Id = id;
            _text = text ?? string.Empty;
        }

        /// <summary>稳定键，与 <see cref="MachineStateEdge.Id"/> 对应。</summary>
        public string Id { get; }

        /// <summary>显示文字。允许为空（画布上留一个空框，便于再点回来）；允许 <c>\n</c> 换行。</summary>
        public string Text
        {
            get => _text;
            set
            {
                string next = value ?? string.Empty;
                if (_text == next)
                {
                    return;
                }

                _text = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 画布上的一个**指示灯**（Error / Fault 两个之一）。
    /// <para>
    /// 视觉与 IO 监控里那颗灯同源（圆点 + 右侧文字），但这里它是画布上的独立元件：
    /// 双击可改文字与表达式，表达式为真时点亮（绿），为假时熄灭（灰）。
    /// </para>
    /// <para>
    /// 与 <see cref="MachineStateItem"/> 的区别：灯**没有字形**（永远常规体），
    /// 颜色也只有「点亮」这一种 —— 熄灭一律画主题灰。灯亮灯灭要能一眼看出来，
    /// 让用户连灭态都配色只会让「灭」看着像「亮」。
    /// </para>
    /// <para>
    /// 可编辑的是四样：文字、文字大小、圆点大小、点亮颜色（外加表达式）。
    /// 点亮色**永远是具体颜色**，没有「跟随主题」这一档 —— 灯色是用户自己定的，
    /// 换个深浅主题就悄悄变色会让人以为设置没生效。
    /// </para>
    /// </summary>
    public sealed class MachineStateLamp : INotifyPropertyChanged
    {
        private string _text;
        private string _expression = string.Empty;
        private string _color = MachineStateLayout.LampOnColor;
        private double _dotSize = MachineStateLayout.LampDotSize;
        private double _fontSize = MachineStateLayout.LampFontSize;
        private bool _isOn;

        public MachineStateLamp(string id, string text)
        {
            Id = id;
            _text = text ?? string.Empty;
        }

        /// <summary>稳定键（<c>Error</c> / <c>Fault</c>），落盘按它合并。</summary>
        public string Id { get; }

        /// <summary>灯右边显示的文字。</summary>
        public string Text
        {
            get => _text;
            set => Set(ref _text, value ?? string.Empty);
        }

        /// <summary>
        /// 点亮时圆点的颜色，<c>#RRGGBB</c>。**永远是具体颜色**，不会跟着主题变。
        /// <para>
        /// 出厂 <see cref="MachineStateLayout.LampOnColor"/>（浅色主题那支绿）。
        /// 写进来认不出的值一律退回这个色，不会留空 —— 空的颜色在画布上没法画。
        /// </para>
        /// </summary>
        public string Color
        {
            get => _color;
            set => Set(ref _color, NormalizeColor(value));
        }

        /// <summary>
        /// 圆点直径（设计坐标单位）。
        /// <para>上限 28：指示灯格子高 30（<see cref="MachineStateLayout.Lamps"/>），再大就撑出去了。</para>
        /// </summary>
        public double DotSize
        {
            get => _dotSize;
            set => Set(ref _dotSize, Math.Clamp(value, 6, 28));
        }

        /// <summary>灯右边文字的字号（设计坐标单位）。</summary>
        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, Math.Clamp(value, 8, 32));
        }

        /// <summary>BOOLEAN 表达式。空串 = 恒为假（灯一直灭）。</summary>
        public string Expression
        {
            get => _expression;
            set => Set(ref _expression, value ?? string.Empty);
        }

        /// <summary>当前是否点亮。**运行期字段，不落盘**；只有真的变了才通知。</summary>
        public bool IsOn
        {
            get => _isOn;
            set => Set(ref _isOn, value);
        }

        /// <summary>编译后的表达式（运行期缓存，不落盘）。</summary>
        [JsonIgnore]
        public CompiledExpression? Compiled { get; set; }

        /// <summary>最近一次求值失败的原因；空串表示没问题（含「表达式为空」）。</summary>
        [JsonIgnore]
        public string ExpressionError { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 把任意写法归一成 <c>#RRGGBB</c>；**认不出就退回 <see cref="MachineStateLayout.LampOnColor"/>**。
        /// <para>
        /// 与 <see cref="MachineStateItem.NormalizeColor"/> 同一个取舍（认不出退回一个确定的颜色），
        /// 只是退回的色不同：状态框退回浅红，灯退回它自己的出厂绿。
        /// 原本这里是退回空串（= 跟随主题），用户 2026-10-28 明确要求删掉「跟随主题」，
        /// 于是空串不再是一个有意义的取值。
        /// </para>
        /// </summary>
        public static string NormalizeColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return MachineStateLayout.LampOnColor;
            }

            string s = value.Trim();
            if (!s.StartsWith('#'))
            {
                s = "#" + s;
            }

            if (s.Length != 7)
            {
                return MachineStateLayout.LampOnColor;
            }

            for (int i = 1; i < 7; i++)
            {
                if (!Uri.IsHexDigit(s[i]))
                {
                    return MachineStateLayout.LampOnColor;
                }
            }

            return s.ToUpperInvariant();
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 状态机画布的**固定布局**：9 个框的位置、14 条连线、2 个指示灯、灰底容器。
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

        /// <summary>指示灯圆点默认直径（设计坐标，与 IO 监控里那颗 10px 的灯同量级）。</summary>
        public const double LampDotSize = 14;

        /// <summary>指示灯文字默认字号（设计坐标）。</summary>
        public const double LampFontSize = 14;

        /// <summary>
        /// 指示灯出厂的点亮色 —— 与主题键 <c>Io.Input.On</c> 的**浅色**值同一个绿。
        /// <para>
        /// 它是一个**常量**而不是资源引用：灯色由用户配，不跟主题走
        /// （用户 2026-10-28 要求删掉「跟随主题」）。深色主题下这颗灯仍是这个绿。
        /// </para>
        /// </summary>
        public const string LampOnColor = "#0B7A0B";

        /// <summary>
        /// 2 个指示灯的位置与尺寸，键是稳定键。
        /// <para>
        /// 位置落在灰底容器**下面**那条空白带、Manual Control 与 Aborted 之间，
        /// 两灯隔得更开 —— 用户 2026-10-28 在截图上用两条红线标的就是这两处。
        /// 红线是手绘马克笔粗线（6~10 px、端点有抗锯齿），按 Manual Control 的紫色填充标定过比例
        /// （scale = 1.1156、offset = 241.1），量出来 **Error 587–673、Fault 755–835**（设计坐标），
        /// 所以两灯左边缘取 587 / 754。用户的原话是「大概」，±3 都算对。
        /// </para>
        /// <para>
        /// ⚠️ 宽度只有 120 而不是与状态框一致的 150：灯的可点区域就是这个 Border，
        /// 给 150 的话 Fault 的右边缘会压到 Aborted（879）上，双击 Aborted 会打开灯的编辑窗。
        /// 120 收在 874，留 5 的安全边。灯内容（圆点 14 + 间距 8 + 文字）实际只有 60 出头，
        /// 120 已经够宽，多出来的部分是空白命中区。
        /// </para>
        /// </summary>
        public static IReadOnlyDictionary<string, DesignRect> Lamps { get; } =
            new Dictionary<string, DesignRect>(StringComparer.Ordinal)
            {
                ["Error"] = new(600, 368, 120, 30),
                ["Fault"] = new(700, 368, 120, 30),
            };

        /// <summary>2 个指示灯的出厂值：稳定键 → 灯右边的文字。</summary>
        public static IReadOnlyList<(string Id, string Text)> LampDefaults { get; } =
            new (string, string)[]
            {
                ("Error", "Error"),
                ("Fault", "Fault"),
            };

        /// <summary>按出厂值造一组指示灯（不读盘）。</summary>
        public static List<MachineStateLamp> CreateDefaultLamps()
        {
            var list = new List<MachineStateLamp>(LampDefaults.Count);
            foreach ((string id, string text) in LampDefaults)
            {
                list.Add(new MachineStateLamp(id, text) { Expression = string.Empty });
            }

            return list;
        }

        /// <summary>14 条转移连线（顺序即绘制顺序）。几何写死，标签文字可编辑。</summary>
        public static IReadOnlyList<MachineStateEdge> Edges { get; } = new MachineStateEdge[]
        {
            new("Reset", "Reset", false, new(129, 270), new(129, 358), new(129, 187)),
            new("Toggle", "Toggle", true, new(271.5, 383), new(204, 383), new(339, 383)),
            new("ClearOrImmediateStop", "Clear or ImmediateStop", false, new(541.5, 438),
                new(954, 408), new(954, 438), new(129, 438), new(129, 408)),
            new("ResetDone", "Reset\nDone", false, new(271.5, 162), new(204, 162), new(339, 162)),
            new("Start", "Start", false, new(556.5, 162), new(489, 162), new(624, 162)),
            new("Stop", "Stop", false, new(826.5, 162), new(774, 162), new(879, 162)),
            new("Suspend", "Suspend", false, new(736.5, 112), new(736.5, 137), new(736.5, 88)),
            new("UnSuspend", "Un Suspend", false, new(607.5, 108),
                new(624, 63), new(555, 63), new(555, 108), new(661.5, 108), new(661.5, 137)),
            new("HoldOrError", "Hold or Error", false, new(699, 218), new(699, 187), new(699, 247)),
            new("NoError", "No Error", false, new(555, 230), new(624, 272), new(555, 272), new(555, 177)),
            new("EndComplete", "End\nComplete", false, new(954, 84),
                new(954, 137), new(954, 28), new(414, 28), new(414, 137)),
            new("Fault", "Fault", false, new(954, 336), new(954, 314), new(954, 358)),
            new("ImmediateStop", "Immediate\nStop", false, new(186, 335), new(166.5, 314), new(166.5, 358)),

            // Completing → Held：与 Running → Held 同一个条件文字，但**是两条独立的线**
            // （同一条件在两个状态下都可能成立，不能合并）。走右侧通道：从 Completing 底边
            // 竖直下到 Held 的水平中线，再左折进 Held 右边 —— 与 Fault 那条竖线共线，
            // 视觉上像一条从 Completing 直通下方的线。
            new("HoldOrErrorCompleting", "Hold or Error", false, new(955, 231),
                new(955, 187), new(955, 272), new(774, 272)),
        };

        /// <summary>14 条连线的出厂标签文字（与 <see cref="Edges"/> 一一对应）。</summary>
        public static List<MachineEdgeLabel> CreateDefaultEdgeLabels()
        {
            var list = new List<MachineEdgeLabel>(Edges.Count);
            foreach (MachineStateEdge edge in Edges)
            {
                list.Add(new MachineEdgeLabel(edge.Id, edge.DefaultText));
            }

            return list;
        }

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

        /// <summary>只读状态与连线（指示灯用出厂值）。</summary>
        public static (List<MachineStateItem> States, List<MachineEdgeLabel> Edges) Load()
        {
            (List<MachineStateItem> states, List<MachineEdgeLabel> edges, _) = LoadAll();
            return (states, edges);
        }

        /// <summary>读全部：状态、连线标签、指示灯。</summary>
        public static (
            List<MachineStateItem> States,
            List<MachineEdgeLabel> Edges,
            List<MachineStateLamp> Lamps) LoadAll()
        {
            List<MachineStateItem> items = MachineStateLayout.CreateDefault();
            List<MachineEdgeLabel> edges = MachineStateLayout.CreateDefaultEdgeLabels();
            List<MachineStateLamp> lamps = MachineStateLayout.CreateDefaultLamps();

            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return (items, edges, lamps);
                }

                MachineStateFile? file =
                    JsonSerializer.Deserialize<MachineStateFile>(File.ReadAllText(path), Options);
                if (file == null)
                {
                    return (items, edges, lamps);
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

                // 连线标签同理按 id 合并：缺的用出厂文字，多的忽略
                var edgeById = edges.ToDictionary(x => x.Id, StringComparer.Ordinal);
                foreach (EdgeEntry entry in file.Edges ?? new List<EdgeEntry>())
                {
                    if (!string.IsNullOrEmpty(entry.Id) && edgeById.TryGetValue(entry.Id, out var label))
                    {
                        label.Text = entry.Text ?? label.Text;
                    }
                }

                // 指示灯同理（老文件没有这一段 → 用出厂值）
                var lampById = lamps.ToDictionary(x => x.Id, StringComparer.Ordinal);
                foreach (LampEntry entry in file.Lamps ?? new List<LampEntry>())
                {
                    if (!string.IsNullOrEmpty(entry.Id) && lampById.TryGetValue(entry.Id, out var lamp))
                    {
                        lamp.Text = string.IsNullOrWhiteSpace(entry.Text) ? lamp.Text : entry.Text;
                        lamp.Expression = entry.Expression ?? string.Empty;

                        // 外观三项：老文件里没有这几个键 → 用出厂值（0 表示「没写」；
                        // Color 缺省会被归一成 Layout.LampOnColor，正好等于出厂色）
                        lamp.Color = MachineStateLamp.NormalizeColor(entry.Color);
                        if (entry.DotSize > 0)
                        {
                            lamp.DotSize = entry.DotSize;
                        }

                        if (entry.FontSize > 0)
                        {
                            lamp.FontSize = entry.FontSize;
                        }
                    }
                }
            }
            catch
            {
                // 与 DataPointStore / GlobalVariableStore 一致：读失败不阻断启动，退回出厂值
                return (MachineStateLayout.CreateDefault(), MachineStateLayout.CreateDefaultEdgeLabels(),
                    MachineStateLayout.CreateDefaultLamps());
            }

            return (items, edges, lamps);
        }

        /// <summary>
        /// 只存状态与连线（不写指示灯那一段）。
        /// <para>
        /// ⚠️ 产品代码请一律用带 <c>lamps</c> 的重载：这个重载会把文件里的
        /// <c>Lamps</c> 段整段丢掉，下次读盘指示灯就退回出厂值。
        /// 保留它只是为了让断言工程能单独测状态/连线的往返。
        /// </para>
        /// </summary>
        public static bool Save(
            IEnumerable<MachineStateItem> items, IEnumerable<MachineEdgeLabel> edges) =>
            Save(items, edges, lamps: null);

        /// <summary>存全部：状态、连线标签、指示灯。</summary>
        public static bool Save(
            IEnumerable<MachineStateItem> items,
            IEnumerable<MachineEdgeLabel> edges,
            IEnumerable<MachineStateLamp>? lamps)
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
                    Edges = edges.Select(x => new EdgeEntry
                    {
                        Id = x.Id,
                        Text = x.Text,
                    }).ToList(),
                    Lamps = lamps?.Select(x => new LampEntry
                    {
                        Id = x.Id,
                        Text = x.Text,
                        Expression = x.Expression,
                        Color = x.Color,
                        DotSize = x.DotSize,
                        FontSize = x.FontSize,
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

            /// <summary>连线标签。老文件没有这一段 → 用出厂文字。</summary>
            public List<EdgeEntry>? Edges { get; set; }

            /// <summary>指示灯。老文件没有这一段 → 用出厂值。</summary>
            public List<LampEntry>? Lamps { get; set; }
        }

        private sealed class EdgeEntry
        {
            public string? Id { get; set; }

            public string? Text { get; set; }
        }

        private sealed class LampEntry
        {
            public string? Id { get; set; }

            public string? Text { get; set; }

            /// <summary>点亮色；缺省 / 认不出 → 归一成 <see cref="MachineStateLayout.LampOnColor"/>。</summary>
            public string? Color { get; set; }

            /// <summary>圆点直径；缺省（0）= 出厂值。</summary>
            public double DotSize { get; set; }

            /// <summary>文字字号；缺省（0）= 出厂值。</summary>
            public double FontSize { get; set; }

            public string? Expression { get; set; }
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

    /// <summary>9 个状态 + 2 个指示灯，以及它们的表达式求值。</summary>
    public sealed class MachineStateSet
    {
        private readonly List<MachineStateItem> _items;
        private readonly List<MachineEdgeLabel> _edgeLabels;
        private readonly List<MachineStateLamp> _lamps;

        public MachineStateSet()
        {
            _items = MachineStateLayout.CreateDefault();
            _edgeLabels = MachineStateLayout.CreateDefaultEdgeLabels();
            _lamps = MachineStateLayout.CreateDefaultLamps();
        }

        public IReadOnlyList<MachineStateItem> Items => _items;

        /// <summary>14 条连线上的标签（用户可编辑的切换条件文字）。</summary>
        public IReadOnlyList<MachineEdgeLabel> EdgeLabels => _edgeLabels;

        /// <summary>2 个指示灯（Error / Fault）。</summary>
        public IReadOnlyList<MachineStateLamp> Lamps => _lamps;

        /// <summary>从磁盘读，并把每条表达式编译好。</summary>
        public void Load(Func<string, bool> nameExists)
        {
            (List<MachineStateItem> states, List<MachineEdgeLabel> edges, List<MachineStateLamp> lamps) =
                MachineStateStore.LoadAll();

            _items.Clear();
            _items.AddRange(states);

            _edgeLabels.Clear();
            _edgeLabels.AddRange(edges);

            _lamps.Clear();
            _lamps.AddRange(lamps);

            foreach (MachineStateItem item in _items)
            {
                CompileQuietly(item, nameExists);
            }

            foreach (MachineStateLamp lamp in _lamps)
            {
                CompileQuietly(lamp, nameExists);
            }
        }

        public bool Save() => MachineStateStore.Save(_items, _edgeLabels, _lamps);

        /// <summary>
        /// 改一条表达式：编译 + 校验变量存在性，成功才写回。
        /// <para>失败时**不动模型**（调用方负责把输入框退回旧值），与自由监控改名同一套规矩。</para>
        /// </summary>
        public bool TrySetExpression(
            MachineStateItem item, string? text, Func<string, bool> nameExists, out string error)
        {
            if (!TryCompile(text, nameExists, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            item.Compiled = compiled;
            item.ExpressionError = string.Empty;
            item.Expression = compiled!.Source;
            return true;
        }

        /// <summary>改一个指示灯的表达式（规则与状态框完全一致）。</summary>
        public bool TrySetExpression(
            MachineStateLamp lamp, string? text, Func<string, bool> nameExists, out string error)
        {
            if (!TryCompile(text, nameExists, out CompiledExpression? compiled, out error))
            {
                return false;
            }

            lamp.Compiled = compiled;
            lamp.ExpressionError = string.Empty;
            lamp.Expression = compiled!.Source;
            return true;
        }

        /// <summary>启动 / 变量表变动时重建全部表达式缓存（失败的不报错，标红留给界面）。</summary>
        public void RecompileAll(Func<string, bool> nameExists)
        {
            foreach (MachineStateItem item in _items)
            {
                CompileQuietly(item, nameExists);
            }

            foreach (MachineStateLamp lamp in _lamps)
            {
                CompileQuietly(lamp, nameExists);
            }
        }

        /// <summary>按当前变量值刷新 9 个状态的真假与 2 个指示灯。</summary>
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

            foreach (MachineStateLamp lamp in _lamps)
            {
                // 灯的口径与状态框一致：没表达式 / 编译失败 = 灭
                if (lamp.Compiled == null)
                {
                    lamp.IsOn = false;
                    continue;
                }

                if (lamp.Compiled.TryEvaluate(variables, out bool on, out string error))
                {
                    lamp.IsOn = on;
                    lamp.ExpressionError = string.Empty;
                }
                else
                {
                    lamp.IsOn = false;
                    lamp.ExpressionError = error;
                }
            }
        }

        /// <summary>编译 + 变量存在性校验（成功返回 true，且 <paramref name="compiled"/> 非空）。</summary>
        private static bool TryCompile(
            string? text,
            Func<string, bool> nameExists,
            out CompiledExpression? compiled,
            out string error)
        {
            if (!CompiledExpression.TryParse(text, out compiled, out error))
            {
                return false;
            }

            return compiled!.TryValidateNames(nameExists, out error);
        }

        private static void CompileQuietly(MachineStateItem item, Func<string, bool> nameExists)
        {
            bool ok = TryCompile(item.Expression, nameExists, out CompiledExpression? compiled, out string error);
            item.Compiled = compiled;
            item.ExpressionError = ok ? string.Empty : error;

            // 校验不过时也留住编译结果：求值会因为找不到变量而失败，
            // 语义与「编译失败」一样是灭，但界面能拿到更具体的错误
        }

        private static void CompileQuietly(MachineStateLamp lamp, Func<string, bool> nameExists)
        {
            bool ok = TryCompile(lamp.Expression, nameExists, out CompiledExpression? compiled, out string error);
            lamp.Compiled = compiled;
            lamp.ExpressionError = ok ? string.Empty : error;
        }
    }
}
