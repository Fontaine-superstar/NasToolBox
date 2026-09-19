using System.Diagnostics;
using System.Net.Sockets;
using NasToolbox.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// 构建并连接 SSH 客户端:按设备配置选择密码 / 私钥认证,并在握手阶段校验主机指纹。
/// 连接信息(<see cref="BuildConnectionInfo"/>)同时供 SftpClient 复用。
/// </summary>
public static class SshClientFactory
{
    /// <summary>建连时的策略选项。</summary>
    public sealed class CreateOptions
    {
        /// <summary>首次连接时是否直接信任并记入指纹库(「测试连接」对话框会先询问用户)。</summary>
        public bool TrustNewHostKey { get; init; }
    }

    /// <summary>握手阶段主机指纹的校验结果。</summary>
    public sealed class HostKeyState
    {
        public string Fingerprint { get; set; } = "";
        public string HostKeyName { get; set; } = "";
        public bool Trusted { get; set; }
        public bool FirstSeen { get; set; }
        public bool Changed { get; set; }
    }

    /// <summary>建连结果:成功时带客户端与指纹信息,失败时带原因分类。</summary>
    public sealed class CreateOutcome
    {
        public SshClient? Client { get; set; }
        public string ServerVersion { get; set; } = "";
        public string HostKeyName { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public bool Trusted { get; set; }
        public bool FirstSeen { get; set; }
        public bool Changed { get; set; }
        public TimeSpan ConnectElapsed { get; set; }
        public SshConnectFailure Failure { get; set; } = SshConnectFailure.None;
        public string Message { get; set; } = "";
        public string ExceptionText { get; set; } = "";
        public bool Ok => Client is not null;
    }

    /// <summary>默认握手超时(秒)。</summary>
    public const int HandshakeTimeoutSec = 15;

    /// <summary>建连并返回一个已认证的客户端;失败时 Client 为 null,通过 Failure 说明原因。</summary>
    public static CreateOutcome Create(NasDevice device, CreateOptions? options = null)
    {
        options ??= new CreateOptions();
        var outcome = new CreateOutcome();
        var state = new HostKeyState();

        var (info, failure, message) = BuildConnectionInfo(device, options, state);
        CopyState(state, outcome);

        if (info is null)
        {
            outcome.Failure = failure;
            outcome.Message = message;
            return outcome;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var client = new SshClient(info);
            // HostKeyReceived 定义在 BaseClient 上,必须在 Connect() 之前挂载
            client.HostKeyReceived += CreateHostKeyHandler(device, options, state);
            // 每 30 秒发一次保活,避免长时间空闲被 NAS 或路由器断开
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.Connect();
            sw.Stop();

            outcome.ConnectElapsed = sw.Elapsed;
            outcome.ServerVersion = info.ServerVersion ?? "";
            outcome.Client = client;
        }
        catch (Exception ex)
        {
            outcome.Failure = MapException(ex, state);
            outcome.Message = ex.Message;
            outcome.ExceptionText = ex.ToString();
        }
        finally
        {
            CopyState(state, outcome);
        }

        return outcome;
    }

    /// <summary>
    /// 构造连接信息(含认证方式与主机指纹校验回调)。
    /// 返回 null 表示配置有问题,此时 failure / message 已说明原因。
    /// </summary>
    public static (ConnectionInfo? Info, SshConnectFailure Failure, string Message) BuildConnectionInfo(
        NasDevice device, CreateOptions options, HostKeyState state)
    {
        var host = device.Host.Trim();
        var port = device.SshPort is > 0 and <= 65535 ? device.SshPort : 22;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(device.SshUser))
            return (null, SshConnectFailure.NotConfigured, "未配置主机地址或 SSH 用户名。");

        var auths = BuildAuthMethods(device, out var failure, out var message);
        if (auths is null) return (null, failure, message);

        var conn = new ConnectionInfo(host, port, device.SshUser, auths.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(HandshakeTimeoutSec),
        };

        return (conn, SshConnectFailure.None, "");
    }

