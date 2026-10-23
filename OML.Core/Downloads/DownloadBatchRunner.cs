using System.Threading;
using OML.Abstractions.Downloads;

namespace OML.Core.Downloads;

/// <summary>一个失败的下载任务。</summary>
public sealed record DownloadFailure(DownloadTask Task, Exception Error);

/// <summary>批量下载结果。</summary>
public sealed record BatchDownloadResult(int Total, int Succeeded, IReadOnlyList<DownloadFailure> Failures)
{
    /// <summary>是否全部成功。</summary>
    public bool AllSucceeded => Failures.Count == 0;

    /// <summary>失败数量。</summary>
    public int FailedCount => Failures.Count;
}

/// <summary>
/// 批量下载执行器：限制并发、隔离失败。
/// <para>
/// 单个文件失败<b>不</b>中断整批——资源补全动辄几千个文件，一个 404 不该让整次安装归零。
/// 失败清单返回给上层，由它决定重试还是提示。
/// </para>
/// </summary>
public static class DownloadBatchRunner
{
    /// <summary>默认并发上限。整个启动器共用一个数值，避免镜像站被瞬间打爆。</summary>
    public const int DefaultMaxConcurrency = 16;

    /// <summary>
    /// 并发执行一批下载任务。
    /// </summary>
    /// <param name="tasks">任务集合。</param>
    /// <param name="engine">下载引擎。</param>
    /// <param name="maxConcurrency">最大并发数。</param>
    /// <param name="progress">进度回调（已完成数 / 总数），含失败项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<BatchDownloadResult> RunAsync(
        IEnumerable<DownloadTask> tasks,
        IDownloadEngine engine,
        int maxConcurrency = DefaultMaxConcurrency,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var list = tasks as IReadOnlyList<DownloadTask> ?? tasks.ToList();
        if (list.Count == 0)
        {
            return new BatchDownloadResult(0, 0, Array.Empty<DownloadFailure>());
        }

        var failures = new List<DownloadFailure>();
        var gate = new object();
        var completed = 0;

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var workers = list.Select(async task =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await engine.DownloadAsync(task, null, cancellationToken).ConfigureAwait(false);

                lock (gate)
                {
                    completed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    completed++;
                    failures.Add(new DownloadFailure(task, ex));
                }
            }
            finally
            {
                semaphore.Release();
                progress?.Report((double)Volatile.Read(ref completed) / list.Count);
            }
        }).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        int succeeded;
        lock (gate)
        {
            succeeded = list.Count - failures.Count;
        }

        return new BatchDownloadResult(list.Count, succeeded, failures);
    }
}
