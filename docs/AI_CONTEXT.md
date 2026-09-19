# NasToolbox — AI 上下文档

> 目的:让一个**没有任何本仓库历史**的 AI 代理在读完后,能够准确地定位代码、判断改动影响面、避免踩坑。
> 写法刻意采用「可检索的短句 + 精确路径 + 表格」,不做叙事铺垫。
> 版本基线:`NasToolbox v0.2.0`(AssemblyVersion 0.2.0.0)。若代码与本文冲突,**以代码为准**,并回来更新本文。

---

## 0. 30 秒速览

| 项 | 值 |
|---|---|
| 是什么 | 家用 NAS 管理桌面工具。「NAS 工具箱」= 设备台账 + 状态采集 + Docker 管理 + 向导式网络诊断 + 绿色工具库 |
| 技术 | WinUI 3(Windows App SDK 2.2.0)/ .NET 10 / C# / 单工程 / 非打包(解压即用) |
| 根命名空间 | `NasToolbox`;程序集名 `NasToolbox`;工程文件 `NasToolbox.csproj` |
| 规模 | 35 个 `.cs` + 9 个 `.xaml`(不含 `bin/` `obj/`) |
| 入口 | `App.xaml.cs` → `MainWindow.xaml.cs` → `Frame` 路由到 9 个页面 |
| 外部依赖 | `Microsoft.WindowsAppSDK` 2.2.0、`SSH.NET` 2026.0.0、`System.Management` 10.0.8、`Microsoft.Windows.SDK.BuildTools` 10.0.26100.7705、`Microsoft.WindowsDesktop.App.WindowsForms`(FrameworkReference) |
| 网络能力来源 | **全部 .NET BCL**(Ping / TcpClient / UdpClient / Dns / DriveInfo),唯一例外是 SSH 走 SSH.NET |
| 被管理对象 | 用户登记的多台 NAS;其中最多一台是「当前管理设备」(`IsCurrent`) |
| 用户数据目录 | `%LocalAppData%\NasToolbox\`(另有历史遗留的 `%LocalAppData%\MyToolbox\IconCache`) |
| 测试/CI | **无**。没有测试工程、没有解决方案文件、没有 CI 配置 |

---

## 1. 代码地图(路径 → 职责 → 关键符号)

### 1.1 骨架

| 路径 | 职责 | 关键符号 |
|---|---|---|
| `App.xaml` / `App.xaml.cs` | 应用入口,合并 `XamlControlsResources` | `App.MainWin`(静态窗口引用) |
| `MainWindow.xaml` / `.xaml.cs` | 48px 自绘标题栏 + `NavigationView` + `Frame`(`CacheSize=10`) | `MainWindow.Instance`、`NavigateByTag(tag)`、`NavView_SelectionChanged`、`SearchBox_*` |
| `app.manifest` | `asInvoker`、`PerMonitorV2` DPI、支持 Win10/11 | — |
| `global.json` | 固定 SDK `10.0.302`,`rollForward: latestFeature` | — |

### 1.2 Pages(9 个,均为 `sealed partial class ... : Page`)

| 文件 | 导航 Tag | 是否有 `NavigationCacheMode="Enabled"` | 职责 |
|---|---|---|---|
| `Pages/DashboardPage.xaml(.cs)` | `home` | 否 | 本机网络 + WMI 摘要 + NAS 设备速览(含「检测全部」) |
| `Pages/DevicesPage.xaml(.cs)` | `devices` | **是** | 设备台账 CRUD、编辑对话框(基本信息 / SSH 连接 / 权限与高级)、测试连接、当前管理设备勾选 |
| `Pages/NasStatusPage.xaml(.cs)` | `status` | 否 | SSH 采集的 NAS 状态卡片:系统 / 运行 / 内存 / 网络 / 磁盘空间 |
| `Pages/WebAdminPage.xaml(.cs)` | `web` | **是** | 左侧设备侧边栏(缓存在线状态 + 一键检测)+ 右侧 WebView2 内嵌各 NAS 的 Web 管理界面(地址取 `NasDevice.WebOrDefault`;顶部仅后退/前进/刷新/系统浏览器打开,无地址栏) |
| `Pages/NetworkToolsPage.xaml(.cs)` | `network` | 否 | Ping 报告 + 基于 Docker 的局域网测速(内嵌隐藏 WebView2 取数) |
| `Pages/DiskSharePage.xaml(.cs)` | `disk` | 否 | 本机磁盘空间 + `NetShareEnum` 枚举远程 SMB 共享并打开 |
| `Pages/DockerPage.xaml(.cs)` | `docker` | **是** | Docker 五分区:概览 / 容器 / Compose / 镜像(本机+NAS)/ 网络 |
| `Pages/AllToolsPage.xaml(.cs)` | `tools` | **是** | `Tools/` 扫描结果卡片网格,点击启动 |
| `Pages/AboutPage.xaml(.cs)` | `about` | 否 | 纯静态说明 |

### 1.3 Models(`Models/`)

`NasDevice`(持久化根对象)、`DeviceRow`(`INotifyPropertyChanged` 视图模型)、`DeviceStatus`、`NicInfo`、`MountUsage`、`DriveSummary`、`ShareEntry`、`PortProbeResult`、`ToolItem`、`SftpEntry`、`SshAuthKind`、`DockerContainer`、`DockerAssets`(`DockerImage` / `DockerNetwork` / `DockerComposeProject` / `LocalImageFile`)。

### 1.4 Services(`Services/`)

| 文件 | 职责要点 |
|---|---|
| `NasDeviceStore.cs` | 设备台账 JSON 读写;`AssignDisplayNames` 派生显示名;`GetCurrent` / `GetCurrentOrDefault` / `SetCurrent` / `UpdateDevice` |
| `DeviceStatusStore.cs` | 在线状态持久化(`host → DeviceStatus`),内存字典 + 显式 `Save()` |
| `DeviceProbeService.cs` | 在线探测编排:Ping 优先 → 回落 TCP(SSH / Web / 445);`ProbeAllAsync` / `ProbeAndPersistAsync` / `ApplyCached`;整轮检测可被取消;**行状态更新经 `DispatcherQueue.TryEnqueue` 派发回 UI 线程**(后台线程触发 `PropertyChanged` 不会刷新 x:Bind),被取消的轮回填上次结果兜底 |
| `NetworkService.cs` | `PingOnceAsync` / `PingReportAsync` / `TestPortAsync` / `ResolveReportAsync` / `IsVirtualAdapter` / `GetLocalNetworkInfo` |
| `WakeOnLanService.cs` | 魔术包构造与发送(全局广播 + 子网定向广播)、MAC 宽容解析 |
| `SmbService.cs` | `Netapi32.NetShareEnum(SHARE_INFO_1)` P/Invoke + 错误码人话化 |
| `DiskService.cs` | `DriveInfo` → `DriveSummary` |
| `SecretProtector.cs` | DPAPI(CurrentUser)加解密,entropy 固定串 `NasToolbox.Ssh.v1` |
| `NasStatusService.cs` | NAS 状态采集(11 条只读命令)+ 全部解析器 + 单槽快照缓存 + 全局串行门 |
| `DockerService.cs` | 远端 docker 全部操作 + 本机 `img\*.tar` 上传导入 + 错误翻译 |
| `SpeedtestService.cs` | 识别 speedtest-x 容器、解析宿主端口、打开网页版;**HTTP 自实现测速为遗留死代码** |
| `ToolCatalog.cs` | 扫描 `Tools/<中文分类>/` 并与 `Metadata/tools.json` 合并 |
| `ToolLauncher.cs` | `Process.Start`(`UseShellExecute=true`,可 `runas`) |
| `ToolIconService.cs` | `SHGetFileInfoW` 提图标 → PNG 缓存 |
| `SafeTitleBar.cs` | 延展标题栏 + 48px Tall + 明暗主题着色,失败静默降级 |
| `WindowLimits.cs` | 最小窗口尺寸限制(850×700):`AppWindow` 无原生 MinSize,经 `Changed` 事件把低于下限的尺寸 `Resize` 顶回;按 DPI(`RasterizationScale`)换算物理像素;失败静默降级 |

### 1.5 Services/Ssh(`Services/Ssh/`)

| 文件 | 职责要点 |
|---|---|
| `SshService.cs` | 统一执行入口:`RunAsync` / `RunManyAsync` / `TestAsync`(三段式诊断)/ `DetectAsync` / `Validate` / `HintFor` / `ShellQuote` |
| `SshClientFactory.cs` | 建连与认证方式构造、主机指纹回调、异常 → `SshConnectFailure` 分类 |
| `SshConnectionCache.cs` | `internal` 连接复用池:`ConcurrentDictionary<key, Slot>`,`Slot.Gate` 串行化;空闲 5 分钟回收 |
| `SshRootShell.cs` | `sudo -i` 交互会话:提示符识别、密码应答、marker 法取退出码、剥 ANSI / 回显 |
| `SftpService.cs` | 列目录 / 上传 / 下载 / 删除 / 建目录;**每次操作独立建连** |
| `SshHostKeyStore.cs` | 简化 known_hosts:`host:port → SHA256:base64(去 `=`)` |
| `SshConnectReport.cs` | `SshConnectFailure` 枚举、`SshConnectReport`(展示文本)、`SshConnectException` |
| `SshCommandResult.cs` | 命令结果(`Stdout`/`Stderr`/`ExitCode`/`TimedOut`/`ErrorMessage`/`Output`) |
| `NasOsInfo.cs` | `NasKind` 枚举(群晖 / 威联通 / UnRAID / TrueNAS / OpenWrt / 通用)+ 摘要文本 |

### 1.6 其他

| 路径 | 说明 |
|---|---|
| `Converters/Conv.cs` | 供 `x:Bind` 的静态函数绑定:`WhenEmpty` / `WhenNotEmpty` / `WhenTrue` / `FmtBytes` / `DriveLetter` / `OpenText` / `OpenBrush` |
| `Metadata/tools.json` | 8 条工具元数据,字段 `match`(路径子串,大小写不敏感)/ `name` / `description` |
| `Tools/<分类>/` | 第三方绿色工具;当前仅有占位 `网络工具\把NAS相关绿色工具放到这里.txt` |
| `img/` | 随包的建议镜像 `*.tar`,Docker 页可上传导入 |
| `README.md` | **已过时**,见第 11 节 |

---

## 2. 启动时序(改启动行为前必读)

```
App.OnLaunched
└─ new MainWindow()
   ├─ InitializeComponent()            // XAML 树
   ├─ Instance = this
   ├─ SafeTitleBar.ApplyExtendedTall(this, AppTitleBar)
   ├─ WindowLimits.ApplyMinSize(this, 850, 700)       // 最小窗口 850×700
   ├─ _ = DeviceProbeService.ProbeAndPersistAsync()   // ① 后台:按台账 Ping 全部设备并落盘
   ├─ NasStatusService.PrefetchOnStartup()            // ② 后台:采集「当前设备」状态填缓存
   └─ MainWin.Activate()
