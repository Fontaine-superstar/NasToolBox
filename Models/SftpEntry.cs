namespace NasToolbox.Models;

/// <summary>SFTP 目录项(远端文件 / 文件夹)。</summary>
public sealed class SftpEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteTime { get; init; }

    /// <summary>文件大小的可读文本;目录返回空。</summary>
    public string SizeText => IsDirectory ? "" : FormatSize(Length);

    /// <summary>类型文本:列表里区分「文件夹」与「文件」。</summary>
    public string KindText => IsDirectory ? "文件夹" : "文件";

    /// <summary>修改时间的可读文本;无有效时间返回空。</summary>
    public string ModifiedText =>
        LastWriteTime == default ? "" : LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Segoe 图标字符:目录用文件夹图标,文件用文档图标。</summary>
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";

    /// <summary>字节数的可读格式(1024 进制)。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{size:F1} {units[i]}";
    }
}
