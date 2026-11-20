using System.Collections.Generic;
using System.Linq;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>
    /// 驱动注册表。新增品牌通讯地址时，在此追加 CommDriver 定义即可。
    /// </summary>
    public static class DriverRegistry
    {
        private static readonly List<CommDriver> Drivers = new()
        {
            CreateXinjeXd(),
            CreateInovanceEasy(),
        };

        public static IReadOnlyList<CommDriver> All => Drivers;

        public static CommDriver? GetByKey(string key) =>
            Drivers.FirstOrDefault(d => d.Key == key);

        public static CommDriver? GetByDisplayName(string displayName) =>
            Drivers.FirstOrDefault(d => d.DisplayName == displayName);

        /// <summary>
        /// 某个驱动在注册表里的下标；传 <c>null</c> 或该驱动不在表里时返回 <c>-1</c>。
        /// <para>
        /// 给「设备类型」下拉框回填用（2026-11-09 持久化）：下拉项是 DisplayName 字符串，
        /// 按**对象**定位比按显示名匹配稳 —— 以后改文案/加品牌都不会错行。
        /// </para>
        /// </summary>
        public static int IndexOf(CommDriver? driver) =>
            driver == null ? -1 : Drivers.IndexOf(driver);

        /// <summary>
        /// 新增品牌示例：
        /// Drivers.Add(new CommDriver
        /// {
        ///     Key = "mitsubishi_fx",
        ///     DisplayName = "三菱FX系列",
        ///     Areas = new[]
        ///     {
        ///         new AddressArea { Element = "M", Area = ModbusArea.Coil,
        ///                           StartIndex = 0, Count = 8000, StartAddress = 0 },
        ///         new AddressArea { Element = "D", Area = ModbusArea.HoldingRegister,
        ///                           StartIndex = 0, Count = 8000, StartAddress = 0 },
        ///     },
        /// });
        /// </summary>

        // ========== 信捷 XD/XL 系列 ==========
        private static CommDriver CreateXinjeXd() => new()
        {
            Key = "xinje_xd",
            DisplayName = "信捷XD/XL系列",

            // ---- 字节顺序（信捷 XD/XL）----
            // 32 位整型 / 浮点：真机实测 `3412`（低字在低地址）。2026-10-13 XD5E @192.168.1.66，见 Design.md §10.3
            // 字符串：**2026-11-20 改为 `21`**（用户真机实测）。
            //   ⚠️ 为什么从 `12` 改成 `21`：信捷**后来新增了真正的 STRING 类型**，
            //   此前该功能是用 **byte 数组**模拟的 —— 两者在寄存器内的字节落位不同。
            //   旧结论（`12`，高字节是前一个字符，Design.md §10.4 / `abcd` → 0x6162）针对的是
            //   byte 数组那套写法，**不适用于新 STRING 类型**。
            //   两品牌现在字符串序**一致**（都是 `21`），故「两品牌唯一差异是字符串」这条旧结论已失效。
            Int32ByteOrder = ByteOrder32.Order3412,
            FloatByteOrder = ByteOrder32.Order3412,
            StringByteOrder = StringByteOrder.Order21,

            Areas = new[]
            {
                // ---- 线圈（位）：非 IO 元件 ----
                new AddressArea { Element = "M",  Area = ModbusArea.Coil, StartIndex = 0, Count = 20480, StartAddress = 0 },
                new AddressArea { Element = "S",  Area = ModbusArea.Coil, StartIndex = 0, Count = 8000,  StartAddress = 28672 },
                new AddressArea { Element = "SM", Area = ModbusArea.Coil, StartIndex = 0, Count = 4096,  StartAddress = 36864 },
                new AddressArea { Element = "HM", Area = ModbusArea.Coil, StartIndex = 0, Count = 6144,  StartAddress = 49408 },

                // ---- 线圈（位）：X（本体 + 扩展），地址取自 XD/XL 用户手册 Modbus 地址表 ----
                // 同一元件有多段区，StartIndex 为八进制编号的十进制值（如 X10000 → 4096）
                new AddressArea { Element = "X", Area = ModbusArea.Coil, StartIndex = 0,     Count = 64,   StartAddress = 20480, IndexBase = NumberBase.Octal }, // X0–X77      本体      0x5000–0x503F
                new AddressArea { Element = "X", Area = ModbusArea.Coil, StartIndex = 4096,  Count = 1024, StartAddress = 20736, IndexBase = NumberBase.Octal }, // X10000–X11777 扩展 #1–#16 0x5100–0x54FF
                new AddressArea { Element = "X", Area = ModbusArea.Coil, StartIndex = 8192,  Count = 128,  StartAddress = 22736, IndexBase = NumberBase.Octal }, // X20000–X20177 BD #1–#2   0x58D0–0x594F
                new AddressArea { Element = "X", Area = ModbusArea.Coil, StartIndex = 12288, Count = 64,   StartAddress = 23536, IndexBase = NumberBase.Octal }, // X30000–X30077 ED #1      0x5BF0–0x5C2F

                // ---- 线圈（位）：Y（本体 + 扩展）----
                new AddressArea { Element = "Y", Area = ModbusArea.Coil, StartIndex = 0,     Count = 64,   StartAddress = 24576, IndexBase = NumberBase.Octal }, // Y0–Y77      本体      0x6000–0x603F
                new AddressArea { Element = "Y", Area = ModbusArea.Coil, StartIndex = 4096,  Count = 1024, StartAddress = 24832, IndexBase = NumberBase.Octal }, // Y10000–Y11777 扩展 #1–#16 0x6100–0x64FF
                new AddressArea { Element = "Y", Area = ModbusArea.Coil, StartIndex = 8192,  Count = 128,  StartAddress = 26832, IndexBase = NumberBase.Octal }, // Y20000–Y20177 BD #1–#2   0x68D0–0x694F
                new AddressArea { Element = "Y", Area = ModbusArea.Coil, StartIndex = 12288, Count = 64,   StartAddress = 27632, IndexBase = NumberBase.Octal }, // Y30000–Y30077 ED #1      0x6BF0–0x6C2F

                // ---- 寄存器（字）----
                new AddressArea { Element = "D",   Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 20480, StartAddress = 0 },
                new AddressArea { Element = "SD",  Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 4096,  StartAddress = 28672 },
                new AddressArea { Element = "HD",  Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 6144,  StartAddress = 41088 },
                new AddressArea { Element = "HSD", Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 1024,  StartAddress = 47232 },
                new AddressArea { Element = "SFD", Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 4096,  StartAddress = 58560 },
            },
        };

        // ========== 汇川 Easy 系列 ==========
        private static CommDriver CreateInovanceEasy() => new()
        {
            Key = "inovance_easy",
            DisplayName = "汇川Easy系列",

            // ---- 字节顺序：真机实测值（2026-10-21，Easy 系列 @192.168.1.10:502）----
            // 32 位：D800=305419896(0x12345678) → 读回 D800=0x5678 / D801=0x1234（低字在低地址，字序 3412）
            // 浮点：D802=123.45 → 读回 D802=0xE666 / D803=0x42F6（同上，3412）
            // ⚠️ 字符串：D810='ABCD' → 读回 0x4241 / 0x4443 → **第 1 个字符在低字节（21）**
            //    与信捷 XD 相反！这是两品牌目前唯一不同的字序，别照抄信捷的 12。
            // 证据见 Design.md §10.16
            Int32ByteOrder = ByteOrder32.Order3412,
            FloatByteOrder = ByteOrder32.Order3412,
            StringByteOrder = StringByteOrder.Order21,

            Areas = new[]
            {
                // ---- 线圈（位）----
                new AddressArea { Element = "M", Area = ModbusArea.Coil, StartIndex = 0, Count = 8000,  StartAddress = 0 },
                new AddressArea { Element = "B", Area = ModbusArea.Coil, StartIndex = 0, Count = 32768, StartAddress = 12288 },
                new AddressArea { Element = "S", Area = ModbusArea.Coil, StartIndex = 0, Count = 4096,  StartAddress = 57344 },
                new AddressArea { Element = "X", Area = ModbusArea.Coil, StartIndex = 0, Count = 1024,  StartAddress = 63488, IndexBase = NumberBase.Octal }, // X0-X1777
                new AddressArea { Element = "Y", Area = ModbusArea.Coil, StartIndex = 0, Count = 1024,  StartAddress = 64512, IndexBase = NumberBase.Octal }, // Y0-Y1777

                // ---- 寄存器（字）----
                new AddressArea { Element = "D", Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 8000,  StartAddress = 0 },
                new AddressArea { Element = "R", Area = ModbusArea.HoldingRegister, StartIndex = 0, Count = 32768, StartAddress = 12288 },
            },
        };
    }
}