```

副作用要点:

1. ① 与 ② **都会**对「当前设备」发起在线探测,启动瞬间存在重复探测。
2. `DeviceProbeService` 是「单轮制」:任何新调用会 `Cancel` 上一轮尚未完成的检测(`NewRound()`),因此打开设备页触发的自动检测可能取消 ①。
3. ② 不经页面,失败只体现在 `NasStatusSnapshot.Error`,不弹窗。
4. `NasStatusService` 与 `DeviceProbeService` 都不持有 UI 引用,可安全后台跑。

---

## 3. 导航契约

- 侧栏 `NavigationViewItem.Tag` 取值:`home` / `devices` / `status` / `web` / `network` / `disk` / `docker` / `tools` / `about`。
- `MainWindow.NavView_SelectionChanged` 中的 `switch` 是 **Tag → Page 类型** 的唯一映射点。
- 页面内跨页跳转必须走 `MainWindow.Instance?.NavigateByTag("docker")` 这类方式,**不要**直接 `Frame.Navigate`,否则侧栏高亮不同步。
- 顶栏统一搜索是**双前缀**协议:
  - `设备 · ` → 跳 `DevicesPage`,参数为去掉前缀的查询串;
  - `工具 · ` → 跳 `AllToolsPage`,参数同上;
  - 结果上限:设备 4 条 + 工具 6 条。
- `DevicesPage` / `AllToolsPage` 的过滤参数是「一次性」的:在 `OnNavigatedTo` 里取用后立即置 `null`。
- 四个页面开了 `NavigationCacheMode=Enabled`,所以 `Loaded` 只触发一次;这类页面必须在 `OnNavigatedTo` 里显式判断 `IsLoaded` 再刷新。

---

## 4. 数据契约

### 4.1 文件位置

| 文件 | 结构 | 写入者 |
|---|---|---|
| `%LocalAppData%\NasToolbox\devices.json` | `List<NasDevice>` | `NasDeviceStore.Save` |
| `%LocalAppData%\NasToolbox\device_status.json` | `Dictionary<host, DeviceStatus>` | `DeviceStatusStore.Save` |
| `%LocalAppData%\NasToolbox\known_hosts.json` | `Dictionary<"host:port", "SHA256:...">` | `SshHostKeyStore.Trust/Forget` |
| `%LocalAppData%\MyToolbox\IconCache\*.png` | 图标缓存(文件名 = 路径小写 SHA1 前 12 位十六进制) | `ToolIconService.GetIconPng` |

JSON 选项统一为 `WriteIndented = true` + `UnsafeRelaxedJsonEscaping`(中文不转义)。

### 4.2 `NasDevice` 关键成员

持久化字段:`Name`、`Host`、`Hostname`、`Mac`、`WebPort`、`Note`、`SshPort`、`SshUser`、`SshPassEnc`、`SshAuth`、`SshKeyPath`、`SshKeyPassEnc`、`UseSudo`、`SudoPassEnc`、`RootLogin`、`SshTimeoutSec`、`SshHostFingerprint`、`IsCurrent`。

计算成员(`[JsonIgnore]`,不落盘):`DisplayName`、`NameBase`、`HostnameLine`。

派生只读属性:`WebOrDefault`(`http://{Host}:{EffectiveWebPort}`)、`EffectiveWebPort`(Web 管理端口,表单必填,越界兜底 5000,范围 1–65535)、`Unc`(`\\host`)、`SshDisplay`、`SshCacheKey`(`{user}@{host}:{port}|{auth}|{keyPath}`)、`EffectiveTimeoutSec`(非法值兜底 20,范围 1–600)、`HasSshUser`、`SshSummary`。

