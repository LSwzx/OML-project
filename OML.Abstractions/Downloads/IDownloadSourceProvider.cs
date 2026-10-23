namespace OML.Abstractions.Downloads;

/// <summary>
/// 下载源提供者：负责"有哪些源、哪个最快"。
/// </summary>
public interface IDownloadSourceProvider
{
    /// <summary>当前可用的全部下载源（含官方源与镜像源）。</summary>
    IReadOnlyList<DownloadSource> Sources { get; }

    /// <summary>
    /// 测速并返回最快源的基地址（以 <c>/</c> 结尾）。
    /// <para>
    /// 实现约定：测速失败或全部超时时**不得抛异常**，必须回退到官方源，
    /// 否则网络异常会直接炸穿下载流程。
    /// </para>
    /// </summary>
    Task<string> GetFastestSourceAsync(CancellationToken cancellationToken = default);
}
