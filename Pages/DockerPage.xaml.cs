using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NasToolbox.Models;
using NasToolbox.Services;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Pages;

/// <summary>
/// Docker 容器管理:选择已添加的 NAS 设备,经 SSH 列出容器并执行启动 / 停止 / 重启 / 删除 / 看日志。
/// 需要 root 的命令依赖设备配置的「root 会话」权限模式。
/// </summary>
public sealed partial class DockerPage : Page
{
    private readonly List<NasDevice> _devices = new();

    /// <summary>
    /// 由「网络诊断 → 测速」跳转过来时置 true:进页面后自动打开部署对话框并预填 speedtest-x,
    /// 否则用户跳过来只看到空列表,不知道要做什么。
    /// </summary>
    public static bool PendingSpeedtestDeploy { get; set; }

    public DockerPage()
    {
        InitializeComponent();
        SectionNav.SelectedIndex = 1; // 默认进入「容器」分区
        Loaded += (_, _) => ReloadDevices();
    }

    /// <summary>页面已缓存,再次导航进来时重新读取设备列表,跟上「设备管理」里的选择。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (IsLoaded) ReloadDevices();

        if (PendingSpeedtestDeploy)
        {
            PendingSpeedtestDeploy = false;
            _ = ShowSpeedtestDeployHintAsync();
        }
    }

    /// <summary>测速引导:预填 badapple9/speedtest-x 并打开部署对话框,附端口映射说明。</summary>
    private async Task ShowSpeedtestDeployHintAsync()
    {
        await Task.Delay(200); // 等页面布局完成再弹,避免对话框取不到 XamlRoot

        _settingSuggest = true;
        CdImageBox.Text = "badapple9/speedtest-x";
        _settingSuggest = false;
        CdNameBox.Text = "speedtest-x";
        CdPortsBox.Text = "9000:80"; // 宿主 9000 → 容器 80,被占用时改宿主端口即可
        CdRestartBox.SelectedIndex = 0;
        CdPullBox.IsChecked = true;
        _suggestLocalTar = string.Empty;

        var locals = DockerService.ListLocalImageFiles();
        SuggestList.ItemsSource = locals;
        SuggestPanel.Visibility = locals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowCdError(
            locals.Count > 0
                ? "测速需要 badapple9/speedtest-x 容器。已预填镜像与容器名,勾选「部署前先拉取镜像」可在线拉取;" +
                  "也可点上方「建议镜像」用随包 tar 离线导入(不走拉取)。端口映射形如 宿主端口:80," +
                  "9000 被占用就换一个,部署完回网络诊断页测速。"
                : "测速需要 badapple9/speedtest-x 容器。已预填镜像与容器名,保持勾选「部署前先拉取镜像」即可在线拉取" +
                  "(约 460 MB,需 NAS 能联网);本机 img\\ 目录下没有离线镜像 tar 时只能在线拉取。" +
                  "端口映射形如 宿主端口:80,9000 被占用就换一个,部署完回网络诊断页测速。",
            InfoBarSeverity.Informational);

        _ = CreateDialog.ShowAsync();
    }

    private NasDevice? Current => DeviceBox.SelectedItem as NasDevice;

    private void ReloadDevices()
    {
        _devices.Clear();
        _devices.AddRange(NasDeviceStore.Load());

        DeviceBox.ItemsSource = _devices;

        // 默认选中「设备管理」里勾选的当前设备;没勾选过时回退到第一台
        var current = NasDeviceStore.GetCurrentOrDefault(_devices);
        if (current is not null) DeviceBox.SelectedItem = current;
        else if (_devices.Count > 0) DeviceBox.SelectedIndex = 0;

        var empty = _devices.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContainerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            EmptyText.Text = "还没有 NAS 设备";
            StatusText.Text = "";
            return;
        }

        _ = LoadCurrentSection();
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        // 在这里改选也同步为「当前管理设备」,设备管理页与其他功能页会跟着变
        if (Current is { } device && !device.IsCurrent)
            NasDeviceStore.SetCurrent(_devices, device);

        _ = LoadCurrentSection();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadCurrentSection();

    private async Task LoadAsync()
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在读取 {device.DisplayName} 上的容器…";
        try
        {
            var list = await DockerService.ListAsync(device);
            ContainerList.ItemsSource = list;

            var running = list.Count(c => c.Running);
            EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContainerList.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (list.Count == 0) EmptyText.Text = "该设备上没有容器";

            StatusText.Text = $"共 {list.Count} 个容器,其中 {running} 个运行中。";
        }
        catch (SshConnectException ex)
        {
            ContainerList.ItemsSource = null;
            StatusText.Text = $"连接失败:{ex.FriendlyMessage}\r\n{SshService.HintFor(ex.Failure)}";
        }
        catch (Exception ex)
        {
            ContainerList.ItemsSource = null;
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 容器操作 ----------

    private static DockerContainer? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DockerContainer;

    private void StartBtn_Click(object sender, RoutedEventArgs e) =>
        _ = ActAsync(sender, DockerAction.Start);

    private void StopBtn_Click(object sender, RoutedEventArgs e) =>
        _ = ActAsync(sender, DockerAction.Stop);

    private void RestartBtn_Click(object sender, RoutedEventArgs e) =>
        _ = ActAsync(sender, DockerAction.Restart);

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || Current is not { } device) return;

        var dlg = new ContentDialog
        {
            Title = "删除容器",
            Content = $"确定强制删除容器「{row.Name}」?容器内未持久化的数据会一并丢失。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        await ActAsync(sender, DockerAction.Remove);
    }

    private async void LogsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        try
        {
            var text = await DockerService.LogsAsync(device, row.Name);
            LogText.Text = text.Length > 0 ? text : "(无日志输出)";
            _ = LogDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 部署新容器 ----------

    private void DeployBtn_Click(object sender, RoutedEventArgs e)
    {
        CdImageBox.Text = string.Empty;
        CdNameBox.Text = string.Empty;
        CdPortsBox.Text = string.Empty;
        CdRestartBox.SelectedIndex = 0;
        CdPullBox.IsChecked = true;
        CdError.IsOpen = false;

        // 建议镜像:应用目录 img\ 下的 tar
        _suggestLocalTar = string.Empty;
        var locals = DockerService.ListLocalImageFiles();
        SuggestList.ItemsSource = locals;
        SuggestPanel.Visibility = locals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = CreateDialog.ShowAsync();
    }

    // 选中的建议镜像 tar;手动编辑镜像名时清空
    private string _suggestLocalTar = string.Empty;
    private bool _settingSuggest;

    private void SuggestChip_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LocalImageFile f) return;
        _suggestLocalTar = f.FullPath;
        _settingSuggest = true;
        CdImageBox.Text = System.IO.Path.GetFileNameWithoutExtension(f.Name);
        _settingSuggest = false;
    }

    private void CdImageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingSuggest) _suggestLocalTar = string.Empty;
    }

    private void CreateDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var image = CdImageBox.Text.Trim();
        if (image.Length == 0)
        {
            args.Cancel = true;
            ShowCdError("请填写镜像名,例:nginx:latest。");
            return;
        }
        if (image.Contains(' ') || image.Contains('\''))
        {
            args.Cancel = true;
            ShowCdError("镜像名不能包含空格或引号。");
            return;
        }

        var name = CdNameBox.Text.Trim();
        if (name.Contains(' ') || name.Contains('\''))
        {
            args.Cancel = true;
            ShowCdError("容器名不能包含空格或引号。");
            return;
        }

        // 端口映射:宿主:容器,支持逗号 / 分号 / 中文逗号分隔
        var ports = new List<(int Host, int Container)>();
        foreach (var part in CdPortsBox.Text.Split(',', ';', '，'))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            var seg = p.Split(':');
            if (seg.Length != 2 ||
                !int.TryParse(seg[0], out var host) || !int.TryParse(seg[1], out var container) ||
                host is < 1 or > 65535 || container is < 1 or > 65535)
            {
                args.Cancel = true;
                ShowCdError($"端口映射「{p}」无效,应形如 宿主:容器(1–65535),多项用逗号分隔。");
                return;
            }
            ports.Add((host, container));
        }

        var policy = CdRestartBox.SelectedIndex switch
        {
            1 => "always",
            2 => "no",
            _ => "unless-stopped",
        };

        // 部署(含拉取镜像)可能耗时数分钟,关掉对话框、在页面状态行跟进进度
        sender.Hide();
        _ = DeployAsync(image, name, ports, policy, CdPullBox.IsChecked == true, _suggestLocalTar);
    }

    private async Task DeployAsync(
        string image, string name, IReadOnlyList<(int Host, int Container)> ports, string policy, bool pull,
        string localTar = "")
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        try
        {
            var imageRef = image;

            // 选用建议镜像:先上传 tar 并 docker load,再用导入得到的实际镜像引用启动
            if (localTar.Length > 0)
            {
                var upload = new Progress<double>(p => StatusText.Text = $"正在上传镜像… {p:P0}");
                StatusText.Text = "正在上传建议镜像到 NAS…";
                var output = await DockerService.LoadLocalImageAsync(device, localTar, upload);
                var loaded = ParseLoadedRef(output);
                if (loaded.Length > 0) imageRef = loaded;
            }
            else
            {
                StatusText.Text = pull ? $"正在拉取镜像 {image}(首次可能需要几分钟)…" : $"正在创建容器 {image}…";
            }

            var id = await DockerService.CreateAsync(device, new DockerService.DockerCreateSpec
            {
                Image = imageRef,
                Name = name,
                RestartPolicy = policy,
                Ports = ports,
                Pull = localTar.Length == 0 && pull,
            });
            var label = name.Length > 0 ? name : imageRef;
            StatusText.Text = $"容器已创建并启动:{label}。";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "部署失败:" + ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>从 docker load 输出解析实际镜像引用(“Loaded image: repo:tag” / “Loaded image ID: sha256:…”)。</summary>
    private static string ParseLoadedRef(string loadOutput)
    {
        foreach (var line in (loadOutput ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("Loaded image", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = t.IndexOf(':');
            if (idx < 0) continue;
            var r = t[(idx + 1)..].Trim();
            if (r.Length > 0) return r;
        }
        return "";
    }

    /// <summary>在部署对话框里显示一条提示;校验失败用默认 Error,纯说明用 Informational。</summary>
    private void ShowCdError(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        CdError.Message = message;
        CdError.Severity = severity;
        CdError.IsOpen = true;
    }

    // ---------- 二级分区:概览 / Compose / 镜像 / 网络 ----------

    private string _section = "containers";

    private void SectionNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _section = (SectionNav.SelectedItem as FrameworkElement)?.Tag as string ?? "containers";
        OverviewPanel.Visibility = _section == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ContainerPanel.Visibility = _section == "containers" ? Visibility.Visible : Visibility.Collapsed;
        ComposePanel.Visibility = _section == "compose" ? Visibility.Visible : Visibility.Collapsed;
        ImagesPanel.Visibility = _section == "images" ? Visibility.Visible : Visibility.Collapsed;
        NetworksPanel.Visibility = _section == "networks" ? Visibility.Visible : Visibility.Collapsed;

        if (IsLoaded) _ = LoadCurrentSection();
    }

    /// <summary>按当前分区加载对应数据。</summary>
    private Task LoadCurrentSection() => _section switch
    {
        "overview" => LoadOverviewAsync(),
        "compose" => LoadComposeAsync(),
        "images" => LoadImagesAsync(),
        "networks" => LoadNetworksAsync(),
        _ => LoadAsync(),
    };

    private async Task LoadOverviewAsync()
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取 Docker 概览…";
        try
        {
            var info = await DockerService.InfoAsync(device);
            OvVersion.Text = info.Version;
            OvContainers.Text = info.Containers.ToString();
            OvRunning.Text = info.Running.ToString();
            OvStopped.Text = info.Stopped.ToString();
            OvImages.Text = info.Images.ToString();
            OvNetworks.Text = info.Networks.ToString();
            StatusText.Text = $"Docker {info.Version} · 容器 {info.Containers}(运行 {info.Running} / 停止 {info.Stopped})。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadComposeAsync()
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取 Compose 项目…";
        try
        {
            var list = await DockerService.ListComposeAsync(device);
            ComposeList.ItemsSource = list;
            ComposeEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"共 {list.Count} 个 Compose 项目。";
        }
        catch (Exception ex)
        {
            ComposeList.ItemsSource = null;
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ComposeRefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadComposeAsync();

    private static DockerComposeProject? ComposeRowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DockerComposeProject;

    private void ComposeStartBtn_Click(object sender, RoutedEventArgs e) => _ = ComposeActAsync(sender, "start");
    private void ComposeStopBtn_Click(object sender, RoutedEventArgs e) => _ = ComposeActAsync(sender, "stop");
    private void ComposeRestartBtn_Click(object sender, RoutedEventArgs e) => _ = ComposeActAsync(sender, "restart");

    private async void ComposeDownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ComposeRowOf(sender) is not { } row || Current is not { } device) return;

        var dlg = new ContentDialog
        {
            Title = "下线 Compose 项目",
            Content = $"确定下线「{row.Name}」?将停止并移除其容器与关联网络(数据卷保留)。",
            PrimaryButtonText = "下线",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        await ComposeActAsync(sender, "down");
    }

    private async Task ComposeActAsync(object sender, string action)
    {
        if (ComposeRowOf(sender) is not { } row || Current is not { } device) return;

        var verb = action switch { "start" => "启动", "stop" => "停止", "restart" => "重启", _ => "下线" };
        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在{verb} Compose 项目「{row.Name}」…";
        try
        {
            await DockerService.ComposeActionAsync(device, row.Name, action);
            StatusText.Text = $"已{verb}项目「{row.Name}」。";
            await LoadComposeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadImagesAsync()
    {
        LoadLocalImages(); // 本机 img\ 部分与设备无关,先展示
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取本地镜像…";
        try
        {
            var list = await DockerService.ListImagesAsync(device);
            ImagesList.ItemsSource = list;
            ImagesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var dangling = list.Count(i => i.Dangling);
            StatusText.Text = $"共 {list.Count} 个镜像{(dangling > 0 ? $",其中 {dangling} 个悬空" : "")}。";
        }
        catch (Exception ex)
        {
            ImagesList.ItemsSource = null;
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ImagesRefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadImagesAsync();

    // ---------- 本机镜像(img\ 下的 tar)----------

    /// <summary>列出应用目录 img\ 下的镜像 tar。</summary>
    private void LoadLocalImages()
    {
        Directory.CreateDirectory(DockerService.LocalImageDir);
        LocalImgDirHint.Text =
            $"镜像 tar 文件放在本机:{DockerService.LocalImageDir}(可手动复制或用 docker save 生成;「导入到 NAS」= 上传后 docker load)";
        var list = DockerService.ListLocalImageFiles();
        LocalImagesList.ItemsSource = list;
        LocalImagesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LocalImagesRefreshBtn_Click(object sender, RoutedEventArgs e) => LoadLocalImages();

    private static LocalImageFile? LocalRowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as LocalImageFile;

    private async void LoadLocalImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LocalRowOf(sender) is not { } row || Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p => StatusText.Text = $"正在上传 {row.Name} 到 NAS… {p:P0}");
        try
        {
            var output = await DockerService.LoadLocalImageAsync(device, row.FullPath, progress);
            StatusText.Text = $"已导入 {row.Name}:{output}";
            await LoadImagesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败:" + ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void DeleteLocalImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LocalRowOf(sender) is not { } row) return;

        var dlg = new ContentDialog
        {
            Title = "删除本机镜像文件",
            Content = $"确定删除本机文件「{row.Name}」({row.SizeText})?",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            File.Delete(row.FullPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "删除失败:" + ex.Message;
        }
        LoadLocalImages();
    }

    private static DockerImage? ImageRowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DockerImage;

    private async void RemoveImageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ImageRowOf(sender) is not { } row || Current is not { } device) return;

        var dlg = new ContentDialog
        {
            Title = "删除镜像",
            Content = $"确定删除镜像「{row.DisplayTag}」?正被容器使用的镜像会被 docker 拒绝。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在删除镜像 {row.DisplayTag}…";
        try
        {
            await DockerService.RemoveImageAsync(device, row.Id);
            StatusText.Text = $"已删除镜像 {row.DisplayTag}。";
            await LoadImagesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void PruneImagesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } device) return;

        var dlg = new ContentDialog
        {
            Title = "清理悬空镜像",
            Content = "将执行 docker image prune -f,删除所有无标签的悬空镜像。继续?",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在清理悬空镜像…";
        try
        {
            var output = await DockerService.PruneImagesAsync(device);
            StatusText.Text = $"清理完成:{output}";
            await LoadImagesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadNetworksAsync()
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取 Docker 网络…";
        try
        {
            var list = await DockerService.ListNetworksAsync(device);
            NetworksList.ItemsSource = list;
            NetworksEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"共 {list.Count} 个网络(bridge / host / none 为内置,不可删除)。";
        }
        catch (Exception ex)
        {
            NetworksList.ItemsSource = null;
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private void NetworksRefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadNetworksAsync();

    private static DockerNetwork? NetworkRowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DockerNetwork;

    private async void RemoveNetworkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkRowOf(sender) is not { } row || Current is not { } device) return;

        var dlg = new ContentDialog
        {
            Title = "删除网络",
            Content = $"确定删除自定义网络「{row.Name}」({row.Driver})?仍有容器接入时会被拒绝。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在删除网络 {row.Name}…";
        try
        {
            await DockerService.RemoveNetworkAsync(device, row.Name);
            StatusText.Text = $"已删除网络 {row.Name}。";
            await LoadNetworksAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 部署 Compose ----------

    private string _composeLocalFile = string.Empty;

    private void ComposeDeployBtn_Click(object sender, RoutedEventArgs e)
    {
        CpRemoteBox.Text = string.Empty;
        CpLocalBox.Text = string.Empty;
        _composeLocalFile = string.Empty;
        CpError.IsOpen = false;
        _ = ComposeDeployDialog.ShowAsync();
    }

    private void CpPickBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择 compose 文件",
            Filter = "Compose 文件|*.yml;*.yaml|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _composeLocalFile = dlg.FileName;
            CpLocalBox.Text = dlg.FileName;
        }
    }

    private void ComposeDeployDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var remote = CpRemoteBox.Text.Trim();
        if (remote.Length == 0 && _composeLocalFile.Length == 0)
        {
            args.Cancel = true;
            CpError.Message = "请填写 NAS 上的 compose 文件路径,或选择本机文件。";
            CpError.IsOpen = true;
            return;
        }

        var localFile = _composeLocalFile.Length > 0 ? _composeLocalFile : null;
        sender.Hide();
        _ = DeployComposeAsync(localFile ?? remote, localFile);
    }

    private async Task DeployComposeAsync(string remoteOrLocal, string? localFile)
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = localFile is not null
            ? $"正在上传并部署 {localFile}(up -d,可能需要几分钟)…"
            : $"正在部署 {remoteOrLocal}(up -d,可能需要几分钟)…";
        try
        {
            var file = await DockerService.ComposeUpAsync(device, remoteOrLocal, localFile);
            StatusText.Text = $"Compose 部署完成:{file}。";
            await LoadComposeAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose 部署失败:" + ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ActAsync(object sender, DockerAction action)
    {
        if (RowOf(sender) is not { } row || Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在{VerbOf(action)}容器「{row.Name}」…";
        try
        {
            await DockerService.ActAsync(device, row.Name, action);
            StatusText.Text = $"已{VerbOf(action)}容器「{row.Name}」。";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private static string VerbOf(DockerAction a) => a switch
    {
        DockerAction.Start => "启动",
        DockerAction.Stop => "停止",
        DockerAction.Restart => "重启",
        DockerAction.Remove => "删除",
        _ => "操作",
    };
}
