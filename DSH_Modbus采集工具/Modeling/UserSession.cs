using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 操作账户（2026-11-06 用户定案，P 权限）。
    /// <para>
    /// 背景：自由监控里可以对 PLC 随意写入值，这是危险操作 —— 现场误写一个寄存器
    /// 可能让设备动起来。故用账户区分「谁能写」。
    /// </para>
    /// </summary>
    public enum UserRole
    {
        /// <summary>
        /// 技术员（默认）：**除自由监控写入外无限制**，无登录密码。
        /// <para>
        /// 「无密码」是指**不需要登录**：程序启动即处于这一档，
        /// 注销也是回到这一档 —— 不存在「以技术员身份登录」这个动作。
        /// </para>
        /// </summary>
        Technician = 0,

        /// <summary>工程师：所有功能无限制，需要密码才能进入。</summary>
        Engineer = 1,
    }

    /// <summary>
    /// 账户规则（**纯逻辑、不引 WPF**，可离线断言）。
    /// <para>
    /// ⚠️ 密码**硬编码**在这里，按用户需求给的就是一个固定值。
    /// 不落 `app-settings.json`：那儿是用户可改的偏好文件，
    /// 密码写进去等于「谁都能改掉」，反而制造一个假的安全感。
    /// </para>
    /// </summary>
    public static class UserRules
    {
        /// <summary>工程师登录密码（用户 2026-11-06 定）。</summary>
        public const string EngineerPassword = "Admin8080";

        /// <summary>技术员的显示名。</summary>
        public const string TechnicianName = "技术员";

        /// <summary>工程师的显示名。</summary>
        public const string EngineerName = "工程师";

        /// <summary>程序启动时 / 注销后的账户。</summary>
        public const UserRole DefaultRole = UserRole.Technician;

        /// <summary>角色的显示名（下拉框与导航按钮都用它）。</summary>
        public static string NameOf(UserRole role) => role switch
        {
            UserRole.Engineer => EngineerName,
            _ => TechnicianName,
        };

        /// <summary>下拉框里的全部角色（顺序即显示顺序）。</summary>
        public static readonly UserRole[] Roles = { UserRole.Technician, UserRole.Engineer };

        /// <summary>
        /// 这个角色能不能**自由监控写入**（唯一一处受权限限制的功能）。
        /// <para>
        /// 用户原话：「自由监控中对PLC随意写入值是危险的操作，所以需要账户权限来管控」、
        /// 「技术员：除了自由监控不可以写入值，其他无限制」。
        /// 故**其它所有功能（含采集任务的写入、扫码器回写）都按角色无关处理**，
        /// 不要顺手也给它们加限制 —— 用户只点了自由监控这一处。
        /// </para>
        /// </summary>
        public static bool CanWriteMonitor(UserRole role) => role == UserRole.Engineer;

        /// <summary>
        /// 尝试登录工程师。成功返回 <c>true</c> 并给出角色；失败给出原因。
        /// <para>
        /// ⚠️ 校验用 <see cref="string.Equals(string,string,StringComparison)"/> 的
        /// <c>Ordinal</c>：密码是 ASCII 明文，不涉及区域规则；
        /// 用忽略大小写会白白放宽一半的组合。
        /// </para>
        /// </summary>
        public static bool TryLogin(string? roleName, string? password, out UserRole role, out string error)
        {
            role = DefaultRole;

            UserRole? picked = null;
            foreach (UserRole candidate in Roles)
            {
                if (string.Equals(NameOf(candidate), roleName, StringComparison.Ordinal))
                {
                    picked = candidate;
                    break;
                }
            }

            if (picked is not { } wanted)
            {
                error = "请选择用户名";
                return false;
            }

            // 技术员不需要密码 —— 但「登录成技术员」本身也没意义（那本来就是默认档），
            // 仍然放行，只是不校验密码。用户定义里它就是「无登录密码」。
            if (wanted == UserRole.Technician)
            {
                role = UserRole.Technician;
                error = string.Empty;
                return true;
            }

            if (string.IsNullOrEmpty(password))
            {
                error = "请输入密码";
                return false;
            }

            if (!string.Equals(password, EngineerPassword, StringComparison.Ordinal))
            {
                error = "密码错误";
                return false;
            }

            role = UserRole.Engineer;
            error = string.Empty;
            return true;
        }
    }
}
