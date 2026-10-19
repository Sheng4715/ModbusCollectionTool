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

            Application.Current?.Dispatcher.Invoke(() => Apply(ThemeMode.System));
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
            /// <para>属于「自动连接」这一组设置，后续同组还会加别的开关（见设置页卡片）。</para>
            /// </summary>
            public bool AutoConnectPlc { get; set; } = true;
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

        private static void Save(AppSettings settings)
        {
            try
            {
                // 落盘的是**整个**对象，故归一化必须放在这唯一的出口上。
                // 曾经只在 SetMode 里调用，结果是：任何新增的整对象保存路径
                // （如 SetTargetNic）都会把「看一眼路径」留下的绝对默认路径写进文件。
                // 放在这里，调用方就不必记得这件事。
                NormalizeLogDirectory();

                string json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 忽略写入失败：下次启动回到默认主题，不影响功能
            }
        }
    }
}
