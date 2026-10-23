using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using OML.Abstractions.Downloads;
using OML.Core.Downloads;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Core.Java;

/// <summary>已安装的 Java 运行时。</summary>
public sealed record JavaRuntimeInfo(
    string Component,
    string Version,
    string JavaPath,
    string Directory,
    int FileCount);

/// <summary>
/// 从 Mojang 官方下载 Java 运行时（PCL2 的"自动补 Java"）。
/// <para>
/// Mojang 不再以单个压缩包分发 Java，而是给出一份<b>逐文件清单</b>：
/// <c>all.json</c> → 每个组件一份 manifest → 每个文件一个 <c>piston-data</c> 地址。
/// 因此这里的"解压"实际上是把上百个小文件按相对路径铺到 <c>runtimes/&lt;component&gt;/</c>，
/// 不需要处理 zip/lzma。
/// </para>
/// </summary>
public sealed class JavaRuntimeDownloader : IDisposable
{
    /// <summary>官方的 Java 运行时索引（所有平台、所有组件）。</summary>
    public const string IndexUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private const int DownloadConcurrency = 12;

    private readonly string _launcherRoot;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadEngine _engine;
    private readonly bool _ownsEngine;
    private readonly Func<string, string> _urlMapper;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="launcherRoot">启动器根目录，运行时会落到 <c>&lt;root&gt;/runtimes/&lt;component&gt;</c>。</param>
    /// <param name="httpClient">可注入。</param>
    /// <param name="downloadEngine">可注入。</param>
    /// <param name="urlMapper">URL 改写（接镜像时传 <see cref="BmclApiPathMapper.Map"/>）。</param>
    public JavaRuntimeDownloader(
        string launcherRoot,
        HttpClient? httpClient = null,
        IDownloadEngine? downloadEngine = null,
        Func<string, string>? urlMapper = null)
    {
        if (string.IsNullOrWhiteSpace(launcherRoot))
        {
            throw new ArgumentException("启动器根目录不能为空。", nameof(launcherRoot));
        }

        _launcherRoot = Path.GetFullPath(launcherRoot);
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _ownsEngine = downloadEngine is null;
        _engine = downloadEngine ?? new PclStyleDownloader(_http);
        _urlMapper = urlMapper ?? (url => url);
    }

    /// <summary>运行时的安装目录。</summary>
    public string GetRuntimeDirectory(string component) =>
        Path.Combine(_launcherRoot, "runtimes", component);

