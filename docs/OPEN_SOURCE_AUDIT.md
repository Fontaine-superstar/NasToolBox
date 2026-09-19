# NasToolbox 开源依赖审计报告

- **审计对象**:`D:\elements\docs\NasToolbox`(NasToolbox 0.2.0,WinUI 3 / .NET 10)
- **审计日期**:2026-09-19
- **信息来源**:`NasToolbox.csproj`、`obj/project.assets.json`(NuGet 实际解析结果)、`bin/Debug/.../win-x64/` 构建产物、`Assets/Terminal/`、`img/`、`Metadata/tools.json`、`Pages/AboutPage.xaml`、`docs/` 三份文档
- **方法**:静态分析,未联网验证包哈希;许可证结论以各项目官方仓库/包内 LICENSE 为准

---

## 1. 摘要

NasToolbox 是一个**依赖面非常窄**的项目:自研代码占绝对主体,外部开源组件分四类——

| 类别 | 数量 | 是否随程序分发 |
|---|---|---|
| 直接 NuGet 依赖 | 4 包 + 1 框架引用 | 是 |
| 传递 NuGet 依赖 | 16 包 | 是 |
| 内嵌前端开源库 | 2 个(xterm.js 系列) | 是(vendored 副本) |
| 随包分发的开源产物 | 1 个(speedtest-x 容器镜像 tar,约 465 MB) | 是 |
| 协议兼容的开源项目(未捆绑) | 3 个 | 否 |
| 上游衍生项目 | 1 个(tubatools) | —(已在关于页致谢) |
| 仅清单引用的第三方工具 | 8 个 | 否(Tools/ 为空) |

**总体结论**:没有 GPL/AGPL 类"传染性"组件混入二进制分发物,当前依赖结构对计划的 GPL-3.0 开源没有阻碍;真正的缺口是**合规文件缺失**(LICENSE / 第三方声明)与 **xterm.js 版本未记录**。

---

## 2. 直接依赖(csproj 显式声明)

| 包 | 版本 | 用途 | 许可证 |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.2.0 | WinUI 3、TitleBar、AppWindow、WebView2 宿主;元包 | MIT |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.7705 | 构建工具链 | 微软官方,以包内许可为准 |
| `SSH.NET` | 2026.0.0 | SSH / SFTP / SCP 库(程序集名仍为 `Renci.SshNet.dll`) | MIT |
| `System.Management` | 10.0.8 | 首页 WMI 查询 | MIT(.NET Foundation) |
| `Microsoft.WindowsDesktop.App.WindowsForms` | FrameworkReference | 仅取 `System.Drawing`(图标提取)与 `OpenFileDialog` | .NET 运行时一部分 |

要点:

- **SSH.NET 必须用新包 ID**。旧包 ID `Renci.SshNet` 停在 1.0.0,只支持 OpenSSH 8.2 之前的算法,现代 NAS 握手会被断开;2026.0.0 修复 CVE-2026-85756 / CVE-2026-48798(仅影响本项目未使用的 ScpClient)。
- **WebView2 未单独引包**。代码中的 `Microsoft.Web.WebView2.Core` 由 WindowsAppSDK 间接提供,csproj 刻意不重复声明以免版本冲突。

## 3. 传递依赖(共 16 个,均非主动引入)

来源:`obj/project.assets.json` 的实际解析结果。

**经 `Microsoft.WindowsAppSDK` 2.2.0 引入(12 个):**

| 包 | 版本 |
|---|---|
| Microsoft.WindowsAppSDK.Base | 2.0.4 |
| Microsoft.WindowsAppSDK.Foundation | 2.1.0 |
| Microsoft.WindowsAppSDK.WinUI | 2.2.1 |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.0.15 |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 |
| Microsoft.WindowsAppSDK.Runtime | 2.2.0 |
| Microsoft.WindowsAppSDK.AI | 2.2.3 |
| Microsoft.WindowsAppSDK.ML | 2.1.70 |
| Microsoft.Windows.AI.MachineLearning | 2.1.70 |
| Microsoft.Web.WebView2 | 1.0.3719.77 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 |

