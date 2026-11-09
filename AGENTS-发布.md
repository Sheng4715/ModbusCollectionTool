# AGENTS-发布.md — 发布与交付

> 本文件是 [`AGENTS.md`](AGENTS.md) 的配套文档，**只讲怎么把程序交付到现场机**。
> 拆出来的原因同 `AGENTS-细节.md`：`AGENTS.md` 有 **65536 字节**的工作区指令上限，
> 超了会被**整体丢弃**（曾发生一次，规则一条都没生效）。故长段落一律放这里，
> `AGENTS.md` 只留一句指向。
>
> 相关：实现细节见 [`AGENTS-细节.md`](AGENTS-细节.md)；需求见 `Brief.md`；设计理由见 `Design.md`。

---

## 12. 发布与交付（2026-11-03 定案）

**形态：框架依赖（framework-dependent）**。产物只有 **4 个文件 / 约 593 KB**，目标机必须预装
**\.NET 8 Desktop Runtime**。发布到 `Release\<程序集名>_<版本号>\`，`Release/` 已进 `.gitignore`。

> 全部结论均为 2026-11-03 实测（发布 → 启动 → 改配置 → 覆盖更新 → 拷到"第二台机器" → 只读目录），
> 不是推断。**唯独"目标机没装运行时"这条没实测**（本机 8/9/10 全装，且 apphost 会查注册表兜底）。
> 体积于 2026-11-09 重新实测（dll 随功能增长 373 → 452.5 KB），**以本文数字为准**。

### 12.1 发布命令

```powershell
# ① 必须先结束进程（exe 被锁 → MSB3027）
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force

# ② 取版本号：从已发布的 exe 读 ProductVersion，去掉 MSBuild 附加的 +<git sha>
$ver = (Get-Item "Release\*\DSH_Modbus采集工具.exe" | Select-Object -First 1).VersionInfo.ProductVersion -replace '\+.*$',''

# ③ 发布
dotnet publish "DSH_Modbus采集工具\DSH_Modbus采集工具.csproj" `
  -c Release --self-contained false -p:DebugType=None `
  -o "Release\DSH_Modbus采集工具_$ver" -v q -nologo