    /// <summary>
    /// 主机指纹校验处理器:与已知指纹比对,首次连接按策略决定是否信任。
    /// 需挂到 SshClient / SftpClient 的 HostKeyReceived 事件上(在 Connect 之前)。
    /// </summary>
    public static EventHandler<HostKeyEventArgs> CreateHostKeyHandler(
        NasDevice device, CreateOptions options, HostKeyState state)
    {
        var host = device.Host.Trim();
        var port = device.SshPort is > 0 and <= 65535 ? device.SshPort : 22;

        return (_, e) =>
        {
            var fp = SshHostKeyStore.ComputeFingerprint(e.HostKey);
            state.Fingerprint = fp;
            state.HostKeyName = e.HostKeyName ?? "";

            var known = SshHostKeyStore.Get(host, port);
            if (known.Length == 0)
            {
                state.FirstSeen = true;
                // 设备自身记录过指纹时也视为已信任(旧配置迁移场景)
                if (string.Equals(device.SshHostFingerprint, fp, StringComparison.Ordinal))
                {
                    state.Trusted = true;
                    SshHostKeyStore.Trust(host, port, fp);
                }
                e.CanTrust = state.Trusted || options.TrustNewHostKey;
            }
            else if (string.Equals(known, fp, StringComparison.Ordinal))
            {
                state.Trusted = true;
                e.CanTrust = true;
            }
            else
            {
                state.Changed = true;
                e.CanTrust = false;
            }
        };
    }

    /// <summary>按认证方式构造认证方法列表;返回 null 表示配置有问题。</summary>
    private static List<AuthenticationMethod>? BuildAuthMethods(
        NasDevice d, out SshConnectFailure failure, out string message)
    {
        failure = SshConnectFailure.None;
        message = "";

        var user = d.SshUser;
        var auths = new List<AuthenticationMethod>();

        if (d.SshAuth == SshAuthKind.PrivateKey)
        {
            var path = Environment.ExpandEnvironmentVariables((d.SshKeyPath ?? "").Trim());
            if (path.Length == 0 || !File.Exists(path))
            {
                failure = SshConnectFailure.PrivateKeyMissing;
                message = path.Length == 0 ? "未指定私钥文件路径。" : $"私钥文件不存在:{path}";
                return null;
            }

            var keyPass = SecretProtector.Unprotect(d.SshKeyPassEnc);
            try
            {
                var key = string.IsNullOrEmpty(keyPass)
                    ? new PrivateKeyFile(path)
                    : new PrivateKeyFile(path, keyPass);
                auths.Add(new PrivateKeyAuthenticationMethod(user, key));
            }
            catch (Exception ex)
            {
                failure = SshConnectFailure.PrivateKeyInvalid;
                message = $"私钥无法解析(格式不支持或口令错误):{ex.Message}";
                return null;
            }

            return auths;
        }

        var password = SecretProtector.Unprotect(d.SshPassEnc);
        if (!string.IsNullOrEmpty(password))
        {
            auths.Add(new PasswordAuthenticationMethod(user, password));

            // 群晖 / 威联通等 NAS 常强制键盘交互认证,补挂一种
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            auths.Add(ki);
        }
        else
        {
            // 未存密码:允许服务器接受 none / ssh-agent 已加载的密钥
            auths.Add(new NoneAuthenticationMethod(user));
        }

        return auths;
    }

    private static void CopyState(HostKeyState state, CreateOutcome outcome)
    {
        outcome.Fingerprint = state.Fingerprint;
        outcome.HostKeyName = state.HostKeyName;
        outcome.Trusted = state.Trusted;
        outcome.FirstSeen = state.FirstSeen;
        outcome.Changed = state.Changed;
    }

    private static SshConnectFailure MapException(Exception ex, HostKeyState state)
    {
        // 指纹相关的失败优先判断:SSH.NET 抛的是通用连接异常
        if (state.Changed) return SshConnectFailure.HostKeyChanged;
        if (state.FirstSeen) return SshConnectFailure.HostKeyUntrusted;

        return ex switch
        {
            SshAuthenticationException => SshConnectFailure.AuthFailed,
            SshOperationTimeoutException => SshConnectFailure.Timeout,
            SshConnectionException sce when state.Changed => SshConnectFailure.HostKeyChanged,
            SshConnectionException sce when state.FirstSeen => SshConnectFailure.HostKeyUntrusted,
            // TCP 通了但握手被服务端掐断:典型原因是算法协商失败 / fail2ban / MaxStartups
            SshConnectionException sce when sce.Message.Contains("aborted by the server", StringComparison.OrdinalIgnoreCase)
                => SshConnectFailure.ServerAborted,
            SocketException se => se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => SshConnectFailure.PortClosed,
                SocketError.HostNotFound or SocketError.TryAgain => SshConnectFailure.DnsFailed,
                SocketError.TimedOut => SshConnectFailure.Timeout,
                _ => SshConnectFailure.Unknown,
            },
            _ => ex.Message.Contains("time", StringComparison.OrdinalIgnoreCase)
                ? SshConnectFailure.Timeout
                : SshConnectFailure.Unknown,
        };
    }
}
