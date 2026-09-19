using System.Text.RegularExpressions;
using System.Text.Json;
using NasToolbox.Models;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Services;

/// <summary>NAS 状态快照:一次采集得到的全部指标,供启动预取与页面展示共用。</summary>
public sealed class NasStatusSnapshot
{
    /// <summary>来源设备的缓存键(与 <see cref="NasDevice.SshCacheKey"/> 一致),用于核对缓存归属。</summary>
    public string DeviceKey { get; init; } = "";

    /// <summary>采集时刻(本地时间);在线时长走秒以它为基准。</summary>
    public DateTime FetchedAt { get; init; } = DateTime.Now;

    /// <summary>在线探测结果(采集失败时也可能有值)。</summary>
    public bool Online { get; init; }
    public string OnlineDetail { get; init; } = "";

    public string OsSummary { get; init; } = "";
    public string Hostname { get; init; } = "";

    /// <summary>开机总秒数(采集时刻);0 表示未知。</summary>
    public long UptimeSec { get; init; }

    /// <summary>负载均值文本(1/5/15 分钟)。</summary>
    public string Load { get; init; } = "";

    /// <summary>CPU 核心数;0 表示未知。</summary>
    public int Cores { get; init; }

    /// <summary>温度(毫摄氏度);&lt;= 0 表示未读取到。</summary>
    public long TempMilli { get; init; }

    public long MemTotalKb { get; init; }
    public long MemAvailKb { get; init; }
    public List<MountUsage> Mounts { get; init; } = new();

    /// <summary>默认网关地址。</summary>
    public string Gateway { get; init; } = "—";

    /// <summary>DNS 服务器列表(逗号分隔)。</summary>
    public string Dns { get; init; } = "—";

    /// <summary>网卡列表(不含 lo)。</summary>
    public List<NicInfo> Nics { get; init; } = new();

    /// <summary>NAS 全部内网 IPv4(逗号分隔,跨网卡汇总);空表示未采集到。</summary>
    public string LanIps { get; init; } = "";

    /// <summary>NAS 出口公网 IPv4(由 NAS 侧查询);空表示未获取到(无外网 / 无 curl/wget)。</summary>
    public string PublicIp { get; init; } = "";

    /// <summary>GPU 名称(lspci 的 VGA/3D/Display 控制器,多卡逗号分隔);空表示无 GPU 或无法识别。</summary>
    public string Gpu { get; init; } = "";

    /// <summary>
    /// 内存条型号(dmidecode 的 Memory Device 汇总),如「DDR4 3200 MT/s 16 GB × 2」;
    /// 混插不同规格时逐条列出。空表示未采集到(非 root / 无 dmidecode / 虚拟机无 DMI)。
    /// </summary>
    public string MemoryModel { get; init; } = "";

    /// <summary>CPU 型号(/proc/cpuinfo 的 model name / Hardware,回退 uname -m);空表示未采集到。</summary>
    public string CpuModel { get; init; } = "";

    /// <summary>全部温度传感器读数(thermal zones + hwmon);空列表表示系统未暴露温度信息。</summary>
    public List<TempReading> Temps { get; init; } = new();

    /// <summary>物理硬盘列表(lsblk + smartctl);空列表表示未采集到(lsblk 缺失或无磁盘)。</summary>
    public List<DiskInfo> Disks { get; init; } = new();

