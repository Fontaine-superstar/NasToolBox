using Microsoft.UI.Xaml;

namespace NasToolbox.Models;

/// <summary>
/// 一块物理硬盘:lsblk 的设备信息 + smartctl 的健康自评。
/// 大小 / SSD·HDD / 接口来自 lsblk;健康来自 smartctl -H(没装 smartmontools 时为空)。
/// </summary>
public sealed class DiskInfo
{
    /// <summary>设备名(不带 /dev 前缀),例:sda、nvme0n1。</summary>
    public string Name { get; init; } = "";

    /// <summary>容量(字节);0 表示未知。</summary>
    public long SizeBytes { get; init; }

    /// <summary>是否固态(rota=0);机械盘 / 未知为 false。</summary>
    public bool IsSsd { get; init; }

    /// <summary>传输接口原始值(lsblk 的 tran):sata / usb / nvme…;空表示未上报。</summary>
    public string Tran { get; init; } = "";

    /// <summary>厂商型号(lsblk 的 model);空表示未上报。</summary>
    public string Model { get; init; } = "";

    /// <summary>SMART 健康自评原文:PASSED / OK / FAILED!…;空表示无法检测(未装 smartctl 或无权限)。</summary>
    public string Health { get; init; } = "";

    public string SizeText => SizeBytes > 0 ? FileEntry.FormatSize(SizeBytes) : "—";

    public string KindText => IsSsd ? "SSD" : "HDD";

    public string TranText => Tran.ToUpperInvariant() switch
    {
        "SATA" => "SATA",
        "USB" => "USB",
        "NVME" => "NVMe",
        "SAS" => "SAS",
        "" => "—",
        var t => t,
    };

    public string HealthText => Health switch
    {
        "PASSED" or "OK" => "正常",
        "" => "未知",
        var h => "警告", // FAILED!、UNKNOWN! 等一切非通过状态都按警告处理
    };

    public bool IsWarn => HealthText == "警告";

    // 健康状态三态着色:正常绿 / 警告橙 / 未知灰(DataTemplate 里按可见性切换)
    public Visibility WarnVisibility => IsWarn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OkVisibility => HealthText == "正常" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnknownVisibility => HealthText == "未知" ? Visibility.Visible : Visibility.Collapsed;
}
