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

// 设备配置页三个下拉框的当前值：信捷 XD 的出厂默认 = 真机实测值（3412 / 3412 / 12）
CommDriver xinje = DriverRegistry.GetByKey("xinje_xd")!;
ByteOrderSettings orders = ByteOrderSettings.FromDriver(xinje);

Console.WriteLine("=== 1. 真机黄金样本：DINT 123456 （字序 3412，低字在低地址）===");
ushort[] d800 = { 0xE240, 0x0001 };
long rawInt = ValueCodec.DecodeInteger(DataType.DInt, d800, ByteOrder32.Order3412);
Check("DecodeInteger DINT", rawInt, 123456);
Check("FormatRegisters DINT/DEC", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Dec, orders, 0), "123456");
Check("FormatRegisters DINT/HEX", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Hex, orders, 0), "0x0001E240");
Check("FormatRegisters DINT/BIN 位数", ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Bin, orders, 0).Replace(" ", "").Length, 32);

Console.WriteLine();
Console.WriteLine("=== 2. 真机黄金样本：REAL 123.45 ===");
ushort[] d802 = { 0xE666, 0x42F6 };
Check("DecodeReal", ValueCodec.DecodeReal(d802, ByteOrder32.Order3412), 123.45f);

// 注意：123.45f 的单精度实际值是 123.44999694824219，不是数学上的 123.45。
// 显示曾用 "G9"（单精度可往返的最短位数），会如实输出 123.449997 ——
// 那在**数值上**是对的，但与 PLC（XDPPro 显示 123.45）对不上，用户会误以为
// 通讯或字节序有问题。现改为 "G7"：与本项目工程量（6~7 位有效数字）匹配。
// ⚠️ 解码精度不受影响，见下面 DecodeReal 的断言。
Check("REAL 解码值 == float 123.45", ValueCodec.DecodeReal(d802, ByteOrder32.Order3412) == 123.45f, true);
Check("FormatRegisters REAL/DEC 显示为 123.45", ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Dec, orders, 0), "123.45");
// 代价：7 位有效数字以外的末位会被四舍五入（这是选 G7 的已知取舍，不是 bug）
Check("G7 取舍：1234.567 显示为 1234.567", ValueCodec.FormatRegisters(DataType.Real, new ushort[] { 0x5225, 0x449A }, DisplayFormat.Dec, orders, 0), "1234.567");
Check("FormatRegisters REAL/HEX", ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Hex, orders, 0), "0x42F6E666");

Console.WriteLine();
Console.WriteLine("=== 3. 真机黄金样本：STRING 'abcd' （每寄存器 2 字符、高字节在前）===");
ushort[] d810 = { 0x6162, 0x6364 };
Check("DecodeString len=4", ValueCodec.DecodeString(d810, 4, StringByteOrder.Order12), "abcd");
Check("DecodeString len=3 (截断)", ValueCodec.DecodeString(d810, 3, StringByteOrder.Order12), "abc");
Check("FormatRegisters STRING/ASCII", ValueCodec.FormatRegisters(DataType.String, d810, DisplayFormat.Ascii, orders, 4), "abcd");
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
Check("INT  -1", ValueCodec.FormatRegisters(DataType.Int, ffff, DisplayFormat.Dec, orders, 0), "-1");
Check("UINT 65535", ValueCodec.FormatRegisters(DataType.UInt, ffff, DisplayFormat.Dec, orders, 0), "65535");
Check("INT  HEX 按 16 位补零", ValueCodec.FormatRegisters(DataType.Int, ffff, DisplayFormat.Hex, orders, 0), "0xFFFF");
ushort[] d7fff = { 0x8000 };
Check("INT  0x8000 = -32768", ValueCodec.FormatRegisters(DataType.Int, d7fff, DisplayFormat.Dec, orders, 0), "-32768");
Check("UINT 0x8000 = 32768", ValueCodec.FormatRegisters(DataType.UInt, d7fff, DisplayFormat.Dec, orders, 0), "32768");

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
    ValueCodec.FormatRegisters(DataType.String, d810, DisplayFormat.Hex, orders, 4));

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
Console.WriteLine("=== 10. 写入编码：文本 → 寄存器（FC05/06/16 的入参）===");

