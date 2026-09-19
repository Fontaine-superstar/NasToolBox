using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Models;
using NasToolbox.Services;

namespace NasToolbox.Pages;

/// <summary>磁盘与共享:本机磁盘空间概览 + 远程 SMB 共享枚举/打开。</summary>
public sealed partial class DiskSharePage : Page
{
    private string _lastShareHost = string.Empty;

    public DiskSharePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadDrives();
            FillDefaultHost();
        };
    }

    /// <summary>SMB 目标默认使用「当前管理设备」的地址(设备管理里勾选的那台),仍可手动修改。</summary>
    private void FillDefaultHost()
    {
        if (SmbHostBox.Text.Trim().Length > 0) return;
        var current = NasDeviceStore.GetCurrentOrDefault(NasDeviceStore.Load());
        if (current is not null) SmbHostBox.Text = current.Host;
    }

    private void LoadDrives()
    {
        DrivesList.ItemsSource = DiskService.GetDrives();
    }

    private void RefreshDrivesBtn_Click(object sender, RoutedEventArgs e) => LoadDrives();

    private async void ListSharesBtn_Click(object sender, RoutedEventArgs e)
    {
        var host = SmbHostBox.Text.Trim();
        if (host.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "请先输入 NAS 的 IP / 主机名。");
            return;
        }

        _lastShareHost = host;
        ListBtn.IsEnabled = false;
        SmbRing.Visibility = Visibility.Visible;
        SharesList.ItemsSource = null;
        SmbStatus.IsOpen = false;

        try
        {
            var shares = await SmbService.ListSharesAsync(host);
            var showHidden = ShowHiddenBox.IsChecked == true;
            var visible = shares.Where(s => showHidden || !s.Hidden).ToList();

            SharesList.ItemsSource = visible;
            ShowStatus(
                visible.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                visible.Count == 0
                    ? $"{host} 没有可显示的共享。"
                    : $"共 {visible.Count} 个共享,单击列表项即可在资源管理器中打开。");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            ListBtn.IsEnabled = true;
            SmbRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenHostBtn_Click(object sender, RoutedEventArgs e)
    {
        var host = SmbHostBox.Text.Trim();
        if (host.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "请先输入 NAS 的 IP / 主机名。");
            return;
        }
        OpenExplorer(@"\\" + host.TrimStart('\\'), "设备可能离线或 SMB 未开放");
    }

    private void SharesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ShareEntry share) return;
        var unc = $@"\\{_lastShareHost.TrimStart('\\')}\{share.Name}";
        OpenExplorer(unc, $"共享 {unc} 可能不可访问(需要账号或已离线)");
    }

    private void OpenExplorer(string unc, string failHint)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = unc, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"打开失败:{failHint}。{ex.Message}");
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        SmbStatus.Severity = severity;
        SmbStatus.Message = message;
        SmbStatus.IsOpen = true;
    }
}