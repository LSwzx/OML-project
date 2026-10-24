using System.Security.Cryptography;
using System.Text;

namespace OML.Core.Configuration;

/// <summary>
/// 用 Windows DPAPI 保护敏感令牌。
/// <para>
/// 配置里的 <c>accessToken</c> / refresh token 是"拿到就能冒充玩家"的凭据，
/// 明文写在 JSON 里等于把它共享给任何能读文件的进程。
/// DPAPI（<see cref="DataProtectionScope.CurrentUser"/>）用当前用户的主密钥加密，
/// 换用户或换机器都解不开——这也意味着配置不可跨机复制，正好符合安全预期。
/// </para>
/// <para>存储格式：<c>dpapi:&lt;base64&gt;</c>，带前缀便于向后兼容历史明文配置。</para>
/// </summary>
public static class TokenProtector
{
    /// <summary>加密值前缀。</summary>
    public const string Prefix = "dpapi:";

    /// <summary>额外的熵，防止同一用户下其它程序直接解密同形数据。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OML.AccessToken.v1");

    /// <summary>是否已经是加密形态。</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密令牌；空值或已加密时原样返回。</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext))
        {
            return plaintext;
        }

        if (!OperatingSystem.IsWindows())
        {
            // DPAPI 仅 Windows 可用；其它平台保持明文，由上层决定是否提示。
            return plaintext;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// 解密令牌。历史明文原样返回；解密失败（换用户/换机器/数据损坏）返回 <c>null</c>，
    /// 让上层按"未登录"处理而不是抛异常把启动器卡死。
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        if (!IsProtected(stored))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(stored[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
