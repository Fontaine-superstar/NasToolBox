using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NasToolbox.Models;
using NasToolbox.Services;
using NasToolbox.Services.Smb;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NasToolbox.Pages;

/// <summary>
/// 文件管理:经 SMB 浏览 NAS 上的共享文件夹,支持上传 / 下载 / 新建文件夹 / 删除。
/// 登录凭据复用该设备的 SSH 账号密码(SMB 会话由 Windows 建立,密码不落盘也不出现在命令行里),
/// 之后 \\host\share 走的就是普通文件 IO,传输速度取决于局域网而不是 SSH 加密通道。
/// </summary>
public sealed partial class FileManagerPage : Page
{
    private readonly List<NasDevice> _devices = new();
    private readonly List<ShareEntry> _shares = new();

    /// <summary>当前浏览的 UNC 路径(\\host\share\子目录);切设备/共享时重置。</summary>
    private string _path = "";

    public FileManagerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadDevices();
    }

    /// <summary>页面可能被 Frame 缓存,再次导航进来时重新读取设备列表。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (IsLoaded) ReloadDevices();
    }

    private NasDevice? Current => DeviceBox.SelectedItem as NasDevice;

    private List<FileEntry> SelectedEntries() => FileList.SelectedItems.OfType<FileEntry>().ToList();

    // ---------- 设备与共享 ----------

    private void ReloadDevices()
    {
        _devices.Clear();
        _devices.AddRange(NasDeviceStore.Load());

        DeviceBox.ItemsSource = _devices;

        var current = NasDeviceStore.GetCurrentOrDefault(_devices);
        if (current is not null) DeviceBox.SelectedItem = current;
        else if (_devices.Count > 0) DeviceBox.SelectedIndex = 0;

        if (_devices.Count == 0)
        {
            ClearList("还没有 NAS 设备,请先到「设备管理」添加。", "还没有 NAS 设备");
        }
        // 选中项变化会触发 DeviceBox_SelectionChanged → 加载共享列表
    }

    private async void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        if (Current is { } device && !device.IsCurrent) NasDeviceStore.SetCurrent(_devices, device);

        _path = ""; // 空路径 = 共享列表视图
        FileList.ItemsSource = null;
        _shares.Clear();
        UpdateActions();

        await LoadSharesAsync().ConfigureAwait(true);
    }

    /// <summary>枚举该 NAS 的磁盘共享:过程中会用 SSH 账号密码建立 SMB 会话。</summary>
    private async Task LoadSharesAsync()
    {
        if (Current is not { } device) return;

        BusyRing.Visibility = Visibility.Visible;
        StatusText.Text = $"正在用 {device.SshUser} 登录 {device.Host} 的 SMB 并读取共享列表…";
        try
        {
            var shares = await SmbSession.ListDiskSharesAsync(device).ConfigureAwait(true);

            _shares.Clear();
            _shares.AddRange(shares);

            if (_shares.Count == 0)
                ClearList($"没有读到 {device.DisplayName} 上的磁盘共享。该账号可能没有被授权访问任何共享。", "没有可用的共享");
            else
                ShowShares();
        }
        catch (Exception ex)
        {
            ClearList(ReadFriendly(ex), "无法读取共享列表");
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
            UpdateActions();
        }
    }

    /// <summary>把共享列表当作「根级文件夹」显示在主列表里,双击进入。</summary>
    private void ShowShares()
    {
        var rows = _shares.Select(s => new FileEntry
        {
            Name = s.Name,
            FullPath = SmbSession.UncOf(Current!, s.Name),
            IsDirectory = true,
        }).ToList();

        _path = "";
        FileList.ItemsSource = rows;
        FileList.SelectedIndex = -1;
        PathBox.Text = $"{Current?.Host}(共享列表)";
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"共 {rows.Count} 个共享 · 双击进入";
        UpdateActions();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) { await LoadSharesAsync().ConfigureAwait(true); return; }

        // 共享列表为空(还没登录成功过)时先补一次;正在共享列表视图就刷新共享,否则刷新当前目录
        if (_shares.Count == 0) await LoadSharesAsync().ConfigureAwait(true);
        else if (_path.Length == 0) await LoadSharesAsync().ConfigureAwait(true);
        else await BrowseAsync(_path).ConfigureAwait(true);
    }

    // ---------- 浏览 ----------

    private async Task BrowseAsync(string uncPath)
    {
        if (Current is not { } device) return;
        if (string.IsNullOrEmpty(uncPath)) return;

        BusyRing.Visibility = Visibility.Visible;
        FileList.IsEnabled = false;
        StatusText.Text = $"正在读取 {uncPath} …";
        try
        {
            // 目录可能尚未建立会话(例如从资源管理器登出后),先确保登录
            await SmbSession.OpenAsync(device).ConfigureAwait(true);

            var list = await SmbFileService.ListAsync(uncPath).ConfigureAwait(true);
            _path = uncPath;
            PathBox.Text = uncPath;
            FileList.ItemsSource = list;
            FileList.SelectedIndex = -1;

            EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var dirs = list.Count(x => x.IsDirectory);
            StatusText.Text = list.Count == 0
                ? $"该目录没有内容 · {uncPath}"
                : $"共 {list.Count} 项(文件夹 {dirs} / 文件 {list.Count - dirs}) · {uncPath}";
        }
        catch (Exception ex)
        {
            ClearList(ReadFriendly(ex), "无法读取该目录");
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
            FileList.IsEnabled = true;
            UpdateActions();
        }
    }

    private void ClearList(string message, string emptyText)
    {
        FileList.ItemsSource = null;
        EmptyHint.Visibility = Visibility.Visible;
        EmptyText.Text = emptyText;
        StatusText.Text = message;
    }

    private void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        // 已在共享根目录:回到共享列表视图
        if (_path.Length == 0) return;
        var root = _shares.Select(s => SmbSession.UncOf(Current, s.Name))
            .FirstOrDefault(u => string.Equals(u.TrimEnd('\\'), _path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (root is not null)
        {
            ShowShares();
            return;
        }
        _ = BrowseAsync(ParentOf(_path));
    }

    private void GoBtn_Click(object sender, RoutedEventArgs e) => Go();

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Go();
    }

    private void Go()
    {
        var target = PathBox.Text?.Trim() ?? "";
        if (target.Length == 0) return;
        // 输入了主机根(\\host)或共享列表占位文本时回到共享列表视图
        if (target.Contains("共享列表") ||
            string.Equals(target.TrimEnd('\\'), $@"\\{Current?.Host}", StringComparison.OrdinalIgnoreCase))
        {
            if (_shares.Count > 0) ShowShares();
            else _ = LoadSharesAsync();
            return;
        }
        _ = BrowseAsync(target);
    }

    /// <summary>UNC 路径的父目录;已在 \\host\share 一级时返回自身之外的一层。</summary>
    private static string ParentOf(string unc)
    {
        var trimmed = unc.TrimEnd('\\');
        var idx = trimmed.LastIndexOf('\\');
        if (idx <= 1) return trimmed;                       // \\host → 没法再退
        var parent = trimmed[..idx];
        // \\host\share 的父级是 \\host,没有浏览意义,保持原样由调用判断
        return parent;
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory)
        {
            await BrowseAsync(entry.FullPath).ConfigureAwait(true);
            return;
        }
        await DownloadAsync(new[] { entry }).ConfigureAwait(true);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        var inShare = _path.Length > 0; // 共享列表视图里不允许增删传
        var count = inShare ? FileList.SelectedItems.Count : 0;
        DownloadBtn.IsEnabled = count > 0;
        DeleteBtn.IsEnabled = count > 0;
        UploadBtn.IsEnabled = inShare && Current is not null;
        NewFolderBtn?.IsEnabled = inShare;
        RefreshDriveButton();
    }

    // ---------- 下载 / 上传 ----------

    private async Task DownloadAsync(IReadOnlyList<FileEntry> entries)
    {
        var files = entries.Where(x => !x.IsDirectory).ToList();
        if (files.Count == 0)
        {
            StatusText.Text = "请先选中文件(暂不支持整站下载文件夹,可进入后逐个下载)。";
            return;
        }

        var folder = await PickFolderAsync().ConfigureAwait(true);
        if (folder is null) return;

        TransferBar.Visibility = Visibility.Visible;
        try
        {
            foreach (var f in files)
            {
                StatusText.Text = $"正在下载 {f.Name}({FileEntry.FormatSize(f.Length)})…";
                await SmbFileService.DownloadAsync(
                        f.FullPath, Path.Combine(folder, SafeName(f.Name)), Progress())
                    .ConfigureAwait(true);
            }
            StatusText.Text = files.Count == 1
                ? $"已下载 {files[0].Name} 到 {folder}"
                : $"已下载 {files.Count} 个文件到 {folder}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败:" + ReadFriendly(ex);
            await BrowseAsync(_path).ConfigureAwait(true);
        }
        finally
        {
            TransferBar.Visibility = Visibility.Collapsed;
            TransferBar.Value = 0;
        }
    }

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e) =>
        await DownloadAsync(SelectedEntries()).ConfigureAwait(true);

    private async Task UploadAsync()
    {
        if (_path.Length == 0) return; // 共享列表视图不支持上传

        var files = await PickFilesAsync().ConfigureAwait(true);
        if (files.Count == 0) return;

        await UploadPathsAsync(files).ConfigureAwait(true);
    }

    /// <summary>把一组本地文件路径上传到当前目录(「上传」按钮与拖拽共用)。</summary>
    private async Task UploadPathsAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || string.IsNullOrEmpty(_path)) return;

        TransferBar.Visibility = Visibility.Visible;
        var ok = 0;
        var summary = "";
        try
        {
            foreach (var local in files)
            {
                var name = Path.GetFileName(local);
                StatusText.Text = $"正在上传 {name}…";
                await SmbFileService.UploadAsync(
                        local, Path.Combine(_path, SafeName(name)), Progress())
                    .ConfigureAwait(true);
                ok++;
            }
            summary = $"已上传 {ok} 个文件到 {_path}";
        }
        catch (Exception ex)
        {
            summary = $"上传失败:{ReadFriendly(ex)}(已成功 {ok} 个)";
        }
        finally
        {
            TransferBar.Visibility = Visibility.Collapsed;
            TransferBar.Value = 0;
        }

        // 先刷新列表再写回结论,避免被刷新的状态文案盖掉
        await BrowseAsync(_path).ConfigureAwait(true);
        StatusText.Text = summary;
    }

    private async void UploadBtn_Click(object sender, RoutedEventArgs e) =>
        await UploadAsync().ConfigureAwait(true);

    // ---------- 拖拽:拖入上传 / 拖出下载 ----------

    /// <summary>整个列表卡片都接受本地文件拖入。</summary>
    private void ListCard_DragOver(object sender, DragEventArgs e)
    {
        if (Current is null || _path.Length == 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "松开上传到当前目录";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void ListCard_Drop(object sender, DragEventArgs e)
    {
        if (Current is null || string.IsNullOrEmpty(_path)) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items;
            try
            {
                items = await e.DataView.GetStorageItemsAsync().AsTask().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取拖入的内容失败:" + ex.Message;
                return;
            }

            // 只取文件;拖入整个文件夹暂不支持(文件夹需要递归遍历,后续可加)
            var files = items
                .Where(x => !x.IsOfType(StorageItemTypes.Folder))
                .Select(x => x.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            if (files.Count == 0)
            {
                StatusText.Text = "只支持拖入本地文件上传(整个文件夹请进入后选择内容,或用「上传」按钮逐个添加)。";
                return;
            }

            await UploadPathsAsync(files).ConfigureAwait(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>把列表里的文件拖到资源管理器即下载:以 UNC 路径构造 StorageFile 交给系统。</summary>
    private async void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var entries = e.Items.OfType<FileEntry>().Where(x => !x.IsDirectory).ToList();
        if (entries.Count == 0)
        {
            e.Cancel = true;
            StatusText.Text = "文件夹暂不支持拖拽下载,请进入文件夹后拖拽里面的文件,或使用「下载」按钮。";
            return;
        }

        // WinUI 3 的 DragItemsStartingEventArgs 没有 GetDeferral(那是 UWP 的 API),
        // 而 e.Data 必须在本事件返回前设置好;GetFileFromPathAsync 只是取文件句柄,
        // 在线程池完成、不依赖 UI 线程,这里同步等待是安全的。
        try
        {
            var items = new List<IStorageItem>();
            foreach (var entry in entries)
            {
                try
                {
                    var file = StorageFile.GetFileFromPathAsync(entry.FullPath).AsTask()
                        .GetAwaiter().GetResult();
                    items.Add(file);
                }
                catch
                {
                    // 单个文件读取失败就跳过,不让整批拖拽失效
                }
            }

            if (items.Count == 0)
            {
                e.Cancel = true;
                StatusText.Text = "无法读取所选文件(网络路径不可访问?),请改用「下载」按钮。";
                return;
            }

            e.Data.SetStorageItems(items);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            StatusText.Text = items.Count == 1
                ? $"拖到资源管理器即可下载 {entries[0].Name}"
                : $"拖到资源管理器即可下载 {items.Count} 个文件";
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            StatusText.Text = "拖拽下载初始化失败:" + ex.Message;
        }
    }

    /// <summary>传输进度:回调在后台线程,切回 UI 线程更新进度条。</summary>
    private IProgress<double> Progress() =>
        new Progress<double>(p => DispatcherQueue.TryEnqueue(() => TransferBar.Value = p));

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "unnamed" : name;
    }

    // ---------- 新建 / 删除 ----------

    private async Task NewFolderAsync()
    {
        if (_path.Length == 0) return; // 共享列表视图不支持新建

        var box = new TextBox { Header = "文件夹名称", PlaceholderText = "例如 Backup" };
        var dialog = new ContentDialog
        {
            Title = "新建文件夹",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary) return;

        var name = box.Text?.Trim() ?? "";
        if (name.Length == 0) return;

        var remote = Path.Combine(_path, name);
        try
        {
            StatusText.Text = $"正在创建 {remote} …";
            await SmbFileService.CreateDirectoryAsync(remote).ConfigureAwait(true);
            await BrowseAsync(_path).ConfigureAwait(true);
            StatusText.Text = $"已创建 {remote}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "创建失败:" + ReadFriendly(ex);
        }
    }

    private async void NewFolderBtn_Click(object sender, RoutedEventArgs e) =>
        await NewFolderAsync().ConfigureAwait(true);

    private async Task DeleteAsync()
    {
        if (_path.Length == 0) return; // 共享列表视图里的行是共享本身,不允许从这里删除

        var entries = SelectedEntries();
        if (entries.Count == 0) return;

        var preview = entries.Count == 1
            ? $"{entries[0].Name}({entries[0].KindText})"
            : $"{entries.Count} 项";
        var warn = entries.Any(x => x.IsDirectory) ? "文件夹会连带删除内部全部内容。" : "";

        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = $"即将从 NAS 上删除 {preview}。{warn}\n该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary) return;

        BusyRing.Visibility = Visibility.Visible;
        var ok = 0;
        var summary = "";
        try
        {
            foreach (var entry in entries)
            {
                await SmbFileService.DeleteAsync(entry.FullPath, recursive: true).ConfigureAwait(true);
                ok++;
            }
            summary = $"已删除 {ok} 项";
        }
        catch (Exception ex)
        {
            summary = $"删除失败:{ReadFriendly(ex)}(已删除 {ok} 项)";
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }

        await BrowseAsync(_path).ConfigureAwait(true);
        StatusText.Text = summary;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e) =>
        await DeleteAsync().ConfigureAwait(true);

    // ---------- 右键菜单:网络驱动器映射 / 下载 / 删除 ----------

    /// <summary>右键列表:过期目标是文件夹就映射它,否则退回到当前目录。多选时若命中某项就以该项为准。</summary>
    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Current is null) return;

        var entry = (e.OriginalSource as FrameworkElement)?.DataContext as FileEntry;
        var menu = BuildContextMenu(entry);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
    }

    private MenuFlyout BuildContextMenu(FileEntry? entry)
    {
        var target = entry is { IsDirectory: true } ? entry.FullPath : (_path.Length > 0 ? _path : null);
        var menu = new MenuFlyout();

        if (target is not null)
        {
            if (DriveMapService.MappedLetter(target) is { } mapped)
            {
                menu.Items.Add(MenuItem($"在资源管理器中打开 {mapped}:", "\uE8B7",
                    () => OpenInShell($@"{mapped}:\")));
                menu.Items.Add(MenuItem($"断开 {mapped}: 驱动器映射", "\uE74D",
                    () => _ = UnmapAsync(mapped)));
            }
            else
            {
                var suggest = DriveMapService.SuggestLetter();
                menu.Items.Add(suggest == '\0'
                    ? Disabled("没有空闲盘符可用于映射")
                    : MenuItem($"映射为网络驱动器 ({suggest}:)", "\uE8B7",
                        () => _ = MapDialogAsync(target)));
            }

            menu.Items.Add(MenuItem("复制网络路径", "\uE8C8", () => CopyText(target)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var selected = SelectedEntries();
        var canDownload = selected.Any(x => !x.IsDirectory);
        menu.Items.Add(MenuItem("下载所选文件", "\uE896", () => _ = DownloadAsync(selected), canDownload));
        menu.Items.Add(MenuItem("删除所选", "\uE74D", () => _ = DeleteAsync(),
            _path.Length > 0 && selected.Count > 0));

        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action onClick, bool enabled = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = enabled,
            Icon = new FontIcon { Glyph = glyph, FontSize = 15 },
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static MenuFlyoutItem Disabled(string text) => new() { Text = text, IsEnabled = false };

    /// <summary>盘符 + 是否持久 + 映射后是否打开:一次确认,避免"快速映射"变成盲操作。</summary>
    private async Task MapDialogAsync(string unc)
    {
        if (Current is null) return;

        var letters = DriveMapService.AvailableLetters();
        if (letters.Count == 0)
        {
            StatusText.Text = "Z 到 D 之间已经没有空闲盘符了,请先断开一些网络驱动器或本地卷。";
            return;
        }

        var combo = new ComboBox
        {
            Header = "驱动器盘符",
            Width = 140,
            ItemsSource = letters.Select(c => c + ":").ToList(),
            SelectedIndex = 0,
        };
        var persist = new CheckBox { Content = "登录时自动重新连接", IsChecked = true };
        var openAfter = new CheckBox { Content = "映射后在资源管理器中打开", IsChecked = true };
        var note = new TextBlock
        {
            Text = unc + "\r\n凭据复用该设备的 SSH 账号密码(如果 SMB 是另一套账号,请先在 Windows 凭据管理器里保存该地址的 Windows 凭据)。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(note);
        panel.Children.Add(combo);
        panel.Children.Add(persist);
        panel.Children.Add(openAfter);

        var dialog = new ContentDialog
        {
            Title = "映射网络驱动器",
            Content = panel,
            PrimaryButtonText = "映射",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary) return;

        var letter = letters[Math.Max(0, combo.SelectedIndex)];
        BusyRing.Visibility = Visibility.Visible;
        try
        {
            StatusText.Text = $"正在映射 {unc} → {letter}: …";
            await DriveMapService.MapAsync(Current, unc, letter, persist.IsChecked == true)
                .ConfigureAwait(true);
            StatusText.Text = $"已映射 {unc} → {letter}:";
            RefreshDriveButton();
            if (openAfter.IsChecked == true) OpenInShell($@"{letter}:\");
        }
        catch (Exception ex)
        {
            StatusText.Text = "映射失败:" + ReadFriendly(ex);
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task UnmapAsync(char letter)
    {
        BusyRing.Visibility = Visibility.Visible;
        try
        {
            await DriveMapService.DisconnectAsync(letter).ConfigureAwait(true);
            StatusText.Text = $"已断开 {letter}: 的网络驱动器映射";
        }
        catch (Exception ex)
        {
            StatusText.Text = "断开失败:" + ReadFriendly(ex);
        }
        finally
        {
            BusyRing.Visibility = Visibility.Collapsed;
            RefreshDriveButton();
        }
    }

    /// <summary>「网络驱动器」按钮:集中查看与断开这台 NAS 的映射。</summary>
    private async void DriveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;

        var mapped = DriveMapService.ListMapped(Current.Host);
        var panel = new StackPanel { Spacing = 10, Width = 520 };

        // 断开按钮的回调要关掉弹窗,dialog 必须先声明再赋值(C# 不允许先用后声明)
        ContentDialog dialog = null!;

        if (mapped.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"当前没有映射到 {Current.Host} 的网络驱动器。\r\n" +
                       "在左侧列表里右键某个共享(或任意文件夹),选「映射为网络驱动器」即可。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Opacity = 0.75,
            });
        }
        else
        {
            foreach (var drive in mapped)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{drive.Letter}:  →  {drive.Remote}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 13,
                };
                var btn = new Button
                {
                    Content = "断开",
                    FontSize = 12,
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = drive.Letter,
                };
                Grid.SetColumn(btn, 1);
                btn.Click += async (s, _) =>
                {
                    await UnmapAsync((char)((Button)s!).Tag).ConfigureAwait(true);
                    dialog.Hide();
                };

                row.Children.Add(text);
                row.Children.Add(btn);
                panel.Children.Add(row);
            }
        }

        dialog = new ContentDialog
        {
            Title = "网络驱动器",
            Content = panel,
            PrimaryButtonText = _path.Length > 0 ? "映射当前目录" : null,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
            await MapDialogAsync(_path).ConfigureAwait(true);
    }

    /// <summary>按钮上显示这台 NAS 已占用的盘符,便于确认映射是否还在。</summary>
    private void RefreshDriveButton()
    {
        if (Current is null) return;
        var mapped = DriveMapService.ListMapped(Current.Host);
        DriveBtn.Content = mapped.Count == 0
            ? "网络驱动器"
            : $"网络驱动器 ({string.Join(' ', mapped.Select(m => m.Letter + ":"))})";
    }

    private void OpenInShell(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "打开失败:" + ex.Message; }
    }

    private void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusText.Text = "已复制:" + text;
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败:" + ex.Message;
        }
    }

    // ---------- 本地文件 / 文件夹选择(非打包应用必须先给窗口句柄) ----------

    private static nint MainWindowHandle() =>
        App.MainWin is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin);

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        try
        {
            var folder = await picker.PickSingleFolderAsync().AsTask().ConfigureAwait(true);
            return folder?.Path;
        }
        catch (Exception ex)
        {
            // 极少数机器缺少 IFileDialog 支持:退回到「下载」目录,不让功能整体失效
            StatusText.Text = "无法打开文件夹选择器,已改存到「下载」目录:" + ex.Message;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { } p
                ? Path.Combine(p, "Downloads") : null;
        }
    }

    private async Task<List<string>> PickFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        IReadOnlyList<Windows.Storage.StorageFile> picked;
        try
        {
            picked = await picker.PickMultipleFilesAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开本地文件失败:" + ex.Message;
            return new List<string>();
        }
        return picked?.Select(f => f.Path).ToList() ?? new List<string>();
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = MainWindowHandle();
        if (hwnd != 0) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    /// <summary>SMB/IO 常见异常翻译成人话提示。</summary>
    private static string ReadFriendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            $"没有权限:{ex.Message}\r\n请确认该账号对这个共享有读写权限(NAS 的共享权限里一般要单独授权)。",
        DirectoryNotFoundException or FileNotFoundException =>
            $"路径不存在:{ex.Message}",
        IOException io when io.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                             io.Message.Contains("网络", StringComparison.OrdinalIgnoreCase) =>
            $"网络中断:{io.Message}\r\nNAS 可能离线或 SMB 服务异常,可回到本页点刷新重连。",
        IOException io => io.Message,
        _ => ex.Message,
    };
}
