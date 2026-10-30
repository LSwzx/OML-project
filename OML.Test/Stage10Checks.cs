#if !STRICT_ONLINE_ONLY
using System.Net;
using OML.Abstractions.Auth;
using OML.Core.Auth;

namespace OML.Test;

/// <summary>阶段 10：LittleSkin / Yggdrasil 外置登录。</summary>
internal static class Stage10Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 46. Yggdrasil 错误模型 ==");

        var credentialError = new YggdrasilException(
            "凭据错误", HttpStatusCode.Forbidden, "ForbiddenOperationException", "用户名或密码错误。");
        var serverError = new YggdrasilException("服务端错误", HttpStatusCode.InternalServerError, "InternalError");

        check("403 + ForbiddenOperationException 识别为凭据错误", credentialError.IsCredentialError);
        check("5xx 不算凭据错误", !serverError.IsCredentialError);
        check("保留服务端原始消息", credentialError.ServerMessage?.Contains("密码") == true);

        using var service = new YggdrasilAuthService();

        check("默认地址为 LittleSkin",
            service.BaseUrl == "https://littleskin.cn/api/yggdrasil");
        check("自动生成 clientToken", service.ClientToken.Length == 32);

        var rejectedType = false;
        try
        {
            service.AuthenticateAsync(AuthTypes.Microsoft, "a:b").GetAwaiter().GetResult();
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectedType = true;
        }

        check("拒绝非 Yggdrasil 类型", rejectedType);

        var rejectedFormat = false;
        try
        {
            service.AuthenticateAsync(AuthTypes.LittleSkin, "no-colon-here").GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            rejectedFormat = true;
        }

        check("拒绝缺少冒号的凭证格式", rejectedFormat);
    }

    public static async Task RunNetworkAsync(Action<string, bool> check)
    {
        Console.WriteLine("== 47. LittleSkin 连通性 ==");

        using var service = new YggdrasilAuthService();

        YggdrasilServerInfo? info = null;
        try
        {
            info = await service.GetServerInfoAsync();
            check("能读取服务端元信息", !string.IsNullOrWhiteSpace(info.ServerName));
            Console.WriteLine($"   {info.ServerName} / {info.ImplementationName} / {info.Homepage}");
        }
        catch (Exception ex)
        {
            check($"LittleSkin 不可达：{ex.Message}", false);
            return;
        }

        Console.WriteLine("== 48. 错误凭据必须被拒绝 ==");

        const string fakeEmail = "oml_stage10_nonexistent@example.com";
        const string fakePassword = "definitely-not-the-password";

        var loginRejected = false;
        YggdrasilException? caught = null;
        try
        {
            await service.LoginAsync(fakeEmail, fakePassword);
        }
        catch (YggdrasilException ex)
        {
            loginRejected = true;
            caught = ex;
        }

        check("错误邮箱/密码被拒绝", loginRejected);
        check("状态码为 401 或 403",
            caught?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        check("错误类型为 ForbiddenOperationException",
            caught?.Error == "ForbiddenOperationException");
        check("被识别为凭据错误", caught?.IsCredentialError == true);

        if (caught is not null)
        {
            Console.WriteLine($"   HTTP {(int)caught.StatusCode} / {caught.Error}");
            Console.WriteLine($"   {caught.Message}");
        }

        var viaInterface = false;
        try
        {
            await service.AuthenticateAsync(AuthTypes.LittleSkin, $"{fakeEmail}:{fakePassword}");
        }
        catch (YggdrasilException)
        {
            viaInterface = true;
        }

        check("IAuthService 入口同样拒绝", viaInterface);

        Console.WriteLine("== 49. 令牌校验与刷新 ==");

        const string bogusToken = "0000000000000000000000000000000000000000";

        var valid = await service.ValidateAsync(bogusToken);
        check("无效令牌校验返回 false", !valid);

        var refreshRejected = false;
        try
        {
            await service.RefreshAsync(bogusToken);
        }
        catch (YggdrasilException ex)
        {
            refreshRejected = ex.IsCredentialError;
        }

        check("无效令牌刷新被拒绝", refreshRejected);
    }
}
#endif
