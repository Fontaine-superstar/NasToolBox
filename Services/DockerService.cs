using System.Text;
using System.Text.Json;
using NasToolbox.Models;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Services;

/// <summary>
/// 通过 SSH 管理远端 NAS 上的 Docker:容器(列出 / 启停 / 删除 / 日志 / 部署新容器),
/// 以及概览、本地镜像、网络、Compose 项目管理。
/// 需要 root 的命令依赖设备配置的「root 会话」权限模式(见 <see cref="SshService"/>)。
/// </summary>
public static class DockerService
{
    /// <summary>docker ps 的输出格式:制表符分隔,便于解析(端口里可能有逗号,不能用逗号分隔)。</summary>
    private const string PsFormat = "{{.Names}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}";

    /// <summary>列出全部容器(含已停止的)。</summary>
    public static async Task<List<DockerContainer>> ListAsync(NasDevice device, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, $"docker ps -a --format '{PsFormat}'", 25000, ct)
            .ConfigureAwait(false);

        if (r.ErrorMessage is not null) throw new InvalidOperationException(r.ErrorMessage);
        if (r.TimedOut) throw new InvalidOperationException("执行 docker ps 超时(25 秒)。");

        var text = r.Stdout ?? "";
        if (r.ExitCode != 0)
        {
            var err = (r.Stderr ?? "").Trim();
            if (err.Length == 0) err = (r.Stdout ?? "").Trim();
            throw new InvalidOperationException(Explain(err));
        }

        var list = new List<DockerContainer>();
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim('\r');
            if (t.Length == 0) continue;

