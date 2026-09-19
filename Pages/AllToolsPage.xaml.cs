using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NasToolbox.Models;
using NasToolbox.Services;

namespace NasToolbox.Pages;

public sealed partial class AllToolsPage : Page
{
    private string? _filter;

    public AllToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _filter = e.Parameter as string;
        // 页面设置了 NavigationCacheMode=Enabled,Loaded 只触发一次,这里手动刷新
        if (IsLoaded) Reload();
    }

    private void Reload()
    {
        ToolCatalog.ScanIfNeeded();
        var query = _filter?.Trim();
        IEnumerable<ToolItem> items = ToolCatalog.Tools;

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(t =>
                t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (t.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = items.OrderBy(t => t.Category, StringComparer.CurrentCulture)
                        .ThenBy(t => t.Name, StringComparer.CurrentCulture)
                        .ToList();

        HeaderText.Text = string.IsNullOrEmpty(query)
            ? $"全部工具({list.Count})"
            : $"搜索“{query}”({list.Count})";
        ToolsGrid.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _filter = null; // 只对一次导航生效
    }

    private void ToolsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolItem tool)
        {
            try
            {
                ToolLauncher.Launch(tool);
            }
            catch (Exception ex)
            {
                _ = new ContentDialog
                {
                    Title = "启动失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                }.ShowAsync();
            }
        }
    }
}