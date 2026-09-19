using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NasToolbox.Models;

/// <summary>
/// 设备行视图模型:NasDevice + 实时连通状态。
/// 状态更新会触发 PropertyChanged,供 ListView 的 x:Bind 刷新。
/// </summary>
public sealed class DeviceRow : INotifyPropertyChanged
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromArgb(255, 0x9E, 0x9E, 0x9E));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x7C, 0x10));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));

    public NasDevice Device { get; }

    public DeviceRow(NasDevice device) => Device = device;

    private string _statusText = "未检测";
    private Brush _statusBrush = GrayBrush;

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; Raise(nameof(StatusText)); } }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set { if (!ReferenceEquals(_statusBrush, value)) { _statusBrush = value; Raise(nameof(StatusBrush)); } }
    }

    public void SetChecking()
    {
        StatusText = CheckingText;
        StatusBrush = GrayBrush;
    }

    /// <summary>「检测中…」状态文字;公开为常量供检测轮被取消时识别并恢复。</summary>
    public const string CheckingText = "检测中…";

    /// <summary>回到「未检测」初始态(检测轮被取消且无历史记录可回填时兜底)。</summary>
    public void Reset()
    {
        StatusText = "未检测";
        StatusBrush = GrayBrush;
    }

    public void SetOnline(string? detail)
    {
        StatusText = string.IsNullOrEmpty(detail) ? "在线" : $"在线 · {detail}";
        StatusBrush = GreenBrush;
    }

    public void SetOffline(string? detail)
    {
        StatusText = string.IsNullOrEmpty(detail) ? "离线" : $"离线 · {detail}";
        StatusBrush = RedBrush;
    }

    /// <summary>是否为「当前管理的设备」(供列表里的复选框 OneWay 绑定)。</summary>
    public bool IsCurrent => Device.IsCurrent;

    /// <summary>设备上的 IsCurrent 已在外部改动后调用,通知界面刷新复选框。</summary>
    public void NotifyCurrent() => Raise(nameof(IsCurrent));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}