**重要不变量**

- `Name` 在保存时被写成 `Hostname ?? Host`,而 `DisplayName` 是加载时由 `NameBase`(`Hostname` 优先,回退 `Host`)重算的。因此**改显示名 = 改 `Hostname`**;`Name` 对界面几乎无影响,只在 `UpdateDevice` 的匹配条件里参与。
- 同名设备从第二台起由 `AssignDisplayNames` 追加 ` (2)`、` (3)`。
- `IsCurrent` 全表最多一台为真;`SetCurrent` 会遍历全表重置后落盘。
- `SshTimeoutSec` 越界不会报错,读取时被静默兜底为 20。
- 旧配置的 `Web`(Web 管理地址字符串)在 `NasDeviceStore.Load` 时经 `MigrateLegacyWeb` 解析出端口写入 `WebPort` 后清空;`Web` 带 `JsonIgnore(WhenWritingDefault)` 保存时不再落盘;老配置缺 `WebPort` 时默认 5000。

### 4.3 `SshAuthKind`

`Password = 0`(旧配置缺字段时反序列化为 0,兼容)、`PrivateKey = 1`。

### 4.4 权限模式的三态编码

UI 用 `RadioButtons` 单选用 `SelectedIndex` 编码,持久化时**展开成两个 bool**:

