using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using OML.Abstractions.Downloads;

namespace OML.Core.Downloads;

/// <summary>
/// PCL2 风格的多线程下载引擎。
/// <para>核心行为：</para>
/// <list type="bullet">
/// <item>文件大于阈值（默认 5MB）时用 <c>Range</c> 切成 8 块并发下载；</item>
/// <item>每块独立写 <c>.part{i}</c> 临时文件，续传时按"文件当前长度"推算 Range 起点；</item>
/// <item>合并阶段流式计算 SHA-1，不二次读盘；</item>
/// <item>单块失败重试 3 次（指数退避），仍失败则抛出，已下载数据保留供下次续传。</item>
/// </list>
/// <para>
/// <b>与"单个 .part 文件"方案的差异（刻意的）：</b>8 个块并发写同一个文件的随机偏移，
/// 一旦进程崩溃，文件长度无法反映"哪些区间已落盘"，只能再维护一份元数据文件；
/// 而每块一个文件天然可续传、天然线程安全，代价只是合并时多一次顺序读。
/// 对于 26MB 的 client.jar，这次顺序读的成本远低于崩溃后整包重下的概率成本。
/// </para>
/// </summary>
public sealed class PclStyleDownloader : IDownloadEngine, IDisposable
{
    /// <summary>启用分块的默认大小阈值：5MB。</summary>
    public const long DefaultChunkThresholdBytes = 5L * 1024 * 1024;

    /// <summary>默认分块数。</summary>
    public const int DefaultChunkCount = 8;

    /// <summary>默认重试次数。</summary>
    public const int DefaultMaxRetries = 3;

    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly long _chunkThresholdBytes;
    private readonly int _chunkCount;
    private readonly int _maxRetries;
    private readonly TimeSpan _chunkStartStagger;

    /// <param name="httpClient">可注入共享客户端；为 <c>null</c> 时自建（连接超时 10s、无整体超时）。</param>
    /// <param name="maxRetries">单块/单流失败重试次数。</param>
    /// <param name="chunkThresholdBytes">启用分块的文件大小阈值。</param>
    /// <param name="chunkCount">分块数量。</param>
    /// <param name="chunkStartStagger">
    /// 分块启动错峰间隔。BMCLAPI 对同一 IP 的突发并发 Range 请求会返回 429，
    /// 8 块同时起跑几乎必被限流；错峰 100~150ms 可显著降低触发率，代价是可忽略的额外延迟。
    /// </param>
    public PclStyleDownloader(
        HttpClient? httpClient = null,
        int maxRetries = DefaultMaxRetries,
        long chunkThresholdBytes = DefaultChunkThresholdBytes,
        int chunkCount = DefaultChunkCount,
        TimeSpan? chunkStartStagger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCount, 1);

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _maxRetries = maxRetries;
        _chunkThresholdBytes = chunkThresholdBytes;
        _chunkCount = chunkCount;
        _chunkStartStagger = chunkStartStagger ?? TimeSpan.FromMilliseconds(120);
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        DownloadTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (string.IsNullOrWhiteSpace(task.Url))
        {
            throw new ArgumentException("DownloadTask.Url 不能为空。", nameof(task));
        }

        if (string.IsNullOrWhiteSpace(task.SavePath))
        {
            throw new ArgumentException("DownloadTask.SavePath 不能为空。", nameof(task));
        }

        var savePath = Path.GetFullPath(task.SavePath);
        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = string.Equals(savePath, task.SavePath, StringComparison.Ordinal)
            ? task
            : new DownloadTask
            {
                Url = task.Url,
                SavePath = savePath,
                ExpectedSize = task.ExpectedSize,
                Sha1 = task.Sha1,
            };

