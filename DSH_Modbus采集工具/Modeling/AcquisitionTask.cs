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
        /// <summary>
        /// 稳定标识（P1.4 实时数据用，2026-10-31）。
        /// <para>
        /// ⚠️ **不能用「项名称」当键**：项名称允许重复、允许改，改一次布局就丢。
        /// 也不能用数组下标：增删一项后面全体错位。
        /// </para>
        /// <para>
        /// 旧 json 里没有这个字段 → 载入时自动补发（见 <see cref="AcquisitionTaskRules"/> 的迁移说明），
        /// 补完下次保存即落盘，无需手工迁移。
        /// </para>
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>项名称（给人看的显示名，如「压力」）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 变量名称 —— 引用一条**全局变量**（P3.3）。
        /// <para>空串 = 还没选变量；此时数据类型列显示占位符、不会反推出类型。</para>
        /// <para>
        /// **读取类型**：这是「读取变量」（从哪读）。
        /// **写入类型**：这是「写入变量」（写到哪），来源另存 <see cref="SourceVariableName"/>。
        /// 两档共用这一个字段，是因为数据类型列跟着它走 —— 写入时该看的是目标 PLC 的类型。
        /// </para>
        /// </summary>
        public string VariableName { get; set; } = string.Empty;

        /// <summary>
        /// 来源变量（**仅写入类型有意义**）：值从哪个全局变量取。
        /// <para>读取类型恒为空串 —— 那时数据来自设备，没有「来源」一说。</para>
        /// </summary>
        public string SourceVariableName { get; set; } = string.Empty;

        /// <summary>
        /// 实时画布上的 X 坐标（DIP）。P1.4。**null = 从未摆放过**，由界面给默认位置。
        /// <para>
        /// 坐标基准是**图片左上角**，逻辑空间 1280×720（原点是图片左上角，不是父卡片）。
        /// 故窗口大小、DPI 怎么变，填过的坐标永远对应图上的同一个点。
        /// </para>
        /// <para>
        /// ⚠️ **允许为负、也允许超出 1280**：卡片可以拖到图片外的留白区（用户定案
        /// 2026-11-02），只要还在父卡片范围内。故不能用 <c>&gt;= 0</c> 判「有没有摆过」——
        /// 必须用 null（曾经的写法把「拖到图左边」误判成「没摆过」，一重排就跳回默认位置）。
        /// </para>
        /// </summary>
        public double? LiveX { get; set; }

        /// <summary>实时画布上的 Y 坐标（DIP）；null = 从未摆放过，由界面给默认位置。</summary>
        public double? LiveY { get; set; }

        /// <summary>实时卡片「项名称」字号（DIP）。默认 12。</summary>
        public double LiveNameFontSize { get; set; } = AcquisitionTaskRules.DefaultLiveNameFontSize;

        /// <summary>实时卡片「数值」字号（DIP）。默认 24 —— 数值要醒目，故比名称大得多。</summary>
        public double LiveValueFontSize { get; set; } = AcquisitionTaskRules.DefaultLiveValueFontSize;

        /// <summary>
        /// 实时卡片上**浮点数**的小数显示位数（2026-11-06 用户定案，默认 1 位）。
        /// <para>
        /// 只对 <see cref="DataType.Real"/> 生效；整数 / BOOL / STRING 一个字符都不受影响
        /// （见 <see cref="AcquisitionLiveText.Format"/>）。
        /// 用户原话：「如果是浮点数数据，则选项显示：小数显示位数（默认1位）」。
        /// </para>
        /// <para>
        /// ⚠️ **存在数据项上而不是任务组上**：同一个任务组里各变量的量纲不同
        /// （压力要 2 位、温度 1 位），按组统一会逼用户二选一。
        /// </para>
        /// </summary>
        public int LiveFloatDecimals { get; set; } = AcquisitionTaskRules.DefaultLiveFloatDecimals;

        /// <summary>是否已经摆放过（两个坐标都有值）。</summary>
        [JsonIgnore]
        public bool HasLivePosition => LiveX.HasValue && LiveY.HasValue;
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

        /// <summary>
        /// 执行完成输出变量名（2026-11-10）。
        /// <para>
        /// 口径照扫码器的「执行结果输出变量」：**正常执行完成写 1、异常执行完成写 2**
        /// （<see cref="AcquisitionTaskRules.ResultSuccess"/> / <see cref="AcquisitionTaskRules.ResultFailure"/>），
        /// 两个值固定不可配。空串 = 不写结果。
        /// </para>
        /// <para>
        /// ⚠️ 只接受 INT / UINT / DINT / UDINT（写入值 1 / 2 不需要更大范围，非整数类型装不下）。
        /// 填了不存在或类型不对的名字算**配置错误**，整组拦下（判据在
        /// <see cref="AcquisitionTaskCheck"/>，对话框与它共用
        /// <see cref="AcquisitionTaskRules.TryValidateCompletionVariable"/>）。
        /// </para>
        /// <para>
        /// 有映射地址时经 FC06 / FC16 回写 PLC；无地址只改内存（与扫码器、写入任务同一口径）。
        /// </para>
        /// </summary>
        public string CompletionVariableName { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用（**任务跑不跑**）。
        /// <para>⚠️ 与 <see cref="RecordEnabled"/> 是两件事：这个管「采不采 / 写不写」，
        /// 那个管「采到之后记不记文件」。停采集不影响已有记录文件。</para>
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 日志记录启用（**执行时往运行日志里记不记一行**）。
        /// <para>
        /// 默认**打开**（用户定案 2026-10-30）：日志本来就是给现场排查用的，
        /// 新建一个任务组却什么都看不到，只会让人以为它没跑。
        /// </para>
        /// <para>
        /// ⚠️ 与 <see cref="RecordEnabled"/> 是两件事：这个管「界面上看不看得见」，
        /// 那个管「磁盘上有没有 CSV」。关掉日志不影响记录文件，反之亦然。
        /// </para>
        /// </summary>
        public bool LogEnabled { get; set; } = true;

        /// <summary>
        /// 数据记录启用（**采到之后写不写 CSV**）。
        /// <para>默认关闭：没配路径就闷头写文件，比不写更糟。</para>
        /// </summary>
        public bool RecordEnabled { get; set; }

        /// <summary>
        /// 数据记录保存路径 —— **父目录**，任务组自己的子文件夹建在它下面。
        /// <para>空串 = 用默认的 <see cref="AcquisitionTaskRules.DefaultSavePath"/>；
        /// 存量数据（本字段加入之前保存的）都是空串，靠这个兜住。</para>
        /// </summary>
        public string SavePath { get; set; } = string.Empty;

        /// <summary>
        /// 实时数据视图的**背景图片**相对路径（相对 exe 目录，如 <c>Images\cy50571.png</c>）。P1.4。
        /// <para>
        /// ⚠️ 存**相对路径**、且图片拷进 exe 旁的 <c>Images\</c>：存绝对路径的话，
        /// 现场机换个盘符或清一次 D 盘，图就没了，而用户完全不知道
        /// （表现为实时页一片空白）。拷进来则随文件夹一起搬走。
        /// </para>
        /// <para>空串 = 这个任务组还没配图（实时页只显示卡片，没有背景）。</para>
        /// </summary>
        public string LiveImagePath { get; set; } = string.Empty;

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

        /// <summary>默认保存根目录名（建在 exe 旁边，与三份 json 同一层）</summary>
        public const string DefaultSaveFolderName = "DeviceData";

        /// <summary>记录文件扩展名（本版只做 CSV，xlsx 已从需求里去掉）</summary>
        public const string RecordExtension = ".csv";

        // ---------- 实时数据视图（P1.4，2026-10-31） ----------

        /// <summary>
        /// 实时画布的**设计尺寸**（DIP）。固定不随窗口变 —— 坐标填一次永久有效。
        /// <para>
        /// ⚠️ 不要改成「按窗口等比缩放」：那样同一个坐标在不同窗口大小下落在图上的**不同位置**，
        /// 而工位图配控件必须能对到 1px。卡片区放不下时由外层 ScrollViewer 滚动（方案 C）。
        /// </para>
        /// </summary>
        public const double LiveCanvasWidth = 1280;

        /// <summary>实时画布的设计高度（DIP），与 <see cref="LiveCanvasWidth"/> 同为 16:9。</summary>
        public const double LiveCanvasHeight = 720;

        /// <summary>卡片「项名称」默认字号。</summary>
        public const double DefaultLiveNameFontSize = 20;

        /// <summary>
        /// 卡片「数值」默认字号。
        /// <para>
        /// 2026-11-04 用户定为与名称**同字号**（20），早前是名称 12 / 数值 24。
        /// 于是两者靠「名称灰、数值黑 + 加粗」和数值外那圈框区分，不再靠字号差。
        /// </para>
        /// </summary>
        public const double DefaultLiveValueFontSize = 20;

        /// <summary>可选字号档位（选项框下拉框用）。</summary>
        public static readonly double[] LiveFontSizes = { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 42, 48 };

        /// <summary>
        /// 新卡片自动摆放的起始位置与步长。按行蛇形铺开，避免叠在一起
        /// （叠住会让人以为「刚加的项没生效」）。
        /// </summary>
        public const double LiveAutoPlaceStart = 16;

        /// <summary>自动摆放的横向步长。</summary>
        public const double LiveAutoPlaceStepX = 150;

        /// <summary>自动摆放的纵向步长。</summary>
        public const double LiveAutoPlaceStepY = 84;

        /// <summary>自动摆放每行能放几个（1280 宽，留边距后约 8 个）。</summary>
        public const int LiveAutoPlacePerRow = 8;

        /// <summary>STRING 类型在实时卡片上最多显示多少个字符，超出以「…」结尾。</summary>
        public const int LiveStringMaxChars = 20;

        /// <summary>整数在实时卡片上最多显示几位（超出以「…」结尾）。</summary>
        public const int LiveIntMaxDigits = 7;

        /// <summary>浮点在实时卡片上，整数部分最多几位。</summary>
        public const int LiveFloatIntDigits = 5;

        /// <summary>
        /// 浮点小数显示位数的**默认值**（2026-11-06 用户定案：默认 1 位）。
        /// <para>
        /// ⚠️ 早先这里是写死的 2 位（常量 <c>LiveFloatFracDigits</c>）。现在改成
        /// **每个数据项自己配**（<see cref="AcquisitionItem.LiveFloatDecimals"/>），
        /// 本常量只作缺省值 —— 旧 json 没有这个字段，读出来就是它。
        /// </para>
        /// </summary>
        public const int DefaultLiveFloatDecimals = 1;

        /// <summary>可选的小数位数档位（选项框下拉框用）。</summary>
        public static readonly int[] LiveFloatDecimalOptions = { 0, 1, 2, 3, 4 };

        /// <summary>小数位数上限（= <see cref="LiveFloatDecimalOptions"/> 的表尾）。</summary>
        public const int MaxLiveFloatDecimals = 4;

        /// <summary>截断用的省略号（单字符，不占两个字的宽度）。</summary>
        public const string Ellipsis = "…";

        /// <summary>
        /// 任务名称里**不允许**出现的字符 —— 因为任务名称要直接当文件夹名用。
        /// <para>
        /// ⚠️ 这不是洁癖：名称当路径用之后，<c>a/b</c> 会在磁盘上凭空多出一层目录，
        /// <c>a:b</c> 在 NTFS 上会写进备用数据流，两者都**不报错**、只是行为诡异。
        /// </para>
        /// </summary>
        public static readonly char[] InvalidNameChars =
            { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>Windows 保留设备名。做成文件夹会失败或行为异常，提前挡掉。</summary>
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>默认保存根目录（<c>exe 旁\DeviceData</c>）</summary>
        public static string DefaultSavePath =>
            Path.Combine(AppContext.BaseDirectory, DefaultSaveFolderName);

        /// <summary>
        /// 记录文件的完整路径：<c>{父目录}\{任务组名称}\{当天日期}.csv</c>。
        /// <para>
        /// ⚠️ 文件夹按**任务组名称**建（用户定案），不是序号 —— 序号会随增删重排，
        /// 用它当目录名会让两组的数据混进同一个文件夹。
        /// </para>
        /// <para>
        /// ⚠️ 但名称**可变**：改名后新数据写进新文件夹，旧文件夹原地保留。
        /// 这是「改名不搬家」的有意选择（不搬不会丢数据，最多多一个旧目录）。
        /// </para>
        /// </summary>
        public static string ResolveRecordFolder(AcquisitionTask task, DateTime date) =>
            Path.Combine(ResolveRecordDirectory(task), DateStem(date) + RecordExtension);

        /// <summary>
        /// 该任务组的**记录目录**：<c>{保存路径}\{任务组名称}</c>。
        /// <para>
        /// ⚠️ 文件夹按**任务组名称**建（用户定案），不是序号 —— 序号会随增删重排，
        /// 用它当目录名会让两组的数据混进同一个文件夹。
        /// </para>
        /// </summary>
        public static string ResolveRecordDirectory(AcquisitionTask task)
        {
            string parent = string.IsNullOrWhiteSpace(task?.SavePath) ? DefaultSavePath : task.SavePath!;
            return Path.Combine(parent, task?.Name ?? string.Empty);
        }

        /// <summary>记录文件的日期主干（<c>2026-10-26</c>）。日期在**文件名**里，因为时间列只有时分秒。</summary>
        public static string DateStem(DateTime date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// 同一天的第 <paramref name="index"/> 个记录文件：1 → <c>2026-10-26.csv</c>，
        /// 2 → <c>2026-10-26_2.csv</c>，依次往后。
        /// <para>
        /// ⚠️ **列集合一变就换新文件**（用户定案）。表头只在建文件时写一次，
        /// 若中途增删/改名数据项还往同一文件追加，表头与数据行就**列数不等** ——
        /// Excel 打开不报错，只是列错位，等发现时已经攒了一周脏数据。
        /// </para>
        /// </summary>
        public static string RecordFileName(DateTime date, int index)
        {
            string stem = DateStem(date);
            return index <= 1
                ? stem + RecordExtension
                : stem + "_" + index.ToString(CultureInfo.InvariantCulture) + RecordExtension;
        }

        /// <summary>
        /// 从文件名反解出「当天第几个」；不是本组当天的文件返回 0。
        /// 与 <see cref="RecordFileName"/> 严格互逆。
        /// </summary>
        public static int ParseRecordFileIndex(string stem, string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return 0;
            }

            string name = Path.GetFileNameWithoutExtension(fileName);

            if (string.Equals(name, stem, StringComparison.Ordinal))
            {
                return 1;
            }

            string prefix = stem + "_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return 0;
            }

            return int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int index) && index >= 2
                ? index
                : 0;
        }

        /// <summary>任务类型显示名</summary>
        public static string DescribeKind(AcquisitionTaskKind kind) =>
            kind == AcquisitionTaskKind.Write ? "数据写入" : "数据读取";

        /// <summary>
        /// 执行方式的**短词**版（<c>周期</c> / <c>触发</c>）。
        /// <para>
        /// 两处共用：① 任务表的「执行方式」列；② 运行日志的行首（<c>触发读取任务</c>）。
        /// </para>
        /// <para>
        /// ⚠️ 任务表里**只写这两个词**（用户定案 2026-10-29：「只显示为周期或者触发即可，
        /// 不需要那么详细」）。原先这里给的是「周期 5s」/「触发（上升沿）」，
        /// 理由是「列表里要一眼看出周期多长」—— 那条已被推翻：秒数和边沿语义在
        /// 「…」任务选项对话框里看得到，列里挤成六七个字反而把「任务名称」挤窄了。
        /// </para>
        /// </summary>
        public static string DescribeShortTrigger(AcquisitionTriggerMode mode) =>
            mode == AcquisitionTriggerMode.Edge ? "触发" : "周期";

        /// <summary>
        /// 运行日志里的**短词**版任务类型（<c>读取</c> / <c>写入</c>）。
        /// <para>
        /// ⚠️ 与 <see cref="DescribeKind"/>（<c>数据读取</c>）**不是同一套**：
        /// 界面上单列一格，写全称更清楚；日志里跟在「任务」前面，
        /// 拼成「触发读取任务」比「触发数据读取任务」顺口（用户定案）。
        /// </para>
        /// </summary>
        public static string DescribeShortKind(AcquisitionTaskKind kind) =>
            kind == AcquisitionTaskKind.Write ? "写入" : "读取";


        /// <summary>
        /// 运行日志的行首：<c>任务组1（压力采集）：触发读取任务</c>。
        /// <para>
        /// 序号给**当下定位**，名称给**事后可读**（用户定案）。名称是写日志那一刻的快照，
        /// 之后改名历史行不跟着改 —— 那是历史，改了就查不清当时是哪个组了。
        /// </para>
        /// </summary>
        public static string DescribeLogPrefix(AcquisitionTask task, int index)
        {
            if (task == null)
            {
                return string.Empty;
            }

            return "任务组" + index.ToString(CultureInfo.InvariantCulture)
                + "（" + (task.Name ?? string.Empty) + "）："
                + DescribeShortTrigger(task.Trigger) + DescribeShortKind(task.Kind) + "任务";
        }

        /// <summary>执行成功的日志。成功**不带原因**（用户定案），也就不留冒号。</summary>
        public static string DescribeLogSuccess(AcquisitionTask task, int index) =>
            DescribeLogPrefix(task, index) + (task != null && task.RecordEnabled
                ? "执行完成"
                : "执行完成（未启用数据记录）");

        /// <summary>执行失败的日志。失败**必带原因** —— 只说「失败」等于没说。</summary>
        public static string DescribeLogFailure(AcquisitionTask task, int index, string reason) =>
            DescribeLogPrefix(task, index) + "执行失败：" + (reason ?? string.Empty);

        // ---------- 执行完成输出变量（2026-11-10） ----------

        /// <summary>
        /// 「正常执行完成」写进执行完成输出变量的值。
        /// <para>与扫码器的 <c>ScannerRules.ResultSuccess</c> 刻意同值：同一套 1 / 2 口径。</para>
        /// </summary>
        public const string ResultSuccess = "1";

        /// <summary>「异常执行完成」写进执行完成输出变量的值（超时以外的执行失败同样写它）。</summary>
        public const string ResultFailure = "2";

        /// <summary>执行完成输出变量框下的说明文字（界面上那一行小字）。</summary>
        public const string ResultValueHint = "正常执行完成：1   异常执行完成：2";

        /// <summary>
        /// 执行完成输出变量允许的数据类型：只认四种整数。
        /// <para>写入值固定为 1 / 2，BOOL / REAL / STRING 要么装不下、要么语义不对。</para>
        /// </summary>
        public static bool IsCompletionVariableType(DataType type) =>
            type is DataType.Int or DataType.UInt or DataType.DInt or DataType.UDInt;

        /// <summary>
        /// 校验「执行完成输出变量」：留空合法；填了必须是存在的全局变量，
        /// 且类型是 <see cref="IsCompletionVariableType"/> 认可的四类整数。
        /// <para>
        /// ⚠️ **对话框与配置检查必须共用这一处**（<see cref="AcquisitionTaskCheck"/> 也调它）：
        /// 两边各写一套迟早出现「对话框放行、整组配置错误」的自相矛盾。
        /// </para>
        /// <para>
        /// 只读区（X / 离散输入 / 输入寄存器）**不在这里拦**：与扫码器同口径，
        /// 运行期由 <c>AcquisitionWrite.TryBuild</c> 拒写并记一条日志。
        /// </para>
        /// </summary>
        /// <param name="name">用户填的变量名（允许空）。</param>
        /// <param name="findVariable">全局变量查表；<c>null</c> 时非空名字一律算查不到。</param>
        /// <param name="variable">校验通过时解析出的变量；留空或失败时为 <c>null</c>。</param>
        /// <param name="error">失败原因（可直接显示 / 拼进配置问题清单）。</param>
        public static bool TryValidateCompletionVariable(
            string? name,
            Func<string, GlobalVariable?>? findVariable,
            out GlobalVariable? variable,
            out string error) =>
            TryValidateCompletionVariable(name, findVariable, out variable, out _, out error);

        /// <summary>
        /// 同上，额外给出**元素下标**（数组元素时非空）—— 配置检查要比对「是不是同一个引用」。
        /// </summary>
        public static bool TryValidateCompletionVariable(
            string? name,
            Func<string, GlobalVariable?>? findVariable,
            out GlobalVariable? variable,
            out int? elementIndex,
            out string error)
        {
            variable = null;
            elementIndex = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            // 数组只能对元素操作：var1 整条被引用时由 VariableReference 明确报错，
            // 不能退化成「不存在」（用户定案 2026-11-10）。
            if (!VariableReference.TryResolve(
                    name, findVariable, allowWholeArray: false,
                    out GlobalVariable? found, out int? index, out string resolveError))
            {
                error = "执行完成输出变量" + resolveError;
                return false;
            }

            // 元素看**元素类型**，整条标量看变量类型
            DataType type = index is int ? found!.ElementType : found!.Type;

            if (!IsCompletionVariableType(type))
            {
                error = "执行完成输出变量只能是 INT / UINT / DINT / UDINT，当前是 "
                    + DataTypes.Get(type).DisplayName;
                return false;
            }

            // 只读区（X / 离散输入 / 输入寄存器）写不进去 —— 与写入任务同一口径，
            // 在这里就报配置错误，而不是等到运行期写失败只留一条日志（2026-11-11）。
            if (found.IsReadOnly)
            {
                error = "执行完成输出变量「" + name.Trim() + "」映射到 " + found.Address
                    + "，属于只读区（输入元件），不能写入";
                return false;
            }

            variable = found;
            elementIndex = index;
            return true;
        }

        /// <summary>秒数格式化：整数不带小数点（5 而不是 5.0），小数最多三位。</summary>
        public static string FormatSeconds(double seconds) =>
            seconds.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// 任务名称校验：非空、不超长、**能当文件夹名**。
        /// <para>
        /// 后三条（非法字符 / 结尾空格或点 / Windows 保留名）是名称兼作文件夹名之后补的：
        /// 只查长度的话，名字一路绿灯存进 json，等到写文件那步才炸，
        /// 而那时用户早就忘了自己填过什么。
        /// </para>
        /// </summary>
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

            int badIndex = name.IndexOfAny(InvalidNameChars);
            if (badIndex >= 0)
            {
                error = "任务名称不能包含 " + name[badIndex] + "（要当文件夹名用）";
                return false;
            }

            // Windows 会静默吃掉结尾的空格和点，于是「压力」和「压力.」指向同一个目录 ——
            // 界面上却是两个不同的任务组，落盘后互相覆盖
            if (name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
            {
                error = "任务名称不能以空格或点结尾（要当文件夹名用）";
                return false;
            }

            if (Array.IndexOf(ReservedNames, name.Trim().ToUpperInvariant()) >= 0)
            {
                error = "任务名称 " + name + " 是 Windows 保留名，请换一个";
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

        /// <summary>
        /// 写入变量的数据类型必须与**来源变量完全相同**（用户定案 2026-10-27）。
        /// <para>
        /// ⚠️ 不做隐式转换、不做「兼容放宽」：写入是**改变设备状态**的动作。
        /// INT → REAL 看着无损，但寄存器宽度、字节序、值域一起变，
        /// 配错的后果是往 PLC 里丢一个完全不同的数，事后极难追。
        /// </para>
        /// </summary>
        public static bool WriteTypesMatch(DataType sourceType, DataType targetType) =>
            sourceType == targetType;

        /// <summary>数据类型不一致时的提示文案（进 ToolTip，与红字配套）。</summary>
        public static string DescribeTypeMismatch(
            string sourceName, DataType sourceType, string targetName, DataType targetType) =>
            "数据类型必须与来源变量相同：来源「" + sourceName + "」是 "
            + DataTypes.Get(sourceType).DisplayName + "，写入变量「" + targetName + "」是 "
            + DataTypes.Get(targetType).DisplayName;

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

        public static List<AcquisitionTask> Load() => AtomicFile.ReadList<AcquisitionTask>(FilePath, Options);

        public static bool Save(IEnumerable<AcquisitionTask> tasks) =>
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(tasks, Options));
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

            // 旧 json 没有 Id / 实时布局字段 —— 在这里补齐（2026-10-31，P1.4）。
            // 补完不必立刻落盘：下一次任何保存都会把它写进去，这里是「能省一次写就不写」。
            foreach (AcquisitionTask task in _items)
            {
                foreach (AcquisitionItem item in task.Items ?? new List<AcquisitionItem>())
                {
                    if (string.IsNullOrEmpty(item.Id))
                    {
                        item.Id = NewItemId();
                    }

                    // ⚠️ 2026-11-02：坐标从「−1 = 没摆过」改成 null = 没摆过
                    // （因为卡片现在允许拖到图片左侧，负坐标是**合法**值）。
                    // 旧文件里没摆过的项存的就是 −1，必须在这里转成 null，
                    // 否则那些项会被当成「摆在 (−1,−1)」而堆在左上角外面看不见。
                    if (item.LiveX == -1 && item.LiveY == -1)
                    {
                        item.LiveX = null;
                        item.LiveY = null;
                    }
                }
            }
        }

        /// <summary>生成数据项的稳定标识（GUID 的 N 格式，无连字符）。</summary>
        public static string NewItemId() => Guid.NewGuid().ToString("N");

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

    /// <summary>
    /// 实时数据卡片上「数值」那一行的文本（P1.4，2026-10-31）。
    /// <para>
    /// **纯函数、不引 WPF**，故可离线断言。规则由用户定案：
    /// </para>
    /// <list type="bullet">
    /// <item><c>BOOL</c> → <c>TRUE</c> / <c>FALSE</c>（不是 1/0）；</item>
    /// <item>数值型**一律十进制**，不看变量自己配的显示格式（Hex/Bin 也不管）；</item>
    /// <item><c>STRING</c> 最多 20 个字符，超出以 <c>…</c> 结尾；</item>
    /// <item>整数最多 7 位；浮点整数部分最多 5 位、小数部分最多 2 位；超出都以 <c>…</c> 结尾。</item>
    /// </list>
    /// <para>
    /// ⚠️ **不要从 <see cref="GlobalVariable.Value"/> 反解**：那是按显示格式转好的文本，
    /// 一个 INT/Hex 变量的值是 <c>"2A"</c> 而不是 <c>42</c>，而 STRING 里也可能正好是
    /// <c>"2A"</c> —— 文本没法区分「十六进制数」和「内容就是 2A 的字符串」。
    /// 故数值一律走 <see cref="GlobalVariable.Numeric"/>（回填时顺手存的原始数值）。
    /// </para>
    /// </summary>
    public static class AcquisitionLiveText
    {
        /// <summary>没采到值 / 变量不存在时的占位（明确区别于「值就是 0」）。</summary>
        public const string Placeholder = "—";

        /// <summary>
        /// 把一个全局变量的当前值渲染成卡片文本。
        /// </summary>
        /// <param name="variable">变量；<c>null</c> = 名称没填或查不到 → 返回 <see cref="Placeholder"/>。</param>
        /// <param name="floatDecimals">
        /// 浮点小数位数（2026-11-06 新增）。**只对 <see cref="DataType.Real"/> 生效**；
        /// 传 <c>null</c> 或越界值按 <see cref="AcquisitionTaskRules.DefaultLiveFloatDecimals"/> 处理。
        /// </param>
        public static string Format(GlobalVariable? variable, int? floatDecimals = null) =>
            Format(variable, null, floatDecimals);

        /// <summary>
        /// 渲染**数组元素**（<c>var1[0]</c>）的值；<paramref name="elementIndex"/> 为空时按整条变量。
        /// <para>元素值的文本一律十进制，数值形态来自 <see cref="GlobalVariable.GetElementNumeric"/>。</para>
        /// </summary>
        public static string Format(
            GlobalVariable? variable, int? elementIndex, int? floatDecimals = null)
        {
            if (variable == null)
            {
                return Placeholder;
            }

            if (elementIndex is int index)
            {
                if (variable.Type != DataType.Array || index < 0 || index >= variable.Length)
                {
                    return Placeholder;
                }

                DataType elementType = variable.ElementType;

                if (elementType == DataType.Bool)
                {
                    return variable.GetElementNumeric(index) is { } flag
                        ? (flag != 0 ? "TRUE" : "FALSE")
                        : Placeholder;
                }

                if (variable.GetElementNumeric(index) is not { } elementNumber)
                {
                    return Placeholder;
                }

                return elementType == DataType.Real
                    ? FormatFloat(elementNumber, floatDecimals)
                    : FormatInteger(elementNumber);
            }

            if (variable.Type == DataType.Bool)
            {
                // BOOL 走 Numeric：线圈回填时存的是 0/1，STRING/整数都不走这一支
                return variable.Numeric is { } b
                    ? (b != 0 ? "TRUE" : "FALSE")
                    : Placeholder;
            }

            if (variable.Type == DataType.String)
            {
                // STRING 没有数值形态，只能读 Value（它本来就是字符串内容，不存在「格式」问题）
                return Truncate(variable.Value ?? string.Empty, AcquisitionTaskRules.LiveStringMaxChars);
            }

            if (variable.Numeric is not { } number)
            {
                return Placeholder;
            }

            return variable.Type == DataType.Real
                ? FormatFloat(number, floatDecimals)
                : FormatInteger(number);
        }

        /// <summary>
        /// 整数：最多 7 位（不含负号）。超出就退化成科学计数法的「…」形式。
        /// </summary>
        public static string FormatInteger(double value)
        {
            // 先四舍五入到整数：数值字段是 double，整数类型理论上不会有小数，
            // 但 32 位整数在 double 里可能有极小的表示误差，直接截断会差 1
            long rounded = (long)Math.Round(value, MidpointRounding.AwayFromZero);
            string text = rounded.ToString(CultureInfo.InvariantCulture);

            // 负号不算位数
            int digits = text.Length - (text.StartsWith("-", StringComparison.Ordinal) ? 1 : 0);

            if (digits <= AcquisitionTaskRules.LiveIntMaxDigits)
            {
                return text;
            }

            return rounded.ToString("E2", CultureInfo.InvariantCulture) is { } sci
                ? Truncate(sci, AcquisitionTaskRules.LiveIntMaxDigits + 4)
                : rounded.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 浮点：整数部分最多 5 位；小数位数由 <paramref name="decimals"/> 决定
        /// （默认 <see cref="AcquisitionTaskRules.DefaultLiveFloatDecimals"/>）。整数部分超出则以「…」结尾。
        /// </summary>
        /// <param name="decimals">
        /// 小数位数。`null` / 负数 → 默认 1 位；超过 <see cref="AcquisitionTaskRules.MaxLiveFloatDecimals"/>
        /// 也不报错，按上限夹取（配置里一个荒唐的值不该让卡片显示不出来）。
        /// </param>
        public static string FormatFloat(double value, int? decimals = null)
        {
            if (double.IsNaN(value))
            {
                return "NaN";
            }

            if (double.IsPositiveInfinity(value))
            {
                return "+∞";
            }

            if (double.IsNegativeInfinity(value))
            {
                return "−∞";
            }

            int frac = ClampLiveFloatDecimals(decimals);

            double rounded = Math.Round(value, frac, MidpointRounding.AwayFromZero);

            // ⚠️ **负零要归成零**（2026-11-06 断言测出来的）：
            // −0.04 按 1 位四舍五入得到 −0.0，ToString("F1") 会如实打印 "-0.0" ——
            // 卡片上显示「-0.0」看起来像程序算错了，实际是符号位还留着。
            // 判据用 `== 0`（−0.0 == 0 为真），只在「结果确实是零」时才抹掉负号。
            if (rounded == 0)
            {
                rounded = 0;
            }

            // 整数部分位数（不含负号）
            double abs = Math.Abs(Math.Truncate(rounded));
            string intPart = abs.ToString("F0", CultureInfo.InvariantCulture);

            if (intPart.Length > AcquisitionTaskRules.LiveFloatIntDigits)
            {
                // 整数部分已经放不下 —— 科学计数法比「123456…」更有信息量
                return Truncate(
                    rounded.ToString("E2", CultureInfo.InvariantCulture),
                    AcquisitionTaskRules.LiveFloatIntDigits + 1 + frac + 1);
            }

            // F{n} 会把 123 渲染成 "123.0"（需求就是要**固定**位数，不是「最多」几位）
            return rounded.ToString("F" + frac, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把小数位数夹进合法范围。`null` / 负数 → 默认值；超过上限 → 上限。
        /// <para>纯函数，配 <see cref="FormatFloat"/> 一起断言。</para>
        /// </summary>
        public static int ClampLiveFloatDecimals(int? decimals)
        {
            if (decimals is not { } value || value < 0)
            {
                return AcquisitionTaskRules.DefaultLiveFloatDecimals;
            }

            return value > AcquisitionTaskRules.MaxLiveFloatDecimals
                ? AcquisitionTaskRules.MaxLiveFloatDecimals
                : value;
        }

        /// <summary>按**字符数**截断（不是字节数），超出补省略号。</summary>
        public static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            if (maxChars <= 0)
            {
                return AcquisitionTaskRules.Ellipsis;
            }

            return text.Substring(0, maxChars) + AcquisitionTaskRules.Ellipsis;
        }

        /// <summary>
        /// 卡片标题：项名称优先，**空则回退到变量名** ——
        /// 否则卡片上方一片空白，看不出是哪个项。
        /// </summary>
        public static string Title(AcquisitionItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                return item.Name;
            }

            return string.IsNullOrWhiteSpace(item.VariableName)
                ? "（未命名）"
                : item.VariableName;
        }
    }
}
