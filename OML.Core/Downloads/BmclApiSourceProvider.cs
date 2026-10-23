using System.Net.Http;
using OML.Abstractions.Downloads;

namespace OML.Core.Downloads;

/// <summary>
/// PCL2 风格的双源提供者：BMCLAPI（镜像，优先）+ Mojang 官方（兜底）。
/// <para>
/// 除了实现 <see cref="IDownloadSourceProvider"/>，这里还承担"地址改写"职责：
/// 上层拿着版本 JSON 里的官方绝对地址来问"该用什么地址下载"时，
/// 通过 <see cref="BmclApiPathMapper"/> 换成镜像地址即可，不必关心路径规则差异。
/// </para>
/// </summary>
public sealed class BmclApiSourceProvider : IDownloadSourceProvider, IDisposable
{
    /// <summary>Mojang 官方源。</summary>
    public static readonly DownloadSource OfficialSource = new()
    {
        Name = "Mojang",
        BaseUrl = "https://launchermeta.mojang.com/",
        IsOfficial = true,
        Priority = 10,
    };

    /// <summary>BMCLAPI 镜像源。</summary>
    public static readonly DownloadSource BmclApiSource = new()
    {
        Name = "BMCLAPI",
        BaseUrl = BmclApiPathMapper.BmclApiBaseUrl,
        IsOfficial = false,
        Priority = 0,
    };

    /// <summary>
    /// 测速探针：版本清单（小、稳定）+ 一个固定版本的 Guava jar（代表 libraries 主机）。
    /// 只测清单会得到"清单快但文件慢"的误判，所以每源至少两个不同主机的样本。
    /// </summary>
    private static readonly string[] OfficialProbeUrls =
    {
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
        "https://libraries.minecraft.net/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar",
    };

    private static readonly string[] MirrorProbeUrls =
        OfficialProbeUrls.Select(BmclApiPathMapper.Map).ToArray();

    private readonly MirrorSpeedTester _tester;
    private readonly bool _ownsTester;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedBaseUrl;
    private DateTimeOffset _cachedAt;

    /// <param name="tester">可注入测速器；为 <c>null</c> 时自建。</param>
    /// <param name="cacheDuration">测速结果缓存时长，默认 10 分钟。</param>
    public BmclApiSourceProvider(MirrorSpeedTester? tester = null, TimeSpan? cacheDuration = null)
    {
        _ownsTester = tester is null;
        _tester = tester ?? new MirrorSpeedTester();
        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(10);
    }

    /// <inheritdoc />
    /// <remarks>按优先级排序：镜像在前，官方在后。</remarks>
    public IReadOnlyList<DownloadSource> Sources { get; } = new[] { BmclApiSource, OfficialSource };

    /// <summary>
    /// 返回最快源的基地址（以 <c>/</c> 结尾）。
    /// <b>契约要求永不抛异常</b>：测速失败、全部超时、DNS 挂了，一律回退官方源。
    /// </summary>
    public async Task<string> GetFastestSourceAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetCached(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached))
            {
                return cached;
            }

            var fastest = await SelectFastestBaseUrlAsync(cancellationToken).ConfigureAwait(false);

            if (fastest is not null)
            {
                _cachedBaseUrl = fastest;
                _cachedAt = DateTimeOffset.UtcNow;
                return fastest;
            }

            // 全部不可达：不缓存，下次重试；本次先让调用方拿到官方源继续跑。
            return OfficialSource.BaseUrl;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return OfficialSource.BaseUrl;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把官方地址改写为镜像地址；不支持映射的地址原样返回。</summary>
    public static string MapToMirror(string officialUrl) => BmclApiPathMapper.Map(officialUrl);

    /// <summary>清空测速缓存（用户手动切源或网络环境变化时调用）。</summary>
    public void InvalidateCache()
    {
        _cachedBaseUrl = null;
        _cachedAt = default;
    }

    private bool TryGetCached(out string baseUrl)
    {
        var cached = _cachedBaseUrl;
        if (cached is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheDuration)
        {
            baseUrl = cached;
            return true;
        }

        baseUrl = string.Empty;
        return false;
    }

    private async Task<string?> SelectFastestBaseUrlAsync(CancellationToken cancellationToken)
    {
        var allUrls = OfficialProbeUrls.Concat(MirrorProbeUrls);
        var results = await _tester.MeasureAsync(allUrls, cancellationToken).ConfigureAwait(false);

        var officialBest = BestLatency(results, OfficialProbeUrls);
        var mirrorBest = BestLatency(results, MirrorProbeUrls);

        if (officialBest is null && mirrorBest is null)
        {
            // 全挂：由调用方回退官方源。
            return null;
        }

        if (mirrorBest is null)
        {
            return OfficialSource.BaseUrl;
        }

        if (officialBest is null)
        {
            return BmclApiSource.BaseUrl;
        }

        // 延迟相差不到 20% 时选镜像：国内环境镜像通常更稳，也省官方流量。
        return mirrorBest.Value.TotalMilliseconds <= officialBest.Value.TotalMilliseconds * 1.2
            ? BmclApiSource.BaseUrl
            : OfficialSource.BaseUrl;
    }

    private static TimeSpan? BestLatency(IReadOnlyList<MirrorLatency> results, string[] urls)
    {
        var latencies = results
            .Where(r => r.Reachable && r.Elapsed is not null && urls.Contains(r.Url, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Elapsed!.Value)
            .ToArray();

        return latencies.Length == 0 ? null : latencies.Min();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsTester)
        {
            _tester.Dispose();
        }

        _gate.Dispose();
    }
}
