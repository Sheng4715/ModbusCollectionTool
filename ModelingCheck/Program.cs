using System;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

// ============================================================
// P3-3 数据模型层验证：用真机实测的原始寄存器值做黄金样本对照
//
// 黄金样本来自 2026-10-13 真机实测（XDPPro 写、本应用 FC03 只读）：
//   D800=123456 (DINT)  -> 读回 D800=0xE240, D801=0x0001
//   D802=123.45 (REAL)  -> 读回 D802=0xE666, D803=0x42F6
//   D810='abcd' (STRING)-> 读回 D810=0x6162, D811=0x6364
// ============================================================

int pass = 0, fail = 0;

void Check(string name, object actual, object expected)
{
    bool ok = Equals(actual?.ToString(), expected?.ToString());
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name,-46} = {actual}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name,-46} = {actual}   期望 {expected}"); }
}

void CheckThrows(string name, Action act)
{
    try { act(); fail++; Console.WriteLine($"  [FAIL] {name,-46} 未抛异常"); }
    catch (ArgumentException) { pass++; Console.WriteLine($"  [PASS] {name,-46} 正确拒绝"); }
    catch (Exception e) { fail++; Console.WriteLine($"  [FAIL] {name,-46} 抛了 {e.GetType().Name}"); }
}

Console.WriteLine("=== 1. 真机黄金样本：DINT 123456 （字序 3412，低字在低地址）===");
ushort[] d800 = { 0xE240, 0x0001 };
long rawInt = ValueCodec.DecodeInteger(DataType.DInt, d800, ByteOrder32.Order3412);
Check("DecodeInteger DINT", rawInt, 123456);
Check("FormatRegisters DINT/DEC", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "123456");
Check("FormatRegisters DINT/HEX", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Hex, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "0x0001E240");
Check("FormatRegisters DINT/BIN 位数", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Bin, ByteOrder32.Order3412, StringByteOrder.Order12, 0).Replace(" ", "").Length, 32);

Console.WriteLine();
Console.WriteLine("=== 2. 真机黄金样本：REAL 123.45 ===");
ushort[] d802 = { 0xE666, 0x42F6 };
Check("DecodeReal", ValueCodec.DecodeReal(d802, ByteOrder32.Order3412), 123.45f);

// 注意：123.45f 的单精度实际值是 123.44999694824219，不是数学上的 123.45。
// 这里用 "G9"（单精度可往返的最短位数）会如实输出 123.449997 —— 这是**正确**行为，
// 不能为了「看着好看」而丢掉精度。若业务上要定长小数，那是显示层另加格式化的事。
Check("REAL 解码值 == float 123.45", ValueCodec.DecodeReal(d802, ByteOrder32.Order3412) == 123.45f, true);
Check("FormatRegisters REAL/DEC 保持精度", ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "123.449997");
Check("FormatRegisters REAL/HEX", ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Hex, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "0x42F6E666");

Console.WriteLine();
Console.WriteLine("=== 3. 真机黄金样本：STRING 'abcd' （每寄存器 2 字符、高字节在前）===");
ushort[] d810 = { 0x6162, 0x6364 };
Check("DecodeString len=4", ValueCodec.DecodeString(d810, 4, StringByteOrder.Order12), "abcd");
Check("DecodeString len=3 (截断)", ValueCodec.DecodeString(d810, 3, StringByteOrder.Order12), "abc");
Check("FormatRegisters STRING/ASCII", ValueCodec.FormatRegisters(DataType.String, d810, DisplayFormat.Ascii, ByteOrder32.Order3412, StringByteOrder.Order12, 4), "abcd");
Check("STRING[9] 占用寄存器数", DataTypes.Get(DataType.String).RegistersForLength(9), 5);
Check("STRING[4] 占用寄存器数", DataTypes.Get(DataType.String).RegistersForLength(4), 2);

Console.WriteLine();
Console.WriteLine("=== 4. 字序 3412 是唯一正确答案（其余三种解出的都不是 123.45）===");
// 这条是本验证的重点：若当初把字序当成 1234（多数 Modbus 库的默认值），
// D802/D803 会被解成 −2.72e+23 这种「不报错但完全错」的值。
Check("3412 -> 123.45（正确）", ValueCodec.DecodeReal(d802, ByteOrder32.Order3412), 123.45f);
Check("1234 -> 不是 123.45", ValueCodec.DecodeReal(d802, ByteOrder32.Order1234) == 123.45f, false);
Check("2143 -> 不是 123.45", ValueCodec.DecodeReal(d802, ByteOrder32.Order2143) == 123.45f, false);
Check("4321 -> 不是 123.45", ValueCodec.DecodeReal(d802, ByteOrder32.Order4321) == 123.45f, false);
Console.WriteLine($"        （1234 会解成 {ValueCodec.DecodeReal(d802, ByteOrder32.Order1234):E6}，是个「不报错的错值」）");

