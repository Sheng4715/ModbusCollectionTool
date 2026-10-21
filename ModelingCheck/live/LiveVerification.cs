using System;
using System.Net.Sockets;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

// ============================================================
// P3-3 端到端真机验证：用数据模型层的代码读真机，确认解出的值
// 与用户在 XDPPro 里写的完全一致。
//
// 这是「黄金样本」之外的第二重验证 —— 样本证明算法对，
// 这一步证明算法与真机对得上。
//
// 真机不可达时以退出码 2 表示「跳过」，不算失败。
// ============================================================

const string Host = "192.168.1.66";
const int Port = 502;
const byte Unit = 1;

int pass = 0, fail = 0;
void Check(string name, object actual, object expected)
{
    bool ok = Equals(actual?.ToString(), expected?.ToString());
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name,-40} = {actual}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name,-40} = {actual}   期望 {expected}"); }
}

Console.WriteLine($"连接真机 {Host}:{Port} ...");
using var tcp = new TcpClient();
try
{
    var connectTask = tcp.ConnectAsync(Host, Port);
    if (!connectTask.Wait(TimeSpan.FromSeconds(3)))
    {
        Console.WriteLine("[跳过] 连接超时 —— 真机不可达，本次仅验证离线黄金样本");
        return 2;
    }
}
catch (Exception e)
{
    Console.WriteLine($"[跳过] 连接失败：{e.Message} —— 真机不可达，本次仅验证离线黄金样本");
    return 2;
}

Console.WriteLine("已连接。");
using NetworkStream ns = tcp.GetStream();

ushort[] ReadRegs(ushort addr, ushort count)
{
    byte[] pdu = new byte[5];
    pdu[0] = 0x03;
    pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)(addr & 0xFF);
    pdu[3] = (byte)(count >> 8); pdu[4] = (byte)(count & 0xFF);

    // MBAP 头 = tid(2) + proto(2) + len(2) + unit(1) = 7 字节，PDU 紧随其后
    byte[] frame = new byte[7 + pdu.Length];
    frame[0] = 0x00; frame[1] = 0x01;          // tid
    frame[2] = 0x00; frame[3] = 0x00;          // proto
    frame[4] = (byte)((pdu.Length + 1) >> 8);
    frame[5] = (byte)((pdu.Length + 1) & 0xFF);
    frame[6] = Unit;
    Array.Copy(pdu, 0, frame, 7, pdu.Length);

    ns.Write(frame, 0, frame.Length);
    ns.Flush();

    byte[] hdr = ReadExact(ns, 6);
    int len = (hdr[4] << 8) | hdr[5];
    byte[] rest = ReadExact(ns, len);
    if (rest[1] == 0x83) { throw new Exception($"异常码 {rest[2]}"); }

    int bc = rest[2];
    var regs = new ushort[bc / 2];
    for (int i = 0; i < regs.Length; i++) { regs[i] = (ushort)((rest[3 + i * 2] << 8) | rest[4 + i * 2]); }
    return regs;
}

static byte[] ReadExact(NetworkStream s, int n)
{
    byte[] buf = new byte[n];
    int got = 0;
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (got < n)
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("读取超时");
        int r = s.Read(buf, got, n - got);
        if (r <= 0) throw new Exception("连接关闭");
        got += r;
    }
    return buf;
}

CommDriver drv = DriverRegistry.GetByKey("xinje_xd")!;

// 字序取自设备配置页三个下拉框的当前值（信捷 XD 出厂默认 = 真机实测 3412 / 3412 / 12）
ByteOrderSettings orders = ByteOrderSettings.FromDriver(drv);
ByteOrder32 BO = orders.Int32;

Console.WriteLine();
Console.WriteLine("=== 用数据模型层读真机 D800/D802/D810 ===");