    /// <summary>采集失败原因;为 null 表示成功。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// NAS 状态采集:在线探测 + SSH 指标(系统 / 在线时长 / 负载 / 内存 / 温度 / 磁盘)。
/// 软件启动时即可预取,页面点开先用缓存上屏,再后台静默刷新;指标随设备权限模式自动提权。
/// </summary>
public static class NasStatusService
{
    /// <summary>采集命令组(均为只读):uptime / 核心数 / 内存 / 磁盘 / 温度 / 开机秒数。</summary>
    private static readonly string[] Commands =
    {
        "uptime",
        "nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo",
        "grep -E 'MemTotal|MemAvailable|MemFree' /proc/meminfo",
        "df -hP -x tmpfs -x devtmpfs -x overlay 2>/dev/null || df -hP 2>/dev/null || df -h",
        "cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || echo -1",
        "cat /proc/uptime",
        // 网卡与网络信息:两次采样间隔 1 秒,用于计算实时速率
        """for n in /sys/class/net/*; do i=${n##*/}; echo "IF1|$i|$(cat $n/address 2>/dev/null)|$(cat $n/operstate 2>/dev/null)|$(cat $n/speed 2>/dev/null)|$(cat $n/statistics/rx_bytes 2>/dev/null)|$(cat $n/statistics/tx_bytes 2>/dev/null)"; done""",
        "sleep 1",
        """for n in /sys/class/net/*; do i=${n##*/}; echo "IF2|$i|$(cat $n/statistics/rx_bytes 2>/dev/null)|$(cat $n/statistics/tx_bytes 2>/dev/null)"; done""",
        "ip -o -4 addr show 2>/dev/null",
        "ip route show default 2>/dev/null | head -1",
        "grep nameserver /etc/resolv.conf 2>/dev/null",
        // 公网 IP:由 NAS 出口查询(需 NAS 能上外网);curl 优先,busybox wget 兜底,超时 3 秒防拖慢整体采集
        "curl -s --max-time 3 https://ip.3322.net 2>/dev/null || curl -s --max-time 3 https://api.ipify.org 2>/dev/null || wget -qO- -T 3 https://ip.3322.net 2>/dev/null || wget -qO- -T 3 https://api.ipify.org 2>/dev/null",
        // CPU 型号:x86 取 model name,ARM 取 Hardware,再回退 uname -m
        "grep -m1 '^model name' /proc/cpuinfo 2>/dev/null || grep -m1 '^Hardware' /proc/cpuinfo 2>/dev/null || uname -m 2>/dev/null",
        // 全部温度:thermal zone(TZ|zone|type|毫摄氏度) + hwmon 传感器(HW|芯片|label|毫摄氏度)
        """for z in /sys/class/thermal/thermal_zone*; do [ -e "$z/temp" ] && echo "TZ|${z##*/}|$(cat $z/type 2>/dev/null)|$(cat $z/temp 2>/dev/null)"; done; for s in /sys/class/hwmon/hwmon*; do n=$(cat $s/name 2>/dev/null); for f in $s/temp*_input; do [ -e "$f" ] && echo "HW|$n|$(cat "${f%_input}_label" 2>/dev/null)|$(cat $f 2>/dev/null)"; done; done""",
        // GPU:lspci 列出显卡类控制器;无 lspci(未装 pciutils)或无 GPU 时输出为空,UI 显示「—」
        "lspci 2>/dev/null | grep -Ei 'vga|3d controller|display controller' | head -4",
        // 内存型号:dmidecode 的 Memory Device(需 root,dmidecode 常在 /usr/sbin 下故补全 PATH);
        // 只取型号相关字段,跳过未插内存条的空槽;无 dmidecode 时输出为空,UI 显示「—」
        """PATH=$PATH:/usr/sbin:/sbin; dmidecode -t 17 2>/dev/null | grep -Ei '^[[:space:]]*(Size|Type|Speed|Configured Clock Speed|Locator|Manufacturer|Part Number):' | sed 's/^[[:space:]]*//'""",
        // 物理硬盘:lsblk JSON(仅 disk 级,含容量/是否机械/接口/型号) + smartctl 健康自评;
        // smartctl 未安装时该段为空,健康显示「未知」;smartctl 常在 /usr/sbin 下故补全 PATH
        """lsblk -Jdnb -o NAME,SIZE,ROTA,TRAN,MODEL 2>/dev/null; echo ---SMART---; for d in $(lsblk -dnb -o NAME,TYPE 2>/dev/null | awk '$2=="disk"{print $1}'); do s=""; if command -v smartctl >/dev/null 2>&1; then s=$(PATH=$PATH:/usr/sbin:/sbin smartctl -H /dev/$d 2>/dev/null | grep -iE 'overall-health|smart health' | head -1 | awk -F': ' '{print $2}'); fi; echo "$d|$s"; done""",
    };

