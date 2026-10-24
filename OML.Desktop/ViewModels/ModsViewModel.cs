using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Downloads;
using OML.Abstractions.Modrinth;
using OML.Core.Downloads;
using OML.Core.Modrinth;
using OML.Core.Versioning;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>一条 Modrinth 搜索结果（含异步加载的图标）。</summary>
public partial class ModItemViewModel : ObservableObject
{
    public ModItemViewModel(ModrinthSearchHit hit)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        _ = LoadIconAsync();
    }

    public ModrinthSearchHit Hit { get; }

    public string Title => Hit.Title;

    public string Description => Hit.Description;

    public string DownloadsText => $"{Hit.Downloads:N0}";

    public string AuthorText => string.IsNullOrWhiteSpace(Hit.Author) ? string.Empty : $"by {Hit.Author}";

    [ObservableProperty]
    private ImageSource? icon;

    private async Task LoadIconAsync()
    {
        Icon = await ImageCache.GetAsync(Hit.IconUrl);
    }
}

/// <summary>
/// 资源页：Modrinth 搜索 + 详情 + 下载到 mods。
/// </summary>
public partial class ModsViewModel : ObservableObject
{
    private readonly LauncherContext _context;
    private readonly ModrinthClient _client = new();

    public ModsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ObservableCollection<ModItemViewModel> Results { get; } = new();

    [ObservableProperty]
    private string searchQuery = "JEI";

    [ObservableProperty]
    private ModItemViewModel? selectedItem;

    [ObservableProperty]
    private string statusText = "输入关键词并搜索 Modrinth";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string detailTitle = "未选择模组";

    [ObservableProperty]
    private string detailText = "从左侧列表选择一个模组";

    [ObservableProperty]
    private string versionText = string.Empty;

    /// <summary>当前目标：MC 版本 + 加载器。</summary>
    public string TargetText => $"{ResolveGameVersion()} / {ResolveLoader()}";

    partial void OnSelectedItemChanged(ModItemViewModel? value) => _ = LoadDetailAsync(value);

    [RelayCommand]
    private Task SearchAsync() => SearchAsync(SearchQuery);

