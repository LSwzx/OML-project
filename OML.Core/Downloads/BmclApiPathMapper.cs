namespace OML.Core.Downloads;

/// <summary>
/// Mojang 官方地址 → BMCLAPI 镜像地址的映射规则。
/// <para>
/// <b>规则全部经过实测（2026-10，HTTP 探测），不是猜的：</b>
/// </para>
/// <list type="table">
/// <item>
///   <term>launchermeta / piston-meta / piston-data</term>
///   <description>路径原样保留，只换 Host。例：
///   <c>https://launchermeta.mojang.com/mc/game/version_manifest_v2.json</c> →
///   <c>https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json</c>（200）。
///   注意：BMCLAPI <b>保留</b> <c>/mc/game/</c> 前缀，网上流传的"BMCLAPI 没有该前缀"是错的。</description>
/// </item>
/// <item>
///   <term>libraries.minecraft.net</term>
///   <description>需要插入 <c>maven/</c>。例：<c>/com/google/guava/...</c> → <c>/maven/com/google/guava/...</c>（200）。</description>
/// </item>
/// <item>
///   <term>resources.download.minecraft.net</term>
///   <description>需要插入 <c>assets/</c>。例：<c>/b6/b62ca8...</c> → <c>/assets/b6/b62ca8...</c>（200）。</description>
/// </item>
/// </list>
/// <para>
/// 另外实测两个坑，实现里必须兼容：
/// 1. BMCLAPI 对 <c>/maven/</c> 返回 <b>302</b> 到带签名的 CDN，必须允许自动重定向，否则拿到空文件；
/// 2. 该 CDN <b>支持 Range</b>（<c>Range: bytes=100-299</c> → 206），分块下载可用。
/// </para>
/// </summary>
public static class BmclApiPathMapper
{
    /// <summary>BMCLAPI 基地址（末尾带斜杠）。</summary>
    public const string BmclApiBaseUrl = "https://bmclapi2.bangbang93.com/";

    /// <summary>路径原样透传的官方 Host（只换主机名）。</summary>
    private static readonly string[] PassThroughHosts =
    {
        "launchermeta.mojang.com",
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "launcher.mojang.com",
    };

    /// <summary>需要在路径前插入镜像目录段的官方 Host。</summary>
    private static readonly (string Host, string MirrorPrefix)[] PrefixRules =
    {
        ("libraries.minecraft.net", "maven/"),
        ("resources.download.minecraft.net", "assets/"),
    };

    /// <summary>
    /// 尝试把官方 URL 映射为 BMCLAPI URL。
    /// 不认识的 Host 返回 <c>false</c>，调用方应回退到原地址（例如 Forge / Fabric 的第三方站点）。
    /// </summary>
    public static bool TryMap(string officialUrl, out string mirrorUrl)
    {
        mirrorUrl = officialUrl;

        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // 已经是镜像地址，幂等返回。
        if (string.Equals(uri.Host, "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = uri.AbsolutePath.TrimStart('/');

        foreach (var (host, prefix) in PrefixRules)
        {
            if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mirrorUrl = $"{BmclApiBaseUrl}{prefix}{path}{uri.Query}";
            return true;
        }

        foreach (var host in PassThroughHosts)
        {
            if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mirrorUrl = $"{BmclApiBaseUrl}{path}{uri.Query}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 映射为 BMCLAPI URL；无法映射时原样返回（保证调用点不必写 <c>if</c>）。
    /// </summary>
    public static string Map(string officialUrl) =>
        TryMap(officialUrl, out var mapped) ? mapped : officialUrl;
}
