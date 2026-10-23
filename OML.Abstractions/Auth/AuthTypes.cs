namespace OML.Abstractions.Auth;

/// <summary>
/// 认证类型常量。
/// <para>
/// 之所以不直接用 <c>enum</c>：契约要求 <c>string type</c>，且未来可能接入第三方源
/// （如统一通行证、外置登录服务器），字符串常量比封闭枚举更易扩展，
/// 同时避免调用点散落魔法字符串。
/// </para>
/// </summary>
public static class AuthTypes
{
    /// <summary>微软正版账号（OAuth 2.0 设备码流程）。</summary>
    public const string Microsoft = "Microsoft";

    /// <summary>离线账号（仅本地生成 UUID，不联网）。</summary>
    public const string Offline = "Offline";

    /// <summary>LittleSkin 皮肤站（Yggdrasil API）。</summary>
    public const string LittleSkin = "LittleSkin";

    /// <summary>当前支持的全部认证类型。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Microsoft, Offline, LittleSkin };
}
