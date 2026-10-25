using System.Net.Http;
using System.Text.Json;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;
using OML.Core.Downloads;
using OML.Core.Serialization;
using OML.Core.IO;

namespace OML.Core.Versioning;

/// <summary>
/// 版本仓库：负责"拿到版本清单与版本详情"，并做本地缓存。
/// <para>缓存策略（PCL2 的"离网可用"）：</para>
/// <list type="bullet">
/// <item>清单：<c>versions/version_manifest_v2.json</c>，默认 30 分钟新鲜期；新鲜期内不联网。</item>
/// <item>详情：<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>，内容不可变，命中即用，永不因过期重下。</item>
/// <item>任何网络失败都会回退到本地缓存；无缓存才抛异常。</item>
/// </list>
/// <para>
/// 缓存目录与游戏目录是同一套结构，因此缓存文件本身就是可用的游戏文件，不做重复存储。
/// </para>
/// </summary>
public sealed class VersionRepository : IDisposable
{
    /// <summary>官方清单地址。</summary>
    public const string OfficialManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>继承链最大深度，防御畸形的 <c>inheritsFrom</c> 链。</summary>
    private const int MaxInheritanceDepth = 16;

    private static readonly TimeSpan DefaultManifestTtl = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadSourceProvider _sources;
    private readonly bool _ownsSources;
    private readonly TimeSpan _manifestTtl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VersionManifest? _manifest;

    /// <param name="gameDirectory">游戏目录（决定缓存落点）。</param>
    /// <param name="httpClient">可注入共享客户端；为 <c>null</c> 时自建。</param>
    /// <param name="sourceProvider">下载源提供者；为 <c>null</c> 时使用 BMCLAPI + 官方双源。</param>
    /// <param name="manifestTtl">清单缓存新鲜期，默认 30 分钟。</param>
    public VersionRepository(
        GameDirectory gameDirectory,
        HttpClient? httpClient = null,
        IDownloadSourceProvider? sourceProvider = null,
        TimeSpan? manifestTtl = null)
    {
        GameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();

        _ownsSources = sourceProvider is null;
        _sources = sourceProvider ?? new BmclApiSourceProvider();

        _manifestTtl = manifestTtl ?? DefaultManifestTtl;
    }

    /// <summary>游戏目录。</summary>
    public GameDirectory GameDirectory { get; }

