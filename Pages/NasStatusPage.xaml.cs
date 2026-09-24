using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NasToolbox.Models;
using NasToolbox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NasToolbox.Pages;

/// <summary>
/// NAS 状态:对选中设备做在线探测,并经 SSH 采集系统信息
/// (系统 / 主机名 / 在线时长 / 负载 / CPU 核心 / 温度 / 内存 / 磁盘空间)。
/// 指标随设备的权限模式自动提权(root 会话);采集命令均为只读,无需特殊权限。
/// </summary>
public sealed partial class NasStatusPage : Page
{
    private readonly List<NasDevice> _devices = new();
    private bool _suppressBox;

    // 长列表(网络 / 磁盘)默认收起为预览,只显示前 3 行,展开后显示全部
    private const int PreviewRows = 3;
    private List<NicInfo>? _nicAll;
    private List<MountUsage>? _mountAll;
    private List<DiskInfo>? _hwAll;
    private bool _netCollapsed = true;
    private bool _diskCollapsed = true;
    private bool _hwCollapsed = true;

    // 在线时长持续走秒:记住采集时刻的秒数与本地时间,之后每秒按时钟差值推进
    private DispatcherTimer? _uptimeTimer;
    private long _uptimeBaseSec;
    private DateTime _uptimeAt;

