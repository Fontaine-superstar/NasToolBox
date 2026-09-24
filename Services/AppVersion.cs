using System.Reflection;

namespace NasToolbox.Services;

/// <summary>
/// 应用版本号的唯一来源:读程序集的 InformationalVersion(由 csproj 的 InformationalVersion 指定,如 beta1.0.0)。
/// 启动画面与「关于」页都从这里取值,避免各处硬编码版本号。
/// </summary>
public static class AppVersion
{
    /// <summary>用于界面显示的版本串,例:beta1.0.0。</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion 可能被源码控制信息追加成 "x.y.z+<sha>",只取加号前面的部分
        if (!string.IsNullOrWhiteSpace(info))
        {
            var text = info.Split('+')[0].Trim();
            if (text.Length > 0) return text;
        }

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "未知";
        return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }
}