    /// <summary>
    /// 获取版本清单（带缓存）。
    /// </summary>
    /// <param name="forceRefresh">强制联网刷新；仍失败时回退缓存。</param>
    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _manifest is not null)
        {
            return _manifest;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _manifest is not null)
            {
                return _manifest;
            }

            var json = await LoadManifestJsonAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            var manifest = Deserialize<VersionManifest>(json, GameDirectory.ManifestCachePath);
            _manifest = manifest;
            return manifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 获取单个版本的详情（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>），命中缓存则不联网。
    /// </summary>
    public async Task<VersionDetail> GetVersionDetailAsync(
        string versionId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var id = NormalizeId(versionId);
        var jsonPath = GameDirectory.GetVersionJsonPath(id);

        if (!forceRefresh && File.Exists(jsonPath))
        {
            var cached = await TryReadDetailAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        string? remoteJson = null;
        try
        {
            remoteJson = await DownloadVersionJsonAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken) && File.Exists(jsonPath))
        {
            // 断网 / 镜像挂了：回退旧缓存，宁可给用户一个能玩的旧数据也不直接崩。
        }

        if (remoteJson is not null)
        {
            await AtomicFile.WriteAllTextAsync(jsonPath, remoteJson, cancellationToken).ConfigureAwait(false);
            return Deserialize<VersionDetail>(remoteJson, jsonPath);
        }

        var fallback = await TryReadDetailAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        return fallback ?? throw new FileNotFoundException(
            $"无法获取版本 {id} 的详情：网络请求失败且本地无缓存（{jsonPath}）。", jsonPath);
    }

    /// <summary>
    /// 获取完成 <c>inheritsFrom</c> 合并后的"可启动版本"。
    /// 原版版本（无继承）等价于 <see cref="GetVersionDetailAsync"/>。
    /// </summary>
    public async Task<VersionDetail> GetResolvedVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var chain = new List<VersionDetail>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = await GetVersionDetailAsync(versionId, false, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (!seen.Add(current.Id))
            {
                throw new InvalidOperationException($"版本继承出现循环：{string.Join(" -> ", chain.Select(c => c.Id))} -> {current.Id}");
            }

            chain.Add(current);

            var parentId = current.InheritsFrom;
            if (string.IsNullOrWhiteSpace(parentId))
            {
                break;
            }

            if (chain.Count > MaxInheritanceDepth)
            {
                throw new InvalidOperationException($"版本继承链过深（>{MaxInheritanceDepth}）：{versionId}");
            }

            current = await GetVersionDetailAsync(parentId, false, cancellationToken).ConfigureAwait(false);
        }

        return VersionInheritor.MergeChain(chain);
    }

    /// <summary>枚举本地已缓存的版本 ID（扫描 <c>versions/*/*.json</c>）。</summary>
    public IReadOnlyList<string> EnumerateLocalVersionIds()
    {
        if (!Directory.Exists(GameDirectory.VersionsDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateDirectories(GameDirectory.VersionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id) && File.Exists(GameDirectory.GetVersionJsonPath(id!)))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ---------------------------------------------------------------- 内部

    private async Task<string> LoadManifestJsonAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var path = GameDirectory.ManifestCachePath;

        if (!forceRefresh && File.Exists(path) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < _manifestTtl)
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var url = await ResolveUrlAsync(OfficialManifestUrl, cancellationToken).ConfigureAwait(false);
            var json = await FetchStringAsync(url, cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            return json;
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken) && File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> DownloadVersionJsonAsync(string id, CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(false, cancellationToken).ConfigureAwait(false);

        var summary = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"版本清单中不存在版本 {id}。");

        if (string.IsNullOrWhiteSpace(summary.Url))
        {
            throw new InvalidDataException($"版本 {id} 的清单条目缺少 url 字段。");
        }

        var url = await ResolveUrlAsync(summary.Url, cancellationToken).ConfigureAwait(false);
        return await FetchStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把官方地址按当前最优源改写：镜像源就重写，官方源就原样。</summary>
    private async Task<string> ResolveUrlAsync(string officialUrl, CancellationToken cancellationToken)
    {
        var baseUrl = await _sources.GetFastestSourceAsync(cancellationToken).ConfigureAwait(false);
        var useMirror = baseUrl.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);
        return useMirror ? BmclApiPathMapper.Map(officialUrl) : officialUrl;
    }

    private async Task<string> FetchStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(string json, string sourcePath)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, OmlJsonOptions.Default)
                ?? throw new InvalidDataException($"JSON 内容为空：{sourcePath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSON 解析失败（{sourcePath}）：{ex.Message}", ex);
        }
    }

    private static async Task<VersionDetail?> TryReadDetailAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VersionDetail>(json, OmlJsonOptions.Default);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 区分"用户主动取消"与"连接超时"。
    /// <para>
    /// <c>SocketsHttpHandler.ConnectTimeout</c> 超时抛的是 <see cref="TaskCanceledException"/>（OCE 子类），
    /// 若一律当作取消再抛，断网/网络抖动时就无法回退缓存——而这正是离网可用要覆盖的场景。
    /// 只有调用方 token 真的被取消时才算取消。
    /// </para>
    /// </summary>
    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string NormalizeId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("版本 ID 不能为空。", nameof(versionId));
        }

        return versionId.Trim();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        if (_ownsSources && _sources is IDisposable disposableSources)
        {
            disposableSources.Dispose();
        }

        _gate.Dispose();
    }
}
