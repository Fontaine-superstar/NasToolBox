namespace NasToolbox.Models;

/// <summary>NAS 的一块网卡信息(来自 /sys/class/net 与 ip 命令,两次采样计算实时速率)。</summary>
public sealed class NicInfo
{
    public string Name { get; set; } = "";
    public string Ipv4 { get; set; } = "";
    public string Mac { get; set; } = "";
    public string State { get; set; } = "";

    /// <summary>链路速度(Mbps);虚拟网卡可能取不到为 0。</summary>
    public int SpeedMbps { get; set; }

    /// <summary>累计收 / 发字节数。</summary>
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }

    /// <summary>实时下行 / 上行速率(字节/秒,两次采样差值)。</summary>
    public double RateDown { get; set; }
    public double RateUp { get; set; }

    public string StateText => State.Length > 0 ? State : "—";
    public string SpeedText => SpeedMbps > 0 ? $"{SpeedMbps} Mbps" : "—";
    public string TrafficText => $"累计 ↓ {FmtBytes(RxBytes)} · ↑ {FmtBytes(TxBytes)}";
    public string RateText => RateDown <= 0 && RateUp <= 0
        ? "空闲"
        : $"↓ {FmtRate(RateDown)} · ↑ {FmtRate(RateUp)}";

    private static string FmtBytes(double b) => b >= (1 << 30) ? $"{b / (1 << 30):F1} GB"
        : b >= (1 << 20) ? $"{b / (1 << 20):F1} MB"
        : $"{b / 1024.0:F0} KB";

    private static string FmtRate(double bytesPerSec) => bytesPerSec * 8 >= 1_000_000
        ? $"{bytesPerSec * 8 / 1_000_000:F1} Mbps"
        : $"{bytesPerSec / 1024.0:F0} KB/s";
}