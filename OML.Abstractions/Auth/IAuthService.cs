namespace OML.Abstractions.Auth;

/// <summary>
/// 认证服务：把"如何拿到凭证"从启动流程里剥离出来。
/// 阶段 2 仅定义契约，具体实现（Microsoft OAuth 设备码、离线 UUID 推导、LittleSkin Yggdrasil）留待后续。
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 认证并返回用户档案。
    /// </summary>
    /// <param name="type">
    /// 认证类型，取值见 <see cref="AuthTypes"/>：
    /// <c>Microsoft</c> / <c>Offline</c> / <c>LittleSkin</c>。
    /// </param>
    /// <param name="credential">
    /// 凭证。不同 <paramref name="type"/> 语义不同：
    /// Offline 为用户名，Microsoft 为 refresh token 或设备码流程的初始输入，LittleSkin 为 <c>email:password</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="type"/> 不受支持时抛出。</exception>
    Task<UserProfile> AuthenticateAsync(
        string type,
        string credential,
        CancellationToken cancellationToken = default);
}