// 逐元素比较：Check 用 ToString()，数组只会比出类型名，故数组另用这个
void CheckRegs(string name, ushort[] actual, ushort[] expected)
{
    bool ok = actual.Length == expected.Length;
    if (ok)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i]) { ok = false; break; }
        }
    }

    string Fmt(ushort[] a) => "[" + string.Join(", ", a) + "]";
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name,-46} = {Fmt(actual)}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name,-46} = {Fmt(actual)}   期望 {Fmt(expected)}"); }
}

void CheckEncode(string name, DataType t, DisplayFormat f, string text, int len, ByteOrderSettings o, ushort[] expected)
{
    bool ok = ValueCodec.TryEncodeRegisters(t, f, text, len, o, out ushort[] regs, out string err);
    if (!ok) { fail++; Console.WriteLine($"  [FAIL] {name,-46} 解析失败：{err}"); return; }
    CheckRegs(name, regs, expected);
}

void CheckReject(string name, DataType t, DisplayFormat f, string text, int len)
{
    bool ok = ValueCodec.TryEncodeRegisters(t, f, text, len, orders, out _, out string err);
    if (!ok) { pass++; Console.WriteLine($"  [PASS] {name,-46} 正确拒绝：{err}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name,-46} 本应拒绝却通过了"); }
}

// 整数：必须是 FormatRegisters 的严格逆运算（往返一致）
CheckEncode("INT  1234 -> 1 个寄存器", DataType.Int, DisplayFormat.Dec, "1234", 0, orders, new ushort[] { 1234 });
CheckEncode("INT  -1 -> 0xFFFF（补码）", DataType.Int, DisplayFormat.Dec, "-1", 0, orders, new ushort[] { 0xFFFF });
CheckEncode("UINT 65535 -> 0xFFFF", DataType.UInt, DisplayFormat.Dec, "65535", 0, orders, new ushort[] { 0xFFFF });
CheckEncode("INT  HEX 输入 1F", DataType.Int, DisplayFormat.Hex, "1F", 0, orders, new ushort[] { 0x001F });
CheckEncode("INT  HEX 输入 0x1F（带前缀）", DataType.Int, DisplayFormat.Hex, "0x1F", 0, orders, new ushort[] { 0x001F });
CheckEncode("INT  BIN 输入 1010", DataType.Int, DisplayFormat.Bin, "1010", 0, orders, new ushort[] { 0b1010 });

// 32 位：真机黄金样本反向 —— 123456 应产出 D800=0xE240, D801=0x0001（字序 3412）
CheckEncode("DINT 123456 -> 真机两寄存器", DataType.DInt, DisplayFormat.Dec, "123456", 0, orders, new ushort[] { 0xE240, 0x0001 });
CheckEncode("REAL 123.45 -> 真机两寄存器", DataType.Real, DisplayFormat.Dec, "123.45", 0, orders, new ushort[] { 0xE666, 0x42F6 });
CheckEncode("REAL 负数 -1.5", DataType.Real, DisplayFormat.Dec, "-1.5", 0, orders,
    ValueCodec.Split32(unchecked((uint)BitConverter.SingleToInt32Bits(-1.5f)), orders.Int32));

// 字符串：真机黄金样本 'abcd' -> 0x6162, 0x6364
CheckEncode("STRING[4] 'abcd' -> 真机两寄存器", DataType.String, DisplayFormat.Ascii, "abcd", 4, orders, new ushort[] { 0x6162, 0x6364 });

// ⚠️ 关键：编码必须补齐到声明的寄存器数（EncodeString 按文本长度自算，会少给）
CheckRegs("STRING[9] 'abc' 补零到 5 个寄存器", ValueCodec.EncodeStringFixed("abc", 9, orders.String),
    new ushort[] { 0x6162, 0x6300, 0x0000, 0x0000, 0x0000 });
CheckRegs("STRING[9] 超长 'abcdefghijkl' 截断到 9 字符", ValueCodec.EncodeStringFixed("abcdefghijkl", 9, orders.String),
    new ushort[] { 0x6162, 0x6364, 0x6566, 0x6768, 0x6900 });
