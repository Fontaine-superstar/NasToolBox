namespace NasToolbox.Models;

/// <summary>NAS 上一个挂载点的空间使用情况(由 df -hP 输出解析而来)。</summary>
public sealed class MountUsage
{
    /// <summary>文件系统设备,例:/dev/md0。</summary>
    public string Filesystem { get; set; } = "";

    /// <summary>挂载点,例:/volume1。</summary>
    public string Mount { get; set; } = "";

    /// <summary>展示文本:已用 / 总量(df -h 的人类可读单位)。</summary>
    public string UsedText { get; set; } = "";

    /// <summary>使用百分比(0–100)。</summary>
    public int UsePercent { get; set; }

    /// <summary>百分比展示文本。</summary>
    public string UsePercentText => UsePercent + "%";
}