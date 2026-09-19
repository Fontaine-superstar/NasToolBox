using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Pages;
using NasToolbox.Services;

namespace NasToolbox;

public sealed partial class MainWindow : Window
{
    private const string DevPrefix = "设备 · ";

    /// <summary>主窗口单例:供页面同步侧栏选中状态(如跳转到 Docker 管理)。</summary>
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Title = "NAS 工具箱";

        // 关键:内容延伸进系统标题栏,顶栏才是"一体"的(否则会出现系统栏+自绘栏两条)
        SafeTitleBar.ApplyExtendedTall(this, AppTitleBar);

        // 默认打开 1390×1000(物理像素,无视 DPI 缩放)
        WindowLimits.ApplyDefaultSize(this, 1390, 1000);

        // 最小窗口 850×700:AppWindow 无原生 MinSize,经 Changed 事件把小于下限的尺寸顶回
        WindowLimits.ApplyMinSize(this, 850, 700);

        // 启动即后台检测一遍全部设备并落盘,之后打开设备页 / 首页就能立刻看到状态
        _ = DeviceProbeService.ProbeAndPersistAsync();

        // 启动即后台预取当前设备的 NAS 状态:点开「NAS 状态」页时先用缓存上屏,无需等待
        NasStatusService.PrefetchOnStartup();
    }

    /// <summary>按侧栏 Tag 切换页面(供页面内跳转,如测速页跳到 Docker 管理),同时保持侧栏高亮同步。</summary>
    public void NavigateByTag(string tag)
    {
        var item = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null) NavView.SelectedItem = item;
    }

    /// <summary>重复点击当前页(如「设备管理」)时也刷新一次在线状态。</summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (tag == "devices" && NavFrame.Content is DevicesPage page) page.RefreshFromNav();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch
        {
            "home" => typeof(DashboardPage),
            "devices" => typeof(DevicesPage),
            "status" => typeof(NasStatusPage),
            "web" => typeof(WebAdminPage),
            "shell" => typeof(ShellPage),
            "network" => typeof(NetworkToolsPage),
            "files" => typeof(FileManagerPage),
            "docker" => typeof(DockerPage),
            "about" => typeof(AboutPage),
            _ => null
        };
        if (page is not null && NavFrame.Content?.GetType() != page)
        {
            NavFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo);
        }
    }

    /// <summary>顶栏搜索:按名称 / 地址 / 主机名 / 备注匹配 NAS 设备。</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = sender.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q)) { sender.ItemsSource = null; return; }

        var devices = NasDeviceStore.Load()
            .Where(d => d.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || d.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || d.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || d.Note.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(d => DevPrefix + d.DisplayName)
            .Take(8);

        sender.ItemsSource = devices.ToList();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = (args.ChosenSuggestion as string) ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(text)) return;

        var query = text.StartsWith(DevPrefix, StringComparison.Ordinal) ? text[DevPrefix.Length..] : text;

        NavView.SelectedItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .First(i => (string?)i.Tag == "devices");
        NavFrame.Navigate(typeof(DevicesPage), query);
    }
}