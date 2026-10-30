namespace OML.Abstractions.Auth;

/// <summary>
/// 认证服务：把"如何拿到凭证"从启动流程里剥离出来。
/// 当前实现为微软正版 OAuth 2.0 设备码流程。
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 认证并返回用户档案。
    /// </summary>
    /// <param name="type">
    /// 认证类型；目前仅支持 <c>Microsoft</c>。
    /// </param>
    /// <param name="credential">
    /// 微软账号的 refresh token 或设备码流程的初始输入。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="type"/> 不受支持时抛出。</exception>
    Task<UserProfile> AuthenticateAsync(
        string type,
        string credential,
        CancellationToken cancellationToken = default);
}
