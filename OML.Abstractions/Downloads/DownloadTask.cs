namespace OML.Abstractions.Downloads;

/// <summary>
/// 单个下载任务。
/// <para>
/// 刻意保持"哑数据"：不含状态机、不含重试计数、不含进度。
/// 任务状态由 <see cref="IDownloadEngine"/> 内部持有，避免任务对象被多线程共享时产生竞态。
/// </para>
/// </summary>
public sealed class DownloadTask
{
    /// <summary>源地址。</summary>
    public required string Url { get; init; }

    /// <summary>落盘绝对路径（含文件名）。父目录由引擎负责创建。</summary>
    public required string SavePath { get; init; }

    /// <summary>预期大小（字节）；未知时为 <c>-1</c>，引擎据此决定是否能按分块下载。</summary>
    public long ExpectedSize { get; init; } = -1;

    /// <summary>SHA-1 校验值（小写十六进制）；为 <c>null</c> 表示跳过校验。用于断点续传与完整性验证。</summary>
    public string? Sha1 { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Url} -> {SavePath}";
}
