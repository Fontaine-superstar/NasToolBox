using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NasToolbox.Models;
using NasToolbox.Services;
using NasToolbox.Services.Ssh;

namespace NasToolbox.Pages;

/// <summary>
/// NAS 设备台账:增删改查。设备行仅保留「编辑」「删除」两个操作。
/// 设备列表持久化在 %LocalAppData%\NasToolbox\devices.json。
/// </summary>
public sealed partial class DevicesPage : Page
{
    private readonly List<NasDevice> _devices = new();
    private List<DeviceRow> _rows = new();
    private DeviceRow? _editing;
    private string? _filter;

    public DevicesPage()
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
        _devices.Clear();
        _devices.AddRange(NasDeviceStore.Load());

        // 从未指定过「当前设备」时,默认选中第一台,保证功能页有确定的目标
        if (NasDeviceStore.GetCurrent(_devices) is null && _devices.Count > 0)
        {
            _devices[0].IsCurrent = true;
            NasDeviceStore.Save(_devices);
        }

        var query = _filter?.Trim();
        IEnumerable<NasDevice> shown = _devices;
        if (!string.IsNullOrEmpty(query))
        {
            shown = shown.Where(d =>
                d.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Hostname.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _rows = shown.Select(d => new DeviceRow(d)).ToList();
        DeviceList.ItemsSource = _rows;
        DeviceList.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = string.IsNullOrEmpty(query) ? "NAS 设备管理" : $"设备搜索「{query}」";
        _filter = null; // 只对一次导航生效
        UpdateCurrentHint();

        // 先把上次检测的结果显示出来,再后台跑一次实时检测
        foreach (var row in _rows) DeviceProbeService.ApplyCached(row);
        _ = AutoProbeAsync();
    }

    /// <summary>导航到本页 / 再次点击「设备管理」时调用:立即刷新一次在线状态。</summary>
    public void RefreshFromNav()
    {
        if (IsLoaded) Reload();
    }

    private async Task AutoProbeAsync()
    {
        BusyRing.Visibility = Visibility.Visible;
        try
        {
            // 自动检测不把行置为「检测中…」,保留上次状态,避免闪烁
            await DeviceProbeService.ProbeAllAsync(_rows);
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private static DeviceRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DeviceRow;

    // ---------- 当前管理设备 ----------

    /// <summary>勾选 / 取消勾选「当前管理」:同时只保留一台,立即落盘。</summary>
    private void CurrentBox_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var on = (sender as CheckBox)?.IsChecked == true;
        SetCurrent(on ? row : null);
    }

    private void SetCurrent(DeviceRow? target)
    {
        // 直接改设备对象并保存(含被搜索过滤掉的设备,避免它们残留旧标记)
        NasDeviceStore.SetCurrent(_devices, target?.Device);

        // 通知所有行刷新复选框:未选中的行会自动取消勾选
        foreach (var row in _rows) row.NotifyCurrent();
        UpdateCurrentHint();
    }

    private void UpdateCurrentHint()
    {
        var current = NasDeviceStore.GetCurrent(_devices);
        CurrentHint.Text = current is null
            ? "未指定当前设备,功能页将默认使用第一台"
            : $"当前管理:{current.DisplayName}";
    }

    private async void CheckAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        // 手动检测:先置「检测中…」给用户即时反馈,结果同样持久化
        await DeviceProbeService.ProbeAllAsync(_rows, markChecking: true);
    }

    /// <summary>清空所有缓存的 SSH 连接(改过凭据、或想强制重连时使用)。</summary>
    private async void DisconnectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        SshService.ForgetAll();
        await ShowDialog("已断开 SSH 连接",
            "已清空所有设备上缓存的 SSH 连接。\r\n下次执行命令时会重新握手并校验主机指纹。");
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var dlg = new ContentDialog
        {
            Title = "删除设备",
            Content = $"确定删除「{row.Device.DisplayName}」({row.Device.Host})?",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            SshService.Forget(row.Device);
            _devices.Remove(row.Device);
            NasDeviceStore.Save(_devices);
            Reload();
        }
    }

    // ---------- 编辑对话框 ----------

