using System.Net;
using System.Net.Http;

namespace OML.Core.Downloads;

/// <summary>
/// 统一的 <see cref="HttpClient"/> 构造入口。
/// <para>
/// 关键点：<c>Timeout = InfiniteTimeSpan</c>。HttpClient 的超时覆盖整个请求体读取，
/// 对 26MB 的 client.jar 是致命的；连接阶段交给 <c>ConnectTimeout</c>，
/// 传输阶段交给调用方传入的 <see cref="CancellationToken"/>。
/// </para>
/// </summary>
public static class OmlHttpClientFactory
{
    /// <summary>启动器 UA。部分 CDN 会对空 UA 直接 403。</summary>
    public const string UserAgent = "OML/0.1 (+https://github.com/LSwzx/OML_test)";

    /// <summary>创建适合大文件下载的客户端：不禁用重定向、不做透明解压、连接超时 10s。</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            MaxConnectionsPerServer = 64,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
