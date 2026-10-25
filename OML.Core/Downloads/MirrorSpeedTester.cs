using System.Diagnostics;
using System.Net;
using System.Net.Http;
using OML.Abstractions.Downloads;

namespace OML.Core.Downloads;

/// <summary>单个镜像的测速结果。</summary>
/// <param name="Url">被探测的地址。</param>
/// <param name="Elapsed">耗时；不可达时为 <c>null</c>。</param>
/// <param name="StatusCode">HTTP 状态码；连接层失败时为 <c>null</c>。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
public sealed record MirrorLatency(string Url, TimeSpan? Elapsed, int? StatusCode, string? Error)
{
    /// <summary>是否可达。</summary>
    public bool Reachable => Elapsed is not null;
}

/// <summary>
/// 并发镜像测速器（PCL2 的"自动选择下载源"）。
/// <para>
/// 设计约束：<b>任何单个源的失败都不允许冒泡到调用方</b>。
/// HEAD 被拒（405/501）时退化为 <c>Range: bytes=0-0</c> 的 GET，因为不少 CDN 不支持 HEAD。
/// </para>
/// </summary>
public sealed class MirrorSpeedTester : IDisposable
{
    /// <summary>默认单源超时：3 秒。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;

    /// <param name="httpClient">可注入共享客户端（复用连接池）；为 <c>null</c> 时自建。</param>
    /// <param name="timeout">单源超时，默认 3 秒。</param>
    public MirrorSpeedTester(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>并发探测一组 URL，按耗时升序返回；不可达的排在最后。</summary>
    public async Task<IReadOnlyList<MirrorLatency>> MeasureAsync(
        IEnumerable<string> urls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var targets = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (targets.Length == 0)
        {
            return Array.Empty<MirrorLatency>();
        }

        var results = await Task.WhenAll(targets.Select(u => ProbeAsync(u, cancellationToken)))
            .ConfigureAwait(false);

        return results
            .OrderBy(r => r.Elapsed ?? TimeSpan.MaxValue)
            .ToArray();
    }

    private async Task<MirrorLatency> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (IsReachable(headResponse.StatusCode))
            {
                return new MirrorLatency(url, stopwatch.Elapsed, (int)headResponse.StatusCode, null);
            }

            // HEAD 不可用：退化为只取 1 字节的 GET。
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var getResponse = await _http
                .SendAsync(get, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            return IsReachable(getResponse.StatusCode)
                ? new MirrorLatency(url, stopwatch.Elapsed, (int)getResponse.StatusCode, null)
                : new MirrorLatency(url, null, (int)getResponse.StatusCode, $"HTTP {(int)getResponse.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MirrorLatency(url, null, null, $"超时 (> {_timeout.TotalSeconds:0.#}s)");
        }
        catch (Exception ex)
        {
            return new MirrorLatency(url, null, null, ex.Message);
        }
    }

    private static bool IsReachable(HttpStatusCode statusCode) => statusCode switch
    {
        >= HttpStatusCode.OK and < HttpStatusCode.BadRequest => true,
        HttpStatusCode.RequestedRangeNotSatisfiable => true, // 206/416 都说明服务端活着
        _ => false,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
