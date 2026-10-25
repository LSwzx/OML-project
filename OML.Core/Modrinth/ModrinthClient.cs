using System.Net;
using System.Net.Http;
using System.Text.Json;
using OML.Abstractions.Modrinth;
using OML.Core.Serialization;
using OML.Core.Downloads;

namespace OML.Core.Modrinth;

/// <summary>
/// Modrinth 开放 API 客户端（无需 API Key）。
/// <para>
/// 注意两点：Modrinth 的 JSON 是 <c>snake_case</c>；他们的服务条款要求带可识别的 User-Agent。
/// </para>
/// </summary>
public sealed class ModrinthClient : IDisposable
{
    /// <summary>API 根地址。</summary>
    public const string DefaultBaseUrl = "https://api.modrinth.com/v2";

    private static readonly JsonSerializerOptions JsonOptions = new(OmlJsonOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _userAgent;

    public ModrinthClient(HttpClient? httpClient = null, string? baseUrl = null, string? userAgent = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? OmlHttpClientFactory.UserAgent : userAgent;
    }

    /// <summary>
    /// 搜索资源。
    /// </summary>
    /// <param name="query">关键词。</param>
    /// <param name="limit">返回数量（1-100）。</param>
    /// <param name="offset">分页偏移。</param>
    /// <param name="projectType">资源类型：<c>mod</c> / <c>modpack</c> / <c>resourcepack</c> / <c>shader</c>。</param>
    /// <param name="gameVersion">Minecraft 版本，例如 <c>1.20.1</c>。</param>
    /// <param name="loader">加载器，例如 <c>fabric</c> / <c>forge</c>。</param>
    public async Task<ModrinthSearchResponse> SearchAsync(
        string query,
        int limit = 10,
        int offset = 0,
        string? projectType = null,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var facets = BuildFacets(projectType, gameVersion, loader);
        var url = $"{_baseUrl}/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&index=relevance";

        if (facets is not null)
        {
            url += $"&facets={Uri.EscapeDataString(facets)}";
        }

        return await GetAsync<ModrinthSearchResponse>(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取项目详情（支持 ID 或 slug）。</summary>
    public async Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrSlug);
        return await GetAsync<ModrinthProject>(
            $"{_baseUrl}/project/{Uri.EscapeDataString(idOrSlug)}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取项目版本列表（可按游戏版本与加载器过滤），API 默认按发布时间倒序。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string idOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrSlug);

        var url = $"{_baseUrl}/project/{Uri.EscapeDataString(idOrSlug)}/version";
        var parameters = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parameters.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            parameters.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");
        }

        if (parameters.Count > 0)
        {
            url += "?" + string.Join("&", parameters);
        }

        var versions = await GetAsync<List<ModrinthVersion>>(url, cancellationToken).ConfigureAwait(false);
        return versions;
    }

    /// <summary>
    /// 取指定 MC 版本 + 加载器的最新可用版本。
    /// <paramref name="preferRelease"/> 默认 false：Modrinth 的版本列表按发布时间倒序，
    /// 首个元素就是最新版。若优先 formal release，像 JEI 这种主要以 beta 发布的模组
    /// 会一路退回到很老的正式版（实测退到 15.56，连依赖声明都没有）。
    /// </summary>
    public async Task<ModrinthVersion?> GetLatestVersionAsync(
        string idOrSlug,
        string gameVersion,
        string? loader,
        bool preferRelease = false,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(idOrSlug, gameVersion, loader, cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return null;
        }

        if (!preferRelease)
        {
            return versions[0];
        }

        return versions.FirstOrDefault(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
               ?? versions[0];
    }

    /// <summary>取版本里的主文件（<c>primary=true</c>，没有则取第一个）。</summary>
    public static ModrinthVersionFile? GetPrimaryFile(ModrinthVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
    }

    /// <summary>
    /// 构造 Modrinth 的 facets 表达式：
    /// <c>[["project_type:mod"],["versions:1.20.1"],["categories:fabric"]]</c>
    /// </summary>
    public static string? BuildFacets(string? projectType, string? gameVersion, string? loader)
    {
        var facets = new List<string>();

        if (!string.IsNullOrWhiteSpace(projectType))
        {
            facets.Add($"[\"project_type:{projectType}\"]");
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            facets.Add($"[\"categories:{loader}\"]");
        }

        return facets.Count == 0 ? null : $"[{string.Join(",", facets)}]";
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions)
                       ?? throw new InvalidDataException($"Modrinth 返回空响应：{url}");
            }

            // 429 / 5xx 值得重试；其余（404 等）直接抛出，便于上层提示"项目不存在"。
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode >= 500;

            if (!retryable || attempt == maxAttempts - 1)
            {
                throw new HttpRequestException(
                    $"Modrinth 请求失败（HTTP {(int)response.StatusCode}）：{Trim(body)}", null, response.StatusCode);
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1.5 * (attempt + 1));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException($"Modrinth 请求失败：{url}");
    }

    private static string Trim(string body) => body.Length <= 300 ? body : body[..300];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
