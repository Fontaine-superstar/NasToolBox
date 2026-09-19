namespace NasToolbox.Services.Ssh;

/// <summary>SSH 连接失败的原因分类,便于给出针对性的修复建议。</summary>
public enum SshConnectFailure
{
    None = 0,
    /// <summary>设备未填写主机地址 / 用户名 / 凭据。</summary>
    NotConfigured,
    /// <summary>主机名无法解析。</summary>
    DnsFailed,
    /// <summary>主机可达但端口未开放(SSH 服务未启用或被防火墙拦截)。</summary>
    PortClosed,
    /// <summary>TCP 连接或握手超时。</summary>
    Timeout,
    /// <summary>账号或密码 / 私钥被拒绝。</summary>
    AuthFailed,
    /// <summary>私钥文件不存在或无法读取。</summary>
    PrivateKeyMissing,
    /// <summary>私钥格式不支持,或口令错误。</summary>
    PrivateKeyInvalid,
    /// <summary>主机指纹与已记录的不一致(可能是设备重装,也可能是中间人)。</summary>
    HostKeyChanged,
    /// <summary>首次连接,指纹尚未信任。</summary>
    HostKeyUntrusted,
    /// <summary>TCP 通了,但 SSH 握手阶段被服务端主动断开(常见于算法协商失败)。</summary>
    ServerAborted,
    /// <summary>其他未归类错误。</summary>
    Unknown,
}

/// <summary>
/// SSH 连接诊断报告:分阶段耗时 + 失败分类 + 可直接展示给用户的说明。
/// </summary>
public sealed class SshConnectReport
{
    public bool Ok { get; init; }
    public SshConnectFailure Failure { get; init; } = SshConnectFailure.None;

    /// <summary>一句话结论,例:「连接成功 · SSH-2.0-OpenSSH_7.4」。</summary>
    public string Summary { get; init; } = "";

    /// <summary>面向用户的修复建议。</summary>
    public string Hint { get; init; } = "";

    /// <summary>原始异常文本(排障用)。</summary>
    public string ExceptionText { get; init; } = "";

    /// <summary>服务端版本串,例:SSH-2.0-OpenSSH_8.2p1。</summary>
    public string ServerVersion { get; init; } = "";

    /// <summary>主机密钥算法名,例:ssh-ed25519。</summary>
    public string HostKeyName { get; init; } = "";

    /// <summary>主机指纹,SHA256 base64 形式(与 OpenSSH 显示风格一致)。</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>该指纹是否已被信任。</summary>
    public bool FingerprintTrusted { get; init; }

    /// <summary>是否为首次连接(之前没有指纹记录)。</summary>
    public bool FingerprintFirstSeen { get; init; }

    public TimeSpan ConnectElapsed { get; init; }
    public TimeSpan AuthElapsed { get; init; }
    public TimeSpan TotalElapsed => ConnectElapsed + AuthElapsed;

    /// <summary>失败原因的中文标签。</summary>
    public string FailureLabel => Failure switch
    {
        SshConnectFailure.NotConfigured => "配置缺失",
        SshConnectFailure.DnsFailed => "主机无法解析",
        SshConnectFailure.PortClosed => "端口未开放",
        SshConnectFailure.Timeout => "连接超时",
        SshConnectFailure.AuthFailed => "认证失败",
        SshConnectFailure.PrivateKeyMissing => "私钥文件不可用",
        SshConnectFailure.PrivateKeyInvalid => "私钥无效",
        SshConnectFailure.HostKeyChanged => "主机指纹变更",
        SshConnectFailure.HostKeyUntrusted => "指纹未信任",
        SshConnectFailure.ServerAborted => "服务器中断握手",
        SshConnectFailure.Unknown => "未知错误",
        _ => "正常",
    };

    /// <summary>多行诊断文本,适合放进详情对话框。</summary>
    public string ToDisplayText()
    {
        var lines = new List<string>
        {
            $"结果:{(Ok ? "连接成功" : "连接失败 · " + FailureLabel)}",
            $"说明:{Summary}",
        };
        if (!string.IsNullOrEmpty(ServerVersion)) lines.Add($"服务端:{ServerVersion}");
        if (!string.IsNullOrEmpty(Fingerprint))
        {
            lines.Add($"主机密钥:{HostKeyName}");
            lines.Add($"指纹:{Fingerprint}");
            lines.Add(FingerprintTrusted
                ? "指纹状态:已信任"
                : FingerprintFirstSeen ? "指纹状态:首次连接,尚未信任" : "指纹状态:未信任");
        }
        lines.Add($"TCP 连接:{ConnectElapsed.TotalMilliseconds:F0} ms");
        lines.Add($"身份认证:{AuthElapsed.TotalMilliseconds:F0} ms");
        lines.Add($"合计:{TotalElapsed.TotalMilliseconds:F0} ms");
        if (!string.IsNullOrEmpty(Hint)) lines.Add($"建议:{Hint}");
        if (!string.IsNullOrEmpty(ExceptionText)) lines.Add($"原始错误:{ExceptionText}");
        return string.Join("\r\n", lines);
    }
}

/// <summary>
/// SSH 连接阶段失败时抛出:带结构化的失败原因,便于上层给出针对性提示。
/// </summary>
public sealed class SshConnectException : Exception
{
    public SshConnectFailure Failure { get; }

    /// <summary>面向用户的中文说明。</summary>
    public string FriendlyMessage { get; }

    public SshConnectException(SshConnectFailure failure, string friendlyMessage, string? raw = null)
        : base(friendlyMessage)
    {
        Failure = failure;
        FriendlyMessage = friendlyMessage;
        Raw = raw ?? "";
    }

    /// <summary>原始异常文本(排障用)。</summary>
    public string Raw { get; }
}
