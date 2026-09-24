using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Services;

namespace NasToolbox.Pages;

public sealed partial class AboutPage : Page
{
    private const string RepoUrl = "https://github.com/Fontaine-superstar/NasToolBox";
    private const string XiaoheiheUrl = "https://xiaoheihe.cn/app/user/profile/71347149";

    public AboutPage()
    {
        InitializeComponent();

        // 版本号统一取自 InformationalVersion(csproj 里的 InformationalVersion),不再硬编码
        VersionRun.Text = $"NAS 工具箱 {AppVersion.Display}";
    }

    /// <summary>用系统默认浏览器打开项目主页。</summary>
    private async void OpenRepoButton_Click(object sender, RoutedEventArgs e)
        => _ = await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl));

    /// <summary>弹出与首次启动相同的开源声明(不写「已读」标记,不影响首启逻辑)。</summary>
    private void ShowNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        // Window.Current 在页面事件处理器里可能为 null(非窗口线程上下文),
        // 用 App.MainWin / MainWindow.Instance 拿真实主窗口,Content.XamlRoot 此时必然就绪。
        var win = App.MainWin ?? (Window?)MainWindow.Instance;
        if (win is null) return;
        OpenSourceNotice.ShowDialog(win);
    }

    /// <summary>用系统默认浏览器打开作者小黑盒主页。</summary>
    private async void OpenXiaoheiheButton_Click(object sender, RoutedEventArgs e)
        => _ = await Windows.System.Launcher.LaunchUriAsync(new Uri(XiaoheiheUrl));

    /// <summary>弹出赞赏码(与首启声明里的赞赏区同内容,独立小窗)。</summary>
    private void ShowDonateButton_Click(object sender, RoutedEventArgs e)
    {
        var win = App.MainWin ?? (Window?)MainWindow.Instance;
        if (win is null) return;
        OpenSourceNotice.ShowDonateDialog(win);
    }
}
