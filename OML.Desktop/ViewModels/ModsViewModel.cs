using System.Net.Http;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Abstractions.Modrinth;
using OML.Core.Downloads;
using OML.Core.Loaders;
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

    [ObservableProperty]
    private ImageSource? icon;

    private async Task LoadIconAsync()
    {
        Icon = await ImageCache.GetAsync(Hit.IconUrl);
    }
}

/// <summary>
/// 资源页：模组 / 光影 / 整合包。
/// </summary>
public partial class ModsViewModel : ObservableObject
{
    private readonly LauncherContext _context;
    private readonly ModrinthClient _client = new();

    public ModsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>安装完成（整合包会装版本+加载器），通知外壳刷新主页。</summary>
    public event Action<string>? ContentInstalled;

    public ObservableCollection<ModItemViewModel> Results { get; } = new();

    public IReadOnlyList<string> Categories { get; } = new[] { "模组", "光影", "整合包" };

    [ObservableProperty]
    private int selectedCategoryIndex;

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
    private string detailTitle = "未选择项目";

    [ObservableProperty]
    private string detailText = "从左侧列表选择一个项目";

    [ObservableProperty]
    private string versionText = string.Empty;

    [ObservableProperty]
    private ModrinthVersion? latestVersion;

    /// <summary>当前分类对应的 Modrinth project_type。</summary>
    private string ProjectType => SelectedCategoryIndex switch
    {
        1 => "shader",
        2 => "modpack",
        _ => "mod",
    };

    private string CategoryName => Categories[Math.Clamp(SelectedCategoryIndex, 0, Categories.Count - 1)];

    /// <summary>下载目录名：mods / shaderpacks / modpacks。</summary>
    private string ContentFolder => SelectedCategoryIndex switch
    {
        1 => "shaderpacks",
        2 => "modpacks",
        _ => "mods",
    };

    public string TargetText => $"{ResolveGameVersion()} / {ResolveLoader()} · {CategoryName}";

    /// <summary>下载按钮文案随分类变化。</summary>
    public string DownloadButtonText => $"下载到 {ContentFolder}";

