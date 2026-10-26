using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 任务组的类型（P3.1）：**数据读取** 或 **数据写入**。
    /// <para>
    /// ⚠️ 目前只影响界面显示与后续执行分支，**读写链路都还没接** ——
    /// 需求要求「先做 UI 再实现功能」，所以这一层刻意只落地结构。
    /// </para>
    /// </summary>
    public enum AcquisitionTaskKind
    {
        /// <summary>数据读取（默认）</summary>
        Read,

        /// <summary>数据写入</summary>
        Write,
    }

    /// <summary>
    /// 任务组的执行方式（P3.1）。
    /// <para>
    /// <see cref="Edge"/> 的语义按需求定为 **上升沿有效**：表达式由假变真的那一拍触发一次，
    /// 持续为真**不**重复触发（见 <see cref="AcquisitionTaskRules"/> 的注释）。
    /// </para>
    /// </summary>
    public enum AcquisitionTriggerMode
    {
        /// <summary>周期式（默认）：每过固定时间采集一次</summary>
        Periodic,

        /// <summary>触发：表达式成立上升沿有效</summary>
        Edge,
    }

    /// <summary>
    /// 任务组里的一个**数据项**（P3.1）。
    /// <para>
    /// 一个数据项 = 一个显示名 + 一个**全局变量名**。数据类型**不在这里存** ——
    /// 它由被引用的变量反推（需求：数据类型列只读），所以变量改名/换类型后
    /// 采集项会跟着变，不存在两边对不上的可能。
    /// </para>
    /// </summary>
    public sealed class AcquisitionItem
    {
        /// <summary>项名称（给人看的显示名，如「压力」）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 变量名称 —— 引用一条**全局变量**（P3.3）。
        /// <para>空串 = 还没选变量；此时数据类型列显示占位符、不会反推出类型。</para>
        /// </summary>
        public string VariableName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一个**任务组**（P3.1）：任务名称 / 任务类型 / 执行方式 / 启用 + 任意个数据项。
    /// <para>
    /// 持久化到 <c>acquisition-tasks.json</c>。
    /// ⚠️ **展开 / 收起是界面态**，不在这里 —— 放在 <c>AcquisitionTaskItem</c> 的包装里，
    /// 免得「上次是展开的」这种显示偏好混进配置数据。
    /// </para>
    /// </summary>
    public sealed class AcquisitionTask
    {
        /// <summary>任务名称（默认「任务组N」）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>任务类型：数据读取 / 数据写入</summary>
        public AcquisitionTaskKind Kind { get; set; } = AcquisitionTaskKind.Read;

        /// <summary>执行方式：周期式 / 触发</summary>
        public AcquisitionTriggerMode Trigger { get; set; } = AcquisitionTriggerMode.Periodic;

        /// <summary>
        /// 周期秒数（仅 <see cref="AcquisitionTriggerMode.Periodic"/> 有意义）。
        /// <para>用 <see cref="double"/> 而不是 <see cref="int"/>：现场有 0.5s 这种档位需求。</para>
        /// </summary>
        public double PeriodSeconds { get; set; } = AcquisitionTaskRules.DefaultPeriodSeconds;

        /// <summary>
        /// 触发表达式（仅 <see cref="AcquisitionTriggerMode.Edge"/> 有意义）。
        /// <para>口径与 PLC 状态机一致：系统变量 + 全局变量，**不含元件地址**。</para>
        /// </summary>
        public string Expression { get; set; } = string.Empty;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>数据项（可为空 —— 刚建出来的任务组还没有项）</summary>
        public List<AcquisitionItem> Items { get; set; } = new List<AcquisitionItem>();
    }

    /// <summary>
    /// 任务组 / 数据项的规则与文案（纯函数，可独立断言）。
    /// <para>
    /// 文案集中放这里而不是散在窗口里，是为了让离线断言能直接核对，
    /// 不必启动界面 —— 与 <c>GlobalVariableRules</c> 同一思路。
    /// </para>
    /// </summary>
    public static class AcquisitionTaskRules
    {
        /// <summary>任务名称最大长度。比变量名宽松：它是给人看的，不参与表达式。</summary>
        public const int MaxNameLength = 64;

        /// <summary>周期默认秒数（需求样例里没给，取 5s 这种「看得出在跑、又不刷屏」的档）</summary>
        public const double DefaultPeriodSeconds = 5;

        /// <summary>周期下限。0 会让后续执行变成忙等，直接拒掉。</summary>
        public const double MinPeriodSeconds = 0.1;

        /// <summary>周期上限（1 天）。再大就不是「采集」了，属误输入。</summary>
        public const double MaxPeriodSeconds = 86400;

        /// <summary>任务类型显示名</summary>
        public static string DescribeKind(AcquisitionTaskKind kind) =>
            kind == AcquisitionTaskKind.Write ? "数据写入" : "数据读取";

        /// <summary>执行方式显示名（不含参数）</summary>
        public static string DescribeTrigger(AcquisitionTriggerMode mode) =>
            mode == AcquisitionTriggerMode.Edge ? "触发" : "周期";

        /// <summary>
        /// 「执行方式」列的完整文案：周期 → <c>周期 5s</c>；触发 → <c>触发（上升沿）</c>。
        /// <para>列表里要一眼看出周期多长，光写「周期」等于没写。</para>
        /// </summary>
        public static string DescribeSchedule(AcquisitionTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            return task.Trigger == AcquisitionTriggerMode.Edge
                ? "触发（上升沿）"
                : "周期 " + FormatSeconds(task.PeriodSeconds) + "s";
        }

        /// <summary>秒数格式化：整数不带小数点（5 而不是 5.0），小数最多三位。</summary>
        public static string FormatSeconds(double seconds) =>
            seconds.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>任务名称校验：非空、不超长。</summary>
        public static bool TryValidateName(string? name, out string error)
        {
            error = string.Empty;
            name ??= string.Empty;

            if (name.Trim().Length == 0)
            {
                error = "任务名称不能为空";
                return false;
            }

            if (name.Length > MaxNameLength)
            {
                error = "任务名称最多 " + MaxNameLength + " 个字符（当前 " + name.Length + "）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 周期秒数解析：接受 <c>5</c> / <c>0.5</c>，拒绝空串、非数字、0 与负数、超上限。
        /// <para>⚠️ 用 <see cref="NumberStyles.Float"/> + <see cref="CultureInfo.InvariantCulture"/>
        /// 解析：跟着系统区域走的话，中文环境下 <c>5.5</c> 可能被当成千位分隔符。</para>
        /// </summary>
        public static bool TryParsePeriod(string? text, out double seconds, out string error)
        {
            seconds = DefaultPeriodSeconds;
            error = string.Empty;
            text = (text ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                error = "周期不能为空";
                return false;
            }

            if (!double.TryParse(
                    text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                error = "周期必须是数字";
                return false;
            }

            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                error = "周期必须是数字";
                return false;
            }

            if (parsed < MinPeriodSeconds)
            {
                error = "周期不能小于 " + FormatSeconds(MinPeriodSeconds) + "s";
                return false;
            }

            if (parsed > MaxPeriodSeconds)
            {
                error = "周期不能大于 " + FormatSeconds(MaxPeriodSeconds) + "s";
                return false;
            }

            seconds = parsed;
            return true;
        }

        /// <summary>新建任务组时的默认名称（<c>任务组1</c>、<c>任务组2</c> …）</summary>
        public static string NextTaskName(IEnumerable<AcquisitionTask>? existing)
        {
            int n = (existing?.Count() ?? 0) + 1;
            return "任务组" + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>新建数据项时的默认项名称（<c>项1</c>、<c>项2</c> …，按该组现有数量续号）</summary>
        public static string NextItemName(IReadOnlyList<AcquisitionItem>? items)
        {
            int n = (items?.Count ?? 0) + 1;
            return "项" + n.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 任务组集合的持久化（<c>acquisition-tasks.json</c>，与 <c>GlobalVar.json</c> 同模式）。
    /// </summary>
    public static class AcquisitionTaskStore
    {
        private const string FileName = "acquisition-tasks.json";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 与另两份 json 一致：枚举写成名字，避免枚举顺序变化后静默错位
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<AcquisitionTask> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return new List<AcquisitionTask>();
                }

                var list = JsonSerializer.Deserialize<List<AcquisitionTask>>(File.ReadAllText(path), Options);
                return list ?? new List<AcquisitionTask>();
            }
            catch
            {
                // 与 DataPointStore / GlobalVariableStore 一致：读失败不阻断启动，视为空集合
                return new List<AcquisitionTask>();
            }
        }

        public static bool Save(IEnumerable<AcquisitionTask> tasks)
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(tasks, Options));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 运行时任务组集合：增删改 + 落盘。
    /// <para>⚠️ 本类**不碰 WPF**，与 <c>GlobalVariableSet</c> 同一层次。</para>
    /// </summary>
    public sealed class AcquisitionTaskSet
    {
        private readonly List<AcquisitionTask> _items = new List<AcquisitionTask>();

        public IReadOnlyList<AcquisitionTask> Items => _items;

        /// <summary>载入定义。文件不存在或损坏时为空集合。</summary>
        public void Load()
        {
            _items.Clear();
            _items.AddRange(AcquisitionTaskStore.Load());
        }

        /// <summary>
        /// 新增一个任务组（名称取默认的「任务组N」）。
        /// <para>⚠️ 默认名称也要过重名检查：用户可能正好手动命名过同名的。</para>
        /// </summary>
        public AcquisitionTask Add()
        {
            string name = AcquisitionTaskRules.NextTaskName(_items);
            if (IndexOfName(name) >= 0)
            {
                name = name + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
            }

            var task = new AcquisitionTask { Name = name };
            _items.Add(task);
            return task;
        }

        public void Remove(AcquisitionTask task) => _items.Remove(task);

        public void Clear() => _items.Clear();

        /// <summary>
        /// 改名。**空名放行**（列表里显示为空，不阻断编辑），只拦超长与重名 ——
        /// 任务名不是引用键，没有「配置坏掉」的后果，没必要像变量名那样从严。
        /// </summary>
        public bool Rename(AcquisitionTask task, string newName, out string error)
        {
            error = string.Empty;
            newName ??= string.Empty;

            if (string.Equals(task.Name, newName, StringComparison.Ordinal))
            {
                return true;
            }

            if (!AcquisitionTaskRules.TryValidateName(newName, out error))
            {
                return false;
            }

            int existing = IndexOfName(newName);
            if (existing >= 0 && !ReferenceEquals(_items[existing], task))
            {
                error = "任务名称 " + newName + " 已存在";
                return false;
            }

            task.Name = newName;
            return true;
        }

        /// <summary>按名称查找（序号语义，精确比对）</summary>
        public AcquisitionTask? FindByName(string name) =>
            _items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

        private int IndexOfName(string name) =>
            _items.FindIndex(t => string.Equals(t.Name, name, StringComparison.Ordinal));

        /// <summary>落盘</summary>
        public bool Save() => AcquisitionTaskStore.Save(_items);
    }
}
