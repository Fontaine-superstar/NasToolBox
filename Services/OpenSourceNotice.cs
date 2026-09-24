using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NasToolbox.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace NasToolbox.Services;

/// <summary>
/// 开源声明弹窗:GPL-3.0 义务提示 + 数据存放说明 + 赞赏码。
/// 首次启动自动弹出(读「已读标记文件」,点过「我已阅读并同意」就不再弹);
/// 「关于」页也可手动再次打开(ShowDialog,不写标记、无「退出程序」按钮)。
/// </summary>
public static class OpenSourceNotice
{
    private static string MarkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NasToolbox", "oss_notice_ack.json");

    /// <summary>主窗口激活后调用:首次启动时弹开源声明,同意后写标记。</summary>
    public static async void ShowIfFirstLaunch(Window owner)
    {
        try
        {
            if (File.Exists(MarkPath)) return;

            var xamlRoot = owner.Content?.XamlRoot;
            if (xamlRoot is null) return;

            var result = await ShowDialogCore(xamlRoot, firstLaunch: true);

            if (result == ContentDialogResult.Primary)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkPath)!);
                File.WriteAllText(MarkPath,
                    $"{{\"ack\":true,\"version\":\"{AppVersion.Display}\",\"time\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"}}");
            }
            else
            {
                owner.Close();
            }
        }
        catch
        {
            // 弹窗失败不阻塞主流程
        }
    }

    /// <summary>「关于」页手动打开:同款界面,同意只关闭弹窗,不写标记、不退出程序。</summary>
    public static async void ShowDialog(Window owner)
    {
        try
        {
            var xamlRoot = owner.Content?.XamlRoot;
            if (xamlRoot is null) return;
            await ShowDialogCore(xamlRoot, firstLaunch: false);
        }
        catch
        {
            // 弹窗失败不影响页面
        }
    }

    /// <summary>构建并弹出声明对话框;firstLaunch 决定按钮文案与「退出程序」行为。</summary>
    private static async Task<ContentDialogResult> ShowDialogCore(XamlRoot xamlRoot, bool firstLaunch)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "NAS 工具箱基于 GPL-3.0 许可证开源。",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            FontSize = 13,
            Text = "· 本程序源代码可自由使用、修改和分发;衍生作品必须以相同协议(GPL-3.0)开源\n" +
                   "· 界面基于原生 WinUI 3 构建\n" +
                   "· 设备列表与凭据仅保存在本机 %LocalAppData%\\NasToolbox\\,不会上传到任何服务器\n" +
                   "· 完整许可证文本见程序目录下的 LICENSE 文件",
        });

        // 赞赏码:直接平铺展示
        var donateGroup = new StackPanel { Spacing = 6 };
        donateGroup.Children.Add(new TextBlock
        {
            Text = "赞赏开发者",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        donateGroup.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/Images/DonateQR.jpg")),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        donateGroup.Children.Add(new TextBlock
        {
            Text = "微信扫码,请作者喝杯咖啡",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(donateGroup);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "开源声明",
            Content = panel,
            DefaultButton = ContentDialogButton.Primary,
        };

        if (firstLaunch)
        {
            dialog.PrimaryButtonText = "我已阅读并同意";
            dialog.CloseButtonText = "退出程序";
        }
        else
        {
            dialog.PrimaryButtonText = "关闭";
        }

        return await dialog.ShowAsync();
    }
}