    /// <summary>运行时里 java 可执行文件的路径。</summary>
    public string GetJavaPath(string component) =>
        Path.Combine(GetRuntimeDirectory(component), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    /// <summary>运行时是否已安装（只检查可执行文件存在）。</summary>
    public bool IsInstalled(string component) => File.Exists(GetJavaPath(component));

    /// <summary>
    /// 下载并落盘一个运行时组件。
    /// </summary>
    /// <param name="component">组件名，例如 <c>java-runtime-gamma</c>、<c>java-runtime-delta</c>。</param>
    /// <param name="progress">0.0 - 1.0。</param>
    public async Task<JavaRuntimeInfo> DownloadAsync(
        string component,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var platform = GetPlatformKey(PlatformInfo.Current);
            var runtimeDirectory = GetRuntimeDirectory(component);

            var indexJson = await GetOrFetchAsync(
                Path.Combine(_launcherRoot, "runtimes", "java-runtime-index.json"),
                IndexUrl,
                cancellationToken).ConfigureAwait(false);

            var (manifestUrl, versionName) = ResolveComponent(indexJson, platform, component);

            var manifestJson = await GetOrFetchAsync(
                Path.Combine(_launcherRoot, "runtimes", "cache", $"{component}-{platform}.json"),
                manifestUrl,
                cancellationToken).ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize<RuntimeManifest>(manifestJson, OmlJsonOptions.Default)
                ?? throw new InvalidDataException($"Java 运行时清单解析失败：{component}");

            Directory.CreateDirectory(runtimeDirectory);

            var tasks = new List<DownloadTask>(manifest.Files.Count);
            foreach (var (relativePath, file) in manifest.Files)
            {
                if (!string.Equals(file.Type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var raw = file.Downloads?.Raw;
                if (raw?.Url is null)
                {
                    continue;
                }

                var destination = SafeCombine(runtimeDirectory, relativePath);
                if (destination is null)
                {
                    continue;
                }

                tasks.Add(new DownloadTask
                {
                    Url = _urlMapper(raw.Url),
                    SavePath = destination,
                    ExpectedSize = raw.Size,
                    Sha1 = raw.Sha1,
                });
            }

            if (tasks.Count == 0)
            {
                throw new InvalidDataException($"Java 运行时 {component} 没有可下载文件。");
            }

            var result = await DownloadBatchRunner
                .RunAsync(tasks, _engine, DownloadConcurrency, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!result.AllSucceeded)
            {
                var first = result.Failures[0];
                throw new InvalidOperationException(
                    $"Java 运行时下载失败 {result.FailedCount}/{result.Total} 个文件，首个失败：{Path.GetFileName(first.Task.SavePath)} — {first.Error.Message}",
                    first.Error);
            }

            var javaPath = GetJavaPath(component);
            if (!File.Exists(javaPath))
            {
                throw new FileNotFoundException($"Java 运行时 {component} 下载完成但缺少 java 可执行文件：{javaPath}", javaPath);
            }

            return new JavaRuntimeInfo(component, versionName, javaPath, runtimeDirectory, tasks.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把当前平台映射成 Mojang 清单里的平台键。</summary>
    public static string GetPlatformKey(PlatformInfo platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var arch = PlatformInfo.NormalizeArch(platform.Arch);

        return platform.OsName switch
        {
            "windows" => arch switch
            {
                "x86_64" => "windows-x64",
                "x86" => "windows-x86",
                "arm64" => "windows-arm64",
                _ => "windows-x64",
            },
            "osx" => arch == "arm64" ? "mac-os-arm64" : "mac-os",
            "linux" => arch == "x86" ? "linux-i386" : "linux",
            _ => throw new PlatformNotSupportedException($"不支持的平台：{platform.OsName}/{platform.Arch}"),
        };
    }

    private static (string ManifestUrl, string VersionName) ResolveComponent(string indexJson, string platform, string component)
    {
        using var document = JsonDocument.Parse(indexJson);
        var root = document.RootElement;

        if (!root.TryGetProperty(platform, out var platformNode))
        {
            throw new InvalidOperationException($"Java 运行时清单中没有平台 {platform}。");
        }

        if (!platformNode.TryGetProperty(component, out var componentNode) ||
            componentNode.ValueKind != JsonValueKind.Array ||
            componentNode.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Java 运行时清单中没有组件 {platform}/{component}。");
        }

        var entry = componentNode[0];
        var manifestUrl = entry.GetProperty("manifest").GetProperty("url").GetString()
            ?? throw new InvalidDataException($"组件 {component} 缺少 manifest.url。");

        var versionName = entry.TryGetProperty("version", out var version) &&
                          version.TryGetProperty("name", out var name)
            ? name.GetString() ?? "unknown"
            : "unknown";

        return (manifestUrl, versionName);
    }

    private async Task<string> GetOrFetchAsync(string cachePath, string url, CancellationToken cancellationToken)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 缓存坏了就重新下载。
            }
        }

        using var response = await _http
            .GetAsync(_urlMapper(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var tempPath = cachePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, cachePath, overwrite: true);
        return json;
    }

    private static string? SafeCombine(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        if (_ownsEngine)
        {
            (_engine as IDisposable)?.Dispose();
        }

        _gate.Dispose();
    }

    // ---------------------------------------------------------------- 清单模型

    private sealed class RuntimeManifest
    {
        [JsonPropertyName("files")]
        public Dictionary<string, RuntimeFile> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RuntimeFile
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("executable")]
        public bool Executable { get; set; }

        [JsonPropertyName("downloads")]
        public RuntimeDownloads? Downloads { get; set; }
    }

    private sealed class RuntimeDownloads
    {
        [JsonPropertyName("raw")]
        public RuntimeArtifact? Raw { get; set; }

        [JsonPropertyName("lzma")]
        public RuntimeArtifact? Lzma { get; set; }
    }

    private sealed class RuntimeArtifact
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
