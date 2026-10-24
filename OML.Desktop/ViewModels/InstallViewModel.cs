using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Versioning;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 安装页：在线版本清单 + 一键安装。
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

    /// <summary>
    /// 用索引而不是字符串绑定筛选器：
    /// ComboBox 的 ItemsSource 是延迟赋值的，SelectedItem 字符串绑定会在
    /// 赋值瞬间被重置为 null，导致默认筛选失效。
    /// </summary>
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

    /// <summary>首次进入页面时加载清单（只加载一次）。</summary>
    [RelayCommand]
    private async Task LoadManifestAsync()
    {
        // 注意：不能用 IsBusy 做互斥——自动安装会先占用它，
        // 导致清单永远加载不出来（列表空白但安装照常进行）。
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
    private async Task InstallAsync()
    {
        if (SelectedVersion is null || IsBusy)
        {
            return;
        }

        await InstallVersionAsync(SelectedVersion.Id);
    }

    /// <summary>按版本 ID 安装（供界面按钮与调试参数共用）。</summary>
    public async Task InstallVersionAsync(string versionId)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        try
        {
            var game = _context.CreateGameDirectory();
            using var installer = new VersionInstaller(game);

            StatusText = $"正在安装 {versionId}…";

            var progress = new Progress<LaunchProgress>(p =>
            {
                StatusText = p.Message;
                ProgressValue = p.Percent;
            });

            var result = await installer.InstallVersionAsync(
                versionId,
                new InstallOptions
                {
                    InstallAssets = _context.Config.CompleteAssets,
                    EnsureJava = false,
                    MaxConcurrency = 8,
                },
                progress);

            StatusText = $"安装完成：{result.VersionId}（库 {result.LibraryFiles} 个，资源 {result.AssetsDownloaded}/{result.AssetsTotal}）";
            ProgressValue = 100;

            InstallCompleted?.Invoke(result.VersionId);
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
