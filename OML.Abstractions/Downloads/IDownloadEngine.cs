namespace OML.Abstractions.Downloads;

/// <summary>
/// 下载引擎：负责"怎么下"。
/// <para>
/// 抽象层只约定单任务语义；多线程调度、分块、断点续传、重试策略全部是实现细节，
/// 由具体引擎（自研 PCL 式或 ProjBobcat 适配器）决定。
/// </para>
/// </summary>
public interface IDownloadEngine
{
    /// <summary>
    /// 下载单个文件。
    /// </summary>
    /// <param name="task">下载任务。</param>
    /// <param name="progress">进度回调，取值 <c>0.0 - 1.0</c>；允许为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。取消时应保留已下载部分以便续传。</param>
    Task DownloadAsync(
        DownloadTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
