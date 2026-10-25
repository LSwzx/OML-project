using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Java;
using OML.Core.Launching;
using OML.Core.Loaders;
using OML.Core.Versioning;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 安装页：在线版本清单 + 原版 / Fabric / Forge 安装。
/// <para>所有安装动作都进入全局任务队列，切页不中断。</para>
/// </summary>
public partial class InstallViewModel : ObservableObject
{
    private readonly LauncherContext _context;

    private IReadOnlyList<VersionSummary> _allVersions = Array.Empty<VersionSummary>();
    private bool _loaded;
    private bool _loadingManifest;

    public InstallViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>安装完成后通知外壳刷新主页版本列表。</summary>
    public event Action<string>? InstallCompleted;

    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public IReadOnlyList<string> Filters { get; } = new[] { "正式版", "快照", "全部" };

    [ObservableProperty]
    private int selectedFilterIndex;

    [ObservableProperty]
    private VersionSummary? selectedVersion;

    [ObservableProperty]
    private string statusText = "打开安装页后加载在线版本清单";

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>PCL 式向导：点列表里的版本后弹出附件面板。</summary>
    [ObservableProperty]
    private bool isDetailOpen;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool installFabricOption;

    [ObservableProperty]
    private bool installForgeOption;

    [ObservableProperty]
    private bool installOptiFineOption;

    [ObservableProperty]
    private string? optiFineJarPath;

    [ObservableProperty]
    private string? forgeVersion;

    public System.Collections.ObjectModel.ObservableCollection<string> ForgeVersions { get; } = new();

    public bool HasOptiFineJar => !string.IsNullOrWhiteSpace(OptiFineJarPath);

    partial void OnOptiFineJarPathChanged(string? value) => OnPropertyChanged(nameof(HasOptiFineJar));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public string DetailText => SelectedVersion is null
        ? "选择一个版本查看详情"
        : $"{SelectedVersion.Id}\n类型：{SelectedVersion.Type}\n发布时间：{SelectedVersion.ReleaseTime:yyyy-MM-dd HH:mm}";

    partial void OnSelectedVersionChanged(VersionSummary? value) => OnPropertyChanged(nameof(DetailText));

