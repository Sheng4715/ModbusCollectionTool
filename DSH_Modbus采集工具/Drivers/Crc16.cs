using System;

namespace DSH_Modbus采集工具.Drivers
{
    /// <summary>
    /// Modbus RTU 的 CRC16（多项式 0xA001，初值 0xFFFF，**低字节在前**发送）。
    /// <para>
    /// 独立成纯静态类、不引任何 IO：它是 RTU 唯一的校验手段，错了整条链路都废，
    /// 但现场现象只是「所有响应都校验失败」，看不出是算法问题。故要能离线断言。
    /// </para>
    /// <para>
    /// ⚠️ **发送顺序是低字节在前**（CRC 本身算出来是个 16 位值，但 RTU 帧尾先发低字节）。
    /// 写反的表现与「CRC 算错」完全一样 —— 从站静默丢弃，主机侧只看到超时。
    /// </para>
    /// </summary>
    public static class Crc16
    {
        /// <summary>计算 CRC16（Modbus 变体）。返回的 16 位值在帧里**低字节先发**。</summary>
        public static ushort Compute(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;

            foreach (byte b in data)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb)
                    {
                        crc ^= 0xA001;
                    }
                }
            }

            return crc;
        }

        /// <summary>
        /// 把 CRC 追加到帧尾（低字节在前）。返回新数组，不修改入参 ——
        /// 调用方常常还要复用未带 CRC 的 PDU 做校验比对。
        /// </summary>
        public static byte[] Append(ReadOnlySpan<byte> frame)
        {
            ushort crc = Compute(frame);
            byte[] result = new byte[frame.Length + 2];
            frame.CopyTo(result);

            // 低字节在前 —— 见类注释
            result[frame.Length] = (byte)(crc & 0xFF);
            result[frame.Length + 1] = (byte)(crc >> 8);
            return result;
        }

        /// <summary>
        /// 校验整帧（末尾 2 字节为 CRC）。帧长不足 4 字节（地址 + 功能码 + CRC）
        /// 时直接判失败 —— 那是残帧，不是合法 RTU 帧。
        /// </summary>
        public static bool Validate(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4)
            {
                return false;
            }

            ReadOnlySpan<byte> body = frame[..^2];
            ushort expected = Compute(body);
            byte low = frame[^2];
            byte high = frame[^1];
            ushort actual = (ushort)((high << 8) | low);
            return expected == actual;
        }
    }
}
