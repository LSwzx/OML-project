using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OML.Core.Java;
using OML.Core.Versioning;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 设置页：直接读写共享配置。
/// <para>
/// 滑块拖动会高频触发变更，所以保存做了 500ms 防抖——
/// 否则拖一次内存条会写几十次磁盘。
/// </para>
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherContext _context;
    private readonly DispatcherTimer _saveTimer;

    private bool _suppressSave;

    public SettingsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveAsync();
        };
    }

    [ObservableProperty]
    private string gameDirectory = string.Empty;

    [ObservableProperty]
    private double maxMemoryMB = 4096;

    [ObservableProperty]
    private bool completeAssets = true;

    /// <summary>启动时自动检查更新。</summary>
    [ObservableProperty]
    private bool autoCheckUpdates = true;

    [ObservableProperty]
    private bool versionIsolation;

    [ObservableProperty]
    private string javaPath = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    public string ConfigPath => _context.ConfigPath;

    /// <summary>当前机器上探测到的 Java。</summary>
    public ObservableCollection<JavaInstallation> DetectedJavas { get; } = new();

    [ObservableProperty]
    private string javaScanStatus = "尚未探测";

    [ObservableProperty]
    private bool isScanningJava;

    private bool _javaScanned;

    /// <summary>探测本机 Java（进入页面自动执行一次，也可手动刷新）。</summary>
    [RelayCommand]
    private async Task ScanJavaAsync()
    {
        if (IsScanningJava)
        {
            return;
        }

        IsScanningJava = true;
        JavaScanStatus = "正在探测本机 Java…";
        try
        {
            var installations = await new JavaScanner().ScanAsync();

            DetectedJavas.Clear();
            foreach (var installation in installations)
            {
                DetectedJavas.Add(installation);
            }

            _javaScanned = true;
            JavaScanStatus = installations.Count == 0
                ? "未发现 Java，启动时会提示安装"
                : $"发现 {installations.Count} 个 Java";
        }
        catch (Exception ex)
        {
            JavaScanStatus = $"探测失败：{ex.Message}";
        }
        finally
        {
            IsScanningJava = false;
        }
    }

    /// <summary>页面首次加载时调用，避免每次切页都重启探测进程。</summary>
    public void EnsureJavaScanned()
    {
        if (!_javaScanned && !IsScanningJava)
        {
            _ = ScanJavaAsync();
        }
    }

    /// <summary>当前选中版本要求的 Java 主版本与运行时组件。</summary>
    private async Task<(int Required, string Component)> ResolveJavaRequirementAsync(string versionId)
    {
        try
        {
            var game = _context.CreateGameDirectory();
            using var repository = new VersionRepository(game);
            var detail = await repository.GetVersionDetailAsync(versionId);

            return (detail.JavaVersion?.MajorVersion ?? 17, detail.JavaVersion?.Component ?? "java-runtime-gamma");
        }
        catch
        {
            return (17, "java-runtime-gamma");
        }
    }

    /// <summary>检测缺失的 Java 并一键下载（进全局任务队列）。</summary>
    [RelayCommand]
    private async Task AutoDownloadJavaAsync()
    {
        var selected = _context.Config.SelectedVersion;
        if (string.IsNullOrWhiteSpace(selected))
        {
            MessageBox.Show("请先在安装页装一个版本，或到设置里指定游戏目录。", "OML", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (required, component) = await ResolveJavaRequirementAsync(selected);

        string? existing;
        try
        {
            existing = await new JavaScanner().FindSuitableJavaAsync(required);
        }
        catch
        {
            existing = null;
        }

        if (existing is not null)
        {
            MessageBox.Show(
                $"已检测到可用的 Java {required}+：\n{existing}\n\n无需下载。",
                "Java 已就绪",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await ScanJavaAsync();
            return;
        }

        var launcherRoot = Path.GetDirectoryName(_context.ConfigPath) ?? _context.ConfigPath;

        var task = BackgroundTaskManager.Instance.Enqueue($"下载 Java 运行时（{component}）", async (progress, ct) =>
        {
            using var downloader = new JavaRuntimeDownloader(launcherRoot);
            var info = await downloader.DownloadAsync(component, progress, ct);

            // 直接写配置再落盘：ViewModel 属性的保存是 500ms 防抖的，
            // 立即 SaveAsync 会把旧 JavaPath 写进配置。
            _context.Config.JavaPath = info.JavaPath;

            DispatcherHelper.Run(() =>
            {
                JavaPath = info.JavaPath;
                JavaScanStatus = $"已下载 Java {info.Version}";
            });

            await _context.SaveAsync();
        });

        var ok = await task.Completed;
        if (ok)
        {
            MessageBox.Show("Java 运行时下载完成，已写入设置。", "OML", MessageBoxButton.OK, MessageBoxImage.Information);
            await ScanJavaAsync();
        }
        else
        {
            MessageBox.Show(
                task.WasCanceled ? "已取消 Java 下载。" : $"Java 下载失败：\n{task.Error}",
                "OML",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>显式检查更新，状态通过弹窗告知。</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (App.Shell is null)
        {
            return;
        }

        var result = await App.Shell.Updates.CheckDetailedAsync();

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(
                    $"已是最新版（{App.Shell.Updates.CurrentVersion}）。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;

            case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                var version = result.Update.TargetFullRelease.Version;
                var answer = MessageBox.Show(
                    $"发现新版本 {version}（当前 {App.Shell.Updates.CurrentVersion}）。\n\n是否现在下载并在完成后重启？",
                    "检查更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        await App.Shell.Updates.DownloadAndRestartAsync(result.Update);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"更新下载失败：\n{ex.Message}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                break;

            case UpdateCheckStatus.NotInstalled:
                MessageBox.Show(
                    result.Error ?? "当前版本不是通过安装包安装的，无法检查更新。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;

            default:
                MessageBox.Show(
                    $"检查更新失败：\n{result.Error}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                break;
        }
    }

    /// <summary>从共享配置同步到界面（初始化时调用）。</summary>
    public void LoadFromConfig()
    {
        _suppressSave = true;
        try
        {
            GameDirectory = _context.Config.GameDirectory;
            MaxMemoryMB = Math.Clamp(_context.Config.MaxMemoryMB, 1024, 8192);
            CompleteAssets = _context.Config.CompleteAssets;
            AutoCheckUpdates = _context.Config.AutoCheckUpdates;
            VersionIsolation = _context.Config.VersionIsolation;
            JavaPath = _context.Config.JavaPath;
        }
        finally
        {
            _suppressSave = false;
        }
    }

    partial void OnGameDirectoryChanged(string value) => QueueSave();

    partial void OnMaxMemoryMBChanged(double value) => QueueSave();

    partial void OnCompleteAssetsChanged(bool value) => QueueSave();

    partial void OnAutoCheckUpdatesChanged(bool value) => QueueSave();

    partial void OnVersionIsolationChanged(bool value) => QueueSave();

    partial void OnJavaPathChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_suppressSave)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        try
        {
            var config = _context.Config;
            config.GameDirectory = GameDirectory?.Trim() ?? string.Empty;
            config.MaxMemoryMB = (int)Math.Round(MaxMemoryMB);
            config.CompleteAssets = CompleteAssets;
            config.AutoCheckUpdates = AutoCheckUpdates;
            config.VersionIsolation = VersionIsolation;
            config.JavaPath = JavaPath?.Trim() ?? string.Empty;

            await _context.SaveAsync();
            StatusText = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏目录（.minecraft）",
            Multiselect = false,
        };

        if (Directory.Exists(GameDirectory))
        {
            dialog.InitialDirectory = GameDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            GameDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseJava()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java 可执行文件",
            Filter = "Java 可执行文件|java.exe;javaw.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearJava()
    {
        JavaPath = string.Empty;
        StatusText = "已清空 Java 路径，启动时将自动探测";
    }
}
