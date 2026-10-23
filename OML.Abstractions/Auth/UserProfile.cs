namespace OML.Abstractions.Auth;

/// <summary>
/// 已认证用户档案。启动器只依赖这份"结果数据"，不关心它是怎么换来的。
/// </summary>
public sealed class UserProfile
{
    /// <summary>游戏内显示名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 玩家 UUID（带连字符的 36 位字符串）。
    /// 离线模式下为 <c>OfflinePlayer:&lt;name&gt;</c> 的 MD5（Java 版规则）。
    /// </summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>会话令牌。离线模式为空串；微软正版为 Minecraft access token。</summary>
    public string? AccessToken { get; set; }

    /// <summary>皮肤地址；没有则为 <c>null</c>。</summary>
    public string? SkinUrl { get; set; }

    /// <summary>该档案的认证来源，取值见 <see cref="AuthTypes"/>。</summary>
    public string Type { get; set; } = AuthTypes.Offline;

    /// <inheritdoc />
    public override string ToString() => $"{Username} ({Uuid})";
}
