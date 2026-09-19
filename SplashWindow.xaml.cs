using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace NasToolbox;

/// <summary>
/// 启动画面窗口:无边框、不可调整、屏幕居中,由 <see cref="Services.StartupService"/> 上报进度。
/// 非打包(Unpackaged)应用没有系统 SplashScreen,所以自建一个并全程静默降级 ——
/// 任何窗口样式设置失败都只是"没那么好看",绝不能挡住启动。
/// </summary>
public sealed partial class SplashWindow : Window
{
    private const int WidthPx = 560;
    private const int HeightPx = 340;

    public SplashWindow()
    {
        InitializeComponent();

        try
        {
            VerText.Text = "v" + VersionText();
        }
        catch
        {
            VerText.Text = string.Empty;
        }

        ApplyWindowStyle();
    }

    /// <summary>更新进度(必须在 UI 线程调用;StartupService 在 await 后回到 UI 线程)。</summary>
    public void Report(int percent, string text)
    {
        Bar.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = text;
    }

    private void ApplyWindowStyle()
    {
        try
        {
            var aw = AppWindow;
            if (aw is null) return;

            // 无边框 + 无标题栏:看起来才像启动画面而不是一个多余的窗口
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            aw.SetPresenter(presenter);

            aw.ResizeClient(new SizeInt32(WidthPx, HeightPx));
            Center(aw);
        }
        catch
        {
            // 样式设置失败:窗口照常显示,只是带默认边框
        }
    }

    private static void Center(AppWindow aw)
    {
        try
        {
            var area = DisplayArea.Primary.WorkArea;
            var x = area.X + (area.Width - aw.Size.Width) / 2;
            var y = area.Y + (area.Height - aw.Size.Height) / 2;
            aw.Move(new PointInt32(Math.Max(x, area.X), Math.Max(y, area.Y)));
        }
        catch
        {
            // 拿不到显示器工作区:保持系统默认位置
        }
    }

    /// <summary>取程序集版本的主版本.次版本.修订号(如 0.2.0)。</summary>
    private static string VersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "0.2.0";
        return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }
}
