using NasToolbox.Converters;

namespace NasToolbox.Models;

/// <summary>一块本机磁盘的空间摘要(DriveInfo 映射)。</summary>
public sealed record DriveSummary(string Root, string Label, string Type, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>已用百分比(0–100,保留 1 位小数)。</summary>
    public double UsedPercent => TotalBytes > 0 ? Math.Round(UsedBytes * 100.0 / TotalBytes, 1) : 0;

    /// <summary>"卷标 · 类型"。</summary>
    public string DisplayText => $"{Label} · {Type}";

    /// <summary>"已用 / 总量"。</summary>
    public string SizeText => $"{Conv.FmtBytes(UsedBytes)} / {Conv.FmtBytes(TotalBytes)}";

    /// <summary>"xx.x% 已用"。</summary>
    public string UsedPercentText => $"{UsedPercent:0.#}% 已用";
}