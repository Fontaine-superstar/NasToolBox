using System.Text.Json;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// 工具目录:扫描 Tools/ 中文分类文件夹,合并 Metadata/tools.json 元数据。
/// 对应 TubaWinUi3 的 ToolCatalog + ToolMetadataService(简化版)。
/// </summary>
public static class ToolCatalog
{
    private static readonly string[] ToolExts = { ".exe", ".bat", ".cmd", ".lnk", ".msc", ".ps1", ".vbs" };

    private static List<ToolItem>? _tools;

    public static IReadOnlyList<ToolItem> Tools => _tools ??= Scan();

    public static string ToolsRoot { get; } = FindToolsRoot();

    /// <summary>从运行目录向上寻找 Tools/;找不到就假定在输出目录下(兼容发布后的布局)。</summary>
    private static string FindToolsRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (d.Name.Equals("Tools", StringComparison.OrdinalIgnoreCase)) return d.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "Tools");
    }

    public static void ScanIfNeeded() => _ = Tools;

    public static void Rescan() => _tools = null;

    private static List<ToolItem> Scan()
    {
        var meta = LoadMetadata();
        var list = new List<ToolItem>();
        if (!Directory.Exists(ToolsRoot)) return list;

        foreach (var catDir in Directory.GetDirectories(ToolsRoot))
        {
            var category = Path.GetFileName(catDir);
            foreach (var file in Directory.EnumerateFiles(catDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!ToolExts.Contains(ext)) continue;

                var item = new ToolItem(file, Path.GetFileNameWithoutExtension(file), category);

                // tools.json 的 match 字段:与完整路径做大小写不敏感的"包含"匹配(与 TubaWinUi3 同规则)
                var m = meta.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.Match) &&
                    file.Contains(x.Match, StringComparison.OrdinalIgnoreCase));
                if (m is not null)
                {
                    if (!string.IsNullOrEmpty(m.Name)) item.Name = m.Name!;
                    item.Description = m.Description;
                }

                item.IconPath = ToolIconService.GetIconPng(file);
                if (item.IconPath is not null)
                {
                    item.Icon = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(item.IconPath));
                }

                list.Add(item);
            }
        }
        return list;
    }

    private static List<ToolMeta> LoadMetadata()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Metadata", "tools.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<ToolMeta>>(File.ReadAllText(path)) ?? new List<ToolMeta>();
            }
        }
        catch
        {
            // 元数据损坏不致命,忽略
        }
        return new List<ToolMeta>();
    }

    private sealed record ToolMeta(string? Match, string? Name, string? Description);
}
