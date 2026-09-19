namespace NasToolbox.Services.Ssh;

/// <summary>识别出的 NAS / 系统类型。</summary>
public enum NasKind
{
    Unknown = 0,
    Synology,
    Qnap,
    Unraid,
    TrueNas,
    OpenWrt,
    GenericLinux,
    GenericUnix,
}

/// <summary>远端系统的识别结果(用于按品牌适配命令与面板)。</summary>
public sealed class NasOsInfo
{
    public NasKind Kind { get; init; } = NasKind.Unknown;
    public string OsName { get; init; } = "";
    public string OsId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Kernel { get; init; } = "";
    public string Arch { get; init; } = "";
    public string Hostname { get; init; } = "";

    /// <summary>识别用的原始输出(排障用)。</summary>
    public string Raw { get; init; } = "";

    /// <summary>品牌中文名。</summary>
    public string KindName => Kind switch
    {
        NasKind.Synology => "群晖 DSM",
        NasKind.Qnap => "威联通 QTS",
        NasKind.Unraid => "UnRAID",
        NasKind.TrueNas => "TrueNAS",
        NasKind.OpenWrt => "OpenWrt",
        NasKind.GenericLinux => "Linux",
        NasKind.GenericUnix => "Unix",
        _ => "未识别",
    };

    /// <summary>摘要展示,例:群晖 DSM · Linux nas 4.4.302+ · x86_64。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { KindName };
            if (OsName.Length > 0) parts.Add(OsName);
            if (Kernel.Length > 0) parts.Add(Kernel);
            if (Arch.Length > 0) parts.Add(Arch);
            return string.Join(" · ", parts);
        }
    }
}
