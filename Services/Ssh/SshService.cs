using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NasToolbox.Models;
using Renci.SshNet;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// SSH 远程执行入口:连接复用、真超时中断、sudo 提权、连接诊断与设备识别。
/// 连接建立失败抛 <see cref="SshConnectException"/>(带结构化原因);
/// 命令执行期的问题以 <see cref="SshCommandResult.ErrorMessage"/> / TimedOut 返回,不抛异常。
/// </summary>
public static class SshService
{
    /// <summary>「SSH 状态」一键体检脚本:主机 / 内存 / 磁盘 / Docker 容器。</summary>
    public const string StatusScript =
        "echo === 主机 ===; hostname; uptime; " +
        "echo; echo === 内存 ===; free -m 2>/dev/null || head -3 /proc/meminfo; " +
        "echo; echo === 磁盘 ===; df -h -x tmpfs -x devtmpfs 2>/dev/null || df -h; " +
        "echo; echo === Docker 容器 ===; docker ps --format 'table {{.Names}}\t{{.Status}}' 2>/dev/null || echo 未安装 docker 或当前用户无权限";

    /// <summary>系统识别脚本:内核 / 架构 / os-release / 各品牌特征文件。</summary>
    public const string DetectScript =
        "uname -s -r; " +
        "echo ARCH=$(uname -m); " +
        "echo HOST=$(hostname); " +
        "echo OS_NAME=$(cat /etc/os-release 2>/dev/null | grep -m1 '^PRETTY_NAME=' | cut -d= -f2 | tr -d '\"'); " +
        "echo OS_ID=$(cat /etc/os-release 2>/dev/null | grep -m1 '^ID=' | cut -d= -f2 | tr -d '\"'); " +
        "echo OS_VER=$(cat /etc/os-release 2>/dev/null | grep -m1 '^VERSION_ID=' | cut -d= -f2 | tr -d '\"'); " +
        "echo MARK_SYNO=$([ -f /etc/synoinfo.conf ] && echo 1 || echo 0); " +
        "echo MARK_QNAP=$([ -f /etc/config/uLinux.conf ] && echo 1 || echo 0); " +
        "echo MARK_UNRAID=$([ -f /etc/unraid-version ] && echo 1 || echo 0); " +
        "echo MARK_TRUENAS=$([ -f /etc/truenas_version ] && echo 1 || echo 0); " +
        "echo MARK_MIDCLT=$(command -v midclt >/dev/null 2>&1 && echo 1 || echo 0); " +
        "echo MARK_OPENWRT=$([ -f /etc/openwrt_release ] && echo 1 || echo 0)";

    /// <summary>TCP 预检超时(毫秒)。</summary>
    private const int TcpProbeTimeoutMs = 5000;

    /// <summary>
    /// 执行一条命令。timeoutMs 为 0 时使用设备配置的超时;sudo 为 null 时跟随设备配置。
    /// </summary>
    public static async Task<SshCommandResult> RunAsync(
        NasDevice device,
        string command,
        int timeoutMs = 0,
        bool? sudo = null,
        CancellationToken ct = default)
    {
        var results = await RunCoreAsync(device, new[] { command }, timeoutMs, sudo, ct).ConfigureAwait(false);
        return results.Count > 0
            ? results[0]
            : new SshCommandResult { Command = command, ErrorMessage = "命令为空。" };
    }

    /// <summary>
    /// 在同一条连接上依次执行多条命令(省去重复握手),返回与输入顺序一致的结果。
    /// </summary>
    public static Task<List<SshCommandResult>> RunManyAsync(
        NasDevice device,
        IEnumerable<string> commands,
        int timeoutMs = 0,
        bool? sudo = null,
        CancellationToken ct = default)
        => RunCoreAsync(device, commands, timeoutMs, sudo, ct);

