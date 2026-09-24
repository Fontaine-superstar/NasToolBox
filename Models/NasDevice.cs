namespace NasToolbox.Models;

/// <summary>一台受管理的 NAS 设备(持久化于 %LocalAppData%\NasToolbox\devices.json)。</summary>
public sealed class NasDevice
{
    /// <summary>设备名称,例:群晖 DS920+。</summary>
    public string Name { get; set; } = "";

    /// <summary>IP 地址或主机名。</summary>
    public string Host { get; set; } = "";

    /// <summary>主机名(可选);留空时在首次 SSH 连接成功后自动执行 hostname 获取并回填。</summary>
    public string Hostname { get; set; } = "";

    /// <summary>
    /// 显示名:由 Hostname(未获取时回退 Host)派生,同名设备从第二台起自动加 (2)(3) 后缀。
    /// 加载时计算,不持久化。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName { get; set; } = "";

    /// <summary>派生显示名的基础:Hostname 优先,尚未获取时回退 Host。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NameBase => Hostname.Length > 0 ? Hostname : Host;

    /// <summary>列表里额外展示的原始主机名(仅在与显示名不同时,例如重名加了后缀)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string HostnameLine =>
        Hostname.Length > 0 && !string.Equals(Hostname, DisplayName, StringComparison.Ordinal)
            ? Hostname
            : "";

    /// <summary>MAC 地址(Wake-on-LAN 唤醒用),可留空。</summary>
    public string Mac { get; set; } = "";

    /// <summary>Web 管理端口(必填);群晖 DSM 默认 5000。Web 管理页按 http://{Host}:{端口} 打开。</summary>
    public int WebPort { get; set; } = 5000;

    /// <summary>
    /// 旧版「Web 管理地址」字段:仅为兼容历史 devices.json 而保留,
    /// 加载时经 <see cref="MigrateLegacyWeb"/> 迁移为端口后清空,保存时不再写出。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Web { get; set; } = "";

    /// <summary>备注,例:4 盘位 RAID5。</summary>
    public string Note { get; set; } = "";

    /// <summary>SSH 端口,默认 22。</summary>
    public int SshPort { get; set; } = 22;

    /// <summary>SSH 用户名。</summary>
    public string SshUser { get; set; } = "";

    /// <summary>SSH 密码(DPAPI 加密后的 Base64,不落明文)。密码认证时使用。</summary>
    public string SshPassEnc { get; set; } = "";

    /// <summary>SSH 认证方式;旧配置无此字段时按数值 0(密码)反序列化,保持兼容。</summary>
    public SshAuthKind SshAuth { get; set; } = SshAuthKind.Password;

    /// <summary>私钥文件路径(.ssh/id_rsa、*.pem 等),仅私钥认证时使用。</summary>
    public string SshKeyPath { get; set; } = "";

    /// <summary>私钥文件自身的口令(DPAPI 加密后的 Base64);私钥未加密时可留空。</summary>
    public string SshKeyPassEnc { get; set; } = "";

    /// <summary>
    /// 旧版「sudo 逐条提权」模式的遗留字段:仅为兼容历史 devices.json 而保留,
    /// 加载时经 <see cref="MigrateLegacyPrivilege"/> 迁移为 root 会话后清空,保存时不再写出。
    /// </summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSudo { get; private set; }

    /// <summary>sudo 密码(DPAPI 加密后的 Base64);root 会话提权时使用,留空则需要 NAS 上配置免密 sudo。</summary>
    public string SudoPassEnc { get; set; } = "";

    /// <summary>以 root 方式连接:连接后执行 sudo -i(需密码时自动输入),之后命令都在 root 会话中执行。</summary>
    public bool RootLogin { get; set; }

    /// <summary>单条命令超时秒数(1–600,默认 20)。</summary>
    public int SshTimeoutSec { get; set; } = 20;

    /// <summary>已信任的主机指纹(SHA256:base64);首次连接确认后写入。</summary>
    public string SshHostFingerprint { get; set; } = "";

    /// <summary>
    /// 是否为「当前管理的设备」:Docker 等功能页默认操作这一台。
    /// 同一时刻最多一台;老配置无此字段时按 false 反序列化。
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>生效的 Web 管理端口:越界兜底 5000(范围 1–65535)。</summary>
    public int EffectiveWebPort => WebPort is >= 1 and <= 65535 ? WebPort : 5000;

    /// <summary>Web 管理地址:由 Host 与 Web 管理端口拼出(http://{Host}:{EffectiveWebPort})。</summary>
    public string WebOrDefault => $"http://{Host}:{EffectiveWebPort}";

    /// <summary>旧配置迁移:把历史「Web 管理地址」中的端口解析进 <see cref="WebPort"/>,完成后清空该旧字段。</summary>
    public void MigrateLegacyWeb()
    {
        if (string.IsNullOrWhiteSpace(Web))
        {
            Web = "";
            return;
        }

        // 形如 http://host:5000 / host:5000 / https://host:8443/ 的旧值尽量取出端口
        // (未写端口的 http(s) 地址按 Uri 规则取默认端口 80/443,与旧版行为一致)
        var url = Web.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Web : "http://" + Web;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
            WebPort = uri.Port;

        Web = "";
    }

    /// <summary>旧配置迁移:旧版「sudo 逐条提权」模式已移除,曾勾选 sudo 的设备升级为 root 会话,随后清空旧字段。</summary>
    public void MigrateLegacyPrivilege()
    {
        if (UseSudo && !RootLogin)
            RootLogin = true;
        UseSudo = false;
    }

    /// <summary>对应的 UNC 根路径(\\host)。</summary>
    public string Unc => @"\\" + Host.TrimStart('\\');

    /// <summary>SSH 登录信息显示(user@host:port);未配置用户名时为空。</summary>
    public string SshDisplay => string.IsNullOrEmpty(SshUser) ? "" : $"{SshUser}@{Host}:{SshPort}";

    /// <summary>连接缓存键:同一主机 + 端口 + 用户 + 认证方式复用一条连接。</summary>
    public string SshCacheKey => $"{SshUser}@{Host}:{SshPort}|{SshAuth}|{SshKeyPath}";

    /// <summary>命令超时秒数(对非法值兜底为 20 秒)。</summary>
    public int EffectiveTimeoutSec => SshTimeoutSec is >= 1 and <= 600 ? SshTimeoutSec : 20;

    /// <summary>是否已填写可用的 SSH 用户名(未区分认证方式的凭据是否完整)。</summary>
    public bool HasSshUser => !string.IsNullOrWhiteSpace(SshUser);

    /// <summary>SSH 配置摘要,用于列表展示,例:root@192.168.1.10:22 · 私钥 · sudo。</summary>
    public string SshSummary
    {
        get
        {
            if (!HasSshUser) return "";
            var parts = new List<string> { SshDisplay };
            if (SshAuth == SshAuthKind.PrivateKey) parts.Add("私钥");
            if (RootLogin) parts.Add("root 登录");
            return string.Join(" · ", parts);
        }
    }
}
