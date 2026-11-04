# AGENTS-发布.md — 发布与交付

> 本文件是 [`AGENTS.md`](AGENTS.md) 的配套文档，**只讲怎么把程序交付到现场机**。
> 拆出来的原因同 `AGENTS-细节.md`：`AGENTS.md` 有 **65536 字节**的工作区指令上限，
> 超了会被**整体丢弃**（曾发生一次，规则一条都没生效）。故长段落一律放这里，
> `AGENTS.md` 只留一句指向。
>
> 相关：实现细节见 [`AGENTS-细节.md`](AGENTS-细节.md)；需求见 `Brief.md`；设计理由见 `Design.md`。

---

## 12. 发布与交付（2026-11-03 定案）

**形态：框架依赖（framework-dependent）**。产物只有 **4 个文件 / 约 513 KB**，目标机必须预装
**\.NET 8 Desktop Runtime**。发布到 `Release\<程序集名>_<版本号>\`，`Release/` 已进 `.gitignore`。

> 全部结论均为 2026-11-03 实测（发布 → 启动 → 改配置 → 覆盖更新 → 拷到"第二台机器" → 只读目录），
> 不是推断。**唯独"目标机没装运行时"这条没实测**（本机 8/9/10 全装，且 apphost 会查注册表兜底）。

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
| `-c Release` | Release 配置 | 版本变 `0.6.0-debug`（`VersionSuffix` 由配置决定） |
| `--self-contained false` | 显式声明框架依赖 | 目前是默认值，写上防将来 csproj 加了 `SelfContained` 被改 |
| `-p:DebugType=None` | 不产出 `.pdb` | 多一个 163 KB 的 `.pdb`（内部排查可留，对外交付必去） |
| `-o Release\<名>_<版本>` | 版本号目录 | 落到 `bin\Release\...\publish\`，与调试产物混在一起容易拷错 |

⚠️ **首次发布没有旧目录可取版本号** —— 此时直接写死（`DSH_Modbus采集工具_0.6.0`），
或从 `csproj` 的 `<VersionPrefix>` 取。**不要**自己拼一个和程序集不一致的号：
界面显示的是**程序集里的**版本（`AppInfo.Version`），两边不一致就永远对不上。

### 12.2 产物

```
Release\DSH_Modbus采集工具_0.6.0\
├── DSH_Modbus采集工具.exe                 140 KB   apphost，只负责找运行时、加载 dll
├── DSH_Modbus采集工具.dll                 373 KB   全部业务代码（含 XAML 编译出的 BAML）
├── DSH_Modbus采集工具.deps.json           0.4 KB   依赖清单（本项目只有自己一个库）
└── DSH_Modbus采集工具.runtimeconfig.json  0.4 KB   声明要 .NET 8 桌面运行时
```

- 实测 exe 属性：`FileVersion 0.6.0.0` / `ProductVersion 0.6.0+<sha>` / `ProductName DSH Modbus 采集工具`
- `runtimeconfig.json` 里是**两条** `frameworks`：`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`。
  后者是 WPF 独有 —— 目标机装错成体积更小的 **Console Runtime** 会起不来
- `.deps.json` 只有一个条目，正是「零第三方 NuGet」的直接证据

### 12.3 包里**没有**什么（重要）

下列全部是**运行时生成**、跟着 exe 目录走（`AppContext.BaseDirectory`，7 处），`dotnet publish` 不会带：

| 文件 | 内容 |
|---|---|
| `app-settings.json` | 主题 / 网卡名 / IP / 端口 / 日志设置 / 自动连接 |
| `io-comments.json` | IO 点位注释（导入 IO 表后生成） |
| `data-points.json` | 自由监控项 |
| `GlobalVar.json` | 全局变量表 |
| `machine-state.json` | 状态机 9 个状态的文字/颜色/表达式 |
| `acquisition-tasks.json` | 采集任务组与数据项 |
| `Images\` | 实时画布背景图（json 里存**相对路径**） |
| `Log\` / `DeviceData\` | 日志 / 采集出的 CSV |

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
Copy-Item "Release\DSH_Modbus采集工具_0.7.0\*.exe",
          "Release\DSH_Modbus采集工具_0.7.0\*.dll",
          "Release\DSH_Modbus采集工具_0.7.0\*.deps.json",
          "Release\DSH_Modbus采集工具_0.7.0\*.runtimeconfig.json" $app -Force
```