    private void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _editing = row;
        HostBox.Text = row.Device.Host;
        HostnameBox.Text = row.Device.Hostname;
        MacBox.Text = row.Device.Mac;
        WebPortBox.Value = row.Device.EffectiveWebPort;
        NoteBox.Text = row.Device.Note;

        SshPortBox.Text = row.Device.SshPort.ToString();
        SshUserBox.Text = row.Device.SshUser;
        SshAuthBox.SelectedIndex = row.Device.SshAuth == SshAuthKind.PrivateKey ? 1 : 0;
        SshKeyPathBox.Text = row.Device.SshKeyPath;
        // RootLogin → root 会话;UseSudo → sudo 提权;其余普通用户
        PrivilegeModeBox.SelectedIndex = row.Device.RootLogin ? 2 : row.Device.UseSudo ? 1 : 0;
        SshTimeoutBox.Value = row.Device.EffectiveTimeoutSec;

        // 口令类字段一律不回显,留空表示保持原值
        SshPassBox.Password = string.Empty;
        SshKeyPassBox.Password = string.Empty;
        SudoPassBox.Password = string.Empty;
        ClearPassBox.IsChecked = false;

        ClearPassBox.Visibility = Visibility.Visible;
        EditDialog.Title = $"编辑设备 — {row.Device.DisplayName}";
        TestResultBar.IsOpen = false;
        UpdateSshVisibility();
        ShowError(null);
        _ = EditDialog.ShowAsync();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        HostBox.Text = HostnameBox.Text = MacBox.Text = NoteBox.Text = string.Empty;
        WebPortBox.Value = 5000;

        SshPortBox.Text = "22";
        SshUserBox.Text = string.Empty;
        SshAuthBox.SelectedIndex = 0;
        SshKeyPathBox.Text = string.Empty;
        // 新增设备默认 root 会话:NAS 管理命令(smartctl / docker 等)大多需要提权
        PrivilegeModeBox.SelectedIndex = 2;
        SshTimeoutBox.Value = 20;

        SshPassBox.Password = string.Empty;
        SshKeyPassBox.Password = string.Empty;
        SudoPassBox.Password = string.Empty;
        ClearPassBox.IsChecked = false;