```

| 参数 | 作用 | 漏掉的后果 |
|---|---|---|
| `-c Release` | Release 配置 | 版本变 `1.0.0-debug`（`VersionSuffix` 由配置决定） |
| `--self-contained false` | 显式声明框架依赖 | 目前是默认值，写上防将来 csproj 加了 `SelfContained` 被改 |
| `-p:DebugType=None` | 不产出 `.pdb` | 多一个 163 KB 的 `.pdb`（内部排查可留，对外交付必去） |
| `-o Release\<名>_<版本>` | 版本号目录 | 落到 `bin\Release\...\publish\`，与调试产物混在一起容易拷错 |

⚠️ **首次发布没有旧目录可取版本号** —— 此时直接写死（`DSH_Modbus采集工具_1.0.0`），
或从 `csproj` 的 `<VersionPrefix>` 取。**不要**自己拼一个和程序集不一致的号：
界面显示的是**程序集里的**版本（`AppInfo.Version`），两边不一致就永远对不上。

### 12.2 产物

```
Release\DSH_Modbus采集工具_1.0.0\                       合计 4 个文件 / 593 KB
├── DSH_Modbus采集工具.exe                  140 KB   apphost，只负责找运行时、加载 dll
├── DSH_Modbus采集工具.dll                452.5 KB   全部业务代码（含 XAML 编译出的 BAML）
├── DSH_Modbus采集工具.deps.json           0.4 KB   依赖清单（本项目只有自己一个库）
└── DSH_Modbus采集工具.runtimeconfig.json  0.4 KB   声明要 .NET 8 桌面运行时
```

- 实测 exe 属性：`FileVersion 1.0.0.0` / `ProductVersion 1.0.0+<sha>` / `ProductName DSH Modbus 采集工具`
- `runtimeconfig.json` 里是**两条** `frameworks`：`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`。
  后者是 WPF 独有 —— 目标机装错成体积更小的 **Console Runtime** 会起不来
- `.deps.json` 只有一个条目，正是「零第三方 NuGet」的直接证据

### 12.3 包里**没有**什么（重要）

下列全部是**运行时生成**、跟着 exe 目录走（`AppContext.BaseDirectory`，7 处），`dotnet publish` 不会带：

| 文件 | 内容 |
|---|---|
| `app-settings.json` | 主题 / 网卡名 / IP / 端口 / 日志设置 / 自动连接 / 应用标题 |
| `io-comments.json` | IO 点位注释（导入 IO 表后生成） |
| `data-points.json` | 自由监控项 |
| `GlobalVar.json` | 全局变量表 |
| `machine-state.json` | 状态机 9 个状态的文字/颜色/表达式 |
| `acquisition-tasks.json` | 采集任务组与数据项 |
| `Images\` | 实时画布背景图（json 里存**相对路径**） |
| `Log\` / `DeviceData\` | 日志 / 采集出的 CSV |

**2026-11-09 起，配置落盘还会在旁边留下两类文件**（都是运行时的自愈产物，**打包时不要拷**）：

| 后缀 | 什么时候出现 | 用途 |
|---|---|---|
| `*.json.bak` | **每次**成功写入配置时（`AtomicFile` 用 `File.Replace` 保留上一版） | 覆盖前的那一份，手工回滚用 |
| `*.json.bad-<时间戳>` | 某份 json **整体反序列化失败**时 | 原始坏文件改名保留，防后续 Save 把证据抹掉 |

两者都是「原始字节还在」的保险，**不做自动恢复**（不会被读回来当配置用）。所以交付/复制配置时
按 §12.9 的白名单拷，`.bak` / `.bad-*` / `.tmp` 一律排除。

所以**首次运行是默认状态**（IP `192.168.1.100:502`、无监控项、状态机用内置默认）。
要做「拿到就能连现场」的包，得按白名单另外拷这些 —— **不能整目录拷**，会带进 `Log\`、
`DeviceData\`、`*.bak`、`stderr.txt`、`stdout.txt`。

### 12.4 ⚠️ 交付位置的硬约束：**不能装进 Program Files**

实测（对目录 Deny Write 后启动）：

| 检查项 | 结果 |
|---|---|
| 能否启动 | ✅ 能，不崩 |
| `Log\` 是否创建 | ❌ 没有 |
| `app-settings.json` 是否更新 | ❌ 时间戳不变 |

原因：`ThemeManager.Save()` 与日志写入都是 `try { } catch { }` 静默吞掉异常。
**用户看到的是"改了主题，下次启动又变回去"，而且一条日志都没有**（写日志本身就失败了）。
→ 装到 `D:\Apps\...`、桌面或用户目录；**不要** `C:\Program Files\`。

### 12.5 更新：只覆盖 4 个文件

实测：覆盖 exe/dll 后，`app-settings.json` 哈希与更新前**完全一致**，`Images\`、`Log\`、六个 json 原样保留。
**不需要重装运行时**，也不需要动配置 —— 这是框架依赖相对自包含最实际的收益（373 KB vs 150 MB）。

```powershell
Get-Process -Name "DSH_Modbus采集工具" -ErrorAction SilentlyContinue | Stop-Process -Force
# ① 备份配置（原因见下）
Copy-Item "$app\*.json" "_backup_$(Get-Date -f yyyyMMdd_HHmmss)"
# ② 只覆盖程序本体
Copy-Item "Release\DSH_Modbus采集工具_1.0.0\*.exe",
          "Release\DSH_Modbus采集工具_1.0.0\*.dll",
          "Release\DSH_Modbus采集工具_1.0.0\*.deps.json",
          "Release\DSH_Modbus采集工具_1.0.0\*.runtimeconfig.json" $app -Force
