using System.ComponentModel;
using System.Runtime.InteropServices;
using NasToolbox.Models;

namespace NasToolbox.Services.Smb;

/// <summary>
/// SMB 会话:以设备上的 SSH 账号 / 密码(凭据复用)建立到 NAS 的 SMB 会话。
/// Windows 后续对 \\host\share 的访问都复用这条会话,因此列目录、读写文件用普通 IO API 即可。
/// 密码只存在于内存中(从 DPAPI 解开),不落盘、不出现在任何命令行参数里。
/// </summary>
public static class SmbSession
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

    /// <summary>WNetAddConnection2:建立到远端资源的网络连接(不映射盘符)。</summary>
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NetResource netResource, string? password, string? username, int flags);

    /// <summary>WNetCancelConnection2:断开之前建立的连接。</summary>
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private const int ResourceTypeDisk = 1;

    // 已存在同名连接 / 同一会话已有凭据:都视为"已经在连",不再报错
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorDeviceAlreadyRemembered = 1202;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorNoNetworkOrBadPath = 67;

    /// <summary>设备的 SMB 账号与密码:复用 SSH 用户名,密码从 DPAPI 取出。</summary>
    public static (string Host, string User, string Password) CredentialsOf(NasDevice device)
    {
        var user = device.SshUser.Trim();
        var password = SecretProtector.Unprotect(device.SshPassEnc);
        return (device.Host.Trim().Trim('\\'), user, password);
    }

    /// <summary>UNC 根:\\host。</summary>
    public static string RootOf(NasDevice device) => @"\\" + device.Host.Trim().Trim('\\');

    /// <summary>某个共享的完整 UNC 路径:\\host\share。</summary>
    public static string UncOf(NasDevice device, string share) =>
        RootOf(device) + @"\" + share.Trim('\\', '/');

    /// <summary>当前账号是否可用于 SMB:必须有用户名;私钥登录的设备没有密码,只能碰运气用 Windows 已保存的凭据。</summary>
    public static string? Validate(NasDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.Host)) return "设备没有填写主机地址。";
        if (string.IsNullOrWhiteSpace(device.SshUser)) return "该设备没有配置 SSH 用户名,SMB 登录不知道用哪个账号。";
        return null;
    }

    /// <summary>
    /// 建立到 \\host\IPC$ 的会话(IPC$ 只做身份认证,不占用真实共享),
    /// 之后访问 \\host\任意共享 都会复用这条会话。
    /// 没有密码时(私钥登录)尝试用 Windows 已保存的凭据直连。
    /// </summary>
    public static Task OpenAsync(NasDevice device, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var error = Validate(device);
            if (error is not null) throw new InvalidOperationException(error);

            var (host, user, password) = CredentialsOf(device);
            var remote = RootOf(device) + @"\IPC$";

            var resource = new NetResource
            {
                Type = ResourceTypeDisk,
                RemoteName = remote,
                LocalName = null,
                Provider = null,
            };

            var hasPassword = password.Length > 0;
            // 没有密码(SSH 走私钥)时只能用 Windows 已保存的凭据碰运气
            if (!hasPassword)
            {
                var bare = WNetAddConnection2(resource, null, null, 0);
                if (bare is 0 or ErrorAlreadyAssigned or ErrorDeviceAlreadyRemembered or ErrorSessionCredentialConflict)
                    return;
                throw new InvalidOperationException(
                    $"该设备的 SSH 走私钥登录,没有可用于 SMB 的密码({host})。\r\n" +
                    "请在 Windows 凭据管理器里为该地址保存一份 Windows 凭据,或把设备改成 SSH 密码登录后再试。");
            }

            // 账号写法:先原样提交;若 NAS 要求「主机\用户」形式(独立 Samba 服务器常见)再换一种重试
            var accounts = user.Contains('\\') || user.Contains('@')
                ? new[] { user }
                : new[] { user, $@"{host}\{user}" };

            var lastRc = 0;
            foreach (var account in accounts)
            {
                var rc = WNetAddConnection2(resource, password, account, 0);
                if (rc is 0 or ErrorAlreadyAssigned or ErrorDeviceAlreadyRemembered
                        or ErrorSessionCredentialConflict)
                    return;

                lastRc = rc;
                if (rc is 5 or 1326 or 64) continue; // 认证相关失败 → 换个账号写法再试
                break;
            }

            if (lastRc == 1326)
                throw new InvalidOperationException(
                    $"SMB 登录失败:账号或密码不正确({host})。\r\n" +
                    "文件管理复用该设备的 SSH 账号密码;若 NAS 上 SMB 与 SSH 是两套账号,请在 Windows 凭据管理器保存该地址的 Windows 凭据(开始菜单搜「凭据管理器」→ Windows 凭据 → 添加 Windows 凭据)。");
            if (lastRc == 1219)
                return;
            if (lastRc == ErrorNoNetworkOrBadPath)
                throw new InvalidOperationException($"找不到网络路径:请确认 {host} 在线且 SMB(445 端口)开放。");
            throw new InvalidOperationException(Explain(lastRc, host));
        }, ct);

    /// <summary>断开会话(失败不影响主流程:后续访问可能还挂着别的句柄)。</summary>
    public static Task CloseAsync(NasDevice device) =>
        Task.Run(() =>
        {
            try { WNetCancelConnection2(RootOf(device) + @"\IPC$", 0, true); }
            catch { /* 忽略 */ }
        });

    /// <summary>列出该 NAS 上的磁盘共享(先确保会话已建立)。</summary>
    public static async Task<List<ShareEntry>> ListDiskSharesAsync(
        NasDevice device, bool includeHidden = false, CancellationToken ct = default)
    {
        await OpenAsync(device, ct).ConfigureAwait(false);
        var shares = await NasToolbox.Services.SmbService.ListSharesAsync(device.Host).ConfigureAwait(false);
        return shares
            .Where(s => s.IsDisk && (includeHidden || !s.Hidden))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>常见 WinError → 人话提示(与 SmbService 的错误口径保持一致)。</summary>
    private static string Explain(int rc, string host) => rc switch
    {
        5 => $"访问被拒绝:{host} 拒绝了账号认证,请检查该账号是否被允许访问 SMB。",
        53 => $"找不到网络路径:{host} 可能离线,或 SMB(445 端口)未开放。",
        64 => "指定的网络名不再可用,网络可能中断。",
        121 => "连接信号灯超时(网络不稳定)。",
        1219 => "同一台 NAS 上已有另一组凭据的连接,请先在命令提示符执行 net use 清理。",
        1231 => "无法访问网络:未连接或远程主机不可达。",
        1326 => "账号或密码不正确。",
        _ => $"建立 SMB 会话失败,错误码 {rc}({new Win32Exception(rc).Message})",
    };
}