        // 新增设备没有「已保存的凭据」可清,这个选项没有意义
        ClearPassBox.Visibility = Visibility.Collapsed;
        EditDialog.Title = "添加 NAS 设备";
        TestResultBar.IsOpen = false;
        UpdateSshVisibility();
        ShowError(null);
        _ = EditDialog.ShowAsync();
    }

    private void SshAuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSshVisibility();
        TestResultBar.IsOpen = false;
    }

    private void PrivilegeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSshVisibility();

    private void SshUserBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSshVisibility();

    /// <summary>常见端口提示以提示框(Flyout)出现:聚焦「Web 管理端口」时在下方弹出,失焦自动收起。</summary>
    private void WebPortBox_GotFocus(object sender, RoutedEventArgs e) =>
        FlyoutBase.ShowAttachedFlyout(WebPortBox);

    private void WebPortBox_LostFocus(object sender, RoutedEventArgs e) =>
        FlyoutBase.GetAttachedFlyout(WebPortBox)?.Hide();

    /// <summary>按认证方式 / 权限模式刷新输入项显隐与提示文案。</summary>
    private void UpdateSshVisibility()
    {
        var isKey = SshAuthBox.SelectedIndex == 1;
        SshPassBox.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;

        // 权限模式:0 普通 / 1 sudo / 2 root;后两者才需要 sudo 密码
        var mode = PrivilegeModeBox.SelectedIndex;
        var needSudo = mode is 1 or 2;
        SudoPassBox.Visibility = needSudo ? Visibility.Visible : Visibility.Collapsed;

        // 告诉用户留空的真实后果:已保存 → 保持不变;未保存 → 免密 sudo
        var savedSudo = _editing is not null && _editing.Device.SudoPassEnc.Length > 0;
        SudoPassBox.Description = needSudo
            ? savedSudo
                ? "已保存 sudo 密码,留空保持不变"
                : "未保存;留空将在 NAS 上尝试免密 sudo(sudo -n)"
            : string.Empty;

        // 直接以 root 登录时提权没有意义,就地提醒避免误配
        RootUserHint.Visibility = SshUserBox.Text.Trim().Equals("root", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>显示表单级错误(传 null 清除)。</summary>
    private void ShowError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            DialogError.IsOpen = false;
            return;
        }

        DialogError.Message = message;
        DialogError.IsOpen = true;
    }

    // ---------- 测试连接 ----------

    /// <summary>
    /// 用当前表单内容直接连一次 SSH。分阶段诊断(DNS → TCP → 握手认证),
    /// 结果就地显示在 SSH 卡片里,不打断填写。
    /// </summary>
    private async void TestConnBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateForm(out var port, out var webPort, out var timeout) is { } error)
        {
            ShowError(error);
            return;
        }

        ShowError(null);
        TestConnBtn.IsEnabled = false;
        TestRing.Visibility = Visibility.Visible;
        TestResultBar.IsOpen = false;
        try
        {
            var device = BuildDeviceFromForm(port, webPort, timeout);
            var report = await SshService.TestAsync(device, trustNewHostKey: false);

            // 指纹未信任 / 已变更:把指纹和「信任并重试」按钮放进结果条,避免嵌套弹窗
            if (!report.Ok &&
                report.Failure is SshConnectFailure.HostKeyUntrusted or SshConnectFailure.HostKeyChanged)
            {
                ShowFingerprintPrompt(device, report);
                return;
            }

            ShowTestResult(report);
        }
        catch (Exception ex)
        {
            ShowTestResult(null, "测试出错:" + ex.Message, "");
        }
        finally
        {
            TestConnBtn.IsEnabled = true;
            TestRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>展示诊断结果:成功绿、失败红,附分阶段耗时与修复建议。</summary>
    private void ShowTestResult(SshConnectReport? report, string? fallbackTitle = null, string? fallbackText = null)
    {
        if (report is null)
        {
            TestResultBar.Severity = InfoBarSeverity.Error;
            TestResultBar.Title = fallbackTitle ?? "连接失败";
            TestResultBar.Message = fallbackText ?? "";
            TestResultBar.Content = null;
            TestResultBar.IsOpen = true;
            return;
        }

        TestResultBar.Severity = report.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        TestResultBar.Title = report.Ok ? "连接成功" : $"连接失败 · {report.FailureLabel}";
        TestResultBar.Message =
            $"{report.Summary}　TCP {report.ConnectElapsed.TotalMilliseconds:F0} ms · " +
            $"认证 {report.AuthElapsed.TotalMilliseconds:F0} ms";
        TestResultBar.Content = report.Hint.Length == 0
            ? null
            : new TextBlock { Text = report.Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        TestResultBar.IsOpen = true;
    }

    /// <summary>首次连接 / 指纹变更时展示指纹,并提供就地信任重连的按钮。</summary>
    private void ShowFingerprintPrompt(NasDevice device, SshConnectReport report)
    {
        var changed = report.Failure == SshConnectFailure.HostKeyChanged;
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = $"主机密钥:{report.HostKeyName}\r\n指纹:{report.Fingerprint}",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono,Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = changed
                ? "该设备的主机密钥与上次记录不一致。确认是同一台 NAS(未重装、网络无中间人)后再信任。"
                : "首次连接该设备,本机还没有记录它的主机指纹。可在 NAS 上执行 ssh-keygen -l -f /etc/ssh/ssh_host_ed25519_key.pub 比对。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var btn = new Button { Content = "信任此指纹并重连", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            TestRing.Visibility = Visibility.Visible;
            try
            {
                SshHostKeyStore.Trust(device.Host.Trim(), device.SshPort, report.Fingerprint);
                var again = await SshService.TestAsync(device, trustNewHostKey: true);
                ShowTestResult(again);
            }
            catch (Exception ex)
            {
                ShowTestResult(null, "重连失败", ex.Message);
            }
            finally
            {
                TestRing.Visibility = Visibility.Collapsed;
                btn.IsEnabled = true;
            }
        };
        panel.Children.Add(btn);

        TestResultBar.Severity = InfoBarSeverity.Warning;
        TestResultBar.Title = changed ? "主机指纹已变更" : "需确认主机指纹";
        TestResultBar.Message = report.Summary;
        TestResultBar.Content = panel;
        TestResultBar.IsOpen = true;
    }

    private void BrowseKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        // 借 Windows Forms 的文件对话框:WinUI 3 未打包形态下省去窗口句柄初始化
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择 SSH 私钥文件",
            Filter = "私钥文件|*.pem;*.key;id_rsa;id_ed25519;id_ecdsa|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SshKeyPathBox.Text = dlg.FileName;
    }

    /// <summary>
    /// 校验表单公共字段。返回 null 表示通过,否则返回面向用户的错误文案。
    /// 保存与「测试连接」共用,避免两处规则不一致。
    /// </summary>
    private string? ValidateForm(out int port, out int webPort, out int timeout)
    {
        port = 22;
        webPort = 5000;
        timeout = 20;

        var host = HostBox.Text.Trim();
        if (host.Length == 0) return "IP / 主机名为必填项。";

        var mac = MacBox.Text.Trim();
        if (mac.Length > 0 && !WakeOnLanService.TryParseMac(mac, out _))
            return "MAC 地址格式无效,应为 6 组十六进制字符(例 AA:BB:CC:DD:EE:FF),或留空。";

        var portText = SshPortBox.Text.Trim();
        if (portText.Length == 0) portText = "22";
        if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            return "SSH 端口必须是 1–65535 的整数(默认 22)。";

        // Web 管理端口必填:NumberBox 清空后 Value 为 NaN,越界在控件层已限制,这里兜底
        var webPortValue = WebPortBox.Value;
        if (double.IsNaN(webPortValue) || webPortValue != Math.Floor(webPortValue) || webPortValue is < 1 or > 65535)
            return "Web 管理端口为必填项,必须是 1–65535 的整数(群晖默认 5000)。";
        webPort = (int)webPortValue;

        // NumberBox 清空后 Value 为 NaN;小数 / 越界在控件层已限制,这里兜底
        var timeoutValue = SshTimeoutBox.Value;
        if (double.IsNaN(timeoutValue) || timeoutValue != Math.Floor(timeoutValue) || timeoutValue is < 1 or > 600)
            return "命令超时必须是 1–600 之间的整数秒(默认 20)。";
        timeout = (int)timeoutValue;

        var keyPath = SshKeyPathBox.Text.Trim();
        if (SshAuthBox.SelectedIndex == 1 && keyPath.Length > 0)
        {
            var expanded = Environment.ExpandEnvironmentVariables(keyPath);
            if (!File.Exists(expanded)) return $"私钥文件不存在:{expanded}";
        }

        return null;
    }

    /// <summary>
    /// 按表单当前内容构造一个设备对象(供「测试连接」使用,不写入台账)。
    /// 口令框留空表示保持原值,测试时沿用设备上已保存的凭据。
    /// </summary>
    private NasDevice BuildDeviceFromForm(int port, int webPort, int timeout)
    {
        var kind = SshAuthBox.SelectedIndex == 1 ? SshAuthKind.PrivateKey : SshAuthKind.Password;
        var mode = PrivilegeModeBox.SelectedIndex; // 0 普通 / 1 sudo / 2 root
        var saved = _editing?.Device;

        // 编辑时输入框为空 = 不修改,测试要用已保存的那份
        var pass = SshPassBox.Password;
        if (pass.Length == 0 && saved is not null) pass = SecretProtector.Unprotect(saved.SshPassEnc);

        var keyPass = SshKeyPassBox.Password;
        if (keyPass.Length == 0 && saved is not null) keyPass = SecretProtector.Unprotect(saved.SshKeyPassEnc);

        var sudoPass = SudoPassBox.Password;
        if (sudoPass.Length == 0 && saved is not null) sudoPass = SecretProtector.Unprotect(saved.SudoPassEnc);

        return new NasDevice
        {
            Host = HostBox.Text.Trim(),
            SshPort = port,
            WebPort = webPort,
            SshUser = SshUserBox.Text.Trim(),
            SshAuth = kind,
            SshPassEnc = pass.Length > 0 ? SecretProtector.Protect(pass) : string.Empty,
            SshKeyPath = kind == SshAuthKind.PrivateKey ? SshKeyPathBox.Text.Trim() : string.Empty,
            SshKeyPassEnc = keyPass.Length > 0 ? SecretProtector.Protect(keyPass) : string.Empty,
            UseSudo = mode is 1 or 2,
            RootLogin = mode == 2,
            SudoPassEnc = sudoPass.Length > 0 ? SecretProtector.Protect(sudoPass) : string.Empty,
            SshTimeoutSec = timeout,
        };
    }

    private void EditDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var host = HostBox.Text.Trim();
        var hostname = HostnameBox.Text.Trim();
        var mac = MacBox.Text.Trim();

        if (ValidateForm(out var sshPort, out var webPort, out var timeoutSec) is { } error)
        {
            args.Cancel = true;
            ShowError(error);
            return;
        }

        // 名称不再由用户填写:有 Hostname 就用它,否则用 IP / 主机名(连接后会自动补上真实主机名)
        var name = hostname.Length > 0 ? hostname : host;

        var kind = SshAuthBox.SelectedIndex == 1 ? SshAuthKind.PrivateKey : SshAuthKind.Password;
        var keyPath = SshKeyPathBox.Text.Trim();
        var mode = PrivilegeModeBox.SelectedIndex; // 0 普通 / 1 sudo / 2 root
        var useSudo = mode is 1 or 2;
        var rootLogin = mode == 2;
        var clearSecrets = ClearPassBox.IsChecked == true;

        if (_editing is null)
        {
            _devices.Add(new NasDevice
            {
                Name = name,
                Host = host,
                Hostname = hostname,
                Mac = mac,
                WebPort = webPort,
                Note = NoteBox.Text.Trim(),
                SshPort = sshPort,
                SshUser = SshUserBox.Text.Trim(),
                SshAuth = kind,
                SshPassEnc = kind == SshAuthKind.Password && SshPassBox.Password.Length > 0
                    ? SecretProtector.Protect(SshPassBox.Password)
                    : string.Empty,
                SshKeyPath = kind == SshAuthKind.PrivateKey ? keyPath : string.Empty,
                SshKeyPassEnc = kind == SshAuthKind.PrivateKey && SshKeyPassBox.Password.Length > 0
                    ? SecretProtector.Protect(SshKeyPassBox.Password)
                    : string.Empty,
                UseSudo = useSudo,
                RootLogin = rootLogin,
                SudoPassEnc = SudoPassBox.Password.Length > 0
                    ? SecretProtector.Protect(SudoPassBox.Password)
                    : string.Empty,
                SshTimeoutSec = timeoutSec,
            });
        }
        else
        {
            var d = _editing.Device;
            d.Name = name;
            d.Host = host;
            d.Hostname = hostname;
            d.Mac = mac;
            d.WebPort = webPort;
            d.Note = NoteBox.Text.Trim();
            d.SshPort = sshPort;
            d.SshUser = SshUserBox.Text.Trim();
            d.SshAuth = kind;
            d.SshKeyPath = kind == SshAuthKind.PrivateKey ? keyPath : string.Empty;
            d.UseSudo = useSudo;
            d.RootLogin = rootLogin;
            d.SshTimeoutSec = timeoutSec;

            if (clearSecrets)
            {
                d.SshPassEnc = string.Empty;
                d.SshKeyPassEnc = string.Empty;
                d.SudoPassEnc = string.Empty;
                d.SshHostFingerprint = string.Empty;
                SshHostKeyStore.Forget(d.Host.Trim(), sshPort);
            }
            else
            {
                if (SshPassBox.Password.Length > 0) d.SshPassEnc = SecretProtector.Protect(SshPassBox.Password);
                if (SshKeyPassBox.Password.Length > 0) d.SshKeyPassEnc = SecretProtector.Protect(SshKeyPassBox.Password);
                if (SudoPassBox.Password.Length > 0) d.SudoPassEnc = SecretProtector.Protect(SudoPassBox.Password);
            }

            // 凭据或地址变了,丢弃旧连接让其重新握手
            SshService.Forget(d);
        }

        NasDeviceStore.Save(_devices);
        Reload();
    }

    // ---------- 通用 ----------

    private async Task ShowDialog(string title, string content)
    {
        await new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }
}