Console.WriteLine();
Console.WriteLine("=== 5. INT/UINT 严格按声明类型（同一 0xFFFF 两种结果）===");
ushort[] ffff = { 0xFFFF };
Check("INT  -1", ValueCodec.FormatRegisters(DataType.Int, ffff, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "-1");
Check("UINT 65535", ValueCodec.FormatRegisters(DataType.UInt, ffff, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "65535");
Check("INT  HEX 按 16 位补零", ValueCodec.FormatRegisters(DataType.Int, ffff, DisplayFormat.Hex, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "0xFFFF");
ushort[] d7fff = { 0x8000 };
Check("INT  0x8000 = -32768", ValueCodec.FormatRegisters(DataType.Int, d7fff, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "-32768");
Check("UINT 0x8000 = 32768", ValueCodec.FormatRegisters(DataType.UInt, d7fff, DisplayFormat.Dec, ByteOrder32.Order3412, StringByteOrder.Order12, 0), "32768");

Console.WriteLine();
Console.WriteLine("=== 6. 类型与格式的约束（Brief 要求）===");
Check("BOOL 只允许 BIN", DataTypes.Get(DataType.Bool).AllowedFormats.Count, 1);
Check("BOOL 允许 BIN", DataTypes.Get(DataType.Bool).AllowsFormat(DisplayFormat.Bin), true);
Check("BOOL 不允许 DEC", DataTypes.Get(DataType.Bool).AllowsFormat(DisplayFormat.Dec), false);
Check("STRING 只允许 ASCII", DataTypes.Get(DataType.String).AllowedFormats.Count, 1);
Check("STRING 不允许 HEX", DataTypes.Get(DataType.String).AllowsFormat(DisplayFormat.Hex), false);
Check("线圈类型只有 BOOL", DataTypes.CoilTypes.Count, 1);
Check("寄存器类型有 6 种", DataTypes.RegisterTypes.Count, 6);
CheckThrows("STRING 按 HEX 格式化应报错", () =>
    ValueCodec.FormatRegisters(DataType.String, d810, DisplayFormat.Hex, ByteOrder32.Order3412, StringByteOrder.Order12, 4));

Console.WriteLine();
Console.WriteLine("=== 7. 编解码互逆（P4-2 写入的正确性前提）===");
foreach (ByteOrder32 bo in new[] { ByteOrder32.Order1234, ByteOrder32.Order2143, ByteOrder32.Order3412, ByteOrder32.Order4321 })
{
    uint sample = 0x12345678;
    ushort[] split = ValueCodec.Split32(sample, bo);
    long back = ValueCodec.DecodeInteger(DataType.UDInt, split, bo);
    Check($"Split32/Assemble 互逆 {bo}", (uint)back, sample);
}
foreach (StringByteOrder so in new[] { StringByteOrder.Order12, StringByteOrder.Order21 })
{
    const string text = "abcdefg";  // 奇数长度，验证补 0x00
    ushort[] enc = ValueCodec.EncodeString(text, so);
    Check($"EncodeString 占用 {text.Length} 字符 -> 寄存器数", enc.Length, 4);
    Check($"EncodeString/DecodeString 互逆 {so}", ValueCodec.DecodeString(enc, text.Length, so), text);
}

Console.WriteLine();
Console.WriteLine("=== 8. 驱动解析与越界校验（信捷）===");
CommDriver xinje = DriverRegistry.GetByKey("xinje_xd")!;
var p1 = new DataPoint { Element = "D", Index = "800", Type = DataType.DInt, Format = DisplayFormat.Dec };
Check("D800 DINT 解析", DataPointStore.TryResolve(p1, xinje, out _), true);
Check("D800 DINT 地址", p1.Address, 800);
Check("D800 DINT 需要 2 个寄存器", p1.RawCount, 2);

var p2 = new DataPoint { Element = "D", Index = "20479", Type = DataType.DInt, Format = DisplayFormat.Dec };
Check("D20479 DINT 越界应拒绝", DataPointStore.TryResolve(p2, xinje, out string e2), false);
Console.WriteLine($"         原因：{e2}");

var p3 = new DataPoint { Element = "M", Index = "100", Type = DataType.Bool, Format = DisplayFormat.Bin };
Check("M100 BOOL 解析", DataPointStore.TryResolve(p3, xinje, out _), true);
Check("M100 BOOL 区", p3.Area, ModbusArea.Coil);

var p4 = new DataPoint { Element = "M", Index = "100", Type = DataType.Int, Format = DisplayFormat.Dec };
Check("M100 INT 应拒绝（线圈非寄存器）", DataPointStore.TryResolve(p4, xinje, out string e4), false);
Console.WriteLine($"         原因：{e4}");

var p5 = new DataPoint { Element = "X", Index = "17", Type = DataType.Bool, Format = DisplayFormat.Bin };
Check("X17（八进制）解析", DataPointStore.TryResolve(p5, xinje, out _), true);
Check("X17 八进制 -> 地址 20495", p5.Address, 20495);

var p6 = new DataPoint { Element = "D", Index = "810", Type = DataType.String, Format = DisplayFormat.Ascii, Length = 9 };
Check("D810 STRING[9] 解析", DataPointStore.TryResolve(p6, xinje, out _), true);
Check("D810 STRING[9] 读 5 个寄存器", p6.RawCount, 5);

var p7 = new DataPoint { Element = "D", Index = "810", Type = DataType.String, Format = DisplayFormat.Dec, Length = 4 };
Check("STRING 配 DEC 应拒绝", DataPointStore.TryResolve(p7, xinje, out string e7), false);
Console.WriteLine($"         原因：{e7}");

var p8 = new DataPoint { Element = "D", Index = "810", Type = DataType.String, Format = DisplayFormat.Ascii, Length = 0 };
Check("STRING 长度 0 应拒绝", DataPointStore.TryResolve(p8, xinje, out string e8), false);
Console.WriteLine($"         原因：{e8}");

Console.WriteLine();
Console.WriteLine("=== 9. JSON 持久化往返 ===");
var list = new List<DataPoint> { p1, p3, p6 };
string json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
});
Console.WriteLine(json);
bool hasTypeName = json.Contains("\"DInt\"") && json.Contains("\"String\"");
bool noAddress = !json.Contains("\"Address\"");
Check("枚举序列化为名字（非数字）", hasTypeName, true);
Check("Address 不落盘（JsonIgnore 生效）", noAddress, true);

Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine($"结果：{pass} 通过 / {fail} 失败");
Console.WriteLine("============================================================");
return fail == 0 ? 0 : 1;