        // 已存在且校验通过 → 直接短路，这是"重复启动不重下"的关键。
        if (File.Exists(savePath))
        {
            if (task.Sha1 is null || await IsSha1MatchAsync(savePath, task.Sha1, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(1d);
                return;
            }

            File.Delete(savePath);
        }

        var total = task.ExpectedSize;
        if (total <= 0)
        {
            total = await ProbeContentLengthAsync(task.Url, cancellationToken).ConfigureAwait(false);
        }

        if (total <= 0 || total <= _chunkThresholdBytes || _chunkCount <= 1)
        {
            await DownloadSingleStreamAsync(normalized, total, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadChunkedAsync(normalized, total, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- 单流

    private async Task DownloadSingleStreamAsync(
        DownloadTask task,
        long total,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = task.SavePath + ".part";
        var throttled = new ThrottledProgress(progress);

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (total > 0 && existing > total)
            {
                TryDelete(partPath);
                existing = 0;
            }

            try
            {
                if (total > 0 && existing == total && existing > 0)
                {
                    await VerifyAndMoveAsync(task, partPath, cancellationToken).ConfigureAwait(false);
                    progress?.Report(1d);
                    return;
                }

                using var request = CreateRequest(task.Url, existing);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // 429/503 是镜像站常见限流，必须退避重试而不是直接把异常抛给用户。
                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatus(response.StatusCode) && attempt < _maxRetries)
                    {
                        await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                // 服务端忽略 Range（返回 200）时必须从头写，否则文件会被拼坏。
                if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    TryDelete(partPath);
                    existing = 0;
                }

                throttled.Report(existing, total);

                await using (var output = new FileStream(
                    partPath,
                    existing > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            existing += read;
                            throttled.Report(existing, total);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                var size = new FileInfo(partPath).Length;
                if (total > 0 && size < total)
                {
                    throw new IOException($"连接提前结束：{size}/{total} 字节。");
                }

                await VerifyAndMoveAsync(task, partPath, cancellationToken).ConfigureAwait(false);
                progress?.Report(1d);
                return;
            }
            catch (OperationCanceledException)
            {
                // 取消语义：保留 .part，下次续传。
                throw;
            }
            catch (DownloadIntegrityException)
            {
                // 校验失败已删除临时文件，重试即从头再来。
                if (attempt >= _maxRetries)
                {
                    throw;
                }
            }
            catch (Exception) when (attempt < _maxRetries)
            {
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------- 分块

    private async Task DownloadChunkedAsync(
        DownloadTask task,
        long total,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = BuildChunks(task.SavePath, total);
        if (chunks.Length <= 1)
        {
            await DownloadSingleStreamAsync(task, total, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var chunk in chunks)
        {
            if (File.Exists(chunk.PartPath) && new FileInfo(chunk.PartPath).Length > chunk.Length)
            {
                TryDelete(chunk.PartPath);
            }
        }

        var done = new long[chunks.Length];
        for (var i = 0; i < chunks.Length; i++)
        {
            done[i] = File.Exists(chunks[i].PartPath)
                ? Math.Min(new FileInfo(chunks[i].PartPath).Length, chunks[i].Length)
                : 0;
        }

        var aggregator = new ChunkProgress(done, total, progress);
        aggregator.ReportInitial();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // 错峰启动：第 i 块延迟 i * stagger，避免 8 个请求同时砸到镜像站触发 429。
            var tasks = chunks.Select(async (chunk, index) =>
            {
                if (index > 0 && _chunkStartStagger > TimeSpan.Zero)
                {
                    await Task.Delay(_chunkStartStagger * index, linked.Token).ConfigureAwait(false);
                }

                await DownloadChunkAsync(task.Url, chunk, aggregator, linked, linked.Token).ConfigureAwait(false);
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            (IsRangeUnsupported(ex) || linked.IsCancellationRequested))
        {
            // 该源不支持 Range：清掉残块，退化为单流（功能不受影响，只是慢）。
            foreach (var chunk in chunks)
            {
                TryDelete(chunk.PartPath);
            }

            await DownloadSingleStreamAsync(task, total, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await MergeChunksAsync(task, chunks, cancellationToken).ConfigureAwait(false);

        foreach (var chunk in chunks)
        {
            TryDelete(chunk.PartPath);
        }

        progress?.Report(1d);
    }

    private async Task DownloadChunkAsync(
        string url,
        Chunk chunk,
        ChunkProgress aggregator,
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var have = File.Exists(chunk.PartPath)
                ? Math.Min(new FileInfo(chunk.PartPath).Length, chunk.Length)
                : 0;

            if (have >= chunk.Length)
            {
                aggregator.Set(chunk.Index, chunk.Length);
                return;
            }

            try
            {
                using var request = CreateRangeRequest(url, chunk.Start + have, chunk.End);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    owner.Cancel();
                    throw new RangeNotSupportedException(url);
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatus(response.StatusCode) && attempt < _maxRetries)
                    {
                        await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                await using (var output = new FileStream(
                    chunk.PartPath,
                    have > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            have += read;
                            aggregator.Advance(chunk.Index, read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                if (have < chunk.Length)
                {
                    throw new IOException($"分块 #{chunk.Index} 提前结束：{have}/{chunk.Length} 字节。");
                }

                aggregator.Set(chunk.Index, chunk.Length);
                return;
            }
            catch (RangeNotSupportedException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < _maxRetries)
            {
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MergeChunksAsync(DownloadTask task, Chunk[] chunks, CancellationToken cancellationToken)
    {
        var mergePath = task.SavePath + ".merging";

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        await using (var output = new FileStream(
            mergePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous))
        {
            foreach (var chunk in chunks)
            {
                await using var input = new FileStream(
                    chunk.PartPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        sha1.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        if (task.Sha1 is not null)
        {
            var actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, task.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(mergePath);
                foreach (var chunk in chunks)
                {
                    TryDelete(chunk.PartPath);
                }

                throw new DownloadIntegrityException(
                    $"SHA-1 校验失败：期望 {task.Sha1}，实际 {actual}（{task.SavePath}）",
                    task.Sha1,
                    actual);
            }
        }

        File.Move(mergePath, task.SavePath, overwrite: true);
    }

    // ---------------------------------------------------------------- 工具

    private async Task VerifyAndMoveAsync(DownloadTask task, string partPath, CancellationToken cancellationToken)
    {
        if (task.Sha1 is not null)
        {
            var actual = await ComputeSha1Async(partPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, task.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                throw new DownloadIntegrityException(
                    $"SHA-1 校验失败：期望 {task.Sha1}，实际 {actual}（{task.SavePath}）",
                    task.Sha1,
                    actual);
            }
        }

        File.Move(partPath, task.SavePath, overwrite: true);
    }

    private Chunk[] BuildChunks(string savePath, long total)
    {
        var chunkSize = total / _chunkCount;
        if (chunkSize <= 0)
        {
            chunkSize = total;
        }

        var chunks = new List<Chunk>(_chunkCount);
        for (var i = 0; i < _chunkCount; i++)
        {
            var start = i * chunkSize;
            if (start >= total)
            {
                break;
            }

            var end = i == _chunkCount - 1 ? total - 1 : Math.Min(start + chunkSize - 1, total - 1);
            chunks.Add(new Chunk(i, start, end, $"{savePath}.part{i}"));
        }

        return chunks.ToArray();
    }

    private static HttpRequestMessage CreateRequest(string url, long from)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, null);
        }

        return request;
    }

    private static HttpRequestMessage CreateRangeRequest(string url, long from, long to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        return request;
    }

    private async Task<long> ProbeContentLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength is long length && length > 0)
            {
                return length;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 忽略，走 Range 探测。
        }

        try
        {
            using var get = CreateRangeRequest(url, 0, 0);
            using var response = await _http
                .SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.Content.Headers.ContentRange?.Length is long total && total > 0)
            {
                return total;
            }

            if (response.StatusCode == HttpStatusCode.OK &&
                response.Content.Headers.ContentLength is long contentLength && contentLength > 0)
            {
                return contentLength;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 大小未知，退回单流 + 无法上报进度的模式。
        }

        return -1;
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> IsSha1MatchAsync(string path, string expected, CancellationToken cancellationToken)
    {
        var actual = await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRangeUnsupported(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RangeNotSupportedException)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(2000, 200 * Math.Pow(2, attempt)));

    /// <summary>值得重试的 HTTP 状态：限流、网关抖动、服务端临时故障。</summary>
    private static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// 重试等待：优先尊重服务端的 <c>Retry-After</c>，否则指数退避 + 抖动。
    /// 抖动很重要——8 个分块同时被 429 拒绝时，若退避时间完全一致会同时再来一轮，再次触发限流。
    /// </summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } delta)
        {
            return Clamp(delta) + Jitter(attempt);
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? Clamp(wait) + Jitter(attempt) : Jitter(attempt);
        }

        // 429 单独给更长的基数：镜像站限流窗口通常以秒计，200ms 级别退避等于没退。
        var baseDelay = response.StatusCode == HttpStatusCode.TooManyRequests
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromMilliseconds(500);

        return TimeSpan.FromMilliseconds(Math.Min(15_000, baseDelay.TotalMilliseconds * Math.Pow(2, attempt)))
            + Jitter(attempt);

        static TimeSpan Clamp(TimeSpan value) =>
            value > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : value;

        static TimeSpan Jitter(int attempt) =>
            TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250 + (attempt * 250)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    // ---------------------------------------------------------------- 内部类型

    /// <summary>一个分块的区间与临时文件。</summary>
    private readonly record struct Chunk(int Index, long Start, long End, string PartPath)
    {
        /// <summary>该块应下载的字节数。</summary>
        public long Length => End - Start + 1;
    }

    /// <summary>服务端不支持 Range 时抛出的内部控制流异常（会被外层转换为单流下载）。</summary>
    private sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException(string url) : base($"服务端忽略 Range，无法分块下载：{url}")
        {
        }
    }

    /// <summary>进度节流：每 100ms 最多上报一次，避免小缓冲刷爆 UI 线程。</summary>
    private sealed class ThrottledProgress
    {
        private readonly IProgress<double>? _sink;
        private readonly long _intervalTicks;
        private long _lastTicks;

        public ThrottledProgress(IProgress<double>? sink, TimeSpan? interval = null)
        {
            _sink = sink;
            _intervalTicks = (long)((interval ?? TimeSpan.FromMilliseconds(100)).TotalSeconds * Stopwatch.Frequency);
        }

        public void Report(long done, long total)
        {
            if (_sink is null || total <= 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastTicks);
            if (now - last < _intervalTicks)
            {
                return;
            }

            // 抢占失败的线程直接放弃本次上报，保证只有一个线程在算总量。
            if (Interlocked.CompareExchange(ref _lastTicks, now, last) != last)
            {
                return;
            }

            _sink.Report(Math.Clamp((double)done / total, 0d, 1d));
        }
    }

    /// <summary>多块进度汇总：各块增量相加后换算成 0..1。</summary>
    private sealed class ChunkProgress
    {
        private readonly long[] _done;
        private readonly long _total;
        private readonly ThrottledProgress _throttle;

        public ChunkProgress(long[] done, long total, IProgress<double>? sink)
        {
            _done = done;
            _total = total;
            _throttle = new ThrottledProgress(sink);
        }

        /// <summary>断点续传时先上报一次"已有进度"，UI 立刻能显示非零百分比。</summary>
        public void ReportInitial() => _throttle.Report(Sum(), _total);

        public void Advance(int index, long delta)
        {
            Interlocked.Add(ref _done[index], delta);
            _throttle.Report(Sum(), _total);
        }

        public void Set(int index, long value)
        {
            Interlocked.Exchange(ref _done[index], value);
            _throttle.Report(Sum(), _total);
        }

        private long Sum()
        {
            var sum = 0L;
            for (var i = 0; i < _done.Length; i++)
            {
                sum += Interlocked.Read(ref _done[i]);
            }

            return sum;
        }
    }
}
