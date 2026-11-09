using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DSH_Modbus采集工具.Themes
{
    /// <summary>主题模式：浅色 / 深色 / 跟随系统。</summary>
    public enum ThemeMode
    {
        Light,
        Dark,
        System,
    }

    /// <summary>
    /// 主题管理：把 <c>Themes/Light.xaml</c> 或 <c>Themes/Dark.xaml</c> 换进
    /// <see cref="Application.Resources"/> 的合并字典末位，并持久化用户选择。
    ///
    /// <para>
    /// **为什么是「换字典」而不是「逐个改控件属性」**：全部颜色已经在 XAML 里引用
    /// <c>DynamicResource</c>，换掉字典即整体生效，不需要遍历可视树，也不需要
    /// 记录每个控件的原色。
    /// </para>
    /// <para>
    /// **为什么两个字典的键必须一致**：换字典时控件只按名字取值，缺键会静默回退成
    /// 未设置（多数表现为黑色或透明）。这类问题只在切到某个主题时才暴露，很难查，
    /// 故 <see cref="ValidateKeyParity"/> 在启动时主动校验并直接抛异常。
    /// </para>
    /// </summary>
    public static class ThemeManager
    {
        private const string LightUri = "Themes/Light.xaml";
        private const string DarkUri = "Themes/Dark.xaml";

        private static ResourceDictionary? _current;

        /// <summary>当前模式（含「跟随系统」这一原始选择，不是解析后的实际主题）。</summary>
        public static ThemeMode Mode { get; private set; } = ThemeMode.System;

        /// <summary>当前实际生效的主题（「跟随系统」已解析为 Light 或 Dark）。</summary>
        public static bool IsDarkEffective { get; private set; }

        /// <summary>主题变化后触发，供窗口同步非 XAML 的颜色（如 IoPoint 的灯刷、日志级别色）。</summary>
        public static event Action? ThemeChanged;

        /// <summary>最近一次设置落盘失败的原因；成功写入后为 <c>null</c>。</summary>
        public static string? LastSaveError { get; private set; }

        /// <summary>设置落盘失败时触发（参数为失败原因），供界面提示用户「改了配置但没存上」。</summary>
        public static event Action<string>? SettingsSaveFailed;

        /// <summary>设置文件路径（与 io-comments.json / data-points.json 同级，都在 exe 旁）。</summary>
        public static string SettingsFilePath =>
            Path.Combine(AppContext.BaseDirectory, "app-settings.json");

        /// <summary>
        /// 启动时调用：读取持久化选择、做键一致性校验、应用主题、挂系统主题监听。
        /// 必须在 <see cref="Application.Resources"/> 可用之后（即 App 构造完成后）调用。
        /// </summary>
        public static void Initialize()
        {
            // 键不一致属于开发期配置错误，宁可启动即崩也不要静默显示错色
            ValidateKeyParity();

            _settings = Load();
            Mode = ParseMode(_settings.Theme);

            // 显式指定的主题不随后续系统切换而变；「跟随系统」才挂监听
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            Apply(Mode);
        }

        // ==== 日志保存（P5-2） ====
        // 与主题共用 app-settings.json：两者都是「应用级偏好」，拆两个文件会让
        // 设置页的读取分散在两处。日志部分只在这里存取值，落盘逻辑在
        // Logging/DeviceLogFileWriter.cs。

        /// <summary>日志保存是否启用（默认启用）。</summary>
        public static bool LogSaveEnabled => _settings.LogSaveEnabled;

        /// <summary>日志保存目录（默认 exe 旁的 <c>Log</c>；存空串时也回退到默认）。</summary>
        public static string LogDirectory =>
            string.IsNullOrWhiteSpace(_settings.LogDirectory)
                ? Logging.DeviceLogFileWriter.DefaultDirectory
                : _settings.LogDirectory;

        /// <summary>
        /// 改日志保存设置并落盘。<paramref name="directory"/> 传 <c>null</c> 表示不改目录。
        /// </summary>
        public static void SetLogSave(bool enabled, string? directory = null)
        {
            _settings.LogSaveEnabled = enabled;
            if (directory != null)
            {
                _settings.LogDirectory = directory;
            }

            Save(_settings);
        }

        /// <summary>
        /// 切换主题并落盘。
        ///
        /// <para>
        /// ⚠️ 落盘的是**整个** <see cref="AppSettings"/>（主题 / 日志 / 网卡同处一个文件），
        /// 所以这里会把日志目录一并**固化**下来。为保持「空串 = 用默认目录」这个不变式，
        /// 归一回空串的动作统一放在 <see cref="Save"/> 里，**不要**在各 Setter 里各写一遍 ——
        /// 漏写一处的后果是：用户在设置页**看了一眼**路径（失焦即写盘）之后，
        /// 即使他从未打算自定义，文件里也会多出一条写死的绝对路径，
        /// 一旦 exe 被移动/复制到别处，日志就会继续写回旧位置。
        /// </para>
        /// </summary>
        public static void SetMode(ThemeMode mode)
        {
            Mode = mode;
            _settings.Theme = mode.ToString();
            Apply(mode);
            Save(_settings);
        }

        // ==== 目标网卡（方案 A：记住用户选的网卡） ====
        // 存**网卡名**（如 "WLAN"）而不是下拉框的显示文本 "WLAN(192.168.1.15)"：
        // 显示文本里嵌着 IP，而同一块网卡换个网络 IP 就变了（笔记本的 WLAN 在家
        // 是 192.168.1.x、在公司是 10.0.0.x），按文本匹配恰好在最需要它的场景失效。

        /// <summary>上次选用的网卡名；空串表示「没有偏好，用列表第 0 项」。</summary>
        public static string TargetNic => _settings.TargetNic;

        /// <summary>
        /// 记住用户选用的网卡名并落盘。
        /// <paramref name="name"/> 传空串表示清除偏好。
        /// </summary>
        public static void SetTargetNic(string name)
        {
            _settings.TargetNic = name ?? string.Empty;
            Save(_settings);
        }

        // ==== 目标 IP / 端口（P?：连接参数持久化） ====

        /// <summary>上次使用的目标 IP；空串表示没保存过，界面用默认值。</summary>
        public static string TargetIp => _settings.TargetIp;

        /// <summary>记住目标 IP 并落盘。传空串表示清除（回落到界面默认值）。</summary>
        public static void SetTargetIp(string? ip)
        {
            _settings.TargetIp = (ip ?? string.Empty).Trim();
            Save(_settings);
        }

        /// <summary>上次使用的端口号；0 表示没保存过，界面用默认值 502。</summary>
        public static int TargetPort => _settings.TargetPort;

        /// <summary>记住端口号并落盘。传 0 表示清除（回落到界面默认值）。</summary>
        public static void SetTargetPort(int port)
        {
            _settings.TargetPort = port is >= 1 and <= 65535 ? port : 0;
            Save(_settings);
        }

        // ==== 自动连接 ====

        /// <summary>启动后是否自动连接 PLC（默认 true）。</summary>
        public static bool AutoConnectPlc => _settings.AutoConnectPlc;

        /// <summary>记住「自动连接 PLC」开关并落盘。</summary>
        public static void SetAutoConnectPlc(bool enabled)
        {
            _settings.AutoConnectPlc = enabled;
            Save(_settings);
        }

        // ==== 应用标题 ====

        /// <summary>
        /// 自定义应用标题；空串表示「没设置过」，界面用 <see cref="Modeling.AppTitle"/> 的默认值。
        /// </summary>
        public static string AppTitleText => _settings.AppTitleText;

        /// <summary>记住自定义应用标题并落盘。传空串表示恢复默认。</summary>
        public static void SetAppTitle(string? title)
        {
            // 归一在入口做一次：落到 json 里的必须是清洗后的值（换行/超长/纯空白都要挡住）
            string normalized = Modeling.AppTitle.Normalize(title);
            _settings.AppTitleText = normalized;
            Save(_settings);
        }

        // ==== 总览页「当前视图」 ====

        /// <summary>
        /// 上次停在哪个总览视图；没保存过 / 值不认识时退回
        /// <see cref="Modeling.OverviewViewSetting.Default"/>（= PLC状态机，与出厂外观一致）。
        /// </summary>
        public static Modeling.OverviewViewMode OverviewView =>
            Modeling.OverviewViewSetting.TryParse(_settings.OverviewView, out var mode)
                ? mode
                : Modeling.OverviewViewSetting.Default;

        /// <summary>
        /// 记住总览视图并落盘。
        /// <para>
        /// ⚠️ 值没变时**直接返回、不写盘**：界面切换视图会连同可见性与定时器一起刷新，
        /// 恢复上一次选择时也会走到这个入口，没必要为此重写整份 app-settings.json。
        /// </para>
        /// </summary>
        public static void SetOverviewView(Modeling.OverviewViewMode mode)
        {
            string text = Modeling.OverviewViewSetting.Format(mode);
            if (string.Equals(_settings.OverviewView, text, StringComparison.Ordinal))
            {
                return;
            }

            _settings.OverviewView = text;
            Save(_settings);
        }

        // ==== 设备类型（通讯驱动品牌） ====

        /// <summary>
        /// 上次选用的通讯驱动 **Key**（如 <c>xinje_xd</c>）；空串表示没保存过 → 界面用注册表第 0 个。
        /// <para>
        /// ⚠️ 存 **Key 而不是 DisplayName**：显示名是给人看的文案（「信捷XD/XL系列」），
        /// 以后改文案或调整品牌顺序都会对不上；Key 是稳定标识 ——
        /// 与 <see cref="TargetNic"/> 存网卡名而不是下拉框文本是同一个取舍。
        /// </para>
        /// </summary>
        public static string DeviceTypeKey => _settings.DeviceTypeKey;

        /// <summary>
        /// 记住设备类型并落盘（传空串 = 清除，回落到注册表第 0 个）。
        /// <para>⚠️ 值没变时直接返回、不写盘（与 <see cref="SetOverviewView"/> 同一手法）。</para>
        /// </summary>
        public static void SetDeviceType(string? key)
        {
            string text = key ?? string.Empty;
            if (string.Equals(_settings.DeviceTypeKey, text, StringComparison.Ordinal))
            {
                return;
            }

            _settings.DeviceTypeKey = text;
            Save(_settings);
        }

        /// <summary>
        /// 显式选了默认目录时，存空串而非绝对路径（见 <see cref="SetMode"/>）。
        /// 由 <see cref="Save"/> 统一调用，**不放在各个 Setter 里**。
        /// </summary>
        private static void NormalizeLogDirectory()
        {
            if (string.Equals(_settings.LogDirectory, Logging.DeviceLogFileWriter.DefaultDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings.LogDirectory = string.Empty;
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 只在「跟随系统」下响应；其余模式用户已明确指定，不该被系统设置覆盖。
            // 事件可能来自非 UI 线程，切字典必须回 UI 线程。
            if (Mode != ThemeMode.System || e.Category != UserPreferenceCategory.General)
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
        }

        /// <summary>按模式解析出实际主题并换字典（幂等）。</summary>
        private static void Apply(ThemeMode mode)
        {
            bool dark = mode switch
            {
                ThemeMode.Dark => true,
                ThemeMode.Light => false,
                _ => IsSystemDark(),
            };

            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            var next = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkUri : LightUri, UriKind.Relative),
            };

            // 先加后删：换的瞬间两套键并存，不会出现「取值落到空档」的中间态
            app.Resources.MergedDictionaries.Add(next);
            if (_current != null)
            {
                app.Resources.MergedDictionaries.Remove(_current);
            }

            _current = next;
            IsDarkEffective = dark;
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// 读系统是否处于深色。注册表值缺失（老系统 / 未设置）时按浅色处理 —— 与本应用
        /// 改造前的默认外观一致，不会让老机器突然变深色。
        /// </summary>
        private static bool IsSystemDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                // 0 = 浅色，1 = 深色；缺失时 GetValue 返回 null
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取当前主题下的画刷（C# 侧用）。XAML 走 <c>DynamicResource</c> 自动跟随，
        /// 但 C# 直接赋给控件的画刷必须每次重新取 —— 否则换主题后它仍是旧主题的实例。
        /// 键不存在时返回 <c>null</c>，由调用方决定回退。
        /// </summary>
        public static System.Windows.Media.Brush? GetBrush(string key)
        {
            var res = Application.Current?.TryFindResource(key);
            return res as System.Windows.Media.Brush;
        }

        /// <summary>
        /// 校验浅色/深色两个字典的键集合完全一致，不一致直接抛 —— 见类注释的说明。
        /// </summary>
        private static void ValidateKeyParity()
        {
            var light = new ResourceDictionary { Source = new Uri(LightUri, UriKind.Relative) };
            var dark = new ResourceDictionary { Source = new Uri(DarkUri, UriKind.Relative) };

            var missingInDark = light.Keys.Cast<object>()
                .Select(k => k.ToString() ?? string.Empty)
                .Where(k => !dark.Contains(k))
                .ToList();
            var missingInLight = dark.Keys.Cast<object>()
                .Select(k => k.ToString() ?? string.Empty)
                .Where(k => !light.Contains(k))
                .ToList();

            if (missingInDark.Count > 0 || missingInLight.Count > 0)
            {
                throw new InvalidOperationException(
                    "Themes/Light.xaml 与 Themes/Dark.xaml 的键集合不一致：" +
                    $"Dark 缺少 [{string.Join(", ", missingInDark)}]；" +
                    $"Light 缺少 [{string.Join(", ", missingInLight)}]。");
            }
        }

        // ---------- 持久化（app-settings.json） ----------

        /// <summary>当前设置的内存副本（Initialize 时载入，之后原地改并整体落盘）。</summary>
        private static AppSettings _settings = new();

        /// <summary>设置文件的内容。新增设置项时在此加属性即可，缺字段按默认值处理。</summary>
        private sealed class AppSettings
        {
            /// <summary>序列化为字符串（"Light"/"Dark"/"System"），比存数字更耐改枚举顺序。</summary>
            public string Theme { get; set; } = nameof(ThemeMode.System);

            /// <summary>日志保存开关（P5-2）。用户定稿：**默认启用**。</summary>
            public bool LogSaveEnabled { get; set; } = true;

            /// <summary>日志保存目录（P5-2）。空串表示用默认目录（exe 旁的 Log）。</summary>
            public string LogDirectory { get; set; } = string.Empty;

            /// <summary>
            /// 上次选用的目标网卡**名**（不含 IP）。空串表示没有偏好。
            ///
            /// <para>
            /// 存名字不存显示文本：文本是 "WLAN(192.168.1.15)"，含 IP，
            /// 而同一块网卡换网络后 IP 会变，按文本匹配会在最需要它的场景失效。
            /// 旧版 json 没有这个字段 → 反序列化后保持空串 → 退回列表第 0 项，
            /// 与升级前的行为一致，无需迁移。
            /// </para>
            /// </summary>
            public string TargetNic { get; set; } = string.Empty;

            /// <summary>
            /// 上次使用的目标 IP。空串表示「没保存过」，沿用界面上的默认值
            /// （<c>IpTextBox</c> 的 <c>Text="192.168.1.100"</c>）。
            ///
            /// <para>
            /// 存空串而不是把默认值也写进文件：与 <see cref="LogDirectory"/> 同理，
            /// 升级后的旧 json 没有这个字段 → 反序列化得空串 → 界面上仍是默认 IP，
            /// 与升级前的行为完全一致，无需迁移。
            /// </para>
            /// </summary>
            public string TargetIp { get; set; } = string.Empty;

            /// <summary>上次使用的端口号。0 表示「没保存过」，沿用界面默认 502。</summary>
            public int TargetPort { get; set; }

            /// <summary>
            /// 程序启动后是否自动连接 PLC。用户定稿：**默认打开**。
            /// <para>
            /// 2026-11-05：开关从设置页搬到**设备连接 → PLC连接**页（它只管 PLC，
            /// 摆在「自动连接」大卡里会让人以为扫码器也归它管）。
            /// </para>
            /// </summary>
            public bool AutoConnectPlc { get; set; } = true;

            /// <summary>
            /// 自定义应用标题（P5.6）。空串 = 没设置过，界面用 <c>Modeling.AppTitle</c> 的默认值。
            /// <para>
            /// 与 <see cref="LogDirectory"/> 同一不变式：**等于默认值时也归回空串**
            /// （由 <see cref="Save"/> 统一处理），这样「用户原样敲一遍默认标题」不会
            /// 在 json 里留下一条看着像自定义、实际与默认相同的记录。
            /// </para>
            /// <para>
            /// 旧版 json 没有这个字段 → 反序列化得空串 → 界面显示默认标题，
            /// 与升级前完全一致，无需迁移。
            /// </para>
            /// </summary>
            public string AppTitleText { get; set; } = string.Empty;

            /// <summary>
            /// 总览页「当前视图」的选择（P：2026-11-09 用户要求记住）。
            /// <para>
            /// 存**枚举名**（<c>MachineState</c> / <c>ProductionData</c> / <c>Acquisition</c>），
            /// 不存数字下标 —— 与 <see cref="Theme"/> 同一取舍：以后往下拉框中间插一项，
            /// 数字会被静默解释成别的视图，而枚举名对不上时能安全退回默认值。
            /// </para>
            /// <para>
            /// 旧 json 没有这个字段 → 反序列化后保持默认（<c>MachineState</c>），
            /// 与升级前「每次启动都停在第 0 项」的行为完全一致，无需迁移。
            /// </para>
            /// </summary>
            public string OverviewView { get; set; } =
                Modeling.OverviewViewSetting.Format(Modeling.OverviewViewSetting.Default);

            /// <summary>
            /// 上次选用的通讯驱动 Key（2026-11-09 用户要求持久化）。
            /// <para>
            /// 空串 = 没保存过 → 界面用注册表第 0 个（信捷 XD/XL）；旧 json 没有这个字段
            /// 就是这个状态，与升级前「每次启动都回第 0 项」的行为一致，无需迁移。
            /// </para>
            /// <para>
            /// ⚠️ **字节顺序刻意不跟着存**：它由驱动默认值决定，切换设备类型时整份重置
            /// （用户 2026-10 定案，见 AGENTS-细节.md §8.1）。存下来会让「换了品牌、字序还是
            /// 旧品牌」变成静默错值 —— 两品牌唯一差异就在字符串序（信捷 12 / 汇川 21）。
            /// </para>
            /// </summary>
            public string DeviceTypeKey { get; set; } = string.Empty;
        }

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var loaded = JsonSerializer
                        .Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath));
                    if (loaded != null)
                    {
                        return loaded;
                    }
                }
            }
            catch
            {
                // 损坏 / 不可读 → 用默认（跟随系统）
            }

            return new AppSettings();
        }

        /// <summary>
        /// 把持久化的字符串解析回枚举。用 <c>Enum.TryParse</c> 而非直接强转：
        /// 手改过的 json、或被降级版本写过的 json 都可能带来无效值，
        /// 那种情况下退回「跟随系统」比抛异常合适。
        /// </summary>
        private static ThemeMode ParseMode(string? raw) =>
            Enum.TryParse<ThemeMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                ? mode
                : ThemeMode.System;

        private static bool Save(AppSettings settings)
        {
            try
            {
                // 落盘的是**整个**对象，故归一化必须放在这唯一的出口上。
                // 曾经只在 SetMode 里调用，结果是：任何新增的整对象保存路径
                // （如 SetTargetNic）都会把「看一眼路径」留下的绝对默认路径写进文件。
                // 放在这里，调用方就不必记得这件事。
                NormalizeLogDirectory();

                // 标题同理：等于默认值（或纯空白）就归回空串，与「没设置过」同一个表示
                if (Modeling.AppTitle.IsDefault(_settings.AppTitleText))
                {
                    _settings.AppTitleText = string.Empty;
                }

                string json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true });

                bool ok = Modeling.AtomicFile.WriteAllText(SettingsFilePath, json);
                if (ok)
                {
                    LastSaveError = null;
                    return true;
                }

                string failReason = "设置写入失败：" + SettingsFilePath;
                LastSaveError = failReason;
                SettingsSaveFailed?.Invoke(failReason);
                return false;
            }
            catch (Exception ex)
            {
                // 归一化或序列化异常 → 同样视为写失败并上浮，不再静默
                string failReason = "设置保存异常：" + ex.Message;
                LastSaveError = failReason;
                SettingsSaveFailed?.Invoke(failReason);
                return false;
            }
        }
    }
}
