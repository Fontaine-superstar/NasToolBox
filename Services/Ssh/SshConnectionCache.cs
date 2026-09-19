using System.Collections.Concurrent;
using Renci.SshNet;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// SSH 连接复用缓存:同一设备(用户@主机:端口 + 认证方式)复用一条已握手连接,
/// 避免每条命令都重新做 SSH 握手。同一设备的命令串行执行(SSH.NET 客户端非线程安全)。
/// </summary>
internal sealed class SshConnectionCache
{
    /// <summary>租借句柄:Dispose 表示归还,Abandon 表示连接已不可用需强制销毁。</summary>
    public sealed class Lease : IDisposable
    {
        private readonly Slot _slot;
        private readonly Entry _entry;

        internal Lease(Slot slot, Entry entry)
        {
            _slot = slot;
            _entry = entry;
        }

        public SshClient Client => _entry.Client;

        /// <summary>该连接上的 root 会话(仅勾选「以 root 方式连接」的设备才有)。</summary>
        public SshRootShell? RootShell => _entry.Root;

        /// <summary>把新建的 root 会话挂到当前连接上,随连接一起回收。</summary>
        public void AttachRootShell(SshRootShell shell) => _entry.Root = shell;

        /// <summary>连接已损坏(超时 / 协议错误):销毁它,下次访问重新握手。</summary>
        public void Abandon() => _entry.Broken = true;

        public void Dispose()
        {
            _slot.LastUsed = DateTimeOffset.UtcNow;

            if (_entry.Broken || !_entry.Client.IsConnected)
            {
                _slot.Current = null;
                SafeDispose(_entry);
            }
            else if (!ReferenceEquals(_slot.Current, _entry))
            {
                SafeDispose(_entry);
            }

            _slot.Gate.Release();
        }
    }

    internal sealed class Entry
    {
        public required SshClient Client { get; init; }
        public SshRootShell? Root { get; set; }
        public bool Broken { get; set; }
    }

    internal sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Entry? Current;
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, Slot> Slots = new();

    /// <summary>空闲超过该时长的连接会被后台回收。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private static readonly Timer Reaper = new(
        _ => ReapIdle(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

    /// <summary>
    /// 租借一条可用连接:优先复用缓存中的活连接,否则用 create 新建。
    /// 等待同一设备上正在执行的命令时也会计入 waitTimeoutMs。
    /// </summary>
    public static async Task<Lease> RentAsync(
        string key,
        Func<SshClientFactory.CreateOutcome> create,
        int waitTimeoutMs = 15000,
        CancellationToken ct = default)
    {
        var slot = Slots.GetOrAdd(key, _ => new Slot());

        if (!await slot.Gate.WaitAsync(waitTimeoutMs, ct).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"等待设备连接空闲超时({waitTimeoutMs / 1000} 秒),该设备上可能有命令正在执行。");
        }

        try
        {
            var entry = slot.Current;
            if (entry is null || entry.Broken || !entry.Client.IsConnected)
            {
                if (entry is not null) SafeDispose(entry);
                slot.Current = null;

                var outcome = create();
                if (outcome.Client is null)
                {
                    throw new SshConnectException(outcome.Failure, outcome.Message, outcome.ExceptionText);
                }

                entry = new Entry { Client = outcome.Client };
                slot.Current = entry;
            }

            return new Lease(slot, entry);
        }
        catch
        {
            slot.Gate.Release();
            throw;
        }
    }

    /// <summary>丢弃指定设备的连接(凭据变更、设备删除、指纹重置时调用)。</summary>
    public static void Forget(string key)
    {
        if (!Slots.TryRemove(key, out var slot)) return;
        if (!slot.Gate.Wait(TimeSpan.FromSeconds(5))) return;
        try
        {
            if (slot.Current is { } entry) SafeDispose(entry);
            slot.Current = null;
        }
        finally { slot.Gate.Release(); }
    }

    /// <summary>断开并清空所有缓存连接(退出应用或全局重置时调用)。</summary>
    public static void ForgetAll()
    {
        foreach (var key in Slots.Keys.ToList()) Forget(key);
    }

    private static void ReapIdle()
    {
        foreach (var kv in Slots)
        {
            var slot = kv.Value;
            if (DateTimeOffset.UtcNow - slot.LastUsed < IdleTimeout) continue;
            if (!slot.Gate.Wait(0)) continue; // 正在使用,跳过

            try
            {
                if (slot.Current is { } entry) SafeDispose(entry);
                slot.Current = null;
                Slots.TryRemove(kv.Key, out _);
            }
            finally { slot.Gate.Release(); }
        }
    }

    private static void SafeDispose(Entry entry)
    {
        if (entry.Root is { } root)
        {
            try { root.Dispose(); }
            catch { /* 关闭失败无需处理 */ }
            entry.Root = null;
        }

        try { entry.Client.Dispose(); }
        catch { /* 断开失败无需处理 */ }
    }
}