    // 采集串行化:共享 SSH 连接 / root 会话,并发会互相踩
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static NasStatusSnapshot? _cached;

    /// <summary>最近一次成功采集的快照;页面先用它立即渲染。</summary>
    public static NasStatusSnapshot? Cached => _cached;

    /// <summary>设备对应的缓存键。</summary>
    public static string Key(NasDevice d) => d.SshCacheKey;

    /// <summary>软件启动时后台预取当前设备的状态,页面点开即有数据可显示。</summary>
    /// <summary>
    /// 启动预取:返回 Task 以便启动流程(StartupService)把它算作一个进度步骤并可设超时。
    /// 无当前设备时直接返回已完成。
    /// </summary>
    public static Task PrefetchOnStartupAsync()
    {
        var devices = NasDeviceStore.Load();
        var current = NasDeviceStore.GetCurrentOrDefault(devices);
        return current is null ? Task.CompletedTask : FetchAsync(current);
    }

    /// <summary>采集一台设备的状态;成功结果写入缓存。失败不抛异常,以 Error 字段返回。</summary>
    public static async Task<NasStatusSnapshot> FetchAsync(NasDevice device)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snap = await FetchCoreAsync(device).ConfigureAwait(false);
            if (snap.Error is null) _cached = snap;
            return snap;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<NasStatusSnapshot> FetchCoreAsync(NasDevice dev)
    {
        try
        {
            // 在线探测走网络,可与 SSH 并行;两条 SSH 调用必须串行(共享连接 / root 会话)
            var probeTask = DeviceProbeService.ProbeAsync(dev);

            var os = await SshService.DetectAsync(dev).ConfigureAwait(false);
            var metrics = await SshService.RunManyAsync(dev, Commands).ConfigureAwait(false);

            var (online, detail) = await probeTask.ConfigureAwait(false);

            var first = metrics.FirstOrDefault();
            if (first?.ErrorMessage is not null)
            {
                return new NasStatusSnapshot
                {
                    DeviceKey = Key(dev), Online = online, OnlineDetail = detail ?? "",
                    Error = first.ErrorMessage,
                };
            }

            var totalKb = MemKb(Out(metrics, 2), "MemTotal");
            var availKb = MemKb(Out(metrics, 2), "MemAvailable");
            if (availKb == 0) availKb = MemKb(Out(metrics, 2), "MemFree"); // 老内核回退

            var (gateway, dns, nics) = ParseNetwork(
                Out(metrics, 9), Out(metrics, 10), Out(metrics, 6), Out(metrics, 8), Out(metrics, 11));

            // 排除 docker 相关虚拟网卡(docker0 / veth* / br-* / virbr*)与无 IPv4 的网卡:
            // 前者只带 172.x 容器网段,后者无展示价值;「—」是旧数据/占位横线,同样视为无 IP
            nics.RemoveAll(n => IsDockerIf(n.Name)
                || string.IsNullOrWhiteSpace(n.Ipv4)
                || n.Ipv4.Trim() is "\u2014" or "-");

            // 当前内网 IP:汇总全部网卡的全部 IPv4(网卡已按「192. 优先」排序,单网卡多地址已逗号连接)
            var lanIps = string.Join(", ",
                nics.Where(n => !string.IsNullOrWhiteSpace(n.Ipv4)).Select(n => n.Ipv4));
            var publicIp = ExtractIpv4(Out(metrics, 12));
            var (cpuModel, temps) = ParseCpuAndTemps(Out(metrics, 13), Out(metrics, 14));
            var gpu = ParseGpu(Out(metrics, 15));
            var memoryModel = ParseMemoryModel(Out(metrics, 16));
            var disks = ParseDisks(Out(metrics, 17));

            return new NasStatusSnapshot
            {
                DeviceKey = Key(dev),
                FetchedAt = DateTime.Now,
                Online = online,
                OnlineDetail = detail ?? "",
                OsSummary = os.Kind == NasKind.Unknown && os.OsName.Length == 0 ? "—" : os.Summary,
                Hostname = os.Hostname.Length > 0 ? os.Hostname
                    : dev.Hostname.Length > 0 ? dev.Hostname : dev.Host,
                UptimeSec = ParseUptimeSec(Out(metrics, 5)),
                Load = ParseUptime(Out(metrics, 0)).Load,
                Cores = int.TryParse(Out(metrics, 1), out var cores) && cores > 0 ? cores : 0,
                TempMilli = int.TryParse(Out(metrics, 4), out var milli) && milli > 0 ? milli : 0,
                MemTotalKb = totalKb,
                MemAvailKb = availKb,
                Mounts = ParseDf(Out(metrics, 3)),
                Gateway = gateway,
                Dns = dns,
                Nics = nics,
                LanIps = lanIps,
                PublicIp = publicIp,
                Gpu = gpu,
                MemoryModel = memoryModel,
                CpuModel = cpuModel,
                Temps = temps,
                Disks = disks,
            };
        }
        catch (SshConnectException ex)
        {
            return new NasStatusSnapshot
            {
                DeviceKey = Key(dev),
                Error = $"{ex.FriendlyMessage}\r\n{SshService.HintFor(ex.Failure)}",
            };
        }
        catch (Exception ex)
        {
            return new NasStatusSnapshot { DeviceKey = Key(dev), Error = ex.Message };
        }
    }

