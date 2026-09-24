using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace NasToolbox.Services;

/// <summary>
/// "高版一体化标题栏":
///   1. Window.ExtendsContentIntoTitleBar = true          → XAML 内容延伸进系统标题栏(消灭上下两条栏)
///   2. AppWindow.TitleBar.PreferredHeightOption = Tall    → 系统标题栏升高为 48px,与 TitleBar 控件对齐
///   3. Window.SetTitleBar(TitleBar 控件)                  → 指定拖拽区(控件内交互元素仍可点击)
///   4. 系统 -□× 按钮背景透明、前景按明暗主题着色
/// 注意:部分旧版 Win10 上 AppWindow.TitleBar 可能为 null,任何失败都静默退回默认样式而不是闪退。
/// </summary>
public static class SafeTitleBar
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    public static void ApplyExtendedTall(Window window, FrameworkElement titleBar)
    {
        try
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);

            var caption = window.AppWindow?.TitleBar;
            if (caption is null) return;

            try
            {
                caption.PreferredHeightOption = TitleBarHeightOption.Tall;
            }
            catch
            {
                // 不支持 Tall(老系统)则保持标准高度
            }

            caption.ButtonBackgroundColor = Transparent;
            caption.ButtonInactiveBackgroundColor = Transparent;

            ApplyTheme(titleBar, caption);
            titleBar.ActualThemeChanged += (_, _) => ApplyTheme(titleBar, caption);
        }
        catch
        {
            // 拿不到 AppWindow.TitleBar 等场景:退回系统默认按钮样式
        }
    }

    private static void ApplyTheme(FrameworkElement source, AppWindowTitleBar caption)
    {
        try
        {
            var dark = source.ActualTheme == ElementTheme.Dark;
            var fg = dark ? White : Black;
            caption.ButtonForegroundColor = fg;
            caption.ButtonHoverForegroundColor = fg;
            caption.ButtonPressedForegroundColor = fg;
            caption.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            caption.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x11, 0x00, 0x00, 0x00);
            caption.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
        }
        catch
        {
            // 主题应用失败不影响运行
        }
    }
}

