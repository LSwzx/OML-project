using OML.Abstractions.Auth;
using OML.Core.Auth;

namespace OML.Test;

/// <summary>
/// 交互式微软登录探针：打印设备码 → 等待用户在浏览器授权 → 打印 Minecraft 档案 → 验证刷新令牌。
/// <para>用 <c>--ms-login</c> 运行。这是唯一能验证 Xbox / XSTS / Minecraft 整条链路的测试。</para>
/// </summary>
internal static class MicrosoftLoginProbe
{
    public static async Task<int> RunAsync(TimeSpan? timeout = null)
    {
        string clientId;
        try
        {
            clientId = MicrosoftAuthService.ResolveClientId(null);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }

        using var service = new MicrosoftAuthService(clientId);

        Console.WriteLine("正在请求设备码…");
        var code = await service.StartDeviceCodeAsync();

        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine($"  浏览器打开：{code.VerificationUri}");
        Console.WriteLine($"  输入代码  ：{code.UserCode}");
        Console.WriteLine($"  有效期    ：{code.ExpiresIn} 秒");
        Console.WriteLine("==================================================");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(8));
        var status = new Progress<string>(message => Console.WriteLine($"   {message}"));

        try
        {
            var session = await service.WaitForAuthorizationAsync(code, status, cts.Token);

            Console.WriteLine();
            Console.WriteLine($"登录成功：{session.Profile.Username}");
            Console.WriteLine($"  UUID      ：{session.Profile.Uuid}");
            Console.WriteLine($"  类型      ：{session.Profile.Type}");
            Console.WriteLine($"  MC 令牌至 ：{session.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");

            Console.WriteLine();
            Console.WriteLine("验证刷新令牌…");
            var refreshed = await service.RefreshAsync(session.RefreshToken);
            Console.WriteLine($"刷新成功：{refreshed.Profile.Username}，新令牌至 {refreshed.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("等待授权超时（设备码已过期）。");
            return 1;
        }
        catch (MicrosoftAuthException ex)
        {
            Console.WriteLine($"登录失败：{ex.Message}");
            if (ex.AadErrorCode is not null)
            {
                Console.WriteLine($"  AADSTS{ex.AadErrorCode}");
            }

            return 1;
        }
    }
}