**再经 AI/ML 拆分包引入(2 个):**

| 包 | 版本 | 说明 |
|---|---|---|
| System.Numerics.Tensors | 9.0.0 | 由 `Microsoft.Windows.AI.MachineLearning` 依赖 |
| Microsoft.Extensions.DependencyInjection.Abstractions / Logging.Abstractions | 8.0.2 / 8.0.3 | 日志抽象链 |

**经 `SSH.NET` 引入(1 个):**

| 包 | 版本 | 说明 |
|---|---|---|
| BouncyCastle.Cryptography | 2.7.0 | SSH.NET 的加密算法后端 |

### 3.1 值得注意的现象:AI 组件"白拉进包"

`bin/.../win-x64/` 构建产物中实际出现了 `Microsoft.ML.OnnxRuntime.dll`、`onnxruntime.dll`、`DirectML.dll`、`Microsoft.Windows.AI.*.dll` 等——本项目**没有任何 AI 功能**,它们是 WindowsAppSDK 2.2 元包默认携带的。这意味着安装包体积里有一块与功能无关的 AI 运行时。如果未来在意体积,可关注 WindowsAppSDK 后续版本是否提供裁剪手段(当前不要自行删 DLL,可能破坏 WinAppSDK 运行时加载)。

## 4. 内嵌前端开源资源(2 个,vendored)

`Assets/Terminal/`,经 WebView2 虚拟主机(`host.html`)加载,服务于「SSH 终端」页:

| 文件 | 项目 | 许可证 | 版本 |
|---|---|---|---|
| xterm.js / xterm.css | [xterm.js](https://github.com/xtermjs/xterm.js)(含 Christopher Jeffrey 的 term.js 前身版权声明) | MIT(文件头已验证) | **未记录**,特征串探测为 5.0–5.3.x 区间(含 `scrollOnUserInput`、`windowsPty`,不含 5.4 才加入的 `rescaleOverlappingGlyphs`) |
| xterm-addon-fit.js | xterm.js 官方 fit addon | MIT | 同上,未记录 |

> 风险:vendored 副本无版本锚点,后续做安全比对或升级时无据可查。**建议**在 `Assets/Terminal/` 增加 `VERSION.txt`,记录来源 URL、版本号与拷贝日期。

## 5. 随包分发的开源产物(1 个)

| 文件 | 项目 | 用途 | 分发方式 |
|---|---|---|---|
| `img/badapple9_speedtest-x(latest).tar`(约 465 MB) | [speedtest-x](https://github.com/BadApple9/speedtest-x)(基于 LibreSpeed) | 局域网测速:导入 NAS 的 Docker 后由「网络诊断」页自动发现并测速 | 随构建复制到输出目录;**仓库分发时应排除,改为 Release 附件**(既定方案) |

这是部署物料而非代码依赖,但它在 `dotnet publish` 产物内,分发软件包时等于在再分发该镜像,需在第三方声明中列明。

## 6. 协议兼容的开源项目(未捆绑,运行期对接)

「网络诊断」页的测速实现(SpeedtestService / NetworkToolsPage 注入脚本)按**协议**识别三类开源测速项目,容器内跑哪个都能用:

| 项目 | 协议特征 |
|---|---|
| speedtest-x / LibreSpeed | `startStop()`、`garbage.php`、`empty.php`、`getIP.php`(backend/ 子目录兼容) |
| OpenSpeedTest | `startButtonDesk`、`downResult`、`upResult`、`pingResult`、`YourIP` |

这三者只要求 NAS 上自行部署,程序不携带其代码,无再分发义务;仅在文档中提及。

## 7. 上游衍生与致谢

- 项目 UI 骨架重构自 **TubaWinUi3**([github.com/luolangaga/tubatools](https://github.com/luolangaga/tubatools)),GPL-3.0。
- `Pages/AboutPage.xaml` 已放置致谢超链接("UI 模式参考:TubaWinUi3")——**衍生作品署名义务已履行**。
- 按既定方案,本项目将以 **GPL-3.0** 开源(上游为 GPL-3.0,衍生作品必须同协议)。

## 8. 仅清单引用的第三方工具(8 个,不随包分发)

`Metadata/tools.json` 登记、由「工具库」页识别启动的绿色工具。`Tools/` 目录当前只有占位说明文件,**没有任何实体工具被捆绑**:

WinSCP、FileZilla、PuTTY、WinDirStat、WizTree、qBittorrent、rclone、Angry IP Scanner。

> 这些由最终用户自行放置,程序只做"识别 + 启动",不构成再分发;但注意 WizTree 为专有软件,若未来官方仓库提供"一键下载工具"之类功能,需重新评估各工具 EULA。

## 9. 合规现状与建议

### 9.1 缺口

1. **根目录无 `LICENSE`**——GPL-3.0 计划的第一阻塞项。
2. **无 `THIRD-PARTY-NOTICES` / `NOTICE`**——MIT 组件(BouncyCastle、SSH.NET、xterm.js、WindowsAppSDK)的版权声明保留义务需要一份汇总文件来履行。
3. **xterm.js vendored 副本未记录版本与来源**。
4. **文档表述偏差**:
   - `README.md`:"网络功能全部基于 .NET BCL,零第三方依赖" —— 与 SSH.NET 的存在直接矛盾(README 已知过时,待重写);
   - `docs/DEVELOPER_GUIDE.md`:"唯一第三方网络库是 SSH.NET" —— 未提 BouncyCastle / WebView2 / xterm.js,严格意义上不完整。
5. **构建产物含未使用的 AI 运行时**(OnnxRuntime / DirectML),非合规问题,但是体积与审查噪音。

### 9.2 建议行动(按优先级)

| # | 动作 | 说明 |
|---|---|---|
| 1 | 根目录添加 `LICENSE`(GPL-3.0 全文) | 开源前置条件 |
| 2 | 添加 `THIRD-PARTY-NOTICES.md` | 汇总第 2–5 节组件的版权与许可声明;可直接复制各包内 LICENSE 文本 |
| 3 | `Assets/Terminal/VERSION.txt` | 记录 xterm.js 来源 URL、版本、拷贝日期 |
| 4 | 重写 README 依赖表述 / 同步 DEVELOPER_GUIDE | 消除"零依赖"与事实的差异 |
| 5 | 发布脚本确认 `img/*.tar` 不进仓库 | 改为 Release 附件(已定案,待执行) |
| 6 | (可选)关注 WindowsAppSDK 后续版本的 AI 组件裁剪能力 | 降低安装体积 |

## 10. 附:实际随程序分发的第三方 DLL(构建产物验证)

`bin/Debug/net10.0-windows10.0.26100.0/win-x64/` 中与本项目代码一同输出的非自研 DLL(节选核心项):

- `Renci.SshNet.dll`(SSH.NET 2026.0.0)、`BouncyCastle.Cryptography.dll`(2.7.0)
- `System.Management.dll`、`System.Numerics.Tensors.dll`
- `Microsoft.Web.WebView2.Core.dll`(+ Projection)、`WebView2Loader.dll`
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll`、`Microsoft.Extensions.Logging.Abstractions.dll`
- WindowsAppSDK 运行时全家桶:`Microsoft.WinUI.dll`、`Microsoft.UI.*.dll`、`Microsoft.WindowsAppRuntime.dll`、`DWriteCore.dll`、`Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll` 及 AI 相关(`onnxruntime.dll`、`DirectML.dll`、`Microsoft.Windows.AI.*.dll`、`Microsoft.Windows.Widgets.dll` 等)

---

*本报告由静态审计生成;提交开源前建议用 `dotnet list package --include-transitive` 复核一次版本快照,并核实各 NuGet 包内 LICENSE 原文。*