    // ---------- 解析辅助 ----------

    private static string Out(List<SshCommandResult> results, int index) =>
        index >= 0 && index < results.Count ? (results[index].Stdout ?? "").Trim() : "";

    /// <summary>
    /// 解析 CPU 型号与温度:型号线取冒号后内容(无冒号的 uname -m 回退取整行);
    /// 温度为 TZ|zone|type|毫摄氏度 与 HW|芯片|label|毫摄氏度 两种行,同名同值去重。
    /// </summary>
    /// <summary>
    /// 解析 lspci 的显卡控制器行(形如「00:02.0 VGA compatible controller: Intel Corporation UHD 730 (rev 0c)」)
    /// 为 GPU 名称:取「controller:」之后的内容并去掉尾部修订号,多卡逗号连接。
    /// 无 GPU、无 lspci 或输出只有提示符残留时返回空串。
    /// </summary>
    private static string ParseGpu(string raw)
    {
        var names = new List<string>();
        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var idx = line.IndexOf("controller:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var name = line[(idx + "controller:".Length)..].Trim();
            // 去掉尾部「(rev 0c)」修订号
            var rev = name.IndexOf(" (rev ", StringComparison.OrdinalIgnoreCase);
            if (rev > 0) name = name[..rev].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return string.Join(", ", names);
    }

    /// <summary>一条内存条(DIMM)的型号信息;单位统一为 GB,速度为原始文本(如「3200 MT/s」)。</summary>
    private sealed record DimmSlot(double SizeGb, string Type, string Speed, string Locator,
                                   string Manufacturer, string PartNumber);

    /// <summary>
    /// 解析 dmidecode -t 17 的 Memory Device 字段(已过滤为 Size/Type/Speed/Configured Clock Speed/Locator/Manufacturer/Part Number):
    /// 以「Size:」作为每根内存条的起始;跳过空槽(No Module Installed)。
    /// 全部规格一致时合并为「DDR4 3200 MT/s 16 GB × 2」,混插时逐条列出。无有效数据返回空串。
    /// </summary>
    private static string ParseMemoryModel(string raw)
    {
        var slots = new List<DimmSlot>();
        double size = 0;
        string type = "", speed = "", locator = "", maker = "", part = "";

        void Flush()
        {
            if (size > 0) slots.Add(new DimmSlot(size, type, speed, locator, maker, part));
            size = 0; type = ""; speed = ""; locator = ""; maker = ""; part = "";
        }

        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            var colon = t.IndexOf(':');
            if (colon <= 0) continue;
            var key = t[..colon].Trim();
            var val = t[(colon + 1)..].Trim();
            if (val.Length == 0 || val.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("Other", StringComparison.OrdinalIgnoreCase)) continue;

            switch (key)
            {
                case "Size":
                    Flush(); // 每根内存条以 Size 开头
                    // 形如「16 GB」/「8192 MB」;空槽为「No Module Installed」
                    var m = Regex.Match(val, @"(\d+)\s*(GB|MB)", RegexOptions.IgnoreCase);
                    if (m.Success && double.TryParse(m.Groups[1].Value, out var n))
                        size = m.Groups[2].Value.Equals("MB", StringComparison.OrdinalIgnoreCase) ? n / 1024.0 : n;
                    break;
                case "Type":
                    if (type.Length == 0) type = val; // 「Type Detail」不匹配本键
                    break;
                case "Speed":
                    if (speed.Length == 0) speed = val; // 优先标称速度
                    break;
                case "Configured Clock Speed":
                    if (speed.Length == 0) speed = val; // 老主板只有实际运行速度
                    break;
                case "Locator":
                    if (locator.Length == 0) locator = val;
                    break;
                case "Manufacturer":
                    if (maker.Length == 0) maker = val;
                    break;
                case "Part Number":
                    if (part.Length == 0) part = val;
                    break;
            }
        }
        Flush();

        if (slots.Count == 0) return "";

        var groups = slots.GroupBy(s => (s.Type, s.Speed, s.SizeGb, s.Manufacturer, s.PartNumber)).ToList();
        if (groups.Count == 1)
        {
            var g = groups[0];
            var parts = new List<string>();
            if (g.Key.Type.Length > 0) parts.Add(g.Key.Type);
            if (g.Key.Speed.Length > 0) parts.Add(g.Key.Speed);
            parts.Add($"{g.Key.SizeGb:0.#} GB");
            if (g.Count() > 1) parts.Add($"× {g.Count()}");

            var line = string.Join(" ", parts);
            // 品牌与料号放在后半段:如「DDR4 3200 MT/s 16 GB × 2 · Kingston KHX3200C16D4/16GX」
            var tail = new List<string>();
            if (g.Key.Manufacturer.Length > 0) tail.Add(g.Key.Manufacturer);
            if (g.Key.PartNumber.Length > 0 &&
                !g.Key.PartNumber.Equals(g.Key.Manufacturer, StringComparison.OrdinalIgnoreCase)) tail.Add(g.Key.PartNumber);
            return tail.Count > 0 ? $"{line} · {string.Join(" ", tail)}" : line;
        }

        // 混插:逐条列出「DIMM_A1: 16 GB DDR4 3200 MT/s Kingston」
        return string.Join(" | ", slots.Select(s =>
        {
            var head = s.Locator.Length > 0 ? $"{s.Locator}: " : "";
            var brand = s.Manufacturer.Length > 0 ? " " + s.Manufacturer : "";
            return $"{head}{s.SizeGb:0.#} GB {s.Type} {s.Speed}{brand}".Trim();
        }));
    }