    /// <summary>
    /// 连接诊断:先 TCP 预检(区分域名解析失败 / 端口未开放 / 超时),再做 SSH 握手与认证。
    /// 使用独立连接,不占用命令执行用的连接缓存。
    /// </summary>
    public static async Task<SshConnectReport> TestAsync(
        NasDevice device,
        bool trustNewHostKey = false,
        CancellationToken ct = default)
    {
        var configError = Validate(device);
        if (configError is not null)
        {
            return new SshConnectReport
            {
                Ok = false,
                Failure = SshConnectFailure.NotConfigured,
                Summary = configError,
                Hint = HintFor(SshConnectFailure.NotConfigured),
            };
        }

        var host = device.Host.Trim();
        var port = NormalizePort(device);

        // 阶段一:域名解析
        var sw = Stopwatch.StartNew();
        IPAddress? ip;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            ip = addrs.FirstOrDefault(a =>
                a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        }
        catch (Exception ex)
        {
            return Fail(SshConnectFailure.DnsFailed, $"无法解析主机「{host}」。",
                "", ex.Message, sw.Elapsed, TimeSpan.Zero);
        }

        if (ip is null)
        {
            return Fail(SshConnectFailure.DnsFailed, $"主机「{host}」没有可用的 IPv4 / IPv6 地址。",
                "", "", sw.Elapsed, TimeSpan.Zero);
        }

        var dnsElapsed = sw.Elapsed;

        // 阶段二:TCP 端口连通性
        sw.Restart();
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(ip, port);
            var winner = await Task.WhenAny(connectTask, Task.Delay(TcpProbeTimeoutMs, ct)).ConfigureAwait(false);
            if (winner != connectTask)
            {
                return Fail(SshConnectFailure.Timeout,
                    $"连接 {host}:{port} 超时({TcpProbeTimeoutMs / 1000} 秒无响应)。",
                    "", "", dnsElapsed, sw.Elapsed);
            }
            await connectTask.ConfigureAwait(false); // 传播真实异常
        }
        catch (SocketException se)
        {
            var failure = se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => SshConnectFailure.PortClosed,
                SocketError.TimedOut => SshConnectFailure.Timeout,
                SocketError.HostNotFound or SocketError.TryAgain => SshConnectFailure.DnsFailed,
                _ => SshConnectFailure.Unknown,
            };
            return Fail(failure, $"无法连接 {host}:{port}({se.SocketErrorCode})。",
                "", se.Message, dnsElapsed, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return Fail(SshConnectFailure.Unknown, $"连接 {host}:{port} 时出错。",
                "", ex.Message, dnsElapsed, sw.Elapsed);
        }

        var connectElapsed = sw.Elapsed;