Check("STRING 补零后的寄存器数 == RegistersForLength",
    ValueCodec.EncodeStringFixed("abc", 9, orders.String).Length,
    DataTypes.Get(DataType.String).RegistersForLength(9));

// 范围校验：红字提示要能说清边界
CheckReject("INT  32768 超上界", DataType.Int, DisplayFormat.Dec, "32768", 0);
CheckReject("INT  -32769 超下界", DataType.Int, DisplayFormat.Dec, "-32769", 0);
CheckReject("HEX  '1FFFF' 超出 16 位", DataType.Int, DisplayFormat.Hex, "1FFFF", 0);
CheckReject("BIN  17 位超 16 位宽", DataType.Int, DisplayFormat.Bin, "10000000000000000", 0);
CheckReject("UDINT 4294967296 超上界", DataType.UDInt, DisplayFormat.Dec, "4294967296", 0);
CheckReject("UINT -1 不能为负", DataType.UInt, DisplayFormat.Dec, "-1", 0);
CheckReject("HEX  非法字面量 'ZZ'", DataType.Int, DisplayFormat.Hex, "ZZ", 0);
CheckReject("DEC  非法字面量 '12.5'", DataType.Int, DisplayFormat.Dec, "12.5", 0);
CheckReject("空文本", DataType.Int, DisplayFormat.Dec, "", 0);
CheckReject("REAL 非法字面量 'abc'", DataType.Real, DisplayFormat.Dec, "abc", 0);
CheckReject("REAL 拒绝 NaN", DataType.Real, DisplayFormat.Dec, "NaN", 0);
CheckReject("BOOL 不能作为寄存器写入", DataType.Bool, DisplayFormat.Bin, "1", 0);

// 往返一致性：编码后再解码应回到原值（写入正确性的核心保证）
void CheckRoundTrip(string name, DataType t, DisplayFormat f, string text)
{
    if (!ValueCodec.TryEncodeRegisters(t, f, text, 0, orders, out ushort[] regs, out string err))
    {
        fail++; Console.WriteLine($"  [FAIL] {name,-46} 编码失败：{err}"); return;
    }

    string back = ValueCodec.FormatRegisters(t, regs, f, orders, 0);
    Check(name, back, text);
}

CheckRoundTrip("往返 INT '1234'", DataType.Int, DisplayFormat.Dec, "1234");
CheckRoundTrip("往返 INT '-1'", DataType.Int, DisplayFormat.Dec, "-1");
CheckRoundTrip("往返 DINT '123456'", DataType.DInt, DisplayFormat.Dec, "123456");
CheckRoundTrip("往返 DINT '-123456'", DataType.DInt, DisplayFormat.Dec, "-123456");
CheckRoundTrip("往返 REAL '123.45'", DataType.Real, DisplayFormat.Dec, "123.45");
CheckRoundTrip("往返 REAL '-1.5'", DataType.Real, DisplayFormat.Dec, "-1.5");
CheckRoundTrip("往返 UDINT '4000000000'", DataType.UDInt, DisplayFormat.Dec, "4000000000");

Console.WriteLine();
Console.WriteLine("=== 11. 浮点字序与 32 位整数字序相互独立（P4.3）===");
// 用户定案：设备配置页的「32位整数」与「32位浮点」两个下拉框各管各的。
// 下面这组断言就是「改浮点不影响整数、反之亦然」的可执行证明 ——
// 若哪天有人把它们又并回一套，这几条会立刻红。
var int3412Float1234 = new ByteOrderSettings(ByteOrder32.Order3412, ByteOrder32.Order1234, StringByteOrder.Order12);
var int1234Float3412 = new ByteOrderSettings(ByteOrder32.Order1234, ByteOrder32.Order3412, StringByteOrder.Order12);

Check("整数 3412：D800 -> 123456",
    ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Dec, int3412Float1234, 0), "123456");
Check("同一份 settings 下浮点 1234：REAL 解不出 123.45",
    ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Dec, int3412Float1234, 0) == "123.45", false);
Check("整数 1234：D800 解不出 123456",
    ValueCodec.FormatRegisters(DataType.DInt, d800, DisplayFormat.Dec, int1234Float3412, 0) == "123456", false);
