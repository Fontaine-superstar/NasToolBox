using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NasToolbox.Models;

/// <summary>Docker 容器一行信息:docker ps 的输出解析结果。</summary>
public sealed class DockerContainer
{
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x7C, 0x10));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromArgb(255, 0x9E, 0x9E, 0x9E));

    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Status { get; set; } = "";
    public string Ports { get; set; } = "";

    /// <summary>状态以 Up 开头即为运行中。</summary>
    public bool Running => Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);

    public bool CanStart => !Running;
    public bool CanStop => Running;

    public string StateText => Running ? "运行中" : "已停止";

    /// <summary>状态圆点颜色:运行中为绿,停止为灰。</summary>
    public Brush StateBrush => Running ? GreenBrush : GrayBrush;
}
