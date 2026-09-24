using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Services;

namespace NasToolbox.Pages;

public sealed partial class AboutPage : Page
{
    private const string RepoUrl = "https://github.com/Fontaine-superstar/NasToolBox";

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
        => OpenSourceNotice.ShowDialog(Window.Current);
}
