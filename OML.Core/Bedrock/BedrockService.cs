using OML.Core.Downloads;

namespace OML.Core.Bedrock;

/// <summary>
/// 基岩版门面：探测、解析、下载、安装、授权与启动。
/// <para>
/// 基岩版通过 Microsoft Store 分发；下载的安装包需要当前账号拥有授权才能安装/运行，
/// 因此 UI 在下载前与启动前都会调用 <see cref="BedrockEntitlementService"/>。
/// </para>
/// </summary>
public sealed class BedrockService : IDisposable
{
    private readonly string _launcherRoot;

    public BedrockService(string launcherRoot, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        _launcherRoot = launcherRoot;

        Locator = new BedrockPackageLocator();
        Store = new BedrockStoreClient(httpClient);
        Downloader = new BedrockDownloader(httpClient);
        Installer = new BedrockInstaller();
        Entitlements = new BedrockEntitlementService(httpClient);
        Launcher = new BedrockLauncher();
    }

    public BedrockPackageLocator Locator { get; }

    public BedrockStoreClient Store { get; }

    public BedrockDownloader Downloader { get; }

    public BedrockInstaller Installer { get; }

    public BedrockEntitlementService Entitlements { get; }

    public BedrockLauncher Launcher { get; }

    /// <summary>当前通道的缓存目录。</summary>
    public string GetCacheDirectory(BedrockChannel channel) =>
        Path.Combine(_launcherRoot, "bedrock", channel.ToString().ToLowerInvariant());

    /// <summary>解析、下载并安装指定通道的最新版本。</summary>
    public async Task<BedrockInstallResult> InstallLatestAsync(
        BedrockChannel channel,
        IProgress<double>? progress = null,
        Action<DownloadLogEntry>? log = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        status?.Invoke("正在解析 Microsoft Store 安装包…");

        var files = await Store.GetPackageFilesAsync(channel, cancellationToken).ConfigureAwait(false);

        status?.Invoke($"找到 {files.Count} 个安装包，开始下载…");

        var downloaded = await Downloader.DownloadAsync(
            files,
            GetCacheDirectory(channel),
            progress,
            log,
            status,
            cancellationToken).ConfigureAwait(false);

        await Installer.InstallAsync(
            downloaded.Select(item => item.Path).ToArray(),
            status,
            cancellationToken).ConfigureAwait(false);

        var installation = await Locator.GetInstalledAsync(channel, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("安装命令已完成，但没有检测到基岩版，请查看 Windows 应用列表。");

        return new BedrockInstallResult(installation, downloaded.Count);
    }

    public void Dispose()
    {
        Store.Dispose();
        Downloader.Dispose();
        Entitlements.Dispose();
    }
}
