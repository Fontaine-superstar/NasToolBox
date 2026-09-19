using NasToolbox.Models;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Services;

/// <summary>
/// NAS 端依赖自检与补装:检测 NasToolbox 在 NAS 上实际用到的命令是否齐全,
/// 缺失时按识别到的包管理器自动安装。全部只读探测先行,安装必须经用户确认后才执行。
/// </summary>
public static class DependencyService
{
    /// <summary>安装命令超时(毫秒)。apt 首次 update 可能较久。</summary>
    public const int InstallTimeoutMs = 600000;

    /// <summary>检测项:命令名 / 显示名 / 缺失后受影响的功能。</summary>
    private static readonly (string Key, string Label, string Why)[] Checks =
    {
        ("curl", "curl", "公网 IP 查询与外网诊断"),
        ("ping", "ping", "网络诊断页的外网连通性测试"),
        ("ip", "ip", "网卡列表、网关、IP 与实时速率"),
        ("lsblk", "lsblk", "硬盘列表"),
        ("smartctl", "smartctl", "硬盘健康度与 SMART 详情"),
        ("dmidecode", "dmidecode", "内存条型号(需 root)"),
        ("lspci", "lspci", "GPU 信息"),
        ("smbd", "Samba", "文件管理与共享枚举"),
        ("docker", "Docker", "Docker 管理页与测速容器"),
        ("compose", "Docker Compose", "compose 项目列表"),
        ("sudo", "sudo", "提权执行安装与高权限采集"),
    };

    /// <summary>
    /// 一次性探测脚本:逐项检查命令是否存在(兼容 /usr/sbin、/sbin 下的 smbd / sudo),
    /// 再识别包管理器、当前 uid 与发行版名称。
    /// </summary>
    private const string ProbeScript =
        "for c in curl ping ip lsblk smartctl dmidecode lspci smbd docker sudo; do " +
        "if command -v $c >/dev/null 2>&1 || [ -x /usr/sbin/$c ] || [ -x /sbin/$c ]; " +
        "then echo \"$c=1\"; else echo \"$c=0\"; fi; done; " +
        "if docker compose version >/dev/null 2>&1; then echo 'compose=1'; else echo 'compose=0'; fi; " +
        "if command -v apt-get >/dev/null 2>&1; then echo MGR=apt; " +
        "elif command -v dnf >/dev/null 2>&1; then echo MGR=dnf; " +
        "elif command -v yum >/dev/null 2>&1; then echo MGR=yum; " +
        "elif command -v apk >/dev/null 2>&1; then echo MGR=apk; " +
        "elif command -v zypper >/dev/null 2>&1; then echo MGR=zypper; " +
        "elif command -v pacman >/dev/null 2>&1; then echo MGR=pacman; " +
        "else echo MGR=none; fi; " +
        "echo UID=$(id -u 2>/dev/null); " +
        "echo DISTRO=$(. /etc/os-release 2>/dev/null && echo \"$NAME\")";

    /// <summary>自检:只做只读探测,不会改动 NAS。</summary>
    public static async Task<DependencyReport> ProbeAsync(NasDevice device, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, ProbeScript, 30000, sudo: false, ct).ConfigureAwait(false);
        if (r.ErrorMessage is not null || r.TimedOut)
        {
            return new DependencyReport
            {
                Ok = false,
                Error = r.TimedOut ? "依赖自检超时。" : r.ErrorMessage,
            };
        }

        var dict = Parse(r.Stdout);
        var mgr = ParseManager(dict.GetValueOrDefault("MGR") ?? "");

        var items = Checks.Select(c => new DependencyItem
        {
            Key = c.Key,
            Label = c.Label,
            Why = c.Why,
            Present = dict.GetValueOrDefault(c.Key) == "1",
            Package = PackageFor(c.Key, mgr),
        }).ToList();