    /// <summary>模组需要按加载器过滤；光影/整合包与加载器无关。</summary>
    private string? CurrentLoaderFilter => SelectedCategoryIndex == 0 ? ResolveLoader() : null;

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(DownloadButtonText));
        Results.Clear();
        SelectedItem = null;
        DetailTitle = "未选择项目";
        DetailText = "选择分类后重新搜索";
        VersionText = string.Empty;
        LatestVersion = null;
        StatusText = $"已切换到「{CategoryName}」，点击搜索";
    }

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
            StatusText = $"正在搜索「{query}」（{CategoryName}）…";

            var response = await _client.SearchAsync(
                query,
                limit: 20,
                projectType: ProjectType,
                gameVersion: SelectedCategoryIndex == 2 ? null : ResolveGameVersion(),
                loader: CurrentLoaderFilter);

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
                CurrentLoaderFilter);

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

    [RelayCommand]
    private Task DownloadAsync() => DownloadSelectedAsync();

    /// <summary>下载当前选中项目（模组会先解析依赖树）。</summary>
    public async Task DownloadSelectedAsync()
    {
        if (SelectedItem is null || IsBusy)
        {
            return;
        }

        if (LatestVersion is null)
        {
            await LoadDetailAsync(SelectedItem);
        }

        if (LatestVersion is null)
        {
            StatusText = "尚未获取到兼容版本，无法下载";
            return;
        }

        if (SelectedCategoryIndex == 2)
        {
            var slug = SelectedItem.Hit.Slug is { Length: > 0 } hitSlug ? hitSlug : SelectedItem.Hit.ProjectId;
            await InstallModpackAsync(SelectedItem.Title, slug, SelectedItem.Hit.IconUrl, LatestVersion);
            return;
        }

        await DownloadContentAsync(SelectedItem, LatestVersion);
    }

    private async Task DownloadContentAsync(ModItemViewModel item, ModrinthVersion version)
    {
        var file = ModrinthClient.GetPrimaryFile(version);
        if (file is null)
        {
            StatusText = "该版本没有可下载文件";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        try
        {
            var dependencies = await ResolveDependenciesAsync(version);

            // 没有依赖时也要明确确认一次（PCL 式：选中 → 确认 → 后台队列）。
            if (dependencies.Count == 0 && !AutoConfirmDependencies)
            {
                var confirm = await OML.Desktop.Infrastructure.DialogService.Instance.ShowConfirmAsync(
                    "下载确认",
                    $"即将下载：\n\n{item.Title}\n版本 {version.VersionNumber}\n目标目录：{ContentFolder}/\n\n确认加入后台队列？");

                if (!confirm)
                {
                    StatusText = "已取消下载";
                    return;
                }
            }

            var includeDependencies = dependencies.Count > 0;
            if (dependencies.Count > 0 && !AutoConfirmDependencies)
            {
                var tree = ModrinthDependencyResolver.ToTreeText(
                    $"{item.Title} {version.VersionNumber}",
                    dependencies);

                includeDependencies = await OML.Desktop.Infrastructure.DialogService.Instance.ShowConfirmAsync(
                    "前置依赖",
                    $"检测到前置依赖：\n\n{tree}\n\n是否一并下载？");
            }

            var downloads = new List<(string Label, string Url, string FileName, long Size, string? Sha1)>();
            if (includeDependencies)
            {
                foreach (var node in ModrinthDependencyResolver.Flatten(dependencies))
                {
                    downloads.Add((node.Title, node.Url, node.FileName, node.Size, node.Sha1));
                }
            }

            downloads.Add((item.Title, file.Url, file.Filename, file.Size, file.Hashes.Sha1));

            var game = _context.CreateGameDirectory();
            var targetDirectory = Path.Combine(game.GetGameRoot(_context.Config.SelectedVersion), ContentFolder);

            var title = downloads.Count > 1
                ? $"下载 {item.Title} + {downloads.Count - 1} 个依赖"
                : $"下载 {file.Filename}";

            var task = BackgroundTaskManager.Instance.Enqueue(title, async (progress, ct) =>
            {
                BackgroundTaskManager.Instance.ArmWatchdog(TimeSpan.FromSeconds(60));
                Directory.CreateDirectory(targetDirectory);

                for (var i = 0; i < downloads.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (label, url, fileName, size, sha1) = downloads[i];
                    var index = i;
                    var savePath = Path.Combine(targetDirectory, SanitizeFileName(fileName));

                    DispatcherHelper.Run(() => StatusText = $"正在下载 {label}（{index + 1}/{downloads.Count}）…");

                    using var downloader = new PclStyleDownloader
                {
                    Log = entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                    Status = message => BackgroundTaskManager.Instance.ReportStatus(message),
                };
                    await downloader.DownloadAsync(
                        new DownloadTask { Url = url, SavePath = savePath, ExpectedSize = size, Sha1 = sha1 },
                        new Progress<double>(v =>
                        {
                            var overall = (index + v) / downloads.Count;
                            progress.Report(overall);
                            DispatcherHelper.Run(() => ProgressValue = overall * 100);
                        }),
                        ct);
                }

                DispatcherHelper.Run(() =>
                {
                    StatusText = $"已下载 {downloads.Count} 个文件到 {targetDirectory}";
                    ProgressValue = 100;
                });
            });

            if (!await task.Completed)
            {
                StatusText = task.WasCanceled ? "下载已取消" : $"下载失败：{task.Error}";
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

    /// <summary>调试/自动化：跳过依赖确认框。</summary>
    public bool AutoConfirmDependencies { get; set; }

    private async Task<IReadOnlyList<DependencyNode>> ResolveDependenciesAsync(ModrinthVersion version)
    {
        if (SelectedCategoryIndex != 0)
        {
            return Array.Empty<DependencyNode>();
        }

        try
        {
            return await new ModrinthDependencyResolver(_client)
                .ResolveAsync(version, ResolveGameVersion(), ResolveLoader());
        }
        catch
        {
            return Array.Empty<DependencyNode>();
        }
    }

    // ---------------------------------------------------------------- 整合包

    /// <summary>按 slug 直接安装整合包（供调试参数与自动化使用）。</summary>
    public async Task InstallModpackBySlugAsync(string slug, string? gameVersion = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SelectedCategoryIndex = 2;
            StatusText = $"查询整合包 {slug}…";

            var versions = await _client.GetVersionsAsync(slug, gameVersion ?? ResolveGameVersion(), null);
            var version = versions.FirstOrDefault();
            if (version is null)
            {
                StatusText = $"{slug} 没有适用于 {gameVersion ?? ResolveGameVersion()} 的版本";
                return;
            }

            var project = await _client.GetProjectAsync(slug);
            IsBusy = false;
            await InstallModpackAsync(project.Title, slug, project.IconUrl, version);
        }
        catch (Exception ex)
        {
            StatusText = $"整合包查询失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallModpackAsync(string displayName, string slug, string? iconUrl, ModrinthVersion version)
    {
        var file = ModrinthClient.GetPrimaryFile(version);
        if (file is null)
        {
            StatusText = "该整合包版本没有可下载文件";
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        try
        {
            var game = _context.CreateGameDirectory();
            var workDirectory = Path.Combine(Path.GetTempPath(), "oml-modpack-" + Guid.NewGuid().ToString("N"));

            var task = BackgroundTaskManager.Instance.Enqueue($"安装整合包 {displayName}", async (progress, ct) =>
            {
                BackgroundTaskManager.Instance.ArmWatchdog(TimeSpan.FromSeconds(60));
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    var archivePath = Path.Combine(workDirectory, "pack.zip");

                    DispatcherHelper.Run(() => StatusText = "下载整合包…");
                    using (var downloader = new PclStyleDownloader
                    {
                        Log = entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                        Status = message => BackgroundTaskManager.Instance.ReportStatus(message),
                    })
                    {
                        await downloader.DownloadAsync(
                            new DownloadTask { Url = file.Url, SavePath = archivePath, ExpectedSize = file.Size, Sha1 = file.Hashes.Sha1 },
                            new Progress<double>(v => progress.Report(v * 0.15)),
                            ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    DispatcherHelper.Run(() => StatusText = "解压整合包…");
                    var extractDirectory = Path.Combine(workDirectory, "extracted");
                    ZipFile.ExtractToDirectory(archivePath, extractDirectory);
                    progress.Report(0.18);

                    var indexPath = Path.Combine(extractDirectory, "modrinth.index.json");
                    if (!File.Exists(indexPath))
                    {
                        throw new InvalidDataException("整合包里没有 modrinth.index.json");
                    }

                    var index = await ModpackIndex.LoadAsync(indexPath, ct);
                    var instanceId = await EnsureInstanceAsync(index, game, slug, progress, ct);

                    // overrides 覆盖到实例目录（实例目录 = versions/<slug>-<loader>-<mc>/）。
                    DispatcherHelper.Run(() => StatusText = "写入 overrides…");
                    var instanceRoot = Path.Combine(game.VersionsDirectory, instanceId);
                    Directory.CreateDirectory(instanceRoot);

                    // 实例元数据：主页优先显示这里的名字与图标。
                    var metadata = JsonSerializer.Serialize(
                        new { name = index.Name, icon = iconUrl, packVersion = version.VersionNumber },
                        new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(Path.Combine(instanceRoot, "instance.json"), metadata, ct);

                    // 图标同时落一份本地文件，断网时主页不会裂图。
                    if (!string.IsNullOrWhiteSpace(iconUrl))
                    {
                        try
                        {
                            using var iconClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                            var iconBytes = await iconClient.GetByteArrayAsync(iconUrl, ct);
                            await File.WriteAllBytesAsync(Path.Combine(instanceRoot, "icon.png"), iconBytes, ct);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                        {
                            // 图标下载失败不影响整合包安装。
                        }
                    }

                    foreach (var overridesName in new[] { "overrides", "client-overrides" })
                    {
                        var overridesDirectory = Path.Combine(extractDirectory, overridesName);
                        if (Directory.Exists(overridesDirectory))
                        {
                            CopyDirectory(overridesDirectory, instanceRoot);
                        }
                    }

                    progress.Report(0.7);

                    // 下载整合包声明的文件。
                    for (var i = 0; i < index.Files.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = index.Files[i];
                        var destination = SafeCombine(instanceRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar))
                            ?? throw new InvalidDataException($"整合包声明了实例目录之外的路径：{entry.Path}");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                        DispatcherHelper.Run(() => StatusText = $"下载整合包文件 {i + 1}/{index.Files.Count}：{Path.GetFileName(entry.Path)}");

                        using var downloader = new PclStyleDownloader
                {
                    Log = entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                    Status = message => BackgroundTaskManager.Instance.ReportStatus(message),
                };
                        await downloader.DownloadAsync(
                            new DownloadTask
                            {
                                Url = entry.Downloads.FirstOrDefault() ?? string.Empty,
                                SavePath = destination,
                                ExpectedSize = entry.FileSize,
                                Sha1 = entry.Sha1,
                            },
                            new Progress<double>(v =>
                            {
                                var overall = 0.7 + (i + v) / index.Files.Count * 0.28;
                                progress.Report(overall);
                            }),
                            ct);
                    }

                    progress.Report(1);

                    // 整合包装完，切到该实例并启用隔离。
                    await DispatcherHelper.RunAsync(() =>
                    {
                        _context.Config.VersionIsolation = true;
                        _context.Config.SelectedVersion = instanceId;
                    });
                    await _context.SaveAsync();

                    DispatcherHelper.Run(() =>
                    {
                        StatusText = $"整合包安装完成：{index.Name}（{instanceId}）";
                        ProgressValue = 100;
                    });

                    ContentInstalled?.Invoke(instanceId);
                }
                finally
                {
                    TryDeleteDirectory(workDirectory);
                }
            });

            if (!await task.Completed)
            {
                StatusText = task.WasCanceled ? "整合包安装已取消" : $"整合包安装失败：{task.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 为整合包准备一个独立实例：<c>versions/&lt;slug&gt;-&lt;loader&gt;-&lt;mcver&gt;/</c>。
    /// <para>
    /// 直接复用加载器版本目录（fabric-loader-x-y）会让两个使用同一加载器的整合包互相覆盖，
    /// 所以这里把加载器 JSON 复制一份并改写 <c>id</c>，得到属于这个整合包的版本 ID。
    /// <c>inheritsFrom</c> 保持不变，扫描器/启动链路无需改动。
    /// </para>
    /// </summary>
    private async Task<string> EnsureInstanceAsync(
        ModpackIndex index,
        GameDirectory game,
        string slug,
        IProgress<double> progress,
        CancellationToken ct)
    {
        var minecraft = index.MinecraftVersion;
        var loaderTag = index.Loader == "forge" ? "forge" : "fabric";
        var instanceId = $"{Slugify(slug)}-{loaderTag}-{minecraft}";
        var instanceJsonPath = game.GetVersionJsonPath(instanceId);

        if (File.Exists(instanceJsonPath))
        {
            return instanceId;
        }

        if (!File.Exists(game.GetVersionJsonPath(minecraft)))
        {
            DispatcherHelper.Run(() => StatusText = $"安装原版 {minecraft}…");
            using var installer = new VersionInstaller(game);
            await installer.InstallVersionAsync(
                minecraft,
                new InstallOptions { InstallAssets = _context.Config.CompleteAssets, EnsureJava = false, MaxConcurrency = 8 },
                new Progress<LaunchProgress>(p => progress.Report(0.2 + p.Percent / 300)),
                ct,
                downloadLog: entry => BackgroundTaskManager.Instance.ReportLog(entry.Message),
                status: message => BackgroundTaskManager.Instance.ReportStatus(message));
        }

        DispatcherHelper.Run(() => StatusText = $"安装加载器 {index.Loader} {index.LoaderVersion}…");
        progress.Report(0.55);

        string loaderVersionId;
        using (var loader = new LoaderInstaller(game))
        {
            var result = index.Loader == "fabric-loader"
                ? await loader.InstallFabricAsync(minecraft, index.LoaderVersion, cancellationToken: ct)
                : await loader.InstallForgeAsync(minecraft, index.LoaderVersion, cancellationToken: ct);
            loaderVersionId = result.VersionId;
        }

        var loaderJsonPath = game.GetVersionJsonPath(loaderVersionId);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(loaderJsonPath, ct))
                   ?? throw new InvalidDataException($"加载器版本 JSON 无法解析：{loaderJsonPath}");
        node["id"] = instanceId;

        Directory.CreateDirectory(game.GetVersionDirectory(instanceId));
        await File.WriteAllTextAsync(
            instanceJsonPath,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return instanceId;
    }

    private static string Slugify(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var lastDash = false;

        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "pack" : slug;
    }


    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结果。
        }
    }

    /// <summary>
    /// 把整合包声明的相对路径合并进实例目录；含 <c>..</c> 等逃逸成分时返回 null。
    /// <para>modrinth.index.json 来自任意第三方，路径不校验就能写到实例目录之外。</para>
    /// </summary>
    private static string? SafeCombine(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---------------------------------------------------------------- 公共

    private string ResolveGameVersion()
    {
        var selected = _context.Config.SelectedVersion;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return "1.20.1";
        }

        try
        {
            var game = _context.CreateGameDirectory();
            var info = new LocalVersionScanner(game).Find(selected);
            if (info?.InheritsFrom is { Length: > 0 } parent)
            {
                return parent;
            }
        }
        catch
        {
            // 扫描失败退回原 ID。
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

/// <summary><c>modrinth.index.json</c> 的轻量模型。</summary>
internal sealed class ModpackIndex
{
    public string Name { get; init; } = "整合包";

    public string MinecraftVersion { get; init; } = "1.20.1";

    public string Loader { get; init; } = "fabric-loader";

    public string LoaderVersion { get; init; } = string.Empty;

    public List<ModpackFile> Files { get; } = new();

    public static async Task<ModpackIndex> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var dependencies = root.GetProperty("dependencies");

        var minecraft = dependencies.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "1.20.1" : "1.20.1";

        var loader = "fabric-loader";
        var loaderVersion = string.Empty;

        if (dependencies.TryGetProperty("fabric-loader", out var fabric))
        {
            loaderVersion = fabric.GetString() ?? string.Empty;
        }
        else if (dependencies.TryGetProperty("forge", out var forge))
        {
            loader = "forge";
            loaderVersion = forge.GetString() ?? string.Empty;
        }

        var index = new ModpackIndex
        {
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "整合包" : "整合包",
            MinecraftVersion = minecraft,
            Loader = loader,
            LoaderVersion = loaderVersion,
        };

        if (root.TryGetProperty("files", out var files))
        {
            foreach (var entry in files.EnumerateArray())
            {
                var fileNamePath = entry.GetProperty("path").GetString() ?? string.Empty;
                var downloads = entry.TryGetProperty("downloads", out var urls)
                    ? urls.EnumerateArray().Select(u => u.GetString() ?? string.Empty).Where(u => u.Length > 0).ToList()
                    : new List<string>();

                string? sha1 = null;
                if (entry.TryGetProperty("hashes", out var hashes) &&
                    hashes.TryGetProperty("sha1", out var hash))
                {
                    sha1 = hash.GetString();
                }

                var size = entry.TryGetProperty("fileSize", out var fileSize) ? fileSize.GetInt64() : 0;

                index.Files.Add(new ModpackFile(fileNamePath, downloads, sha1, size));
            }
        }

        return index;
    }
}

internal sealed record ModpackFile(string Path, List<string> Downloads, string? Sha1, long FileSize);