```

**怎么确认更新生效**：看设置页「项目信息」的**构建日期**（由 `WriteBuildDate` 每次编译现取）。
版本号只在提版本时变，**不能**用来判断这次更新是否生效。

⚠️ **备份不是"以防万一"，是防一条具体的数据丢失路径**（历史缺陷，2026-11-09 已加固但仍建议备份）：

```csharp
// 加固前的形状（已删除，不要写回来）：反序列化失败 → 空集合 → 下次 Save 覆盖 → 原数据消失
catch { return new List<AcquisitionTask>(); }
```

当时的链路：新版若改了 json 字段/结构导致反序列化失败 → `Load()` 返回**空集合** → 界面看是
"任务全没了" → 用户随手改一项 → `Save()` **把空集合写回文件** → 原数据彻底消失。

**现在**（见 `AGENTS-细节.md` §13）：

- 整体反序列化失败**不再回空集合**，而是逐个元素抢救（坏的跳过、好的照收）；
- 抢救前先把原文件改名成 `*.json.bad-<时间戳>`，**证据不会被后续 Save 抹掉**；
- 写盘改原子替换，覆盖前那一版保留为 `*.json.bak`；
- `app-settings.json` 写失败会以琥珀警告出现在运行日志里（不再静默）。

**但仍然建议备份**：`.bak` 只保留**上一版**，连着改两次就滚掉了；`.bad-*` 只是原始字节，
恢复得人工看 json。所以升级后第一件事仍是打开界面，确认任务组 / 变量表 / 状态机都还在。

**回滚**：每个版本一个目录的意义就在这 —— 换回旧目录的 dll，再把备份的 json 拷回来即可。**旧版本目录不要删。**

### 12.6 为什么不用自包含 / 单文件 / MSIX

| 方案 | 结论 | 理由 |
|---|---|---|
| 框架依赖 | ✅ **当前采用** | 513 KB；更新只换 373 KB |
| 自包含（`--self-contained true`） | ⚠️ 备选 | 150–180 MB；免装运行时，现场机器不可控时才用 |
| 单文件（`PublishSingleFile`） | ❌ 不用 | 体积同自包含、首次启动要解压、`.deps.json`/`.runtimeconfig.json` 被吞进 exe 后无法在目标机改运行时绑定 |
| 裁剪（`PublishTrimmed`） | ❌ **绝对不能开** | WPF 不支持裁剪，XAML 里靠反射/字符串的路径会被裁掉，症状是"某几个页面一点就崩"，且**只在发布版出现** |
| MSIX / Store | ❌ 不用 | 包内写入被虚拟化重定向，"写 exe 旁"的行为在沙箱里会变 |
| Inno Setup / MSI | 可选 | 要快捷方式和卸载入口时再加；装到 `Program Files` 仍需先解决 12.4 |

### 12.7 目标机前置条件

**要装的东西只有一样：.NET 8 桌面运行时（Windows Desktop Runtime）x64。**

| 项 | 值 |
|---|---|
| 下载页 | `https://dotnet.microsoft.com/download/dotnet/8.0` → 选「**.NET Desktop Runtime 8.0.x**」→ Windows → **x64** |
| 安装包文件名 | `windowsdesktop-runtime-8.0.<补丁号>-win-x64.exe`（约 55 MB） |
| 静默安装（无网批量部署） | `windowsdesktop-runtime-8.0.x-win-x64.exe /install /quiet /norestart` |
| 权限 | 需要管理员（装到 `C:\Program Files\dotnet`） |
| 支持的 OS | Windows 10 1607 及以上（含 LTSC）/ Windows 11，x64 |

```powershell
dotnet --list-runtimes | Select-String "WindowsDesktop"        # 有 dotnet 命令时（装了别的 .NET 也会有）
Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8*"   # 没有任何 .NET 时也能判
```
两条都返回空 → 运行时没装，程序双击无反应（WinExe 无控制台，**看不到任何报错**）。

- **必须是 Desktop Runtime，不是 Console Runtime**：WPF 要的是 `Microsoft.WindowsDesktop.App`，
  体积更小的 Console Runtime（`Microsoft.NETCore.App` 单独一份）**起不来**。`runtimeconfig.json`
  里那两条 `frameworks` 就是这件事的直接证据
- **不需要**：SDK、NuGet 包、VC++ 运行库、IIS、.NET Framework 3.5
- **不需要预先装 .NET 8 的其它补丁**：安装包自己带

⚠️ `runtimeconfig.json` 请求的是 `8.0.0`，按 roll-forward 策略只会在 **8.x** 内往上取补丁；
**默认不跨大版本** —— 目标机只装了 .NET 9/10 时会报缺 8.0。
（此条为 .NET 既定行为，**本机未能实测**，上现场前在一台干净机器验一次。）
不建议加 `<RollForward>LatestMajor</RollForward>`：那会让程序跑在没测过的运行时上。

**如果现场机器实在不允许装运行时** → 改用自包含发布（免装任何运行时，约 150–180 MB）：

```powershell
dotnet publish "DSH_Modbus采集工具\DSH_Modbus采集工具.csproj" `
  -c Release --self-contained true -r win-x64 -p:DebugType=None `
  -o "Release\DSH_Modbus采集工具_1.0.0_sc" -v q -nologo
```
⚠️ **不要加 `-p:PublishTrimmed=true`**：WPF 不支持裁剪，XAML 里靠反射/字符串的路径会被裁掉，
症状是「某几个页面一点就崩」，且**只在发布版出现**（见 §12.6）。

### 12.8 复制到另一台机器（实测可行，但要改 4 处）

