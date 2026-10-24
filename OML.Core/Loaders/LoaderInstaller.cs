using System.IO;
using System.Net.Http;
using System.Text.Json;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Core.Downloads;
using OML.Core.Java;
using OML.Core.Launching;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Core.Loaders;

/// <summary>加载器安装结果。</summary>
/// <param name="VersionId">生成的版本 ID，例如 <c>fabric-loader-0.20.1-1.20.1</c>。</param>
/// <param name="Loader">加载器名（Fabric / Forge）。</param>
/// <param name="VersionJsonPath">生成的版本 JSON 路径。</param>
public sealed record LoaderInstallResult(string VersionId, string Loader, string VersionJsonPath);

/// <summary>
/// 模组加载器安装器。
/// <para>
/// <b>Fabric</b>：Fabric Meta 提供 <c>/profile/json</c>，直接返回一份完整的、带
/// <c>inheritsFrom</c> 的版本 JSON，落盘即可，不需要执行任何 jar。
/// 这比"下载 installer.jar 再跑 java -jar"更安全（不执行第三方代码）也更快。
/// </para>
/// <para>
/// <b>Forge</b>：官方没有等价的 profile 接口，只能下载 installer.jar 并执行
/// <c>--installClient</c>。安装器会自己下载依赖、生成 versions/&lt;id&gt;/&lt;id&gt;.json。
/// </para>
/// </summary>
public sealed class LoaderInstaller : IDisposable
{
    /// <summary>Fabric Meta 基地址。</summary>
    public const string FabricMetaBase = "https://meta.fabricmc.net/v2";

    /// <summary>Forge 版本提升列表。</summary>
    public const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    /// <summary>Forge Maven 基地址。</summary>
    public const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge/";

    private readonly GameDirectory _gameDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly JavaScanner _javaScanner = new();

