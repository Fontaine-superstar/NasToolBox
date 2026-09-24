using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NasToolbox.Models;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Services;

/// <summary>部署进度:阶段文案 + 整体 0–1。</summary>
public sealed record SpeedtestProgress(string Stage, double Percent);

/// <summary>
/// 基于 Docker 的 speedtest-x 局域网测速:扫描 NAS 上已部署的 speedtest-x 容器,
/// 解析其端口映射后直接调用容器内 speedtest-x 的后端接口在应用内测速;
/// 未部署时由页面引导跳转 Docker 管理。命令执行随设备权限模式自动提权。
/// </summary>
public static class SpeedtestService
{
    public const string ContainerName = "speedtest-x";

    /// <summary>测速页地址(按容器实际映射的宿主端口)。</summary>
    public static string UrlOf(NasDevice d, int port) => $"http://{d.Host}:{port}/";

    /// <summary>
    /// 在 NAS 上扫描 speedtest-x 容器:仅按容器名精确匹配(忽略大小写;{{.Names}} 可能是「主名,别名」,
    /// 只取第一段)。镜像名不参与判断 —— 否则其他容器恰好用了同名镜像、或镜像名含该子串时,
    /// 会被误认为已部署,导致容器不存在时该弹的引导提示不弹。SSH / docker 异常直接抛出。
    /// </summary>
    public static async Task<DockerContainer?> FindAsync(NasDevice device, CancellationToken ct = default)
    {
        var list = await DockerService.ListAsync(device, ct).ConfigureAwait(false);
        return list.FirstOrDefault(IsSpeedtest);
    }

