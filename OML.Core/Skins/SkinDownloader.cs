using System.Security.Cryptography;
using System.Text;
using OML.Core.Downloads;
using OML.Core.Serialization;

namespace OML.Core.Skins;

/// <summary>
/// 皮肤下载与本地缓存。
/// <para>
/// 皮肤只按 URL 缓存一次：离线/断网时复用本地文件，避免每次打开界面都请求网络，
/// 也避免网络失败时头像变空白。
/// </para>
/// </summary>
public sealed class SkinDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheDirectory;

    public SkinDownloader(string cacheRoot, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            throw new ArgumentException("缓存目录不能为空。", nameof(cacheRoot));
        }

        _cacheDirectory = Path.Combine(cacheRoot, "cache", "skins");
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
    }

    /// <summary>
    /// 取皮肤本地路径。URL 为空或下载失败返回 <c>null</c>（调用方回退到 Steve）。
    /// </summary>
    public async Task<string?> GetSkinPathAsync(
        string? skinUrl,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skinUrl))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, Hash(cacheKey + "|" + skinUrl) + ".png");

            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            var bytes = await _http.GetByteArrayAsync(skinUrl, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
