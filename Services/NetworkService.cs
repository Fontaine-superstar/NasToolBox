using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// 网络诊断:Ping 报告、TCP 端口探测、DNS 解析、本机网络信息。
/// 全部基于 BCL,无第三方依赖。
/// </summary>
public static class NetworkService
{
    /// <summary>单次 Ping,返回(是否成功, 摘要)。</summary>
    public static async Task<(bool Ok, string Detail)> PingOnceAsync(string host, int timeoutMs = 1500)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            if (reply.Status == IPStatus.Success)
                return (true, $"{reply.Address} · {reply.RoundtripTime} ms");
            return (false, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>多次 Ping 并生成中文报告(类似系统 ping 命令输出 + 统计)。</summary>
    public static async Task<string> PingReportAsync(string host, int count = 4)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"正在 Ping {host}(共 {count} 次)…");

        var times = new List<long>();
        var lost = 0;
        using var ping = new Ping();

        for (var i = 0; i < count; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, 2000);
                if (reply.Status == IPStatus.Success)
                {
                    times.Add(reply.RoundtripTime);
                    sb.AppendLine($"来自 {reply.Address} 的回复:字节=32 时间={reply.RoundtripTime}ms TTL={reply.Options?.Ttl ?? 0}");
                }
                else
                {
                    lost++;
                    sb.AppendLine($"请求失败:{reply.Status}");
                }
            }
            catch (Exception ex)
            {
                lost++;
                sb.AppendLine($"错误:{ex.GetBaseException().Message}");
            }
            if (i < count - 1) await Task.Delay(300);
        }

        sb.AppendLine();
        sb.AppendLine($"统计:发送={count},接收={count - lost},丢失={lost}({lost * 100.0 / Math.Max(count, 1):0}% 丢失)");
        if (times.Count > 0)
        {
            sb.AppendLine($"延迟:最短 = {times.Min()}ms,最长 = {times.Max()}ms,平均 = {(int)times.Average()}ms");
        }
        return sb.ToString();
    }

    /// <summary>带超时的 TCP 端口探测。</summary>
    public static async Task<PortProbeResult> TestPortAsync(string host, int port, string service, int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        var open = false;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            open = tcp.Connected;
        }
        catch
        {
            open = false;
        }
        sw.Stop();
        return new PortProbeResult(port, service, open, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>DNS 解析报告,每行一条 "域名 → IP"。</summary>
    public static async Task<string> ResolveReportAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0) return $"{host}:未解析到任何地址";
            return string.Join(Environment.NewLine, addresses.Select(a => $"{host} → {a}"));
        }
        catch (Exception ex)
        {
            return $"解析失败:{ex.GetBaseException().Message}";
        }
    }

    /// <summary>
    /// 虚拟网卡(虚拟机 / 容器 / 隧道 / 伪接口)的名称或描述关键词,不区分大小写。
    /// 命中即视为非物理网卡,本机 IP 展示时隐藏。
    /// </summary>
    private static readonly string[] VirtualNicKeywords =
    {
        // VMware
        "vmware", "vmnet", "vmxnet",
        // VirtualBox
        "virtualbox", "vbox",
        // Hyper-V / WSL / Docker Desktop
        "hyper-v", "hyperv", "wsl", "docker",
        // 通用虚拟化
        "virtio", "qemu", "kvm", "xen", "parallels",
        "virtual ethernet", "virtual switch", "virtual adapter", "virtual machine", "虚拟机",
        // Windows 伪接口 / 隧道 / VPN 虚拟网卡
        "wan miniport", "wi-fi direct", "wifi direct", "kernel debug", "内核调试",
        "teredo", "isatap", "6to4", "pseudo-interface", "隧道",
        "npcap", "loopback", "tap-windows", "tap-win", "openvpn", "wintun", "zerotier", "radmin",
    };

    /// <summary>虚拟化厂商的 MAC 地址前三字节(OUI),用于兜底识别改过名的虚拟网卡。</summary>
    private static readonly (byte A, byte B, byte C)[] VirtualMacOuis =
    {
        (0x00, 0x50, 0x56), // VMware
        (0x00, 0x0C, 0x29), // VMware
        (0x00, 0x05, 0x69), // VMware
        (0x00, 0x1C, 0x14), // VMware
        (0x08, 0x00, 0x27), // VirtualBox
        (0x0A, 0x00, 0x27), // VirtualBox
        (0x00, 0x15, 0x5D), // Hyper-V / WSL
        (0x00, 0x1C, 0x42), // Parallels
        (0x52, 0x54, 0x00), // QEMU / KVM
        (0x00, 0x16, 0x3E), // Xen
        (0x00, 0x03, 0xFF), // Microsoft Virtual PC
    };

    /// <summary>
    /// 判断是否为虚拟网卡:名称 / 描述含虚拟化关键词,或 MAC 属于虚拟化厂商 OUI。
    /// 物理网卡(含 USB 网卡、Wi-Fi、蓝牙 PAN)一律返回 false。
    /// </summary>
    public static bool IsVirtualAdapter(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}";
        foreach (var k in VirtualNicKeywords)
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }

        byte[] mac;
        try
        {
            mac = nic.GetPhysicalAddress().GetAddressBytes();
        }
        catch
        {
            return false;
        }

        if (mac.Length < 3 || (mac[0] == 0 && mac[1] == 0 && mac[2] == 0)) return false;

        foreach (var (a, b, c) in VirtualMacOuis)
        {
            if (mac[0] == a && mac[1] == b && mac[2] == c) return true;
        }
        return false;
    }

    /// <summary>本机网络信息:主机名、IPv4 地址(含网卡名)、网关。虚拟网卡不计入;192 开头的地址优先展示。</summary>
    public static (string HostName, List<string> Ipv4s, List<string> Gateways) GetLocalNetworkInfo()
    {
        var ips = new List<string>();
        var gateways = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                // VMware / VirtualBox / Hyper-V / WSL / 隧道等虚拟网卡的地址不展示
                if (IsVirtualAdapter(nic)) continue;

                var props = nic.GetIPProperties();
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address))
                    {
                        ips.Add($"{a.Address}({nic.Name})");
                    }
                }
                foreach (var g in props.GatewayAddresses)
                {
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork)
                        gateways.Add(g.Address.ToString());
                }
            }
        }
        catch
        {
            // 枚举失败不致命
        }

        // 展示顺序:192 开头的地址(家用网段 192.168.x.x)排最前,其余保持系统枚举顺序(稳定排序)
        var orderedIps = ips.Distinct()
            .OrderBy(a => a.StartsWith("192.", StringComparison.Ordinal) ? 0 : 1)
            .ToList();
        return (Environment.MachineName, orderedIps, gateways.Distinct().ToList());
    }
}