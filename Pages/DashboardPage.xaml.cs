using System.Globalization;
using System.Management;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Models;
using NasToolbox.Services;

namespace NasToolbox.Pages;

/// <summary>
/// 首页概览:本机网络 + WMI 系统摘要(后台线程查询),NAS 设备在线速览。
/// </summary>
public sealed partial class DashboardPage : Page
{
    private List<DeviceRow> _rows = new();

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ = LoadLocalAsync();
            ReloadDevices();
        };
    }

    private async Task LoadLocalAsync()
    {
        // 网络信息很快,直接同步取
        var (hostName, ips, gateways) = NetworkService.GetLocalNetworkInfo();
        HostText.Text = hostName;
        IpText.Text = ips.Count > 0 ? string.Join("   ", ips) : "未检测到";
        GwText.Text = gateways.Count > 0 ? string.Join(", ", gateways) : "-";

        // WMI 较慢,放后台线程;await 回到 UI 线程后再回填
        var (cpu, mem, os, uptime) = await Task.Run(QueryWmi);

        CpuText.Text = string.IsNullOrEmpty(cpu) ? "未知" : cpu;
        MemText.Text = string.IsNullOrEmpty(mem) ? "未知" : $"{mem} GB";
        OsText.Text = string.IsNullOrEmpty(os) ? "未知" : os;
        UpText.Text = string.IsNullOrEmpty(uptime) ? "未知" : uptime;
    }

    private static (string cpu, string mem, string os, string uptime) QueryWmi()
    {
        string Query(string wql, string prop)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(wql);
                foreach (var o in searcher.Get())
                {
                    var v = o[prop]?.ToString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch
            {
                // WMI 查询失败不致命
            }
            return string.Empty;
        }

        var cpu = Query("SELECT Name FROM Win32_Processor", "Name");

        var mem = string.Empty;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
            {
                mem = (Convert.ToDouble(o["TotalPhysicalMemory"]) / (1024.0 * 1024 * 1024)).ToString("0");
                break;
            }
        }
        catch { }

        var os = Query("SELECT Caption FROM Win32_OperatingSystem", "Caption");

        var uptime = string.Empty;
        try
        {
            var raw = Query("SELECT LastBootUpTime FROM Win32_OperatingSystem", "LastBootUpTime");
            if (raw.Length >= 14)
            {
                var boot = DateTime.ParseExact(raw[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                var span = DateTime.Now - boot;
                uptime = $"{(int)span.TotalDays} 天 {span.Hours} 小时 {span.Minutes} 分钟";
            }
        }
        catch { }

        return (cpu, mem, os, uptime);
    }

    private void ReloadDevices()
    {
        _rows = NasDeviceStore.Load().Select(d => new DeviceRow(d)).ToList();
        // 先显示上次持久化的检测结果,避免每次进首页都灰着一片
        foreach (var row in _rows) DeviceProbeService.ApplyCached(row);
        DeviceList.ItemsSource = _rows;
        DevEmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CheckAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        CheckAllBtn.IsEnabled = false;
        await DeviceProbeService.ProbeAllAsync(_rows, markChecking: true);
        CheckAllBtn.IsEnabled = true;
    }
}