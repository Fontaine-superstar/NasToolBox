using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using NasToolbox.Models;
using NasToolbox.Services;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Pages;

/// <summary>网络诊断:Ping 连通性测试、NAS 外网连通性诊断(SSH 在 NAS 上执行)+ 基于 Docker 的 Speedtest-x 局域网测速。</summary>
public sealed partial class NetworkToolsPage : Page
{
    private int _speedPort; // 最近一次测速容器的宿主端口,供「打开网页版」复用

    // 网页端测速:轮询页面数值,稳定/引擎结束后取页面显示的最终结果
    private DispatcherTimer? _webPoll;
    private string _lastWebJson = "";
    private int _webStable;
    private int _webTicks;

    public NetworkToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { LoadSpeedDevices(); LoadDiagDevices(); };
        Unloaded += (_, _) => _webPoll?.Stop();
    }

    private async void PingBtn_Click(object sender, RoutedEventArgs e)
    {
        var host = PingHostBox.Text.Trim();
        if (host.Length == 0)
        {
            PingOut.Text = "请先输入 IP / 主机名。";
            return;
        }
        if (!int.TryParse(PingCountBox.Text.Trim(), out var count)) count = 4;
        count = Math.Clamp(count, 1, 20);

        PingBtn.IsEnabled = false;
        PingRing.Visibility = Visibility.Visible;
        try
        {
            PingOut.Text = await NetworkService.PingReportAsync(host, count);
        }
        finally
        {
            PingBtn.IsEnabled = true;
            PingRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- NAS 外网诊断(命令在 NAS 上执行) ----------

    /// <summary>加载外网诊断的设备下拉框:默认选中「当前管理设备」。</summary>
    private void LoadDiagDevices()
    {
        var devices = NasDeviceStore.Load();
        NasDiagDeviceBox.ItemsSource = devices;
        var current = NasDeviceStore.GetCurrentOrDefault(devices);
        if (current is not null) NasDiagDeviceBox.SelectedItem = current;
        else if (devices.Count > 0) NasDiagDeviceBox.SelectedIndex = 0;
    }

    private async void NasDiagPingBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!DiagInputOk(out var device, out var host, out var count)) return;

        SetDiagBusy(true);
        NasDiagStatus.Text = $"正在从 {device.DisplayName} 上 Ping {host} …";
        try
        {
            // ping 由普通用户即可执行;超时 = 次数 × 等待 + 握手余量
            var r = await SshService.RunAsync(device, $"ping -c {count} -W 2 {host} 2>&1", count * 2000 + 15000);

            NasPingOut.Text = r.Output;
            NasLossPanel.Visibility = Visibility.Visible;

            var lossM = Regex.Match(r.Stdout ?? "", @"(\d+(?:\.\d+)?)% packet loss");
            var rttM = Regex.Match(r.Stdout ?? "", @"= [\d.]+/([\d.]+)/");
            var loss = lossM.Success ? lossM.Groups[1].Value : null;
            var avg = rttM.Success ? rttM.Groups[1].Value : null;
            NasLossValue.Text = loss is not null ? $"{loss} %" : "—";

            NasDiagStatus.Text = loss is not null
                ? $"{host} · 丢包 {loss}%" + (avg is not null ? $" · 平均 {avg} ms" : "")
                : "Ping 失败:目标不可达、禁止 ICMP,或 NAS 无外网。";
        }
        catch (Exception ex)
        {
            NasDiagStatus.Text = ex.Message;
        }
        finally
        {
            SetDiagBusy(false);
        }
    }

    /// <summary>校验设备、Ping 目标与次数输入;不通过时给出提示并返回 false。</summary>
    private bool DiagInputOk(out NasDevice device, out string host, out int count)
    {
        device = NasDiagDeviceBox.SelectedItem as NasDevice ?? new NasDevice();
        host = "";
        count = 4;
        if (NasDiagDeviceBox.SelectedItem is not NasDevice d)
        {
            NasDiagStatus.Text = "还没有 NAS 设备:请先到「设备管理」添加并配置 SSH。";
            return false;
        }
        device = d;

        host = DiagHostBox.Text.Trim();
        if (host.Length == 0)
        {
            NasDiagStatus.Text = "请先输入 Ping 目标。";
            return false;
        }
        // 目标会拼进 shell 命令,只放行 IP/主机名的合法字符,防注入
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9.\-]+$"))
        {
            NasDiagStatus.Text = "Ping 目标格式不正确:只支持 IP 或主机名。";
            return false;
        }

        if (!int.TryParse(DiagCountBox.Text.Trim(), out count)) count = 4;
        count = Math.Clamp(count, 1, 10);
        return true;
    }

    private void SetDiagBusy(bool busy)
    {
        NasPingBtn.IsEnabled = !busy;
        NasDiagRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Speedtest-x 测速 ----------

    /// <summary>加载 NAS 设备下拉框:默认选中「当前管理设备」。</summary>
    private void LoadSpeedDevices()
    {
        var devices = NasDeviceStore.Load();
        SpeedDeviceBox.ItemsSource = devices;
        var current = NasDeviceStore.GetCurrentOrDefault(devices);
        if (current is not null) SpeedDeviceBox.SelectedItem = current;
        else if (devices.Count > 0) SpeedDeviceBox.SelectedIndex = 0;

        TryShowLastResult();
    }

    /// <summary>设备下拉切换时,Ping 目标默认跟随选中的 NAS(仍可手动改成任意地址)。</summary>
    private void SpeedDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedDeviceBox.SelectedItem is NasDevice d)
            PingHostBox.Text = d.Host;
    }

    /// <summary>进入页面时默认展示最近一次测速结果(设备匹配时)。</summary>
    private void TryShowLastResult()
    {
        if (SpeedDeviceBox.SelectedItem is NasDevice d &&
            SpeedtestService.LastTest is { } last &&
            last.Host == d.Host)
        {
            RenderSpeedResult(last.Result, last.Port, $"上次测速 · {last.At:HH:mm:ss},点击「开始测速」重新测量");
        }
    }

    /// <summary>把测速结果渲染到面板,并记录端口供「打开网页版」使用。</summary>
    private void RenderSpeedResult(SpeedtestService.SpeedtestResult r, int port, string statusLine)
    {
        PingValue.Text = r.PingMs > 0 ? $"{r.PingMs:F1} ms" : "0 ms";
        DownValue.Text = $"{r.DownloadMbps:F1} Mbps";
        UpValue.Text = $"{r.UploadMbps:F1} Mbps";
        SpeedResults.Visibility = Visibility.Visible;

        if (r.IpInfo.Length > 0)
        {
            SpeedIp.Text = r.IpInfo;
            SpeedIp.Visibility = Visibility.Visible;
        }
        else SpeedIp.Visibility = Visibility.Collapsed;

        if (port > 0) _speedPort = port;
        SpeedStatus.Text = statusLine;
    }

    private async void SpeedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SpeedDeviceBox.SelectedItem is not NasDevice device)
        {
            SpeedStatus.Text = "还没有 NAS 设备:请先到「设备管理」添加并配置 SSH。";
            return;
        }

        SpeedBtn.IsEnabled = false;
        SpeedRing.Visibility = Visibility.Visible;
        SpeedBar.Visibility = Visibility.Collapsed;
        SpeedBar.Value = 0;
        SpeedStatus.Text = $"正在扫描 {device.DisplayName} 上的 Docker 容器…";
        try
        {
            // 先扫描已有容器:找到 speedtest-x 才测速;没有则引导去 Docker 管理部署
            var container = await SpeedtestService.FindAsync(device);
            if (container is null)
            {
                SpeedStatus.Text = "未找到 speedtest-x 容器,等待选择获取方式…";
                var choice = await AskMissingSpeedtestAsync();
                if (choice == ContentDialogResult.Primary)
                {
                    OpenReleasesPage();
                    SpeedStatus.Text =
                        $"已打开下载页:下载后把其中 img\\ 里的 {DockerService.LocalImageFileName} 放到本应用目录的 img\\ 下," +
                        $"再到「Docker 管理 → 部署容器」点「建议镜像」离线导入。";
                }
                else if (choice == ContentDialogResult.Secondary)
                {
                    GoToDockerPage(hintSpeedtest: true);
                }
                else
                {
                    SpeedStatus.Text = "已取消:准备好 speedtest-x 容器后再回来测速。";
                }
                return;
            }

            // 未运行先启动:docker ps -a 对停止的容器不显示 Ports,启动后再解析端口才拿得到
            if (!container.Running)
            {
                SpeedStatus.Text = $"容器「{container.Name}」未运行,正在启动…";
                await SpeedtestService.StartAsync(device, container.Name);
            }

            // 解析该容器实际映射的宿主端口(扫描字段拿不到时用 docker port 补查)
            var port = await SpeedtestService.ResolveHostPortAsync(device, container);
            if (port <= 0)
            {
                SpeedStatus.Text =
                    $"未能发现容器「{container.Name}」的端口映射:请确认已用 -p 把宿主端口映射到容器 80(host 网络模式无法自动发现)。";
                return;
            }
            _speedPort = port;

            // 内嵌网页端引擎测速(隐藏 WebView,仅取数据):完成后抓取页面显示的最终数值,与网页版完全一致
            var url = SpeedtestService.UrlOf(device, port);
            SpeedBar.Visibility = Visibility.Collapsed;
            SpeedStatus.Text = $"正在加载测速引擎({url})…";
            StartWebSpeedtest(url);
        }
        catch (Exception ex)
        {
            SpeedStatus.Text = ex.Message;
        }
        finally
        {
            SpeedBtn.IsEnabled = true;
            SpeedRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenSpeedPage_Click(object sender, RoutedEventArgs e)
    {
        if (SpeedDeviceBox.SelectedItem is NasDevice device)
            SpeedtestService.OpenUrl(SpeedtestService.UrlOf(device, _speedPort > 0 ? _speedPort : 80));
    }

    /// <summary>跳转到 Docker 管理页(经侧栏选中触发导航,保持高亮同步)。</summary>
    /// <summary>跳转 Docker 管理页;hintSpeedtest=true 时让对方页面自动弹出预填好的部署对话框。</summary>
    private static void GoToDockerPage(bool hintSpeedtest = false)
    {
        if (hintSpeedtest) DockerPage.PendingSpeedtestDeploy = true;
        MainWindow.Instance?.NavigateByTag("docker");
    }

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DockerService.ReleasesUrl) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器也不影响用户手动复制链接
        }
    }

    /// <summary>
    /// NAS 上没有 speedtest-x 容器时的引导:要么在线拉取,要么从 Releases 下载本地镜像 tar 离线导入。
    /// 返回用户选择:主按钮=打开下载页,次按钮=去 Docker 管理,关闭=取消。
    /// </summary>
    private async Task<ContentDialogResult> AskMissingSpeedtestAsync()
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = "测速需要 NAS 上有名为 speedtest-x 的容器(镜像 badapple9/speedtest-x;按容器名查找,名字不同会被视为未部署),当前没有找到。两种获取方式:",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "1) 在线拉取:到「Docker 管理 → 部署容器」,镜像填 badapple9/speedtest-x,勾选「部署前先拉取镜像」" +
                   "(需 NAS 能联网,镜像约 460 MB)。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"2) 离线导入:从下面的 Releases 页下载安装包,把其中 img\\ 目录里的 {DockerService.LocalImageFileName} " +
                   $"放到本应用目录的 img\\ 下,再在部署对话框里点「建议镜像」,程序会自动上传并 docker load," +
                   $"不需要联网拉取。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = DockerService.ReleasesUrl,
            NavigateUri = new Uri(DockerService.ReleasesUrl),
            Padding = new Thickness(0),
        });

        var dlg = new ContentDialog
        {
            Title = "未找到 speedtest-x 容器",
            Content = panel,
            PrimaryButtonText = "打开 Releases 下载页",
            SecondaryButtonText = "去 Docker 管理部署",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        return await dlg.ShowAsync();
    }

    // ---------- 内嵌网页端测速 ----------

    /// <summary>自动开始按钮的注入脚本:speedtest-x 调 startStop(),OpenSpeedTest 点击 START。</summary>
    private const string StartJs = """
        (function(){
          try{ if(typeof startStop==='function'){var b=document.getElementById('startStopBtn'); if(b&&b.className.indexOf('running')<0){startStop();return 'x';}} }catch(e){}
          try{ var d=document.getElementById('startButtonDesk')||document.getElementById('startButtonMob'); if(d){d.dispatchEvent(new MouseEvent('click',{bubbles:true}));return 'o';} }catch(e){}
          return '';
        })()
        """;

    /// <summary>采集页面显示数值的脚本:speedtest-x(dlText/ulText/pingText/ip)+ OpenSpeedTest(downResult/upResult/pingResult/YourIP)。</summary>
    private const string CollectJs = """
        (function(){
          function t(id){var e=document.getElementById(id);return e?e.textContent.trim():'';}
          var btn=document.getElementById('startStopBtn');
          return JSON.stringify({
            dl:t('dlText'),ul:t('ulText'),ping:t('pingText'),ip:t('ip'),
            d2:t('downResult'),u2:t('upResult'),p2:t('pingResult'),ip2:t('YourIP'),
            running: btn? (btn.className.indexOf('running')>=0) : false
          });
        })()
        """;

    private void StartWebSpeedtest(string url)
    {
        _webPoll?.Stop();
        _lastWebJson = "";
        _webStable = 0;
        _webTicks = 0;
        SpeedWeb.Source = new Uri(url);
    }

    private async void SpeedWeb_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            SpeedStatus.Text = "测速页加载失败:请点下方链接在浏览器中打开检查,或确认容器运行正常。";
            return;
        }

        try
        {
            await Task.Delay(800); // 等页面脚本就绪
            await SpeedWeb.ExecuteScriptAsync(StartJs);
        }
        catch { /* 注入失败无碍:用户可手动点击页面中的开始按钮 */ }

        _webStable = 0;
        _webPoll ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _webPoll.Tick -= WebPoll_Tick;
        _webPoll.Tick += WebPoll_Tick;
        _webPoll.Start();
    }

    private async void WebPoll_Tick(object? sender, object e)
    {
        if (++_webTicks > 240)
        {
            _webPoll?.Stop();
            SpeedStatus.Text = "等待超时:请重试,或点下方链接改用浏览器打开网页版测速。";
            return;
        }

        try
        {
            var raw = await SpeedWeb.ExecuteScriptAsync(CollectJs);
            var inner = JsonDocument.Parse(raw).RootElement.GetString();
            using var doc = JsonDocument.Parse(inner ?? "{}");
            var root = doc.RootElement;
            string V(string k) => root.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";

            // speedtest-x / LibreSpeed 优先;没有再取 OpenSpeedTest 的元素
            var dlS = V("dl");
            var ulS = V("ul");
            var pingS = V("ping");
            var ipS = V("ip");
            if (dlS.Length == 0 && V("d2").Length > 0)
            {
                dlS = V("d2");
                ulS = V("u2");
                pingS = V("p2");
                ipS = V("ip2");
            }

            var dl = ParseNum(dlS);
            var ul = ParseNum(ulS);
            var ping = ParseNum(pingS);

            // 实时刷新结果面板
            SpeedResults.Visibility = Visibility.Visible;
            if (dl > 0) DownValue.Text = $"{dl:F1} Mbps";
            if (ul > 0) UpValue.Text = $"{ul:F1} Mbps";
            if (ping > 0) PingValue.Text = $"{ping:F1} ms";
            if (ipS.Length > 0)
            {
                SpeedIp.Text = ipS;
                SpeedIp.Visibility = Visibility.Visible;
            }

            // 完成:三项数值齐备,且网页引擎已结束(speedtest-x 按钮 running 类消失)或数值连续 5 秒不变
            var running = V("running") == "true";
            var snapshot = $"{dlS}|{ulS}|{pingS}|{ipS}";
            _webStable = snapshot == _lastWebJson ? _webStable + 1 : 0;
            _lastWebJson = snapshot;

            var complete = dl > 0 && ul > 0 && ping > 0 && (!running || _webStable >= 5);
            if (complete)
            {
                _webPoll?.Stop();
                var result = new SpeedtestService.SpeedtestResult(ping, dl, ul, ipS);
                if (SpeedDeviceBox.SelectedItem is NasDevice device)
                    SpeedtestService.Remember(device, _speedPort, result);
                RenderSpeedResult(result, _speedPort, $"测速完成(网页端数据)· {DateTime.Now:HH:mm:ss}");
            }
            else
            {
                var stage = running ? "测速进行中"
                    : dl > 0 || ping > 0 ? "测速进行中"
                    : "正在启动页面测速引擎";
                SpeedStatus.Text = $"{stage}…(数据取自网页端引擎)";

                // 自动开始的注入可能早于页面脚本就绪,期间每 5 秒补注一次
                if (!running && dl == 0 && ping == 0 && _webTicks % 5 == 0)
                    _ = SpeedWeb.ExecuteScriptAsync(StartJs);
            }
        }
        catch
        {
            // 页面跳转 / 脚本尚未就绪等瞬时错误:下一轮再试
        }
    }

    /// <summary>从页面文本中提取数值;带 G/K 单位时换算为 Mbps。</summary>
    private static double ParseNum(string s)
    {
        var m = Regex.Match(s, @"\d+(?:\.\d+)?");
        if (!m.Success) return 0;
        if (!double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        if (s.Contains('G', StringComparison.OrdinalIgnoreCase)) v *= 1000; // Gbps → Mbps
        if (s.Contains('K', StringComparison.OrdinalIgnoreCase)) v /= 1000; // Kbps → Mbps
        return v;
    }
}
