using System.IO;
using Velopack;
using Velopack.Sources;

namespace OML.Desktop.Infrastructure;

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
        if (_manager is null || !_manager.IsInstalled)
        {
            return null;
        }

        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
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
