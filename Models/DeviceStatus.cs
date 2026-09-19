using System.Text.Json.Serialization;

namespace NasToolbox.Models;

/// <summary>
/// 一次在线检测的持久化结果。落盘在 %LocalAppData%\NasToolbox\device_status.json,
/// 以设备 Host 为键,供下次启动 / 打开设备页时先展示上次状态。
/// </summary>
public sealed class DeviceStatus
{
    [JsonPropertyName("online")]
    public bool Online { get; set; }

    /// <summary>检测依据,例「192.168.10.3 · 1 ms」或「SSH 22 可达 · 19 ms」。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("checkedAt")]
    public DateTime CheckedAt { get; set; }

    /// <summary>状态的可读时间,例「今天 19:38」「昨天 09:02」「09-16 14:20」。</summary>
    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var today = DateTime.Today;
            if (CheckedAt.Date == today) return $"今天 {CheckedAt:HH:mm}";
            if (CheckedAt.Date == today.AddDays(-1)) return $"昨天 {CheckedAt:HH:mm}";
            return $"{CheckedAt:MM-dd HH:mm}";
        }
    }

    /// <summary>带检测时间的状态文字,用于区分「刚检测」与「上次记录」。</summary>
    [JsonIgnore]
    public string CachedText =>
        Online
            ? (Detail.Length > 0 ? $"{Detail} · {TimeText}" : $"在线 · {TimeText}")
            : $"离线 · {TimeText}";
}