var pInt = new DataPoint { Element = "D", Index = "800", Type = DataType.DInt, Format = DisplayFormat.Dec };
if (!DataPointStore.TryResolve(pInt, drv, out string e1)) { Console.WriteLine($"  D800 解析失败：{e1}"); return 1; }
ushort[] rInt = ReadRegs((ushort)pInt.Address, (ushort)pInt.RawCount);
Console.WriteLine($"  原始：D800=0x{rInt[0]:X4} D801=0x{rInt[1]:X4}");
Check("D800 声明 DINT -> 值", ValueCodec.FormatRegisters(pInt.Type, rInt, pInt.Format, orders, 0), "123456");
Check("D800 声明 DINT -> HEX", ValueCodec.FormatRegisters(pInt.Type, rInt, DisplayFormat.Hex, orders, 0), "0x0001E240");

var pReal = new DataPoint { Element = "D", Index = "802", Type = DataType.Real, Format = DisplayFormat.Dec };
if (!DataPointStore.TryResolve(pReal, drv, out string e2)) { Console.WriteLine($"  D802 解析失败：{e2}"); return 1; }
ushort[] rReal = ReadRegs((ushort)pReal.Address, (ushort)pReal.RawCount);
Console.WriteLine($"  原始：D802=0x{rReal[0]:X4} D803=0x{rReal[1]:X4}");
Check("D802 声明 REAL -> 值", ValueCodec.DecodeReal(rReal, BO), 123.45f);

var pStr = new DataPoint { Element = "D", Index = "810", Type = DataType.String, Format = DisplayFormat.Ascii, Length = 4 };
if (!DataPointStore.TryResolve(pStr, drv, out string e3)) { Console.WriteLine($"  D810 解析失败：{e3}"); return 1; }
ushort[] rStr = ReadRegs((ushort)pStr.Address, (ushort)pStr.RawCount);
Console.WriteLine($"  原始：D810=0x{rStr[0]:X4} D811=0x{rStr[1]:X4}");
Check("D810 声明 STRING[4] -> 值", ValueCodec.DecodeString(rStr, pStr.Length, orders.String), "abcd");

// 关键对照：同一段寄存器按不同类型声明，解出的值不同 —— 证明类型真的在起作用
var pAsInt = new DataPoint { Element = "D", Index = "800", Type = DataType.Int, Format = DisplayFormat.Dec };
if (!DataPointStore.TryResolve(pAsInt, drv, out _)) { Console.WriteLine("  D800 解析失败"); return 1; }

string asInt = ValueCodec.FormatRegisters(DataType.Int, rInt, DisplayFormat.Dec, orders, 0);
string asUInt = ValueCodec.FormatRegisters(DataType.UInt, rInt, DisplayFormat.Dec, orders, 0);
string asDInt = ValueCodec.FormatRegisters(DataType.DInt, rInt, DisplayFormat.Dec, orders, 0);
Console.WriteLine($"  对照：同一 2 寄存器原始值 0x{rInt[0]:X4}/0x{rInt[1]:X4} 按三种声明解释：");
Console.WriteLine($"          INT  -> {asInt}      (只取首寄存器低字)");
Console.WriteLine($"          UINT -> {asUInt}");
Console.WriteLine($"          DINT -> {asDInt}   (完整 32 位)");

Check("INT 解释 != DINT 解释（类型确实起作用）", asInt != asDInt, true);
Check("UINT 解释 = 0xE240 = 57920", asUInt, "57920");
Check("DINT 解释 = 123456", asDInt, "123456");

// 已实测的结论：PLC **不会**因 XDPPro 里声明为 32 位而拒绝半字读取。
// 这意味着类型声明写错时，PLC 不报错，只会静默返回一个「看着正常但错」的值。
Console.WriteLine();
Console.WriteLine("=== 单读 D800 一个寄存器（验证 PLC 是否拒绝半字读取）===");
try
{
    ushort[] one = ReadRegs(800, 1);
    Console.WriteLine($"  [结论] PLC 允许半字读取，返回 0x{one[0]:X4} —— 类型声明写错时不会报错，只会静默给出错值");
}
catch (Exception ex)
{
    Console.WriteLine($"  [结论] 被拒绝：{ex.Message} —— PLC 会挡住半字读取");
}

Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine($"端到端真机结果：{pass} 通过 / {fail} 失败");
Console.WriteLine("============================================================");
return fail == 0 ? 0 : 1;
