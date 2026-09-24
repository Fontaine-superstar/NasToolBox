using System.Runtime.InteropServices;
using Microsoft.UI;
using NasToolbox.Services;
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
    private const int WidthPx = 540;
    private const int HeightPx = 320;

    // DWMWINDOWATTRIBUTE(dwmapi.h):2 = 非客户区渲染策略,33 = 窗口圆角策略,34 = 边框颜色
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    // DWMNCRENDERING_POLICY:2 = 禁用非客户区渲染(彻底的"无框",DWM 不再画那圈 1px 描边)
    private const int DwmncrpDisabled = 2;

    // DWM_WINDOW_CORNER_PREFERENCE:1 = 不圆角(圆角处会露出窗口底色,视觉上就是"白角")
    private const int DwmwcpDoNotRound = 1;

    // DWMWA_BORDER_COLOR 的特殊值:0xFFFFFFFE = DWMWA_COLOR_NONE,完全不绘制边框
    private const int DwmBorderColorNone = unchecked((int)0xFFFFFFFE);

    // Win32 窗口样式(user32.h)
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;   // = WS_BORDER | WS_DLGFRAME
    private const int WsThickFrame = 0x00040000; // 可调整大小的边框
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    // SetWindowPos 标志
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    // 窗口类背景画刷(把客户区未被 XAML 覆盖的那 1px 也刷成内容色)
    private const int GclpHbrBackground = -10;

    // 窗口过程子类化:GWLP_WNDPROC = -4;WM_NCCALCSIZE = 0x0083
    private const int GwlpWndProc = -4;
    private const uint WmNcCalcSize = 0x0083;

    // WndProc 回调委托必须保存在字段里,防止被 GC 回收后窗口过程变成野指针
    private WndProcDelegate? _procDelegate;
    private IntPtr _procDelegatePtr;
    private IntPtr _originalProc;
    private bool _procInstalled;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private Color _background;

    private IntPtr _classBrush;
    private IntPtr _oldClassBrush;
    private bool _classBrushSet;

    public SplashWindow()
    {
        InitializeComponent();

        try
        {
            VerText.Text = VersionText();
        }
        catch
        {
            VerText.Text = string.Empty;
        }

        ApplyWindowStyle();

        // 首次激活时窗口已完全创建,再剥一次边框:
        // WinUI 在 Show/Activate 之后可能重新应用窗口样式,只做一次会被覆盖回来
        Activated += (_, _) => StripFrame();

        // 窗口类背景画刷是同类窗口共享的,关掉时还原
        Closed += (_, _) => RestoreClassBrush();
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
        _background = ResolveBackground();
        try
        {
            Root.Background = new SolidColorBrush(_background);
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

            StripFrame();

            aw.ResizeClient(new SizeInt32(WidthPx, HeightPx));
            Center(aw);
        }
        catch
        {
            // 样式设置失败:窗口照常显示,只是带默认边框
        }
    }

    /// <summary>
    /// 彻底消除无边框窗口四周的白色描边。
    /// 关键点:Windows 11 上即使 SetBorderAndTitleBar(false, false),DWM 仍会画一圈 1px 浅色框架;
    /// 而 DWMWA_BORDER_COLOR 只有在系统设置里开启了"标题栏和窗口边框显示强调色"时才生效,
    /// 默认关闭 —— 所以只刷边框色是没用的。这里从 Win32 层下手:
    /// ① 摘掉 WS_CAPTION / WS_THICKFRAME 等框架样式;② 禁用 DWM 非客户区渲染;③ 去圆角 + 边框色兜底。
    /// 全部静默降级,失败只是"仍有边框",不影响启动。
    /// </summary>
    private void StripFrame()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

        // ① 摘掉框架样式(WS_CAPTION 含 WS_BORDER 与 WS_DLGFRAME)
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var stripped = style & ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox);
        if (stripped != style)
        {
            SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)stripped);
            // 必须带 SWP_FRAMECHANGED,否则样式变更不会立即重算非客户区
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        // ①½ 子类化窗口过程,拦截 WM_NCCALCSIZE:wParam=TRUE 时返回 0,让客户区吃掉整个窗口矩形。
        //    Win11 对剥掉 WS_CAPTION 的窗口仍会按默认 NCCALCSIZE 在窗口顶部保留 2~3px 标题栏色带
        //    (实测 RGB 239,241,247),DWMWA_BORDER_COLOR / COLOR_NONE / NCRENDERING_POLICY /
        //    SetWindowRgn 对它统统无效 —— 只有这一招是根治(WinUIEx 同款做法)。
        if (!_procInstalled)
        {
            _procDelegate = WndProc;
            _procDelegatePtr = Marshal.GetFunctionPointerForDelegate(_procDelegate);
            _originalProc = SetWindowLongPtr(hwnd, GwlpWndProc, _procDelegatePtr);
            if (_originalProc != IntPtr.Zero) _procInstalled = true;
        }

            // ② 禁用非客户区渲染 —— 这才是四边白边的根治办法
            var disabled = DwmncrpDisabled;
            DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref disabled, sizeof(int));

            // ③ 去圆角(圆角外会露出浅色底)。
            // ④ 边框:DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE(0xFFFFFFFE) 让 DWM 完全不画边框。
            //    Win11 上剥掉 WS_CAPTION 后 DWM 仍会在窗口顶部画 2px 浅色线(实测 RGB 238,240,246),
            //    刷成内容色不生效,只有 NONE 才能彻底去掉 —— 这是无边框窗口白条的真正根源。
            var corner = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            var none = DwmBorderColorNone;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));

            // ④ 窗口底色:WinUI 的 XAML 岛常常没有铺满客户区最外一圈(尤其顶部 1px),
            //    那里露出的是窗口类背景(默认白色)。把它也刷成内容色,视觉上就消失了。
            if (!_classBrushSet)
            {
                var brush = CreateSolidBrush(ColorRefOf(_background));
                if (brush != IntPtr.Zero)
                {
                    _oldClassBrush = GetClassLongPtr(hwnd, GclpHbrBackground);
                    SetClassLongPtr(hwnd, GclpHbrBackground, brush);
                    InvalidateRect(hwnd, IntPtr.Zero, true);
                    _classBrush = brush;
                    _classBrushSet = true;
                }
            }
        }
        catch
        {
            // 拿不到句柄或系统不支持:保留系统默认外观
        }
    }

    /// <summary>窗口关闭时还原窗口类背景画刷并释放 GDI 对象,避免影响同类的其他窗口。</summary>
    private void RestoreClassBrush()
    {
        if (!_classBrushSet) return;
        _classBrushSet = false;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                SetClassLongPtr(hwnd, GclpHbrBackground, _oldClassBrush);
                InvalidateRect(hwnd, IntPtr.Zero, true);
            }
        }
        catch
        {
            // 还原失败:只影响后续窗口的底色,不影响功能
        }

        if (_classBrush != IntPtr.Zero)
        {
            DeleteObject(_classBrush);
            _classBrush = IntPtr.Zero;
        }
    }

    /// <summary>窗口过程:仅拦截 WM_NCCALCSIZE 去掉非客户区保留带,其余全部转回原窗口过程。</summary>
    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero) return IntPtr.Zero;
        return CallWindowProc(_originalProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>转成 COLORREF(0x00BBGGRR,注意字节序与 RGB 相反)。</summary>
    private static int ColorRefOf(Color c) => c.B << 16 | c.G << 8 | c.R;

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

    /// <summary>取界面显示用的版本串(如 beta1.0.0)。</summary>
    private static string VersionText() => AppVersion.Display;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, ref RECT rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colorref);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
