#if !STRICT_ONLINE_ONLY
using System.Net;

namespace OML.Abstractions.Auth;

/// <summary>
/// Yggdrasil（外置登录）会话。
/// </summary>
/// <param name="Profile">选中的玩家档案。</param>
/// <param name="AccessToken">访问令牌。</param>
/// <param name="ClientToken">客户端令牌（同一启动器实例应保持一致）。</param>
/// <param name="AvailableProfiles">该账号下可选的全部角色。</param>
public sealed record YggdrasilSession(
    UserProfile Profile,
    string AccessToken,
    string ClientToken,
    IReadOnlyList<UserProfile> AvailableProfiles);

/// <summary>
/// Yggdrasil 服务器返回的错误。
/// <para>
/// 注意：Yggdrasil 协议里"用户名密码错误"与"令牌无效"通常都是 <b>403</b>，
/// 不是 401。用 <see cref="IsCredentialError"/> 判断，不要只看状态码。
/// </para>
/// </summary>
public sealed class YggdrasilException : Exception
{
    public YggdrasilException(
        string message,
        HttpStatusCode statusCode,
        string? error = null,
        string? serverMessage = null,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Error = error;
        ServerMessage = serverMessage;
    }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>服务端错误类型，例如 <c>ForbiddenOperationException</c>。</summary>
    public string? Error { get; }

    /// <summary>服务端原始错误消息（LittleSkin 是中英双语）。</summary>
    public string? ServerMessage { get; }

    /// <summary>是否为凭据类错误（用户名密码错误 / 令牌失效）。</summary>
    public bool IsCredentialError =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden &&
        string.Equals(Error, "ForbiddenOperationException", StringComparison.Ordinal);
}

/// <summary>Yggdrasil 服务端元信息（用于连通性检测与"登录到哪"的展示）。</summary>
public sealed record YggdrasilServerInfo(
    string ServerName,
    string? ImplementationName,
    string? Homepage);
#endif