| UI 索引 | 语义 | `UseSudo` | `RootLogin` |
|---|---|---|---|
| 0 | 普通用户 | false | false |
| 1 | sudo 提权 | true | false |
| 2 | root 会话 | true | **true** |

读取时的反向映射:`RootLogin ? 2 : UseSudo ? 1 : 0`。新增设备默认索引 2。

---

## 5. SSH 子系统契约

### 5.1 调用分层

```
页面 / 业务 Service
   └─ SshService.RunAsync / RunManyAsync        ← 唯一推荐的执行入口
        └─ SshConnectionCache.RentAsync(key, factory, waitMs)
             ├─ 命中活连接 → Lease
             └─ 未命中 → SshClientFactory.Create(device, { TrustNewHostKey = true })
        └─ Lease.RootShell 有值 ? 走 SshRootShell.RunAsync : 走 exec 通道
   SftpService.*                                ← 独立建连,不复用缓存
```

### 5.2 行为契约

- **不抛异常的场景**:命令级问题(超时、连接断开、配置缺失)统一通过 `SshCommandResult.ErrorMessage` / `TimedOut` 返回。只有**建连失败**才抛 `SshConnectException`(带 `SshConnectFailure` 分类)。
- **超时语义**:`RunAsync(device, cmd, timeoutMs, sudo, ct)` 中 `timeoutMs == 0` 表示用设备配置(`EffectiveTimeoutSec * 1000`);等待连接空闲的上限 = `单条超时 + 20s`。
- **超时后连接必被废弃**:exec 通道超时会 `lease.Abandon()`(远端进程可能还在跑,连接不可安全复用)。
- **sudo 包装**:`sudo ?? device.UseSudo`,但 `device.RootLogin == true` 时强制不加 sudo(会话里已是 root)。
  - 有 sudo 密码 → `printf '%s\n' <pass> | sudo -S -p '' sh -c <cmd>`
  - 无 sudo 密码 → `sudo -n sh -c <cmd>`
  - 命令一律经 `SshService.ShellQuote` 单引号转义。