    /// <summary>
    /// 解析物理硬盘信息:---SMART--- 之前是 lsblk 的 JSON(blockdevices 数组),
    /// 之后是「sda|PASSED」形式的健康自评行。lsblk 缺失或 JSON 解析失败时返回空列表。
    /// 注意:root 会话会回显命令文本,而命令里就含 ---SMART---,故取「最后一次」出现的标记;
    /// JSON 也可能混入提示符等杂质,实际只取第一对花括号之间的内容。
    /// </summary>
    private static List<DiskInfo> ParseDisks(string raw)
    {
        const string marker = "---SMART---";
        var idx = raw.LastIndexOf(marker, StringComparison.Ordinal);
        var jsonPart = idx >= 0 ? raw[..idx] : raw;

        // 容忍提示符 / 回显残留:截取第一个 '{' 到最后一个 '}' 之间
        var start = jsonPart.IndexOf('{');
        var end = jsonPart.LastIndexOf('}');
        if (start < 0 || end <= start) return new List<DiskInfo>();
        jsonPart = jsonPart[start..(end + 1)];

        // 健康自评:设备名 → PASSED / OK / FAILED!…
        var smartPart = idx >= 0 ? raw[(idx + marker.Length)..] : "";
        var health = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in smartPart.Replace("\r", "").Split('\n'))
        {
            var f = line.Trim().Split('|');
            if (f.Length == 2 && f[0].Length > 0) health[f[0]] = f[1].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            if (!doc.RootElement.TryGetProperty("blockdevices", out var arr)) return new List<DiskInfo>();

            var list = new List<DiskInfo>();
            foreach (var d in arr.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                // lsblk -J 的 rota:util-linux ≥2.32 输出布尔 true/false,老版本是 0/1,两种都要认
                bool isSsd = false;
                if (d.TryGetProperty("rota", out var rt))
                {
                    if (rt.ValueKind == JsonValueKind.True || rt.ValueKind == JsonValueKind.False)
                        isSsd = !rt.GetBoolean();
                    else if (rt.TryGetInt32(out var r)) isSsd = r == 0;
                }
                list.Add(new DiskInfo
                {
                    Name = name,
                    SizeBytes = d.TryGetProperty("size", out var sz) &&
                                (sz.ValueKind == JsonValueKind.Number) && sz.TryGetInt64(out var b) ? b : 0,
                    IsSsd = isSsd,
                    Tran = d.TryGetProperty("tran", out var tr) && tr.ValueKind == JsonValueKind.String
                        ? tr.GetString() ?? "" : "",
                    Model = d.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String
                        ? mo.GetString() ?? "" : "",
                    Health = health.TryGetValue(name, out var h) ? h : "",
                });
            }
            return list;
        }
        catch
        {
            // lsblk 输出异常:整体放弃,UI 显示提示
            return new List<DiskInfo>();
        }
    }

