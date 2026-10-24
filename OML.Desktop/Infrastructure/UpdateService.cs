using System.IO;
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

/// <summary>
/// Velopack 自动更新。
/// <para>
/// 更新源优先级：
/// 1. 环境变量 <c>OML_UPDATE_SOURCE</c>（http(s) 地址 → SimpleWebSource；本地目录 → SimpleFileSource），
///    用于本地联调，不用真发版；
/// 2. 默认 GitHub Releases（GithubSource）。
/// </para>
/// <para>未通过 Velopack 安装（直接 dotnet run）时 <see cref="IsInstalled"/> 为 false，静默跳过检查。</para>
/// </summary>
public sealed class UpdateService
{
    private const string GitHubRepository = "https://github.com/LSwzx/OML_test";

    private readonly UpdateManager? _manager;

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(CreateSource());
        }
        catch
        {
            _manager = null;
        }
    }

    /// <summary>是否由 Velopack 安装（开发期直接运行 exe 时为 false）。</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>当前版本（未安装时显示开发版）。</summary>
    public string CurrentVersion => _manager?.CurrentVersion?.ToFullString() ?? "开发版";

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
        if (_manager is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotInstalled, Error: "更新管理器初始化失败");
        }

        if (!_manager.IsInstalled)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.NotInstalled,
                Error: "当前不是通过安装包安装的版本，无法检查更新。请使用 Setup 或 MSI 安装后再试。");
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync();
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
    public async Task DownloadAndRestartAsync(
        UpdateInfo update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("更新管理器未初始化。");
        }

        await _manager.DownloadUpdatesAsync(
            update,
            percent => progress?.Invoke(percent),
            cancellationToken);

        _manager.ApplyUpdatesAndRestart(update);
    }

    private static IUpdateSource CreateSource()
    {
        var custom = Environment.GetEnvironmentVariable("OML_UPDATE_SOURCE");
        if (string.IsNullOrWhiteSpace(custom))
        {
            return new GithubSource(GitHubRepository, accessToken: null, prerelease: false);
        }

        if (Uri.TryCreate(custom, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new SimpleWebSource(uri.ToString());
        }

        // 本地目录：vpk pack 产出的 releases 目录。
        return new SimpleFileSource(new DirectoryInfo(custom));
    }
}