            var parts = t.Split('\t');
            list.Add(new DockerContainer
            {
                Name = parts.Length > 0 ? parts[0].Trim() : "",
                Image = parts.Length > 1 ? parts[1].Trim() : "",
                Status = parts.Length > 2 ? parts[2].Trim() : "",
                Ports = parts.Length > 3 ? parts[3].Trim() : "",
            });
        }
        return list;
    }

    /// <summary>启动 / 停止 / 重启 / 删除容器;返回命令输出(通常为空)。</summary>
    public static async Task<string> ActAsync(
        NasDevice device, string container, DockerAction action, CancellationToken ct = default)
    {
        var verb = action switch
        {
            DockerAction.Start => "start",
            DockerAction.Stop => "stop",
            DockerAction.Restart => "restart",
            DockerAction.Remove => "rm -f",
            _ => "start",
        };

        var timeout = action is DockerAction.Stop or DockerAction.Remove ? 60000 : 40000;
        var r = await SshService.RunAsync(
            device, $"docker {verb} {Quote(container)}", timeout, ct).ConfigureAwait(false);

        if (r.ErrorMessage is not null) throw new InvalidOperationException(r.ErrorMessage);
        if (r.TimedOut) throw new InvalidOperationException($"docker {verb} 超时({timeout / 1000} 秒)。");

        var output = ((r.Stdout ?? "") + (r.Stderr ?? "")).Trim();
        if (r.ExitCode != 0) throw new InvalidOperationException(Explain(output));
        return output;
    }

    /// <summary>查看容器日志尾部。</summary>
    public static async Task<string> LogsAsync(
        NasDevice device, string container, int tail = 300, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(
            device, $"docker logs --tail {tail} {Quote(container)}", 30000, ct).ConfigureAwait(false);

        if (r.ErrorMessage is not null) throw new InvalidOperationException(r.ErrorMessage);
        if (r.TimedOut) throw new InvalidOperationException("拉取日志超时(30 秒)。");

        // docker logs 的正常输出也会走 stderr,两边都要带上
        var text = ((r.Stdout ?? "") + "\n" + (r.Stderr ?? "")).Trim('\n', '\r');
        if (r.ExitCode != 0 && text.Length == 0) throw new InvalidOperationException("拉取日志失败,容器可能已不存在。");
        return text;
    }

    /// <summary>新建容器部署参数。</summary>
    public sealed record DockerCreateSpec
    {
        /// <summary>镜像名(仓库[:标签]),例:badapple9/speedtest-x。</summary>
        public string Image { get; init; } = "";

        /// <summary>容器名;留空则由 docker 随机命名。</summary>
        public string Name { get; init; } = "";

        /// <summary>重启策略:unless-stopped / always / no。</summary>
        public string RestartPolicy { get; init; } = "unless-stopped";

        /// <summary>端口映射列表,每项为(宿主端口, 容器端口)。</summary>
        public IReadOnlyList<(int Host, int Container)> Ports { get; init; } =
            Array.Empty<(int, int)>();

        /// <summary>创建前是否先 docker pull(拉取/更新镜像)。</summary>
        public bool Pull { get; init; } = true;
    }

    /// <summary>部署新容器:可选先拉取镜像,再 docker run -d 启动;返回容器 ID。</summary>
    public static async Task<string> CreateAsync(
        NasDevice device, DockerCreateSpec spec, CancellationToken ct = default)
    {
        var image = spec.Image.Trim();

        if (spec.Pull)
        {
            // 拉取大镜像可能要几分钟,超时放宽到 10 分钟
            var pull = await SshService.RunAsync(device, $"docker pull {image}", 600000, ct);
            if (!pull.Success) throw new InvalidOperationException($"拉取镜像失败:\r\n{Truncate(pull.Output)}");
        }

        var cmd = new StringBuilder("docker run -d");
        if (spec.Name.Length > 0) cmd.Append(" --name ").Append(Quote(spec.Name));
        cmd.Append(" --restart ").Append(spec.RestartPolicy);
        foreach (var (host, container) in spec.Ports)
            cmd.Append(" -p ").Append(host).Append(':').Append(container);
        cmd.Append(' ').Append(image);

        var run = await SshService.RunAsync(device, cmd.ToString(), 60000, ct);
        if (!run.Success) throw new InvalidOperationException(Truncate(run.Output));

        return (run.Stdout ?? "").Trim();
    }

    /// <summary>报错输出截断,避免 InfoBar 被刷屏。</summary>
    private static string Truncate(string s) =>
        string.IsNullOrWhiteSpace(s) ? "(无输出)" : (s.Trim().Length > 400 ? s.Trim()[..400] + "…" : s.Trim());

    /// <summary>Docker 概览:服务端版本、容器统计、镜像数、网络数。</summary>
    public static async Task<(string Version, int Containers, int Running, int Stopped, int Images, int Networks)>
        InfoAsync(NasDevice device, CancellationToken ct = default)
    {
        var info = await SshService.RunAsync(device,
            "docker info --format '{{.ServerVersion}}|{{.Containers}}|{{.ContainersRunning}}|{{.ContainersStopped}}|{{.Images}}'",
            30000, ct);
        if (!info.Success) throw new InvalidOperationException(Explain(info.Output));

        var p = (info.Stdout ?? "").Replace("\r", "").Trim().Split('|');
        string At(int i) => i < p.Length ? p[i].Trim() : "";

        var nets = await SshService.RunAsync(device, "docker network ls --format '{{.Name}}' | wc -l", 20000, ct);
        var netsCount = int.TryParse((nets.Stdout ?? "").Trim(), out var n) ? n : 0;

        return (At(0).Length > 0 ? At(0) : "—",
            ParseInt(At(1)), ParseInt(At(2)), ParseInt(At(3)), ParseInt(At(4)), netsCount);
    }

    private static int ParseInt(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;

    /// <summary>列出本地镜像。</summary>
    public static async Task<List<DockerImage>> ListImagesAsync(NasDevice device, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device,
            "docker images --format '{{.Repository}}:{{.Tag}}\\t{{.ID}}\\t{{.Size}}\\t{{.CreatedSince}}'",
            30000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(r.Output));

        var list = new List<DockerImage>();
        foreach (var line in (r.Stdout ?? "").Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var f = t.Split('\t');
            list.Add(new DockerImage
            {
                RepoTag = f.Length > 0 ? f[0].Trim() : "",
                Id = f.Length > 1 ? f[1].Trim() : "",
                Size = f.Length > 2 ? f[2].Trim() : "",
                Created = f.Length > 3 ? f[3].Trim() : "",
            });
        }
        return list;
    }

    /// <summary>删除镜像(按 ID)。</summary>
    public static async Task RemoveImageAsync(NasDevice device, string imageId, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, $"docker rmi {imageId}", 120000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));
    }

    /// <summary>清理悬空镜像,返回 docker 输出摘要。</summary>
    public static async Task<string> PruneImagesAsync(NasDevice device, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, "docker image prune -f", 180000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));
        return Truncate(r.Output);
    }

    /// <summary>列出 docker 网络。</summary>
    public static async Task<List<DockerNetwork>> ListNetworksAsync(NasDevice device, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device,
            "docker network ls --format '{{.Name}}\\t{{.Driver}}\\t{{.Scope}}'", 20000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(r.Output));

        var list = new List<DockerNetwork>();
        foreach (var line in (r.Stdout ?? "").Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var f = t.Split('\t');
            list.Add(new DockerNetwork
            {
                Name = f.Length > 0 ? f[0].Trim() : "",
                Driver = f.Length > 1 ? f[1].Trim() : "",
                Scope = f.Length > 2 ? f[2].Trim() : "",
            });
        }
        return list;
    }

    /// <summary>删除用户自定义网络(内置网络 docker 会拒绝)。</summary>
    public static async Task RemoveNetworkAsync(NasDevice device, string name, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, $"docker network rm {SshService.ShellQuote(name)}", 60000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));
    }

    /// <summary>探测 compose 命令前缀:v2 插件(docker compose)优先,回退 docker-compose。</summary>
    private static async Task<string> ComposePrefixAsync(NasDevice device, CancellationToken ct)
    {
        var v2 = await SshService.RunAsync(device, "docker compose version", 20000, ct);
        if (v2.Success) return "docker compose";
        var v1 = await SshService.RunAsync(device, "docker-compose version", 20000, ct);
        if (v1.Success) return "docker-compose";
        throw new InvalidOperationException("NAS 上未检测到 Docker Compose(docker compose / docker-compose)。");
    }

    /// <summary>列出 compose 项目(含已停止的)。</summary>
    public static async Task<List<DockerComposeProject>> ListComposeAsync(NasDevice device, CancellationToken ct = default)
    {
        var prefix = await ComposePrefixAsync(device, ct);
        var r = await SshService.RunAsync(device, $"{prefix} ls -a --format json", 30000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));

        var text = (r.Stdout ?? "").Trim();
        var list = new List<DockerComposeProject>();
        if (text.Length == 0) return list;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    list.Add(ComposeEntry(e));
        }
        catch (JsonException)
        {
            // 兼容旧版逐行 JSON 输出
            foreach (var line in text.Split('\n'))
            {
                try
                {
                    using var doc2 = JsonDocument.Parse(line.Trim());
                    if (doc2.RootElement.ValueKind == JsonValueKind.Object) list.Add(ComposeEntry(doc2.RootElement));
                }
                catch (JsonException) { /* 跳过坏行 */ }
            }
        }
        return list;
    }

    private static DockerComposeProject ComposeEntry(JsonElement e)
    {
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
        return new DockerComposeProject { Name = S("Name"), Status = S("Status"), ConfigFiles = S("ConfigFiles") };
    }

    /// <summary>对 compose 项目执行 start / stop / restart / down。</summary>
    public static async Task ComposeActionAsync(
        NasDevice device, string project, string action, CancellationToken ct = default)
    {
        var prefix = await ComposePrefixAsync(device, ct);
        var timeout = action == "down" ? 300000 : 120000;
        var r = await SshService.RunAsync(device, $"{prefix} -p {SshService.ShellQuote(project)} {action}", timeout, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));
    }

    /// <summary>
    /// 用 compose 文件部署(up -d)。提供 localComposePath 时先经 SFTP 上传到 /tmp/nastb-compose/,
    /// 否则 remoteFile 必须是 NAS 上已存在的路径。
    /// </summary>
    public static async Task<string> ComposeUpAsync(
        NasDevice device, string remoteFile, string? localComposePath = null, CancellationToken ct = default)
    {
        var file = remoteFile;
        if (!string.IsNullOrEmpty(localComposePath))
        {
            var name = Path.GetFileName(localComposePath);
            file = $"/tmp/nastb-compose/{name}";
            await SftpService.UploadAsync(device, localComposePath, file, null, ct).ConfigureAwait(false);
        }

        var prefix = await ComposePrefixAsync(device, ct);
        var r = await SshService.RunAsync(device, $"{prefix} -f {SshService.ShellQuote(file)} up -d", 600000, ct);
        if (!r.Success) throw new InvalidOperationException(Explain(Truncate(r.Output)));
        return file;
    }

    // ---------- 本机镜像(应用目录 img\ 下的 tar)----------

    /// <summary>本机镜像 tar 存放目录:应用目录下 img\。</summary>
    public static string LocalImageDir => Path.Combine(AppContext.BaseDirectory, "img");

    /// <summary>列出本机镜像 tar 文件(按修改时间倒序)。</summary>
    public static List<LocalImageFile> ListLocalImageFiles()
    {
        var dir = LocalImageDir;
        var list = new List<LocalImageFile>();
        if (!Directory.Exists(dir)) return list;

        foreach (var f in Directory.EnumerateFiles(dir, "*.tar"))
        {
            var fi = new FileInfo(f);
            list.Add(new LocalImageFile
            {
                Name = fi.Name,
                FullPath = fi.FullName,
                SizeBytes = fi.Length,
                Modified = fi.LastWriteTime,
            });
        }
        return list.OrderByDescending(x => x.Modified).ToList();
    }

    /// <summary>把本机镜像 tar 上传到 NAS 并 docker load 导入;返回 docker load 输出摘要。</summary>
    public static async Task<string> LoadLocalImageAsync(
        NasDevice device, string localTarPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var remote = "/tmp/nastb_img_" + Path.GetFileName(localTarPath);
        await SftpService.UploadAsync(device, localTarPath, remote, progress, ct).ConfigureAwait(false);

        try
        {
            var load = await SshService.RunAsync(device,
                $"docker load -i {SshService.ShellQuote(remote)}", 600000, ct);
            if (!load.Success) throw new InvalidOperationException(Explain(Truncate(load.Output)));
            return Truncate(load.Output);
        }
        finally
        {
            // 导入完清理远端临时文件(失败无碍)
            _ = SshService.RunAsync(device, $"rm -f {SshService.ShellQuote(remote)}", 15000, ct);
        }
    }

    /// <summary>把 docker 的常见报错翻译成可执行的建议。</summary>
    public static string Explain(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "docker 命令返回失败,但没有输出。";

        if (s.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return $"远端没有 docker 命令,可能未安装或不在 PATH 中。\r\n原始输出:{s}";

        if (s.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return $"当前用户无权访问 Docker 守护进程。请在设备「编辑 → 权限与高级」里选择 root 会话,或把该用户加入 docker 组。\r\n原始输出:{s}";

        if (s.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
            return $"Docker 守护进程未运行或无法连接。\r\n原始输出:{s}";

        return s;
    }

    /// <summary>容器名加引号,避免特殊字符被 shell 解析。</summary>
    private static string Quote(string name) => "'" + (name ?? "").Replace("'", "'\\''") + "'";
}

/// <summary>对容器执行的操作。</summary>
public enum DockerAction
{
    Start,
    Stop,
    Restart,
    Remove,
}