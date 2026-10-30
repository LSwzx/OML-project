namespace OML.Abstractions.Auth;

/// <summary>认证类型常量。</summary>
public static class AuthTypes
{
    /// <summary>微软正版账号（OAuth 2.0 设备码流程）。</summary>
    public const string Microsoft = "Microsoft";

    /// <summary>当前支持的全部认证类型。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Microsoft };
}
