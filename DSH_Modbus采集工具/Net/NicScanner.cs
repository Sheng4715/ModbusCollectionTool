using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DSH_Modbus采集工具.Net
{
    /// <summary>网卡下拉项：显示 "网卡名(ip)"，携带真实 IP 供连接时绑定本地端点。</summary>
    public sealed class NicInfo
    {
        public string Name { get; init; } = string.Empty;
        public IPAddress Ip { get; init; } = IPAddress.None;

        public override string ToString() => $"{Name}({Ip})";
    }

    /// <summary>
    /// 网卡枚举与「上次选择」的还原规则。**刻意不引用 WPF**：扫描只认
    /// <see cref="NetworkInterface"/>，还原只做列表查找，两者都能脱离窗口阅读。
    /// <para>
    /// 下拉框的填充与 <c>_restoringNicSelection</c> 闸门留在窗口 —— 那属于
    /// 「用户意图 vs 程序改动」的判断，离不开控件事件。
    /// </para>
    /// </summary>
    public static class NicScanner
    {
        /// <summary>
        /// 枚举可用网卡：跳过回环、只取 <see cref="OperationalStatus.Up"/>、且必须有 IPv4 地址。
        /// 顺序沿用 <see cref="NetworkInterface.GetAllNetworkInterfaces"/> 的系统顺序。
        /// </summary>
        public static List<NicInfo> Scan()
        {
            var result = new List<NicInfo>();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPAddress? ipv4 = nic.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4 == null)
                {
                    continue;
                }

                result.Add(new NicInfo { Name = nic.Name, Ip = ipv4 });
            }

            return result;
        }

        /// <summary>按网卡名找下标；名字为空或未命中返回 -1。</summary>
        public static int IndexOf(IReadOnlyList<NicInfo> nics, string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            for (int i = 0; i < nics.Count; i++)
            {
                if (string.Equals(nics[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 还原优先级：**本次扫描前的选择 → 上次运行记住的选择 → 第 0 项**。
        /// <para>
        /// 一律按**网卡名**匹配（每次扫描都是新对象，不能用引用比较）；不用下拉框显示文本，
        /// 因为文本里含 IP，而同一块网卡换网络后 IP 会变 —— 笔记本的 WLAN 在家是
        /// 192.168.1.15、在公司可能是 10.0.0.5，按文本匹配恰好在最需要它的场景下失效。
        /// </para>
        /// </summary>
        public static int RestoreIndex(IReadOnlyList<NicInfo> nics, string? previous, string? remembered)
        {
            int index = IndexOf(nics, previous);
            if (index < 0)
            {
                index = IndexOf(nics, remembered);
            }

            return index >= 0 ? index : 0;
        }
    }
}
