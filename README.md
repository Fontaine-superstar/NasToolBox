![NAS 工具箱 Typing](https://readme-typing-svg.demolab.com?font=Fira%20Code&size=26&pause=1000&color=0078D4&center=true&vCenter=true&width=600&lines=Home%20NAS%20Toolbox%20for%20Windows;WinUI%203%20%C2%B7%20.NET%2010;Status%20%C2%B7%20Files%20%C2%B7%20Docker%20%C2%B7%20SSH%20%C2%B7%20Network)

# NAS 工具箱 NasToolbox

**面向家用 NAS 的 Windows 桌面管理工具** -- 基于 WinUI 3 / .NET 10 全新打造,非打包(Unpackaged)形态,解压即用

![License](https://img.shields.io/badge/License-GPLv3-blue.svg) ![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet) ![WinUI](https://img.shields.io/badge/WinUI-3-0078D4) ![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4?logo=windows) ![Version](https://img.shields.io/badge/Version-1.0.0-orange) ![Stars](https://img.shields.io/github/stars/Fontaine-superstar/NasToolBox?color=ffcb47&style=flat)

[快速开始](#快速开始) · [功能总览](#功能总览) · [NAS 端依赖](#nas-端依赖) · [使用须知](#使用须知) · [问题反馈](https://github.com/Fontaine-superstar/NasToolBox/issues)

> [!NOTE]
> NAS 状态、Docker、文件管理、终端等远程能力都走 SSH —— 请先在 NAS 上开启 SSH 服务,并在「设备管理」中添加设备、填好账号密码。

* * *

## 目录

- 功能亮点
- 功能总览
- NAS 端依赖
- 快速开始
- 系统兼容性
- 目录结构
- 数据存放
- 隐私与安全
- 使用须知
- 许可证
- 

* * *

## 功能亮点

**设备台账** 一台 NAS 一张卡:名称 / IP / MAC / Web 端口 / SSH 账号;一键 Ping、打开 Web 后台、WOL 唤醒、打开 SMB 共享

**实时状态快照** 一次 SSH 往返批量采集系统 / 运行 / 内存 / 硬盘 / 网络 / 磁盘空间;点单块硬盘可看完整 SMART 详情

**SMB 文件管理** 浏览共享与目录、上传下载、新建与删除;本地文件拖进列表即上传,列表文件拖到资源管理器即下载;右键可把共享映射为本地网络驱动器

**Docker 远程管理** 查看 / 启动 / 停止 / 重启 / 删除容器、查看日志、镜像与 compose 项目列表,命令全部经 SSH 下发,不占用 NAS 端口

**内嵌 SSH 终端** 基于 xterm.js + WebView2 的交互式终端,支持 sudo 提权与 root 登录通道

**网络诊断** Ping 报告(丢包率 / 最短 / 最长 / 平均延迟)、NAS 外网连通性测试(命令在 NAS 上执行)、常用 NAS 端口探测、DNS 解析、内网测速

**应用内 Web 管理** WebView2 直接打开 NAS 后台,支持后退 / 前进 / 刷新,不用再切浏览器

**环境自检与一键安装** 首次连接自动探测 NAS 端依赖(`smartctl` / `lsblk` / `dmidecode` …),缺什么列什么,有提权时可一键补装

* * *

## 功能总览

| 页面 | 功能 |
|---|---|
| 首页 | 本机摘要(主机名 / IPv4 / 网关 / CPU / 内存 / 开机时长,WMI 后台查询),NAS 设备在线速览 + 一键检测 |
| 设备管理 | NAS 台账(名称 / IP / MAC / Web / SSH 账号),增删改 + 一键 Ping、打开 Web 管理、WOL 唤醒、打开 SMB 共享;**SSH 管理**:内置 SSH 终端、SSH 状态一键体检、自定义命令执行、**环境自检与依赖安装** |
| NAS 状态 | 远程采集运行快照:系统 / 运行(在线时长、负载)/ 内存 / 硬盘(容量、类型、接口、型号、SMART 健康,点击单盘查看完整 SMART 详情)/ 网络(内网 IP、公网 IP、网关、DNS、网卡速率)/ 磁盘空间(挂载点用量) |
| 文件管理 | 经 SMB 浏览共享与目录,支持上传、下载、新建文件夹、删除;文件可**拖入即上传**、**拖出即下载**;右键共享或文件夹可**映射为本地网络驱动器**(也可一键断开) |
| Docker | 远程管理 NAS 上的容器(查看 / 启动 / 停止 / 重启 / 日志等)与 compose 项目 |
| 网络诊断 | Ping 报告(丢包率 / 最短 / 最长 / 平均延迟)、NAS 外网连通性测试、常用 NAS 端口探测(SSH 22 / SMB 445 / 群晖 5000 等)、DNS 解析、网速测试 |
| Web 管理 | 左侧选择 NAS 设备,应用内(WebView2)直接打开其 Web 管理界面,支持后退 / 前进 / 刷新 |
| SSH 终端(Shell) | 基于 xterm.js + WebView2 的交互式终端,内嵌于应用内 |
| 关于 | 功能说明 / 数据存放位置 / 使用须知 |

<details>
<summary>点击展开各页面详细说明</summary>

### 设备管理

- 设备字段:显示名称、主机地址、MAC、Web 端口、SSH 端口 / 账号 / 认证方式(密码或私钥)、备注
- 权限模式:普通用户 / sudo 提权 / root 登录,决定采集类命令能否拿到完整信息
- 行内操作:编辑、删除、**环境自检**、Ping、打开 Web、WOL 唤醒、打开 SMB
- 保存时可选「测试连接」,成功后自动触发一次 NAS 端依赖自检

### NAS 状态

- 一条 SSH 命令组批量取回所有指标,不是逐条往返,采集快
- 硬盘卡:`lsblk` 拿容量 / 类型(SSD·HDD)/ 接口 / 型号,`smartctl -H` 拿健康自评;点击任一硬盘弹出 SMART 详情(温度、通电时间、属性表、厂商告警)
- 内存行显示内存条型号(需 root;虚拟机无 DMI 时显示「—」属正常)
- 公网 IP 通过 NAS 侧 curl 查询,避免拿到本机出口 IP

### 文件管理

- 共享列表并入主列表:双击进入共享,`UpBtn` 回到共享列表
- 拖拽:外部文件拖入列表即上传;列表文件拖到资源管理器 / 桌面即下载
- 右键菜单:映射为网络驱动器、在资源管理器中打开、断开映射、复制网络路径、下载所选、删除所选
- 映射使用 `WNetAddConnection2`,凭据复用该设备已保存的 SSH 账号密码,不经命令行、不落盘

### Docker

- 容器:列表 / 启动 / 停止 / 重启 / 删除 / 日志 / 详情
- 镜像:列表 / 拉取 / 删除 / 导入离线 tar
- compose 项目列表(优先 `docker compose`,回退 `docker-compose`)
- 需要 SSH 登录用户在 NAS 的 `docker` 组里,否则命令无权限

### 网络诊断

- Ping:目标、次数、丢包率与三档延迟
- NAS 外网诊断:选一台 NAS,**在 NAS 上执行** ping,判断 NAS 自己能不能出网
- 端口探测:常用 NAS 端口一键 TCP 连通性扫描
- DNS 解析:域名 → IP
- 网速测试:识别内网自建测速容器(见下方 `speedtest-x`)

</details>

* * *

## NAS 端依赖

> 项目只在 NAS 上执行只读命令与 docker 命令,不装服务、不改配置 —— 但下列命令必须存在

| 包 | 项目里谁在用 | 缺了会怎样 |
|---|---|---|
| `openssh-server` | 全部远程功能走 SSH | 全部不可用 |
| `sudo` | 设备权限三态(sudo / root 登录) | 只能普通用户,部分采集拿不到 |
| `iproute2` | `ip -o -4 addr`、`ip route`(网卡、IP、网关、速率) | 网络卡片全空 |
| `util-linux` | `lsblk`(硬盘列表、容量、接口、型号) | 硬盘卡片为空 |
| `smartmontools` | `smartctl -H` / `-a`(健康自评 + SMART 详情) | 健康度「未知」,详情打不开 |
| `dmidecode` | `-t 17`(内存型号,需 root) | 内存型号显示「—」 |
| `pciutils` | `lspci`(系统卡片 GPU 行) | GPU 显示「—」 |
| `curl` | 公网 IP 查询、外网诊断 | 公网 IP 显示「—」 |
| `iputils-ping` | `ping -c N -W 2`(NAS 外网诊断) | 连通性测试失败 |
| `samba` | 文件管理页与共享枚举(445) | 文件管理、共享列表不可用 |
| `docker-ce` | Docker 页:`ps / start / stop / rm / logs / images / pull / run` | Docker 页整页报错 |
| `docker-compose-plugin` | `docker compose version`(compose 项目列表) | compose 列表报错 |

`uptime`、`nproc`、`df`、`cat`、`grep`、`sed`、`awk`、`uname` 属 coreutils / procps / gawk,主流发行版自带,无需安装。

一条命令装齐(Debian / Ubuntu):

```bash
sudo apt install -y openssh-server sudo iproute2 util-linux smartmontools \
  dmidecode pciutils curl iputils-ping samba docker-ce docker-compose-plugin
```

### 一键自检

不用手敲:「设备管理」里点设备行的 **环境自检**(或编辑设备后点测试连接),程序会用单条 SSH 命令探测上述组件,列出缺失项及其影响;设备开了 sudo 提权或 root 登录时,可直接点按钮让程序装,装完自动复检。群晖 / UnRAID / TrueNAS 这类没有通用包管理器的系统,只提示不硬装。

### 还需要两条配置

```bash
sudo smbpasswd -a 你的用户名       # 密码需与 SSH 登录密码一致,文件管理页复用 SSH 密码连 SMB
sudo usermod -aG docker 你的用户名  # 否则 docker 命令无权限,重连 SSH 生效
```

防火墙放行 **445**(SMB)与你给测速容器映射的宿主端口。

### 测速容器(唯一被代码写死的镜像)

```bash
docker run -d --name speedtest-x --restart unless-stopped -p 8081:80 badapple9/speedtest-x
```

容器名 `speedtest-x` 由 `SpeedtestService` 硬编码,部署后内网测速页会自动扫到它并解析端口映射。除此之外项目**不预置任何镜像模板**,Docker 页部署什么完全由你在界面里填。`img/` 目录可放离线镜像 tar(体积过大未纳入仓库),无外网时用 `docker load` 导入。

* * *

## 快速开始

```powershell
git clone https://github.com/Fontaine-superstar/NasToolBox.git
cd NasToolBox
dotnet run            # 非打包(Unpackaged)模式直接运行
```

构建便携版(自带 WinAppSDK 运行时,解压即用):

```powershell
dotnet publish -c Release -r win-x64 -o publish\x64
# 同理 win-x86 / win-arm64
```

环境要求:

- .NET 10 SDK(`winget install Microsoft.DotNet.SDK.10`)
- Visual Studio 2022 17.14+ 或 Rider / VS Code(C# Dev Kit)
- Windows 10 19041+ / Windows 11

* * *

## 系统兼容性

| 平台 | 支持状态 |
|---|---|
| x64 (Intel/AMD 64 位) | ✅ 完全支持 |
| ARM64 (高通骁龙等) | ✅ 完全支持 |
| x86 (Intel/AMD 32 位) | ✅ 完全支持 |

| Windows 版本 | 支持状态 |
|---|---|
| Windows 11 | ✅ 完全支持 |
| Windows 10 19041+ | ✅ 完全支持 |
| Windows 10 19041 以下 | ❌ 不支持(WinAppSDK 最低要求) |

* * *

## 目录结构

```
├── App.xaml(.cs) / MainWindow.xaml(.cs)   入口 + 标题栏/导航骨架
├── Pages/                                 各功能页面(见功能总览)
├── Models/                                数据模型(NasDevice / DiskInfo / FileEntry / DockerContainer / DependencyReport …)
├── Services/
│   ├── NasStatusService      NAS 状态批量采集(命令组一次 SSH 往返)
│   ├── SmartService          SMART 详情采集与解析
│   ├── DependencyService     NAS 端依赖自检与按发行版安装
│   ├── DriveMapService       网络驱动器映射与断开(WNet)
│   ├── Smb/                  SMB 会话(WNet)与 UNC 文件操作
│   ├── Ssh/                  SSH 连接、命令执行、root 提权 shell、SFTP、终端会话
│   ├── NetworkService        Ping / TCP 端口探测 / DNS / 本机网络信息
│   ├── WakeOnLanService      魔术包(全局广播 + 子网定向广播)
│   ├── SmbService            NetShareEnum 共享枚举(P/Invoke)
│   └── NasDeviceStore / DeviceStatusStore / SecretProtector …
├── Converters/Conv.cs        x:Bind 函数绑定辅助(字节格式化 / 端口状态 / 盘符等)
├── Assets/Terminal/          xterm.js 前端终端(vendored,MIT)
└── img/                      离线容器镜像 tar 放置目录
```

* * *

## 数据存放

- 设备台账:`%LocalAppData%\NasToolbox\devices.json`(首次保存设备时创建)
- 设备状态缓存:`%LocalAppData%\NasToolbox\device_status.json`
- SSH 主机指纹:`%LocalAppData%\NasToolbox\known_hosts.json`

* * *

## 隐私与安全

- **SSH 密码**使用 Windows DPAPI(`SecretProtector`)加密存储,绑定当前用户与本机;把 `devices.json` 拷到别处无法解出。
- SMB 会话与网络驱动器映射复用已保存的 SSH 账号密码,密码**只存在于内存**,不会写入磁盘、也不会作为命令行参数出现。
- SSH 主机密钥按设备指纹校验,避免中间人风险。
- 所有远程操作均需用户显式触发,程序不会在后台主动连接设备。

* * *

## 使用须知

- **Wake-on-LAN**:需要 NAS 的 BIOS/UEFI 与网卡开启 WOL;部分路由器/AP 会拦截广播帧,已同时向 `255.255.255.255` 与各网卡子网定向广播地址发包以提高成功率。
- **SMB**:文件管理复用设备的 SSH 账号密码,所以 NAS 上要用 `smbpasswd -a` 把同一用户加进 Samba 且**设成相同密码**;共享枚举依赖目标允许枚举,群晖等默认禁用匿名访问,提示「访问被拒绝」时先在资源管理器打开 `\\NAS的IP` 登录一次。
- **硬盘 SMART** 需要目标 NAS 装有 `smartmontools` 且运行用户有检测权限(走设备配置的 root / sudo 提权通道);未安装时该盘显示「未知」,容量与型号不受影响。
- **端口探测**只是 TCP 连通性测试,不代表服务一定正常。
- **SSH**:群晖等 NAS 需先在管理后台开启 SSH;项目使用 SSH.NET 2026(新包 ID),老旧的 `Renci.SshNet` 已不支持现代算法,请勿降级。
- `img/` 用于存放离线容器镜像包(如 speedtest-x),因体积过大未纳入仓库;分发前请确认镜像自身授权。

* * *

## 许可证

本项目采用 **GPL-3.0** 许可证,详见 [`LICENSE`](LICENSE)。

- 源代码可自由使用、修改和分发
- 衍生作品必须以相同协议开源

致谢:项目骨架衍生自 [luolangaga/tubatools](https://github.com/luolangaga/tubatools)(TubaWinUi3,GPL-3.0);内嵌的 [xterm.js](https://github.com/xtermjs/xterm.js)(MIT)位于 `Assets/Terminal/`。

[![Star History Chart](https://api.star-history.com/svg?repos=Fontaine-superstar/NasToolBox&type=Date)](https://star-history.com/#Fontaine-superstar/NasToolBox&type=Date)

**如果觉得有用,给个 Star 吧!**
