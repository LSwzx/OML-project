using System.Diagnostics;
using System.Net.Http;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Assets;
using OML.Core.Downloads;
using OML.Core.Java;
using static OML.Core.Launching.LaunchProgressReporter;

namespace OML.Core.Versioning;

/// <summary>安装选项。</summary>
public sealed class InstallOptions
{
    /// <summary>是否下载资源文件（assets）。</summary>
    public bool InstallAssets { get; init; } = true;

    /// <summary>
    /// 只安装前 N 个资源。为 <c>null</c> 表示全量（1.20.1 约 3900 个对象、650MB）。
    /// 测试与"先能启动再说"的场景可以限制。
    /// </summary>
    public int? AssetLimit { get; init; }

    /// <summary>是否解压 natives。</summary>
    public bool ExtractNatives { get; init; } = true;

    /// <summary>缺少合适 Java 时是否自动下载运行时。</summary>
    public bool EnsureJava { get; init; }

    /// <summary>下载并发上限。</summary>
    public int MaxConcurrency { get; init; } = 8;
}

/// <summary>安装结果。</summary>
public sealed record InstallResult(
    string VersionId,
    int LibraryFiles,
    int AssetsTotal,
    int AssetsDownloaded,
    int NativesExtracted,
    string? JavaPath,
    TimeSpan Elapsed);

/// <summary>
/// 版本安装器：把一个版本从"清单里存在"变成"本地可启动"。
/// <para>顺序：解析 → 库 → 客户端 jar → 资源 → natives →（可选）Java 运行时。</para>
/// </summary>
public sealed class VersionInstaller : IDisposable
{
    private readonly GameDirectory _gameDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadEngine? _injectedEngine;
    private readonly JavaRuntimeDownloader? _injectedJavaDownloader;
    private readonly string _launcherRoot;

    private bool _disposed;

