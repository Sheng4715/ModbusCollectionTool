using System.Reflection;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 项目信息（P5-3）：从程序集元数据读出版本号，配合 csproj 生成的构建日期常量。
    ///
    /// <para>
    /// **为什么读程序集而不是读 csproj**：运行期没有 csproj，只有 exe 里的元数据。
    /// 读程序集还能保证显示的就是**当前正在跑的这个 exe** 的版本 —— 若用户手里是旧
    /// 拷贝，显示出来的也是旧的，不会撒谎。
    /// </para>
    /// <para>
    /// **为什么不用 <c>Assembly.Location</c> 里的文件时间当构建日期**：那个时间会被
    /// 复制/解压/杀软扫描改动，且与"代码什么时候编的"无关。由编译期写入常量才准。
    /// </para>
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>产品名（取自 csproj 的 <c>Product</c>，缺失时退回程序集名）。</summary>
        public static string ProductName
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                return asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product
                    ?? asm.GetName().Name
                    ?? "DSH_Modbus采集工具";
            }
        }

        /// <summary>
        /// 版本号。用 <c>InformationalVersion</c>（即 csproj 的 <c>Version</c>，形如 `0.6.0`），
        /// 它比 <c>AssemblyVersion</c> 多了修订位、更适合给人看。带 `+` 的构建元数据后缀会在
        /// 显示时去掉（那是机器读的部分，对用户是噪声）。
        /// </summary>
        public static string Version
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();

                string? informational = asm
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational))
                {
                    int plus = informational.IndexOf('+');
                    return plus > 0 ? informational[..plus] : informational;
                }

                return asm.GetName().Version?.ToString(3) ?? "未知";
            }
        }

        /// <summary>
        /// 构建日期。由 csproj 的 <c>WriteBuildDate</c> 目标在编译时写入
        /// <c>BuildInfo.BuildDate</c>；设计器/单测等未编译场景下可能取不到，故给个兜底文案。
        /// </summary>
        public static string BuildDate =>
            string.IsNullOrWhiteSpace(BuildInfo.BuildDate) ? "未知" : BuildInfo.BuildDate;

        /// <summary>运行时版本，如 `.NET 8.0.11`。</summary>
        public static string Runtime =>
            $".NET {Environment.Version.ToString(3)}";

        /// <summary>操作系统描述，如 `Windows 10.0.26100 (64 位)`。</summary>
        public static string OperatingSystem
        {
            get
            {
                string bits = Environment.Is64BitProcess ? "64 位" : "32 位";
                return $"Windows {Environment.OSVersion.Version} ({bits})";
            }
        }

        /// <summary>支持的 Modbus 功能码（只读 + 写，按实现列出）。</summary>
        public static string FunctionCodes => "FC01/02/03/04 读，FC05/06/16 写";
    }
}
