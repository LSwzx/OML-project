namespace OML.Abstractions.Downloads;

/// <summary>
/// URL 拼接工具。
/// <para>
/// 之所以不做成 <c>string</c> 的扩展方法：基地址是"业务概念"而非通用字符串能力，
/// 泛滥的扩展方法会污染整个工程的 IntelliSense，也让调用点看不出在拼什么。
/// </para>
/// </summary>
public static class DownloadUrls
{
    /// <summary>
    /// 把相对路径拼到基地址上，自动处理双方斜杠，例：
    /// <c>Combine("https://bmclapi2.bangbang93.com/", "/version.json")</c>
    /// → <c>https://bmclapi2.bangbang93.com/version.json</c>
    /// </summary>
    public static string Combine(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }
}
