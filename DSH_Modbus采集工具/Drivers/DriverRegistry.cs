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
            Areas = new[]
            {
                // ---- 线圈（位）----
                new AddressArea { Element = "M",  Area = ModbusArea.Coil, StartIndex = 0, Count = 20480, StartAddress = 0 },
                new AddressArea { Element = "X",  Area = ModbusArea.Coil, StartIndex = 0, Count = 64,    StartAddress = 20480, IndexBase = NumberBase.Octal }, // X0-X77 本体
                new AddressArea { Element = "Y",  Area = ModbusArea.Coil, StartIndex = 0, Count = 64,    StartAddress = 24576, IndexBase = NumberBase.Octal }, // Y0-Y77 本体
                new AddressArea { Element = "S",  Area = ModbusArea.Coil, StartIndex = 0, Count = 8000,  StartAddress = 28672 },
                new AddressArea { Element = "SM", Area = ModbusArea.Coil, StartIndex = 0, Count = 4096,  StartAddress = 36864 },
                new AddressArea { Element = "HM", Area = ModbusArea.Coil, StartIndex = 0, Count = 6144,  StartAddress = 49408 },

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