    private static (string CpuModel, List<TempReading> Temps) ParseCpuAndTemps(string modelRaw, string tempsRaw)
    {
        var line = modelRaw.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        var colon = line.IndexOf(':');
        var model = (colon >= 0 ? line[(colon + 1)..].Trim() : line);

        var temps = new List<TempReading>();
        foreach (var raw in tempsRaw.Replace("\r", "").Split('\n'))
        {
            var f = raw.Trim().Split('|');
            if (f.Length < 4 || (f[0] != "TZ" && f[0] != "HW")) continue;
            if (!int.TryParse(f[3], out var milli) || milli <= 0 || milli > 200_000) continue;
            var name = f[0] == "TZ" ? $"thermal · {f[2]}"
                     : (f[2].Length > 0 ? f[2] : f[1]);
            var reading = new TempReading { Name = name, Celsius = (int)Math.Round(milli / 1000.0) };
            if (temps.Any(t => t.Name == reading.Name && t.Celsius == reading.Celsius)) continue;
            temps.Add(reading);
        }

        return (model, temps);
    }

    /// <summary>docker 相关虚拟网卡:docker0、veth*(每容器一根)、br-*(compose 自定义网络)、virbr*(虚拟桥)。</summary>
    private static bool IsDockerIf(string name) =>
        name == "docker0" || name.StartsWith("veth", StringComparison.Ordinal) ||
        name.StartsWith("br-", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal);

