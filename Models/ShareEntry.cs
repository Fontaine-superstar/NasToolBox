namespace NasToolbox.Models;

/// <summary>一条 SMB 共享(NetShareEnum 枚举结果)。</summary>
/// <param name="Name">共享名,例:media。</param>
/// <param name="Remark">共享备注。</param>
/// <param name="IsDisk">是否为磁盘共享(排除打印/IPC)。</param>
/// <param name="Hidden">是否为管理用隐藏共享(名称以 $ 结尾)。</param>
public sealed record ShareEntry(string Name, string Remark, bool IsDisk, bool Hidden);