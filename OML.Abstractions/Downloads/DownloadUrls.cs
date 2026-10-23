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

    /// <summary>
    /// 把 Mojang 官方绝对地址重写到指定镜像源。
    /// 镜像站与官方目录结构一致，因此只取官方 URL 的路径部分即可。
    /// 例：官方 <c>https://piston-data.mojang.com/v1/objects/ab/cd/abcd</c>
    /// → BMCLAPI <c>https://bmclapi2.bangbang93.com/v1/objects/ab/cd/abcd</c>
    /// </summary>
    public static string Rebase(string officialUrl, string newBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(officialUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseUrl);

        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri))
        {
            return officialUrl;
        }

        var path = uri.PathAndQuery.TrimStart('/');
        return Combine(newBaseUrl, path);
    }
}
