using System.Text.RegularExpressions;
using NasToolbox.Models;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Services;

/// <summary>SMART 属性表的一行(ATA 盘)。NVMe 盘无此表,详情只能看原始输出。</summary>
public sealed class SmartAttribute
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Worst { get; init; } = "";
    public string Thresh { get; init; } = "";
    public string Type { get; init; } = "";
    public string Updated { get; init; } = "";
    public string WhenFailed { get; init; } = "";
    public string Raw { get; init; } = "";

    /// <summary>WHEN_FAILED 为 FAILING_NOW:该属性当前已判定失效。</summary>
    public bool IsFailed { get; init; }

    /// <summary>当前值已跌破厂商阈值(CURRENT &lt;= THRESH)。</summary>
    public bool IsWarning { get; init; }
}

/// <summary>一行需要用户立刻注意的提示;Critical 为真表示「马上要坏」,用红色强调。</summary>
public sealed record SmartWarning(string Text, bool Critical);

/// <summary>一块硬盘的完整 SMART 信息;解析出的结构化部分 + 供查阅的原始输出。</summary>
public sealed class SmartReport
{
    /// <summary>被查询的设备路径,如 /dev/sda。</summary>
    public string DevicePath { get; init; } = "";

    /// <summary>基本信息(型号 / 序列号 / 容量…)键值对,已映射为中文标签。</summary>
    public List<(string Key, string Value)> Info { get; init; } = new();

    /// <summary>ATA 属性表;NVMe 盘为空。</summary>
    public List<SmartAttribute> Attributes { get; init; } = new();

    /// <summary>温度文本,如「42 °C」;拿不到时为空。</summary>
    public string Temperature { get; init; } = "";

    /// <summary>厂商 / 工具给出的醒目嘱托,如「预计 24 小时内失效,请立刻备份」。</summary>
    public List<SmartWarning> Warnings { get; init; } = new();

    /// <summary>smartctl 原始输出(已剔除 root 会话的命令回显)。</summary>
    public string Raw { get; init; } = "";

    /// <summary>失败原因;为 null 表示拿到了输出。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 读取单块硬盘的完整 SMART 信息(smartctl -a)。
/// 走设备既有的 SSH 提权通道;USB 硬盘盒 / SAS 扩展柜这类需要显式指定协议的盘会自动以 -d sat 重试。
/// </summary>
public static class SmartService
{
    /// <summary>设备名合法性校验:只允许 /dev 下的简单名字,杜绝拼进 shell 元字符。</summary>
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>输出里出现这些字样说明 smartctl 需要显式指定设备类型(常见于 USB 硬盘盒 / 扩展柜)。</summary>
    private static readonly string[] RetryHints =
    {
        "Please specify device type", "Unknown USB bridge", "Unknown bridge",
        "Device of type", "-d sat", " specify '-d'",
    };

