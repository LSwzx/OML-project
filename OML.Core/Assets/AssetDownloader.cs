using System.Net.Http;
using System.Text.Json;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;
using OML.Core.Downloads;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Core.Assets;

/// <summary>
/// 资源补全。
/// <para>
/// 资源是<b>内容寻址</b>的：文件名就是 SHA-1，落点固定为
/// <c>assets/objects/&lt;hash 前两位&gt;/&lt;hash&gt;</c>，
/// 因此不同版本、不同实例可以共享同一份文件，补全时只需跳过已存在项。
/// </para>
/// <para>
/// 1.21.1 的索引有 3000+ 个对象、约 800MB，必须限并发：
/// 否则几千个请求瞬间打到 BMCLAPI，换来的是一整屏 429。
/// </para>
/// </summary>
public sealed class AssetDownloader : IDisposable
{
    /// <summary>资源文件官方基地址。</summary>
    public const string AssetsBaseUrl = "https://resources.download.minecraft.net/";

    private readonly GameDirectory _gameDirectory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadSourceProvider _sources;
    private readonly bool _ownsSources;

    public AssetDownloader(
        GameDirectory gameDirectory,
        HttpClient? httpClient = null,
        IDownloadSourceProvider? sourceProvider = null)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();

        _ownsSources = sourceProvider is null;
        _sources = sourceProvider ?? new BmclApiSourceProvider();
    }

    /// <summary>
    /// 获取资源索引：优先本地缓存（<c>assets/indexes/&lt;id&gt;.json</c>），缺失时联网下载并缓存。
    /// </summary>
    public async Task<AssetIndex> GetIndexAsync(AssetIndex reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var indexPath = _gameDirectory.GetAssetIndexPath(reference.Id);

        if (File.Exists(indexPath))
        {
            var cached = await TryReadIndexAsync(indexPath, cancellationToken).ConfigureAwait(false);
            if (cached is { Objects.Count: > 0 })
            {
                return cached;
            }
        }

        if (string.IsNullOrWhiteSpace(reference.Url))
        {
            throw new InvalidDataException($"资源索引 {reference.Id} 既无本地缓存，也无下载地址。");
        }

        var baseUrl = await _sources.GetFastestSourceAsync(cancellationToken).ConfigureAwait(false);
        var url = PrefersMirror(baseUrl) ? BmclApiPathMapper.Map(reference.Url) : reference.Url;

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(indexPath, json, cancellationToken).ConfigureAwait(false);

        var index = JsonSerializer.Deserialize<AssetIndex>(json, OmlJsonOptions.Default)
            ?? throw new InvalidDataException($"资源索引解析失败：{indexPath}");

        return index;
    }

    /// <summary>
    /// 生成资源下载任务。
    /// </summary>
    /// <param name="index">资源索引（需含 objects）。</param>
    /// <param name="preferMirror">是否改写为 BMCLAPI 地址。</param>
    /// <param name="limit">只取前 N 个（按钮"补全前 20 个"之类的调试场景）。</param>
    /// <param name="onlyMissing">跳过本地已存在且大小一致的文件。</param>
    public IReadOnlyList<DownloadTask> CreateTasks(
        AssetIndex index,
        bool preferMirror = false,
        int? limit = null,
        bool onlyMissing = true)
    {
        ArgumentNullException.ThrowIfNull(index);

        var tasks = new List<DownloadTask>(index.Objects.Count);
        var count = 0;

        // 按虚拟路径排序，保证每次运行顺序一致（Dictionary 枚举顺序不保证）。
        foreach (var (_, asset) in index.Objects.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(asset.Hash) || asset.Hash.Length < 2)
            {
                continue;
            }

            if (limit is not null && count >= limit.Value)
            {
                break;
            }

            var savePath = GetObjectPath(asset.Hash);
            if (onlyMissing && IsPresent(savePath, asset.Size))
            {
                continue;
            }

            var url = $"{AssetsBaseUrl}{asset.Hash[..2]}/{asset.Hash}";
            tasks.Add(new DownloadTask
            {
                Url = preferMirror ? BmclApiPathMapper.Map(url) : url,
                SavePath = savePath,
                ExpectedSize = asset.Size,
                Sha1 = asset.Hash,
            });

            count++;
        }

        return tasks;
    }

    /// <summary>
    /// 补全资源文件。
    /// </summary>
    /// <param name="index">资源索引。</param>
    /// <param name="engine">下载引擎。</param>
    /// <param name="maxConcurrency">并发上限，默认 16。</param>
    /// <param name="limit">只补全前 N 个。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="preferMirror">强制指定源；为 <c>null</c> 时按测速结果决定。</param>
    /// <param name="retryPasses">
    /// 失败重试轮数。镜像站在并发高峰返回 429 是常态，
    /// 单轮失败直接放弃会让"补全 3900 个资源"必然留下缺口。
    /// </param>
    public async Task<BatchDownloadResult> DownloadAsync(
        AssetIndex index,
        IDownloadEngine engine,
        int maxConcurrency = DownloadBatchRunner.DefaultMaxConcurrency,
        int? limit = null,
        IProgress<double>? progress = null,
        bool? preferMirror = null,
        int retryPasses = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegative(retryPasses);

        var mirror = preferMirror ?? PrefersMirror(
            await _sources.GetFastestSourceAsync(cancellationToken).ConfigureAwait(false));

        var tasks = CreateTasks(index, mirror, limit);
        var total = tasks.Count;
        var pending = tasks;
        var failures = new List<DownloadFailure>();

        for (var pass = 0; pass <= retryPasses && pending.Count > 0; pass++)
        {
            var result = await DownloadBatchRunner
                .RunAsync(pending, engine, maxConcurrency, progress, cancellationToken)
                .ConfigureAwait(false);

            failures = result.Failures.ToList();
            pending = failures.Select(f => f.Task).ToList();

            if (pending.Count > 0 && pass < retryPasses)
            {
                // 限流窗口通常以秒计，退避后整批重试比逐个重试更省事。
                await Task.Delay(TimeSpan.FromSeconds(2 * (pass + 1)), cancellationToken).ConfigureAwait(false);
            }
        }

        return new BatchDownloadResult(total, total - failures.Count, failures);
    }

    /// <summary>资源对象的落点：<c>assets/objects/&lt;h2&gt;/&lt;hash&gt;</c>。</summary>
    public string GetObjectPath(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
        {
            throw new ArgumentException($"非法的资源 hash：{hash}", nameof(hash));
        }

        return Path.Combine(_gameDirectory.AssetsObjectsDirectory, hash[..2], hash);
    }

    private static bool PrefersMirror(string baseUrl) =>
        baseUrl.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);

    private static bool IsPresent(string path, long expectedSize)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // 大小一致就认为已存在：逐个算 SHA-1 在几千个资源上会把启动拖到无法接受。
        var info = new FileInfo(path);
        return expectedSize <= 0 || info.Length == expectedSize;
    }

    private static async Task<AssetIndex?> TryReadIndexAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AssetIndex>(json, OmlJsonOptions.Default);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        if (_ownsSources && _sources is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