**怎么确认更新生效**：看设置页「项目信息」的**构建日期**（由 `WriteBuildDate` 每次编译现取）。
版本号只在提版本时变，**不能**用来判断这次更新是否生效。

⚠️ **备份不是"以防万一"，是防一条具体的数据丢失路径**：

```csharp
// AcquisitionTask.cs:595  同样模式也在 DataPointStore / GlobalVariableStore / MachineStateStore
catch { return new List<AcquisitionTask>(); }
```

新版若改了 json 字段/结构导致反序列化失败 → `Load()` 返回**空集合** → 界面看是"任务全没了" →
用户随手改一项 → `Save()` **把空集合写回文件** → 原数据彻底消失。
所以升级后第一件事是打开界面确认任务组 / 变量表 / 状态机都还在。

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

```powershell
dotnet --list-runtimes | Select-String "WindowsDesktop"        # 有安装 SDK 时
Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8*"   # 没有 dotnet 命令时
```

安装包装的是 **Desktop Runtime**（约 55 MB），不是 Console Runtime，也不需要 SDK。
需要管理员权限（写 `C:\Program Files\dotnet`）。

⚠️ `runtimeconfig.json` 请求的是 `8.0.0`，按 roll-forward 策略只会在 **8.x** 内往上取补丁；
**默认不跨大版本** —— 目标机只装了 .NET 9/10 时会报缺 8.0。
（此条为 .NET 既定行为，**本机未能实测**，上现场前在一台干净机器验一次。）
不建议加 `<RollForward>LatestMajor</RollForward>`：那会让程序跑在没测过的运行时上。

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

⚠️ **设备类型（驱动品牌）与三套字节顺序都不持久化** —— `DeviceTypeComboBox.SelectedIndex = 0`
写在构造函数里，字节顺序由驱动默认重置（见 §8.1）。所以这两项**本来就不在 json 里**，
复制配置不会带过去，两台机器每次启动都回到「信捷 XD/XL + 驱动默认字序」。
同品牌同型号的两台机器没问题；**不同品牌就要每次手动切**。

### 12.9 发布时不要做的事

- ❌ **不要拷 `bin\Debug\net8.0-windows` 当交付包**：带 `.pdb`、`stderr.txt`、`stdout.txt`、`*.json.bak`
- ❌ **不要用通配符整目录拷配置**：`Log\` / `DeviceData\` / `*.bak` 会被一起带走（`*.bak` 代码里根本没有引用，是手工备份）
- ⚠️ **`dotnet publish` 不清理 `-o` 目录**：实测往已有目录发布，旧的 `Log\`、`DeviceData\`、上一个版本遗留的文件**全都还在**。做交付包用带版本号的新目录，或先 `Remove-Item -Recurse`；**原地更新则相反 —— 要保留 `Log\` 和 `DeviceData\`**
- ✅ 运行时垃圾本来就不会进包：`Log\`、六个 json 在源码目录里也不会被拷进 publish（SDK 把它们收成 `None` 项，默认不复制）

### 12.10 交付前验证清单

```powershell
$dir = "Release\DSH_Modbus采集工具_0.6.0"
Get-ChildItem $dir -Recurse -File            # ① 应为 4 个文件，无 pdb、无 Log
(Get-Item "$dir\DSH_Modbus采集工具.exe").VersionInfo.ProductVersion   # ② 应为干净的 0.6.0+<sha>
Get-Content "$dir\DSH_Modbus采集工具.runtimeconfig.json" | Select-String "Microsoft\."  # ③ 必须含 WindowsDesktop
Start-Process "$dir\DSH_Modbus采集工具.exe"; Start-Sleep 6
Get-Process "DSH_Modbus采集工具" | Select-Object MainWindowTitle      # ④ 窗口应出现
```

然后在**目标机**上：`Test-Path` 运行时 → 双击能开 → 改主题、关掉、重开 → **主题还在**（这一条同时验证目录可写）。
自检完把 `Log\` 和 `app-settings.json` 删掉，包才是干净的出厂状态。