        // 阶段三:SSH 握手 + 身份认证
        var outcome = await Task.Run(
            () => SshClientFactory.Create(device, new SshClientFactory.CreateOptions
            {
                TrustNewHostKey = trustNewHostKey,
            }), ct).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            return Fail(outcome.Failure, outcome.Message, outcome.Fingerprint,
                outcome.ExceptionText, dnsElapsed + connectElapsed, outcome.ConnectElapsed,
                outcome.HostKeyName, outcome.ServerVersion, outcome.FirstSeen);
        }

        if (trustNewHostKey && outcome.Fingerprint.Length > 0 &&
            !SshHostKeyStore.IsTrusted(host, port, outcome.Fingerprint))
        {
            SshHostKeyStore.Trust(host, port, outcome.Fingerprint);
        }

        var client = outcome.Client!;
        var version = outcome.ServerVersion;
        client.Dispose();

        return new SshConnectReport
        {
            Ok = true,
            Summary = $"连接成功{(version.Length > 0 ? " · " + version : "")}",
            Hint = device.RootLogin
                ? "已启用 root 登录,命令会在 sudo -i 会话中执行(sudo 密码会在连接时自动输入一次)。"
                : device.UseSudo ? "已配置 sudo 提权,需要 root 的命令会自动加 sudo。" : "",
            ServerVersion = version,
            HostKeyName = outcome.HostKeyName,
            Fingerprint = outcome.Fingerprint,
            FingerprintTrusted = true,
            FingerprintFirstSeen = outcome.FirstSeen,
            ConnectElapsed = dnsElapsed + connectElapsed,
            AuthElapsed = outcome.ConnectElapsed,
        };
    }

    /// <summary>识别远端系统类型(群晖 / 威联通 / UnRAID / TrueNAS / 通用 Linux)。</summary>
    public static async Task<NasOsInfo> DetectAsync(NasDevice device, CancellationToken ct = default)
    {
        var result = await RunAsync(device, DetectScript, 15000, sudo: false, ct: ct).ConfigureAwait(false);
        var text = result.ErrorMessage is null ? result.Stdout : "";
        return ParseOsInfo(text);
    }

    /// <summary>丢弃该设备的缓存连接(凭据变更、删除设备后调用)。</summary>
    public static void Forget(NasDevice device) => SshConnectionCache.Forget(device.SshCacheKey);

    /// <summary>断开并清空所有缓存连接。</summary>
    public static void ForgetAll() => SshConnectionCache.ForgetAll();

    /// <summary>信任该设备当前的主机指纹,并写入设备配置与指纹库。</summary>
    public static void TrustFingerprint(NasDevice device, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        SshHostKeyStore.Trust(device.Host.Trim(), NormalizePort(device), fingerprint);
        device.SshHostFingerprint = fingerprint;
        Forget(device);
    }

    /// <summary>
    /// 检查设备 SSH 配置是否完整;返回 null 表示可用,否则返回面向用户的中文提示。
    /// </summary>
    public static string? Validate(NasDevice d)
    {
        if (string.IsNullOrWhiteSpace(d.Host)) return "未配置主机地址(IP 或主机名)。";
        if (d.SshPort is < 1 or > 65535) return "SSH 端口需在 1–65535 之间。";
        if (string.IsNullOrWhiteSpace(d.SshUser)) return "未配置 SSH 用户名。";

        if (d.SshAuth == SshAuthKind.PrivateKey)
        {
            var path = Environment.ExpandEnvironmentVariables((d.SshKeyPath ?? "").Trim());
            if (path.Length == 0) return "未指定私钥文件路径。";
            if (!File.Exists(path)) return $"私钥文件不存在:{path}";
            return null;
        }

        return string.IsNullOrEmpty(SecretProtector.Unprotect(d.SshPassEnc))
            ? "未配置 SSH 密码(也可改用私钥认证)。"
            : null;
    }

    // ---------- 内部实现 ----------

    private static async Task<List<SshCommandResult>> RunCoreAsync(
        NasDevice device,
        IEnumerable<string> commands,
        int timeoutMs,
        bool? sudo,
        CancellationToken ct)
    {
        var list = commands.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var results = new List<SshCommandResult>();
        if (list.Count == 0) return results;

        var configError = Validate(device);
        if (configError is not null)
        {
            results.AddRange(list.Select(c => new SshCommandResult { Command = c, ErrorMessage = configError }));
            return results;
        }

        var perCommandMs = timeoutMs > 0 ? timeoutMs : device.EffectiveTimeoutSec * 1000;
        // root 会话里已经是 root,无需再逐条加 sudo
        var useSudo = (sudo ?? device.UseSudo) && !device.RootLogin;

        // 等待「连接空闲」的上限放宽一点:单条超时 + 20 秒握手余量
        var waitMs = perCommandMs + 20000;

        var lease = await SshConnectionCache.RentAsync(
            device.SshCacheKey,
            () => SshClientFactory.Create(device, new SshClientFactory.CreateOptions { TrustNewHostKey = true }),
            waitMs,
            ct).ConfigureAwait(false);

        using (lease)
        {
            // 勾选「以 root 方式连接」:首次使用时建立 sudo -i 会话(自动应答密码),后续命令都在其中执行
            if (device.RootLogin && lease.RootShell is null)
            {
                try
                {
                    lease.AttachRootShell(await SshRootShell
                        .CreateAsync(lease.Client, SecretProtector.Unprotect(device.SudoPassEnc), perCommandMs, ct)
                        .ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    lease.Abandon();
                    var reason = ex is TimeoutException
                        ? $"sudo -i 提权超时:{ex.Message}\r\n可先在 NAS 上手动执行 sudo -i 验证;若该环境不支持交互提权,请改选「sudo 提权」权限模式。"
                        : $"sudo -i 提权失败:{ex.Message}";
                    FillRemaining(results, list, reason);
                    return results;
                }
            }

            // 连接成功且尚未记录主机名时,顺带执行 hostname 回填(每条连接只尝试一次)
            await TryFillHostnameAsync(lease, device, perCommandMs, ct).ConfigureAwait(false);

            foreach (var raw in list)
            {
                ct.ThrowIfCancellationRequested();

                if (!lease.Client.IsConnected)
                {
                    FillRemaining(results, list, "连接已断开,后续命令未执行。");
                    return results;
                }

                results.Add(await ExecuteOneAsync(lease, device, raw, useSudo, perCommandMs, ct)
                    .ConfigureAwait(false));
            }
        }

        return results;
    }

    private static void FillRemaining(List<SshCommandResult> results, List<string> all, string error)
    {
        while (results.Count < all.Count)
            results.Add(new SshCommandResult { Command = all[results.Count], ErrorMessage = error });
    }

    private static readonly HashSet<string> HostnameTried = new(StringComparer.Ordinal);
    private static readonly object HostnameGate = new();

    /// <summary>
    /// 设备未填写 Hostname 时,经当前连接执行 hostname 自动回填并落盘;
    /// 同一连接缓存键只尝试一次,失败不影响本次命令执行。
    /// </summary>
    private static async Task TryFillHostnameAsync(
        SshConnectionCache.Lease lease,
        NasDevice device,
        int timeoutMs,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(device.Hostname)) return;

        var key = device.SshCacheKey;
        lock (HostnameGate)
        {
            if (!HostnameTried.Add(key)) return;
        }

        try
        {
            var r = await ExecuteOneAsync(lease, device, "hostname", useSudo: false,
                Math.Min(timeoutMs, 5000), ct).ConfigureAwait(false);
            var name = r.ErrorMessage is null ? r.Stdout.Trim() : "";
            if (name.Length == 0 || name.Contains('\n') || name.Contains(' ') || name.Length > 128) return;

            device.Hostname = name;
            NasDeviceStore.UpdateDevice(device);
        }
        catch
        {
            // 主机名获取失败不影响本次命令执行,下次连接会再尝试
            lock (HostnameGate) HostnameTried.Remove(key);
        }
    }

    private static async Task<SshCommandResult> ExecuteOneAsync(
        SshConnectionCache.Lease lease,
        NasDevice device,
        string rawCommand,
        bool useSudo,
        int timeoutMs,
        CancellationToken ct)
    {
        var full = BuildCommand(device, rawCommand, useSudo);
        var sw = Stopwatch.StartNew();

        // root 会话:直接在这个已提权的 shell 里跑,不需要 sudo 包装
        if (lease.RootShell is { } rootShell)
        {
            try
            {
                var (output, code) = await rootShell.RunAsync(rawCommand, timeoutMs, ct).ConfigureAwait(false);
                sw.Stop();
                return new SshCommandResult
                {
                    Command = rawCommand,
                    Stdout = output,
                    ExitCode = code,
                    Elapsed = sw.Elapsed,
                };
            }
            catch (TimeoutException)
            {
                lease.Abandon();
                sw.Stop();
                return new SshCommandResult { Command = rawCommand, TimedOut = true, Elapsed = sw.Elapsed };
            }
            catch (Exception ex)
            {
                lease.Abandon();
                return Broken(rawCommand, "root 会话执行失败:" + ex.Message, sw.Elapsed);
            }
        }

        SshCommand cmd;
        try
        {
            cmd = lease.Client.CreateCommand(full);
        }
        catch (Exception ex)
        {
            lease.Abandon();
            return Broken(full, "创建命令失败:" + ex.Message, sw.Elapsed);
        }

        IAsyncResult ar;
        try
        {
            ar = cmd.BeginExecute(null, null);
        }
        catch (Exception ex)
        {
            lease.Abandon();
            return Broken(full, "下发命令失败:" + ex.Message, sw.Elapsed);
        }

        bool finished;
        try
        {
            finished = await Task.Run(() => ar.AsyncWaitHandle.WaitOne(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lease.Abandon();
            return Broken(full, "等待命令返回时出错:" + ex.Message, sw.Elapsed);
        }

        if (!finished)
        {
            // 远端命令仍在跑,这条连接已不可安全复用
            lease.Abandon();
            sw.Stop();
            return new SshCommandResult { Command = full, TimedOut = true, Elapsed = sw.Elapsed };
        }

        string stdout;
        try
        {
            stdout = cmd.EndExecute(ar);
        }
        catch (Exception ex)
        {
            lease.Abandon();
            return Broken(full, "读取命令输出失败:" + ex.Message, sw.Elapsed);
        }

        sw.Stop();
        return new SshCommandResult
        {
            Command = full,
            Stdout = stdout ?? "",
            Stderr = cmd.Error ?? "",
            ExitCode = cmd.ExitStatus ?? -1,
            Elapsed = sw.Elapsed,
        };
    }

    private static SshCommandResult Broken(string command, string message, TimeSpan elapsed) =>
        new() { Command = command, ErrorMessage = message, Elapsed = elapsed };

    /// <summary>按需套上 sudo;无 sudo 密码时使用免密 sudo(sudo -n)。</summary>
    private static string BuildCommand(NasDevice d, string command, bool useSudo)
    {
        var cmd = (command ?? "").Trim();
        if (!useSudo || cmd.Length == 0) return cmd;

        var inner = ShellQuote(cmd);
        var sudoPass = SecretProtector.Unprotect(d.SudoPassEnc);
        return sudoPass.Length == 0
            ? $"sudo -n sh -c {inner}"
            : $"printf '%s\\n' {ShellQuote(sudoPass)} | sudo -S -p '' sh -c {inner}";
    }

    /// <summary>单引号包裹,内部单引号转义,避免命令与密码被 shell 二次解析。</summary>
    internal static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    private static int NormalizePort(NasDevice d) => d.SshPort is > 0 and <= 65535 ? d.SshPort : 22;

    private static NasOsInfo ParseOsInfo(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        string kernel = "";

        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                continue;
            }
            // 第一行通常是 "uname -s -r" 的输出,例:Linux 4.4.302+
            if (kernel.Length == 0) kernel = line;
        }

        var kind = NasKind.Unknown;
        if (dict.GetValueOrDefault("MARK_SYNO") == "1") kind = NasKind.Synology;
        else if (dict.GetValueOrDefault("MARK_QNAP") == "1") kind = NasKind.Qnap;
        else if (dict.GetValueOrDefault("MARK_UNRAID") == "1") kind = NasKind.Unraid;
        else if (dict.GetValueOrDefault("MARK_TRUENAS") == "1" ||
                 dict.GetValueOrDefault("MARK_MIDCLT") == "1") kind = NasKind.TrueNas;
        else if (dict.GetValueOrDefault("MARK_OPENWRT") == "1") kind = NasKind.OpenWrt;
        else if (kernel.Contains("Linux", StringComparison.OrdinalIgnoreCase)) kind = NasKind.GenericLinux;
        else if (kernel.Length > 0) kind = NasKind.GenericUnix;

        return new NasOsInfo
        {
            Kind = kind,
            OsName = dict.GetValueOrDefault("OS_NAME") ?? "",
            OsId = dict.GetValueOrDefault("OS_ID") ?? "",
            Version = dict.GetValueOrDefault("OS_VER") ?? "",
            Kernel = kernel,
            Arch = dict.GetValueOrDefault("ARCH") ?? "",
            Hostname = dict.GetValueOrDefault("HOST") ?? "",
            Raw = text ?? "",
        };
    }

    private static SshConnectReport Fail(
        SshConnectFailure failure,
        string summary,
        string fingerprint = "",
        string exceptionText = "",
        TimeSpan connectElapsed = default,
        TimeSpan authElapsed = default,
        string hostKeyName = "",
        string serverVersion = "",
        bool firstSeen = false) => new()
    {
        Ok = false,
        Failure = failure,
        Summary = summary,
        Hint = HintFor(failure),
        ExceptionText = exceptionText,
        ServerVersion = serverVersion,
        HostKeyName = hostKeyName,
        Fingerprint = fingerprint,
        FingerprintFirstSeen = firstSeen,
        ConnectElapsed = connectElapsed,
        AuthElapsed = authElapsed,
    };

    /// <summary>按失败原因给出可执行的修复建议。</summary>
    public static string HintFor(SshConnectFailure failure) => failure switch
    {
        SshConnectFailure.NotConfigured =>
            "点「编辑」补全 SSH 连接信息(地址、端口、用户名、密码或私钥)。",
        SshConnectFailure.DnsFailed =>
            "确认 IP / 主机名是否正确;主机名依赖路由器解析,建议优先用 IP。",
        SshConnectFailure.PortClosed =>
            "SSH 服务未开启或被防火墙拦截。群晖:控制面板 → 终端机和 SNMP → 启动 SSH;威联通:控制台 → 网络和文件服务 → Telnet/SSH。",
        SshConnectFailure.Timeout =>
            "设备可能离线或网络不通,先点「检测」确认能否 Ping 通,再排查 VPN / 网段隔离。",
        SshConnectFailure.AuthFailed =>
            "用户名或密码被拒绝。群晖需用 administrators 群组账号;DSM 7 以上普通命令无需 root,smartctl 等需 sudo。",
        SshConnectFailure.PrivateKeyMissing =>
            "检查私钥文件路径是否存在;支持 %USERPROFILE% 等环境变量,例 %USERPROFILE%\\.ssh\\id_rsa。",
        SshConnectFailure.PrivateKeyInvalid =>
            "私钥需为 OpenSSH 或 PEM 格式;若私钥带口令,请在「私钥口令」中填写。",
        SshConnectFailure.HostKeyChanged =>
            "主机密钥与上次记录不一致(设备重装 / 换 SSH 服务 / 或存在中间人风险)。确认无误后可点「信任新指纹」。",
        SshConnectFailure.HostKeyUntrusted =>
            "首次连接该设备,尚未记录指纹。核对指纹后勾选「信任此主机指纹」再测一次。",
        SshConnectFailure.ServerAborted =>
            "TCP 已连通但握手被服务端断开,多为 SSH 算法协商失败(客户端或服务端禁用了对方的算法),也可能是 fail2ban 封禁或 sshd MaxStartups 限流。客户端库已升级至 SSH.NET 2024.x 支持现代算法;若仍复现,可在 NAS 上查 /var/log/auth.log,或用「SSH 终端」交叉验证。",
        _ =>
            "可改用「SSH 终端」(系统自带 OpenSSH)交叉验证,或查看原始错误排查。",
    };
}