- **Hostname 自动回填**:设备 `Hostname` 为空时,连接成功后执行一次 `hostname` 并 `NasDeviceStore.UpdateDevice` 落盘;每个 `SshCacheKey` 只尝试一次(`HostnameTried` + 失败可重试)。
- **连接缓存**:同一 `SshCacheKey` 复用一条连接;`KeepAliveInterval = 30s`;空闲 5 分钟由每 2 分钟一次的 `Timer` 回收;SSH.NET 客户端非线程安全,故同 key 严格串行(`SemaphoreSlim(1,1)`)。
- **指纹信任策略(安全要点)**:
  - `SshService.TestAsync(trustNewHostKey: false)`(设备页「测试连接」)→ 未知指纹会返回 `HostKeyUntrusted` / `HostKeyChanged`,由 UI 引导用户确认。
  - **命令执行与 SFTP 通道传的是 `TrustNewHostKey: true`,即静默接受未知主机密钥,并且不写入指纹库。**
  - 指纹只在两处落库:`TestAsync` 成功后(且 `trustNewHostKey == true`)、`SshHostKeyStore.Trust` 被 UI 显式调用时。若 `device.SshHostFingerprint` 与握手结果一致,也算已信任并补写指纹库。
- **主机指纹格式**:`"SHA256:" + Base64(SHA256(hostKey))` 去掉尾部 `=`。

### 5.3 root 会话(`SshRootShell`)的三个关键技巧

1. **原始字符流匹配**:密码提示与 shell 提示符都不带换行,按行读永远拼不出完整行 → 必须累积原始字节流再匹配(超时返回 `PromptKind.None`)。
2. **先看回显再认提示符**(`gateOnEcho`):避免残留的旧用户提示符迟到被误判为提权完成。
3. **marker 取退出码**:命令与 `echo $M1$M2$?` 分两行写,marker 被拆成 `M1='__NT_'; M2='...__'`,防止回显行里出现完整 marker 被误判;随后按「去空白前缀比对」抹掉终端折行的回显碎片,并剥离首行提示符。建立会话后执行 `stty -echo; PS1=''`,并用 `id -u == 0` 复核提权是否真的成功。

---

## 6. 线程与并发不变量

| 不变量 | 位置 |
|---|---|
| 所有 `x:Bind` 的状态绑定必须显式写 `Mode=OneWay`(`x:Bind` 默认 `OneTime`) | 各页面 XAML |
| 跨线程更新 UI 必须回到 UI 线程(`await` 后自然回 UI;`IProgress<T>` 回调在后台线程) | `SftpService` 注释、各页面;`DeviceProbeService` 捕获 UI 线程 `DispatcherQueue` 后 `TryEnqueue` 派发 |
| 同 `SshCacheKey` 的命令严格串行;不同设备可并发 | `SshConnectionCache` |
| `NasStatusService.Gate`(`SemaphoreSlim(1,1)`)是**全局**门:采集一台设备期间,任何设备的采集都排队 | `NasStatusService.FetchAsync` |
| `DeviceProbeService` 同一时刻只允许一轮批量检测 | `_pending` + `CancelPending` |
| `ToolCatalog.Tools` 走 `??=` 惰性扫描,**首次访问即扫描并创建 `BitmapImage`**,应在 UI 线程首次触发 | `ToolCatalog.Tools` |
| `DispatcherTimer` 驱动的 UI 走秒/轮询必须在 `Unloaded` 里停掉 | `NasStatusPage`、`NetworkToolsPage` |

