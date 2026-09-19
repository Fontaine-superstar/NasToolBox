using System.Text;
using Microsoft.UI.Dispatching;
using NasToolbox.Models;
using Renci.SshNet;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// SSH 交互终端会话。独立建连(不走 SshConnectionCache:终端是长时会话,空闲回收会误断),
/// 经 ShellStream(带 PTY 的真实 shell)与 WebView2 内的 xterm.js 双向转发。
/// SSH.NET 没有 window-change API,窗口尺寸变化时在同一连接上重建 ShellStream;
/// 全屏程序(备用屏,如 vim/top)运行期间由调用方跳过重建,以免杀死会话。
/// </summary>
public sealed class SshTerminalSession : IDisposable
{
    private readonly DispatcherQueue _dq;
    private SshClient? _client;
    private volatile ShellStream? _stream;
    private CancellationTokenSource? _cts;
    private int _generation;                    // Resize 递增:旧读循环凭此安静退出
    private readonly object _writeLock = new();

    /// <summary>收到远端输出(已在 UI 线程触发)。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>会话异常断开(已在 UI 线程触发;主动 Close 不触发)。</summary>
    public event Action<string>? Closed;

    // SSH.NET 2026 的 ShellStream 继承自 Stream,没有 IsOpen;通道关闭时 CanRead 变 false
    public bool IsOpen => _client?.IsConnected == true && _stream?.CanRead == true;

    public SshTerminalSession(DispatcherQueue dq) => _dq = dq;

    /// <summary>建连并打开 PTY;与命令执行通道同策略:新主机指纹直接信任。</summary>
    public async Task<(bool Ok, string Message)> OpenAsync(NasDevice device, int cols, int rows)
    {
        var outcome = await Task.Run(() => SshClientFactory.Create(device,
            new SshClientFactory.CreateOptions { TrustNewHostKey = true })).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            var hint = SshService.HintFor(outcome.Failure);
            return (false, hint is { Length: > 0 } ? $"{outcome.Message}({hint})" : outcome.Message);
        }

        _client = outcome.Client!;               // Ok 分支里 Client 必非空
        _cts = new CancellationTokenSource();
        var stream = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 16384);
        _stream = stream;
        StartReadLoop(stream, _generation);
        return (true, "");
    }

    /// <summary>把 xterm 的键击写入 PTY(UTF-8)。</summary>
    public void Write(string data)
    {
        var stream = _stream;
        if (stream is null || !stream.CanRead) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            lock (_writeLock)
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch
        {
            // 连接正在关闭等瞬时错误:断开由读循环统一上报
        }
    }

    /// <summary>按新尺寸重建 ShellStream(同一连接,免重认证;登录 shell 会重启)。</summary>
    public void Resize(int cols, int rows)
    {
        if (_client?.IsConnected != true) return;

        _generation++;
        var old = _stream;
        _stream = null;                          // 旧读循环凭引用比对安静退出
        try { old?.Dispose(); } catch { }

        var fresh = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 16384);
        _stream = fresh;
        StartReadLoop(fresh, _generation);
    }

    /// <summary>读循环:DataAvailable 时同步读一把,否则小睡等待;攒批后派发回 UI 线程。</summary>
    private void StartReadLoop(ShellStream stream, int gen)
    {
        var ct = _cts!.Token;
        _ = Task.Run(async () =>
        {
            var buf = new byte[16384];
            var chars = new char[16384];
            var decoder = Encoding.UTF8.GetDecoder();  // 正确处理跨读的半个多字节字符
            var pending = new StringBuilder();

            void Flush()
            {
                if (pending.Length == 0) return;
                var text = pending.ToString();
                pending.Clear();
                _dq.TryEnqueue(() => OutputReceived?.Invoke(text));
            }

            try
            {
                while (!ct.IsCancellationRequested && ReferenceEquals(_stream, stream))
                {
                    if (!stream.CanRead) break;
                    if (stream.DataAvailable)
                    {
                        var n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        pending.Append(chars, 0, decoder.GetChars(buf, 0, n, chars, 0));
                        if (pending.Length >= 8192) Flush();
                    }
                    else
                    {
                        Flush();
                        await Task.Delay(20, ct);
                    }
                }
                Flush();
            }
            catch (OperationCanceledException) { }
            catch
            {
                // 读失败通常意味着连接已断,走下方统一上报
            }

            // 仍是当前流却退出循环 → 连接真的断了;若是被 Resize 换掉则安静退出
            if (!ct.IsCancellationRequested && ReferenceEquals(_stream, stream))
            {
                _stream = null;
                _dq.TryEnqueue(() => Closed?.Invoke("连接已断开。"));
            }
        });
    }

    /// <summary>清空事件订阅(事件不允许在声明类外赋值,外部用此方法解除挂接)。</summary>
    public void ClearHandlers()
    {
        OutputReceived = null;
        Closed = null;
    }

    /// <summary>主动断开(不触发 Closed 事件)。</summary>
    public void Close()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _generation++;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try { _client?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public void Dispose() => Close();
}