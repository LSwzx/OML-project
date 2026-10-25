using System.IO;
using System.Security.Cryptography;
using OML.Abstractions.Configuration;
using Velopack;
using Velopack.Sources;

namespace OML.Desktop.Infrastructure;

/// <summary>检查更新的结果状态。</summary>
public enum UpdateCheckStatus
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>一次更新检查的完整结果（含失败原因）。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null);

/// <summary>更新包校验失败（下载损坏）。</summary>
public sealed class UpdateIntegrityException : Exception
{
    public UpdateIntegrityException(string message) : base(message)
    {
    }
}

/// <summary>
/// Velopack 自动更新。
/// <para>
/// 更新源按 <see cref="LauncherConfig.UpdateSourceType"/> / <see cref="LauncherConfig.UpdateSourceUrl"/>
/// 动态生成，每次检查/下载都会重新读取配置，切换源后立即生效：
/// <list type="bullet">
/// <item>GitHub → <see cref="GithubSource"/></item>
/// <item>Custom / Mirror → <see cref="SimpleWebSource"/>（HTTP(S)）或 <see cref="SimpleFileSource"/>（本地目录）</item>
/// </list>
/// 环境变量 <c>OML_UPDATE_SOURCE</c> 优先级最高，用于本地联调。
/// </para>
/// <para>未通过 Velopack 安装（直接 dotnet run）时 <see cref="IsInstalled"/> 为 false，静默跳过检查。</para>
/// </summary>
public sealed class UpdateService
{
    private const string GitHubRepository = "https://github.com/LSwzx/OML_test";

    private readonly Func<LauncherConfig> _configProvider;

    public UpdateService(Func<LauncherConfig> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>是否由 Velopack 安装（开发期直接运行 exe 时为 false）。</summary>
    public bool IsInstalled => CreateManager()?.IsInstalled == true;

    /// <summary>当前版本（未安装时显示开发版）。</summary>
    public string CurrentVersion => CreateManager()?.CurrentVersion?.ToFullString() ?? "开发版";

    /// <summary>静默检查更新，失败/无更新返回 null。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckDetailedAsync(cancellationToken);
        return result.Update;
    }

    /// <summary>
    /// 显式检查更新（供"检查更新"按钮使用）：区分未安装 / 已最新 / 有新版本 / 网络失败。
    /// </summary>
    public async Task<UpdateCheckResult> CheckDetailedAsync(CancellationToken cancellationToken = default)
    {
        var manager = CreateManager();
        if (manager is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotInstalled, Error: "更新管理器初始化失败");
        }

        if (!manager.IsInstalled)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.NotInstalled,
                Error: "当前不是通过安装包安装的版本，无法检查更新。请使用安装包装后再试。");
        }

        try
        {
            var update = await manager.CheckForUpdatesAsync();
            return update is null
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate)
                : new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>后台下载更新并重启到新版本。</summary>
    /// <param name="update">待安装的更新。</param>
    /// <param name="progress">下载百分比。</param>
    /// <param name="status">等待锁/校验时的状态文案。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DownloadAndRestartAsync(
        UpdateInfo update,
        Action<int>? progress = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var manager = CreateManager()
            ?? throw new InvalidOperationException("更新管理器未初始化。");

        // Velopack 用安装目录下的独占锁文件串行化更新操作；另一个实例
        // （或上一次未退出的 Update.exe）占着锁时会抛 "exclusive lock file"。
        await WithLockRetryAsync(
            () => manager.DownloadUpdatesAsync(
                update,
                percent => progress?.Invoke(percent),
                cancellationToken),
            status,
            cancellationToken).ConfigureAwait(false);

        status?.Invoke("正在校验更新包…");
        await VerifyDownloadedPackageAsync(update, status, cancellationToken).ConfigureAwait(false);

        await WithLockRetryAsync(
            () =>
            {
                manager.ApplyUpdatesAndRestart(update);
                return Task.CompletedTask;
            },
            status,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>判断异常是否为 Velopack 更新锁冲突。</summary>
    public static bool IsLockError(Exception exception) =>
        exception.Message.Contains("exclusive lock", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("lock file", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains(".velopack_lock", StringComparison.OrdinalIgnoreCase);

    private const int MaxLockRetries = 5;

    private static async Task WithLockRetryAsync(
        Func<Task> action,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsLockError(ex) && attempt < MaxLockRetries)
            {
                var wait = TimeSpan.FromSeconds(5 * (attempt + 1));
                status?.Invoke(
                    $"另一个更新进程正在运行，{wait.TotalSeconds:0} 秒后重试（{attempt + 1}/{MaxLockRetries}）…");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 下载完成后校验安装包：优先 SHA-256，其次大小；不一致时删除坏包并抛错。
    /// 只下载了增量包时找不到完整包，退化为依赖 Velopack 内置校验。
    /// </summary>
    private static async Task VerifyDownloadedPackageAsync(
        UpdateInfo update,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var asset = update.TargetFullRelease;
        var packagesDir = FindPackagesDirectory();

        if (string.IsNullOrWhiteSpace(packagesDir))
        {
            return;
        }

        var packagePath = Path.Combine(packagesDir, asset.FileName);
        if (!File.Exists(packagePath))
        {
            status?.Invoke("未找到独立安装包，已使用 Velopack 内置校验");
            return;
        }

        if (!string.IsNullOrWhiteSpace(asset.SHA256))
        {
            await using var stream = File.OpenRead(packagePath);
            using var sha256 = SHA256.Create();
            var actual = Convert.ToHexString(
                await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));

            if (!actual.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(packagePath);
                throw new UpdateIntegrityException("更新包下载损坏，请检查网络后重试");
            }

            status?.Invoke("更新包校验通过");
            return;
        }

        if (asset.Size > 0 && new FileInfo(packagePath).Length != asset.Size)
        {
            TryDelete(packagePath);
            throw new UpdateIntegrityException("更新包下载损坏，请检查网络后重试");
        }
    }

    /// <summary>
    /// 定位 Velopack 的 packages 目录。
    /// 安装版从 <c>&lt;root&gt;\current</c> 运行，包在 <c>&lt;root&gt;\packages</c>；
    /// 便携版则直接在应用目录下。
    /// </summary>
    private static string? FindPackagesDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var root = string.Equals(Path.GetFileName(baseDirectory), "current", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(baseDirectory)
            : baseDirectory;

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var packages = Path.Combine(root, "packages");
        return Directory.Exists(packages) ? packages : null;
    }

    private static void TryDelete(string path)    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 文件被占用时留给下一次下载覆盖。
        }
    }

    private UpdateManager? CreateManager()
    {
        try
        {
            return new UpdateManager(CreateSource(_configProvider()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>根据配置生成更新源；环境变量 <c>OML_UPDATE_SOURCE</c> 优先级最高。</summary>
    public static IUpdateSource CreateSource(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var custom = Environment.GetEnvironmentVariable("OML_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return CreateCustomSource(custom) ?? new GithubSource(GitHubRepository, null, false);
        }

        if (config.UpdateSourceType == UpdateSourceType.Github)
        {
            return new GithubSource(GitHubRepository, accessToken: null, prerelease: false);
        }

        var configuredUrl = config.UpdateSourceUrl?.Trim();
        return CreateCustomSource(configuredUrl) ?? new GithubSource(GitHubRepository, null, false);
    }

    private static IUpdateSource? CreateCustomSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new SimpleWebSource(uri.ToString());
        }

        // 本地目录：vpk pack 产出的 releases 目录，便于离线联调。
        return Directory.Exists(url) ? new SimpleFileSource(new DirectoryInfo(url)) : null;
    }
}
