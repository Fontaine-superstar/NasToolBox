using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NasToolbox.Services;

/// <summary>
/// Wake-on-LAN:向受限广播 + 各网卡子网定向广播发送魔术包(端口 9/7)。
/// 需要目标 NAS 的 BIOS/UEFI 与网卡开启 WOL 支持。
/// </summary>
public static class WakeOnLanService
{
    /// <summary>宽容解析 MAC:兼容 AA:BB:CC:DD:EE:FF / AA-BB-… / AABBCCDDEEFF 等写法。</summary>
    public static bool TryParseMac(string? mac, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(mac)) return false;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return false;
        bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return true;
    }

    /// <summary>发送唤醒包,任一目标发送成功即返回 true。</summary>
    public static async Task<bool> SendAsync(string mac)
    {
        if (!TryParseMac(mac, out var macBytes) || macBytes is null) return false;

        // 魔术包:6 字节 0xFF + 目标 MAC 重复 16 次
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, 9),
            new(IPAddress.Broadcast, 7),
        };
        targets.AddRange(GetSubnetBroadcasts().Select(ip => new IPEndPoint(ip, 9)));

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var anySent = false;
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(packet, packet.Length, target);
                anySent = true;
            }
            catch
            {
                // 单个目标失败不影响其余
            }
        }
        return anySent;
    }

    /// <summary>依据各网卡的 IPv4 地址与前缀长度计算子网定向广播地址(穿透部分路由/AP 的隔离)。</summary>
    private static IEnumerable<IPAddress> GetSubnetBroadcasts()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue; // 只关心能出网的网卡

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    try
                    {
                        var prefix = ua.PrefixLength; // Windows 上为非空 int,部分平台可能抛异常(已捕获)
                        if (prefix is 0 or 32) continue;
                        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                        var ip = BitConverter.ToUInt32(ua.Address.GetAddressBytes(), 0);
                        var broadcastBytes = BitConverter.GetBytes(ip | ~mask);
                        list.Add(new IPAddress(broadcastBytes));
                    }
                    catch
                    {
                        // 单地址失败忽略
                    }
                }
            }
        }
        catch
        {
            // 枚举失败忽略
        }
        return list.Distinct();
    }
}