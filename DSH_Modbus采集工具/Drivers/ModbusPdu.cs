using System;
using System.IO;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>
    /// 传输无关的 Modbus PDU 层：构造请求、解析响应、判定异常码。
    /// <para>
    /// **为什么单独抽出来**：PDU（功能码 + 数据）是 Modbus 协议真正的部分，
    /// MBAP 头（TCP）与「地址 + CRC」（RTU）只是两种装订方式。
    /// 抽之前同一个 `BuildReadPdu` 只有 TCP 一份，加 RTU 就得抄一遍 ——
    /// 抄出来的两份会在「异常响应怎么判」这种细节上慢慢走偏，
    /// 而那种偏差现场表现为「某种功能码偶发失败」，极难查。
    /// </para>
    /// <para>
    /// 本类**不知道**任何传输细节：不碰 Socket、不碰串口、不算 CRC。
    /// </para>
    /// </summary>
    public static class ModbusPdu
    {
        /// <summary>单次 FC03 / FC04 最多读的寄存器数（协议上限 125）。</summary>
        public const int MaxRegistersPerRead = 125;

        /// <summary>单次 FC16 最多写的寄存器数（协议上限 123）。</summary>
        public const int MaxRegistersPerWrite = 123;

        /// <summary>单次 FC01 / FC02 最多读的线圈数（协议上限 2000）。</summary>
        public const int MaxCoilsPerRead = 2000;

        /// <summary>构造读请求 PDU（FC01/02/03/04 共用：功能码 + 起始地址 + 数量）。</summary>
        public static byte[] BuildRead(byte functionCode, ushort startAddress, ushort count) => new[]
        {
            functionCode,
            (byte)(startAddress >> 8), (byte)startAddress,
            (byte)(count >> 8), (byte)count,
        };

        /// <summary>构造单写请求 PDU（FC05 线圈 / FC06 寄存器共用：功能码 + 地址 + 值）。</summary>
        public static byte[] BuildWriteSingle(byte functionCode, ushort address, ushort value) => new[]
        {
            functionCode,
            (byte)(address >> 8), (byte)address,
            (byte)(value >> 8), (byte)value,
        };

        /// <summary>构造 FC16（写多个寄存器）请求 PDU。</summary>
        public static byte[] BuildWriteMultiple(ushort startAddress, ushort[] values)
        {
            if (values == null || values.Length == 0)
            {
                throw new ArgumentException("写入内容不能为空", nameof(values));
            }

            if (values.Length > MaxRegistersPerWrite)
            {
                throw new ArgumentException(
                    $"单次最多写 {MaxRegistersPerWrite} 个寄存器，实际 {values.Length} 个", nameof(values));
            }

            int count = values.Length;
            byte[] pdu = new byte[6 + count * 2];
            pdu[0] = 0x10;
            pdu[1] = (byte)(startAddress >> 8);
            pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(count >> 8);
            pdu[4] = (byte)count;
            pdu[5] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
            {
                pdu[6 + i * 2] = (byte)(values[i] >> 8);
                pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
            }

            return pdu;
        }

        /// <summary>
        /// 判定异常响应。异常响应的功能码是「请求功能码 | 0x80」，后跟 1 字节异常码。
        /// 返回 <c>true</c> 表示这是异常响应（此时 <paramref name="code"/> 有效）。
        /// </summary>
        public static bool IsException(ReadOnlySpan<byte> respPdu, byte requestFunctionCode, out byte code)
        {
            code = 0;
            if (respPdu.Length < 1)
            {
                return false;
            }

            if (respPdu[0] != (requestFunctionCode | 0x80))
            {
                return false;
            }

            if (respPdu.Length != 2)
            {
                throw new IOException("Modbus 异常响应长度无效");
            }

            code = respPdu[1];
            return true;
        }

        /// <summary>按请求 PDU 算出「读响应应有多少数据字节」（FC01/02 按位打包，FC03/04 每寄存器 2 字节）。</summary>
        public static int ExpectedReadBytes(byte functionCode, int count) =>
            functionCode is 0x01 or 0x02 ? (count + 7) / 8 : count * 2;

        /// <summary>
        /// 校验读响应声明的数据字节数是否与请求数量匹配。
        /// <para>
        /// **RTU 侧必须先查这一步再切帧**：RTU 没有 MBAP 长度字段，收帧长度直接取自
        /// 响应第 3 字节（数据字节数）。若该值与请求数量对不上，多半是流已错位或对端
        /// 回的是别的请求 —— 此时按它继续读会吞掉下一帧的开头，整条链路从此失步。
        /// </para>
        /// </summary>
        public static void ValidateReadByteCount(byte functionCode, int count, int declaredBytes)
        {
            int expected = ExpectedReadBytes(functionCode, count);
            if (declaredBytes != expected)
            {
                throw new IOException(
                    $"Modbus 响应数据字节数与请求不符：期望 {expected}，实际 {declaredBytes}");
            }
        }

        /// <summary>
        /// 解析读响应 PDU 为布尔数组（FC01/FC02）。入参已确认不是异常响应。
        /// </summary>
        public static bool[] ParseBits(ReadOnlySpan<byte> respPdu, int count)
        {
            if (respPdu.Length < 2)
            {
                throw new IOException("Modbus 响应报文过短");
            }

            int byteCount = respPdu[1];
            if (respPdu.Length < 2 + byteCount || byteCount < (count + 7) / 8)
            {
                throw new IOException("Modbus 响应数据不完整");
            }

            var bits = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bits[i] = (respPdu[2 + i / 8] & (1 << (i % 8))) != 0;
            }

            return bits;
        }

        /// <summary>解析读响应 PDU 为寄存器数组（FC03/FC04）。入参已确认不是异常响应。</summary>
        public static ushort[] ParseRegisters(ReadOnlySpan<byte> respPdu, int count)
        {
            if (respPdu.Length < 2)
            {
                throw new IOException("Modbus 响应报文过短");
            }

            int byteCount = respPdu[1];
            if (byteCount != count * 2 || respPdu.Length < 2 + byteCount)
            {
                throw new IOException("Modbus 响应寄存器数不匹配");
            }

            var values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = (ushort)((respPdu[2 + i * 2] << 8) | respPdu[3 + i * 2]);
            }

            return values;
        }

        /// <summary>
        /// 校验写响应。写的响应是**回显**（恒 5 字节 PDU：功能码 + 地址 + 值/数量），
        /// 与读的「字节数 + 数据」完全不同，故不能套读的那套校验。
        /// </summary>
        public static void ValidateWriteResponse(ReadOnlySpan<byte> respPdu, byte[] requestPdu, bool isMultiple)
        {
            if (respPdu.Length != 5)
            {
                throw new IOException($"Modbus 写响应长度异常：{respPdu.Length}");
            }

            if (respPdu[0] != requestPdu[0])
            {
                throw new IOException("Modbus 写响应功能码不匹配");
            }

            // 回显的地址必须与请求一致（0x10 还要比对数量）——防止应答错位时误判成功
            if (respPdu[1] != requestPdu[1] || respPdu[2] != requestPdu[2])
            {
                throw new IOException("Modbus 写响应地址与请求不符");
            }

            if (respPdu[3] != requestPdu[3] || respPdu[4] != requestPdu[4])
            {
                throw new IOException(isMultiple
                    ? "Modbus 写响应寄存器数与请求不符"
                    : "Modbus 写响应值与请求不符");
            }
        }

        /// <summary>
        /// Modbus 异常码的中文说明（GB/T 19582 / MODBUS Application Protocol V1.1b3）。
        /// 注意：部分 PLC 对越界或跨地址区的请求回非标准码 —— 实测信捷 XD 对跨本体/扩展空隙的
        /// 读取回 <c>06</c>（标准语义为「从站设备忙」，而非「非法数据地址」02）。
        /// </summary>
        public static string DescribeException(byte code) => code switch
        {
            1 => "非法功能码",
            2 => "非法数据地址",
            3 => "非法数据值",
            4 => "从站设备故障",
            5 => "确认（从站已受理，需稍后查询结果）",
            6 => "从站设备忙",
            7 => "否认",
            8 => "存储奇偶校验错误",
            10 => "网关路径不可用",
            11 => "网关目标设备无响应",
            _ => "未知异常",
        };

        /// <summary>
        /// 全应用唯一的异常响应出口：把异常 PDU 变成要抛的异常。
        /// TCP 与 RTU 共用，保证两边的文案与异常码语义一字不差。
        /// </summary>
        public static ModbusResponseException CreateException(byte code) =>
            new(code, $"Modbus 异常：{DescribeException(code)}（异常码 {code}）");
    }
}