Check("同一份 settings 下浮点 3412：REAL -> 123.45",
    ValueCodec.FormatRegisters(DataType.Real, d802, DisplayFormat.Dec, int1234Float3412, 0), "123.45");

// 编码侧同样各走各的：整数按 Int32、浮点按 Float
CheckRegs("DINT 编码走 Int32=3412", Encode(DataType.DInt, "123456", int3412Float1234), new ushort[] { 0xE240, 0x0001 });
CheckRegs("REAL 编码走 Float=3412（Int32 是 1234 也不影响）", Encode(DataType.Real, "123.45", int1234Float3412), new ushort[] { 0xE666, 0x42F6 });

ushort[] Encode(DataType t, string text, ByteOrderSettings o)
{
    if (!ValueCodec.TryEncodeRegisters(t, DisplayFormat.Dec, text, 0, o, out ushort[] regs, out string err))
    {
        throw new InvalidOperationException($"编码失败：{err}");
    }

    return regs;
}

// 驱动默认值就是出厂约定；未选驱动时用大端兜底
Check("信捷出厂默认 DINT 字序", ByteOrderSettings.FromDriver(xinje).Int32, ByteOrder32.Order3412);
Check("信捷出厂默认 REAL 字序", ByteOrderSettings.FromDriver(xinje).Float, ByteOrder32.Order3412);
// 兜底值取主力品牌（信捷）的实测值，不再是照惯例猜的大端 ——
// 两台验收机（信捷 XD / 汇川 Easy）实测都不是大端，猜错只会给出看着正常的错值。
Check("未选驱动时整数回落到 3412（信捷实测值）", ByteOrderSettings.Default.Int32, ByteOrder32.Order3412);
Check("未选驱动时字符串回落到 12（信捷实测值）", ByteOrderSettings.Default.String, StringByteOrder.Order12);

// 两个品牌的默认值都来自真机实测（信捷 §10.3/§10.4、汇川 §10.16）：
// 整数与浮点两品牌相同（3412），**字符串相反**（信捷 12、汇川 21）。
// 这组断言同时证明「切换驱动 = 重新取该驱动的默认，不是继承上一份」。
CommDriver inovance = DriverRegistry.GetByKey("inovance_easy")!;
Check("信捷默认字符串 = 12", ByteOrderSettings.FromDriver(xinje).String, StringByteOrder.Order12);
Check("汇川默认字符串 = 21（与信捷相反）", ByteOrderSettings.FromDriver(inovance).String, StringByteOrder.Order21);
Check("汇川默认整数 = 3412", ByteOrderSettings.FromDriver(inovance).Int32, ByteOrder32.Order3412);
Check("汇川默认浮点 = 3412", ByteOrderSettings.FromDriver(inovance).Float, ByteOrder32.Order3412);
Check("切换驱动后整份 settings 跟着换（不是继承上一份）",
    ByteOrderSettings.FromDriver(inovance) == ByteOrderSettings.FromDriver(xinje), false);


// ============================================================
// 12. 变量名补全的纯逻辑（P7.1）
//
// 只测「算出哪些候选」：怎么画在 Ui/CompletionPopup.cs，那里只能靠 UIA 端到端。
// 分开测的前提是 CompletionSource 不引用 WPF —— 这条一旦破坏，本节就编译不过，
// 等于顺手守住分层。
// ============================================================

Console.WriteLine();
Console.WriteLine("=== 12. 变量名补全：取词与筛选（P7.1）===");

// ---- 取词：光标左侧那一段标识符 ----
Check("整串就是词", CompletionSource.TryGetWord("sys_", 4, out int s1, out string w1) ? $"{s1}|{w1}" : "false", "0|sys_");
Check("光标在词中间只取左边", CompletionSource.TryGetWord("sys_Pulse1s", 4, out int s2, out string w2) ? $"{s2}|{w2}" : "false", "0|sys_");
Check("运算符后的词", CompletionSource.TryGetWord("a && b", 6, out int s3, out string w3) ? $"{s3}|{w3}" : "false", "5|b");
Check("左括号后的词", CompletionSource.TryGetWord("!(sys_", 6, out int s4, out string w4) ? $"{s4}|{w4}" : "false", "2|sys_");
Check("下划线开头也算词", CompletionSource.TryGetWord("_x", 2, out int s5, out string w5) ? $"{s5}|{w5}" : "false", "0|_x");

