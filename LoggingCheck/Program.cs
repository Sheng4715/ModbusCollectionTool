using System.Text;
using DSH_Modbus采集工具.Logging;

// DeviceLogFileWriter 的独立断言（P5-2）。不需要窗口，直接跑：
//   dotnet run --project LoggingCheck
//
// 覆盖重点是**只看代码不容易发现、但一错就静默丢数据**的地方：
//   1. 每行格式严格是 [时间] [级别] 消息
//   2. 跨天自动换文件（应用常驻跨零点）
//   3. 同一天两次启动**续写**而不是覆盖
//   4. 换目录后不继续写进旧目录
//   5. 关闭开关后不再落盘、且不发散成异常
//   6. 目录不可写时吞掉异常并记录原因（不能因写日志而崩）

int passed = 0, failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"  [PASS] {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  [FAIL] {name}" + (detail == null ? "" : $"  -> {detail}"));
    }
}

string root = Path.Combine(Path.GetTempPath(), "dsh-logging-check-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);

// 读日志文件时必须显式允许 ReadWrite 共享：写入器在应用运行期间一直持有写句柄
// （这正是生产场景 —— 用户会在应用运行时打开日志），而 File.ReadAllLines 默认只
// 请求 FileShare.Read，会与写句柄冲突。这里模拟"应用在跑、用户在看日志"的真实姿势，
// 而不是先把写入器关掉再读（那样就测不到共享行为了）。
static string[] ReadLog(string path)
{
    if (!File.Exists(path))
    {
        return Array.Empty<string>();
    }

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs, Encoding.UTF8);
    var list = new List<string>();
    while (sr.ReadLine() is { } line)
    {
        list.Add(line);
    }

    return list.ToArray();
}

// 同样必须共享读：写入器持有写句柄时不放开共享就拿不到字节流
static byte FirstByteOf(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    return (byte)fs.ReadByte();
}

