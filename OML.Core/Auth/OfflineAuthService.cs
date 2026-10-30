#if !STRICT_ONLINE_ONLY
using System.Security.Cryptography;
using System.Text;
using OML.Abstractions.Auth;

namespace OML.Core.Auth;

/// <summary>
/// 离线账号认证。
/// <para>
/// UUID 规则与 Minecraft 官方离线模式一致：
/// <c>UUID = UUIDv3(MD5("OfflinePlayer:" + name))</c>，
/// 即对拼接串取 MD5，再把版本位改成 3、变体位改成 RFC 4122 的 10xx。
/// 同一个名字在任何机器上都会得到同一个 UUID，这是多人服务器识别离线玩家的依据。
/// </para>
/// </summary>
public sealed class OfflineAuthService : IAuthService
{
    /// <summary>离线 UUID 的固定前缀（大小写敏感，不能改）。</summary>
    private const string UuidPrefix = "OfflinePlayer:";

    /// <inheritdoc />
    public Task<UserProfile> AuthenticateAsync(
        string type,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(type, AuthTypes.Offline, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                $"OfflineAuthService 只支持 {AuthTypes.Offline}，其他类型请使用对应的认证服务。");
        }

        return Task.FromResult(CreateProfile(credential));
    }

    /// <summary>根据用户名生成离线档案（同步版本，测试与 UI 直接调用更方便）。</summary>
    public static UserProfile CreateProfile(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空。", nameof(username));
        }

        var name = username.Trim();
        if (name.Length > 16)
        {
            throw new ArgumentException("Minecraft 用户名最长 16 个字符。", nameof(username));
        }

        return new UserProfile
        {
            Username = name,
            Uuid = ComputeOfflineUuid(name),
            AccessToken = GenerateFakeToken(),
            SkinUrl = null,
            Type = AuthTypes.Offline,
        };
    }

    /// <summary>
    /// 计算离线 UUID（36 位、带连字符）。结果与 Official Launcher 的离线模式一致。
    /// </summary>
    public static string ComputeOfflineUuid(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空。", nameof(username));
        }

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(UuidPrefix + username));

        // RFC 4122：版本 3（MD5 命名空间）+ 变体位 10xx。
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return FormatUuid(bytes);
    }

    private static string FormatUuid(byte[] bytes)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return string.Create(36, hex, static (span, source) =>
        {
            span[0] = source[0];
            span[1] = source[1];
            span[2] = source[2];
            span[3] = source[3];
            span[4] = source[4];
            span[5] = source[5];
            span[6] = source[6];
            span[7] = source[7];
            span[8] = '-';
            span[9] = source[8];
            span[10] = source[9];
            span[11] = source[10];
            span[12] = source[11];
            span[13] = '-';
            span[14] = source[12];
            span[15] = source[13];
            span[16] = source[14];
            span[17] = source[15];
            span[18] = '-';
            span[19] = source[16];
            span[20] = source[17];
            span[21] = source[18];
            span[22] = source[19];
            span[23] = '-';
            span[24] = source[20];
            span[25] = source[21];
            span[26] = source[22];
            span[27] = source[23];
            span[28] = source[24];
            span[29] = source[25];
            span[30] = source[26];
            span[31] = source[27];
            span[32] = source[28];
            span[33] = source[29];
            span[34] = source[30];
            span[35] = source[31];
        });
    }

    private static string GenerateFakeToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
#endif
