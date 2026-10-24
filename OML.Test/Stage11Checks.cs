using OML.Abstractions.Auth;
using OML.Abstractions.Configuration;
using OML.Core.Configuration;

namespace OML.Test;

/// <summary>阶段 11：桌面端的配置加密与基础设施。</summary>
internal static class Stage11Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 50. DPAPI 令牌保护 ==");

        const string plainToken = "super-secret-token-1234567890";

        var protectedValue = TokenProtector.Protect(plainToken)!;
        check("加密后带 dpapi: 前缀且不含明文",
            protectedValue.StartsWith(TokenProtector.Prefix, StringComparison.Ordinal) &&
            !protectedValue.Contains(plainToken, StringComparison.Ordinal));
        check("可解密还原", TokenProtector.Unprotect(protectedValue) == plainToken);
        check("历史明文原样返回", TokenProtector.Unprotect("legacy-plain-token") == "legacy-plain-token");
        check("损坏数据返回 null（不抛异常）",
            TokenProtector.Unprotect("dpapi:not-valid-base64!!") is null);
        check("重复加密不叠加", TokenProtector.Protect(protectedValue) == protectedValue);
        check("空值安全", TokenProtector.Protect(null) is null && TokenProtector.Unprotect(null) is null);

        Console.WriteLine("== 51. 配置文件不落明文令牌 ==");

        var root = Path.Combine(Path.GetTempPath(), "oml-stage11", "launcher");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        var manager = new ConfigManager(root);
        var config = new LauncherConfig
        {
            SelectedVersion = "1.21.1",
            MaxMemoryMB = 4096,
            GameDirectory = ".minecraft",
            UserProfile = new UserProfile
            {
                Username = "Player",
                Uuid = "a01e3843-e521-3998-958a-f459800e4d11",
                AccessToken = plainToken,
                Type = AuthTypes.Offline,
            },
        };

        manager.SaveAsync(config).GetAwaiter().GetResult();

        var json = File.ReadAllText(manager.ConfigPath);
        check("JSON 中无明文令牌", !json.Contains(plainToken, StringComparison.Ordinal));
        check("JSON 中是 dpapi 密文", json.Contains(TokenProtector.Prefix, StringComparison.Ordinal));

        var loaded = manager.LoadAsync().GetAwaiter().GetResult();
        check("读回时自动解密", loaded.UserProfile?.AccessToken == plainToken);
        check("保存不修改调用方对象", config.UserProfile?.AccessToken == plainToken);

        var noTokenManager = new ConfigManager(Path.Combine(root, "no-token"));
        noTokenManager.SaveAsync(new LauncherConfig
        {
            UserProfile = new UserProfile { Username = "NoToken", Uuid = string.Empty, AccessToken = null },
        }).GetAwaiter().GetResult();

        check("无令牌时不写入 dpapi 字段",
            !File.ReadAllText(noTokenManager.ConfigPath).Contains(TokenProtector.Prefix, StringComparison.Ordinal));
    }
}
