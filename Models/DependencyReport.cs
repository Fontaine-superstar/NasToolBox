namespace NasToolbox.Models;

/// <summary>包管理器类型(由 NAS 上实际可用的安装命令推断)。</summary>
public enum PkgManager
{
    /// <summary>未识别到受支持的包管理器(群晖 / UnRAID / TrueNAS 等),只能给出手动命令。</summary>
    None,
    Apt,
    Dnf,
    Yum,
    Apk,
    Zypper,
    Pacman,
}

/// <summary>单个依赖项的检测结果。</summary>
public sealed class DependencyItem
{
    /// <summary>内部键(与检测脚本输出一致)。</summary>
    public string Key { get; init; } = "";

    /// <summary>面向用户的名称。</summary>
    public string Label { get; init; } = "";

    /// <summary>缺失后受影响的功能,用于说明"为什么要装"。</summary>
    public string Why { get; init; } = "";

    /// <summary>命令是否已存在。</summary>
    public bool Present { get; set; }

    /// <summary>按当前包管理器解析出的包名;空表示无法自动安装。</summary>
    public string Package { get; set; } = "";
}

/// <summary>一次依赖自检的完整结果。</summary>
public sealed class DependencyReport
{
    /// <summary>检测命令是否成功执行。</summary>
    public bool Ok { get; init; }

    /// <summary>检测失败原因(SSH 异常 / 超时)。</summary>
    public string? Error { get; init; }

    public PkgManager Manager { get; init; }

    /// <summary>SSH 登录用户是否为 root(root 时安装无需 sudo)。</summary>
    public bool IsRoot { get; init; }

    /// <summary>发行版名称(展示用,可能为空)。</summary>
    public string Distro { get; init; } = "";

    public List<DependencyItem> Items { get; init; } = new();

    public bool AllPresent => Items.Count > 0 && Items.All(i => i.Present);

    public List<DependencyItem> Missing => Items.Where(i => !i.Present).ToList();

    /// <summary>可自动安装的缺失项(解析出了包名)。</summary>
    public List<DependencyItem> Installable => Missing.Where(i => i.Package.Length > 0).ToList();
}
