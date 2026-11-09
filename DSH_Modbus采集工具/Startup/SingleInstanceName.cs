using System;

namespace DSH_Modbus采集工具.Startup
{
    /// <summary>
    /// 单实例互斥量的**命名规则**（纯逻辑，无 WPF / 无内核对象）。
    /// <para>
    /// 抽成纯逻辑的理由与 <see cref="AutoStartCommand"/> 同一层次：名字是「跨会话能不能互相看见」
    /// 的唯一决定因素，写错了症状是「保护看起来生效、远程桌面一进来就冒出第二个实例」——
    /// 那种缺陷在单机自测里永远暴露不出来，所以规则本身必须能离线断言。
    /// </para>
    /// </summary>
    public static class SingleInstanceName
    {
        /// <summary>
        /// 内核对象命名空间前缀。
        /// <para>
        /// ⚠️ **必须是 <c>Global\</c>，不能用 <c>Local\</c>**：<c>Local\</c> 只在**当前登录会话**内
        /// 可见，同一个用户在远程桌面 / 快速用户切换 / 另一账户登录后是另一个会话，
        /// 看不到本会话的对象 → 会跑出第二个实例，而两者写同一份 json。
        /// 需求是「每台机器只能运行一个」，故必须跨会话。
        /// </para>
        /// <para>
        /// 实测：标准用户（非管理员）即可创建 <c>Global\</c> 命名互斥量，无需提权。
        /// </para>
        /// </summary>
        public const string NamespacePrefix = @"Global\";

        /// <summary>
        /// 互斥量名主体。
        /// <para>
        /// **刻意不含 exe 路径、版本号、用户 SID**：
        /// </para>
        /// <list type="bullet">
        /// <item>不含路径 —— 否则同一台机器上两个发布目录（如 <c>_1.0.0</c> / <c>_1.0.1</c>）
        /// 会各起一个实例，同时连同一个 PLC、互相打架，正是要防的场景；</item>
        /// <item>不含版本号 —— 升级期间的旧版与新版不能共存。代价是「升级前必须先关旧的」，
        /// 但第二个实例会明确提示「程序已在运行」，不会让人以为是双击没反应；</item>
        /// <item>不含用户名 —— 含了就退化成「每用户一个」，与「每台机器一个」相反。</item>
        /// </list>
        /// <para>
        /// 用固定的产品名而不是程序集名：程序集名将来改了（或加了 <c>-debug</c> 后缀分支）
        /// 不至于让新旧两版互相看不见。
        /// </para>
        /// </summary>
        public const string ObjectName = "DSH_Modbus采集工具";

        /// <summary>完整互斥量名：<c>Global\DSH_Modbus采集工具</c>。</summary>
        public static string FullName => NamespacePrefix + ObjectName;

        /// <summary>
        /// 校验一个名字是否满足「跨会话可见」这条硬要求。给断言与代码复查用。
        /// </summary>
        public static bool IsMachineWide(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.StartsWith(NamespacePrefix, StringComparison.OrdinalIgnoreCase)
            && name.Length > NamespacePrefix.Length;
    }
}