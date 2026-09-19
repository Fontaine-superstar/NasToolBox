using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

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

    // DWMWINDOWATTRIBUTE(dwmapi.h):33 = 窗口圆角策略,34 = 边框颜色
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    // DWM_WINDOW_CORNER_PREFERENCE:1 = 不圆角(圆角处会露出窗口底色,视觉上就是"白角")
    private const int DwmwcpDoNotRound = 1;

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
        // 背景色先落地:后面 DWM 边框要用同一个颜色,否则无边框窗口会留一圈系统默认的浅色描边
        var background = ResolveBackground();
        try
        {
            Root.Background = new SolidColorBrush(background);
        }
        catch
        {
            // 保留 XAML 里的 ThemeResource 背景
        }

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

            PaintBorderLikeContent(aw, background);
        }
        catch
        {
            // 样式设置失败:窗口照常显示,只是带默认边框
        }
    }

    /// <summary>
    /// 消除无边框窗口的白色描边:Windows 11 上即使 SetBorderAndTitleBar(false, false),
    /// 系统仍会画一圈 1px 边框且默认为浅色;同时圆角外会露出窗口底色。
    /// 这里把边框色刷成与内容一致的颜色,并关闭圆角(Win10 不支持时静默忽略)。
    /// </summary>
    private void PaintBorderLikeContent(AppWindow aw, Color background)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            // 关圆角,避免四角露出浅色底
            var corner = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            // 边框色 = 内容背景色(COLORREF 为 0x00BBGGRR)
            var colorref = background.B << 16 | background.G << 8 | background.R;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorref, sizeof(int));
        }
        catch
        {
            // 不支持(如 Win10)或拿不到句柄:保留系统默认边框色
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

    /// <summary>
    /// 取窗口内容实际使用的背景色,供 DWM 边框保持同色。
    /// 优先取主题里的 ApplicationPageBackgroundThemeBrush,拿不到就按明暗主题兜底。
    /// </summary>
    private Color ResolveBackground()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("ApplicationPageBackgroundThemeBrush", out var value)
                && value is SolidColorBrush brush)
            {
                return brush.Color;
            }
        }
        catch
        {
            // 资源不可用:走下方兜底
        }

        var dark = App.Current.RequestedTheme == ApplicationTheme.Dark;
        return dark
            ? Color.FromArgb(255, 0x20, 0x20, 0x20)
            : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
    }

    /// <summary>取程序集版本的主版本.次版本.修订号(如 0.2.0)。</summary>
    private static string VersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "0.2.0";
        return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