        return new DependencyReport
        {
            Ok = true,
            Manager = mgr,
            IsRoot = dict.GetValueOrDefault("UID") == "0",
            Distro = dict.GetValueOrDefault("DISTRO") ?? "",
            Items = items,
        };
    }

    /// <summary>安装缺失项(需设备已配置 sudo 提权或 root 登录)。返回远端执行结果。</summary>
    public static async Task<SshCommandResult> InstallAsync(
        NasDevice device,
        PkgManager manager,
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        if (manager == PkgManager.None)
        {
            return new SshCommandResult { ErrorMessage = "未识别到受支持的包管理器,无法自动安装。" };
        }

        var pkgs = keys.Select(k => PackageFor(k, manager))
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();
        if (pkgs.Count == 0)
        {
            return new SshCommandResult { ErrorMessage = "没有可安装的包。" };
        }

        var cmd = BuildInstallCommand(manager, pkgs);
        return await SshService.RunAsync(device, cmd, InstallTimeoutMs, sudo: true, ct).ConfigureAwait(false);
    }

    /// <summary>无包管理器时给用户的手动安装提示(按缺失项拼发行版通用命令)。</summary>
    public static string ManualHint(IEnumerable<DependencyItem> missing)
    {
        var names = missing.Select(m => m.Label).Distinct().ToList();
        return names.Count == 0
            ? ""
            : $"该系统没有 apt / dnf / apk 等包管理器(多为群晖、UnRAID、TrueNAS 一类定制系统)," +
              $"请在套件中心或应用商店手动安装:{string.Join("、", names)}。";
    }

    /// <summary>安装完成后的收尾提醒(不是每条都适用,按需返回)。</summary>
    public static string PostInstallHint(IEnumerable<string> installedKeys)
    {
        var set = installedKeys.ToHashSet(StringComparer.Ordinal);
        var tips = new List<string>();
        if (set.Contains("smbd"))
            tips.Add("Samba:执行 sudo smbpasswd -a 用户名 设置与 SSH 相同的密码,并放行 445 端口");
        if (set.Contains("docker"))
            tips.Add("Docker:执行 sudo usermod -aG docker 用户名 后重连 SSH,否则 docker 命令无权限");
        return tips.Count == 0 ? "" : string.Join("；", tips);
    }

    // ---------- 内部实现 ----------

    private static Dictionary<string, string> Parse(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return dict;
    }

    private static PkgManager ParseManager(string value) => value switch
    {
        "apt" => PkgManager.Apt,
        "dnf" => PkgManager.Dnf,
        "yum" => PkgManager.Yum,
        "apk" => PkgManager.Apk,
        "zypper" => PkgManager.Zypper,
        "pacman" => PkgManager.Pacman,
        _ => PkgManager.None,
    };

    private static string BuildInstallCommand(PkgManager mgr, List<string> pkgs)
    {
        var list = string.Join(' ', pkgs);
        return mgr switch
        {
            PkgManager.Apt => $"apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y {list}",
            PkgManager.Dnf => $"dnf install -y {list}",
            PkgManager.Yum => $"yum install -y {list}",
            PkgManager.Apk => $"apk add --no-cache {list}",
            PkgManager.Zypper => $"zypper -n install {list}",
            PkgManager.Pacman => $"pacman -S --noconfirm {list}",
            _ => "",
        };
    }

    /// <summary>命令键 → 包名(同一组件在不同发行版里名字并不一致)。</summary>
    private static string PackageFor(string key, PkgManager mgr) => key switch
    {
        "curl" => "curl",
        "ping" => mgr is PkgManager.Apt ? "iputils-ping" : "iputils",
        "ip" => mgr is PkgManager.Dnf or PkgManager.Yum ? "iproute" : "iproute2",
        "lsblk" => "util-linux",
        "smartctl" => "smartmontools",
        "dmidecode" => "dmidecode",
        "lspci" => "pciutils",
        "smbd" => "samba",
        "docker" => mgr is PkgManager.Apt ? "docker.io" : "docker",
        "compose" => mgr is PkgManager.Apt or PkgManager.Dnf or PkgManager.Yum
            ? "docker-compose-plugin"
            : "docker-compose",
        "sudo" => "sudo",
        _ => "",
    };
}
