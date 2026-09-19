using System.Security.Cryptography;
using System.Text.Json;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// 主机指纹信任库(简化版 known_hosts):%LocalAppData%\NasToolbox\known_hosts.json。
/// 记录 host:port → SHA256 指纹(OpenSSH 显示风格),用于识别设备重装 / 中间人攻击。
/// </summary>
public static class SshHostKeyStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NasToolbox");

    private static readonly string FilePath = Path.Combine(Dir, "known_hosts.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly object Sync = new();

    private static Dictionary<string, string> _map = Load();

    /// <summary>由主机公钥原始字节算出 OpenSSH 风格的 SHA256 指纹(SHA256:base64)。</summary>
    public static string ComputeFingerprint(byte[] hostKey)
    {
        if (hostKey is null || hostKey.Length == 0) return "";
        var hash = SHA256.HashData(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    private static string KeyOf(string host, int port) => $"{host}:{port}";

    /// <summary>已记录的指纹;未记录返回空串。</summary>
    public static string Get(string host, int port)
    {
        lock (Sync) return _map.TryGetValue(KeyOf(host, port), out var fp) ? fp : "";
    }

    /// <summary>该指纹是否与已记录的一致。</summary>
    public static bool IsTrusted(string host, int port, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return false;
        var known = Get(host, port);
        return known.Length > 0 && string.Equals(known, fingerprint, StringComparison.Ordinal);
    }

    /// <summary>是否已有该主机的任何指纹记录(用于区分「首次连接」与「指纹变更」)。</summary>
    public static bool HasRecord(string host, int port) => Get(host, port).Length > 0;

    /// <summary>信任(或更新)该主机的指纹。</summary>
    public static void Trust(string host, int port, string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        lock (Sync)
        {
            _map[KeyOf(host, port)] = fingerprint;
            Save();
        }
    }

    /// <summary>删除该主机的指纹记录(设备重装 / 换 SSH 服务后重新信任)。</summary>
    public static void Forget(string host, int port)
    {
        lock (Sync)
        {
            if (_map.Remove(KeyOf(host, port))) Save();
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string, string>();
            }
        }
        catch
        {
            // 文件损坏:当作空库
        }
        return new Dictionary<string, string>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, JsonOpts));
        }
        catch
        {
            // 写盘失败不致命,仅影响指纹记忆
        }
    }
}
