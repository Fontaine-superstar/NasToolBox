namespace NasToolbox.Models;

/// <summary>一次 TCP 端口探测的结果。</summary>
public sealed record PortProbeResult(int Port, string Service, bool Open, int ElapsedMs);