    /// <summary>从公网 IP 查询输出中提取首个合法 IPv4;无则返回空串(容忍服务横幅、提示符残留等杂质)。</summary>
    private static string ExtractIpv4(string raw)
    {
        foreach (Match m in Regex.Matches(raw, @"(?:\d{1,3}\.){3}\d{1,3}"))
        {
            var parts = m.Value.Split('.');
            if (parts.All(p => int.TryParse(p, out var o) && o is >= 0 and <= 255)) return m.Value;
        }
        return "";
    }

    /// <summary>从 /proc/uptime 第一字段读取开机总秒数;优先取带小数点的独立数字,避免提示符残留里主机名的数字(如 nas2)被误读。</summary>
    private static long ParseUptimeSec(string raw)
    {
        var m = Regex.Match(raw, @"(?:^|\s)(\d+\.\d+)");
        if (!m.Success) m = Regex.Match(raw, @"(?:^|\s)(\d+)(?=\s|$)");
        return m.Success &&
               double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? (long)v
            : 0;
    }

    /// <summary>从 uptime 输出取「在线时长」与「负载均值」;兼容完整版与 busybox。</summary>
    private static (string Up, string Load) ParseUptime(string raw)
    {
        var s = raw.Replace("\r", "").Trim();
        if (s.Length == 0) return ("", "");

        var load = "";
        var loadIdx = s.IndexOf("load average", StringComparison.OrdinalIgnoreCase);
        if (loadIdx >= 0)
        {
            var colon = s.IndexOf(':', loadIdx);
            if (colon >= 0) load = s[(colon + 1)..].Trim();
        }

        var up = "";
        var upIdx = s.IndexOf(" up ", StringComparison.Ordinal);
        if (upIdx >= 0)
        {
            var rest = s[(upIdx + 4)..];
            // 完整版 uptime 到「, N users」为止;busybox 没有 users 段,取第一段
            var m = Regex.Match(rest, @",\s*\d+\s+user");
            up = m.Success ? rest[..m.Index].Trim() : rest.Split(',')[0].Trim();
        }

        return (up, load);
    }

    /// <summary>从 /proc/meminfo 输出中读取指定键的 kB 值;行内任意位置匹配,容忍提示符残留等前缀垃圾。</summary>
    private static long MemKb(string text, string key)
    {
        var m = Regex.Match(text, key + @"\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>
    /// 解析网络信息:ip -o -4 addr(IPv4)、默认路由(网关)、IF1/IF2 两次采样(网卡参数与实时速率)、
    /// resolv.conf(DNS)。跳过 lo 回环;IPv4 为 192 开头的网卡优先展示。
    /// </summary>
    private static (string Gateway, string Dns, List<NicInfo> Nics) ParseNetwork(
        string ipAddrRaw, string routeRaw, string if1Raw, string if2Raw, string dnsRaw)
    {
        // IPv4:形如「2: eth0    inet 192.168.1.10/24 …」
        var ips = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in ipAddrRaw.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^\d+:\s+(\S+)\s+inet\s+([0-9.]+)/");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            if (name == "lo") continue;
            if (!ips.TryGetValue(name, out var l)) ips[name] = l = new List<string>();
            l.Add(m.Groups[2].Value);
        }

        // 第一次采样:IF1|名称|MAC|状态|速度|rx|tx
        var nics = new List<NicInfo>();
        foreach (var line in if1Raw.Replace("\r", "").Split('\n'))
        {
            var f = line.Trim().Split('|');
            if (f.Length < 7 || f[0] != "IF1") continue;
            var name = f[1];
            if (name == "lo") continue;

            var nic = new NicInfo
            {
                Name = name,
                Mac = f[2],
                State = f[3],
                SpeedMbps = int.TryParse(f[4], out var sp) ? sp : 0,
                RxBytes = long.TryParse(f[5], out var rx) ? rx : 0,
                TxBytes = long.TryParse(f[6], out var tx) ? tx : 0,
            };
            if (ips.TryGetValue(name, out var l)) nic.Ipv4 = string.Join(", ", l);
            nics.Add(nic);
        }

        // 第二次采样(间隔约 1 秒)→ 实时速率
        foreach (var line in if2Raw.Replace("\r", "").Split('\n'))
        {
            var f = line.Trim().Split('|');
            if (f.Length < 4 || f[0] != "IF2") continue;
            var nic = nics.FirstOrDefault(n => n.Name == f[1]);
            if (nic is null) continue;
            if (long.TryParse(f[2], out var rx) && long.TryParse(f[3], out var tx))
            {
                nic.RateDown = Math.Max(0, rx - nic.RxBytes);
                nic.RateUp = Math.Max(0, tx - nic.TxBytes);
            }
        }

        // 网关:default via 192.168.1.1 dev eth0 …
        var gateway = Regex.Match(routeRaw, @"via\s+(\S+)").Groups[1].Value;

        // DNS:nameserver 223.5.5.5
        var dnsList = new List<string>();
        foreach (var line in dnsRaw.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 2 && t[0] == "nameserver") dnsList.Add(t[1]);
        }

        // 展示顺序:IPv4 为 192 开头(家用网段 192.168.x.x)的网卡排最前,其余保持采集顺序(稳定排序)
        nics = nics.OrderBy(n => n.Ipv4.StartsWith("192.", StringComparison.Ordinal) ? 0 : 1).ToList();

        return (gateway.Length > 0 ? gateway : "—",
            dnsList.Count > 0 ? string.Join(", ", dnsList) : "—",
            nics);
    }

