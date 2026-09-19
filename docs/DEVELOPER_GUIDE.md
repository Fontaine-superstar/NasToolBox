# NAS 工具箱(NasToolbox)开发者文档

> 面向要**修改或扩展本项目**的开发者。读完本文应当能:独立跑起工程、说清每个模块的职责边界、按正确的分层新增功能、知道哪里有坑。
> 配套文档:`docs/AI_CONTEXT.md`(面向 AI 代理的密集事实档,可直接投放给编码助手作为上下文)。
> 版本:`v0.2.0` / .NET 10 / WinUI 3(Windows App SDK 2.2.0)。

---

## 目录

1. [项目定位与功能现状](#1-项目定位与功能现状)
2. [技术栈与工程配置](#2-技术栈与工程配置)
3. [架构总览](#3-架构总览)
4. [目录结构](#4-目录结构)
5. [运行时骨架:启动、导航与搜索](#5-运行时骨架启动导航与搜索)
6. [数据层:模型与持久化](#6-数据层模型与持久化)
7. [设备探测链路](#7-设备探测链路)
8. [SSH 子系统](#8-ssh-子系统)
9. [NAS 状态采集](#9-nas-状态采集)
10. [Docker 管理](#10-docker-管理)
11. [局域网测速](#11-局域网测速)
12. [安全模型](#12-安全模型)
13. [线程与并发模型](#13-线程与并发模型)
14. [构建、运行与发布](#14-构建运行与发布)
15. [扩展指南](#15-扩展指南)
16. [已知问题与技术债](#16-已知问题与技术债)
17. [排障手册](#17-排障手册)

---

## 1. 项目定位与功能现状

「NAS 工具箱」是一个**家用 NAS 的日常运维面板**。设计取向很明确:

- **零服务端依赖**:所有网络能力尽量用 .NET BCL 实现(Ping / TCP / UDP 广播 / DNS / DriveInfo),唯一第三方网络库是 SSH.NET。
- **免安装**:非打包(Unpackaged)形态 + 自带 Windows App SDK 运行时,解压即用。
- **面向真实家庭网络**:NAS 常年禁 Ping、只开 Web 端口、SMB 匿名枚举被禁、SSH 需要提权——这些都是默认路径要考虑的情况。

### 功能现状(以代码为准)

| 页面 | 实际已实现 |
|---|---|
| 首页 | 本机主机名 / IPv4(自动剔除虚拟网卡,192 网段优先)/ 网关;WMI 取 CPU / 内存 / 系统 / 开机时长;NAS 设备在线速览 + 「检测全部」 |
| 设备管理 | 设备增删改、显示名去重、当前管理设备勾选、一键全量检测、清空 SSH 连接缓存、SSH 三段式「测试连接」(含指纹确认)、Web 管理端口(必填,NumberBox 1–65535) |
| NAS 状态 | SSH 采集:系统类型(群晖/威联通/UnRAID/TrueNAS/OpenWrt/通用)、主机名、在线时长(走秒)、负载、CPU 核心、温度、内存(进度条)、网关、DNS、网卡列表(含实时上下行速率,192 网段优先排序)、磁盘挂载点(「存储空间N」友好命名);长列表 3 行折叠 |
| Web 管理 | 左侧设备侧边栏(沿用上次检测的在线状态,可一键「检测」)+ 右侧 WebView2 内嵌各 NAS 的 Web 管理界面;支持后退 / 前进 / 刷新 / 在系统浏览器打开(无地址栏,导航统一走设备列表) |
| 网络诊断 | Ping 报告(丢包/最短/最长/平均);基于 Docker 的局域网测速(自动发现 speedtest-x 容器、自动启动、解析宿主端口、内嵌隐藏 WebView2 取数) |
| 磁盘与共享 | 本机磁盘空间进度条;`NetShareEnum` 枚举远程 SMB 共享、隐藏共享开关、单击打开 UNC |
| Docker | 五个分区:概览、容器(启停/重启/删除/日志)、Compose(ls / start / stop / restart / down / 部署)、镜像(NAS 端 + 本机 `img\*.tar` 上传导入、删除、清理悬空)、网络(列表/删除) |
| 工具库 | 扫描 `Tools/<中文分类>/`,图标提取缓存,`tools.json` 覆盖中文名与描述,点击启动 |
| 关于 | 静态说明 |

### ⚠️ 与 README.md 的差异

`README.md` 停留在更早的设计,以下能力**已经不存在于代码中**,请勿据其判断功能:

- 设备行的「一键 Ping / 打开 Web 管理 / WOL 唤醒 / 打开 SMB 共享」按钮(现在设备行只有「编辑」「删除」);
- 「SSH 终端(Windows Terminal / OpenSSH)」「SSH 状态一键体检」「SSH 自定义命令执行」;
- 网络诊断页的「常用 NAS 端口探测(10 个)」与「DNS 解析」UI(相应常量 `CommonPorts` 全仓不存在);
- README 里的 `cd MyToolbox` 路径与 `%LocalAppData%\NasToolbox\IconCache` 缓存路径。

保留但**暂时未被调用**的基础设施:`SshService.StatusScript`、`WakeOnLanService.SendAsync`、`NetworkService.ResolveReportAsync`、`SpeedtestService.TestAsync`。详见[第 16 节](#16-已知问题与技术债)。

---

## 2. 技术栈与工程配置

### 关键属性(`NasToolbox.csproj`)

```xml
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
<Platforms>x86;x64;ARM64</Platforms>
<RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">
  win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())
</RuntimeIdentifier>
<UseWinUI>true</UseWinUI>
<EnableMsixTooling>false</EnableMsixTooling>
<WindowsPackageType>None</WindowsPackageType>          <!-- 非打包形态 -->
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<Version>0.2.0</Version>
<PublishTrimmed>false</PublishTrimmed>                 <!-- 永不裁剪 -->
```

`RuntimeIdentifier` 的自动推导是为了让 `dotnet run` / `dotnet publish` 在不显式给 `-r` 时也能工作;WinUI3 工程缺 RID 会在发布时报 `NETSDK1129`。

### 依赖

| 包 | 版本 | 用途 |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.2.0 | WinUI 3、`TitleBar` 控件、`AppWindow`、WebView2 宿主 |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.7705 | 构建工具链 |
| `SSH.NET` | 2026.0.0 | SSH / SFTP。**注意包 ID**:旧的 `Renci.SshNet` 停在 1.0.0,只支持 OpenSSH 8.2 之前的老算法,现代 NAS 会在握手阶段直接断开。2026.0.0 修复 CVE-2026-85756 / CVE-2026-48798(均只影响未使用的 ScpClient) |
| `System.Management` | 10.0.8 | 首页 WMI 查询 |
| `Microsoft.WindowsDesktop.App.WindowsForms`(FrameworkReference) | — | `System.Drawing` 提图标,以及借 `OpenFileDialog`(非打包 WinUI 下最省事的文件选择框) |

> `NetworkToolsPage` 里的 `Microsoft.Web.WebView2.Core` 由 Windows App SDK 间接提供,**不需要**单独引 `Microsoft.Web.WebView2`。

### 随包内容

```xml
<Content Include="Tools\**\*"    CopyToOutputDirectory="PreserveNewest" ExcludeFromSingleFile="true" />
<Content Include="Metadata\**\*" CopyToOutputDirectory="PreserveNewest" />
<Content Include="img\**\*"      CopyToOutputDirectory="PreserveNewest" ExcludeFromSingleFile="true" />
```

### 其他工程文件

- `global.json`:SDK `10.0.302`,`rollForward: latestFeature`。
- `app.manifest`:`asInvoker`;`PerMonitorV2` DPI;声明支持 Windows 10/11。
- 无解决方案文件、无测试工程、无 CI。

---

## 3. 架构总览

整体是**「View(Page) → Service(静态无状态) → 基础能力(.NET BCL / SSH.NET / Win32)」**的三层结构,没有 DI 容器、没有 MVVM 框架,状态靠静态类与「单槽缓存」承载。

```
┌──────────────────────────────────────────────────────────────────────┐
│  App.xaml.cs                                                          │
│    └─ MainWindow (48px TitleBar + NavigationView + Frame)             │
└───────────────────────────┬──────────────────────────────────────────┘
                            │ Frame.Navigate(page)
        ┌───────────────────┴────────────────────────────────────────┐
        │  Pages/  首页 · 设备管理 · NAS状态 · 网络诊断 · 磁盘与共享   │
        │          Docker · 工具库 · 关于                             │
        │  ── 只做:UI 组装 / 表单校验 / 结果渲染 / 错误文案展示        │
        └───────────────────┬────────────────────────────────────────┘
                            │ 调用静态 Service(除 x:Bind 外无数据绑定层)
        ┌───────────────────┴────────────────────────────────────────┐
        │  Services/  业务编排层                                      │
        │  ┌────────────────┬────────────────┬─────────────────────┐ │
        │  │ 数据与状态      │ 网络与探测      │ 远端管理             │ │
        │  │ NasDeviceStore │ DeviceProbe    │ SshService  ────┐   │ │
        │  │ DeviceStatus   │ NetworkService │ DockerService   │   │ │
        │  │ SshHostKeyStore│ SmbService     │ SpeedtestService│   │ │
        │  │ SecretProtector│ DiskService    │ SftpService     │   │ │
        │  │ NasStatus      │ WakeOnLan      │                 │   │ │
        │  └────────────────┴────────────────┴─────────────────┘   │ │
        │  Services/Ssh/  SshService · SshClientFactory ·           │ │
        │                 SshConnectionCache · SshRootShell ·        │ │
        │                 SshHostKeyStore · SshConnectReport ·       │ │
        │                 SshCommandResult · NasOsInfo               │ │
        └───────────────────┬───────────────────────────────────────┘
                            │
   ┌────────────────────────┴─────────────────────────────────────────┐
   │  平台能力层                                                        │
   │  .NET BCL: Ping · TcpClient · UdpClient · Dns · DriveInfo          │
   │  SSH.NET:  SSH exec / ShellStream / SFTP                           │
   │  Win32:    Netapi32.NetShareEnum · shell32.SHGetFileInfoW          │
   │  加密:     ProtectedData(DPAPI, CurrentUser)                      │
   │  OS:       System.Management(WMI) · WebView2                       │
   └──────────────────────────────────────────────────────────────────┘
```

### 设计约定(重要)

1. **Service 全是 `static class`**,不持 UI 引用,因此可安全地在后台线程调用。**唯一的例外**是 `ToolCatalog.Tools` 会构造 `BitmapImage`(必须在 UI 线程首次访问)。
2. **Service 不吞掉「用户需要知道」的错误**:建连失败抛 `SshConnectException`(带分类),命令级失败通过 `SshCommandResult.ErrorMessage` 返回;页面负责把两者都翻译成中文文案。
3. **页面不直连平台 API**:除 `Process.Start`(打开资源管理器 / 浏览器)与 `File.Delete`(删本机镜像 tar)等纯本地动作外,一律经 Service。
4. **没有全局状态容器**:`NasStatusService.Cached`、`SpeedtestService.LastTest` 是刻意的「单槽缓存」,用于「切页面回来不白屏」。
5. **XAML 侧用 `x:Bind` 函数绑定**而非 `IValueConverter`(`Converters/Conv.cs`),绑定只出现在 `ListView` / `GridView` 的 `DataTemplate` 里。

---

## 4. 目录结构

```
NasToolBox/
├── App.xaml(.cs)            应用入口,静态 MainWin
├── MainWindow.xaml(.cs)     标题栏 + NavigationView + Frame;统一搜索
├── NasToolbox.csproj        工程定义
├── global.json              SDK 版本锚定
├── app.manifest             asInvoker / PerMonitorV2
├── README.md                ⚠️ 已过时(见 1.2)
├── Pages/                   9 个页面(XAML + code-behind)
│   ├── DashboardPage        首页
│   ├── DevicesPage          设备管理(含设备编辑对话框)
│   ├── NasStatusPage        NAS 状态
│   ├── WebAdminPage         Web 管理(设备侧边栏 + WebView2)
│   ├── NetworkToolsPage     网络诊断
│   ├── DiskSharePage        磁盘与共享
│   ├── DockerPage           Docker 管理(含 3 个对话框)
│   ├── AllToolsPage         工具库
│   └── AboutPage            关于
├── Models/                  13 个 POCO / 记录类型(含视图模型 DeviceRow)
├── Services/                16 个顶层服务 + Ssh/ 子目录 9 个文件
│   └── Ssh/                 SSH 子系统
├── Converters/Conv.cs       x:Bind 静态函数绑定
├── Metadata/tools.json      工具元数据
├── Tools/<中文分类>/         第三方绿色工具(随包复制)
├── img/                     建议镜像 tar(随包复制)
└── docs/                    本文档与 AI 上下文文档
```

---

## 5. 运行时骨架:启动、导航与搜索

### 启动

```csharp
// App.xaml.cs
protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    MainWin = new MainWindow();
    MainWin.Activate();
}
```

```csharp
// MainWindow 构造函数(顺序有含义)
InitializeComponent();
Instance = this;
SafeTitleBar.ApplyExtendedTall(this, AppTitleBar);   // 必须早于 Activate
WindowLimits.ApplyMinSize(this, 850, 700);            // 最小窗口 850×700(经 AppWindow.Changed 顶回)
_ = DeviceProbeService.ProbeAndPersistAsync();        // 后台:全设备在线检测 + 落盘
NasStatusService.PrefetchOnStartup();                 // 后台:预取当前设备状态快照
```

两处后台预取的意义是:用户点开「设备管理」或「NAS 状态」时**先渲染上次/已取到的数据,再静默刷新**,避免空白等待。代价是启动瞬间对当前设备有一次重复探测,以及「单轮制」的探测可能互相取消——都属于可接受的设计取舍。

### 标题栏(`Services/SafeTitleBar.cs`)

WinUI3 默认会出现「系统标题栏 + 自绘标题栏」两条栏。`SafeTitleBar.ApplyExtendedTall` 用四步把它们合成一条:

1. `Window.ExtendsContentIntoTitleBar = true` —— XAML 内容延伸进系统标题栏;
2. `AppWindow.TitleBar.PreferredHeightOption = Tall` —— 系统栏升高到 48px,与 `TitleBar` 控件对齐;
3. `Window.SetTitleBar(titleBar)` —— 指定拖拽区(控件内的搜索框等交互元素仍可点);
4. 系统 -□× 按钮背景透明、前景按明暗主题着色,并挂 `ActualThemeChanged` 跟随切换。

**三层 try/catch 是刻意的**:旧版 Win10 上 `AppWindow.TitleBar` 可能为 `null`,`Tall` 也可能不支持,任何失败都**静默退回系统默认样式而不是崩溃**。改这里请保留这个特性。

### 导航

`NavigationViewItem.Tag` 是页面标识,`NavView_SelectionChanged` 里的 `switch` 是 Tag → `Page` 类型的唯一映射点:

| Tag | 页面 | 缓存 |
|---|---|---|
| `home` | `DashboardPage` | 否 |
| `devices` | `DevicesPage` | `NavigationCacheMode=Enabled` |
| `status` | `NasStatusPage` | 否 |
| `web` | `WebAdminPage` | `NavigationCacheMode=Enabled` |
| `network` | `NetworkToolsPage` | 否 |
| `disk` | `DiskSharePage` | 否 |
| `docker` | `DockerPage` | `NavigationCacheMode=Enabled` |
| `tools` | `AllToolsPage` | `NavigationCacheMode=Enabled` |
| `about` | `AboutPage` | 否 |

两个容易踩的点:

- **跨页跳转必须走 `MainWindow.Instance?.NavigateByTag(tag)`**(内部改 `NavView.SelectedItem`,触发 `SelectionChanged` 完成导航)。直接 `Frame.Navigate` 会让侧栏高亮与实际页面不一致。
- **开了 `NavigationCacheMode=Enabled` 的页面,`Loaded` 只触发一次**。所以 `DevicesPage` / `AllToolsPage` / `DockerPage` / `WebAdminPage` 都在 `OnNavigatedTo` 里判断 `if (IsLoaded) Reload()` 来手动刷新。

另有一个小细节:`NavView_ItemInvoked` 会在用户**重复点击已选中的「设备管理」**时调用 `page.RefreshFromNav()` 立即刷一次在线状态——因为这种情况下 `SelectionChanged` 不会再触发。

### 顶栏统一搜索

`AutoSuggestBox` 同时检索设备台账与工具库,结果用中文前缀区分类型:

```csharp
private const string DevPrefix  = "设备 · ";
private const string ToolPrefix = "工具 · ";
```

- 输入 → 设备最多 4 条(匹配 `DisplayName` / `Host` / `Hostname` / `Note`)+ 工具最多 6 条(匹配 `Name` / `Category` / `Description`);
- 回车 → 按前缀判定目标页,把「去掉前缀的查询串」作为导航参数传给页面;
- 页面侧参数是**一次性**的(`OnNavigatedTo` 取用后立即置 `null`),避免下次进入还带着旧过滤。

---

## 6. 数据层:模型与持久化

### 持久化位置

| 文件 | 结构 | 说明 |
|---|---|---|
| `%LocalAppData%\NasToolbox\devices.json` | `List<NasDevice>` | 设备台账,含 DPAPI 密文 |
| `%LocalAppData%\NasToolbox\device_status.json` | `Dictionary<host, DeviceStatus>` | 在线状态上次检测结果 |
| `%LocalAppData%\NasToolbox\known_hosts.json` | `Dictionary<"host:port", "SHA256:...">` | 简化版 known_hosts |
| `%LocalAppData%\MyToolbox\IconCache\*.png` | — | 工具图标缓存(`ToolIconService` 里的历史路径) |

所有 Store 的共同约定:**文件损坏/不存在时返回空集合,不抛异常**;JSON 用 `WriteIndented=true` + `UnsafeRelaxedJsonEscaping`(中文原样输出)。

### `NasDevice`(核心模型)

字段分组看更清楚:

| 组 | 字段 |
|---|---|
| 基本信息 | `Name`、`Host`、`Hostname`、`Mac`、`WebPort`(必填)、`Note` |
| SSH | `SshPort`、`SshUser`、`SshAuth`、`SshPassEnc`、`SshKeyPath`、`SshKeyPassEnc`、`SshHostFingerprint` |
| 权限 | `UseSudo`、`RootLogin`、`SudoPassEnc`、`SshTimeoutSec` |
| 状态 | `IsCurrent` |

**显示名机制**(很容易看错):

```
Hostname 非空 ─┐
              ├─→ NameBase ─→ 同名去重(第 2 台起加 " (2)") ─→ DisplayName(界面显示)
Host 非空 ────┘
```

- `Name` 在保存时被写成 `Hostname ?? Host`,但它**不参与显示**;`DisplayName` 是加载时重算的、`[JsonIgnore]` 不落盘。
- 结论:**要改设备在界面上的名字,就改 `Hostname`**(留空时系统会在首次 SSH 连接成功后自动回填真实主机名)。
- `Hostname` 为空时 `NameBase` 回退 `Host`,所以刚添加的设备显示 IP,连上一次后自动变成主机名——这是刻意的体验设计。

### 三个 Store 的 API 语义

```csharp
NasDeviceStore.Load()                      // 每次读盘 + 重算 DisplayName(无内存单例)
NasDeviceStore.Save(list)                  // 全量覆盖写
NasDeviceStore.GetCurrent(list)            // 未勾选 → null
NasDeviceStore.GetCurrentOrDefault(list)   // 未勾选 → 第一台(功能页永远有目标)
NasDeviceStore.SetCurrent(list, target)    // 全表重置后落盘
NasDeviceStore.UpdateDevice(updated)       // 按 Host + Name 定位并替换(用于 Hostname 回填)

DeviceStatusStore.Get(host) / Set(host, s) // 内存操作
DeviceStatusStore.Save()                   // 显式落盘
```

`GetCurrentOrDefault` 是「功能页默认设备」的统一入口:Docker / NAS 状态 / 测速 / SMB 都先取它。反过来,Docker 页与 NAS 状态页在用户切换设备下拉框时,会**主动把选中设备写成当前管理设备**(`SetCurrent`),让各个页面保持一致。

### `DeviceRow`(唯一带通知的视图模型)

`DeviceRow` 包装 `NasDevice` 并承载实时连通状态(`StatusText` / `StatusBrush`,配合 `SetChecking` / `SetOnline` / `SetOffline`),实现 `INotifyPropertyChanged`,`IsCurrent` 通过 `NotifyCurrent()` 手动通知。

XAML 侧有个硬性要求:**`x:Bind` 默认是 `OneTime`,状态类绑定必须显式写 `Mode=OneWay`**,否则状态永远停在首次渲染的值。

---

## 7. 设备探测链路

`Services/DeviceProbeService.cs` 解决一个很实际的问题:**NAS 常常禁 Ping,但服务是活的**。

```
ProbeAsync(device)
 ├─ 1. ICMP Ping(内置 1500ms)          → 成功即返回 "192.168.1.10 · 3 ms"
 └─ 2. Ping 失败 → 依次 TCP 探测(每条 1200ms)
       ├─ SSH  (device.SshPort,默认 22)  → "SSH 22 可达 · 19 ms"
       ├─ WebPort(WebOrDefault 的端口) → "Web 5000 可达 · 21 ms"
       └─ SMB  (445)                     → "SMB 445 可达 · 18 ms"
    全失败 → "Ping 与常用端口均无响应"
```

三个公开入口:

| 方法 | 用途 | 特点 |
|---|---|---|
| `ProbeAllAsync(rows, markChecking)` | 页面上「检测全部」/自动刷新 | `markChecking: true` 先把行置「检测中…」给即时反馈;结果逐行写入 `DeviceStatusStore`,结束时统一 `Save()` |
| `ProbeAndPersistAsync()` | 启动时无页面依赖的预检 | 直接按台账检测并落盘 |
| `ApplyCached(row)` | 打开页面先渲染上次结果 | 无记录返回 `false`,保持「未检测」 |

**单轮制并发控制**:`NewRound()` 会先 `Cancel` 上一轮。因此启动预检可能被「打开设备页触发的自动检测」取消——这是有意的:用户主动操作优先。

---

## 8. SSH 子系统

这是全项目最复杂、也最值得先读懂的部分。设计目标:让上层用一行 `await SshService.RunAsync(device, "df -h")` 就能拿到结果,**不用关心连接复用、提权、超时、指纹**。

### 8.1 分层与数据流

```
业务层(DockerService / NasStatusService / SpeedtestService / 页面)
        │  RunAsync(device, cmd, timeoutMs?, sudo?, ct?)
        ▼
SshService ──► Validate(device)              配置不全 → 直接产出带 ErrorMessage 的结果
        │
        ├─► SshConnectionCache.RentAsync(SshCacheKey, createFactory, perCommandMs+20000, ct)
        │        ├─ Slot.Gate.WaitAsync  → 同设备串行
        │        ├─ 复用活连接,否则 SshClientFactory.Create(TrustNewHostKey: true)
        │        └─ 返回 Lease(Dispose=归还, Abandon=销毁)
        │
        ├─► device.RootLogin ? 首次建 SshRootShell(sudo -i) → 之后全部走会话
        │                    : 走 exec 通道(BuildCommand 决定是否套 sudo)
        │
        └─► TryFillHostnameAsync   ← 顺带把设备的主机名补全并落盘
```

### 8.2 连接复用(`SshConnectionCache`)

- 键 = `NasDevice.SshCacheKey` = `{user}@{host}:{port}|{auth}|{keyPath}`。**改了用户名 / 端口 / 认证方式 / 私钥路径就等于换了连接**,旧连接不会命中(但也需 `SshService.Forget` 主动释放)。
- 每个键一个 `Slot`(`SemaphoreSlim(1,1)` + `Current` entry)。**SSH.NET 的 `SshClient` 不是线程安全的**,所以同设备命令严格串行;不同设备之间可以并行。
- `Lease.Dispose()` 归还:若连接已断或被打上 `Broken`,顺手销毁,下次自动重连。
- 空闲 5 分钟的连接由每 2 分钟一次的定时器回收;`Forget(key)` / `ForgetAll()` 用于凭据变更、删除设备、退出应用。
- 保活:`KeepAliveInterval = 30s`,防止长时间空闲被 NAS 或路由器断链。

### 8.3 超时与错误

| 场景 | 表现 |
|---|---|
| 配置缺失(无 Host / 用户名 / 密码 / 私钥文件) | 所有请求的命令都返回 `ErrorMessage = "未配置..."`,不建连 |
| 建连失败 | 抛 `SshConnectException`,`Failure` 是 `SshConnectFailure` 分类 |
| 命令超时 | 结果 `TimedOut = true`;**当前连接被 Abandon**(远端进程可能还在跑,连接不可复用) |
| 连接中途断开 | 后续命令填 `ErrorMessage = "连接已断开,后续命令未执行。"`,`RunManyAsync` 仍返回与输入等长的结果列表 |
| 等待连接空闲超时 | `TimeoutException` |

**`RunManyAsync` 的返回值与输入严格同序等长**,`NasStatusService` 依赖这一点用固定下标取值——这是修改采集命令时必须守住的契约。

### 8.4 提权三种形态

| 模式 | 实现 | 用于 |
|---|---|---|
| 普通用户 | 直接执行 | 只读命令 |
| sudo 提权 | `printf '%s\n' <pass> \| sudo -S -p '' sh -c <cmd>`;无密码则 `sudo -n sh -c <cmd>` | 少量需要 root 的命令(smartctl / docker) |
| root 会话 | 连接后 `sudo -i` 进入交互式 root shell,后续命令都在其中执行 | 大批需要 root 的命令,只输一次密码 |

实现细节:

- `BuildCommand` 用 `ShellQuote`(单引号包裹 + `'` 转义为 `'\''`)包住整条命令,sudo 密码经 stdin 传入,**不落在命令行里**。
- `RootLogin == true` 时**强制不加 sudo**(会话里已经是 root);`SshRootShell` 创建后会执行 `id -u` 复核是否真的变成 0,失败即明确报错而不是继续跑。

### 8.5 root 会话(`SshRootShell`)的实现要点

交互式 shell 与 exec 通道完全不同,这里是踩坑最多的地方:

1. **必须按原始字节流匹配,不能按行读**。`sudo -i` 的密码提示和 shell 提示符都不以换行结尾,`ReadLine` 永远拼不出完整行——结果是提示符探测不到,密码被 sudo 当密码重试吃掉,最终表现为莫名其妙的超时。
2. **先看到 `sudo -i` 的回显,再认提示符**(`gateOnEcho`)。否则登录 banner 里残留的旧用户提示符(如 `admin@nas:~$`)会被误判为「已经提权」。
3. **识别三类结果**:密码提示(`password` / `密码`)、已提权(`#` / `$` / `~` 结尾)、明确拒绝(`try again` / `incorrect password` / `not in the sudoers` / `may not run sudo` → 抛中文原因)。判定顺序上**拒绝优先**(拒绝语里也含 "password")。
4. **退出码靠 marker 拿**:命令与 `echo $M1$M2$?` 分两行写,marker 被拆成两段拼接,避免命令回显行里出现完整 marker 造成误判。读输出时剥掉 ANSI 转义、按「去空白前缀比对」整段删除被终端折行的回显碎片、并剥离首行可能残留的提示符。
5. **建会话后 `stty -echo; PS1=''`**,让后续输出里不再混入回显与提示符。
6. 终端尺寸给到 500×200 并配 16KB 缓冲——太窄会把长命令按列折行,碎片污染输出。

### 8.6 主机指纹

- 存储:`SshHostKeyStore`,格式 `SHA256:<base64 去 '='>`,与 OpenSSH 显示风格一致。
- 回调挂在 `SshClient` / `SftpClient` 的 `HostKeyReceived` 事件上,**必须在 `Connect()` 之前挂载**。
- 三种状态:已知且一致 → 信任;首次(无记录)→ 按策略决定;记录不一致 → **拒绝连接**并标记 `HostKeyChanged`。
- 迁移兼容:若设备自身的 `SshHostFingerprint` 与本次握手结果一致,也算已信任并补写指纹库。

> ⚠️ 安全提示:目前只有「测试连接」路径会**询问**用户(`trustNewHostKey: false`);而 `RunAsync` 与 `SftpService` 传入的是 `TrustNewHostKey: true`,即**静默接受未知主机密钥且不记录**。如果你要收紧安全策略,这里是主要改造点(见[第 16 节](#16-已知问题与技术债))。

### 8.7 SFTP

`SftpService` 刻意**不复用连接缓存**,每次操作独立建连:

> 文件传输是长任务,握手开销可忽略。

- 上传 / 下载用 64KB 缓冲 + `IProgress<double>` 进度(0–1)。**进度回调在后台线程,更新 UI 需自行切回**。
- 目录删除:SFTP 只能删空目录;`recursive` 参数当前两条分支都调 `DeleteDirectory`,非空目录会由服务端报错。

---

## 9. NAS 状态采集

`Services/NasStatusService.cs` 是本项目「采集 + 解析」最重的模块。

### 采集方式

一次采集 = **一次 `RunManyAsync`(11 条只读命令,同一条 SSH 连接)**,命令数组本身就是解析下标契约:

| 下标 | 命令 | 采集内容 |
|---|---|---|
| 0 | `uptime` | 在线时长文本 + 负载均值 |
| 1 | `nproc \|\| grep -c ^processor /proc/cpuinfo` | CPU 核心数 |
| 2 | `grep -E 'MemTotal\|MemAvailable\|MemFree' /proc/meminfo` | 内存 |
| 3 | `df -hP -x tmpfs -x devtmpfs -x overlay \|\| df -hP \|\| df -h` | 磁盘 |
| 4 | `cat /sys/class/thermal/thermal_zone0/temp \|\| echo -1` | 温度(毫摄氏度) |
| 5 | `cat /proc/uptime` | 开机总秒数(走秒基准) |
| 6 | `for n in /sys/class/net/*; ... echo "IF1\|..."` | 网卡第一次采样 |
| 7 | `sleep 1` | **两次采样间隔** |
| 8 | 同上 `echo "IF2\|..."` | 网卡第二次采样 → 实时速率 |
| 9 | `ip -o -4 addr show` | IPv4 |
| 10 | `ip route show default \| head -1` | 网关 |
| 11 | `grep nameserver /etc/resolv.conf` | DNS |

因为数组里有 `sleep 1`,一次状态采集**至少占用设备连接 1 秒以上**;而 `Gate` 是全局单锁(不是按设备加锁),所以多设备并发采集会互相排队。要并发采集多台设备,需要把 `Gate` 改成按设备的 `SemaphoreSlim`。

### 解析器一览(各自都能容忍真实世界的脏输出)

| 方法 | 处理的问题 |
|---|---|
| `ParseUptimeSec` | 用「带小数点的独立数字」优先匹配,防止提示符残留里的主机名(如 `nas2`)被当成秒数 |
| `ParseUptime` | 完整版 `uptime` 的 `up` 段以 `, N users` 结束;busybox 版没有该段,取第一段 |
| `MemKb` | 正则允许行内任意位置命中,容忍提示符残留前缀;`MemAvailable` 取不到回退 `MemFree` |
| `ParseDf` | 以「第 5 字段以 `%` 结尾」判定有效行;过滤 `tmpfs`/`devtmpfs`/`overlay`/`none`/含 `loop`;过滤 `/proc` `/sys` `/dev` `/run` 挂载点;`vol1`/`volume1` → 「存储空间1」并排最前 |
| `ParseNetwork` | 解析 `IF1`/`IF2` 两行管道分隔数据算速率;`ip -o -4 addr` 取 IPv4;`via` 取网关;`nameserver` 取 DNS;跳过 `lo` |
| `ParseOsInfo` | 首个不含 `=` 的行当内核串;按特征文件判定品牌(群晖 → 威联通 → UnRAID → TrueNAS → OpenWrt → Linux → Unix) |

### 快照与缓存

`NasStatusSnapshot` 承载一次采集的全部指标 + `DeviceKey` + `FetchedAt` + `Error`。

- `FetchAsync` 用 `Gate` 串行;成功才写入 `_cached`(失败不覆盖缓存,页面保留上次显示)。
- 失败不抛异常,以 `Error` 字段返回,失败原因带 `HintFor(failure)` 的修复建议。
- 页面渲染策略:先看 `Cached` 且 `DeviceKey` 与当前设备匹配 → 立即渲染并提示「已显示 xx:xx:xx 的数据,正在后台刷新」;然后 `await FetchAsync` 覆盖。
- **在线时长走秒**:`NasStatusPage` 用 `DispatcherTimer`(1 秒)以「快照的 `UptimeSec` + `FetchedAt`」为基准推进显示,所以即使看到的是缓存,秒数也是连续不减的。

---

## 10. Docker 管理

`Services/DockerService.cs` 把远端 docker 命令封装成强类型方法,**所有命令都经 `SshService.RunAsync`**,因此自动继承设备配置的提权模式与超时策略。

### 命令与超时

| 方法 | 远端命令 | 超时 |
|---|---|---|
| `ListAsync` | `docker ps -a --format '{{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'` | 25s |
| `ActAsync` | `docker start/stop/restart/rm -f <name>` | 40s(停止/删除 60s) |
| `LogsAsync` | `docker logs --tail N <name>` | 30s |
| `CreateAsync` | 可选 `docker pull`(600s)+ `docker run -d --name --restart -p ...`(60s) | — |
| `InfoAsync` | `docker info --format ...` + `docker network ls \| wc -l` | 30s / 20s |
| `ListImagesAsync` | `docker images --format '{{.Repository}}:{{.Tag}}\t{{.ID}}\t{{.Size}}\t{{.CreatedSince}}'` | 30s |
| `RemoveImageAsync` / `PruneImagesAsync` | `docker rmi` / `docker image prune -f` | 120s / 180s |
| `ListNetworksAsync` / `RemoveNetworkAsync` | `docker network ls` / `rm` | 20s / 60s |
| `ComposePrefixAsync` | 探测 `docker compose` 优先,回退 `docker-compose` | 20s |
| `ListComposeAsync` | `<prefix> ls -a --format json` | 30s |
| `ComposeActionAsync` | `<prefix> -p <project> start/stop/restart/down` | 120s(down 300s) |
| `ComposeUpAsync` | 可选 SFTP 上传 → `<prefix> -f <file> up -d` | 600s |
| `LoadLocalImageAsync` | SFTP 上传到 `/tmp/nastb_img_*` → `docker load -i` → 清理临时文件 | 600s |

### 设计细节

- **用 `\t` 而不是逗号分隔**:`docker ps` 的 `Ports` 字段里含逗号(`0.0.0.0:8800->80/tcp, :::8800->80/tcp`),逗号分隔会解析错。
- **`Explain()` 把 docker 报错翻译成可执行建议**:`command not found` → 未安装/不在 PATH;`permission denied` → 提示去「编辑 → 权限与高级」选 sudo 提权或 root 会话;`Cannot connect to the Docker daemon` → 守护进程未运行。
- **`Truncate()` 截断到 400 字符**,避免 `InfoBar` 被刷屏。
- **`docker logs` 的正常输出也走 stderr**,读取时必须 stdout + stderr 合并。
- **容器名一律 `Quote()`**(单引号 + 转义),防止特殊字符被 shell 解析。
- **本机镜像导入**:`img\` 目录(位于 `AppContext.BaseDirectory`)下的 `*.tar`,SFTP 上传后用 `docker load`,并从输出里解析真实镜像引用(``Loaded image: repo:tag``)用于随后创建容器——因为 tar 重新导入后 `repo:tag` 可能与用户填的名字不一致。

### 页面结构(`DockerPage`)

左侧二级导航(`SectionNav`,默认选中「容器」)切换五个面板:`overview` / `containers` / `compose` / `images` / `networks`,`LoadCurrentSection()` 按当前分区分发加载。

值得注意的交互约定:**在 Docker 页或 NAS 状态页切换设备下拉框,会把该设备同步为「当前管理设备」**(写盘),这样用户下次在别的页面看到的默认设备是一致的。

---

## 11. 局域网测速

`Pages/NetworkToolsPage` 的测速流程(与行业惯例不同,值得单独说明):

```
用户点「开始测速」
 └─ SpeedtestService.FindAsync(device)        在 NAS 上 docker ps,按容器名/镜像名找 speedtest-x
     ├─ 没找到 → 提示后自动跳转 Docker 管理页(NavigateByTag("docker"))
     └─ 找到
         ├─ 未运行 → docker start
         ├─ ResolveHostPortAsync               优先用 docker ps 的 Ports 字段解析宿主端口,
         │                                     解析不到(刚启动时该字段为空)→ docker port <name> 80 补查
         └─ StartWebSpeedtest(url)
              └─ 隐藏的 WebView2 打开测速页
                   ├─ NavigationCompleted → 注入 StartJs 自动点开始
                   └─ DispatcherTimer 每秒 ExecuteScriptAsync(CollectJs) 采集页面显示数值
                        ├─ 三项数值齐备 且 (引擎结束 running 类消失 / 数值 5 秒不变) → 判定完成
                        └─ 结果写入 SpeedtestService.Remember,页面渲染
```

关键点:

- **数据源是网页端引擎的 DOM 数值**,不是自己实现协议。这样做的好处是「应用内结果 = 浏览器打开该地址测出来的结果」,不会因为自实现的算法差异被质疑。
- `StartJs` / `CollectJs` 同时兼容两个协议:`speedtest-x`/LibreSpeed(`startStop()` / `dlText` / `ulText` / `pingText` / `ip`)与 `OpenSpeedTest`(`startButtonDesk` / `downResult` / `upResult` / `pingResult` / `YourIP`)。
- 因为注入可能早于页面脚本就绪,轮询期间**每 5 秒补注一次**;超过 240 次(`_webTicks > 240`,约 4 分钟)视为超时并给出降级提示。
- `ParseNum` 会把页面上的 `G` / `K` 单位换算成 Mbps。
- `SpeedtestService.LastTest` 是单槽缓存,重新进入页面时若设备匹配则默认展示上次结果。

> `SpeedtestService.TestAsync` 及其私有实现(`DetectProtocolAsync` / `MeasureDownloadAsync` / `MeasureUploadAsync` / `GetIpInfoAsync`)是**早期自实现 HTTP 协议测速的遗留代码,当前无调用方**。保留原因见[第 16 节](#16-已知问题与技术债)。

---

## 12. 安全模型

| 资产 / 动作 | 现状 |
|---|---|
| SSH 密码、私钥口令、sudo 密码 | `SecretProtector` = DPAPI `DataProtectionScope.CurrentUser` + entropy `"NasToolbox.Ssh.v1"`,Base64 存盘。**密文与当前 Windows 用户 + 本机绑定**,拷贝 `devices.json` 到别处无法解密;解密失败静默返回空串(当作没密码) |
| 界面回显 | 所有 `PasswordBox` 编辑时一律**不回显**已保存口令,留空 = 保持不变;另有「清除已保存的密码」复选框,勾选保存后同时清空三个密文与该主机指纹记录 |
| 主机身份 | `known_hosts.json` 记录 SHA256 指纹;「测试连接」路径会在指纹首次出现/变更时阻塞并展示指纹供用户核对后手动信任 |
| 本地权限 | `app.manifest` 为 `asInvoker`(开发调试友好);工具库支持 `runas` 启动 |
| 远端权限 | 见 8.4,密码只经 stdin |
| 未覆盖 | ① 命令执行与 SFTP 通道对未知主机密钥静默接受且不记录;② `devices.json` 中主机/用户名/备注为明文;③ 无操作审计日志;④ 工具库启动第三方 exe 不做签名校验 |

**如果你要加固,优先级建议**:① 让 `RunAsync` 也走「指纹未信任则拒绝并提示」→ ② 为 `devices.json` 提供导出/导入时的凭据隔离说明 → ③ 增加可选的审计日志。

---

## 13. 线程与并发模型

| 机制 | 位置 | 说明 |
|---|---|---|
| per-key 串行 | `SshConnectionCache.Slot.Gate` | 同设备命令串行;不同设备并行 |
| 全局串行 | `NasStatusService.Gate` | 一次只处理一个状态采集请求(跨设备) |
| 单轮探测 | `DeviceProbeService._pending` | 新调用取消旧调用 |
| 后台线程边界 | `SftpService`、`Task.Run`(WMI、`Dns` 等) | 重活一律 `Task.Run` 包裹,避免阻塞 UI |
| 进度回调线程 | `IProgress<double>`(`SftpService` / `DockerService`) | **回调在后台线程**,直接改控件会崩 |
| 定时器 | `SshConnectionCache.Reaper` | 每 2 分钟回收空闲连接 |
| UI 定时器 | `NasStatusPage` 走秒、`NetworkToolsPage` 测速轮询 | 必须在 `Unloaded` 里 `Stop()`(两页都已处理) |
| 惰性单例 | `ToolCatalog._tools` | `??=` 扫描;**首次访问会创建 `BitmapImage`,应在 UI 线程触发** |

页面通用的异步写法是 `async void` 事件处理器 + `try/finally` 复位 `IsEnabled` / `ProgressRing`;`async void` 内的异常会被吞(未处理即崩溃),所以每个处理器都要自己 catch 并把错误写进 `InfoBar` 或状态行文案。

---

## 14. 构建、运行与发布

### 环境

- Windows 10 19041+ / Windows 11
- .NET 10 SDK(`winget install Microsoft.DotNet.SDK.10`),`global.json` 锚定 `10.0.302`

### 开发

```powershell
dotnet build                    # 编译(自动按当前进程架构选 RID)
dotnet run                      # 非打包模式直接运行
```

### 发布(便携版:解压即用,自带 WinAppSDK 运行时)

```powershell
dotnet publish -c Release -r win-x64   -o publish\x64
dotnet publish -c Release -r win-x86   -o publish\x86
dotnet publish -c Release -r win-arm64 -o publish\arm64
```

发布产物里必须包含 `Tools\`、`Metadata\`、`img\`(csproj 已配置为 `Content` + `PreserveNewest`)。

### 目录与文件名约定

- 源码文件用 `Pages/` `Models/` `Services/` 分层,一个类一个文件;`Services/Ssh/` 内聚 SSH 子系统。
- 命名:XAML 页面 `XxxPage.xaml` + `XxxPage.xaml.cs`;服务 `XxxService` / `XxxStore`;视图模型 `XxxRow`。
- 注释与界面文案统一中文;`///` 摘要用来说明「为什么这么做」而不是「做了什么」。

### 代码风格要点(现有代码的一致做法)

- 全是 `static class` + `async Task`,不引入 DI;
- 用 `??=` / `?.` / 模式匹配(`is not { } x`)等现代 C# 语法,`Nullable` 已开启;
- `catch` 必须带注释说明为什么可以忽略(如「损坏则当作空列表,不致命」);
- 对外暴露的中文文案要「说明原因 + 给出下一步」(参考 `SshService.HintFor`)。

---

## 15. 扩展指南

### 15.1 新增页面

1. `Pages/NewPage.xaml` + `NewPage.xaml.cs`(`sealed partial class NewPage : Page`);
2. `MainWindow.xaml` 的 `NavigationView.MenuItems` 加一项:`<NavigationViewItem Content="xx" Tag="newtag"><NavigationViewItem.Icon><FontIcon Glyph="&#xE700;"/></NavigationViewItem.Icon></NavigationViewItem>`;
3. `MainWindow.xaml.cs` 的 `NavView_SelectionChanged` 里 `switch` 加 `"newtag" => typeof(NewPage)`;
4. 若页面内的样式卡片(`SectionCard` 等)需要复用,**从 `DevicesPage.xaml` 复制资源定义**(目前是逐页定义的);
5. 若要支持顶栏搜索直达,可在 `MainWindow.SearchBox_QuerySubmitted` 增加类型前缀。

### 15.2 新增第三方工具

- 直接把 exe/bat/cmd/lnk/msc/ps1/vbs 放进 `Tools/<分类名>/`(支持子目录递归);扩展名白名单在 `ToolCatalog.ToolExts`;
- 需要正式名/描述:`Metadata/tools.json` 加一条,`match` 是**与完整路径**做大小写不敏感子串匹配:

```json
{ "match": "winscp", "name": "WinSCP", "description": "SFTP / SCP / FTP 图形化文件传输" }
```

- `ToolCatalog.Tools` 是惰性单例,**运行期新增工具需重启应用**(代码里有 `Rescan()`,但当前无 UI 入口——可考虑在工具库页加一个「重新扫描」按钮)。

### 15.3 修改设备表单字段(最容易漏的地方)

`NasDevice` 加字段后,以下 **5 处**都要同步,否则会出现「测试连接能过但保存丢字段」:

| # | 位置 | 作用 |
|---|---|---|
| 1 | `DevicesPage.xaml` | 表单控件(建议按「基本信息 / SSH 连接 / 权限与高级 / 其他」分区) |
| 2 | `EditBtn_Click` | 打开编辑时回填控件 |
| 3 | `AddBtn_Click` | 打开新增时给默认值 |
| 4 | `ValidateForm` | 校验 |
| 5 | `BuildDeviceFromForm` + `EditDialog_PrimaryButtonClick` | 构造 `NasDevice`(测试用 / 保存用),注意「留空 = 保持原值」的口令语义 |

新增的敏感字段请一律走 `SecretProtector.Protect` / `Unprotect`,并在 `ClearPassBox` 的分支里一并清空。

### 15.4 修改 NAS 状态采集

- 采集项在 `NasStatusService.Commands` 数组,**数组顺序即索引契约**;
- 新增项要同步 `FetchCoreAsync` 中的 `Out(metrics, n)`;
- 若动到网络相关下标,复核 `ParseNetwork` 的入参 `(Out(9), Out(10), Out(6), Out(8), Out(11))`;
- 解析器必须能容忍真实世界的脏输出(提示符残留、busybox 精简版的字段缺失、非 GNU 工具),新增解析建议照现有风格写好注释说明「为什么这样匹配」。

### 15.5 新增 Docker 操作

在 `DockerService` 加一个方法,遵循三步:

```csharp
var r = await SshService.RunAsync(device, $"docker xxx {SshService.ShellQuote(name)}", timeoutMs, null, ct);
if (r.ErrorMessage is not null) throw new InvalidOperationException(r.ErrorMessage);
if (r.TimedOut) throw new InvalidOperationException($"... 超时({timeoutMs / 1000} 秒)。");
if (!r.Success) throw new InvalidOperationException(Explain(r.Output));
```

参数校验、确认对话框(破坏性操作)在页面侧完成(参考 `RemoveImageBtn_Click` 的 `ContentDialog`);`--format` 里的 Go 模板占位符(双花括号)在 C# 字符串里原样书写即可,注意别被字符串插值(`$"..."`)吞掉。

### 15.6 支持新 NAS 品牌

`SshService.DetectScript` 加特征文件探测(`MARK_XXX`),`ParseOsInfo` 加判定分支,`NasKind` 加枚举值,`NasOsInfo.KindName` 加中文名。若该品牌命令体系特殊(如磁盘命名、`df` 输出格式不同),再在 `NasStatusService` 的解析器里加兼容分支。

---

## 16. 已知问题与技术债

按修复价值排序。

### 高

1. **命令执行与 SFTP 静默接受未知主机密钥**
   `SshService.RunCoreAsync` 与 `SftpService.ConnectAsync` 都传 `TrustNewHostKey: true`,即首次连接不提示、也不写入指纹库。指纹机制目前只在「测试连接」路径生效。
   *建议*:首次连接改为「拒绝 + 提示用户到设备页确认指纹」,或在设备页连接成功后主动 `SshHostKeyStore.Trust`。

2. **`README.md` 严重过时**
   描述了已不存在的功能(设备行按钮、SSH 终端、端口探测、DNS 解析 UI)、错误的目录名(`MyToolbox`)与错误的图标缓存路径。
   *建议*:以本文为准重写 README,或直接改为「见 `docs/`」。

3. **图标缓存路径不一致**
   `ToolIconService` 用 `%LocalAppData%\MyToolbox\IconCache`,而关于页与 README 均声称 `%LocalAppData%\NasToolbox\IconCache`。
   *建议*:统一到 `NasToolbox\IconCache`,并做一次旧目录迁移(或直接留旧目录、只改文案)。

### 中

4. **`NasStatusService.Gate` 是全局单锁**
   多设备并发采集互相排队,且一次采集含 `sleep 1` 至少占 1 秒。
   *建议*:改成 `ConcurrentDictionary<deviceKey, SemaphoreSlim(1,1)>`。

5. **启动重复探测**
   `ProbeAndPersistAsync` 与 `NasStatusService.FetchAsync` 都会探测当前设备。
   *建议*:让 `NasStatusService` 复用 `DeviceProbeService` 的结果,或把探测结果做成短期缓存。

6. **5 处死代码**
   `SshService.StatusScript`、`SpeedtestService.TestAsync`(+ `DetectProtocolAsync` / `MeasureDownloadAsync` / `MeasureUploadAsync` / `GetIpInfoAsync` / `SpeedtestProgress`)、`WakeOnLanService.SendAsync`、`NetworkService.ResolveReportAsync`、`Conv.OpenText` / `Conv.OpenBrush`。
   *判断*:`SendAsync` / `ResolveReportAsync` 是「UI 被砍但能力保留」;`SpeedtestService.TestAsync` 是「被 WebView2 方案取代」,同文件里留着两套测速实现容易误导维护者。
   *建议*:要么恢复对应 UI,要么删掉;`TestAsync` 保留则至少在 XML 注释里标注 `[未使用] 保留原因`。

7. **样式资源逐页重复**
   `SectionCard` / `SectionTitle` / `FieldHint` 在 `DevicesPage.xaml` 与 `NasStatusPage.xaml` 各有一份。
   *建议*:提到 `App.xaml` 的 `ResourceDictionary.MergedDictionaries`(代码里已留了「后续在这里合并自定义主题词典」的注释位)。

8. **无测试、无 CI**
   33 个源文件、0 个测试。解析类(`ParseUptime` / `ParseDf` / `ParseNetwork` / `ParseOsInfo` / `ParseHostPort`)是纯函数、**非常适合做单元测试**,是最低成本的质量提升点。

### 低

9. `NasDeviceStore` 每次 `Load()` 都读盘,且 `MainWindow` 搜索每次按键都 `Load()` 一次(可加内存缓存 + 文件时间戳失效)。
10. `SftpService.DeleteAsync` 的 `recursive` 参数没有实际效果(两个分支都调 `DeleteDirectory`)。
11. `ToolCatalog.Rescan()` 无 UI 入口。
12. `SpeedtestService` / `NasStatusService` 的单槽缓存没有过期策略,长时间挂机后可能展示很旧的数据(UI 上有时间戳提示,算可接受)。
13. `DeviceProbeService` 的超时值(1500ms / 1200ms)是硬编码,慢网络下可能误判离线。

---

## 17. 排障手册

### 开发期

| 现象 | 原因 / 处理 |
|---|---|
| `publish` 报 `NETSDK1129` | 缺 RID(正常情况 csproj 会自动推导;若显式传了 `-r` 请确认拼写) |
| 发布后启动即崩 | 确认 `WindowsAppSDKSelfContained=true` 且**没有** `PublishTrimmed=true`(WinUI 反射依赖多) |
| 界面全是黑底 | 根 `Grid` 需要自己画背景(`{ThemeResource ApplicationPageBackgroundThemeBrush}`),`NavigationView` 的 `ContentBackground` 已被设成 `Transparent` |
| 出现上下两条标题栏 | `SafeTitleBar.ApplyExtendedTall` 未生效或 `SetTitleBar` 未指向 `TitleBar` 控件 |
| 状态文字/圆点不刷新 | `x:Bind` 忘了写 `Mode=OneWay`(默认 `OneTime`) |
| 跨页跳转后侧栏高亮错位 | 用了 `Frame.Navigate` 而不是 `MainWindow.Instance.NavigateByTag` |
| 缓存页面进去不刷新 | 页面开了 `NavigationCacheMode`,需在 `OnNavigatedTo` 里判断 `IsLoaded` 手动刷新 |
| 新增工具不出现 | 扩展名不在 `ToolCatalog.ToolExts`;或需要重启(`Tools` 是惰性单例);确认文件确实被复制到了输出目录 |
| `WebView2` 类型找不到 | 不要单独引 `Microsoft.Web.WebView2`,它由 Windows App SDK 提供 |
| `Tools/` 中文目录读不到 | 路径处理引入了非 Unicode API |

### 用户现场(面向支持)

| 现象 | 排查方向 |
|---|---|
| 设备一直显示离线,但 NAS 明明活着 | NAS 禁 Ping → 服务会回落探测 SSH / Web / 445;若这些端口也被关,只能改造成「以 HTTP 探测为准」 |
| 测试连接提示「端口未开放」 | NAS 未开 SSH:群晖「控制面板 → 终端机和 SNMP → 启动 SSH」;威联通「控制台 → 网络和文件服务 → Telnet/SSH」 |
| 测试连接提示「服务器中断握手」 | 算法协商失败 / fail2ban / `sshd MaxStartups` 限流。客户端已是 SSH.NET 2024.x(支持现代算法);可让用户查 `/var/log/auth.log` 交叉验证 |
| 提示「认证失败」 | 群晖需用 `administrators` 群组账号;DSM 7 普通命令无需 root,`smartctl` 等需 sudo |
| sudo 相关报错 | 该账号不在 sudoers → 换账号;或未填 sudo 密码且未配免密 sudo → 填密码或配置免密 |
| root 会话报「sudo -i 提权超时」 | 该环境不支持交互式提权 → 改选「sudo 提权」模式 |
| Docker 报 `permission denied` | 引导用户「编辑 → 权限与高级」选 sudo 提权或 root 会话,或把账号加入 docker 组 |
| SMB 枚举「访问被拒绝」(错误码 5) | 目标禁止匿名枚举 → 先在资源管理器打开 `\\NAS的IP` 登录一次账号 |
| SMB 「找不到网络路径」(53) | 设备离线或 445 未开放 |
| 测速提示「未找到 speedtest-x 容器」 | 引导到 Docker 管理部署,需把宿主端口映射到容器 80(host 网络模式无法自动发现端口) |
| 测速页加载失败 | 用「打开网页版」在浏览器验证容器与端口是否正常 |
| 状态页报「读取系统信息失败」 | 看 `Error` 里附带的 `HintFor` 建议;常见是 SSH 配置不全或权限不足 |
| 密文解不开(换机器/换用户后密码为空) | 预期行为:DPAPI 绑定当前用户与本机,需重新填写密码 |

---

## 附:一分钟上手清单

1. `dotnet run` 跑起来,确认顶栏搜索、9 个页面都能进。
2. 读 `MainWindow.xaml.cs` 和 `Pages/DevicesPage.xaml.cs`——理解「导航 + 表单」两件事。
3. 读 `Services/Ssh/SshService.cs` + `SshConnectionCache.cs`——理解全项目唯一的复杂抽象。
4. 读 `Services/NasStatusService.cs`——理解「采集 + 抗脏解析」的写法。
5. 动手一件小事:按 15.4 给 NAS 状态页加一项采集(例如磁盘 SMART 温度),跑通后再看[第 16 节](#16-已知问题与技术债)挑一个「高」优先项修掉。