    /// <summary>判断是否 speedtest-x 容器:按容器名精确匹配(忽略大小写;{{.Names}} 可能是「主名,别名」,只取第一段)。镜像名不参与判断。</summary>
    public static bool IsSpeedtest(DockerContainer c) =>
        c.Name.Split(',')[0].Trim().Equals(ContainerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>启动已部署但未运行的容器。</summary>
    public static async Task StartAsync(NasDevice device, string containerName, CancellationToken ct = default)
    {
        var r = await SshService.RunAsync(device, $"docker start {SshService.ShellQuote(containerName)}", 60000, ct)
            .ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException(DockerService.Explain(r.Output));
    }

    /// <summary>
    /// 从 docker ps 的 Ports 字段解析宿主端口。形如「0.0.0.0:8800->80/tcp, :::8800->80/tcp」,
    /// 优先取映射到容器 80 的宿主端口(多端口容器避免取错);没有再退回第一条 TCP 映射。
    /// </summary>
    public static int ParseHostPort(string? ports)
    {
        var s = ports ?? "";
        var m = Regex.Match(s, @"(\d+)->80/tcp");
        if (!m.Success) m = Regex.Match(s, @"(\d+)->(\d+)/tcp");
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : 0;
    }

    /// <summary>
    /// 解析容器映射的宿主端口:优先用 docker ps 扫描到的 Ports 字段;
    /// 拿不到时(容器刚启动,扫描那一刻还没有端口信息)用 docker port 补查一次。
    /// </summary>
    public static async Task<int> ResolveHostPortAsync(
        NasDevice device, DockerContainer container, CancellationToken ct = default)
    {
        var port = ParseHostPort(container.Ports);
        if (port > 0) return port;

        var r = await SshService.RunAsync(device,
            $"docker port {SshService.ShellQuote(container.Name)} 80", 15000, ct).ConfigureAwait(false);
        if (!r.Success) return 0;

        // 输出形如「80/tcp -> 0.0.0.0:8800」「80/tcp -> [::]:8800」
        foreach (var line in (r.Stdout ?? "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @":(\d+)\s*$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0) return p;
        }
        return 0;
    }

    /// <summary>应用内测速结果:延迟 / 下行 / 上行(Mbps)与本机出口信息。</summary>
    public sealed record SpeedtestResult(double PingMs, double DownloadMbps, double UploadMbps, string IpInfo);

    /// <summary>最近一次测速(应用运行期间保留):结果 + 设备地址 + 端口 + 时间,页面打开时默认展示。</summary>
    public static (SpeedtestResult Result, string Host, int Port, DateTime At)? LastTest { get; private set; }

    /// <summary>记录一次成功测速,供页面下次进入时默认显示。</summary>
    public static void Remember(NasDevice device, int port, SpeedtestResult result) =>
        LastTest = (result, device.Host, port, DateTime.Now);

    /// <summary>
    /// 应用内测速:按 speedtest-x(LibreSpeed 协议)的后端接口实现 ——
    /// empty.php 测往返延迟与上传,garbage.php 测下载,getIP.php 取本机信息。
    /// 下载 / 上传各按「时间预算 + 数据量上限」截断,按累计字节 × 8 ÷ 秒折算 Mbps。
    /// </summary>
    public static async Task<SpeedtestResult> TestAsync(
        NasDevice device, int port, IProgress<SpeedtestProgress>? progress = null, CancellationToken ct = default)
    {
        var baseUri = UrlOf(device, port);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 时长由各阶段自行控制

        // 容器刚启动时 Web 服务可能还在初始化:拿到任何 HTTP 响应(含 404)即视为服务已起
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var probe = await http.GetAsync(baseUri, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception) when (i < 19) { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        // 协议自动识别:speedtest-x / LibreSpeed(garbage.php)优先,其次 OpenSpeedTest(静态 downloading)
        var proto = await DetectProtocolAsync(http, baseUri, ct).ConfigureAwait(false);

        // 本机出口信息(仅 LibreSpeed 系提供 getIP.php;失败不影响测速)
        var ipInfo = "";
        if (proto.IpUrl is not null)
        {
            try { ipInfo = await GetIpInfoAsync(http, proto.IpUrl, ct).ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }

        // 延迟:小响应往返;丢弃首次预热,取其后最小值
        progress?.Report(new SpeedtestProgress($"测量延迟({proto.Name})", 0.03));
        var best = double.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var resp = await http.GetAsync(proto.PingUrl + Rand(), ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                _ = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            sw.Stop();
            if (i > 0 && sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
            progress?.Report(new SpeedtestProgress($"测量延迟({proto.Name})", 0.03 + 0.04 * i / 9.0));
        }
        if (best == double.MaxValue) best = 0;

        var down = await MeasureDownloadAsync(http, proto.DownloadUrl, progress, ct).ConfigureAwait(false);
        var up = await MeasureUploadAsync(http, proto.UploadUrl, progress, ct).ConfigureAwait(false);

        progress?.Report(new SpeedtestProgress("测速完成", 1));
        return new SpeedtestResult(best, down, up, ipInfo);
    }

    /// <summary>识别出的一组测速端点(URL 均以查询参数前缀结尾,调用方拼接随机数防缓存)。</summary>
    private sealed record Proto(string Name, string PingUrl, string DownloadUrl, string UploadUrl, string? IpUrl);

    /// <summary>
    /// 探测容器使用的测速协议:
    /// · speedtest-x / LibreSpeed:garbage.php(动态随机流)+ empty.php + getIP.php
    ///   —— PHP 后端可能在根目录,也可能在 backend/ 子目录(speedtest-x 官方 Docker 即后者)
    /// · OpenSpeedTest:静态大文件 downloading + 空 upload(GET 计延迟、POST 收上传)
    /// 两者都不匹配时抛出带地址的明确错误。
    /// </summary>
    private static async Task<Proto> DetectProtocolAsync(HttpClient http, string baseUri, CancellationToken ct)
    {
        // 1) speedtest-x / LibreSpeed:依次尝试根目录与 backend/ 子目录
        foreach (var dir in new[] { "", "backend/" })
        {
            using var r = await http.GetAsync(baseUri + dir + "garbage.php?ckSize=1&cors=true&r=" + Rand(), ct)
                .ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
                return new Proto("speedtest-x",
                    baseUri + dir + "empty.php?cors=true&r=",
                    baseUri + dir + "garbage.php?ckSize=100&cors=true&r=",
                    baseUri + dir + "empty.php?cors=true&r=",
                    baseUri + dir + "getIP.php?cors=true&r=");
        }

        // 2) OpenSpeedTest:用 Range 只取 1KB 探测,避免拉整个 30MB 文件
        using (var req = new HttpRequestMessage(HttpMethod.Get, baseUri + "downloading?n=" + Rand()))
        {
            req.Headers.Range = new RangeHeaderValue(0, 1023);
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
                return new Proto("OpenSpeedTest",
                    baseUri + "upload?n=",
                    baseUri + "downloading?n=",
                    baseUri + "upload?n=",
                    null);
        }

        throw new InvalidOperationException(
            $"{baseUri} 上未识别出受支持的测速协议:既没有 speedtest-x/LibreSpeed 的 garbage.php(根目录与 backend/ 均无)," +
            "也没有 OpenSpeedTest 的 downloading。请确认浏览器打开该地址能正常测速,且部署的是这两种之一。");
    }

    /// <summary>下载测速:循环拉取随机流;时间预算 15 秒 / 数据上限 1GB,前 1.5 秒预热期不计入速率(排除 TCP 慢启动)。</summary>
    private static async Task<double> MeasureDownloadAsync(
        HttpClient http, string urlBase, IProgress<SpeedtestProgress>? progress, CancellationToken ct)
    {
        const int budgetMs = 15000;
        const int warmupMs = 1500;
        const long maxBytes = 1L << 30;
        long total = 0;
        long bytesAtWarm = -1;
        double msAtWarm = 0;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < budgetMs && total < maxBytes && !ct.IsCancellationRequested)
        {
            using var resp = await http.GetAsync(urlBase + Rand(),
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var buf = new byte[1 << 16];
            int n;
            while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (bytesAtWarm < 0 && sw.Elapsed.TotalMilliseconds >= warmupMs)
                {
                    bytesAtWarm = total;
                    msAtWarm = sw.Elapsed.TotalMilliseconds;
                }
                if (sw.ElapsedMilliseconds >= budgetMs || total >= maxBytes) break;
            }

            progress?.Report(new SpeedtestProgress("下载测速",
                0.07 + 0.38 * Math.Min(1, sw.Elapsed.TotalMilliseconds / budgetMs)));
        }

        // 预热期之后的字节 ÷ 预热期之后的时长;没撑过预热期则退回全程计算
        var rateMs = sw.Elapsed.TotalMilliseconds - msAtWarm;
        var rateBytes = bytesAtWarm >= 0 ? total - bytesAtWarm : total;
        var sec = rateMs > 200 && rateBytes > 0 ? rateMs / 1000.0 : sw.Elapsed.TotalSeconds;
        var bytes = rateMs > 200 && rateBytes > 0 ? rateBytes : total;
        return sec > 0 ? bytes * 8 / sec / 1_000_000.0 : 0;
    }

    /// <summary>上传测速:反复 POST 随机数据;时间预算 15 秒 / 数据上限 512MB(每次 8MB),前 1.5 秒预热期不计入速率。</summary>
    private static async Task<double> MeasureUploadAsync(
        HttpClient http, string urlBase, IProgress<SpeedtestProgress>? progress, CancellationToken ct)
    {
        const int budgetMs = 15000;
        const int warmupMs = 1500;
        const int chunk = 8 << 20; // 每次上传 8MB
        const long maxBytes = 512L << 20;
        long total = 0;
        long bytesAtWarm = -1;
        double msAtWarm = 0;
        var sw = Stopwatch.StartNew();

        var payload = new byte[chunk];
        Random.Shared.NextBytes(payload); // 随机内容,避免链路上的透明压缩虚高

        while (sw.ElapsedMilliseconds < budgetMs && total < maxBytes && !ct.IsCancellationRequested)
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await http.PostAsync(urlBase + Rand(), content, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            total += chunk;

            if (bytesAtWarm < 0 && sw.Elapsed.TotalMilliseconds >= warmupMs)
            {
                bytesAtWarm = total;
                msAtWarm = sw.Elapsed.TotalMilliseconds;
            }

            progress?.Report(new SpeedtestProgress("上传测速",
                0.45 + 0.52 * Math.Min(1, sw.Elapsed.TotalMilliseconds / budgetMs)));
        }

        // 预热期之后的字节 ÷ 预热期之后的时长;没撑过预热期则退回全程计算
        var rateMs = sw.Elapsed.TotalMilliseconds - msAtWarm;
        var rateBytes = bytesAtWarm >= 0 ? total - bytesAtWarm : total;
        var sec = rateMs > 200 && rateBytes > 0 ? rateMs / 1000.0 : sw.Elapsed.TotalSeconds;
        var bytes = rateMs > 200 && rateBytes > 0 ? rateBytes : total;
        return sec > 0 ? bytes * 8 / sec / 1_000_000.0 : 0;
    }

    private static async Task<string> GetIpInfoAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url + Rand(), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("processedString", out var ps)
                ? ps.GetString() ?? text
                : text;
        }
        catch (JsonException)
        {
            return text; // 非 JSON 时原样展示
        }
    }

    private static string Rand() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>在本机默认浏览器打开测速页。</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}