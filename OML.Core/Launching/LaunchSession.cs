using System.Diagnostics;
using System.Net.Http;
using OML.Abstractions.Auth;
using OML.Abstractions.Configuration;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Assets;
using OML.Core.Downloads;
using OML.Core.Java;
using OML.Core.Versioning;
using static OML.Core.Launching.LaunchProgressReporter;

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
    private readonly string _launcherRoot;

    public LaunchSession(HttpClient? httpClient = null, IDownloadEngine? downloadEngine = null, string? launcherRoot = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _downloadEngine = downloadEngine;
        _launcherRoot = launcherRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OML");
    }

    /// <summary>游戏日志行（stdout + stderr），供 UI 追加到控制台。</summary>
    public event Action<string>? LogReceived;

    /// <summary>下载重试/失败的诊断日志（供 UI 日志面板）。</summary>
    public Action<DownloadLogEntry>? DownloadLog { get; set; }

    /// <summary>下载实时状态（"正在重试下载…"），供全局状态栏。</summary>
    public Action<string>? DownloadStatus { get; set; }

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

#if STRICT_ONLINE_ONLY
        // 合规构建：绝不允许以离线 / 外置账号启动。
        if (!string.Equals(config.UserProfile.Type, AuthTypes.Microsoft, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前版本仅支持微软正版账号，请先在账号页登录。");
        }
#endif

        var versionId = config.SelectedVersion.Trim();
        var game = GameDirectory.FromConfig(config.GameDirectory, config.VersionIsolation);

        Report(progress, LaunchStage.Preparing, 0, $"准备启动 {versionId}");
        Directory.CreateDirectory(game.Root);

        using var repository = new VersionRepository(game, _http);
        var engine = _downloadEngine ?? new PclStyleDownloader(_http);
        if (engine is PclStyleDownloader pcl)
        {
            pcl.Log = DownloadLog;
            pcl.Status = DownloadStatus;
        }

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

            var javaPath = await ResolveJavaAsync(config, version, progress, cancellationToken).ConfigureAwait(false);

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
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var required = version.JavaVersion?.MajorVersion ?? 8;
        var component = version.JavaVersion?.Component ?? "java-runtime-gamma";
        string? javaPath = null;

        if (!string.IsNullOrWhiteSpace(config.JavaPath) && File.Exists(config.JavaPath))
        {
            // 配置里的路径也必须校验主版本：用户之前自动下载过 Java 17，
            // 之后再启动 1.21（需要 21）时不能继续用旧运行时，否则会以
            // UnsupportedClassVersionError 直接退出。
            var configuredMajor = await _javaScanner
                .GetJavaMajorVersionAsync(config.JavaPath, cancellationToken)
                .ConfigureAwait(false);

            if (configuredMajor is not null && configuredMajor >= required)
            {
                javaPath = config.JavaPath;
            }
        }

        if (javaPath is null)
        {
            // 配置不可用（未设置 / 文件不存在 / 版本过低）时回退到自动探测。
            javaPath = await _javaScanner.FindSuitableJavaAsync(required, cancellationToken).ConfigureAwait(false);
        }

        // 探测到的是 32 位 Java 且内存超过 2048MB 时，也尝试下载 64 位运行时：
        // 32 位 JVM 会直接抛 "Invalid maximum heap size" 并退出。
        var tooSmall = javaPath is not null &&
                       config.MaxMemoryMB > 2048 &&
                       await _javaScanner.Is32BitAsync(javaPath, cancellationToken).ConfigureAwait(false);

        if (javaPath is null || tooSmall)
        {
            progress?.Report(new LaunchProgress(
                LaunchStage.Downloading, 0.02,
                javaPath is null
                    ? $"未检测到 Java {required}，正在自动下载 {component}"
                    : $"当前 Java 是 32 位，正在下载 64 位 {component}"));

            try
            {
                using var downloader = new JavaRuntimeDownloader(_launcherRoot);
                var runtime = await downloader.DownloadAsync(
                    component,
                    new Progress<double>(v => progress?.Report(new LaunchProgress(
                        LaunchStage.Downloading,
                        0.02 + v * 0.25,
                        $"下载 Java {component}：{v:P0}"))),
                    cancellationToken).ConfigureAwait(false);

                javaPath = runtime.JavaPath;
            }
            catch (Exception ex)
            {
                // 完全没有 Java 才算致命；32 位回退失败时交给下面的拦截提示。
                if (javaPath is null)
                {
                    throw new InvalidOperationException(
                        $"缺少 Java {required}，自动下载失败：{ex.Message}\n\n请在设置里手动指定 Java 路径。", ex);
                }
            }
        }

        if (javaPath is null)
        {
            throw new InvalidOperationException(
                $"缺少 Java {required}，自动下载失败。请在设置里手动指定 Java 路径。");
        }

        // 32 位 JVM 最大堆通常只有 1.5-2GB，超过 2048MB 会直接抛
        // "Invalid maximum heap size" 并退出，启动前必须拦截。
        if (config.MaxMemoryMB > 2048 &&
            await _javaScanner.Is32BitAsync(javaPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"当前使用的是 32 位 Java：\n{javaPath}\n\n32 位 JVM 最多只能分配 2048MB 内存（当前设置 {config.MaxMemoryMB}MB）。\n请改用 64 位 Java，或在设置里把内存调整到 2048MB 以内。");
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