`NasStatusService` 与 `SpeedtestService.LastTest` 都是**单槽缓存**,换设备时靠 `DeviceKey` / `Host` 比对判断能否复用。

---

## 7. 硬性约束(动代码前必读)

1. **WinUI3 必须指定 RID**,否则 `publish` 报 `NETSDK1129`。csproj 已按当前进程架构自动推导 `win-x64|win-x86|win-arm64`。
2. **禁止开启 `PublishTrimmed`**。WinUI3 反射依赖多,csproj 已显式 `false`。
3. **非打包形态**:`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`。若要改回 MSIX,需去掉 `None` 并补 `appxmanifest`。
4. **路径必须 Unicode 安全**:`Tools/` 使用中文目录名,任何路径处理不得用 ANSI/本地编码 API。
5. **不要用 `Renci.SshNet` 这个旧包 ID**(停在 1.0.0,只支持 OpenSSH 8.2 之前的老算法,现代 NAS 握手会被服务端直接断开)。命名空间仍是 `Renci.SshNet`,但包引用必须是 `SSH.NET` 2026.x(2026.0.0 修复 CVE-2026-85756 / CVE-2026-48798,两者均只影响本项目未使用的 ScpClient;本项目只用 SshClient / SftpClient / ShellStream)。
6. **`Tools\**` / `Metadata\**` / `img\**` 是 `Content`+`PreserveNewest`**,发布时必须跟着输出;`Tools`/`img` 另标 `ExcludeFromSingleFile`。
7. **`WebView2` 未显式引包**:`NetworkToolsPage` 用到的 `Microsoft.Web.WebView2.Core` 由 Windows App SDK 间接提供,不要重复添加 `Microsoft.Web.WebView2` 以免版本冲突。
8. **凭据只经 `SecretProtector` 落盘**,禁止新增明文口令字段;`SecretProtector` 的 entropy 串一旦更改,历史密文全部失效。
9. **新增页面** = 3 处改动:`Pages/` 建 XAML+cs → `MainWindow.xaml` 加 `NavigationViewItem(Tag)` → `MainWindow.xaml.cs` 的 `switch` 加映射。
10. 页面内的分区卡片样式(`SectionCard` / `SectionTitle` / `FieldHint`)当前是**逐页复制**的资源定义,新增页面需自行复制,不要指望全局主题里有。

---

## 8. 常见改动配方

| 需求 | 改哪里 | 注意 |
|---|---|---|
| 加页面 | 见约束 9 | Tag 唯一;需要 `NavigateByTag` 可达就保持 `MainWindow` 单点映射 |
| 加第三方工具 | 丢进 `Tools/<分类>/`;要正式名/描述就改 `Metadata/tools.json` | `match` 是**与完整路径**的大小写不敏感子串;新增扩展名要改 `ToolCatalog.ToolExts` |
| 改设备表单字段 | `Pages/DevicesPage.xaml`(表单)+ `.xaml.cs` 的 `EditBtn_Click` / `AddBtn_Click` / `BuildDeviceFromForm` / `EditDialog_PrimaryButtonClick` / `ValidateForm` (5 处) + `Models/NasDevice.cs` | 五处漏一处就会「测试连接能过、保存丢字段」 |
| 改 NAS 状态采集项 | `Services/NasStatusService.cs` 的 `Commands` 数组 | 数组顺序即索引契约;新增项要同步 `FetchCoreAsync` 里的 `Out(metrics, n)`,并复核 `ParseNetwork` 的入参下标 |
| 改命令超时范围 | `Models/NasDevice.EffectiveTimeoutSec` + `DevicesPage.xaml` 的 `NumberBox` | 两处必须一致 |
| 加 Docker 操作 | `Services/DockerService.cs` | 复用 `SshService.RunAsync` + `Explain()` + 超时;输出格式串用 `\t` 分隔(端口里含逗号,不能用逗号分隔) |
| 切到 MSIX | csproj 去掉 `WindowsPackageType=None`,补 `Package.appxmanifest` | — |

---

## 9. 重要解析细则(改采集/解析前核对)