    partial void OnSelectedFilterIndexChanged(int value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadManifestAsync()
    {
        // 不能用 IsBusy 做互斥：自动安装会先占用它，导致清单永远加载不出来。
        if (_loaded || _loadingManifest)
        {
            return;
        }

        _loadingManifest = true;
        try
        {
            StatusText = "正在获取版本清单…";
            var game = _context.CreateGameDirectory();
            using var repository = new VersionRepository(game);

            var manifest = await repository.GetManifestAsync();
            _allVersions = manifest.Versions;
            _loaded = true;

            ApplyFilter();
            StatusText = $"在线共 {_allVersions.Count} 个版本";
        }
        catch (Exception ex)
        {
            StatusText = $"获取版本清单失败：{ex.Message}";
        }
        finally
        {
            _loadingManifest = false;
        }
    }

    private void ApplyFilter()
    {
        Versions.Clear();

        var wanted = SelectedFilterIndex switch
        {
            0 => "release",
            1 => "snapshot",
            _ => null,
        };

        var query = SearchText?.Trim() ?? string.Empty;

        foreach (var version in _allVersions.Where(v =>
                     (wanted is null || string.Equals(v.Type, wanted, StringComparison.OrdinalIgnoreCase)) &&
                     (query.Length == 0 || v.Id.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            Versions.Add(version);
        }

        SelectedVersion = Versions.FirstOrDefault();
    }

    // ---------------------------------------------------------------- PCL 式下载向导

    /// <summary>点列表里的版本：打开附件面板。</summary>
    [RelayCommand]
    private void OpenDetail(VersionSummary? version)
    {
        if (version is null)
        {
            return;
        }

        SelectedVersion = version;
        InstallFabricOption = false;
        InstallForgeOption = false;
        InstallOptiFineOption = false;
        ForgeVersion = null;
        ForgeVersions.Clear();
        IsDetailOpen = true;

        _ = LoadForgeVersionsAsync(version.Id);
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;

    private async Task LoadForgeVersionsAsync(string gameVersion)
    {
        try
        {
            var game = _context.CreateGameDirectory();
            using var loader = new LoaderInstaller(game);
            var versions = await loader.GetForgeVersionsAsync(gameVersion);

            DispatcherHelper.Run(() =>
            {
                ForgeVersions.Clear();
                foreach (var version in versions)
                {
                    ForgeVersions.Add(version);
                }

                ForgeVersion = ForgeVersions.FirstOrDefault();
            });
        }
        catch
        {
            // 查不到 Forge 版本时下拉为空，不影响原版/Fabric 安装。
        }
    }

    [RelayCommand]
    private void PickOptiFine()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 OptiFine 安装包",
            Filter = "OptiFine 安装包 (*.jar)|*.jar",
        };

        if (dialog.ShowDialog() == true)
        {
            OptiFineJarPath = dialog.FileName;
            InstallOptiFineOption = true;
        }
    }

    /// <summary>把「原版 + 附件 + 资源」打包成一个后台任务。</summary>
    [RelayCommand]
    private async Task BeginDownloadAsync()
    {
        if (SelectedVersion is null || IsBusy)
        {
            return;
        }

        if (InstallOptiFineOption && string.IsNullOrWhiteSpace(OptiFineJarPath))
        {
            System.Windows.MessageBox.Show(
                "已勾选 OptiFine，但还没有选择安装包。",
                "OML",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var versionId = SelectedVersion.Id;
        // 下载默认补齐全部资源，不再提供开关。
        var fabric = InstallFabricOption;
        var forge = InstallForgeOption;
        var optiFine = InstallOptiFineOption;
        var optiFinePath = OptiFineJarPath;
        var forgeVersion = ForgeVersion;

        IsDetailOpen = false;
        IsBusy = true;
        try
        {
            var game = _context.CreateGameDirectory();

            var item = BackgroundTaskManager.Instance.Enqueue($"安装 {versionId}", async (progress, ct) =>
            {
                BackgroundTaskManager.Instance.ArmWatchdog(TimeSpan.FromSeconds(60));
                using var installer = new VersionInstaller(game);
                var report = new Progress<LaunchProgress>(p =>
                {
                    DispatcherHelper.Run(() => StatusText = p.Message);
                    progress.Report(p.Percent / 100.0);
                });

                await installer.InstallVersionAsync(
                    versionId,
                    new InstallOptions
                    {
                        InstallAssets = true,
                        EnsureJava = false,
                        MaxConcurrency = 8,
                    },
                    report,
                    ct,
                    downloadLog: entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                    status: message => BackgroundTaskManager.Instance.ReportStatus(message));

                if (fabric)
                {
                    DispatcherHelper.Run(() => StatusText = "安装 Fabric…");
                    using var loader = new LoaderInstaller(game);
                    await loader.InstallFabricAsync(versionId, cancellationToken: ct);
                }

                if (forge)
                {
                    DispatcherHelper.Run(() => StatusText = "安装 Forge…");
                    using var loader = new LoaderInstaller(game);
                    await loader.InstallForgeAsync(versionId, forgeVersion, cancellationToken: ct);
                }

                if (optiFine && !string.IsNullOrWhiteSpace(optiFinePath))
                {
                    DispatcherHelper.Run(() => StatusText = "运行 OptiFine 安装器…");
                    await RunOptiFineAsync(optiFinePath!, ct);
                }

                DispatcherHelper.Run(() =>
                {
                    StatusText = $"安装完成：{versionId}";
                    ProgressValue = 100;
                });

                InstallCompleted?.Invoke(versionId);
            });

            if (!await item.Completed)
            {
                StatusText = item.WasCanceled ? "安装已取消" : $"安装失败：{item.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>运行 OptiFine 安装器并检查是否生成了新版本。</summary>
    private async Task RunOptiFineAsync(string jarPath, CancellationToken cancellationToken)
    {
        var created = await RunOptiFineInstallerAsync(jarPath, cancellationToken);
        if (created.Length == 0)
        {
            throw new InvalidOperationException(
                "没有检测到新版本：OptiFine 安装器是图形界面，需要在弹出的窗口里点 Install 再关闭。");
        }
    }

    /// <summary>
    /// 运行 OptiFine 安装器 jar（图形界面），等待退出并返回它新生成的版本 ID。
    /// <para>取消时杀掉整个进程树再抛出。</para>
    /// </summary>
    private async Task<string[]> RunOptiFineInstallerAsync(string jarPath, CancellationToken cancellationToken)
    {
        var game = _context.CreateGameDirectory();

        var before = Directory.Exists(game.VersionsDirectory)
            ? Directory.EnumerateDirectories(game.VersionsDirectory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>();

        var javaPath = await new JavaScanner().FindSuitableJavaAsync(8, cancellationToken)
            ?? throw new InvalidOperationException("没有检测到可运行 OptiFine 安装器的 Java。");

        var arguments = new LaunchArguments
        {
            JavaPath = javaPath,
            WorkingDirectory = game.Root,
            Arguments = new[] { "-jar", jarPath },
        };

        using var launcher = new JvmLauncher(Path.Combine(game.Root, "logs", "optifine"));
        using var process = await launcher.LaunchAsync(arguments, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已退出。
            }

            throw;
        }

        return Directory.Exists(game.VersionsDirectory)
            ? Directory.EnumerateDirectories(game.VersionsDirectory)
                .Select(Path.GetFileName)
                .Where(id => id is not null && !before.Contains(id))
                .Select(id => id!)
                .ToArray()
            : Array.Empty<string>();
    }

    [RelayCommand]
    private Task InstallAsync()
    {
        return SelectedVersion is null ? Task.CompletedTask : InstallVersionAsync(SelectedVersion.Id);
    }

    [RelayCommand]
    private Task InstallFabricAsync()
    {
        return SelectedVersion is null ? Task.CompletedTask : InstallLoaderForAsync(SelectedVersion.Id, "Fabric");
    }

    [RelayCommand]
    private Task InstallForgeAsync()
    {
        return SelectedVersion is null ? Task.CompletedTask : InstallLoaderForAsync(SelectedVersion.Id, "Forge");
    }

    /// <summary>自动化场景跳过"是否补全资源"的询问，直接沿用设置。</summary>
    public bool AutoConfirmAssets { get; set; }

    /// <summary>询问是否补全资源；返回 null 表示用户取消了安装。</summary>
    private bool? AskInstallAssets()
    {
        if (AutoConfirmAssets)
        {
            return _context.Config.CompleteAssets;
        }

        var answer = System.Windows.MessageBox.Show(
            "是否同时下载完整的游戏资源（推荐，约 800MB）？\n\n不下载也能启动，但可能缺音效、字体或语言文件。",
            "安装选项",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        return answer switch
        {
            System.Windows.MessageBoxResult.Yes => true,
            System.Windows.MessageBoxResult.No => false,
            _ => null,
        };
    }

    /// <summary>导入 OptiFine 安装包并执行（OptiFine 没有公开的静默安装接口）。</summary>
    [RelayCommand]
    private async Task ImportOptiFineAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 OptiFine 安装包",
            Filter = "OptiFine 安装包 (*.jar)|*.jar",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportOptiFinePackageAsync(dialog.FileName);
    }

    /// <summary>执行 OptiFine 安装包，并检测它生成的版本目录。</summary>
    public async Task ImportOptiFinePackageAsync(string jarPath)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var item = BackgroundTaskManager.Instance.Enqueue("安装 OptiFine", async (progress, ct) =>
            {
                DispatcherHelper.Run(() => StatusText = "启动 OptiFine 安装器…");

                var created = await RunOptiFineInstallerAsync(jarPath, ct);
                if (created.Length == 0)
                {
                    throw new InvalidOperationException(
                        "没有检测到新版本：OptiFine 安装器是图形界面，需要在弹出的窗口里点 Install 再关闭，然后重试。");
                }

                var versionId = created[0];
                DispatcherHelper.Run(() =>
                {
                    StatusText = $"OptiFine 安装完成：{versionId}";
                    ProgressValue = 100;
                });

                InstallCompleted?.Invoke(versionId);
            });

            if (!await item.Completed)
            {
                StatusText = item.WasCanceled ? "OptiFine 安装已取消" : $"OptiFine 安装失败：{item.Error}";

                System.Windows.MessageBox.Show(
                    item.WasCanceled
                        ? "已取消 OptiFine 安装。"
                        : $"OptiFine 安装未完成：\n{item.Error}\n\n" +
                          "说明：OptiFine 官方安装器是图形界面，没有静默参数。\n" +
                          "请在弹出的安装窗口里点击 Install，等它完成后关闭窗口，再重新导入一次。",
                    "OptiFine",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>安装原版（进入全局队列）。</summary>
    public async Task InstallVersionAsync(string versionId)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var installAssets = AskInstallAssets();
            if (installAssets is null)
            {
                StatusText = "已取消安装";
                return;
            }

            var game = _context.CreateGameDirectory();
            var item = BackgroundTaskManager.Instance.Enqueue($"安装 {versionId}", async (progress, ct) =>
            {
                BackgroundTaskManager.Instance.ArmWatchdog(TimeSpan.FromSeconds(60));
                using var installer = new VersionInstaller(game);

                var report = new Progress<LaunchProgress>(p =>
                {
                    DispatcherHelper.Run(() =>
                    {
                        StatusText = p.Message;
                        ProgressValue = p.Percent;
                    });
                    progress.Report(p.Percent / 100.0);
                });

                var result = await installer.InstallVersionAsync(
                    versionId,
                    new InstallOptions
                    {
                        InstallAssets = installAssets.Value,
                        EnsureJava = false,
                        MaxConcurrency = 8,
                    },
                    report,
                    ct,
                    downloadLog: entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                    status: message => BackgroundTaskManager.Instance.ReportStatus(message));

                DispatcherHelper.Run(() =>
                {
                    StatusText = $"安装完成：{result.VersionId}（库 {result.LibraryFiles} 个，资源 {result.AssetsDownloaded}/{result.AssetsTotal}）";
                    ProgressValue = 100;
                });

                InstallCompleted?.Invoke(result.VersionId);
            });

            var ok = await item.Completed;
            if (!ok)
            {
                StatusText = item.WasCanceled ? "安装已取消" : $"安装失败：{item.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>安装加载器（Fabric / Forge），进入全局队列。</summary>
    public async Task InstallLoaderForAsync(string gameVersion, string loader)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var game = _context.CreateGameDirectory();
            var item = BackgroundTaskManager.Instance.Enqueue($"安装 {loader}（{gameVersion}）", async (progress, ct) =>
            {
                using var installer = new LoaderInstaller(game);

                var report = new Progress<LaunchProgress>(p =>
                {
                    DispatcherHelper.Run(() => StatusText = p.Message);
                    progress.Report(p.Percent / 100.0);
                });

                var result = string.Equals(loader, "Fabric", StringComparison.OrdinalIgnoreCase)
                    ? await installer.InstallFabricAsync(gameVersion, progress: report, cancellationToken: ct)
                    : await installer.InstallForgeAsync(gameVersion, progress: report, cancellationToken: ct);

                DispatcherHelper.Run(() =>
                {
                    StatusText = $"{result.Loader} 安装完成：{result.VersionId}";
                    ProgressValue = 100;
                });

                InstallCompleted?.Invoke(result.VersionId);
            });

            var ok = await item.Completed;
            if (!ok)
            {
                StatusText = item.WasCanceled ? "加载器安装已取消" : $"加载器安装失败：{item.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