// 下面三种不该弹补全 —— 返回 false
Check("光标在空格后：没有词", CompletionSource.TryGetWord("a ", 2, out _, out _), false);
Check("光标在最前", CompletionSource.TryGetWord("abc", 0, out _, out _), false);
Check("数字字面量不是变量名", CompletionSource.TryGetWord("123", 3, out _, out _), false);
Check("运算符后没有词", CompletionSource.TryGetWord("a==", 3, out _, out _), false);
Check("空文本", CompletionSource.TryGetWord("", 0, out _, out _), false);
Check("null 文本", CompletionSource.TryGetWord(null, 0, out _, out _), false);

// 越界光标不该抛异常（拖选、程序置 CaretIndex 都可能越界）
Check("光标越界被夹住", CompletionSource.TryGetWord("abc", 99, out _, out string w6) ? w6 : "false", "abc");
Check("负光标被夹住", CompletionSource.TryGetWord("abc", -5, out _, out _), false);

// ---- 候选表：系统变量 + 全局变量 ----
// ⚠️ 变量名只允许 ASCII 标识符（GlobalVariableRules.TryValidateName 会拒绝中文），
// 所以补全的候选也一定是 ASCII —— 测试数据跟着用合法名，免得断言描述的是一种存不进来的东西。
var globalA = new GlobalVariable { Name = "StartButton", Type = DataType.Bool, Comment = "启动信号" };
var globalB = new GlobalVariable { Name = "sys_Custom", Type = DataType.Int };
var globals = new[] { globalA, globalB };

List<CompletionCandidate> expressionCandidates = CompletionSource.ForExpression(globals);
Check("表达式候选含全部系统变量", expressionCandidates.Count(c => c.Kind == "系统变量"), SystemVariable.All.Count);
Check("表达式候选含全局变量", expressionCandidates.Count(c => c.Kind == "全局变量"), 2);
Check("表达式候选里能找到 sys_Pulse1s", expressionCandidates.Any(c => c.Text == "sys_Pulse1s"), true);
Check("表达式候选里能找到用户变量", expressionCandidates.Any(c => c.Text == "StartButton"), true);
Check("用户变量带注释时说明里有注释", expressionCandidates.First(c => c.Text == "StartButton").Detail.Contains("启动信号"), true);
Check("用户变量无注释时说明里只有类型", expressionCandidates.First(c => c.Text == "sys_Custom").Detail.Contains("·"), false);

// 自由监控只认全局变量：系统变量不映射 PLC 地址，写进监控行读不出东西
List<CompletionCandidate> monitorCandidates = CompletionSource.ForMonitorName(globals);
Check("监控候选不含系统变量", monitorCandidates.Count(c => c.Kind == "系统变量"), 0);
Check("监控候选 = 全局变量数", monitorCandidates.Count, 2);

Check("变量表为空时表达式仍有系统变量", CompletionSource.ForExpression(null).Count, SystemVariable.All.Count);
Check("变量表为空时监控候选为空", CompletionSource.ForMonitorName(null).Count, 0);

// ---- 筛选：前缀优先、子串其次 ----
List<CompletionCandidate> sys = CompletionSource.Filter(expressionCandidates, "sys_");
Check("前缀命中排在第一条", sys[0].Text.StartsWith("sys_", StringComparison.OrdinalIgnoreCase), true);
Check("用户变量（无 sys_ 前缀）被排除", sys.Any(c => c.Text == "StartButton"), false);
Check("sys_Custom 也在候选里", sys.Any(c => c.Text == "sys_Custom"), true);

List<CompletionCandidate> pulse = CompletionSource.Filter(expressionCandidates, "pulse");
Check("子串命中能搜到 sys_Pulse1s", pulse.Any(c => c.Text == "sys_Pulse1s"), true);
Check("子串命中不含无关变量", pulse.Any(c => c.Text == "StartButton"), false);