- `ParseUptimeSec`:优先匹配「带小数点的独立数字」,避免把提示符残留里的主机名数字(如 `nas2`)误读为秒数。
- `ParseUptime`:`up` 段在完整版 `uptime` 里以 `, N users` 结束,busybox 版没有 users 段,取第一段;`load average` 取其后到行尾。
- `MemKb`:正则允许行内任意位置匹配,容忍提示符残留前缀;`MemAvailable` 取不到时回落 `MemFree`(老内核)。
- `ParseDf`:以「第 5 字段以 `%` 结尾」判定有效行;过滤 `tmpfs` / `devtmpfs` / `overlay` / `none` / 含 `loop` 的文件系统,以及 `/proc` `/sys` `/dev` `/run` 挂载点;挂载名 `vol1` / `volume1` → 显示为「存储空间1」并置于列表最前(`OrderBy` 稳定排序保留其余原始顺序)。
- `ParseNetwork`:输入下标为 `(ipAddr=Out(9), route=Out(10), IF1=Out(6), IF2=Out(8), dns=Out(11))`;网卡两次采样间隔 **1 秒**(命令数组里真的有一条 `sleep 1`),速率 = 字节差(按 1 秒近似换算,不做真实时间差校准)。网卡排序:IPv4 以 `192.` 开头的排最前,其余保持采集顺序(稳定排序)。
- `GetLocalNetworkInfo`:本机 IPv4 展示顺序为 `192.` 开头优先,其余保持系统枚举顺序(稳定排序)。
- `ParseOsInfo`:先按 `=` 拆 key=value,首个不含 `=` 的行当作 `uname -s -r` 的内核串;品牌判定优先级 群晖 → 威联通 → UnRAID → TrueNAS(`/etc/truenas_version` 或存在 `midclt`)→ OpenWrt → Linux → Unix。
- `SmbService.Explain`:5 拒绝访问 / 53 找不到网络路径 / 64 / 121 / 1231 / 1326 已给出中文建议;`NetShareEnum` 返回 234(`ERROR_MORE_DATA`)且有数据时**仍然继续解析**。
- `ToolIconService`:缓存键 = `SHA1(UTF-16LE(路径小写))` 取前 12 位十六进制;`SHGetFileInfoW` 拿到的 `hIcon` **必须** `DestroyIcon`。
- `SpeedtestService.ParseHostPort`:优先匹配映射到容器 `80` 的宿主端口,否则退回第一条 `-> .../tcp`。

---

## 10. 安全模型小结

| 资产 | 保护方式 |
|---|---|
| SSH 密码 / 私钥口令 / sudo 密码 | DPAPI `CurrentUser` + entropy `NasToolbox.Ssh.v1`,Base64 存 `devices.json`;**跨用户/跨机器拷贝无法解密**(解密失败静默当空串) |
| 主机身份 | 简化 known_hosts,`host:port → SHA256` 指纹;变更时 UI 给出「指纹已变更」警示(仅在测试连接路径生效) |
| 本地提权 | `app.manifest` 为 `asInvoker`;工具库支持 `runas`(`ToolLauncher.Launch(asAdmin: true)`) |
| 远端提权 | `sudo -n` / `sudo -S` / `sudo -i` 三态,密码只经 stdin,不落命令行历史 |
| 未覆盖 | 指纹静默接受(见 5.2)、`devices.json` 明文存放主机与用户名、无日志审计 |

---

## 11. 陷阱与已知不一致(写代码前先看这张表)

