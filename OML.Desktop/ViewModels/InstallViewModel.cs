using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
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

        foreach (var version in _allVersions.Where(v => wanted is null ||
                                                        string.Equals(v.Type, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            Versions.Add(version);
        }

        SelectedVersion = Versions.FirstOrDefault();
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
            var game = _context.CreateGameDirectory();
            var item = BackgroundTaskManager.Instance.Enqueue($"安装 {versionId}", async (progress, ct) =>
            {
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
                        InstallAssets = _context.Config.CompleteAssets,
                        EnsureJava = false,
                        MaxConcurrency = 8,
                    },
                    report,
                    ct);

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
                StatusText = $"安装失败：{item.Error}";
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
                StatusText = $"加载器安装失败：{item.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
