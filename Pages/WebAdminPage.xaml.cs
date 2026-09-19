using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using NasToolbox.Models;
using NasToolbox.Services;

namespace NasToolbox.Pages;

/// <summary>
/// Web 管理:左侧设备侧边栏 + 右侧 WebView2 内嵌各 NAS 的 Web 管理界面。
/// 打开地址取 <see cref="NasDevice.WebOrDefault"/>(设备管理中可编辑端口);顶部不设地址栏,
/// 仅保留后退 / 前进 / 刷新 / 在系统浏览器打开,导航统一走左侧设备列表。
/// 在线状态沿用 DeviceProbeService 的持久化结果。
/// </summary>
public sealed partial class WebAdminPage : Page
{
    private List<DeviceRow> _rows = new();

    public WebAdminPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadDevices();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // NavigationCacheMode=Enabled 时 Loaded 只触发一次,重复进入页面在此刷新设备列表
        if (IsLoaded) ReloadDevices();
    }

    /// <summary>重载设备侧边栏:先套用上次持久化的在线状态;保留原选中(按 Host 匹配),否则选当前管理设备。</summary>
    private void ReloadDevices()
    {
        var keep = (DeviceList.SelectedItem as DeviceRow)?.Device.Host;

        _rows = NasDeviceStore.Load().Select(d => new DeviceRow(d)).ToList();
        foreach (var row in _rows) DeviceProbeService.ApplyCached(row);
        DeviceList.ItemsSource = _rows;

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _rows.FirstOrDefault(r => string.Equals(r.Device.Host, keep, StringComparison.OrdinalIgnoreCase))
                       ?? _rows.FirstOrDefault(r => r.Device.IsCurrent)
                       ?? _rows.FirstOrDefault();
        DeviceList.SelectedItem = selected;
    }

    // ---------- 左侧侧边栏 ----------

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceRow row)
            Navigate(row.Device.WebOrDefault);
    }

    private async void CheckBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        CheckBtn.IsEnabled = false;
        try
        {
            await DeviceProbeService.ProbeAllAsync(_rows, markChecking: true);
        }
        finally
        {
            CheckBtn.IsEnabled = true;
        }
    }

    private void ReloadBtn_Click(object sender, RoutedEventArgs e) => ReloadDevices();

    private void GoDevicesBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.NavigateByTag("devices");

    // ---------- 右侧浏览器区 ----------

    /// <summary>导航到指定地址:自动补 http 前缀;地址未变化时跳过,避免重复进页面就整页刷新。</summary>
    private void Navigate(string? address)
    {
        var url = (address ?? "").Trim();
        if (url.Length == 0) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && AdminWeb.Source != uri)
            AdminWeb.Source = uri;
    }

    private void AdminWeb_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        WebProgress.Visibility = Visibility.Visible;
        WebStatus.Visibility = Visibility.Collapsed;
    }

    private void AdminWeb_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        WebProgress.Visibility = Visibility.Collapsed;
        BackBtn.IsEnabled = sender.CoreWebView2?.CanGoBack ?? false;
        ForwardBtn.IsEnabled = sender.CoreWebView2?.CanGoForward ?? false;

        if (!args.IsSuccess)
        {
            WebStatus.Text = "页面加载失败:请确认设备在线、Web 地址与端口正确(自签名 HTTPS 证书可能被 WebView2 拦截,可改用系统浏览器打开)。";
            WebStatus.Visibility = Visibility.Visible;
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e) => AdminWeb.CoreWebView2?.GoBack();

    private void ForwardBtn_Click(object sender, RoutedEventArgs e) => AdminWeb.CoreWebView2?.GoForward();

    private void WebRefreshBtn_Click(object sender, RoutedEventArgs e) => AdminWeb.CoreWebView2?.Reload();

    private void OpenBrowserBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = AdminWeb.Source?.OriginalString;
        if (string.IsNullOrWhiteSpace(url) && DeviceList.SelectedItem is DeviceRow row)
            url = row.Device.WebOrDefault;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 无默认浏览器等环境异常:忽略,不打断界面
        }
    }
}