    /// <summary>搜索（供按钮与调试参数共用）。</summary>
    public async Task SearchAsync(string query)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        IsBusy = true;
        try
        {
            SearchQuery = query;
            StatusText = $"正在搜索“{query}”…";

            var response = await _client.SearchAsync(
                query,
                limit: 20,
                projectType: "mod",
                gameVersion: ResolveGameVersion(),
                loader: ResolveLoader());

            Results.Clear();
            foreach (var hit in response.Hits)
            {
                Results.Add(new ModItemViewModel(hit));
            }

            StatusText = $"找到 {response.TotalHits:N0} 个结果，显示前 {Results.Count} 个";
            SelectedItem = Results.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDetailAsync(ModItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        DetailTitle = item.Title;
        DetailText = item.Description;
        VersionText = "正在查询兼容版本…";
        LatestVersion = null;

        try
        {
            LatestVersion = await _client.GetLatestVersionAsync(
                item.Hit.ProjectId,
                ResolveGameVersion(),
                ResolveLoader());

            if (LatestVersion is null)
            {
                VersionText = $"{ResolveGameVersion()} / {ResolveLoader()} 没有兼容版本";
                return;
            }

            var file = ModrinthClient.GetPrimaryFile(LatestVersion);
            VersionText = file is null
                ? $"版本 {LatestVersion.VersionNumber} 没有可下载文件"
                : $"版本 {LatestVersion.VersionNumber}（{LatestVersion.VersionType}）· {file.Filename} · {file.Size / 1024.0:0.#} KB";
        }
        catch (Exception ex)
        {
            VersionText = $"查询失败：{ex.Message}";
        }
    }

    [ObservableProperty]
    private ModrinthVersion? latestVersion;

    [RelayCommand]
    private Task DownloadAsync() => DownloadSelectedAsync();

    /// <summary>下载当前选中模组的主文件到 mods 目录。</summary>
    public async Task DownloadSelectedAsync()
    {
        if (SelectedItem is null || IsBusy)
        {
            return;
        }

        // 详情是异步加载的：用户很快点下载时 LatestVersion 可能还没到，先补一次。
        if (LatestVersion is null)
        {
            await LoadDetailAsync(SelectedItem);
        }

        if (LatestVersion is null)
        {
            StatusText = "尚未获取到兼容版本，无法下载";
            return;
        }

        LaunchVersion = LatestVersion;

        var file = ModrinthClient.GetPrimaryFile(LatestVersion);
        if (file is null)
        {
            StatusText = "该版本没有可下载文件";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        try
        {
            // 解析 required 依赖，并在下载前询问用户。
            var dependencies = await ResolveRequiredDependenciesAsync(LatestVersion, CancellationToken.None);
            var includeDependencies = true;

            if (dependencies.Count > 0 && !AutoConfirmDependencies)
            {
                var names = string.Join("、", dependencies.Select(d => d.Title));
                var answer = System.Windows.MessageBox.Show(
                    $"此模组需要前置依赖：{names}。\n是否一并下载？",
                    "前置依赖",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                includeDependencies = answer == System.Windows.MessageBoxResult.Yes;
            }

            var downloads = new List<(string Label, ModrinthVersionFile File)>();
            if (includeDependencies)
            {
                foreach (var (dependencyVersion, title) in dependencies)
                {
                    var dependencyFile = ModrinthClient.GetPrimaryFile(dependencyVersion);
                    if (dependencyFile is not null)
                    {
                        downloads.Add((title, dependencyFile));
                    }
                }
            }

            downloads.Add((SelectedItem.Title, file));

            var game = _context.CreateGameDirectory();
            var modsDirectory = game.GetModsDirectory(_context.Config.SelectedVersion);
            var taskTitle = downloads.Count > 1
                ? $"下载 {SelectedItem.Title} + {downloads.Count - 1} 个依赖"
                : $"下载 {file.Filename}";

            var item = BackgroundTaskManager.Instance.Enqueue(taskTitle, async (progress, ct) =>
            {
                Directory.CreateDirectory(modsDirectory);

                for (var i = 0; i < downloads.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (label, current) = downloads[i];
                    var savePath = Path.Combine(modsDirectory, SanitizeFileName(current.Filename));
                    var fileIndex = i;

                    DispatcherHelper.Run(() => StatusText = $"正在下载 {label}（{fileIndex + 1}/{downloads.Count}）…");

                    using var downloader = new PclStyleDownloader();
                    await downloader.DownloadAsync(
                        new DownloadTask
                        {
                            Url = current.Url,
                            SavePath = savePath,
                            ExpectedSize = current.Size,
                            Sha1 = current.Hashes.Sha1,
                        },
                        new Progress<double>(v =>
                        {
                            var overall = (fileIndex + v) / downloads.Count;
                            progress.Report(overall);
                            DispatcherHelper.Run(() => ProgressValue = overall * 100);
                        }),
                        ct);
                }

                DispatcherHelper.Run(() =>
                {
                    StatusText = $"已下载 {downloads.Count} 个文件到 {modsDirectory}";
                    ProgressValue = 100;
                });
            });

            if (!await item.Completed)
            {
                StatusText = item.WasCanceled ? "下载已取消" : $"下载失败：{item.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>当前展示的兼容版本（下载逻辑用）。</summary>
    private ModrinthVersion? LaunchVersion { get; set; }

    /// <summary>调试/自动化：跳过"是否一并下载依赖"的确认框。</summary>
    public bool AutoConfirmDependencies { get; set; }

    private async Task<List<(ModrinthVersion Version, string Title)>> ResolveRequiredDependenciesAsync(
        ModrinthVersion version,
        CancellationToken cancellationToken)
    {
        var results = new List<(ModrinthVersion, string)>();
        var required = version.Dependencies
            .Where(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(d.ProjectId))
            .ToList();

        foreach (var dependency in required)
        {
            var projectId = dependency.ProjectId!;

            string title;
            try
            {
                title = (await _client.GetProjectAsync(projectId, cancellationToken)).Title;
            }
            catch
            {
                title = projectId;
            }

            var dependencyVersion = await _client.GetLatestVersionAsync(
                projectId,
                ResolveGameVersion(),
                ResolveLoader(),
                cancellationToken: cancellationToken);

            if (dependencyVersion is not null)
            {
                results.Add((dependencyVersion, title));
            }
        }

        return results;
    }

    private string ResolveGameVersion()
    {
        var selected = _context.Config.SelectedVersion;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return "1.20.1";
        }

        try
        {
            // 加载器版本的 ID 不是 MC 版本号（fabric-loader-x-1.20.1），要回溯父版本。
            var game = _context.CreateGameDirectory();
            var info = new LocalVersionScanner(game).Find(selected);
            if (info?.InheritsFrom is { Length: > 0 } parent)
            {
                return parent;
            }
        }
        catch
        {
            // 扫描失败就退回原 ID。
        }

        return selected;
    }

    private string ResolveLoader()
    {
        try
        {
            var game = _context.CreateGameDirectory();
            var info = new LocalVersionScanner(game).Find(_context.Config.SelectedVersion);

            return info?.Loader?.ToLowerInvariant() switch
            {
                "forge" => "forge",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                "fabric" => "fabric",
                _ => "fabric",
            };
        }
        catch
        {
            return "fabric";
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
