using OML.Abstractions.Downloads;

namespace OML.Core.Downloads;

/// <summary>下载诊断日志级别。</summary>
public enum DownloadLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 一条下载诊断日志。
/// <para>UI 用它把"重试/失败"写进日志面板，同时把关键信息展示在全局状态栏。</para>
/// </summary>
/// <param name="Level">级别。</param>
/// <param name="Message">已经排版好的中文文案。</param>
/// <param name="Url">下载地址（失败时必填）。</param>
/// <param name="SavePath">目标路径（失败时必填）。</param>
/// <param name="StatusCode">HTTP 状态码，没有则为 null。</param>
/// <param name="Reason">简短失败原因。</param>
public sealed record DownloadLogEntry(
    DownloadLogLevel Level,
    string Message,
    string? Url = null,
    string? SavePath = null,
    int? StatusCode = null,
    string? Reason = null);

/// <summary>
/// 批量下载失败：携带每个失败文件的地址、路径与原因，供 UI 弹窗逐条展示。
/// </summary>
public sealed class DownloadBatchException : Exception
{
    public DownloadBatchException(IReadOnlyList<DownloadFailure> failures)
        : base($"{failures.Count} 个文件下载失败")
    {
        Failures = failures;
    }

    public IReadOnlyList<DownloadFailure> Failures { get; }

    /// <summary>把失败列表格式化成弹窗/日志用的多行文本。</summary>
    public string ToDetailedText()
    {
        var lines = new List<string> { $"共 {Failures.Count} 个文件下载失败：" };

        foreach (var failure in Failures)
        {
            var (reason, status) = Describe(failure.Error);
            lines.Add(
                $"- {failure.Task.Url}\n" +
                $"  原因：{reason}\n" +
                $"  状态码：{(status is null ? "无" : status.ToString())}\n" +
                $"  目标路径：{failure.Task.SavePath}");
        }

        return string.Join("\n\n", lines);
    }

    private static (string Reason, int? StatusCode) Describe(Exception ex) => ex switch
    {
        TimeoutException timeout => (timeout.Message, null),
        HttpRequestException http => ($"HTTP 请求失败：{http.Message}", http.StatusCode is { } code ? (int)code : null),
        DownloadIntegrityException => ("文件校验失败（SHA-1 不匹配）", null),
        IOException io => (io.Message, null),
        _ => (ex.Message, null),
    };
}
