![NAS 工具箱 Typing](https://readme-typing-svg.demolab.com?font=Fira%20Code&size=26&pause=1000&color=0078D4&center=true&vCenter=true&width=600&lines=Home%20NAS%20Toolbox%20for%20Windows;WinUI%203%20%C2%B7%20.NET%2010;Status%20%C2%B7%20Files%20%C2%B7%20Docker%20%C2%B7%20SSH%20%C2%B7%20Network)

# NAS 工具箱 NasToolbox

**面向家用 NAS 的 Windows 桌面管理工具** -- 基于 WinUI 3 / .NET 10 全新打造,非打包(Unpackaged)形态,解压即用

![License](https://img.shields.io/badge/License-GPLv3-blue.svg) ![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet) ![WinUI](https://img.shields.io/badge/WinUI-3-0078D4) ![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4?logo=windows) ![Version](https://img.shields.io/badge/Version-beta1.0.0-orange) ![Stars](https://img.shields.io/github/stars/Fontaine-superstar/NasToolBox?color=ffcb47&style=flat)

[快速开始](#快速开始) · [功能总览](#功能总览) · [NAS 端依赖](#nas-端依赖) · [使用须知](#使用须知) · [问题反馈](https://github.com/Fontaine-superstar/NasToolBox/issues)

> [!注意]
> 本工具的操作功能都基于SSH来实现，请确保NAS端安装并开启了SSH功能

* * *

## 目录

- 功能总览
- 功能详细


* * *

## 功能概览

| 功能 | 说明 |
|---|---|
| 设备管理 | 快速切换需要管理的 NAS，有编辑配置、环境自检、删除设备的功能，建议在权限与高级中选择 root 会话登录，避免部分功能不可用。支持维护多台 NAS，快速切换要管控的目标 NAS |
| NAS 状态 | 可显示 NAS 系统信息，运行状态，内存使用，硬盘信息，网络信息，存储空间信息 |
| Web 管理 | 快速打开 NAS 的 Web 界面，须在「设备管理 - 添加设备 / 编辑设备」中填写 Web 端口 |
| 终端 | 内嵌交互式 SSH 命令行终端 |
| 网络诊断 | 包含 Ping 连通检测、NAS 网络连通测试，本地网络测速，排查网络故障 |
| 文件管理 | 基于 SMB 的文件管理，可以快速查看文件，也可右键快速映射为网络驱动器 |
| Docker | 完整的 Docker 管理功能，包含概览、容器、compose、镜像、docker 网络。支持导入本地镜像，将本地镜像放置到 `/img` 文件夹可以安装本地镜像 |

* * *



<details>
<summary>点击展开各页面详细说明</summary>

### 首页
   ![屏幕截图 2026-09-24 191804.png](photo/%E5%B1%8F%E5%B9%95%E6%88%AA%E5%9B%BE%202026-09-24%20191804.png)
    显示本机信息与已添加的NAS信息
### 设备管理
![屏幕截图 2026-09-24 191816.png](photo/%E5%B1%8F%E5%B9%95%E6%88%AA%E5%9B%BE%202026-09-24%20191816.png)
- 设备字段:显示名称、主机地址、MAC、Web 端口、SSH 端口 / 账号 / 权限模式、备注
- 权限模式:普通用户 / root 会话,决定采集类命令能否拿到完整信息
- 行内操作:编辑、删除、环境自检、Ping、打开 Web、WOL 唤醒、打开 SMB
- 保存时可选「测试连接」,成功后自动触发一次 NAS 端依赖自检

### NAS 状态
![屏幕截图 2026-09-24 191820.png](photo/%E5%B1%8F%E5%B9%95%E6%88%AA%E5%9B%BE%202026-09-24%20191820.png)


采集由一条 SSH 命令组一次性下发,命令与采集内容对照如下:

| # | 命令 | 采集内容 |
| --- | --- | --- |
| 1 | `uptime` | 在线时长、负载 |
| 2 | `nproc 2>/dev/null \|\| grep -c ^processor /proc/cpuinfo` | CPU 核心数 |
| 3 | `grep -E 'MemTotal\|MemAvailable\|MemFree' /proc/meminfo` | 内存总量 / 可用 |
| 4 | `df -hP -x tmpfs -x devtmpfs -x overlay 2>/dev/null \|\| df -hP ... \|\| df -h` | 磁盘空间(多级回退兼容) |
| 5 | `cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null \|\| echo -1` | 主温度 |
| 6 | `cat /proc/uptime` | 开机秒数(走秒) |
| 7 | `for n in /sys/class/net/*; do ... echo "IF1\|$i\|MAC\|状态\|速率\|rx_bytes\|tx_bytes"; done` | 网卡列表(第一次采样) |
| 8 | `sleep 1` | 间隔 1 秒(算实时速率用) |
| 9 | `for n in /sys/class/net/*; do ... echo "IF2\|$i\|rx\|tx"; done` | 网卡流量(第二次采样) |
| 10 | `ip -o -4 addr show 2>/dev/null` | 各网卡 IPv4 |
| 11 | `ip route show default 2>/dev/null \| head -1` | 默认网关 |
| 12 | `grep nameserver /etc/resolv.conf 2>/dev/null` | DNS |
| 13 | `curl -s --max-time 3 https://ip.3322.net \|\| curl ... api.ipify.org \|\| wget ...(共 4 级回退,3 秒超时)` | 公网 IP(NAS 侧出口查询) |
| 14 | `grep -m1 '^model name' /proc/cpuinfo \|\| grep -m1 '^Hardware' ... \|\| uname -m` | CPU 型号(x86 / ARM 回退) |
| 15 | `for z in /sys/class/thermal/thermal_zone*; ...; for s in /sys/class/hwmon/hwmon*; ...` | 全部温度传感器(thermal zone + hwmon) |
| 16 | `lspci 2>/dev/null \| grep -Ei 'vga\|3d controller\|display controller' \| head -4` | GPU 型号 |
| 17 | `PATH=$PATH:/usr/sbin:/sbin; dmidecode -t 17 ...(需 root)` | 内存条型号 |
| 18 | `lsblk -Jdnb -o NAME,SIZE,ROTA,TRAN,MODEL; echo ---SMART---; for d in $(lsblk ...); do ... smartctl -H /dev/$d ...; done` | 物理硬盘列表 + SMART 健康自评 |

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

| 包 | 需求项目                                                           | 缺失影响           |
|---|----------------------------------------------------------------|----------------|
| `openssh-server` | 全部远程功能走 SSH                                                    | 全部不可用          |
| `sudo` | root 会话模式建立提权会话                                          | 只能普通用户,部分采集拿不到 |
| `iproute2` | `ip -o -4 addr`、`ip route`(网卡、IP、网关、速率)                        | 网络卡片全空         |
| `util-linux` | `lsblk`(硬盘列表、容量、接口、型号)                                         | 硬盘卡片为空         |
| `smartmontools` | `smartctl -H` / `-a`(健康自评 + SMART 详情)                          | 健康度「未知」,详情打不开  |
| `dmidecode` | `-t 17`(内存型号,需 root)                                           | 内存型号显示「—」      |
| `pciutils` | `lspci`(系统卡片 GPU 行)                                            | GPU 显示「—」      |
| `curl` | 公网 IP 查询、外网诊断                                                  | 公网 IP 显示「—」    |
| `iputils-ping` | `ping -c N -W 2`(NAS 外网诊断)                                     | 连通性测试失败        |
| `samba` | 文件管理页与共享枚举(445)                                                | 文件管理、共享列表不可用   |
| `docker-ce` | Docker 页:`ps / start / stop / rm / logs / images / pull / run` | Docker 页整页报错   |
| `docker-compose-plugin` | `docker compose version`(compose 项目列表)                         | compose 列表报错   |




* * *

## 许可证

本项目采用 **GPL-3.0** 许可证,详见 [`LICENSE`](LICENSE)。

- 源代码可自由使用、修改和分发
- 衍生作品必须以相同协议开源

致谢:项目骨架衍生自 [luolangaga/tubatools](https://github.com/luolangaga/tubatools)(TubaWinUi3,GPL-3.0);内嵌的 [xterm.js](https://github.com/xtermjs/xterm.js)(MIT)位于 `Assets/Terminal/`。

[![Star History Chart](https://api.star-history.com/svg?repos=Fontaine-superstar/NasToolBox&type=Date)](https://star-history.com/#Fontaine-superstar/NasToolBox&type=Date)

**如果觉得有用,给个 Star 吧!**
