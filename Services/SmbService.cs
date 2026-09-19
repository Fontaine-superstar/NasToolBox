using System.Runtime.InteropServices;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// SMB 共享枚举:P/Invoke NetShareEnum(SHARE_INFO_1)。
/// 若 NAS 禁止匿名枚举会得到"访问被拒绝",提示先在资源管理器登录账号。
/// </summary>
public static class SmbService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public IntPtr Name;
        public uint Type;
        public IntPtr Comment;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName,
        int level,
        out IntPtr bufPtr,
        int prefMaxLen,
        out int entriesRead,
        out int totalEntries,
        IntPtr resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private const uint StypeMask = 0x00000003;
    private const uint StypeDisk = 0x00000000;
    private const int ErrorMoreData = 234;

    public static async Task<List<ShareEntry>> ListSharesAsync(string host) =>
        await Task.Run(() => ListShares(host));

    private static List<ShareEntry> ListShares(string host)
    {
        var server = @"\\" + host.TrimStart('\\');
        var rc = NetShareEnum(server, 1, out var buf, -1, out var read, out _, IntPtr.Zero);
        try
        {
            if (rc != 0 && !(rc == ErrorMoreData && read > 0))
            {
                throw new InvalidOperationException(Explain(rc, host));
            }

            var list = new List<ShareEntry>();
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var si = Marshal.PtrToStructure<ShareInfo1>(IntPtr.Add(buf, i * size));
                var name = si.Name != IntPtr.Zero ? Marshal.PtrToStringUni(si.Name) : string.Empty;
                if (string.IsNullOrEmpty(name)) continue;
                var remark = si.Comment != IntPtr.Zero ? Marshal.PtrToStringUni(si.Comment) ?? "" : "";
                list.Add(new ShareEntry(name, remark, (si.Type & StypeMask) == StypeDisk, name.EndsWith('$')));
            }
            return list;
        }
        finally
        {
            if (buf != IntPtr.Zero) NetApiBufferFree(buf);
        }
    }

    /// <summary>常见错误码 → 人话提示。</summary>
    private static string Explain(int rc, string host) => rc switch
    {
        5 => $"访问被拒绝:{host} 不允许匿名枚举共享,请先在资源管理器中登录该 NAS 的账号(打开一次 \\\\{host} 并输入账号密码)",
        53 => $"找不到网络路径:{host} 可能离线,或 SMB(445 端口)未开放",
        64 => "指定的网络名不再可用",
        121 => "连接信号灯超时(网络不稳定)",
        1231 => "无法访问网络:未连接或远程主机不可达",
        1326 => "用户名或密码不正确",
        _ => $"枚举共享失败,错误码 {rc}({host})",
    };
}