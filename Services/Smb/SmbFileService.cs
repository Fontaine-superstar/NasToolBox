using NasToolbox.Models;

namespace NasToolbox.Services.Smb;

/// <summary>
/// SMB 远端文件操作:列目录、上传、下载、删除、建目录。
/// 会话由 <see cref="SmbSession"/> 建立后,\\host\share 可以直接用普通 IO API 读写,
/// 因此这里的操作本质是本地文件操作,速度取决于局域网,不走 SSH。
/// 注意:进度回调发生在后台线程,更新 UI 需自行切回 UI 线程。
/// </summary>
public static class SmbFileService
{
    /// <summary>列出 UNC 目录下的内容(目录排在前面,其余按名称排序)。</summary>
    public static async Task<List<FileEntry>> ListAsync(
        string uncPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var list = new List<FileEntry>();

            foreach (var dir in Directory.EnumerateDirectories(uncPath))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(dir);
                    list.Add(new FileEntry
                    {
                        Name = info.Name,
                        FullPath = dir,
                        IsDirectory = true,
                        LastWriteTime = info.LastWriteTime,
                    });
                }
                catch (UnauthorizedAccessException) { /* 无权限读取的目录:略过 */ }
                catch (IOException) { /* 正在被占用/已消失:略过 */ }
            }

            foreach (var file in Directory.EnumerateFiles(uncPath))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    list.Add(new FileEntry
                    {
                        Name = info.Name,
                        FullPath = file,
                        IsDirectory = false,
                        Length = info.Length,
                        LastWriteTime = info.LastWriteTime,
                    });
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>UNF 路径是否存在(文件或目录)。</summary>
    public static Task<bool> ExistsAsync(string uncPath) =>
        Task.Run(() => Directory.Exists(uncPath) || File.Exists(uncPath));

    /// <summary>创建远端目录(已存在则不报错)。</summary>
    public static async Task CreateDirectoryAsync(string uncPath, CancellationToken ct = default) =>
        await Task.Run(() =>
        {
            if (!Directory.Exists(uncPath)) Directory.CreateDirectory(uncPath);
        }, ct).ConfigureAwait(false);

    /// <summary>删除远端文件或目录(目录连带内部内容)。</summary>
    public static async Task DeleteAsync(string uncPath, bool recursive = true, CancellationToken ct = default) =>
        await Task.Run(() =>
        {
            if (Directory.Exists(uncPath)) Directory.Delete(uncPath, recursive);
            else if (File.Exists(uncPath)) File.Delete(uncPath);
        }, ct).ConfigureAwait(false);

    /// <summary>上传本地文件到远端,返回已传输字节数。</summary>
    public static async Task<long> UploadAsync(
        string localPath, string remotePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) throw new FileNotFoundException("本地文件不存在。", localPath);

        return await CopyAsync(localPath, remotePath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>下载远端文件到本地,返回已传输字节数。</summary>
    public static async Task<long> DownloadAsync(
        string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(remotePath)) throw new FileNotFoundException("NAS 上找不到该文件。", remotePath);

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        return await CopyAsync(remotePath, localPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>带进度的流式拷贝(任何一端是 UNC 路径都适用)。</summary>
    private static async Task<long> CopyAsync(
        string source, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var total = new FileInfo(source).Length;

        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

        var buffer = new byte[1 << 16];
        long copied = 0;
        var lastReport = DateTime.UtcNow;

        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;

            // 进度每隔约 200ms 才回调一次,避免大文件把 UI 线程刷爆
            if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
            {
                lastReport = DateTime.UtcNow;
                if (total > 0) progress.Report(Math.Clamp(copied / (double)total, 0, 1));
            }
        }

        progress?.Report(1);
        return copied;
    }
}
