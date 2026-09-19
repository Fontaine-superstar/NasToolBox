using Microsoft.UI.Xaml.Media;

namespace NasToolbox.Models;

/// <summary>一个可启动的第三方工具(来自 Tools/ 目录扫描 + tools.json 元数据合并)。</summary>
public sealed class ToolItem
{
    public string ExePath { get; }
    public string Name { get; set; }
    public string Category { get; }
    public string? Description { get; set; }

    /// <summary>提取并缓存后的 PNG 图标路径(可能为 null,UI 需回退 FontIcon)。</summary>
    public string? IconPath { get; set; }

    /// <summary>由 IconPath 生成的 ImageSource(可能为 null)。</summary>
    public ImageSource? Icon { get; set; }

    public ToolItem(string exePath, string name, string category)
    {
        ExePath = exePath;
        Name = name;
        Category = category;
    }

    public override string ToString() => Name;
}