    /// <summary>
    /// 查询一块硬盘的 SMART 详情。devName 为块设备名(sda / nvme0n1),不含 /dev 前缀。
    /// 失败不抛异常,以 <see cref="SmartReport.Error"/> 返回原因。
    /// </summary>
    public static async Task<SmartReport> FetchAsync(NasDevice device, string devName, string model = "")
    {
        var name = (devName ?? "").Trim();
        if (!NameRegex.IsMatch(name))
            return new SmartReport { Error = $"设备名不合法:{devName}" };

        var path = $"/dev/{name}";

        try
        {
            // smartctl 常在 /usr/sbin 下;补全 PATH 免得非 root 账户找不到
            var cmd = $"PATH=$PATH:/usr/sbin:/sbin; smartctl -a {path}";
            var r = await SshService.RunAsync(device, cmd, timeoutMs: 25_000).ConfigureAwait(false);

            if (r.ErrorMessage is not null)
                return new SmartReport { DevicePath = path, Error = r.ErrorMessage };

            if (r.TimedOut)
                return new SmartReport
                {
                    DevicePath = path,
                    Error = "读取超时(smartctl 在等待硬盘响应,可能是休眠中的机械盘或被占用的阵列)。",
                };

            var combined = (r.Stdout + "\r\n" + r.Stderr).Trim();
            if (IsMissing(combined))
                return new SmartReport
                {
                    DevicePath = path,
                    Error = "该 NAS 上没有安装 smartmontools,无法读取 SMART 信息。\r\n" +
                            "可在 NAS 上执行 apt install smartmontools(Debian/Ubuntu)或 yum install smartmontools(CentOS)后重试。",
                };

            // USB 硬盘盒 / 扩展柜需要显式协议,先直连失败就换 -d sat 再试一次
            if (combined.Length == 0 || RetryHints.Any(h =>
                    combined.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                var retry = await SshService.RunAsync(device,
                    $"PATH=$PATH:/usr/sbin:/sbin; smartctl -a -d sat {path}", timeoutMs: 25_000)
                    .ConfigureAwait(false);
                var retryOut = (retry.Stdout + "\r\n" + retry.Stderr).Trim();
                if (retryOut.Length > combined.Length) combined = retryOut;
            }

            var raw = Clean(combined);
            if (raw.Length == 0)
                return new SmartReport
                {
                    DevicePath = path,
                    Error = $"smartctl 没有返回任何内容(可能是虚拟机虚拟磁盘、阵列卡屏蔽了 SMART)。\r\n原始输出:\r\n{combined}",
                };

            var lines = raw.Replace("\r", "").Split('\n');
            var attributes = ParseAttributes(lines);

            return new SmartReport
            {
                DevicePath = path,
                Info = ParseInfo(lines, model),
                Attributes = attributes,
                Warnings = ParseWarnings(lines),
                Temperature = ParseTemperature(lines, attributes),
                Raw = raw,
            };
        }
        catch (SshConnectException ex)
        {
            return new SmartReport
            {
                DevicePath = path,
                Error = $"{ex.FriendlyMessage}\r\n{SshService.HintFor(ex.Failure)}",
            };
        }
        catch (Exception ex)
        {
            return new SmartReport { DevicePath = path, Error = ex.Message };
        }
    }

    /// <summary>判断 smartctl 是否压根没装:命令未找到的各种报错。</summary>
    private static bool IsMissing(string output) =>
        output.Contains("smartctl: not found", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
        (output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) &&
         output.Contains("smartctl", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 去掉命令回显:root 会话(sudo -i)会把下发的整条命令回显到输出里,
    /// 其中含设备路径与参数,不清理会污染「基本信息」与属性表的解析。
    /// </summary>
    private static string Clean(string raw)
    {
        var kept = new List<string>();
        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            // 万一 shell 回显没被清干净(root 会话偶发):含命令本身的行直接丢掉
            if (t.Contains("smartctl -", StringComparison.Ordinal) || t.Contains(" M1=", StringComparison.Ordinal))
                continue;
            kept.Add(line.TrimEnd());
        }
        return string.Join("\r\n", kept).Trim();
    }

    /// <summary>基本信息字段:smartctl 的键 → 中文标签。</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Model Family"] = "型号系列",
        ["Model Number"] = "型号",
        ["Device Model"] = "型号",
        ["Serial Number"] = "序列号",
        ["Serial number"] = "序列号",
        ["Serial"] = "序列号",
        ["Firmware Version"] = "固件版本",
        ["Revision"] = "固件版本",
        ["User Capacity"] = "容量",
        ["Total NVM Capacity"] = "容量",
        ["Namespace 1 Size/Capacity"] = "容量",
        ["Sector Size"] = "扇区大小",
        ["Sector Sizes"] = "扇区大小",
        ["Rotation Rate"] = "转速",
        ["Form Factor"] = "形态",
        ["Local Time is"] = "设备本地时间",
        ["Local Time"] = "设备本地时间",
        ["SMART support is"] = "SMART 支持",
        ["SMART overall-health self-assessment test result"] = "健康自评",
        ["SMART Health Status"] = "健康自评",
        ["ATA Version is"] = "ATA 版本",
        ["SATA Version is"] = "SATA 版本",
        ["NVMe Version"] = "NVMe 版本",
        ["Number of Namespaces"] = "命名空间数",
        ["Critical Warning"] = "严重告警",
        ["Available Spare"] = "可用备用块",
        ["Available Spare Threshold"] = "备用块阈值",
        ["Percentage Used"] = "已用寿命",
        ["Power Cycles"] = "通电次数",
        ["Power On Hours"] = "通电时间",
        ["Data Units Read"] = "读取数据量",
        ["Data Units Written"] = "写入数据量",
        ["Media and Data Integrity Errors"] = "介质错误",
        ["Error Information Log Entries"] = "错误日志条数",
        ["TRIM Command"] = "TRIM",
    };

    /// <summary>
    /// 抽取基本信息行(「键: 值」)。只保留白名单内的键,避免把 SMART 长文的其他内容当字段。
    /// 目录名级别的型号(model 参数)在 smartctl 未给出型号时兜底。
    /// </summary>
    private static List<(string Key, string Value)> ParseInfo(string[] lines, string model)
    {
        var list = new List<(string Key, string Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("===", StringComparison.Ordinal)) continue;
            var colon = t.IndexOf(':');
            if (colon <= 0 || colon >= 60) continue;

            var key = t[..colon].Trim();
            var val = t[(colon + 1)..].Trim();
            if (val.Length == 0) continue;
            if (!Labels.TryGetValue(key, out var label)) continue;
            if (!seen.Add(label)) continue;

            list.Add((label, val));
        }

        if (!list.Any(i => i.Key == "型号") && model.Length > 0)
            list.Insert(0, ("型号", model));

        return list;
    }

