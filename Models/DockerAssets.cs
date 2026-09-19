namespace NasToolbox.Models;

/// <summary>docker images 列表项。</summary>
public sealed class DockerImage
{
    /// <summary>仓库:标签;悬空镜像为 &lt;none&gt;。</summary>
    public string RepoTag { get; set; } = "";

    public string Id { get; set; } = "";
    public string Size { get; set; } = "";
    public string Created { get; set; } = "";

    /// <summary>是否为悬空镜像(无标签)。</summary>
    public bool Dangling => RepoTag.StartsWith("<none>") || RepoTag.Length == 0;

    /// <summary>列表展示名:悬空镜像显示短 ID。</summary>
    public string DisplayTag => Dangling
        ? "‹悬空镜像› " + (Id.Length > 12 ? Id[..12] : Id)
        : RepoTag;
}

/// <summary>docker network 列表项。</summary>
public sealed class DockerNetwork
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";

    /// <summary>docker 内置网络,不可删除。</summary>
    public bool Builtin => Name is "bridge" or "host" or "none";

    /// <summary>是否允许删除。</summary>
    public bool CanDelete => !Builtin;
}

/// <summary>docker compose 项目(docker compose ls)。</summary>
public sealed class DockerComposeProject
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string ConfigFiles { get; set; } = "";
}

/// <summary>本机镜像 tar 文件(应用目录 img\ 下,可上传到 NAS 后 docker load 导入)。</summary>
public sealed class LocalImageFile
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>人类可读大小。</summary>
    public string SizeText => SizeBytes >= (1L << 30)
        ? $"{SizeBytes / 1073741824.0:F1} GB"
        : $"{SizeBytes / 1048576.0:F1} MB";

    /// <summary>修改时间展示。</summary>
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
}