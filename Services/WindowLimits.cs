using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NasToolbox.Services;

/// <summary>
/// 窗口尺寸限制。Windows App SDK 的 AppWindow 没有原生 MinSize,
/// 通过监听 <see cref="AppWindow.Changed"/> 把小于下限的尺寸顶回 <see cref="AppWindow.Resize"/>,
/// 实现最小窗口限制;下限按当前显示器 DPI(RasterizationScale)换算为物理像素。
/// 与 SafeTitleBar 同策略:任何失败静默降级,不影响启动。
/// </summary>
public static class WindowLimits
{
    /// <summary>
    /// 设置默认打开尺寸(物理像素,无视 DPI 缩放,即 ResizeClient 原始值)。
    /// 构造函数阶段 AppWindow 已可用,直接执行。
    /// </summary>
    public static void ApplyDefaultSize(Window window, int width, int height)
    {
        try
        {
            window.AppWindow?.ResizeClient(new SizeInt32(width, height));
        }
        catch
        {
            // 拿不到 AppWindow:保持系统默认尺寸,不影响启动
        }
    }

    /// <summary>限制窗口最小尺寸(逻辑像素 width×height,如 850×700)。</summary>
    public static void ApplyMinSize(Window window, int width, int height)
    {
        try
        {
            var appWindow = window.AppWindow;
            if (appWindow is null) return;

            // 启动时已小于下限则先顶一次(XamlRoot 未就绪时按 100% 缩放兜底)
            Clamp(appWindow, MinOf(window, width, height));

            // 之后每次尺寸变化(拖拽 / 最大化还原 / 代码改尺寸)都钳制在下限之上;
            // Resize 触发的再次 Changed 因已达标自然返回,不会无限循环
            appWindow.Changed += (aw, e) =>
            {
                if (e.DidSizeChange) Clamp(aw, MinOf(window, width, height));
            };
        }
        catch
        {
            // 拿不到 AppWindow 等场景:退回无限制,不影响运行
        }
    }

    /// <summary>按窗口当前 DPI 缩放把逻辑下限换算成物理像素。</summary>
    private static SizeInt32 MinOf(Window window, int width, int height)
    {
        var scale = window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        return new SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
    }

    private static void Clamp(AppWindow aw, SizeInt32 min)
    {
        var s = aw.Size;
        if (s.Width >= min.Width && s.Height >= min.Height) return;
        aw.Resize(new SizeInt32(Math.Max(s.Width, min.Width), Math.Max(s.Height, min.Height)));
    }
}