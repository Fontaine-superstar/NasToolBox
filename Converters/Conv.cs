using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NasToolbox.Converters;

/// <summary>供 XAML x:Bind 函数绑定使用的静态转换(比 IValueConverter 更轻)。</summary>
public static class Conv
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x7C, 0x10));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));

    /// <summary>字符串为空 → 显示(用于"没有图标时显示 FontIcon 占位")。</summary>
    public static Visibility WhenEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>字符串非空 → 显示(用于"有备注才显示备注")。</summary>
    public static Visibility WhenNotEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>字节 → 人类可读(KB/MB/GB/TB)。</summary>
    public static string FmtBytes(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.#} TB"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : $"{bytes / (double)(1L << 10):0.#} KB";

    /// <summary>盘符根路径("C:\") → 盘符字母("C")。</summary>
    public static string DriveLetter(string root) =>
        string.IsNullOrEmpty(root) ? "?" : root[..1];

    public static string OpenText(bool open) => open ? "开放" : "关闭";

    public static Brush OpenBrush(bool open) => open ? OkBrush : BadBrush;

    /// <summary>true → 显示,false → 折叠(用于按状态显隐按钮)。</summary>
    public static Visibility WhenTrue(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
}