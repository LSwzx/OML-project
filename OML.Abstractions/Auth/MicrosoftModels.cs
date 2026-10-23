namespace OML.Abstractions.Auth;

/// <summary>
/// 微软设备代码流返回给 UI 的信息。
/// <para>
/// <see cref="DeviceCode"/> 是内部轮询凭据，<b>不要</b>展示给用户；
/// UI 只需要 <see cref="UserCode"/> 与 <see cref="VerificationUri"/>。
/// </para>
/// </summary>
public sealed class MicrosoftDeviceCode
{
    /// <summary>内部轮询用的设备代码（敏感）。</summary>
    public required string DeviceCode { get; init; }

    /// <summary>用户在网页上输入的短代码，例如 <c>ABCD-EFGH</c>。</summary>
    public required string UserCode { get; init; }

    /// <summary>用户需要访问的地址，通常是 <c>https://microsoft.com/link</c>。</summary>
    public required string VerificationUri { get; init; }

    /// <summary>带 user_code 的完整地址，可直接生成二维码。</summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>给用户看的完整提示文案。</summary>
    public string? Message { get; init; }

    /// <summary>设备代码有效期（秒）。</summary>
    public int ExpiresIn { get; init; }

    /// <summary>建议轮询间隔（秒）。</summary>
    public int Interval { get; init; }

    /// <summary>过期时间点。</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>微软登录成功后的会话。</summary>
/// <param name="Profile">Minecraft 档案（用户名、UUID、访问令牌）。</param>
/// <param name="RefreshToken">用于静默续期的刷新令牌，应加密持久化。</param>
/// <param name="ExpiresAt">Minecraft 访问令牌的过期时间。</param>
public sealed record MicrosoftSession(UserProfile Profile, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>微软认证链路失败。</summary>
public sealed class MicrosoftAuthException : Exception
{
    public MicrosoftAuthException(string message, string? errorCode = null, int? aadErrorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        AadErrorCode = aadErrorCode;
    }

    /// <summary>OAuth 错误码，例如 <c>authorization_pending</c>、<c>invalid_grant</c>。</summary>
    public string? ErrorCode { get; }

    /// <summary>微软的 <c>AADSTSxxxxx</c> 数字码，便于排障。</summary>
    public int? AadErrorCode { get; }
}
