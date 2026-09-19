using NasToolbox.Models;
using Renci.SshNet;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// 基于 SFTP 的远端文件操作:列目录、上传、下载、删除、建目录。
/// 文件传输是长任务,握手开销可忽略,因此每个操作使用独立连接,操作结束即断开。
/// 注意:进度回调发生在后台线程,更新 UI 需自行切回 UI 线程。
/// </summary>
public static class SftpService
{
    /// <summary>列出远端目录内容(目录排在前面,其余按名称排序)。</summary>
    public static async Task<List<SftpEntry>> ListAsync(
        NasDevice device, string path = ".", CancellationToken ct = default)
    {
        var configError = SshService.Validate(device);
        if (configError is not null) throw new SshConnectException(SshConnectFailure.NotConfigured, configError);

        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);

        var items = await Task.Run(() =>
        {
            var list = new List<SftpEntry>();
            foreach (var f in client.ListDirectory(path))
            {
                if (f.Name is "." or "..") continue;
                list.Add(new SftpEntry
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsDirectory = f.IsDirectory,
                    Length = f.Length,
                    LastWriteTime = f.LastWriteTime,
                });
            }
            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct).ConfigureAwait(false);

        return items;
    }

    /// <summary>远端路径是否存在。</summary>
    public static async Task<bool> ExistsAsync(
        NasDevice device, string remotePath, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);
        return await Task.Run(() => client.Exists(remotePath), ct).ConfigureAwait(false);
    }

    /// <summary>创建远端目录(已存在则不报错)。</summary>
    public static async Task CreateDirectoryAsync(
        NasDevice device, string remotePath, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath)) client.CreateDirectory(remotePath);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>删除远端文件或目录。</summary>
    public static async Task DeleteAsync(
        NasDevice device, string remotePath, bool recursive = false, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath)) return;

            var isDir = client.Get(remotePath).IsDirectory;
            if (isDir)
            {
                if (recursive) client.DeleteDirectory(remotePath);
                else client.DeleteDirectory(remotePath); // SFTP 只能删空目录,非递归时由服务端报错
            }
            else
            {
                client.DeleteFile(remotePath);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>上传本地文件到远端,返回已传输字节数。</summary>
    public static async Task<long> UploadAsync(
        NasDevice device,
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("本地文件不存在。", localPath);

        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);
        var total = new FileInfo(localPath).Length;

        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        await Task.Run(() => client.UploadFile(fs, remotePath, true,
            sent => Report(progress, sent, total)), ct).ConfigureAwait(false);

        return total;
    }

    /// <summary>下载远端文件到本地,返回已传输字节数。</summary>
    public static async Task<long> DownloadAsync(
        NasDevice device,
        string remotePath,
        string localPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using var client = await ConnectAsync(device, ct).ConfigureAwait(false);

        var total = await Task.Run(() => client.Get(remotePath).Length, ct).ConfigureAwait(false);

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        await Task.Run(() => client.DownloadFile(remotePath, fs,
            got => Report(progress, got, total)), ct).ConfigureAwait(false);

        return total;
    }

    private static void Report(IProgress<double>? progress, ulong done, long total)
    {
        if (progress is null || total <= 0) return;
        progress.Report(Math.Clamp(done / (double)total, 0, 1));
    }

    private static async Task<SftpClient> ConnectAsync(NasDevice device, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var state = new SshClientFactory.HostKeyState();
            var (info, failure, message) = SshClientFactory.BuildConnectionInfo(
                device,
                new SshClientFactory.CreateOptions { TrustNewHostKey = true },
                state);

            if (info is null) throw new SshConnectException(failure, message);

            var client = new SftpClient(info)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            };
            client.HostKeyReceived += SshClientFactory.CreateHostKeyHandler(
                device, new SshClientFactory.CreateOptions { TrustNewHostKey = true }, state);
            client.Connect();
            return client;
        }, ct).ConfigureAwait(false);
    }
}
