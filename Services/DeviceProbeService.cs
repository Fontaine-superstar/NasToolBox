using Microsoft.UI.Dispatching;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// 设备在线检测:ICMP Ping 优先,不通时回落 TCP 端口探测(SSH → Web → SMB)。
/// 检测结果统一写入 <see cref="DeviceStatusStore"/> 持久化,下次启动可先展示上次状态。
/// 并发安全:新发起的批量检测会取消上一次尚未完成的检测。
/// </summary>
public static class DeviceProbeService
{
    private static CancellationTokenSource? _pending;

    /// <summary>取消上一次尚未完成的批量检测。</summary>
    public static void CancelPending() => _pending?.Cancel();

    /// <summary>开启新一轮检测(自动取消上一轮),返回本轮令牌。</summary>
    private static CancellationToken NewRound()
    {
        CancelPending();
        var cts = new CancellationTokenSource();
        _pending = cts;
        return cts.Token;
    }

    /// <summary>把上次持久化的状态套到行上;无记录时返回 false(保持「未检测」)。</summary>
    public static bool ApplyCached(DeviceRow row)
    {
        var s = DeviceStatusStore.Get(row.Device.Host);
        if (s is null) return false;

        if (s.Online) row.SetOnline(s.CachedText);
        else row.SetOffline(s.CachedText);
        return true;
    }

    /// <summary>
    /// 批量检测并把每行结果写入持久化存储。
    /// markChecking 为 true 时先把行置为「检测中…」(手动点「全部检测」用)。
    /// </summary>
    public static async Task ProbeAllAsync(IEnumerable<DeviceRow> rows, bool markChecking = false)
    {
        var list = rows?.ToList() ?? new List<DeviceRow>();
        if (list.Count == 0) return;

        // 检测在 ConfigureAwait(false) 的后台线程完成,而行的状态要靠 x:Bind OneWay 刷新:
        // 后台线程触发的 PropertyChanged 不会更新界面(会一直停留在「检测中…」),
        // 因此捕获调用方(UI 线程)的队列,把结果派发回去更新。
        var dq = DispatcherQueue.GetForCurrentThread();
        void ApplyOnUi(DeviceRow row, bool ok, string? detail)
        {
            if (dq is null)
            {
                if (ok) row.SetOnline(detail); else row.SetOffline(detail);
            }
            else
            {
                dq.TryEnqueue(() =>
                {
                    if (ok) row.SetOnline(detail); else row.SetOffline(detail);
                });
            }
        }

        var ct = NewRound();
        if (markChecking)
        {
            foreach (var row in list) row.SetChecking();
        }

        try
        {
            await Task.WhenAll(list.Select(async row =>
            {
                ct.ThrowIfCancellationRequested();
                var (ok, detail) = await ProbeAsync(row.Device, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                ApplyOnUi(row, ok, detail);

                DeviceStatusStore.Set(row.Device.Host, new DeviceStatus
                {
                    Online = ok,
                    Detail = detail ?? "",
                    CheckedAt = DateTime.Now,
                });
            })).ConfigureAwait(false);

            DeviceStatusStore.Save();
        }
        catch (OperationCanceledException)
        {
            // 被新一轮检测取代:本轮行可能停在「检测中…」没人接管,
            // 回填上次持久化的结果兜底;没有记录的回到「未检测」。
            void Recover(DeviceRow row)
            {
                if (row.StatusText != DeviceRow.CheckingText) return;
                if (!ApplyCached(row)) row.Reset();
            }
            if (dq is null) foreach (var row in list) Recover(row);
            else dq.TryEnqueue(() => { foreach (var row in list) Recover(row); });
        }
    }

    /// <summary>
    /// 软件启动时调用:直接按台账检测全部设备并落盘,不依赖任何页面。
    /// 结果供之后打开的设备页 / 首页显示。
    /// </summary>
    public static async Task ProbeAndPersistAsync()
    {
        var devices = NasDeviceStore.Load();
        if (devices.Count == 0) return;

        var ct = NewRound();
        try
        {
            await Task.WhenAll(devices.Select(async d =>
            {
                ct.ThrowIfCancellationRequested();
                var (ok, detail) = await ProbeAsync(d, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                DeviceStatusStore.Set(d.Host, new DeviceStatus
                {
                    Online = ok,
                    Detail = detail ?? "",
                    CheckedAt = DateTime.Now,
                });
            })).ConfigureAwait(false);

            DeviceStatusStore.Save();
        }
        catch (OperationCanceledException)
        {
            // 被新一轮检测取代
        }
    }

    /// <summary>
    /// 单台设备检测:先 ICMP Ping;不通时依次探测 SSH 端口 → Web 管理端口 → SMB 445,
    /// 避免「NAS 禁 Ping 却显示离线」。
    /// </summary>
    public static async Task<(bool Ok, string? Detail)> ProbeAsync(NasDevice d, CancellationToken ct = default)
    {
        var (ok, detail) = await NetworkService.PingOnceAsync(d.Host).ConfigureAwait(false);
        if (ok) return (true, detail);

        var targets = new List<(int Port, string Name)>
        {
            (d.SshPort is > 0 and <= 65535 ? d.SshPort : 22, "SSH"),
        };
        if (Uri.TryCreate(d.WebOrDefault, UriKind.Absolute, out var uri) && uri.Port > 0)
            targets.Add((uri.Port, "Web"));
        targets.Add((445, "SMB"));

        foreach (var (port, name) in targets.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var r = await NetworkService.TestPortAsync(d.Host, port, name, 1200).ConfigureAwait(false);
            if (r.Open) return (true, $"{name} {port} 可达 · {r.ElapsedMs} ms");
        }

        return (false, "Ping 与常用端口均无响应");
    }
}