    public LoaderInstaller(GameDirectory gameDirectory, HttpClient? httpClient = null)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
    }

    // ---------------------------------------------------------------- Fabric

    /// <summary>取该 MC 版本可用的最新 Fabric Loader 版本。</summary>
    public async Task<string?> GetLatestFabricLoaderAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(
            $"{FabricMetaBase}/versions/loader/{Uri.EscapeDataString(gameVersion)}",
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        // 优先稳定版。
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("stable", out var stable) &&
                stable.ValueKind == JsonValueKind.True)
            {
                return loader.GetProperty("version").GetString();
            }
        }

        return document.RootElement[0].GetProperty("loader").GetProperty("version").GetString();
    }

    /// <summary>
    /// 安装 Fabric：拉取 profile JSON 并写入 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>。
    /// </summary>
    public async Task<LoaderInstallResult> InstallFabricAsync(
        string gameVersion,
        string? loaderVersion = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        EnsureParentInstalled(gameVersion);

        progress?.Report(new LaunchProgress(LaunchStage.Checking, 0, "查询 Fabric Loader 版本"));
        var loader = loaderVersion ?? await GetLatestFabricLoaderAsync(gameVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Fabric 没有适用于 {gameVersion} 的 Loader 版本。");

        progress?.Report(new LaunchProgress(LaunchStage.Downloading, 0.3, $"获取 Fabric {loader} 的配置"));
        var json = await GetStringAsync(
            $"{FabricMetaBase}/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loader)}/profile/json",
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetString()
            ?? throw new InvalidDataException("Fabric profile 缺少 id。");
        var parent = root.TryGetProperty("inheritsFrom", out var inherits) ? inherits.GetString() : null;

        if (!string.Equals(parent, gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Fabric profile 的 inheritsFrom（{parent}）与目标版本 {gameVersion} 不一致。");
        }

        progress?.Report(new LaunchProgress(LaunchStage.Extracting, 0.8, "写入版本 JSON"));
        var jsonPath = _gameDirectory.GetVersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

        var tempPath = jsonPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, jsonPath, overwrite: true);

        progress?.Report(new LaunchProgress(LaunchStage.Completed, 1, $"Fabric {loader} 安装完成"));
        return new LoaderInstallResult(id, "Fabric", jsonPath);
    }

    // ---------------------------------------------------------------- Forge

    /// <summary>取该 MC 版本推荐的 Forge 版本（回退 latest）。</summary>
    public async Task<string?> GetLatestForgeVersionAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(ForgePromotionsUrl, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("promos", out var promos))
        {
            return null;
        }

        foreach (var key in new[] { $"{gameVersion}-recommended", $"{gameVersion}-latest" })
        {
            if (promos.TryGetProperty(key, out var value))
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// 安装 Forge：下载官方 installer.jar，用 Java 执行 <c>--installClient</c>，
    /// 然后从 versions/ 里找出新生成的版本。
    /// </summary>
    public async Task<LoaderInstallResult> InstallForgeAsync(
        string gameVersion,
        string? forgeVersion = null,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        EnsureParentInstalled(gameVersion);

        progress?.Report(new LaunchProgress(LaunchStage.Checking, 0, "查询 Forge 版本"));
        var forge = forgeVersion ?? await GetLatestForgeVersionAsync(gameVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"没有找到 {gameVersion} 的 Forge 版本。");

        var installerUrl = $"{ForgeMavenBase}{gameVersion}-{forge}/forge-{gameVersion}-{forge}-installer.jar";
        var installerPath = Path.Combine(Path.GetTempPath(), $"oml-forge-{gameVersion}-{forge}-installer.jar");

        var existingVersions = SnapshotVersionIds();

        using (var downloader = new PclStyleDownloader(_http))
        {
            progress?.Report(new LaunchProgress(LaunchStage.Downloading, 0.1, "下载 Forge 安装器"));
            await downloader.DownloadAsync(
                new DownloadTask { Url = installerUrl, SavePath = installerPath },
                new Progress<double>(v => progress?.Report(
                    new LaunchProgress(LaunchStage.Downloading, 0.1 + (v * 0.3), "下载 Forge 安装器"))),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new LaunchProgress(LaunchStage.Checking, 0.45, "查找可用的 Java"));
        var requiredMajor = 17;
        var javaPath = await _javaScanner.FindSuitableJavaAsync(requiredMajor, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"安装 Forge 需要 Java {requiredMajor}+，当前机器没有检测到。");

        progress?.Report(new LaunchProgress(LaunchStage.Launching, 0.5, "运行 Forge 安装器（可能需要几分钟）"));

        var arguments = new LaunchArguments
        {
            JavaPath = javaPath,
            WorkingDirectory = _gameDirectory.Root,
            Arguments = new[] { "-jar", installerPath, "--installClient", _gameDirectory.Root },
        };

        var logDirectory = Path.Combine(_gameDirectory.Root, "logs", "forge-install");
        using var launcher = new JvmLauncher(logDirectory);
        using var process = await launcher.LaunchAsync(arguments, cancellationToken).ConfigureAwait(false);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Forge 安装器退出码 {process.ExitCode}，日志见 {launcher.LogFilePath}");
        }

        progress?.Report(new LaunchProgress(LaunchStage.Extracting, 0.9, "检查生成的版本"));

        try
        {
            File.Delete(installerPath);
        }
        catch (IOException)
        {
            // 临时文件删不掉不影响安装结果。
        }

        var created = SnapshotVersionIds().Except(existingVersions, StringComparer.OrdinalIgnoreCase).ToArray();
        var forgeId = created.FirstOrDefault(id => id.Contains("forge", StringComparison.OrdinalIgnoreCase))
                      ?? created.FirstOrDefault();

        if (forgeId is null)
        {
            throw new InvalidOperationException("Forge 安装器执行完毕，但没有生成新的版本目录。");
        }

        progress?.Report(new LaunchProgress(LaunchStage.Completed, 1, $"Forge {forge} 安装完成"));
        return new LoaderInstallResult(forgeId, "Forge", _gameDirectory.GetVersionJsonPath(forgeId));
    }

    /// <summary>
    /// 加载器版本依赖父版本的文件（jar / libraries / assets），
    /// 父版本没装就装加载器，启动时必然缺 jar，提前给出明确提示。
    /// </summary>
    private void EnsureParentInstalled(string gameVersion)
    {
        if (!File.Exists(_gameDirectory.GetVersionJsonPath(gameVersion)))
        {
            throw new InvalidOperationException(
                $"请先安装原版 {gameVersion}，再安装加载器。");
        }
    }

    private HashSet<string> SnapshotVersionIds()
    {
        if (!Directory.Exists(_gameDirectory.VersionsDirectory))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory
            .EnumerateDirectories(_gameDirectory.VersionsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
