using Microsoft.UI.Xaml.Controls;
using NasToolbox.Services;

namespace NasToolbox.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        // 版本号统一取自 InformationalVersion(csproj 里的 InformationalVersion),不再硬编码
        VersionRun.Text = $"NAS 工具箱 {AppVersion.Display}";
    }
}
