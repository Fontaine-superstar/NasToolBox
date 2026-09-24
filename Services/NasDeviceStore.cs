using System.Text.Json;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// NAS 设备台账持久化:%LocalAppData%\NasToolbox\devices.json。
/// 文件不存在或损坏时返回空列表,不抛异常。
/// </summary>
public static class NasDeviceStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NasToolbox");

    private static readonly string FilePath = Path.Combine(Dir, "devices.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 中文原样输出,不转义为 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<NasDevice> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var devices = AssignDisplayNames(
                    JsonSerializer.Deserialize<List<NasDevice>>(File.ReadAllText(FilePath))
                    ?? new List<NasDevice>());
                // 旧「Web 管理地址」→ Web 管理端口:迁移后旧字段清空,保存时不再落盘
                // 旧「sudo 逐条提权」→ root 会话:迁移后旧字段清空,保存时不再落盘
                foreach (var d in devices)
                {
                    d.MigrateLegacyWeb();
                    d.MigrateLegacyPrivilege();
                }
                return devices;
            }
        }
        catch
        {
            // 损坏则当作空列表,不致命
        }
        return new List<NasDevice>();
    }

    /// <summary>
    /// 计算显示名:以 Hostname(未获取时用 Host)为名,同名设备从第二台起加 (2)(3) 后缀,
    /// 先加入台账的保持原名。
    /// </summary>
    private static List<NasDevice> AssignDisplayNames(List<NasDevice> devices)
    {
        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            var b = d.NameBase;
            total[b] = total.TryGetValue(b, out var c) ? c + 1 : 1;
        }

        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            var b = d.NameBase;
            var n = used.TryGetValue(b, out var c) ? c + 1 : 1;
            used[b] = n;
            d.DisplayName = total[b] > 1 && n > 1 ? $"{b} ({n})" : b;
        }

        return devices;
    }

    public static void Save(IEnumerable<NasDevice> devices)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices.ToList(), JsonOpts));
    }

    /// <summary>用户在设备管理中勾选的「当前管理设备」;未勾选时为 null。</summary>
    public static NasDevice? GetCurrent(IReadOnlyList<NasDevice> devices) =>
        devices.FirstOrDefault(d => d.IsCurrent);

    /// <summary>
    /// 取当前管理设备;若用户还没勾选过,回退到第一台,
    /// 保证功能页(Docker 等)永远有一个可用的默认设备。
    /// </summary>
    public static NasDevice? GetCurrentOrDefault(IReadOnlyList<NasDevice> devices) =>
        GetCurrent(devices) ?? devices.FirstOrDefault();

    /// <summary>把某台设备设为当前管理设备(其他设备自动取消),并落盘。</summary>
    public static void SetCurrent(IReadOnlyList<NasDevice> devices, NasDevice? target)
    {
        foreach (var d in devices) d.IsCurrent = target is not null && ReferenceEquals(d, target);
        Save(devices);
    }

    /// <summary>按 Host + Name 匹配并替换单台设备后落盘(用于连接后回填 Hostname 等场景);找不到则忽略。</summary>
    public static void UpdateDevice(NasDevice updated)
    {
        var devices = Load();
        var idx = devices.FindIndex(d =>
            string.Equals(d.Host, updated.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Name, updated.Name, StringComparison.Ordinal));
        if (idx < 0) return;
        devices[idx] = updated;
        Save(devices);
    }
}