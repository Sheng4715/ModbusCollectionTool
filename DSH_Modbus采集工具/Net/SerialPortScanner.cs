using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DSH_Modbus采集工具.Net
{
    /// <summary>串口下拉项：显示「COM4 · USB-Enhanced-SERIAL CH343」，携带真实端口名供连接时打开。</summary>
    public sealed class SerialPortInfo
    {
        /// <summary>端口名（如 <c>COM4</c>）。**连接时用的是它**，不是下拉框显示文本。</summary>
        public string PortName { get; init; } = string.Empty;

        /// <summary>设备名称（如 <c>USB-Enhanced-SERIAL CH343</c>）；取不到时为空串。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 下拉框显示文本。<c>PortName · Description</c>；没有描述时只显示端口名。
        /// <para>
        /// ⚠️ 用它只是「给人看」—— 保存与连接一律用 <see cref="PortName"/>。
        /// 与目标网卡那条同一个取舍：显示文本里嵌着会变的东西（这里是设备名，
        /// 换个 USB 口可能就不同），按文本匹配会在最需要它的场景下失效。
        /// </para>
        /// </summary>
        public override string ToString() =>
            Description.Length > 0 ? $"{PortName} · {Description}" : PortName;
    }

    /// <summary>
    /// 串口枚举 + 设备名称查询。**刻意不引用 WPF**，与 <see cref="NicScanner"/> 对称。
    /// <para>
    /// 端口名来自 <c>SerialPort.GetPortNames()</c>（判定「存在哪些口」的唯一依据），
    /// 设备名称来自注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum</c> 的 <c>FriendlyName</c> ——
    /// **只用于显示**，取不到就退回只显示端口名，绝不让「查不到描述」影响端口本身可用性。
    /// </para>
    /// <para>
    /// ⚠️ **刻意不用 WMI（<c>Win32_PnPEntity</c>）**：那需要额外的 <c>System.Management</c> 包，
    /// 而本项目只有一个依赖（串口驱动）。注册表遍历零依赖，实测能拿到同一份
    /// <c>FriendlyName</c>（2026-11-18 实测 CH343 / CH340 / CP210x / ELTIMA 虚拟口全部命中）。
    /// </para>
    /// </summary>
    public static class SerialPortScanner
    {
        /// <summary>枚举设备所用的注册表根（设备枚举树，与 COM 口分配无关，纯描述来源）。</summary>
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

        /// <summary>
        /// 枚举本机串口（按端口号数字升序，COM2 在 COM10 前）并附带设备名称。
        /// <para>
        /// ⚠️ **顺序不能直接沿用 <c>GetPortNames()</c>**：它按字符串排序，COM10 会排在 COM2 前面。
        /// </para>
        /// </summary>
        public static List<SerialPortInfo> Scan()
        {
            string[] names;
            try
            {
                names = System.IO.Ports.SerialPort.GetPortNames();
            }
            catch
            {
                // 枚举失败（极少见）不该让整个页面点不开：返回空列表，用户点「刷新」再试
                return new List<SerialPortInfo>();
            }

            Dictionary<string, string>? descriptions = TryGetDescriptions();

            var result = new List<SerialPortInfo>(names.Length);
            foreach (string name in names)
            {
                string description = descriptions != null && descriptions.TryGetValue(name, out string? d)
                    ? d
                    : string.Empty;

                result.Add(new SerialPortInfo { PortName = name, Description = description });
            }

            result.Sort(static (a, b) => PortNumberOf(a.PortName).CompareTo(PortNumberOf(b.PortName)));
            return result;
        }

        /// <summary>
        /// 查「端口 → 设备名称」映射（遍历注册表设备枚举树）。
        /// <para>
        /// 任一步失败都返回 <c>null</c>，由调用方退回「只显示端口名」——
        /// 设备名称是**锦上添花**，不能因为查不到就让串口列表变空。
        /// </para>
        /// <para>
        /// 路径形如 <c>Enum\&lt;类&gt;\&lt;设备&gt;\&lt;实例&gt;\Device Parameters</c> 下的
        /// <c>PortName</c>，设备名取同层 <c>FriendlyName</c>。
        /// </para>
        /// </summary>
        private static Dictionary<string, string>? TryGetDescriptions()
        {
            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(EnumRoot);
                if (root == null)
                {
                    return null;
                }

                foreach (string classKey in root.GetSubKeyNames())
                {
                    using RegistryKey? classNode = TryOpen(root, classKey);
                    if (classNode == null)
                    {
                        continue;
                    }

                    foreach (string deviceKey in SafeNames(classNode))
                    {
                        using RegistryKey? deviceNode = TryOpen(classNode, deviceKey);
                        if (deviceNode == null)
                        {
                            continue;
                        }

                        foreach (string instanceKey in SafeNames(deviceNode))
                        {
                            using RegistryKey? instanceNode = TryOpen(deviceNode, instanceKey);
                            if (instanceNode == null)
                            {
                                continue;
                            }

                            using RegistryKey? parameters = TryOpen(instanceNode, "Device Parameters");
                            string? port = parameters?.GetValue("PortName") as string;
                            if (string.IsNullOrWhiteSpace(port))
                            {
                                continue;
                            }

                            string? friendly = instanceNode.GetValue("FriendlyName") as string;
                            if (string.IsNullOrWhiteSpace(friendly))
                            {
                                continue;
                            }

                            // 设备名常自带「(COM4)」后缀 —— 从显示名里剥掉，
                            // 否则下拉框会显示成「COM4 · USB-SERIAL CH340 (COM4)」，端口号重复两遍。
                            string description = StripPortSuffix(friendly);

                            // ⚠️ 同一端口号可能有多条记录（实测 COM11 同时被 CH340 与 ELTIMA 虚拟口占用）。
                            // 保留**第一条**非空的 —— 先到的那条通常就是实际绑定的设备。
                            if (description.Length > 0 && !map.ContainsKey(port))
                            {
                                map[port] = description;
                            }
                        }
                    }
                }

                return map;
            }
            catch
            {
                // 权限受限 / 注册表结构异常：静默退回「只显示端口名」
                return null;
            }
        }

        private static RegistryKey? TryOpen(RegistryKey parent, string name)
        {
            try
            {
                return parent.OpenSubKey(name);
            }
            catch
            {
                return null;
            }
        }

        private static string[] SafeNames(RegistryKey key)
        {
            try
            {
                return key.GetSubKeyNames();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>去掉设备名尾部的「(COM4)」「(COM10)」这类后缀。</summary>
        internal static string StripPortSuffix(string text) =>
            Regex.Replace(text, @"\s*\(COM\d+\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        /// <summary>从端口名里取数字部分（<c>COM10</c> → 10）；取不到返回 <see cref="int.MaxValue"/> 排最后。</summary>
        public static int PortNumberOf(string? portName)
        {
            if (string.IsNullOrEmpty(portName))
            {
                return int.MaxValue;
            }

            Match match = Regex.Match(portName, @"(\d+)$");
            return match.Success && int.TryParse(match.Groups[1].Value, out int value)
                ? value
                : int.MaxValue;
        }

        /// <summary>按端口名找下标（不按显示文本 —— 见 <see cref="SerialPortInfo.ToString"/>）；未命中返回 -1。</summary>
        public static int IndexOf(IReadOnlyList<SerialPortInfo> ports, string? portName)
        {
            if (string.IsNullOrEmpty(portName))
            {
                return -1;
            }

            for (int i = 0; i < ports.Count; i++)
            {
                if (string.Equals(ports[i].PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