try
{
    // ---------- 1. 行格式 ----------
    Console.WriteLine("1. 行格式 [时间] [级别] 消息");
    {
        string dir = Path.Combine(root, "fmt");
        using var w = new DeviceLogFileWriter(dir, enabled: true);
        var t = new DateTime(2026, 10, 15, 11, 16, 54);
        w.Write(t, LogFileLevel.INFO, "已连接：192.168.1.66:502");
        w.Write(t.AddSeconds(1), LogFileLevel.WARN, "通讯异常：响应超时");
        w.Write(t.AddSeconds(2), LogFileLevel.ERROR, "已断开：网络不可达");

        var lines = ReadLog(DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15)));
        Check("写满 3 行", lines.Length == 3, $"实际 {lines.Length} 行");
        Check("INFO 行逐字符吻合",
            lines.Length > 0 && lines[0] == "[2026-10-15 11:16:54] [INFO] 已连接：192.168.1.66:502",
            lines.Length > 0 ? lines[0] : "(无)");
        Check("WARN 行逐字符吻合",
            lines.Length > 1 && lines[1] == "[2026-10-15 11:16:55] [WARN] 通讯异常：响应超时",
            lines.Length > 1 ? lines[1] : "(无)");
        Check("ERROR 行逐字符吻合",
            lines.Length > 2 && lines[2] == "[2026-10-15 11:16:56] [ERROR] 已断开：网络不可达",
            lines.Length > 2 ? lines[2] : "(无)");
        Check("无 BOM（首字节不是 EF BB BF）", FirstByteOf(
            DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15))) == (byte)'[');

        // 文件名必须是日期
        Check("文件名为日期 .log",
            Path.GetFileName(DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15))) == "2026-10-15.log");
        Check("中文字符未损坏（UTF-8 正确解码）", lines.Length > 0 && lines[0].Contains("已连接"));
    }

    // ---------- 2. 跨天换文件 ----------
    Console.WriteLine("2. 跨天自动换文件");
    {
        string dir = Path.Combine(root, "roll");
        using var w = new DeviceLogFileWriter(dir, enabled: true);
        w.Write(new DateTime(2026, 10, 15, 23, 59, 59), LogFileLevel.INFO, "当天最后一条");
        w.Write(new DateTime(2026, 10, 16, 0, 0, 1), LogFileLevel.INFO, "次日第一条");
        // 再回写前一天：日期回退也必须换回去，不能"只认更大日期"
        w.Write(new DateTime(2026, 10, 15, 23, 59, 59), LogFileLevel.INFO, "又回前一天");

        string d15 = DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15));
        string d16 = DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 16));

        Check("15 日文件存在", File.Exists(d15));
        Check("16 日文件存在", File.Exists(d16));
        Check("15 日得到 2 行（首条 + 回写那条）",
            File.Exists(d15) && ReadLog(d15).Length == 2,
            File.Exists(d15) ? string.Join(" | ", ReadLog(d15)) : "(无文件)");
        Check("16 日得到 1 行",
            File.Exists(d16) && ReadLog(d16).Length == 1,
            File.Exists(d16) ? string.Join(" | ", ReadLog(d16)) : "(无文件)");
        Check("两条日志没有串到同一个文件里",
            File.Exists(d15) && File.Exists(d16) &&
            ReadLog(d15).All(l => l.Contains("当天最后一条") || l.Contains("又回前一天")) &&
            ReadLog(d16).All(l => l.Contains("次日第一条")));
    }

    // ---------- 3. 续写而非覆盖 ----------
    Console.WriteLine("3. 同一天两次启动续写");
    {
        string dir = Path.Combine(root, "append");
        using (var w1 = new DeviceLogFileWriter(dir, enabled: true))
        {
            w1.Write(new DateTime(2026, 10, 15, 8, 0, 0), LogFileLevel.INFO, "第一次启动");
        }

        using (var w2 = new DeviceLogFileWriter(dir, enabled: true))
        {
            w2.Write(new DateTime(2026, 10, 15, 9, 0, 0), LogFileLevel.INFO, "第二次启动");
        }

        var lines = ReadLog(DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15)));
        Check("两次启动共 2 行（后一次没覆盖前一次）", lines.Length == 2, $"实际 {lines.Length} 行");
        Check("第一行是第一次启动写的", lines.Length > 0 && lines[0].Contains("第一次启动"));
        Check("第二行是第二次启动写的", lines.Length > 1 && lines[1].Contains("第二次启动"));
    }

    // ---------- 4. 换目录 ----------
    Console.WriteLine("4. 换目录后不再写旧目录");
    {
        string a = Path.Combine(root, "dirA");
        string b = Path.Combine(root, "dirB");
        using var w = new DeviceLogFileWriter(a, enabled: true);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 0), LogFileLevel.INFO, "写在 A");
        w.SetDirectory(b);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 1), LogFileLevel.INFO, "写在 B");

        string fa = DeviceLogFileWriter.FilePathFor(a, new DateOnly(2026, 10, 15));
        string fb = DeviceLogFileWriter.FilePathFor(b, new DateOnly(2026, 10, 15));

        Check("A 目录只有 1 行", File.Exists(fa) && ReadLog(fa).Length == 1,
            File.Exists(fa) ? string.Join(" | ", ReadLog(fa)) : "(无文件)");
        Check("B 目录只有 1 行", File.Exists(fb) && ReadLog(fb).Length == 1,
            File.Exists(fb) ? string.Join(" | ", ReadLog(fb)) : "(无文件)");
        Check("换目录后没有继续写进 A",
            File.Exists(fa) && ReadLog(fa).All(l => l.Contains("写在 A")));
        Check("DirectoryPath 已更新", w.DirectoryPath == b);
    }

    // ---------- 5. 开关 ----------
    Console.WriteLine("5. 关闭开关后不落盘");
    {
        string dir = Path.Combine(root, "switch");
        using var w = new DeviceLogFileWriter(dir, enabled: true);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 0), LogFileLevel.INFO, "开启时的第一条");
        w.SetEnabled(false);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 1), LogFileLevel.INFO, "关闭后不该出现");
        w.SetEnabled(true);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 2), LogFileLevel.INFO, "重新开启后的第二条");

        var lines = ReadLog(DeviceLogFileWriter.FilePathFor(dir, new DateOnly(2026, 10, 15)));
        Check("共 2 行（关闭期间那条被丢弃）", lines.Length == 2, $"实际 {lines.Length} 行");
        Check("不含关闭期间的消息", lines.All(l => !l.Contains("关闭后不该出现")));
        Check("重新开启后能继续写", lines.Any(l => l.Contains("重新开启后的第二条")));
        Check("重新开启后是续写不是新建", lines.Any(l => l.Contains("开启时的第一条")));
        Check("关闭时不产生错误", w.LastError == null, w.LastError);
    }

    // ---------- 6. 目录不可写 ----------
    Console.WriteLine("6. 目录不可写时不抛异常、记录原因");
    {
        // 用一个**已存在的文件**充当目录路径 —— CreateDirectory 必然失败，
        // 且不依赖权限设置（跨机器可复现）
        string blocker = Path.Combine(root, "not-a-dir");
        File.WriteAllText(blocker, "x");
        string badDir = Path.Combine(blocker, "sub");

        using var w = new DeviceLogFileWriter(badDir, enabled: true);
        bool threw = false;
        try
        {
            w.Write(new DateTime(2026, 10, 15, 10, 0, 0), LogFileLevel.INFO, "写不进去");
        }
        catch
        {
            threw = true;
        }

        Check("Write 不抛异常", !threw);
        Check("LastError 记录了原因", w.LastError != null, w.LastError);
        Check("失败时行数不增加", w.LinesWritten == 0, $"LinesWritten={w.LinesWritten}");

        // 失败之后恢复：把挡路的文件删掉，下一次写入应当自动重试成功
        File.Delete(blocker);
        w.Write(new DateTime(2026, 10, 15, 10, 0, 1), LogFileLevel.INFO, "恢复后写进去");
        Check("失败后能自愈（重新打开成功）", w.LastError == null, w.LastError);
        Check("自愈后确实落盘",
            File.Exists(DeviceLogFileWriter.FilePathFor(badDir, new DateOnly(2026, 10, 15))));
    }

    // ---------- 7. 默认目录 ----------
    Console.WriteLine("7. 默认目录");
    {
        string def = DeviceLogFileWriter.DefaultDirectory;
        Check("默认目录是 exe 旁的 Log", def == Path.Combine(AppContext.BaseDirectory, "Log"), def);
        Check("默认目录以 Log 结尾", Path.GetFileName(def) == "Log");
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* 清理失败无所谓 */ }
}

Console.WriteLine();
Console.WriteLine($"通过 {passed} 项，失败 {failed} 项");
return failed == 0 ? 0 : 1;