实测：把发布目录 + 六个 json + `Images\` 拷到新目录、改掉 IP 后启动 → 正常读入新配置
（日志显示它连的确实是新 IP）。**没有任何机器绑定 / 加密校验**，程序完全按目录内容运行。

| # | 项 | 为什么 |
|---|---|---|
| ① | `app-settings.json` 的 `TargetNic` | 存的是**网卡名**（如 `以太网`）。名字对不上时实测**不会崩、也不会改写 json**，只是回退到下拉框第 0 项 —— 而第 0 项很可能是 WLAN/Tailscale，表现为**连接超时**（同网段双网卡时尤其容易误判成"PLC 没响应"）。复制后**必须手动重选一次网卡** |
| ② | `acquisition-tasks.json` 的 `SavePath` | 是**绝对路径**（当前值 `C:\Users\G.Sheng\Desktop\任务组数据`）。对端不存在时 `Directory.CreateDirectory` 会去建它，无权限则每周期在日志里刷一条失败。建议改空串（= `exe\DeviceData\`，跟着目录走） |
| ③ | `app-settings.json` 的 `LogDirectory` | 空串 = 默认 `exe\Log`（安全）；用「浏览」选过就会存绝对路径 |
| ④ | 两台同时连同一个 PLC | 读多主站一般可以；**写**会互相打架；两台都记录且指向同一路径时，`File.AppendAllText` 会让 CSV 行交错。建议一台采集+记录、另一台只监看 |

**不用改**：`io-comments.json` / `data-points.json` / `GlobalVar.json` / `machine-state.json`
（纯配置，无路径）、`Images\`（相对路径）。

⚠️ **设备类型（驱动品牌）现在会持久化**（2026-11-09 用户要求；此前写在构造函数里
`SelectedIndex = 0`，每次启动都回信捷）。位置是 `app-settings.json` → `DeviceTypeKey`，
存的是驱动的 **`Key`**（`xinje_xd` / `inovance_easy`），不是显示名 —— 改文案或加品牌都不会错行。

- 旧 json 没有这个字段 → 回退注册表第 0 个（信捷 XD/XL），**与升级前行为一致，无需迁移**
- 该品牌后来被从 `DriverRegistry` 删掉 / Key 被手改坏 → 同样静默回退第 0 个，不弹框不报错
- **复制配置到另一台机器时它会跟着带过去**（复制 json 即可），这点与升级前相反：
  升级前是「不同品牌就要每次手动切」，现在不用了

⚠️ **三套字节顺序仍然不持久化**（用户 2026-10 定案）：它由驱动默认值决定，
切换设备类型时整份重置（见 §8.1）。**这是刻意的** —— 存下来会出现「换了品牌、字序还是
旧品牌」的静默错值，而两品牌唯一差异恰好在字符串序（信捷 `12` / 汇川 `21`）。
所以复制配置到对端后，**字节顺序是驱动默认值，不是原机器上手动改过的值**。
同品牌同型号没问题；若在现场手动调过字序，换机器要重调一次。

### 12.9 发布时不要做的事

- ❌ **不要拷 `bin\Debug\net8.0-windows` 当交付包**：带 `.pdb`、`stderr.txt`、`stdout.txt`、`*.json.bak`
- ❌ **不要用通配符整目录拷配置**：`Log\` / `DeviceData\` / `*.bak` / `*.bad-*` 会被一起带走
  （`.bak` 与 `.bad-*` 是 2026-11-09 起 `Modeling/AtomicFile.cs` 自动生成的，见 §12.3）
- ⚠️ **`dotnet publish` 不清理 `-o` 目录**：实测往已有目录发布，旧的 `Log\`、`DeviceData\`、上一个版本遗留的文件**全都还在**。做交付包用带版本号的新目录，或先 `Remove-Item -Recurse`；**原地更新则相反 —— 要保留 `Log\` 和 `DeviceData\`**
- ✅ 运行时垃圾本来就不会进包：`Log\`、六个 json 在源码目录里也不会被拷进 publish（SDK 把它们收成 `None` 项，默认不复制）

### 12.10 交付前验证清单

```powershell
$dir = "Release\DSH_Modbus采集工具_1.0.0"
Get-ChildItem $dir -Recurse -File            # ① 应为 4 个文件，无 pdb、无 Log
(Get-Item "$dir\DSH_Modbus采集工具.exe").VersionInfo.ProductVersion   # ② 应为干净的 1.0.0+<sha>
Get-Content "$dir\DSH_Modbus采集工具.runtimeconfig.json" | Select-String "Microsoft\."  # ③ 必须含 WindowsDesktop
Start-Process "$dir\DSH_Modbus采集工具.exe"; Start-Sleep 6
Get-Process "DSH_Modbus采集工具" | Select-Object MainWindowTitle      # ④ 窗口应出现
```

然后在**目标机**上：`Test-Path` 运行时 → 双击能开 → 改主题、关掉、重开 → **主题还在**（这一条同时验证目录可写）。
自检完把 `Log\` 和 `app-settings.json` 删掉，包才是干净的出厂状态。
