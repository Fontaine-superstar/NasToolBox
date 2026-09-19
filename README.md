# NAS 工具箱(NasToolbox)

面向家用 NAS 的 Windows 桌面管理工具,基于 **WinUI 3 / .NET 10**,采用非打包(Unpackaged)模式分发。

一个应用覆盖日常 NAS 运维的高频动作:设备台账、实时状态监控、SMB 文件管理、Docker 容器管理、SSH 终端、网络诊断与 Wake-on-LAN 唤醒。

## 功能总览

| 页面 | 功能 |
|---|---|
| 首页 | 本机摘要(主机名 / IPv4 / 网关 / CPU / 内存 / 开机时长,WMI 后台查询),NAS 设备在线速览 + 一键检测 |
| 设备管理 | NAS 台账(名称 / IP / MAC / Web / SSH 账号),增删改 + 一键 Ping、打开 Web 管理、WOL 唤醒、打开 SMB 共享;**SSH 管理**:内置 SSH 终端、SSH 状态一键体检、自定义命令执行 |
| NAS 状态 | 远程采集目标 NAS 的运行快照:系统 / 运行(在线时长、负载)/ 内存 / 硬盘(容量、类型、接口、型号、SMART 健康,点击单盘查看完整 SMART 详情)/ 网络(内网 IP、公网 IP、网关、DNS、网卡)/ 磁盘空间(挂载点用量) |
| 文件管理 | 经 SMB 浏览共享与目录,支持上传、下载、新建文件夹、删除;文件可从资源管理器**拖入即上传**、从列表**拖出即下载**;右键共享或文件夹可**映射为本地网络驱动器**(也可一键断开) |
| Docker | 远程管理 NAS 上的容器(查看 / 启动 / 停止 / 重启 / 日志等) |
| 网络诊断 | Ping 报告(丢包率 / 最短 / 最长 / 平均延迟)、NAS 外网连通性测试(命令在 NAS 上执行)、常用 NAS 端口探测(SSH 22 / SMB 445 / 群晖 5000 等)、DNS 解析 |
| Web 管理 | 左侧选择 NAS 设备,应用内(WebView2)直接打开其 Web 管理界面,支持后退 / 前进 / 刷新 |
| SSH 终端(Shell) | 基于 xterm.js + WebView2 的交互式终端,内嵌于应用内 |
| 关于 | 功能说明 / 数据存放位置 / 使用须知 |

顶栏搜索框:按名称 / 地址 / 主机名 / 备注匹配 NAS 设备,回车直达「设备管理」并自动过滤。

## 环境要求

- Windows 10 19041+ / Windows 11
- .NET 10 SDK(`winget install Microsoft.DotNet.SDK.10`)

## 快速开始

```powershell
git clone https://github.com/Fontaine-superstar/NasToolBox.git
cd NasToolBox
dotnet run            # 非打包(Unpackaged)模式直接运行
```

> NAS 状态、Docker、文件管理等远程功能,需先在「设备管理」中添加设备并配置 SSH / SMB 凭据。

## 目录结构

```
├── App.xaml(.cs) / MainWindow.xaml(.cs)   入口 + 标题栏/导航骨架
├── Pages/                                 各功能页面(见功能总览)
├── Models/                                数据模型(NasDevice / DiskInfo / FileEntry / DockerContainer …)
├── Services/
│   ├── NasStatusService      NAS 状态批量采集(命令组一次 SSH 往返)
│   ├── SmartService          SMART 详情采集与解析
│   ├── Smb/                  SMB 会话(WNet)与 UNC 文件操作
│   ├── Ssh/                  SSH 连接、命令执行、root 提权 shell、SFTP、终端会话
│   ├── NetworkService        Ping / TCP 端口探测 / DNS / 本机网络信息
│   ├── WakeOnLanService      魔术包(全局广播 + 子网定向广播)
│   ├── SmbService            NetShareEnum 共享枚举(P/Invoke)
│   └── NasDeviceStore / DeviceStatusStore / SecretProtector …
├── Converters/Conv.cs        x:Bind 函数绑定辅助(字节格式化 / 端口状态 / 盘符等)
├── Assets/Terminal/          xterm.js 前端终端(vendored,MIT)
└── docs/                     开发者指南 / 用户手册 / AI 上下文档
```

## 数据存放

- 设备台账:`%LocalAppData%\NasToolbox\devices.json`(首次保存设备时创建)
- 设备状态缓存:`%LocalAppData%\NasToolbox\device_status.json`
- SSH 主机指纹:`%LocalAppData%\NasToolbox\known_hosts.json`

## 隐私与安全

- **SSH 密码**使用 Windows DPAPI(`SecretProtector`)加密存储,绑定当前用户与本机;把 `devices.json` 拷到别处无法解出。
- 文件管理复用设备已保存的 SSH 账号密码,通过 `WNetAddConnection2` 建立 SMB 会话,密码**只存在于内存**,不会写入磁盘、也不会作为命令行参数出现。
- SSH 主机密钥按设备指纹校验,避免中间人风险。
- 所有远程操作均需用户显式触发,程序不会在后台主动连接设备。

## 发布

```powershell
# 便携版(解压即用,自带 WinAppSDK 运行时)
dotnet publish -c Release -r win-x64 -o publish\x64
# 同理 win-x86 / win-arm64
```

## 使用须知

- **Wake-on-LAN**:需要 NAS 的 BIOS/UEFI 与网卡开启 WOL;部分路由器/AP 会拦截广播帧,已同时向 `255.255.255.255` 与各网卡子网定向广播地址发包以提高成功率。
- **SMB 枚举**:NetShareEnum 依赖目标允许枚举;群晖等默认禁用匿名访问,提示"访问被拒绝"时,先在资源管理器打开 `\\NAS的IP` 登录一次账号。
- **硬盘 SMART 健康**需要目标 NAS 装有 `smartmontools`,且运行用户有检测权限(走设备配置的 root / sudo 提权通道);未安装时该盘显示"未知",容量与型号不受影响。
- **端口探测**只是 TCP 连通性测试,不代表服务一定正常。
- **SSH**:群晖等 NAS 需先在管理后台开启 SSH;若目标服务器禁用了旧版加密算法导致连接失败,可改用「SSH 终端」(系统 OpenSSH)。
- `Tools/` 目录用于放置第三方绿色工具,仓库内为空;分发前请逐一确认各工具自身授权(多为禁止再分发的 EULA,常见做法是"首次运行按需下载")。
- `img/` 用于存放离线容器镜像包(如 speedtest-x),因体积过大未纳入仓库。

## 开发文档

- [`docs/DEVELOPER_GUIDE.md`](docs/DEVELOPER_GUIDE.md) — 架构、SSH 子系统、扩展指南、技术债
- [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) — 逐页面的用户手册
- [`docs/AI_CONTEXT.md`](docs/AI_CONTEXT.md) — 面向 AI 的密集代码事实档
- [`docs/OPEN_SOURCE_AUDIT.md`](docs/OPEN_SOURCE_AUDIT.md) — 第三方依赖与许可证审计

## 许可证

本项目采用 **GPL-3.0** 许可证,详见 [`LICENSE`](LICENSE)。

项目骨架衍生自 [luolangaga/tubatools](https://github.com/luolangaga/tubatools)(TubaWinUi3,GPL-3.0),在此致谢。
内嵌的 [xterm.js](https://github.com/xtermjs/xterm.js)(MIT)位于 `Assets/Terminal/`。
