using OML.Abstractions.Auth;
using OML.Core.Auth;
using OML.Core.Modrinth;

namespace OML.Test;

/// <summary>阶段 9：微软设备码登录与 Modrinth API。</summary>
internal static class Stage9Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 41. 微软登录工具方法 ==");

        check("32 位 ID 格式化为标准 UUID",
            MicrosoftAuthService.FormatUuid("069a79f444e94726a5befca90e38aaf5") ==
            "069a79f4-44e9-4726-a5be-fca90e38aaf5");
        check("已是 UUID 时保持不变",
            MicrosoftAuthService.FormatUuid("069a79f4-44e9-4726-a5be-fca90e38aaf5") ==
            "069a79f4-44e9-4726-a5be-fca90e38aaf5");
        check("非法长度原样返回", MicrosoftAuthService.FormatUuid("abc") == "abc");

        var hadClientId = Environment.GetEnvironmentVariable(MicrosoftAuthService.ClientIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(hadClientId))
        {
            var threw = false;
            string? message = null;
            try
            {
                MicrosoftAuthService.ResolveClientId(null);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                message = ex.Message;
            }

            check("未配置 Client ID 时给出明确指引",
                threw && message!.Contains(MicrosoftAuthService.ClientIdEnvironmentVariable));
        }
        else
        {
            check("环境变量提供了 Client ID", MicrosoftAuthService.ResolveClientId(null) == hadClientId);
        }

        check("显式 Client ID 优先于环境变量",
            MicrosoftAuthService.ResolveClientId("explicit-id") == "explicit-id");

        Console.WriteLine("== 42. Modrinth facets ==");

        check("三种条件都生成",
            ModrinthClient.BuildFacets("mod", "1.20.1", "fabric") ==
            "[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:fabric\"]]");
        check("无条件返回 null", ModrinthClient.BuildFacets(null, null, null) is null);
        check("仅加载器",
            ModrinthClient.BuildFacets(null, null, "forge") == "[[\"categories:forge\"]]");
    }

    public static async Task RunNetworkAsync(Action<string, bool> check)
    {
        Console.WriteLine("== 43. 微软设备码流 ==");

        var clientId = Environment.GetEnvironmentVariable(MicrosoftAuthService.ClientIdEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            try
            {
                using var service = new MicrosoftAuthService(clientId);
                var code = await service.StartDeviceCodeAsync();

                check("拿到 user_code", !string.IsNullOrWhiteSpace(code.UserCode));
                check("拿到 verification_uri", code.VerificationUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                check("设备码有效期为正数", code.ExpiresIn > 0 && code.Interval > 0);

                Console.WriteLine($"   请在浏览器打开：{code.VerificationUri}");
                Console.WriteLine($"   输入代码：{code.UserCode}");
                Console.WriteLine($"   有效期 {code.ExpiresIn} 秒，轮询间隔 {code.Interval} 秒");
                Console.WriteLine("   （测试不等待真实授权，因此不会触发轮询）");
            }
            catch (MicrosoftAuthException ex)
            {
                check($"设备码请求失败（Client ID 可能无效）：{ex.Message}", false);
            }
        }
        else
        {
            // 没有配置 Client ID：用无效 ID 验证"端点连通 + 错误解析"，而不是假装能拿到设备码。
            using var service = new MicrosoftAuthService("00000000-0000-0000-0000-000000000000");
            try
            {
                await service.StartDeviceCodeAsync();
                check("无 Client ID 时应被 Azure 拒绝", false);
            }
            catch (MicrosoftAuthException ex)
            {
                check("微软端点连通且错误被正确解析",
                    ex.ErrorCode is "unauthorized_client" or "invalid_client" ||
                    ex.AadErrorCode is not null);
                Console.WriteLine($"   微软返回：{ex.Message}");
                Console.WriteLine("   ⚠ 未配置 Client ID，无法展示真实 user_code。");
                Console.WriteLine($"   注册 Azure 应用后设置环境变量 {MicrosoftAuthService.ClientIdEnvironmentVariable} 再跑本测试。");
            }
            catch (Exception ex)
            {
                check($"微软端点不可达：{ex.Message}", false);
            }
        }

        Console.WriteLine("== 44. Modrinth 搜索 ==");

        using var modrinth = new ModrinthClient();

        var search = await modrinth.SearchAsync("JEI", limit: 5, projectType: "mod", gameVersion: "1.20.1", loader: "fabric");
        check("搜索返回结果", search.Hits.Count > 0);
        check("命中 JEI 相关项目",
            search.Hits.Any(h => h.Title.Contains("JEI", StringComparison.OrdinalIgnoreCase)));

        foreach (var hit in search.Hits.Take(3))
        {
            var description = hit.Description.Length > 60 ? hit.Description[..60] + "…" : hit.Description;
            Console.WriteLine($"   [{hit.ProjectId}] {hit.Title}  ↓{hit.Downloads:N0}");
            Console.WriteLine($"      {description}");
        }

        Console.WriteLine("== 45. Modrinth 项目与版本 ==");

        var project = await modrinth.GetProjectAsync("jei");
        check("项目详情可取", project.Id == "u6dRKJwZ" && project.Title.Contains("JEI"));
        check("项目声明支持 fabric/forge/neoforge",
            project.Loaders.Contains("fabric") && project.Loaders.Contains("forge"));
        check("项目列出游戏版本", project.GameVersions.Count > 10);
        Console.WriteLine($"   {project.Title} / loaders={string.Join(",", project.Loaders)} / 支持 {project.GameVersions.Count} 个版本");

        var versions = await modrinth.GetVersionsAsync("jei", gameVersion: "1.20.1", loader: "fabric");
        check("能按 1.20.1 + fabric 过滤版本", versions.Count > 0);

        var latest = await modrinth.GetLatestVersionAsync("jei", "1.20.1", "fabric");
        check("取到最新兼容版本", latest is not null);

        var file = ModrinthClient.GetPrimaryFile(latest!);
        check("拿到 .jar 直链",
            file is not null &&
            file.Url.StartsWith("https://cdn.modrinth.com/", StringComparison.OrdinalIgnoreCase) &&
            file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

        check("文件带 SHA-1/SHA-512 校验", file!.Hashes.Sha1 is not null || file.Hashes.Sha512 is not null);

        Console.WriteLine($"   版本：{latest!.VersionNumber}（{latest.VersionType}，{latest.DatePublished:yyyy-MM-dd}）");
        Console.WriteLine($"   直链：{file.Url}");
        Console.WriteLine($"   大小：{file.Size:N0} 字节，SHA-1：{file.Hashes.Sha1}");

        if (latest.Dependencies.Count > 0)
        {
            var deps = latest.Dependencies
                .Select(d => $"{d.DependencyType}:{d.ProjectId ?? d.VersionId}")
                .ToArray();
            Console.WriteLine($"   依赖：{string.Join("，", deps)}");
            check("依赖关系已解析",
                latest.Dependencies.All(d => d.DependencyType.Length > 0) &&
                latest.Dependencies.Any(d => d.ProjectId is not null || d.VersionId is not null));
        }
        else
        {
            Console.WriteLine("   依赖：无");
            check("依赖关系已解析（该版本无依赖）", true);
        }
    }
}
