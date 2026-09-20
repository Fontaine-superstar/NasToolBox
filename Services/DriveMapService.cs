using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NasToolbox.Models;
using NasToolbox.Services.Smb;

namespace NasToolbox.Services;

/// <summary>一条已映射到本地盘符的网络驱动器。</summary>
public sealed record MappedDrive(char Letter, string Remote);

/// <summary>
/// 网络驱动器映射:把 NAS 上的 SMB 共享(或共享下的任意子目录)映射成本地盘符。
/// 凭据复用设备的 SSH 账号密码,经 WNetAddConnection2 直接交给 SMB 重定向器 ——
/// 不落盘、不出现在任何命令行参数里,后续读写就是普通本地文件 IO。
/// </summary>
public static class DriveMapService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    private const int ResourceTypeDisk = 1;

    /// <summary>CONNECT_UPDATE_PROFILE:把这次连接记进用户配置文件(下次登录自动恢复)。</summary>
    private const int ConnectUpdateProfile = 0x00000001;

    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorBadNetName = 67;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorPathNotFound = 53;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorNotConnected = 2250;

    /// <summary>
    /// 把 <paramref name="unc"/> 映射为本地盘符。
    /// 先补一次 SMB 会话(映射往往会复用已建立的 IPC$ 会话),失败也不拦后面的映射。
    /// </summary>
    public static async Task MapAsync(NasDevice device, string unc, char letter, bool persistent,
        CancellationToken ct = default)
    {
        var target = TrimUnc(unc);
        if (target.Length == 0) throw new InvalidOperationException("没有可映射的网络路径。");

        try { await SmbSession.OpenAsync(device, ct).ConfigureAwait(false); }
        catch { /* 会话建立失败不阻断:映射本身还会带凭据再试一次 */ }

        await Task.Run(() => AddConnection(device, target, letter, persistent), ct).ConfigureAwait(false);
    }

    private static void AddConnection(NasDevice device, string target, char letter, bool persistent)
    {
        var (host, user, password) = SmbSession.CredentialsOf(device);
        var resource = new NetResource
        {
            Type = ResourceTypeDisk,
            LocalName = $"{char.ToUpperInvariant(letter)}:",
            RemoteName = target,
        };
        var flags = persistent ? ConnectUpdateProfile : 0;

        // 私钥登录的设备没有密码:退回 Windows 已保存的凭据
        if (password.Length == 0)
        {
            var bare = WNetAddConnection2(resource, null, null, flags);
            if (bare is 0 or ErrorSessionCredentialConflict) return;
            if (bare == ErrorAlreadyAssigned)
                throw new InvalidOperationException($"盘符 {letter}: 已经被占用,请换一个。");
            if (bare is 5 or 1326)
                throw new InvalidOperationException(
                    $"该设备没有可用于 SMB 的密码({host})。\r\n" +
                    "请在 Windows 凭据管理器里为该地址保存一份 Windows 凭据,或把设备改成 SSH 密码登录后再试。");
            throw new InvalidOperationException(Explain(bare, target));
        }

        // 账号写法:先原样提交;若 NAS 要求「主机\用户」形式再换一种重试
        var accounts = user.Contains('\\') || user.Contains('@')
            ? new[] { user }
            : new[] { user, $@"{host}\{user}" };

        var last = 0;
        foreach (var account in accounts)
        {
            var rc = WNetAddConnection2(resource, password, account, flags);
            if (rc is 0 or ErrorSessionCredentialConflict) return;

            last = rc;
            if (rc is 5 or 1326 or ErrorNetNameDeleted) continue; // 换个账号写法再试
            break;
        }

        // 1219:同一台主机上已有一组「别的凭据」建立的连接(此前会把 1219 误当成功,盘符其实没建)。
        // 带凭据的映射在此状态下必然失败 —— 退回不带凭据裸连一次,复用主机上已保存/已连接的凭据;
        // 实测裸连能成功建起盘符(net use 状态 OK),且随会话持久,与创建进程无关。
        if (last == ErrorSessionCredentialConflict)
        {
            var bare = WNetAddConnection2(resource, null, null, flags);
            if (bare == 0) return;
        }

        throw new InvalidOperationException(Explain(last, target));
    }

    /// <summary>断开某个盘符的网络驱动器映射。persistent 的映射会从用户配置里一并抹掉。</summary>
    public static Task DisconnectAsync(char letter, bool removeSaved = true, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var name = $"{char.ToUpperInvariant(letter)}:";
            var rc = WNetCancelConnection2(name, removeSaved ? ConnectUpdateProfile : 0, true);
            // 2250:该盘符本来就没连着——已经在资源管理器里断开过
            if (rc is 0 or ErrorNotConnected) return;
            throw new InvalidOperationException(Explain(rc, name));
        }, ct);

    /// <summary>列出本机已映射的网络驱动器;给 host 时只保留指向那台主机的(不区分主机名/IP 之外的细节)。</summary>
    public static List<MappedDrive> ListMapped(string? host = null)
    {
        var prefix = string.IsNullOrWhiteSpace(host) ? null : @"\\" + host.Trim().Trim('\\');
        var result = new List<MappedDrive>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Network) continue;
            var letter = char.ToUpperInvariant(drive.Name[0]);
            var remote = RemoteOf(letter);
            if (remote is null) continue;
            if (prefix is not null &&
                !remote.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                !remote.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new MappedDrive(letter, remote));
        }

        return result.OrderBy(x => x.Letter).ToList();
    }

    /// <summary>该网络路径是否已被映射;已映射返回盘符,否则 null。</summary>
    public static char? MappedLetter(string unc)
    {
        var target = TrimUnc(unc);
        if (target.Length == 0) return null;

        foreach (var mapped in ListMapped())
        {
            if (TrimUnc(mapped.Remote).Equals(target, StringComparison.OrdinalIgnoreCase))
                return mapped.Letter;
        }
        return null;
    }

    /// <summary>空闲盘符:从 Z 往 D 找(跳过 A/B 软驱与通常被系统占用的 C)。</summary>
    public static List<char> AvailableLetters()
    {
        HashSet<char> used = [];
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                used.Add(char.ToUpperInvariant(d.Name[0]));
        }
        catch { /* 枚举失败时按全空闲处理,交给 net use 报错 */ }

        var letters = new List<char>();
        for (var c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c)) letters.Add(c);
        return letters;
    }

    /// <summary>默认推荐的盘符(Z 起往回);没有空闲返回 '\0'。</summary>
    public static char SuggestLetter()
    {
        var letters = AvailableLetters();
        return letters.Count == 0 ? '\0' : letters[0];
    }

    /// <summary>去掉 UNC 末尾多余的反斜杠,统一比较口径。</summary>
    private static string TrimUnc(string unc) => (unc ?? "").Trim().TrimEnd('\\');

    private static string? RemoteOf(char letter)
    {
        var buffer = new StringBuilder(1024);
        var length = buffer.Capacity;
        var rc = WNetGetConnection($"{char.ToUpperInvariant(letter)}:", buffer, ref length);
        return rc == 0 ? buffer.ToString() : null;
    }

    /// <summary>常见错误码 → 人话提示。</summary>
    private static string Explain(int rc, string target) => rc switch
    {
        ErrorAlreadyAssigned => "盘符已经被占用(可能是别的网络驱动器或本地卷),请换一个盘符。",
        ErrorBadNetName or ErrorPathNotFound or ErrorNetNameDeleted =>
            $"找不到网络路径:{target}\r\n请确认 NAS 在线、SMB(445 端口)开放,且该共享确实存在。",
        5 => $"访问被拒绝:{target} 拒绝了该账号。\r\n请确认 NAS 的共享权限里给这个账号开了访问权。",
        121 => "连接信号灯超时,网络不稳定或 NAS 无响应。",
        ErrorSessionCredentialConflict =>
            "这台 NAS 上已经有另一组凭据建立的连接(常见于资源管理器里手动登录过)。\r\n" +
            "可在命令提示符执行 `net use` 查看、`net use * /del` 清理后重试。",
        1231 => "无法访问网络:未连接或远程主机不可达。",
        1326 => "账号或密码不正确。\r\n文件管理复用该设备的 SSH 账号密码;若 SMB 是另一套账号,请在 Windows 凭据管理器保存该地址的 Windows 凭据。",
        86 => "指定的密码不正确。",
        1202 => "该设备名已在用户配置中存在,请先在命令提示符执行 `net use` 清理。",
        ErrorNotConnected => "该盘符当前没有连接。",
        _ => $"映射网络驱动器失败,错误码 {rc}({SafeText(rc)})",
    };

    private static string SafeText(int rc)
    {
        try { return new Win32Exception(rc).Message; }
        catch { return "未知错误"; }
    }
}
