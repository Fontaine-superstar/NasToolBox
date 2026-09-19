using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using NasToolbox.Models;
using NasToolbox.Services;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Pages;

/// <summary>
/// 终端:WebView2 内嵌 xterm.js + <see cref="SshTerminalSession"/>(SSH.NET ShellStream)的交互式 SSH 终端。
/// 终端走独立连接,不占命令执行缓存;窗口变化时在同一连接上按新尺寸重建 shell
/// (vim / top 等备用屏程序运行期间跳过,避免杀死会话)。页面缓存,切页回来会话仍在。
/// </summary>
public sealed partial class ShellPage : Page
{
    private readonly List<NasDevice> _devices = new();
    private bool _suppressBox;
    private bool _webReady;
    private (int Cols, int Rows) _termSize = (100, 30);
    private (int Cols, int Rows) _pendingSize;
    private SshTerminalSession? _session;
    private DispatcherTimer? _resizeTimer;

    public ShellPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Unloaded += (_, _) => _resizeTimer?.Stop();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // NavigationCacheMode=Enabled:重复进入在此刷新设备列表(不打断已建立的会话)
        if (IsLoaded) ReloadDevices();
    }

    private NasDevice? Current => DeviceBox.SelectedItem as NasDevice;

    /// <summary>初始化:设备下拉 + WebView2(虚拟主机映射到本地 Assets/Terminal)。</summary>
    private async Task InitAsync()
    {
        ReloadDevices();
        if (_webReady) return;

        try
        {
            await TermWeb.EnsureCoreWebView2Async();
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            TermWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.terminal", dir, CoreWebView2HostResourceAccessKind.Allow);
            TermWeb.CoreWebView2.Navigate("https://app.terminal/host.html");
            _webReady = true;
        }
        catch
        {
            StatusText.Text = "终端组件初始化失败(WebView2 或 xterm 资源缺失)。";
        }
    }

    private void ReloadDevices()
    {
        _devices.Clear();
        _devices.AddRange(NasDeviceStore.Load());

        _suppressBox = true;
        try
        {
            DeviceBox.ItemsSource = _devices;
            var current = NasDeviceStore.GetCurrentOrDefault(_devices);
            if (current is not null) DeviceBox.SelectedItem = current;
            else if (_devices.Count > 0) DeviceBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressBox = false;
        }

        if (_devices.Count == 0)
            StatusText.Text = "还没有设备:请先在「设备管理」中登记 NAS(需配置 SSH 用户名)。";
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressBox) return;

        // 改选即同步为「当前管理设备」,与其他功能页一致;并自动连到新设备
        if (Current is { } device && !device.IsCurrent)
            NasDeviceStore.SetCurrent(_devices, device);

        _ = ConnectAsync();
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    private async Task ConnectAsync()
    {
        if (Current is not { } dev)
        {
            StatusText.Text = "请先选择设备。";
            return;
        }
        if (!dev.HasSshUser)
        {
            StatusText.Text = "该设备未配置 SSH 用户名:请先在「设备管理 → 编辑」中填写。";
            return;
        }

        DisconnectQuiet();
        ConnectBtn.IsEnabled = false;
        StatusText.Text = $"正在连接 {dev.SshDisplay} …";

        var session = new SshTerminalSession(DispatcherQueue.GetForCurrentThread());
        session.OutputReceived += WriteToTerm;
        session.Closed += reason =>
        {
            StatusText.Text = $"已断开:{reason}";
            DisconnectQuiet();
            EmptyHint.Visibility = Visibility.Visible;
        };
        _session = session;

        var (ok, message) = await session.OpenAsync(dev, _termSize.Cols, _termSize.Rows);
        if (!ok)
        {
            StatusText.Text = $"连接失败:{message}";
            DisconnectQuiet();
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        DisconnectBtn.Visibility = Visibility.Visible;
        ConnectBtn.IsEnabled = true;
        StatusText.Text = $"已连接 {dev.SshDisplay}";
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        DisconnectQuiet();
        EmptyHint.Visibility = Visibility.Visible;
        StatusText.Text = "已断开。";
    }

    /// <summary>静默断开当前会话并复位按钮态(不弹提示)。</summary>
    private void DisconnectQuiet()
    {
        if (_session is not null)
        {
            var s = _session;
            _session = null;
            s.ClearHandlers();
            s.Close();
        }
        ConnectBtn.IsEnabled = true;
        DisconnectBtn.Visibility = Visibility.Collapsed;
    }

    // ---------- WebView2 ↔ xterm 桥 ----------

    private void TermWeb_WebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.TryGetWebMessageAsString());
            var root = doc.RootElement;
            string S(string k) => root.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            int N(string k) => root.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;

            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _termSize = (N("cols"), N("rows"));
                    // 终端就绪且已有选中设备:自动连接
                    if (_session is null && Current is not null) _ = ConnectAsync();
                    break;

                case "input":
                    _session?.Write(S("data"));
                    break;

                case "resize":
                    // 备用屏(vim/top)运行期间跳过重建,以免杀死会话
                    if (root.TryGetProperty("alt", out var alt) && alt.GetBoolean()) break;
                    _pendingSize = (N("cols"), N("rows"));
                    _resizeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    _resizeTimer.Stop();
                    _resizeTimer.Tick -= ResizeTimer_Tick;   // 防重复挂载
                    _resizeTimer.Tick += ResizeTimer_Tick;
                    _resizeTimer.Start();
                    break;
            }
        }
        catch
        {
            // 非预期消息格式:忽略
        }
    }

    private void ResizeTimer_Tick(object? sender, object e)
    {
        _resizeTimer?.Stop();
        if (_session?.IsOpen != true || _pendingSize == _termSize) return;

        _termSize = _pendingSize;
        _session.Resize(_termSize.Cols, _termSize.Rows);
        WriteToTerm("\r\n\r\n[窗口尺寸已变化,shell 已按新尺寸重启;工作目录与环境变量未保留]\r\n");
    }

    /// <summary>把一段文本写到 xterm(远端输出 / 本地提示)。</summary>
    private void WriteToTerm(string text)
    {
        var core = TermWeb.CoreWebView2;
        if (core is null) return;
        core.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "write", data = text }));
    }
}