    /// <summary>群晖风格挂载点形状:vol1 / volume1(可带前导斜杠,忽略大小写)。</summary>
    private static readonly Regex VolNameRegex =
        new(@"^/?(?:vol(?:ume)?)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>把 vol1 / volume1 这类挂载名换成「存储空间N」;不符合该命名的保持原样。</summary>
    private static string FriendlyMount(string mount)
    {
        var m = VolNameRegex.Match(mount.Trim());
        return m.Success ? $"存储空间{m.Groups[1].Value}" : mount;
    }

    /// <summary>「存储空间N」显示名形状,用于排序分组。</summary>
    private static readonly Regex StorageNameRegex = new(@"^存储空间(\d+)$", RegexOptions.Compiled);

    /// <summary>排序键:存储空间N → 编号 N;其余 → int.MaxValue(排在其后,稳定排序保持原顺序)。</summary>
    private static int StorageOrder(MountUsage m)
    {
        var r = StorageNameRegex.Match(m.Mount);
        return r.Success && int.TryParse(r.Groups[1].Value, out var n) ? n : int.MaxValue;
    }

    /// <summary>解析 df -hP 输出;跳过 tmpfs / 系统伪文件系统与回环设备。</summary>
    private static List<MountUsage> ParseDf(string raw)
    {
        var list = new List<MountUsage>();
        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;
            if (!parts[4].EndsWith('%')) continue;
            if (!int.TryParse(parts[4].TrimEnd('%'), out var pct) || pct is < 0 or > 100) continue;

            var fs = parts[0];
            var mount = string.Join(" ", parts[5..]);
            if (fs is "tmpfs" or "devtmpfs" or "overlay" or "none" ||
                fs.Contains("loop", StringComparison.OrdinalIgnoreCase)) continue;
            if (mount.StartsWith("/proc", StringComparison.Ordinal) ||
                mount.StartsWith("/sys", StringComparison.Ordinal) ||
                mount.StartsWith("/dev", StringComparison.Ordinal) ||
                mount.StartsWith("/run", StringComparison.Ordinal)) continue;

            list.Add(new MountUsage
            {
                Filesystem = fs,
                Mount = FriendlyMount(mount),
                UsedText = $"已用 {parts[2]} / 共 {parts[1]}",
                UsePercent = pct,
            });
        }
        // 「存储空间N」排最前并按编号从小到大;其余保持 df 原始输出顺序(OrderBy 为稳定排序)
        return list.OrderBy(StorageOrder).ToList();
    }
}