using Microsoft.UI.Xaml;
using NasToolbox.Services;

namespace NasToolbox;

public partial class App : Application
{
    public static Window? MainWin { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 启动:先显示启动画面(SplashWindow),在此期间构建主窗口并执行启动任务,
    /// 完成后再激活主窗口并关闭启动画面 —— 这样打开程序的第一眼就是进度条而不是空白窗口。
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var splash = new SplashWindow();
        splash.Activate();

        MainWin = new MainWindow();

        try
        {
            await StartupService.RunAsync(splash.Report);
        }
        catch
        {
            // 启动任务整体异常也不影响进主界面
        }

        MainWin.Activate();

        // 首次启动弹开源声明(GPL-3.0 + 数据存放说明),同意后落标记,之后不再弹
        OpenSourceNotice.ShowIfFirstLaunch(MainWin);

        try
        {
            splash.Close();
        }
        catch
        {
            // 关闭失败:残留一个空窗口不影响使用
        }
    }
}
