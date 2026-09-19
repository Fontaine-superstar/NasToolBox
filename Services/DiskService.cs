using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>本机磁盘空间摘要(DriveInfo 映射,跳过未就绪的驱动器)。</summary>
public static class DiskService
{
    public static List<DriveSummary> GetDrives()
    {
        var list = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? TypeText(drive.DriveType)
                    : drive.VolumeLabel;
                list.Add(new DriveSummary(
                    drive.Name, label, TypeText(drive.DriveType),
                    drive.TotalSize, drive.TotalFreeSpace));
            }
            catch
            {
                // 单盘读取失败(权限/驱动器状态)忽略
            }
        }
        return list;
    }

    private static string TypeText(System.IO.DriveType type) => type switch
    {
        System.IO.DriveType.Fixed => "本地磁盘",
        System.IO.DriveType.Network => "网络驱动器",
        System.IO.DriveType.Removable => "可移动磁盘",
        System.IO.DriveType.CDRom => "光驱",
        System.IO.DriveType.Ram => "RAM 磁盘",
        _ => type.ToString(),
    };
}