| 类型 | 事实 | 影响 |
|---|---|---|
| **文档漂移** | `README.md` 描述的是更早的版本:提到「一键 Ping / 打开 Web 管理 / WOL 唤醒 / 打开 SMB 共享 / SSH 终端 / SSH 状态体检 / SSH 自定义命令」等设备行操作,以及「常用端口探测(10 个)」「DNS 解析 UI」「CommonPorts 数组」 | 这些 UI **当前都不存在**;`CommonPorts` 全仓无此符号。README 还写着 `cd MyToolbox`、`%LocalAppData%\NasToolbox\IconCache` |
| **路径不一致** | 关于页与 README 声称图标缓存在 `%LocalAppData%\NasToolbox\IconCache`,实际代码是 `%LocalAppData%\MyToolbox\IconCache` | 清理/排查时找错目录 |
| **死代码** | `SshService.StatusScript`、`SpeedtestService.TestAsync`(及其 `DetectProtocolAsync` / `MeasureDownloadAsync` / `MeasureUploadAsync` / `GetIpInfoAsync` / `SpeedtestProgress`)、`WakeOnLanService.SendAsync`、`NetworkService.ResolveReportAsync`、`Conv.OpenText` / `Conv.OpenBrush`、`PortProbeResult` 的 UI 用途 | 相关功能已被 UI 移除或改由 WebView2 实现,改这些代码不会影响界面 |
| **重复探测** | 启动时 `ProbeAndPersistAsync` 与 `NasStatusService.FetchAsync` 都会探测当前设备 | 启动瞬间多余一次 Ping/TCP |
| **单轮取消** | 设备页自动检测可能取消启动那一轮 | 启动探测结果可能缺失,属预期;被取消轮次会把停留在「检测中…」的行回填上次持久化结果(无记录回「未检测」),不再永久卡住 |
| **全局串行** | `NasStatusService.Gate` 是全局单锁,不是按设备锁 | 多设备并发采集会互相排队 |
| **样式重复** | `SectionCard` / `SectionTitle` / `FieldHint` 在 `DevicesPage.xaml` 与 `NasStatusPage.xaml` 各定义一份 | 改样式要改多处,容易视觉不一致 |
| **`Name` 字段冗余** | `Name` 落盘但显示走 `DisplayName`(由 `Hostname` 重算) | 直接改 `Name` 不会改变界面显示 |
| **`ToolCatalog` 单例** | `Tools` 走 `??=`,外部改 `Tools/` 后必须 `ToolCatalog.Rescan()`(目前无 UI 入口) | 新增工具需重启应用 |
| **无测试无 CI** | 33 个源文件、0 个测试 | 任何改动只能靠手测,回归风险自担 |

---

## 12. 术语与缩写对照

| 术语 | 含义 |
|---|---|
| 台账 | 设备列表(`devices.json`) |
| 当前管理设备 | `IsCurrent == true` 的那台;Docker / NAS 状态 / 测速 / SMB 默认目标;未勾选时回退第一台 |
| 速览 / 检测 | `DeviceProbeService` 的在线判定结果 |
| 快照 | `NasStatusSnapshot`,一次状态采集的全量结果 |
| 权限模式 | 普通用户 / sudo 提权 / root 会话 三态 |
| 提权 | 通过 `sudo` 获得 root 权限 |
| 绿色工具 | 免安装的第三方 exe/bat/lnk 等,放在 `Tools/` |
| 建议镜像 | `img/*.tar`,可在 Docker 页上传到 NAS 后 `docker load` |
| 走秒 | 用 `DispatcherTimer` 每秒推进的在线时长显示 |

---

## 13. 快速检索锚点

需要找……就搜这些符号:

| 想找 | 搜索词 |
|---|---|
| 页面路由 | `NavView_SelectionChanged`、`NavigateByTag` |
| 统一搜索 | `DevPrefix`、`ToolPrefix` |
| 设备持久化 | `NasDeviceStore`、`devices.json` |
| 在线探测 | `ProbeAsync`、`ProbeAllAsync`、`ApplyCached` |
| 状态采集命令 | `private static readonly string[] Commands` |
| sudo 包装 | `BuildCommand`、`ShellQuote` |
| root 会话细节 | `WaitForPromptAsync`、`Marker`、`PromptKind` |
| 连接复用 | `RentAsync`、`IdleTimeout`、`ReapIdle` |
| 指纹 | `HostKeyReceived`、`CreateHostKeyHandler`、`ComputeFingerprint` |
| 密文 | `SecretProtector.Protect`、`Unprotect` |
| Docker 命令 | `PsFormat`、`DockerCreateSpec`、`Explain` |
| 测速端口解析 | `ParseHostPort`、`ResolveHostPortAsync` |
| 工具扫描 | `ToolExts`、`LoadMetadata`、`match` |
| 标题栏 | `ApplyExtendedTall`、`TitleBarHeightOption.Tall` |
| 最小窗口 | `WindowLimits.ApplyMinSize`、`MinOf` |

---

### 维护约定

- 本文与代码同步的责任在**改动者**:任何影响「文件路径、持久化字段、导航 Tag、命令数组下标、不变量」的改动,应在同一次提交里更新本文。
- 校验方式:用第 13 节的检索锚点逐项确认符号仍存在;`rg "CommonPorts"` 应始终无结果(README 已过时)。
