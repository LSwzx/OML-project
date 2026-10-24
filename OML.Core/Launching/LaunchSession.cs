using System.Diagnostics;
using System.Net.Http;
using OML.Abstractions.Configuration;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Assets;
using OML.Core.Downloads;
using OML.Core.Java;
using OML.Core.Versioning;

namespace OML.Core.Launching;

/// <summary>
/// 启动门面：UI 层唯一需要打交道的入口。
/// <para>
/// 把"读版本 → 解析依赖 → 补全文件 → 解压 natives → 预检 → 建参数 → 起进程"收成一条流水线，
/// 每一步都通过 <see cref="IProgress{LaunchProgress}"/> 上报阶段与文案。
/// </para>
/// </summary>
public sealed class LaunchSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadEngine? _downloadEngine;
    private readonly JavaScanner _javaScanner = new();

    private bool _disposed;

    /// <param name="httpClient">可注入共享客户端；为 <c>null</c> 时自建。</param>
    /// <param name="downloadEngine">可注入下载引擎；为 <c>null</c> 时使用 <see cref="PclStyleDownloader"/>。</param>
    public LaunchSession(HttpClient? httpClient = null, IDownloadEngine? downloadEngine = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _downloadEngine = downloadEngine;
    }

    /// <summary>游戏日志行（stdout + stderr），供 UI 追加到控制台。</summary>
    public event Action<string>? LogReceived;

    /// <summary>最近一次启动使用的参数（用于"显示命令行"或排障）。</summary>
    public LaunchArguments? LastArguments { get; private set; }

    /// <summary>最近一次启动的捕获日志路径。</summary>
    public string? LastLogFile { get; private set; }

    /// <summary>
    /// 启动游戏。进程起来后立即返回；退出码与崩溃报告通过 <see cref="GameProcessInfo.WaitForExitAsync"/> 获取。
    /// </summary>
    public async Task<GameProcessInfo> LaunchGameAsync(
        LauncherConfig config,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(config.SelectedVersion))
        {
            throw new InvalidOperationException("配置中没有选择版本（selectedVersion 为空）。");
        }

        if (config.UserProfile is null)
        {
            throw new InvalidOperationException("配置中没有账号（userProfile 为空）。");
        }

        var versionId = config.SelectedVersion.Trim();
        var game = new GameDirectory(
            string.IsNullOrWhiteSpace(config.GameDirectory) ? ".minecraft" : config.GameDirectory,
            config.VersionIsolation);

        Report(progress, LaunchStage.Preparing, 0, $"准备启动 {versionId}");
        Directory.CreateDirectory(game.Root);

        using var repository = new VersionRepository(game, _http);
        var engine = _downloadEngine ?? new PclStyleDownloader(_http);
        var concurrency = Math.Max(1, config.MaxDownloadConcurrency);

        try
        {
            Report(progress, LaunchStage.Checking, 0, "读取版本信息");
            var version = await repository.GetResolvedVersionAsync(versionId, cancellationToken).ConfigureAwait(false);

            var resolution = new LibraryResolver(game, RuleEvaluator.ForCurrentPlatform()).Resolve(version);

            // 继承版本（Fabric/Forge）自己没有 jar，客户端 jar 在父版本目录里。
            var clientJarPath = await ResolveClientJarAsync(repository, game, version, cancellationToken).ConfigureAwait(false);

            await EnsureLibrariesAsync(resolution, engine, concurrency, progress, cancellationToken).ConfigureAwait(false);
            await EnsureClientJarAsync(version, clientJarPath, engine, progress, cancellationToken).ConfigureAwait(false);
            await EnsureAssetsAsync(game, version, engine, concurrency, config, progress, cancellationToken).ConfigureAwait(false);
            Report(progress, LaunchStage.Downloading, 1, "依赖与资源检查完成");

            Report(progress, LaunchStage.Extracting, 0, "解压本地库（natives）");
            var nativesDirectory = game.GetVersionNativesDirectory(version.Id);
            var extracted = NativesExtractor.Extract(
                resolution.Natives.Select(n => n.JarPath),
                nativesDirectory);
            Report(progress, LaunchStage.Extracting, 1, $"已解压 {extracted.Count} 个本地库");

            var javaPath = await ResolveJavaAsync(config, version, cancellationToken).ConfigureAwait(false);

            // Forge 的 BootstrapLauncher 通过 -p 模块路径加载 client jar，
            // classpath 里再放一份会被当成第二个模块（module minecraft / _1._20._1 冲突）。
            var isForgeBootstrap = version.MainClass?.StartsWith("cpw.mods.", StringComparison.Ordinal) == true;

            var options = new LaunchOptions
            {
                GameDirectory = game,
                Version = version,
                Libraries = resolution,
                User = config.UserProfile,
                JavaPath = javaPath,
                MaxMemoryMb = Math.Max(512, config.MaxMemoryMB),
                ClientJarPath = clientJarPath,
                IncludeClientJarInClasspath = !isForgeBootstrap,
            };

            new LaunchPrechecker().Ensure(options);

            Report(progress, LaunchStage.Launching, 0, "构建启动参数");
            var arguments = new LaunchArgumentBuilder().Build(options);
            LastArguments = arguments;

            var launcher = new JvmLauncher(game.GetLogsDirectory(version.Id));
            launcher.OnLogReceived += line => LogReceived?.Invoke(line);
            LastLogFile = launcher.LogFilePath;

            Report(progress, LaunchStage.Launching, 1, $"启动 {version.Id}（Java {javaPath}）");
            var startedAt = DateTime.UtcNow;
            var process = await launcher.LaunchAsync(arguments, cancellationToken).ConfigureAwait(false);

            var crashDirectory = Path.Combine(game.GetGameRoot(version.Id), "crash-reports");
            var completion = WaitForExitCoreAsync(process, crashDirectory, startedAt, launcher);

            Report(progress, LaunchStage.Completed, 1, "游戏已启动");

            return new GameProcessInfo(process, completion);
        }
        finally
        {
            if (_downloadEngine is null)
            {
                (engine as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// 沿 <c>inheritsFrom</c> 链找到实际存在的客户端 jar。
    /// Fabric/Forge 版本目录里通常没有 jar，真正的 jar 在父版本目录。
    /// </summary>
    private static async Task<string> ResolveClientJarAsync(
        VersionRepository repository,
        GameDirectory game,
        VersionDetail version,
        CancellationToken cancellationToken)
    {
        var currentId = version.Id;

        for (var depth = 0; depth < 16 && !string.IsNullOrWhiteSpace(currentId); depth++)
        {
            var candidate = game.GetVersionJarPath(currentId);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var detail = await repository.GetVersionDetailAsync(currentId, false, cancellationToken).ConfigureAwait(false);
            currentId = detail.InheritsFrom;
        }

        // 找不到就返回当前版本路径，让预检给出明确错误。
        return game.GetVersionJarPath(version.Id);
    }

    // ---------------------------------------------------------------- 补全

    private static async Task EnsureLibrariesAsync(
        LibraryResolution resolution,
        IDownloadEngine engine,
        int concurrency,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (resolution.Downloads.Count == 0)
        {
            return;
        }

        Report(progress, LaunchStage.Downloading, 0, $"正在检查 {resolution.Downloads.Count} 个依赖库");

        var reporter = new PhaseProgress(value =>
            Report(progress, LaunchStage.Downloading, value * 0.6, $"正在下载依赖库（{value:P0}）"));

        var result = await DownloadBatchRunner
            .RunAsync(resolution.Downloads, engine, concurrency, reporter, cancellationToken)
            .ConfigureAwait(false);

        if (!result.AllSucceeded)
        {
            var first = result.Failures[0];
            throw new InvalidOperationException(
                $"依赖库下载失败 {result.FailedCount}/{result.Total} 个，首个失败：{Path.GetFileName(first.Task.SavePath)} — {first.Error.Message}",
                first.Error);
        }
    }

    private static async Task EnsureClientJarAsync(
        VersionDetail version,
        string jarPath,
        IDownloadEngine engine,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = version.Downloads?.Client;

        if (client?.Url is null || File.Exists(jarPath))
        {
            return;
        }

        Report(progress, LaunchStage.Downloading, 0.6, "正在下载客户端 jar");
        await engine.DownloadAsync(
            new DownloadTask
            {
                Url = client.Url,
                SavePath = jarPath,
                ExpectedSize = client.Size,
                Sha1 = client.Sha1,
            },
            new PhaseProgress(value =>
                Report(progress, LaunchStage.Downloading, 0.6 + (value * 0.1), "正在下载客户端 jar")),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAssetsAsync(
        GameDirectory game,
        VersionDetail version,
        IDownloadEngine engine,
        int concurrency,
        LauncherConfig config,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!config.CompleteAssets || version.AssetIndex is not { Url: not null } assetIndex)
        {
            return;
        }

        Report(progress, LaunchStage.Downloading, 0.7, "正在检查资源文件");

        using var assets = new AssetDownloader(game, _http);

        var index = await assets.GetIndexAsync(assetIndex, cancellationToken).ConfigureAwait(false);
        Report(progress, LaunchStage.Downloading, 0.7, $"资源索引 {index.Id}：{index.Objects.Count} 个对象");

        var result = await assets
            .DownloadAsync(
                index,
                engine,
                concurrency,
                limit: null,
                progress: new PhaseProgress(value =>
                    Report(progress, LaunchStage.Downloading, 0.7 + (value * 0.3), $"正在补全资源（{value:P0}）")),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.AllSucceeded)
        {
            // 资源缺失不该阻止启动：Minecraft 会自己降级（缺贴图/缺音效仍能进游戏），
            // 但要通过日志让用户知道。
            LogReceived?.Invoke($"[OML] 警告：{result.FailedCount} 个资源下载失败，游戏仍会启动。");
        }
    }

    // ---------------------------------------------------------------- Java

    private async Task<string> ResolveJavaAsync(
        LauncherConfig config,
        VersionDetail version,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.JavaPath) && File.Exists(config.JavaPath))
        {
            return config.JavaPath;
        }

        // 配置里的路径失效时不要直接失败：回退到自动探测，比让用户面对
        // "Java 路径无效" 的报错更有用。
        var required = version.JavaVersion?.MajorVersion ?? 8;
        var javaPath = await _javaScanner.FindSuitableJavaAsync(required, cancellationToken).ConfigureAwait(false);

        if (javaPath is null)
        {
            throw new InvalidOperationException(
                $"未找到 Java {required} 或更高版本。请安装 JDK {required}+ 或在配置中指定 JavaPath。");
        }

        return javaPath;
    }

    // ---------------------------------------------------------------- 退出

    private static async Task<GameExitInfo> WaitForExitCoreAsync(
        Process process,
        string crashDirectory,
        DateTime startedAtUtc,
        JvmLauncher launcher)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            // 让异步输出读取把尾部日志冲完，再释放日志写入器。
            await Task.Delay(50).ConfigureAwait(false);
            launcher.Dispose();
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        var crashReport = exitCode == 0 ? null : FindNewestCrashReport(crashDirectory, startedAtUtc);
        return new GameExitInfo(exitCode, crashReport);
    }

    /// <summary>
    /// 查找本次启动之后生成的崩溃报告。
    /// 只认"启动时间之后"的文件，避免把上一次的旧报告当成本次崩溃。
    /// </summary>
    private static string? FindNewestCrashReport(string crashDirectory, DateTime startedAtUtc)
    {
        if (!Directory.Exists(crashDirectory))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(crashDirectory, "*.txt")
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= startedAtUtc.AddSeconds(-5))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 工具

    private static void Report(IProgress<LaunchProgress>? progress, LaunchStage stage, double value, string message) =>
        progress?.Report(new LaunchProgress(stage, Math.Clamp(value, 0d, 1d), message));

    /// <summary>把 <c>IProgress&lt;double&gt;</c> 直接转成阶段回调（不回线程池，避免顺序错乱）。</summary>
    private sealed class PhaseProgress : IProgress<double>
    {
        private readonly Action<double> _onReport;

        public PhaseProgress(Action<double> onReport) => _onReport = onReport;

        public void Report(double value) => _onReport(Math.Clamp(value, 0d, 1d));
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
