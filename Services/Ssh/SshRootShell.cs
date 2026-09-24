using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace NasToolbox.Services.Ssh;

/// <summary>
/// root 登录会话:连接建立后执行 sudo -i(需要时自动应答 sudo 密码),
/// 之后所有命令都在这个已提权的交互式 shell 里执行 —— 不必逐条加 sudo,也只输入一次密码。
/// 仅当设备选择「root 会话」权限模式时创建;其余设备仍走一次性 exec 通道。
/// </summary>
internal sealed class SshRootShell : IDisposable
{
    private readonly ShellStream _shell;
    private readonly TimeSpan _readTimeout;

    /// <summary>提示符前缀形状(如 root@nas:~# )。字符类刻意不含空格,避免误伤正常输出行。</summary>
    private static readonly Regex PromptPrefixRegex = new(@"^[\w.\[\]()@:/~-]{0,96}[#\$] ", RegexOptions.Compiled);

    /// <summary>终端 ANSI 转义序列(颜色码等):采集时一律剥掉,避免彩色提示符污染解析。</summary>
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private SshRootShell(ShellStream shell, TimeSpan readTimeout)
    {
        _shell = shell;
        _readTimeout = readTimeout;
    }

    /// <summary>建立 shell 并切换到 root;失败会抛出带中文原因的异常。</summary>
    public static async Task<SshRootShell> CreateAsync(
        SshClient client,
        string sudoPassword,
        int timeoutMs,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 5000, 30000));
        var readTimeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs / 4, 1500, 10000));

        // 终端宽度给足:太窄时长命令的回显会被按列折行,碎片混进输出里污染结果
        var shell = client.CreateShellStream("NasToolbox", 500, 200, 1024, 800, 16384);
        var session = new SshRootShell(shell, readTimeout);

        await Task.Delay(250, ct).ConfigureAwait(false);
        session.Drain();

        shell.WriteLine("sudo -i");

        var kind = await session.WaitForPromptAsync(timeout, ct).ConfigureAwait(false);
        if (kind == PromptKind.Denied)
        {
            session.Dispose();
            throw new InvalidOperationException(
                "该账号没有 sudo 权限(not in the sudoers)。请换用有 sudo 权限的账号,或改用 root 账号直接登录。");
        }

        if (kind == PromptKind.Password)
        {
            if (sudoPassword.Length == 0)
            {
                session.Dispose();
                throw new InvalidOperationException(
                    "sudo -i 需要密码。请在「编辑」里填写 sudo 密码,或在 NAS 上给该账号配置免密 sudo。");
            }

            shell.WriteLine(sudoPassword);
            // 密码可能不对:把「Sorry, try again」识别成明确报错,
            // 否则后续命令会被 sudo 当成密码重试吃掉,最终表现为莫名其妙的超时
            var after = await session.WaitForPromptAsync(timeout, ct, gateOnEcho: false).ConfigureAwait(false);
            if (after == PromptKind.Denied)
            {
                session.Dispose();
                throw new InvalidOperationException(
                    "sudo 密码不正确。请在「编辑」里重新填写 sudo 密码(NAS 上通常与登录密码相同)。");
            }
        }

        // 确认真的提权成功(部分账号无 sudo 权限时会停留在原用户)
        var (output, _) = await session.RunAsync("id -u", timeoutMs, ct).ConfigureAwait(false);
        var uid = output.Trim();
        if (uid.Length == 0 || !uid.EndsWith("0", StringComparison.Ordinal) || uid.Length > 3)
        {
            session.Dispose();
            throw new InvalidOperationException(
                $"sudo -i 未能切换到 root(id -u 返回「{uid}」)。请确认该账号在 sudo 组中且密码正确。");
        }

        // 关闭回显并清空提示符:回显碎片与提示符前缀(root@nas:~# )都不会再混进命令结果
        _ = await session.RunAsync("stty -echo 2>/dev/null; PS1=''", timeoutMs, ct).ConfigureAwait(false);

        return session;
    }

    /// <summary>在 root shell 里执行一条命令,返回输出与退出码。</summary>
    public async Task<(string Output, int ExitCode)> RunAsync(
        string command,
        int timeoutMs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var token = Guid.NewGuid().ToString("N")[..10];
        var marker = "__NT_" + token + "__";

        Drain();

        _shell.WriteLine(command);
        // 拆成两段拼接,避免命令回显行里出现完整 marker 被误判为结束标记
        _shell.WriteLine($"M1='__NT_'; M2='{token}__'; echo $M1$M2$?");

        var lines = new List<string>();
        var exitCode = -1;
        var finished = false;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            var line = await Task.Run(() => _shell.ReadLine(_readTimeout), ct).ConfigureAwait(false);
            if (line is null) continue;

            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var tail = line[(idx + marker.Length)..].Trim();
                var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length > 0) exitCode = int.Parse(digits);
                finished = true;
                break;
            }

            lines.Add(AnsiEscapeRegex.Replace(line.TrimEnd('\r'), string.Empty));
        }

        if (!finished)
            throw new TimeoutException($"root 会话中命令超时({timeoutMs} ms):{command}");

        // 首行可能残留提示符前缀(PS1 清空失败/会话已建立时的兜底),剥离后再做回显比对
        if (lines.Count > 0) lines[0] = PromptPrefixRegex.Replace(lines[0], string.Empty, 1);

        // 抹掉 shell 的命令回显:窄终端会把长命令折成多行回显,去空白拼接后按前缀比对再整段删除
        var echoNorm = StripSpace(command);
        for (var take = Math.Min(4, lines.Count); take > 0; take--)
        {
            var joined = StripSpace(string.Join("", lines.Take(take)));
            if (joined.Length > 0 && echoNorm.StartsWith(joined, StringComparison.Ordinal))
            {
                lines.RemoveRange(0, take);
                break;
            }
        }

        // 抹掉 marker 命令自己的回显行
        if (lines.Count > 0 && lines[^1].Contains("M1=", StringComparison.Ordinal))
            lines.RemoveAt(lines.Count - 1);

        return (string.Join('\n', lines).Trim('\n'), exitCode);
    }

    private enum PromptKind
    {
        None,
        Password,
        Shell,
        Denied,
    }

    /// <summary>
    /// 读原始输出直到出现密码提示 / shell 提示符 / sudo 拒绝信息;超时返回 None。
    /// 关键:密码提示与提示符都不带换行,按行读取永远拼不出完整行,必须累积原始字符流再做匹配,
    /// 否则密码提示永远探测不到,后续命令会被 sudo 当成密码重试吃掉,最终报「id -u 超时」。
    /// </summary>
    private async Task<PromptKind> WaitForPromptAsync(TimeSpan timeout, CancellationToken ct, bool gateOnEcho = true)
    {
        var sw = Stopwatch.StartNew();
        var tail = new StringBuilder();
        // 需要先看到 sudo -i 的回显再认提示符,避免残留的旧用户提示符迟到被误判成提权完成
        var sawEcho = !gateOnEcho;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            // ShellStream 没有带超时的原始读重载:DataAvailable 时同步读一把,否则小睡等待
            if (_shell.DataAvailable)
            {
                var buf = new byte[8192];
                var n = _shell.Read(buf, 0, buf.Length);
                if (n > 0) tail.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            else
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            if (tail.Length > 2048) tail.Remove(0, tail.Length - 2048); // 只留尾部,长 MOTD 不干扰匹配

            if (!sawEcho)
            {
                if (tail.ToString().Contains("sudo -i", StringComparison.Ordinal)) sawEcho = true;
                else continue;
            }

            var text = tail.ToString();

            // sudo 明确拒绝(密码错重试 / 无 sudo 权限)要先判断:
            // 「no password was provided」等拒绝语里同样含 password,先认拒绝更准确
            if (text.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase))
            {
                return PromptKind.Denied;
            }

            if (text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("密码", StringComparison.Ordinal))
            {
                return PromptKind.Password;
            }

            var trimmed = text.TrimEnd();
            if (trimmed.EndsWith("#", StringComparison.Ordinal) ||
                trimmed.EndsWith("$", StringComparison.Ordinal) ||
                trimmed.EndsWith("~", StringComparison.Ordinal))
            {
                return PromptKind.Shell;
            }
        }

        return PromptKind.None;
    }

    /// <summary>去掉全部空白,用于把被终端折行的回显碎片与原命令比对。</summary>
    private static string StripSpace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>清空当前缓冲区里残留的输出(登录 banner、上一条命令的余留等)。</summary>
    private void Drain()
    {
        try
        {
            // 用原始读清缓冲:残留内容可能不含换行(提示符等),按行读清不干净
            var buf = new byte[8192];
            var deadline = DateTime.UtcNow.AddMilliseconds(600);
            while (DateTime.UtcNow < deadline)
            {
                if (!_shell.DataAvailable)
                {
                    Thread.Sleep(100); // 稍等,可能还有路上的尾巴
                    if (!_shell.DataAvailable) break;
                }
                if (_shell.Read(buf, 0, buf.Length) <= 0) break;
            }
        }
        catch
        {
            // 读取失败忽略:后续命令仍会按 marker 判定结果
        }
    }

    public void Dispose()
    {
        try { _shell.Dispose(); }
        catch { /* 关闭失败无需处理 */ }
    }
}