// 大小写不敏感匹配，但插入的是原始大小写
List<CompletionCandidate> upper = CompletionSource.Filter(expressionCandidates, "SYS_PULSE");
Check("大写输入也能匹配", upper.Any(c => c.Text == "sys_Pulse1s"), true);
Check("插入保留原始大小写", upper.First(c => c.Text == "sys_Pulse1s").Text, "sys_Pulse1s");

Check("空词返回全部（Ctrl+Space）", CompletionSource.Filter(expressionCandidates, "").Count, expressionCandidates.Count);
Check("无命中返回空表", CompletionSource.Filter(expressionCandidates, "zzz").Count, 0);

// 前缀命中必须排在子串命中之前：敲 "sys_" 时想要的几乎总是 sys_* 而不是「中间含 sys_」的
List<CompletionCandidate> mixed = CompletionSource.Filter(
    new[]
    {
        new CompletionCandidate { Text = "x_sys_later" },   // 子串命中
        new CompletionCandidate { Text = "sys_First" },    // 前缀命中
    }, "sys_");
Check("前缀命中优先于子串命中", mixed[0].Text, "sys_First");

// 候选上限：列表再长也没人滚到底
var many = Enumerable.Range(0, CompletionSource.MaxCandidates + 20)
    .Select(i => new CompletionCandidate { Text = $"v{i}" })
    .ToList();
Check("候选数量有上限", CompletionSource.Filter(many, "v").Count, CompletionSource.MaxCandidates);


// ============================================================
// 13. 状态机采集全局变量：读计划与回填（P1.2）
//
// 背景（2026-10-21 实测发现）：状态机表达式引用全局变量，但全局变量原本**没有任何采集**，
// GlobalVariable.Value 只在启动/新增时重置为初始值，此后永不更新。
// 于是 SysState == 0 恒真、其余恒假 —— 表达式引擎、定时器、刷新链路都是好的，
// 坏的是「值根本没人填」。
//
// 本节验两件事：① 报文怎么拼（合并 / 断开 / 只收可读的）；② 回填怎么解（含字节顺序）。
// 与 IO 监控同一逻辑：只采可见页面需要的，所以「挑哪些变量」由界面决定、由 Build 收下。
// ============================================================

Console.WriteLine();
Console.WriteLine("=== 13. 状态机全局变量：读计划与回填（P1.2）===");

CommDriver xd = DriverRegistry.GetByKey("xinje_xd")!;
ByteOrderSettings xdOrders = ByteOrderSettings.FromDriver(xd);

// 造变量的辅助：解析地址走真驱动的地址表，与运行时同一条路径
GlobalVariable Var(string name, DataType type, string address, DisplayFormat format = DisplayFormat.Dec, int len = 0)
{
    var v = new GlobalVariable { Name = name, Type = type, Format = format, Address = address, Length = len };
    GlobalVariableStore.TryResolve(v, xd, out _, out _);
    return v;
}

// ---- ① 只收「能读」的变量 ----
GlobalVariable d0 = Var("a", DataType.Int, "D0");
GlobalVariable d1 = Var("b", DataType.Int, "D1");
GlobalVariable mem = new() { Name = "c", Type = DataType.Int, Address = string.Empty };
GlobalVariable bad = new() { Name = "d", Type = DataType.Int, Address = "ZZZ9" };
GlobalVariableStore.TryResolve(bad, xd, out _, out _);

List<GlobalReadBlock> planMixed = GlobalReadPlan.Build(new[] { d0, d1, mem, bad });
Check("内存变量不发报文", planMixed.Sum(b => b.Entries.Count), 2);
Check("解析失败的变量不发报文", planMixed.SelectMany(b => b.Entries).Any(e => e.Variable.Name == "d"), false);
Check("空清单返回空计划", GlobalReadPlan.Build(null).Count, 0);
Check("全部不可读时计划为空", GlobalReadPlan.Build(new[] { mem }).Count, 0);

// ---- ② 相邻地址并成一块（一次报文读完）----
Check("D0+D1 并成一块", planMixed.Count, 1);
Check("块从 D0 开始", planMixed[0].StartAddress, 0);
Check("块长 2（两个 INT）", planMixed[0].Count, 2);
Check("块内两项", planMixed[0].Entries.Count, 2);
Check("D1 在块内偏移 1", planMixed[0].Entries.First(e => e.Variable.Name == "b").Offset, 1);

