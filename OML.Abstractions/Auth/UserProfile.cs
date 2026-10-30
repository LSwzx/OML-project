using System.Text.Json.Serialization;

namespace OML.Abstractions.Auth;

/// <summary>
/// 已认证用户档案。启动器只依赖这份"结果数据"，不关心它是怎么换来的。
/// </summary>
public sealed class UserProfile
{
    /// <summary>游戏内显示名。</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 玩家 UUID（带连字符的 36 位字符串），来自官方 Minecraft 档案接口。
    /// </summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = string.Empty;

    /// <summary>会话令牌。微软正版为 Minecraft access token。</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>皮肤地址；没有则为 <c>null</c>。</summary>
    [JsonPropertyName("skinUrl")]
    public string? SkinUrl { get; set; }

    /// <summary>皮肤模型：<c>classic</c>（粗手臂）或 <c>slim</c>（细手臂）；未知时为 <c>null</c>。</summary>
    [JsonPropertyName("skinModel")]
    public string? SkinModel { get; set; }

    /// <summary>该档案的认证来源，取值见 <see cref="AuthTypes"/>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => $"{Username} ({Uuid})";
}