    public VersionInstaller(
        GameDirectory gameDirectory,
        HttpClient? httpClient = null,
        IDownloadEngine? downloadEngine = null,
        JavaRuntimeDownloader? javaRuntimeDownloader = null,
        string? launcherRoot = null)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _injectedEngine = downloadEngine;
        _injectedJavaDownloader = javaRuntimeDownloader;
        _launcherRoot = launcherRoot ?? AppContext.BaseDirectory;
    }

    /// <summary>安装（或修复）一个版本。</summary>
    public async Task<InstallResult> InstallVersionAsync(
        string versionId,
        InstallOptions? options = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<DownloadLogEntry>? downloadLog = null,
        Action<string>? status = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        options ??= new InstallOptions();

        var stopwatch = Stopwatch.StartNew();
        Report(progress, LaunchStage.Preparing, 0, $"准备安装 {versionId}");
        Directory.CreateDirectory(_gameDirectory.Root);

        using var repository = new VersionRepository(_gameDirectory, _http);
        var version = await repository.GetResolvedVersionAsync(versionId, cancellationToken).ConfigureAwait(false);

        // 已存在且能解析的版本 JSON = 用户已经有这个版本（可能来自 PCL2/官方启动器）。
        // 这种情况下绝不允许"全量重装"：不清理、不覆盖，只做增量修复。
        var hasValidLocalJson = TryParseLocalJson(version.Id, out var localJsonError);

        if (!hasValidLocalJson && InstallState.IsInstalling(_gameDirectory, version.Id))
        {
            // 上次没装完（取消/崩溃）：先清掉启动器管理的半成品，再重装。
            Report(progress, LaunchStage.Preparing, 0, $"清理上次未完成的安装：{version.Id}{localJsonError}");
            InstallState.CleanupPartial(_gameDirectory, version.Id);
        }

        if (hasValidLocalJson)
        {
            Report(progress, LaunchStage.Preparing, 0, $"检测到已有版本 {version.Id}，进入增量修复模式");
        }

        if (!hasValidLocalJson)
        {
            InstallState.Begin(_gameDirectory, version.Id);
        }

        Report(progress, LaunchStage.Checking, 0, "解析版本依赖");
        var resolution = new LibraryResolver(_gameDirectory, RuleEvaluator.ForCurrentPlatform()).Resolve(version);

        var engine = _injectedEngine ?? new PclStyleDownloader(_http);
        if (engine is PclStyleDownloader pcl)
        {
            pcl.Log = downloadLog;
            pcl.Status = status;
        }

        var concurrency = Math.Max(1, options.MaxConcurrency);

        try
        {
            Report(progress, LaunchStage.Downloading, 0, $"下载 {resolution.Downloads.Count} 个依赖库");
            var libraryResult = await DownloadBatchRunner
                .RunAsync(
                    resolution.Downloads,
                    engine,
                    concurrency,
                    new PhaseProgress(v => Report(progress, LaunchStage.Downloading, v * 0.5, $"正在下载依赖库（{v:P0}）")),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!libraryResult.AllSucceeded)
            {
                // 携带完整失败清单：全局任务栏据此显示"N 个文件下载失败"，UI 可弹窗展示。
                throw new DownloadBatchException(libraryResult.Failures);
            }

            await EnsureClientJarAsync(version, engine, progress, cancellationToken).ConfigureAwait(false);

            var (assetsTotal, assetsDownloaded) = await EnsureAssetsAsync(
                version, engine, concurrency, options, progress, cancellationToken).ConfigureAwait(false);

            var nativesExtracted = 0;
            if (options.ExtractNatives && resolution.Natives.Count > 0)
            {
                Report(progress, LaunchStage.Extracting, 0, "解压本地库");
                nativesExtracted = NativesExtractor
                    .Extract(resolution.Natives.Select(n => n.JarPath), _gameDirectory.GetVersionNativesDirectory(version.Id))
                    .Count;
            }

            string? javaPath = null;
            if (options.EnsureJava)
            {
                javaPath = await EnsureJavaAsync(version, progress, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            InstallState.Complete(_gameDirectory, version.Id);
            Report(progress, LaunchStage.Completed, 1, "安装完成");

            return new InstallResult(
                version.Id,
                resolution.Downloads.Count,
                assetsTotal,
                assetsDownloaded,
                nativesExtracted,
                javaPath,
                stopwatch.Elapsed);
        }
        finally
        {
            if (_injectedEngine is null)
            {
                (engine as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// 判断本地是否已有可解析的 <c>&lt;id&gt;.json</c>。
    /// 只要解析成功，就说明这是用户已有的合规版本，安装器只能补缺、不能重装。
    /// </summary>
    private bool TryParseLocalJson(string versionId, out string error)
    {
        error = string.Empty;
        var jsonPath = _gameDirectory.GetVersionJsonPath(versionId);

        if (!File.Exists(jsonPath))
        {
            error = "（本地没有 JSON）";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(jsonPath);
            var detail = System.Text.Json.JsonSerializer.Deserialize<VersionDetail>(
                stream,
                OML.Core.Serialization.OmlJsonOptions.Default);

            if (detail is null)
            {
                error = "（本地 JSON 内容为 null）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"（本地 JSON 损坏：{ex.GetType().Name}: {ex.Message}）";
            return false;
        }
    }

    private async Task EnsureClientJarAsync(
        VersionDetail version,
        IDownloadEngine engine,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = version.Downloads?.Client;
        var jarPath = _gameDirectory.GetVersionJarPath(version.Id);

        if (client?.Url is null || File.Exists(jarPath))
        {
            return;
        }

        Report(progress, LaunchStage.Downloading, 0.5, "下载客户端 jar");
        await engine.DownloadAsync(
            new DownloadTask
            {
                Url = client.Url,
                SavePath = jarPath,
                ExpectedSize = client.Size,
                Sha1 = client.Sha1,
            },
            new PhaseProgress(v => Report(progress, LaunchStage.Downloading, 0.5 + (v * 0.1), "下载客户端 jar")),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Total, int Downloaded)> EnsureAssetsAsync(
        VersionDetail version,
        IDownloadEngine engine,
        int concurrency,
        InstallOptions options,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (version.AssetIndex is not { Url: not null } assetIndex)
        {
            return (0, 0);
        }

        Report(progress, LaunchStage.Downloading, 0.6, "检查资源索引");

        using var assets = new AssetDownloader(_gameDirectory, _http);
        var index = await assets.GetIndexAsync(assetIndex, cancellationToken).ConfigureAwait(false);

        // 即使只下载一部分，也先生成完整任务列表：UI 需要知道"总共有多少要补"。
        var allTasks = assets.CreateTasks(index);
        var total = allTasks.Count;

        if (!options.InstallAssets || total == 0)
        {
            return (total, 0);
        }

        Report(progress, LaunchStage.Downloading, 0.6, $"补全资源（共 {total} 个缺失对象）");

        var result = await assets
            .DownloadAsync(
                index,
                engine,
                concurrency,
                options.AssetLimit,
                new PhaseProgress(v => Report(progress, LaunchStage.Downloading, 0.6 + (v * 0.35), $"正在补全资源（{v:P0}）")),
                preferMirror: null,
                retryPasses: 2,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.AllSucceeded)
        {
            // 资源缺失不阻断安装：游戏仍可启动，只是缺贴图/音效。
            Report(progress, LaunchStage.Downloading, 0.95, $"警告：{result.FailedCount} 个资源下载失败");
        }

        return (total, result.Succeeded);
    }

    private async Task<string> EnsureJavaAsync(
        VersionDetail version,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var required = version.JavaVersion?.MajorVersion ?? 8;

        Report(progress, LaunchStage.Checking, 0.5, $"检查 Java {required}+");

        var scanner = new JavaScanner();
        var existing = await scanner.FindSuitableJavaAsync(required, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var downloader = _injectedJavaDownloader ?? new JavaRuntimeDownloader(_launcherRoot, _http);

        try
        {
            var component = string.IsNullOrWhiteSpace(version.JavaVersion?.Component)
                ? "java-runtime-gamma"
                : version.JavaVersion!.Component!;

            Report(progress, LaunchStage.Downloading, 0.95, $"下载 Java 运行时 {component}");

            var runtime = await downloader
                .DownloadAsync(
                    component,
                    new PhaseProgress(v => Report(progress, LaunchStage.Downloading, 0.95 + (v * 0.04), $"下载 Java 运行时（{v:P0}）")),
                    cancellationToken)
                .ConfigureAwait(false);

            return runtime.JavaPath;
        }
        finally
        {
            if (_injectedJavaDownloader is null)
            {
                downloader.Dispose();
            }
        }
    }


    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