// ---- ③ 有空洞就断开（隔很远一次跨读既慢又可能越界）----
GlobalVariable far = Var("e", DataType.Int, "D100");
List<GlobalReadBlock> planGap = GlobalReadPlan.Build(new[] { d0, d1, far });
Check("D0/D1 与 D100 之间断开成两块", planGap.Count, 2);
Check("两块合计仍是 3 项", planGap.Sum(b => b.Entries.Count), 3);

// ---- ④ 32 位类型占两个寄存器，块长按占用算 ----
GlobalVariable dint = Var("f", DataType.DInt, "D20");
GlobalVariable next = Var("g", DataType.Int, "D22");
List<GlobalReadBlock> plan32 = GlobalReadPlan.Build(new[] { dint, next });
Check("DINT 占 2 个寄存器", plan32[0].Entries.First(e => e.Variable.Name == "f").Units, 2);
Check("紧随其后的 INT 并进同一块", plan32.Count, 1);
Check("块长 = 2 + 1", plan32[0].Count, 3);

// ---- ⑤ 线圈与寄存器分属不同块（功能码不同，绝不能混读）----
GlobalVariable coil = Var("h", DataType.Bool, "M10", DisplayFormat.Bin);
List<GlobalReadBlock> planAreas = GlobalReadPlan.Build(new[] { d0, coil });
Check("线圈与寄存器分成两块", planAreas.Count, 2);
Check("线圈块在 Coil 区", planAreas.Any(b => b.Area == ModbusArea.Coil), true);
Check("寄存器块在 HoldingRegister 区", planAreas.Any(b => b.Area == ModbusArea.HoldingRegister), true);

// ---- ⑥ 回填：整数 ----
GlobalVariable target = Var("sysState", DataType.Int, "D10");
List<GlobalReadBlock> planRead = GlobalReadPlan.Build(new[] { target });
GlobalReadPlan.Apply(planRead[0], null, new ushort[] { 8 }, xdOrders);
Check("INT 回填 8", target.Value, "8");
GlobalReadPlan.Apply(planRead[0], null, new ushort[] { 0 }, xdOrders);
Check("INT 回填 0", target.Value, "0");

// ---- ⑦ 回填：32 位按字节顺序解（真机黄金样本 123456 = 0xE240,0x0001）----
GlobalVariable dintVal = Var("v32", DataType.DInt, "D800");
GlobalReadBlock planDint = GlobalReadPlan.Build(new[] { dintVal })[0];
GlobalReadPlan.Apply(planDint, null, new ushort[] { 0xE240, 0x0001 }, xdOrders);
Check("DINT 按 3412 解回 123456", dintVal.Value, "123456");

// ---- ⑧ 回填：线圈 ----
GlobalVariable coilVal = Var("flag", DataType.Bool, "M10", DisplayFormat.Bin);
GlobalReadBlock planCoil = GlobalReadPlan.Build(new[] { coilVal })[0];
GlobalReadPlan.Apply(planCoil, new[] { true }, null, xdOrders);
Check("线圈回填 1", coilVal.Value, "1");
GlobalReadPlan.Apply(planCoil, new[] { false }, null, xdOrders);
Check("线圈回填 0", coilVal.Value, "0");

// ---- ⑨ 响应长度不足时保留上次值（不要清成 0）----
GlobalVariable keep = Var("keep", DataType.Int, "D30");
GlobalReadBlock planKeep = GlobalReadPlan.Build(new[] { keep })[0];
GlobalReadPlan.Apply(planKeep, null, new ushort[] { 42 }, xdOrders);
Check("先写入 42", keep.Value, "42");
GlobalReadPlan.Apply(planKeep, null, new ushort[0], xdOrders);
Check("长度不足时保留上次值（不清零）", keep.Value, "42");
GlobalReadPlan.Apply(planKeep, null, null, xdOrders);
Check("没有数据时保留上次值", keep.Value, "42");

Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine($"结果：{pass} 通过 / {fail} 失败");
Console.WriteLine("============================================================");
return fail == 0 ? 0 : 1;
