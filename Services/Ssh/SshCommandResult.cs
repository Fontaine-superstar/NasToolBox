namespace NasToolbox.Services.Ssh;

/// <summary>
/// 一条远程命令的执行结果:标准输出与错误输出分离,并保留退出码与耗时。
/// </summary>
public sealed class SshCommandResult
{
    /// <summary>实际下发到远端的完整命令(root 会话下会在已提权的 shell 中执行)。</summary>
    public string Command { get; init; } = "";

    /// <summary>标准输出。</summary>
    public string Stdout { get; init; } = "";

    /// <summary>标准错误。</summary>
    public string Stderr { get; init; } = "";

    /// <summary>远端进程退出码;未取到时为 -1。</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>命令往返耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>是否因超时被中断。</summary>
    public bool TimedOut { get; init; }

    /// <summary>连接 / 认证阶段的错误信息;为 null 表示命令已成功下发。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>命令是否执行成功(有输出、退出码为 0)。</summary>
    public bool Success => ErrorMessage is null && !TimedOut && ExitCode == 0;

    /// <summary>
    /// 供 UI 直接展示的合并文本:优先 stdout,附 stderr 与非零退出码提示。
    /// </summary>
    public string Output
    {
        get
        {
            if (ErrorMessage is not null) return $"执行失败:{ErrorMessage}";
            if (TimedOut) return $"执行超时,已中断。\r\n{Stdout}".TrimEnd();

            var text = Stdout;
            if (!string.IsNullOrWhiteSpace(Stderr))
                text = (text.TrimEnd() + "\r\n[stderr]\r\n" + Stderr.TrimEnd()).TrimStart();
            if (ExitCode != 0)
                text += $"\r\n[退出码 {ExitCode}]";
            return string.IsNullOrWhiteSpace(text) ? "(无输出)" : text.TrimEnd();
        }
    }

    public override string ToString() => Output;
}