    public NasStatusPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadDevices();
        Unloaded += (_, _) => StopUptimeTicker();
    }

    private NasDevice? Current => DeviceBox.SelectedItem as NasDevice;

    private void ReloadDevices()
    {
        _devices.Clear();
        _devices.AddRange(NasDeviceStore.Load());

        // 初始化下拉框期间屏蔽 SelectionChanged,避免与下面的显式加载重复触发
        _suppressBox = true;
        try
        {
            DeviceBox.ItemsSource = _devices;

            // 默认选中「设备管理」里勾选的当前设备;没勾选过时回退到第一台
            var current = NasDeviceStore.GetCurrentOrDefault(_devices);
            if (current is not null) DeviceBox.SelectedItem = current;
            else if (_devices.Count > 0) DeviceBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressBox = false;
        }

        var empty = _devices.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            StatusText.Text = "";
            return;
        }

        _ = LoadAsync();
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressBox) return;

        // 在这里改选也同步为「当前管理设备」,与其他功能页保持一致
        if (Current is { } device && !device.IsCurrent)
            NasDeviceStore.SetCurrent(_devices, device);

        _ = LoadAsync();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>网络卡片:标题或「展开全部 / 收起」按钮点击,在 3 行预览与完整列表间切换。</summary>
    private void NetToggle_Click(object sender, RoutedEventArgs e)
    {
        _netCollapsed = !_netCollapsed;
        ApplyNicPreview();
    }

    /// <summary>磁盘卡片:同上,在 3 行预览与完整列表间切换。</summary>
    private void DiskToggle_Click(object sender, RoutedEventArgs e)
    {
        _diskCollapsed = !_diskCollapsed;
        ApplyDiskPreview();
    }

    /// <summary>硬盘卡片:同上,在 3 行预览与完整列表间切换。</summary>
    private void HwToggle_Click(object sender, RoutedEventArgs e)
    {
        _hwCollapsed = !_hwCollapsed;
        ApplyHwPreview();
    }

    /// <summary>按收起状态把网卡列表裁成前 3 行,并刷新底部「展开全部 / 收起」按钮与标题箭头。</summary>
    private void ApplyNicPreview()
    {
        if (_nicAll is null) return;
        var collapsed = _netCollapsed;
        NicList.ItemsSource = collapsed ? _nicAll.Take(PreviewRows).ToList() : _nicAll;

        if (_nicAll.Count > PreviewRows)
        {
            NetMoreBtn.Visibility = Visibility.Visible;
            NetMoreBtn.Content = collapsed ? $"展开全部 {_nicAll.Count} 个网卡 ▾" : "收起 ▴";
        }
        else NetMoreBtn.Visibility = Visibility.Collapsed;

        if (NetChevron is not null)
            NetChevron.RenderTransform = new RotateTransform { Angle = collapsed ? 180 : 0 };
    }

    /// <summary>磁盘挂载点同理。</summary>
    private void ApplyDiskPreview()
    {
        if (_mountAll is null) return;
        var collapsed = _diskCollapsed;
        MountList.ItemsSource = collapsed ? _mountAll.Take(PreviewRows).ToList() : _mountAll;

        if (_mountAll.Count > PreviewRows)
        {
            DiskMoreBtn.Visibility = Visibility.Visible;
            DiskMoreBtn.Content = collapsed ? $"展开全部 {_mountAll.Count} 个挂载点 ▾" : "收起 ▴";
        }
        else DiskMoreBtn.Visibility = Visibility.Collapsed;

        if (DiskChevron is not null)
            DiskChevron.RenderTransform = new RotateTransform { Angle = collapsed ? 180 : 0 };
    }

    /// <summary>物理硬盘列表同理。</summary>
    private void ApplyHwPreview()
    {
        if (_hwAll is null) return;
        var collapsed = _hwCollapsed;
        HwList.ItemsSource = collapsed ? _hwAll.Take(PreviewRows).ToList() : _hwAll;

        if (_hwAll.Count > PreviewRows)
        {
            HwMoreBtn.Visibility = Visibility.Visible;
            HwMoreBtn.Content = collapsed ? $"展开全部 {_hwAll.Count} 块硬盘 ▾" : "收起 ▴";
        }
        else HwMoreBtn.Visibility = Visibility.Collapsed;

        if (HwChevron is not null)
            HwChevron.RenderTransform = new RotateTransform { Angle = collapsed ? 180 : 0 };
    }

    private async Task LoadAsync()
    {
        if (Current is not { } dev) return;

        // 有启动预取的缓存先立即上屏,点进页面不用干等;随后后台静默刷新
        var cached = NasStatusService.Cached;
        var hasCache = cached is not null && cached.DeviceKey == NasStatusService.Key(dev);
        if (hasCache) RenderSnapshot(cached!);
        else ClearPanels();

        BusyRing.Visibility = hasCache ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = hasCache
            ? $"已显示 {cached!.FetchedAt:HH:mm:ss} 的数据,正在后台刷新…"
            : $"正在检测 {dev.DisplayName}…";

        try
        {
            var snap = await NasStatusService.FetchAsync(dev);
            RenderSnapshot(snap);

            StatusText.Text = snap.Error is not null
                ? (snap.Online ? $"在线({snap.OnlineDetail})" : "离线") +
                  $" · 读取系统信息失败:{snap.Error}"
                : (snap.Online ? "在线" : "离线") + $"({snap.OnlineDetail}) · 更新于 {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>把快照渲染到各卡片;在线状态总是刷新,指标仅在有数据时覆盖。</summary>
    private void RenderSnapshot(NasStatusSnapshot s)
    {
        OnlineText.Text = s.Online ? $"在线 · {s.OnlineDetail}" : "离线";
        OnlineText.Foreground = (Brush)Application.Current.Resources[
            s.Online ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];

        if (s.Error is not null) return; // 指标缺失时保留当前显示,失败原因走状态行

        OsText.Text = s.OsSummary;
        HostnameText.Text = s.Hostname;
        LoadText.Text = s.Load.Length > 0 ? s.Load : "—";

        // 走秒基准锚定在采集时刻,即使显示的是缓存也连续不减
        if (s.UptimeSec > 0) StartUptimeTicker(s.UptimeSec, s.FetchedAt);
        else
        {
            StopUptimeTicker();
            UptimeText.Text = "—";
        }

        // CPU 行:格式「{型号} | {封装温度}」;封装温度优先取 CPU 专属传感器,无则回退 thermal_zone0
        var cpuTempC = s.Temps.FirstOrDefault(t => IsCpuTempName(t.Name))?.Celsius ?? 0;
        if (cpuTempC <= 0 && s.TempMilli > 0) cpuTempC = (int)Math.Round(s.TempMilli / 1000.0);
        var model = s.CpuModel.Length > 0 ? s.CpuModel : "—";
        CpuModelText.Text = cpuTempC > 0 ? $"{model} | {cpuTempC} °C" : model;

        // GPU 行:无 GPU / 未装 lspci 时显示「—」
        GpuText.Text = s.Gpu.Length > 0 ? s.Gpu : "—";

        if (s.MemTotalKb > 0)
        {
            var usedKb = Math.Max(0, s.MemTotalKb - s.MemAvailKb);
            var pct = (int)Math.Round(usedKb * 100.0 / s.MemTotalKb);
            MemBar.Value = pct;
            // /proc/meminfo 单位是 kB:kB → GB 需除以 1024×1024
            MemText.Text = $"已用 {usedKb / 1048576.0:F1} GB / 共 {s.MemTotalKb / 1048576.0:F1} GB({pct}%)";
        }
        else MemText.Text = "—";

        // 系统卡片的「内存」行展示内存条型号(DDR 类型 / 频率 / 容量);
        // 需要 root 且装有 dmidecode,虚拟机或权限不足时拿不到,显示「—」
        SysMemText.Text = s.MemoryModel.Length > 0 ? s.MemoryModel : "—";

        DiskHint.Visibility = s.Mounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HwHint.Visibility = s.Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 网络卡片 ----
        LanIpText.Text = s.LanIps.Length > 0 ? s.LanIps : "—";
        WanIpText.Text = s.PublicIp.Length > 0 ? s.PublicIp : "—";
        GatewayText.Text = s.Gateway;
        DnsText.Text = s.Dns;
        _nicAll = s.Nics;
        ApplyNicPreview();
        _mountAll = s.Mounts;
        ApplyDiskPreview();
        _hwAll = s.Disks;
        ApplyHwPreview();
    }

    // ---------- 硬盘 SMART 详情 ----------

    /// <summary>SMART 弹窗是否正在打开;ContentDialog 同一时刻只能 Show 一次,用它挡住重复点击。</summary>
    private bool _smartBusy;
    private string _smartRaw = "";

    /// <summary>点击一块硬盘:拉取该盘的完整 SMART 信息并在弹窗里展示。</summary>
    private async void HwList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DiskInfo disk) return;
        if (Current is not { } dev) return;

        if (_smartBusy) return; // 弹窗已打开
        _smartBusy = true;
        try
        {
            SmartDialog.Title = $"{disk.Name} · SMART 详情";
            SmartPanel.Children.Clear();
            _smartRaw = "";
            SmartLoading.Visibility = Visibility.Visible;
            SmartScroll.Visibility = Visibility.Collapsed;
            SmartDialog.IsPrimaryButtonEnabled = false;

            SmartDialog.XamlRoot = XamlRoot;
            _ = SmartDialog.ShowAsync(); // 先弹窗再取数据,网络往返期间用户看到加载态

            var report = await SmartService.FetchAsync(dev, disk.Name, disk.Model).ConfigureAwait(true);
            RenderSmart(report);
        }
        finally
        {
            _smartBusy = false;
        }
    }

    /// <summary>把 SMART 报告渲染成「概览 + 基本信息 + 属性表 + 原始输出」四段。</summary>
    private void RenderSmart(SmartReport r)
    {
        SmartPanel.Children.Clear();
        SmartLoading.Visibility = Visibility.Collapsed;
        SmartScroll.Visibility = Visibility.Visible;
        _smartRaw = r.Raw;

        if (r.Error is not null)
        {
            SmartPanel.Children.Add(new TextBlock
            {
                Text = r.Error,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = BrushOf("SystemFillColorCriticalBrush"),
            });
            return;
        }

        SmartDialog.IsPrimaryButtonEnabled = r.Raw.Length > 0;

        // ---- 概览:健康自评 / 温度 / 通电时间 ----
        var health = r.Info.FirstOrDefault(i => i.Key == "健康自评").Value ?? "";
        var hours = PowerOnHours(r);
        var overview = new List<(string Label, string Value, Brush? Brush)>();
        if (health.Length > 0) overview.Add(("健康自评", health, HealthBrush(health)));
        if (r.Temperature.Length > 0) overview.Add(("温度", r.Temperature, null));
        if (hours.Length > 0) overview.Add(("通电时间", hours, null));

        if (overview.Count > 0)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            for (var i = 0; i < overview.Count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var i = 0; i < overview.Count; i++)
            {
                var cell = StatBlock(overview[i].Label, overview[i].Value, overview[i].Brush);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            SmartPanel.Children.Add(grid);
        }

        // ---- 厂商 / 工具告警(如「预计 24 小时内失效,请立刻备份」) ----
        if (r.Warnings.Count > 0)
        {
            var warnings = new StackPanel { Spacing = 4 };
            foreach (var w in r.Warnings)
                warnings.Children.Add(new TextBlock
                {
                    Text = (w.Critical ? "⚠ " : "· ") + w.Text,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = w.Critical ? Microsoft.UI.Text.FontWeights.SemiBold
                                           : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = BrushOf(w.Critical ? "SystemFillColorCriticalBrush"
                                                    : "SystemFillColorCautionBrush"),
                });
            SmartPanel.Children.Add(warnings);
        }

        // ---- 基本信息 ----
        if (r.Info.Count > 0)
        {
            SmartPanel.Children.Add(SectionLabel("基本信息"));
            var box = new StackPanel { Spacing = 6 };
            // 健康自评已显示在顶部概览里,这里不再重复一行
            foreach (var (key, value) in r.Info.Where(i => i.Key != "健康自评"))
                box.Children.Add(KeyValueRow(key, value));
            SmartPanel.Children.Add(box);
        }

        // ---- 属性表(ATA 盘) ----
        if (r.Attributes.Count > 0)
        {
            SmartPanel.Children.Add(SectionLabel($"SMART 属性({r.Attributes.Count} 项)"));
            SmartPanel.Children.Add(AttributeTable(r.Attributes));
        }
        else if (r.Raw.Length > 0)
        {
            SmartPanel.Children.Add(SectionLabel("SMART 数据"));
            SmartPanel.Children.Add(new TextBlock
            {
                Text = "该设备不提供标准属性表(多为 NVMe / SAS 盘),请展开下方原始输出查看。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ---- 原始输出(默认折叠) ----
        if (r.Raw.Length > 0)
        {
            var rawBlock = new TextBlock
            {
                Text = r.Raw,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
            };
            // 原始输出自身可双向滚动:外层已禁用横向滚动,否则超宽行会被直接裁掉
            var rawScroll = new ScrollViewer
            {
                Content = rawBlock,
                MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Visibility = Visibility.Collapsed,
            };
            var toggle = new Button
            {
                Content = "原始输出 ▾",
                Style = (Style)Resources["TextButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            toggle.Click += (_, _) =>
            {
                var show = rawScroll.Visibility == Visibility.Collapsed;
                rawScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? "原始输出 ▴" : "原始输出 ▾";
            };
            SmartPanel.Children.Add(toggle);
            SmartPanel.Children.Add(rawScroll);
        }
    }

    /// <summary>「复制原始输出」:把 smartctl 的原文放进剪贴板,并保持弹窗不关闭。</summary>
    private void SmartDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_smartRaw.Length == 0) return;
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(_smartRaw);
            Clipboard.SetContent(pkg);
            Clipboard.Flush();
        }
        catch
        {
            // 剪贴板不可用(远程会话 / 权限)时忽略:用户仍可在弹窗里选中查看
        }
        args.Cancel = true; // 保持弹窗打开
    }

    /// <summary>通电时间:NVMe 在基本信息里直接给出,ATA 盘要从 Power_On_Hours 属性的原始值换算。</summary>
    private static string PowerOnHours(SmartReport r)
    {
        var direct = r.Info.FirstOrDefault(i => i.Key == "通电时间").Value;
        if (direct is { Length: > 0 }) return direct;

        var attr = r.Attributes.FirstOrDefault(a =>
            a.Name.Equals("Power_On_Hours", StringComparison.OrdinalIgnoreCase));
        if (attr is null) return "";
        var m = System.Text.RegularExpressions.Regex.Match(attr.Raw, @"\d+");
        return m.Success ? $"{long.Parse(m.Value):N0} 小时" : "";
    }

    private static Brush BrushOf(string resource) => (Brush)Application.Current.Resources[resource];

    /// <summary>健康自评文本 → 颜色:PASSED/OK 绿,FAILED 红,其余橙。</summary>
    private static Brush? HealthBrush(string text) =>
        text.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            ? BrushOf("SystemFillColorCriticalBrush")
            : (text.Contains("PASSED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OK", StringComparison.Ordinal))
                ? BrushOf("SystemFillColorSuccessBrush")
                : BrushOf("SystemFillColorCautionBrush");

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Opacity = 0.9,
        Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>概览里的一格「标签在上、数值在下」。</summary>
    private static StackPanel StatBlock(string label, string value, Brush? brush) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = label, FontSize = 11, Opacity = 0.6 },
            new TextBlock
            {
                Text = value,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = brush ?? BrushOf("TextFillColorPrimaryBrush"),
            },
        },
    };

    private static Grid KeyValueRow(string key, string value)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = key, FontSize = 12, Opacity = 0.6 });
        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    /// <summary>
    /// ATA 属性表:ID / 属性名 / 当前 / 最差 / 阈值 / 原始值。
    /// 当前值 ≤ 阈值用橙色标出,WHEN_FAILED 为 FAILING_NOW 用红色加粗。
    /// </summary>
    private static Grid AttributeTable(List<SmartAttribute> attrs)
    {
        var widths = new[] { 40d, 200d, 46d, 46d, 46d };
        var headers = new[] { "ID#", "属性名", "当前", "最差", "阈值", "原始值" };

        var g = new Grid { RowSpacing = 3, ColumnSpacing = 8 };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Cell(string text, int row, int col, Brush? brush = null, bool bold = false, double opacity = 1)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (brush is not null) tb.Foreground = brush;
            if (bold) tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        for (var row = 0; row <= attrs.Count; row++)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < headers.Length; c++) Cell(headers[c], 0, c, opacity: 0.55, bold: true);

        for (var i = 0; i < attrs.Count; i++)
        {
            var a = attrs[i];
            var row = i + 1;
            var brush = a.IsFailed ? BrushOf("SystemFillColorCriticalBrush")
                : a.IsWarning ? BrushOf("SystemFillColorCautionBrush") : null;

            var all = new[] { a.Id, a.Name, a.Value, a.Worst, a.Thresh, a.Raw };
            for (var c = 0; c < all.Length; c++)
                Cell(all[c], row, c, brush, a.IsFailed);
        }
        return g;
    }

    /// <summary>判定是否 CPU 封装/核心温度传感器:Intel coretemp 的 Package、AMD k10temp 的 Tctl/Tdie、热区的 cpu/x86_pkg 命名。</summary>
    private static bool IsCpuTempName(string name) =>
        name.Contains("package", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("tctl", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("tdie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cpu", StringComparison.OrdinalIgnoreCase);

    private void ClearPanels()
    {
        StopUptimeTicker();
        OsText.Text = HostnameText.Text = OnlineText.Text =
            UptimeText.Text = LoadText.Text = "…";
        CpuModelText.Text = "…";
        GpuText.Text = "…";
        SysMemText.Text = "…";
        MemText.Text = "…";
        MemBar.Value = 0;
        _nicAll = null;
        _mountAll = null;
        _hwAll = null;
        _netCollapsed = true;
        _diskCollapsed = true;
        _hwCollapsed = true;
        MountList.ItemsSource = null;
        HwList.ItemsSource = null;
        DiskHint.Visibility = Visibility.Collapsed;
        HwHint.Visibility = Visibility.Collapsed;
        GatewayText.Text = DnsText.Text = "…";
        LanIpText.Text = WanIpText.Text = "…";
        NicList.ItemsSource = null;
        NetMoreBtn.Visibility = Visibility.Collapsed;
        DiskMoreBtn.Visibility = Visibility.Collapsed;
        HwMoreBtn.Visibility = Visibility.Collapsed;
        if (NetChevron is not null) NetChevron.RenderTransform = new RotateTransform { Angle = 180 };
        if (DiskChevron is not null) DiskChevron.RenderTransform = new RotateTransform { Angle = 180 };
        if (HwChevron is not null) HwChevron.RenderTransform = new RotateTransform { Angle = 180 };
    }

    // ---------- 在线时长走秒 ----------

    /// <summary>以快照的开机秒数与采集时刻为基准,启动每秒一次的走秒显示。</summary>
    private void StartUptimeTicker(long baseSec, DateTime baseAt)
    {
        _uptimeBaseSec = baseSec;
        _uptimeAt = baseAt;

        if (_uptimeTimer is null)
        {
            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptimeTimer.Tick += UptimeTimer_Tick;
        }

        _uptimeTimer.Start();
        RenderUptime();
    }

    private void StopUptimeTicker() => _uptimeTimer?.Stop();

    private void UptimeTimer_Tick(object? sender, object e) => RenderUptime();

    private void RenderUptime()
    {
        var sec = _uptimeBaseSec + (long)(DateTime.Now - _uptimeAt).TotalSeconds;
        UptimeText.Text = $"{sec / 86400}天 {sec % 86400 / 3600:00}:{sec % 3600 / 60:00}:{sec % 60:00}";
    }

}