    /// <summary>
    /// 解析 ATA 属性表:形如
    /// 「  5 Reallocated_Sector_Ct 0x0033 100 100 010 Pre-fail Always - 0」。
    /// 用 tokens 定位而非简单 Split,规避 WD/希捷不同固件对列宽的差异。
    /// </summary>
    private static List<SmartAttribute> ParseAttributes(string[] lines)
    {
        var list = new List<SmartAttribute>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("ID#", StringComparison.Ordinal)) continue;

            var tk = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // ID NAME FLAG VALUE WORST THRESH TYPE UPDATED WHEN_FAILED RAW…
            if (tk.Length < 10) continue;
            if (!int.TryParse(tk[0], out _)) continue;
            if (!tk[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(tk[3], out var value) ||
                !int.TryParse(tk[4], out _) ||
                !int.TryParse(tk[5], out var thresh)) continue;
            if (tk[6] is not ("Pre-fail" or "Old_age")) continue;

            var whenFailed = tk[8];
            list.Add(new SmartAttribute
            {
                Id = tk[0],
                Name = tk[1],
                Value = tk[3],
                Worst = tk[4],
                Thresh = tk[5],
                Type = tk[6],
                Updated = tk[7],
                WhenFailed = whenFailed,
                Raw = string.Join(' ', tk[9..]),
                IsFailed = whenFailed.Equals("FAILING_NOW", StringComparison.OrdinalIgnoreCase),
                IsWarning = thresh > 0 && value <= thresh,
            });
        }
        return list;
    }

    /// <summary>关键信息白名单之外的,却必须让用户看到的厂商嘱托 / 工具告警行。</summary>
    private static List<SmartWarning> ParseWarnings(string[] lines)
    {
        var list = new List<SmartWarning>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("===", StringComparison.Ordinal)) continue;

            bool critical =
                t.Contains("failure expected", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("SAVE ALL DATA", StringComparison.OrdinalIgnoreCase);

            var worth =
                critical ||
                t.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Warning! ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("ATA Error Count:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("SMART Status not supported", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("No Errors Logged", StringComparison.OrdinalIgnoreCase) is false &&
                t.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) &&
                t.Contains("occurred at disk power-on lifetime", StringComparison.OrdinalIgnoreCase);

            if (!worth) continue;
            if (!seen.Add(t)) continue;

            list.Add(new SmartWarning(
                t.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ? t["Warning:".Length..].Trim() : t,
                critical));
        }
        return list;
    }

    /// <summary>温度:优先属性和原始值(Temperature_Celsius),其次 NVMe 的 Temperature 字段。</summary>
    private static string ParseTemperature(string[] lines, List<SmartAttribute> attrs)
    {
        var attr = attrs.FirstOrDefault(a => a.Name.Contains("Temperature", StringComparison.OrdinalIgnoreCase));
        if (attr is not null)
        {
            var m = Regex.Match(attr.Raw, @"\d+");
            if (m.Success) return $"{m.Value} °C";
        }

        foreach (var line in lines)
        {
            var t = line.Trim();
            foreach (var key in new[] { "Temperature:", "Current Temperature:", "Temperature_Celsius" })
            {
                var i = t.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                var rest = t[(i + key.Length)..];
                var m = Regex.Match(rest, @"(-?\d+)\s*(C|Celsius)?");
                if (m.Success)
                {
                    var v = int.Parse(m.Groups[1].Value);
                    if (v is >= -20 and <= 130) return $"{v} °C";
                }
            }
        }
        return "";
    }
}
