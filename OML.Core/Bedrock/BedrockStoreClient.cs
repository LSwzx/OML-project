using System.Net.Http;
using System.Text.RegularExpressions;
using OML.Core.Downloads;

namespace OML.Core.Bedrock;

/// <summary>
/// 通过 store.rg-adguard.net 解析 Microsoft Store 的 MSIX 直链。
/// <para>
/// 微软没有公开的 Store 下载 API；社区通用做法是把商品页交给该解析服务，
/// 再从其返回的 HTML 表格里提取 <c>.msix</c> / <c>.appx</c> 链接。
/// 这里只负责"拿到候选文件列表"，过滤架构与识别主包在下层完成。
/// </para>
/// </summary>
public sealed class BedrockStoreClient : IDisposable
{
    private const string ResolverUrl = "https://store.rg-adguard.net/api/GetFiles";

    /// <summary>Minecraft for Windows（正式版）的 Store 商品 ID。</summary>
    public const string ReleaseProductId = "9NBLGGH2JHXJ";

    /// <summary>Minecraft Preview 的 Store 商品 ID。</summary>
    public const string PreviewProductId = "9P5X4QVLC2XR";

    private static readonly Regex LinkPattern = new(
        "<a\\s+href=\"(?<url>[^\"]+)\"[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] PackageExtensions =
    {
        ".msix", ".msixbundle", ".appx", ".appxbundle",
    };

    private static readonly string[] ExcludedArchitectures = { "arm64", "arm", "x86", "x86_64" };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public BedrockStoreClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
    }

    public static string GetProductId(BedrockChannel channel) =>
        channel == BedrockChannel.Preview ? PreviewProductId : ReleaseProductId;

    private static string GetProductPageUrl(BedrockChannel channel) =>
        channel == BedrockChannel.Preview
            ? "https://apps.microsoft.com/detail/9P5X4QVLC2XR"
            : "https://www.microsoft.com/en-us/p/minecraft-for-windows/9nblggh2jhxj";

    /// <summary>解析指定通道的安装包列表（主包 + 依赖）。</summary>
    public async Task<IReadOnlyList<BedrockDownloadFile>> GetPackageFilesAsync(
        BedrockChannel channel,
        CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"] = "url",
            ["url"] = GetProductPageUrl(channel),
            ["lang"] = "en-US",
            ["ring"] = "Retail",
            ["skip"] = "false",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolverUrl)
        {
            Content = content,
        };

        // rg-adguard 对非浏览器请求直接 403；带上 Origin/Referer 与常见 UA 才放行。
        request.Headers.TryAddWithoutValidation("Origin", "https://store.rg-adguard.net");
        request.Headers.TryAddWithoutValidation("Referer", "https://store.rg-adguard.net/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"解析 Microsoft Store 失败（HTTP {(int)response.StatusCode}）。请稍后重试或更换网络。");
        }

        var files = new List<BedrockDownloadFile>();

        foreach (Match match in LinkPattern.Matches(html))
        {
            var url = match.Groups["url"].Value;
            var name = System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!PackageExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!IsCompatibleArchitecture(name))
            {
                continue;
            }

            var file = new BedrockDownloadFile(name, url);
            if (file.IsMain || file.IsDependency)
            {
                files.Add(file);
            }
        }

        if (!files.Any(file => file.IsMain))
        {
            throw new InvalidOperationException(
                "没有从 Store 解析到 Minecraft 主安装包（可能被限流或商品页结构变化）。");
        }

        // 主包优先，其余依赖按名称排序，保证安装顺序稳定。
        return files
            .OrderByDescending(file => file.IsMain)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsCompatibleArchitecture(string name) =>
        !ExcludedArchitectures.Any(arch => name.Contains(arch, StringComparison.OrdinalIgnoreCase)) ||
        name.Contains("neutral", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
