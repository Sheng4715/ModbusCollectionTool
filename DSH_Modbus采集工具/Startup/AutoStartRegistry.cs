using System.IO;
using Microsoft.Win32;

namespace DSH_Modbus采集工具.Startup
{
    /// <summary>
    /// 开机自启动的注册表读写（HKCU，只影响当前用户，不需要管理员权限）。
    ///
    /// <para>
    /// **为什么用注册表而不是「启动」文件夹**：注册表 <c>Run</c> 项是 Windows 记录
    /// 自启动的规范位置 —— 任务管理器的「启动应用」页、Autoruns 这类排查工具都认它，
    /// 用户在那些地方也能看到/关掉本程序，不会出现「一个黑盒在偷偷启动」的观感。
    /// 启动文件夹则要往用户目录放快捷方式，还得处理「快捷方式被删」的情形。
    /// </para>
    /// <para>
    /// **为什么不用计划任务**：那需要管理员权限、且会出现在任务计划程序里，
    /// 对一个现场工具来说过重；同时也绕过了「启动应用」页那个用户可见的开关。
    /// </para>
    /// <para>
    /// 判断与拼串等纯规则在 <see cref="AutoStartCommand"/>，本类只做 IO。
    /// 所有失败都通过返回值/异常告知调用方，**不在这里静默吞掉** ——
    /// 自启设置写不进去必须让用户看见（设置页会显示原因）。
    /// </para>
    /// </summary>
    public static class AutoStartRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// 当前进程的可执行文件完整路径（即开机时要拉起来的那个）。
        ///
        /// <para>
        /// 优先用 <see cref="Environment.ProcessPath"/> —— 它就是「谁启动了这个进程」，
        /// 对 apphost（本程序发布的形态）返回的正是 exe 本身。
        /// 但经 <c>dotnet exec</c> / 反编译器一类宿主启动时会返回宿主的 exe，
        /// 那种情况下退回「程序目录 + 程序集名 + .exe」，才不至于把 <c>dotnet.exe</c>
        /// 写进自启项。
        /// </para>
        /// </summary>
        public static string ExecutablePath
        {
            get
            {
                string expectedName = AppDomain.CurrentDomain.FriendlyName + ".exe";
                string? processPath = Environment.ProcessPath;

                if (!string.IsNullOrWhiteSpace(processPath)
                    && string.Equals(
                        Path.GetFileName(processPath),
                        expectedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(processPath);
                }

                return Path.Combine(AppContext.BaseDirectory, expectedName);
            }
        }

        /// <summary>
        /// 读 <c>Run</c> 里登记的命令串；没有登记时返回 <c>null</c>。
        /// </summary>
        public static string? ReadCommand()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AutoStartCommand.ValueName) as string;
        }

        /// <summary>
        /// 读系统的禁用记录（<c>StartupApproved\Run</c> 同名值）；没有记录时返回 <c>null</c>。
        /// </summary>
        public static byte[]? ReadApprovedValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartCommand.StartupApprovedRunKey);
            return key?.GetValue(AutoStartCommand.ValueName) as byte[];
        }

        /// <summary>
        /// 界面应显示的开关状态 = **已登记** 且 **未被系统禁用** 且 **指向当前这个 exe**。
        ///
        /// <para>
        /// 三条缺一不可，各自对应一种真实情形：
        /// ① 没登记 → 本来就没开；
        /// ② 登记了但被「启动应用」页禁用 → 开机不会起来，开关若显示「开」就是撒谎；
        /// ③ 登记的是**旧路径**（exe 被移动 / 换了发布目录）→ 开机拉起来的是别的副本，
        ///    对当前这个程序等于没生效。旧路径不在这里自动改写，交给用户点一次开关
        ///    （<see cref="Enable"/> 会覆盖成当前路径）。
        /// </para>
        /// </summary>
        public static bool IsEnabled()
        {
            string? command = ReadCommand();
            return command != null
                && AutoStartCommand.IsSameExecutable(command, ExecutablePath)
                && !AutoStartCommand.IsBlockedBySystem(ReadApprovedValue());
        }

        /// <summary>
        /// 登记自启（已登记则覆盖成当前 exe 的路径）。
        ///
        /// <para>
        /// ⚠️ **同时清掉系统禁用记录**：否则会出现最难解释的那种状态 ——
        /// 用户在设置页打开了开关（值也写进 <c>Run</c> 了），但资源管理器里那条禁用记录还在，
        /// 开机照旧不起来。清掉后写一条「已启用」，与「启动应用」页里手动打开完全等价。
        /// </para>
        ///
        /// <para>
        /// 失败（无权限 / 策略锁定）会抛 <see cref="UnauthorizedAccessException"/> 一类异常，
        /// 由调用方转成界面提示。
        /// </para>
        /// </summary>
        public static void Enable()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.SetValue(
                    AutoStartCommand.ValueName,
                    AutoStartCommand.BuildCommand(ExecutablePath),
                    RegistryValueKind.String);
            }

            using var approved = Registry.CurrentUser.CreateSubKey(
                AutoStartCommand.StartupApprovedRunKey);
            approved.SetValue(
                AutoStartCommand.ValueName,
                AutoStartCommand.BuildEnabledApprovedValue(),
                RegistryValueKind.Binary);
        }

        /// <summary>
        /// 撤销自启：只删 <c>Run</c> 里的值。
        ///
        /// <para>
        /// **不删 StartupApproved 里的记录**：那条记录删除与否都不影响「不启动」这个结果，
        /// 但它带着「何时被禁用」的时间戳，留着反而与用户在「启动应用」页手动关掉后的
        /// 系统状态一致。删掉它也没坏处，只是多做一次不必要的写。
        /// </para>
        /// <para>
        /// 值本来就不存在时**不算失败**（幂等），这是关闭路径应有的语义。
        /// </para>
        /// </summary>
        public static void Disable()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AutoStartCommand.ValueName, throwOnMissingValue: false);
        }
    }
}
