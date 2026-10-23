namespace OML.Abstractions.Downloads;

/// <summary>
/// 下载完成但内容校验失败（SHA-1 不匹配）。
/// </summary>
/// <remarks>
/// 放在抽象层而非 Core：这是"契约级"失败，UI 层要能区分"网络挂了"和"文件被污染"，
/// 否则只能靠捕获裸 <see cref="Exception"/> 做字符串匹配。
/// </remarks>
public sealed class DownloadIntegrityException : IOException
{
    public DownloadIntegrityException(string message, string expectedSha1, string actualSha1)
        : base(message)
    {
        ExpectedSha1 = expectedSha1;
        ActualSha1 = actualSha1;
    }

    /// <summary>期望的 SHA-1（来自版本 JSON）。</summary>
    public string ExpectedSha1 { get; }

    /// <summary>实际算出的 SHA-1。</summary>
    public string ActualSha1 { get; }
}
