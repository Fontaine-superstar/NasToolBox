using System.Text.Json;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// 设备在线状态的持久化:%LocalAppData%\NasToolbox\device_status.json。
/// 以 Host 为键(不区分大小写),文件损坏时返回空,不抛异常。
/// </summary>
public static class DeviceStatusStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NasToolbox");

    private static readonly string FilePath = Path.Combine(Dir, "device_status.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Dictionary<string, DeviceStatus> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, DeviceStatus>>(
                    File.ReadAllText(FilePath));
                if (dict is not null)
                {
                    foreach (var kv in dict) Cache[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // 损坏则当作空,不致命
        }
    }

    /// <summary>取某主机上次检测的状态;没有记录返回 null。</summary>
    public static DeviceStatus? Get(string host)
    {
        var key = (host ?? "").Trim();
        if (key.Length == 0) return null;
        EnsureLoaded();
        return Cache.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>写入一条状态(仅更新内存,不落盘;批量保存用 <see cref="Save"/>)。</summary>
    public static void Set(string host, DeviceStatus status)
    {
        var key = (host ?? "").Trim();
        if (key.Length == 0) return;
        EnsureLoaded();
        Cache[key] = status;
    }

    /// <summary>将内存中的状态写入磁盘。</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Cache, JsonOpts));
        }
        catch
        {
            // 落盘失败不影响本次会话的展示
        }
    }
}
