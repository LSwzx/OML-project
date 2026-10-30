using System.Security.Cryptography;
using System.Text.Json;
using OML.Abstractions.Auth;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;
using OML.Core.Downloads;
using OML.Test;

// OML 自检宿主：阶段 2 的模型断言 + 阶段 3 的下载引擎实网验证。
// 任何一个断言失败都以非零退出码结束；`--no-network` 可跳过联网测试。

var failures = new List<string>();
var passed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [PASS] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}");
    }
}

static async Task<string> Sha1OfAsync(string path)
{
    await using var stream = File.OpenRead(path);
    var hash = await SHA1.HashDataAsync(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static bool IsMonotonic(IReadOnlyList<double> values)
{
    for (var i = 1; i < values.Count; i++)
    {
        if (values[i] < values[i - 1])
        {
            return false;
        }
    }

    return true;
}

// 交互式微软登录探针：不跑其它自检，只做一次真实授权。
if (args.Contains("--ms-login"))
{
    return await MicrosoftLoginProbe.RunAsync();
}

// 诊断：扫描指定 .minecraft，打印成功与跳过的版本及原因。
if (args.Length >= 2 && args[0] == "--scan")
{
    var root = args[1];
    var game = new OML.Core.Versioning.GameDirectory(root);
    var scan = new OML.Core.Versioning.LocalVersionScanner(game).ScanDetailed();

    Console.WriteLine($"扫描 {root}");
    Console.WriteLine($"成功 {scan.Versions.Count} 个，跳过 {scan.Issues.Count} 个");
    Console.WriteLine();

    foreach (var version in scan.Versions)
    {
        Console.WriteLine($"  OK   {version.Id}  [{version.Type}]  loader={version.Loader ?? "-"}  jar={version.HasClientJar}  inherits={version.InheritsFrom ?? "-"}");
    }

    foreach (var issue in scan.Issues)
    {
        Console.WriteLine($"  SKIP {issue.VersionId}");
        Console.WriteLine($"       {issue.Reason}");
    }

    return scan.Issues.Count == 0 ? 0 : 1;
}

// 诊断：下载单个 URL，打印重试/失败日志与状态。
if (args.Length >= 2 && args[0] == "--download")
{
    var url = args[1];
    Console.WriteLine($"下载 {url}");

    using var downloader = new OML.Core.Downloads.PclStyleDownloader(
        maxRetries: 2,
        chunkCount: 1,
        readTimeout: TimeSpan.FromSeconds(3));

    downloader.Log = entry => Console.WriteLine($"[{entry.Level}] {entry.Message}");
    downloader.Status = message => Console.WriteLine($"[status] {message}");

    var path = Path.Combine(Path.GetTempPath(), "oml-download-test.bin");
    try
    {
        await downloader.DownloadAsync(new OML.Abstractions.Downloads.DownloadTask { Url = url, SavePath = path });
        Console.WriteLine("下载成功");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"最终失败：{ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

// 诊断：探测本机已安装的基岩版（PowerShell / Get-AppxPackage）。
if (args.Contains("--bedrock"))
{
    var locator = new OML.Core.Bedrock.BedrockPackageLocator();
    var installed = await locator.GetInstalledAsync();

    if (installed.Count == 0)
    {
        Console.WriteLine("未安装基岩版");
        return 0;
    }

    foreach (var item in installed)
    {
        Console.WriteLine($"{item.Name} {item.Version}  AUMID={item.AppUserModelId}");
    }

    return 0;
}

// 诊断：解析 Microsoft Store 的基岩版安装包列表（不下载）。
if (args.Contains("--bedrock-store"))
{
    using var store = new OML.Core.Bedrock.BedrockStoreClient();
    foreach (var channel in new[] { OML.Core.Bedrock.BedrockChannel.Release, OML.Core.Bedrock.BedrockChannel.Preview })
    {
        try
        {
            var files = await store.GetPackageFilesAsync(channel);
            Console.WriteLine($"[{channel}] {files.Count} 个文件");
            foreach (var file in files)
            {
                Console.WriteLine($"  {(file.IsMain ? "MAIN" : "DEP ")} {file.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{channel}] 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    return 0;
}

// 诊断：生成诊断报告 zip（默认读取当前用户的配置）。
if (args.Length >= 2 && args[0] == "--diagnostic")
{
    var output = args[1];
    var launcherRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OML");
    var configPath = Path.Combine(launcherRoot, "launcher_config.json");

    var exporter = new OML.Core.Diagnostics.DiagnosticExporter(configPath, launcherRoot);
    var path = await exporter.ExportAsync(
        output,
        Environment.GetEnvironmentVariable("OML_GAME_DIR") ?? @"E:\新建文件夹\.minecraft",
        versionId: null,
        launcherLog: "[OML] diagnostic test log",
        javaPath: null,
        launcherVersion: "1.1.4");

    Console.WriteLine("导出：" + path);
    using var zip = System.IO.Compression.ZipFile.OpenRead(path);
    foreach (var entry in zip.Entries)
    {
        Console.WriteLine($"  {entry.FullName}  {entry.Length} bytes");
    }

    return 0;
}

// 诊断：验证微软 Client ID 能正常签发设备码（不轮询）。
if (args.Contains("--ms-device"))
{
    using var service = new OML.Core.Auth.MicrosoftAuthService();
    try
    {
        var code = await service.StartDeviceCodeAsync();
        Console.WriteLine($"user_code={code.UserCode}");
        Console.WriteLine($"verification_uri={code.VerificationUri}");
        Console.WriteLine($"expires_in={code.ExpiresIn}s interval={code.Interval}s");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"失败：{ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}
RunAbstractionChecks();

// PCL2 之类的第三方启动器会往版本 JSON 里塞注释、尾逗号、扩展字段，
// 大小写也不完全统一。OmlJsonOptions 必须全部容忍，否则版本会被误判为"未安装"。
{
    const string pclStyleJson = """
    {
      // PCL 自己加的标记
      "ID": "pcl-ish",
      "Type": "release",
      "ReleaseTime": "2024-08-08T12:24:45+0000",
      "pclExtra": { "nested": [1, 2, 3] },
      "libraries": [],
    }
    """;

    try
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<OML.Abstractions.Models.VersionDetail>(
            pclStyleJson,
            OML.Core.Serialization.OmlJsonOptions.Default);

        Check("JSON 容错：注释 / 尾逗号 / 大小写 / 未知字段 / +0000 时间",
            parsed is not null && parsed.Id == "pcl-ish" && parsed.Type == "release");
    }
    catch (Exception ex)
    {
        Check($"JSON 容错：注释 / 尾逗号 / 大小写 / 未知字段 / +0000 时间（异常 {ex.GetType().Name}）", false);
    }
}

// PCL2 / OptiFine 安装器会把 optifine:OptiFine、optifine:launchwrapper-of 这类库
// 直接放进 libraries/，但 JSON 里既没有 downloads 也没有 url。
// 只要文件存在就必须进 classpath，否则启动报 ClassNotFoundException。
{
    var tempRoot = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "oml-local-lib-" + Guid.NewGuid().ToString("N"));

    try
    {
        var jarPath = System.IO.Path.Combine(
            tempRoot, "libraries", "optifine", "launchwrapper-of", "2.3", "launchwrapper-of-2.3.jar");

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(jarPath)!);
        System.IO.File.WriteAllBytes(jarPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var game = new OML.Core.Versioning.GameDirectory(tempRoot);
        var detail = new OML.Abstractions.Models.VersionDetail
        {
            Libraries =
            {
                new OML.Abstractions.Models.Library { Name = "optifine:launchwrapper-of:2.3" },
            },
        };

        var resolution = new OML.Core.Versioning.LibraryResolver(
                game,
                OML.Core.Versioning.RuleEvaluator.ForCurrentPlatform())
            .Resolve(detail);

        Check("classpath 包含只存在于 libraries/ 的库（无 downloads/url）",
            resolution.Classpath.Any(p => string.Equals(p, jarPath, StringComparison.OrdinalIgnoreCase)) &&
            resolution.Downloads.Count == 0);
    }
    finally
    {
        try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { }
    }
}

// 基岩版：安装包分类与授权解析（不联网、不安装）。
{
    var main = new OML.Core.Bedrock.BedrockDownloadFile(
        "Microsoft.MinecraftUWP_1.21.90.3_x64__8wekyb3d8bbwe.msix",
        "https://example.com/main.msix");
    var vclibs = new OML.Core.Bedrock.BedrockDownloadFile(
        "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe.appx",
        "https://example.com/vclibs.appx");
    var uiXaml = new OML.Core.Bedrock.BedrockDownloadFile(
        "Microsoft.UI.Xaml.2.8_8.2310.30001.0_x64__8wekyb3d8bbwe.appx",
        "https://example.com/uixaml.appx");
    var unrelated = new OML.Core.Bedrock.BedrockDownloadFile(
        "SomeOther.Package_1.0.0.0_x64__abc.appx",
        "https://example.com/other.appx");

    Check("基岩版安装包分类：主包 / 依赖 / 无关包",
        main.IsMain && !main.IsDependency &&
        vclibs.IsDependency && !vclibs.IsMain &&
        uiXaml.IsDependency &&
        !unrelated.IsMain && !unrelated.IsDependency);

    var installation = new OML.Core.Bedrock.BedrockInstallation(
        "Microsoft.MinecraftUWP", "1.21.90.3",
        "Microsoft.MinecraftUWP_8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\x");
    Check("基岩版 AUMID 由包族名构成",
        installation.AppUserModelId == "Microsoft.MinecraftUWP_8wekyb3d8bbwe!App" &&
        !installation.IsPreview);

    using var ownedHttp = new HttpClient(new StubJsonHandler(
        """{"items":[{"name":"product_minecraft_bedrock","signature":"x"}]}"""));
    var owned = await new OML.Core.Bedrock.BedrockEntitlementService(ownedHttp)
        .CheckAsync("token");
    Check("基岩版授权：包含 product_minecraft_bedrock 判为已拥有",
        owned.Status == OML.Core.Bedrock.BedrockEntitlementStatus.Owned);

    using var notOwnedHttp = new HttpClient(new StubJsonHandler(
        """{"items":[{"name":"product_minecraft_java"}]}"""));
    var notOwned = await new OML.Core.Bedrock.BedrockEntitlementService(notOwnedHttp)
        .CheckAsync("token");
    Check("基岩版授权：不含基岩版条目判为未购买",
        notOwned.Status == OML.Core.Bedrock.BedrockEntitlementStatus.NotOwned);

    var anonymous = await new OML.Core.Bedrock.BedrockEntitlementService()
        .CheckAsync(null);
    Check("基岩版授权：无令牌时提示先登录",
        anonymous.Status == OML.Core.Bedrock.BedrockEntitlementStatus.NotSignedIn);
}

// 诊断报告：配置脱敏必须递归替换 token / secret，其他字段保持原样。
{
    const string configJson = """
    {
      "username": "Player1",
      "accessToken": "secret-minecraft-token",
      "userProfile": { "refreshToken": "secret-refresh", "name": "Player1" },
      "nested": [ { "clientSecret": "secret-client" } ],
      "completeAssets": true
    }
    """;

    var sanitized = OML.Core.Diagnostics.DiagnosticExporter.SanitizeConfigJson(configJson);

    Check("诊断报告：accessToken / refreshToken / secret 全部替换为 ***",
        sanitized.Contains("\"accessToken\": \"***\"") &&
        sanitized.Contains("\"refreshToken\": \"***\"") &&
        sanitized.Contains("\"clientSecret\": \"***\"") &&
        !sanitized.Contains("secret-minecraft-token") &&
        !sanitized.Contains("secret-refresh") &&
        !sanitized.Contains("secret-client"));

    Check("诊断报告：非敏感字段与类型保持不变",
        sanitized.Contains("\"username\": \"Player1\"") &&
        sanitized.Contains("\"completeAssets\": true"));
}

Stage4Checks.RunLocal(Check);
Stage5Checks.RunLocal(Check);
Stage6Checks.RunLocal(Check);
#if !STRICT_ONLINE_ONLY
Stage7Checks.RunLocal(Check);
#endif
Stage8Checks.RunLocal(Check);
Stage9Checks.RunLocal(Check);
#if !STRICT_ONLINE_ONLY
Stage10Checks.RunLocal(Check);
#endif
Stage11Checks.RunLocal(Check);

if (args.Contains("--no-network"))
{
    Console.WriteLine();
    Console.WriteLine("已跳过联网测试（--no-network）。");
}
else
{
    var onlyStage5 = args.Contains("--stage5-only");
    var onlyStage8 = args.Contains("--stage8-only");
    var onlyStage9 = args.Contains("--stage9-only");
    var onlyStage10 = args.Contains("--stage10-only");

    if (!onlyStage5 && !onlyStage8 && !onlyStage9 && !onlyStage10)
    {
        await RunDownloadChecksAsync();
        await Stage4Checks.RunNetworkAsync(Check, Path.Combine(Path.GetTempPath(), "oml-stage4", ".minecraft"));
    }

    if (!onlyStage8 && !onlyStage9 && !onlyStage10)
    {
        await Stage5Checks.RunNetworkAsync(Check, Path.Combine(Path.GetTempPath(), "oml-stage5", ".minecraft"));

        if (!args.Contains("--no-launch"))
        {
            await Stage6Checks.RunNetworkAsync(Check, Path.Combine(Path.GetTempPath(), "oml-stage5", ".minecraft"));
#if !STRICT_ONLINE_ONLY
            await Stage7Checks.RunNetworkAsync(
                Check,
                Path.Combine(Path.GetTempPath(), "oml-stage7", "launcher"),
                Path.Combine(Path.GetTempPath(), "oml-stage5", ".minecraft"));
#endif
        }
        else
        {
            Console.WriteLine("已跳过真实启动测试（--no-launch）。");
        }
    }

    if (!onlyStage9 && !onlyStage10 && !args.Contains("--no-stage8"))
    {
        await Stage8Checks.RunNetworkAsync(
            Check,
            Path.Combine(Path.GetTempPath(), "oml-stage8", "launcher"),
            Path.Combine(Path.GetTempPath(), "oml-stage8", ".minecraft"));
    }

    if (!onlyStage10 && !args.Contains("--no-stage9"))
    {
        await Stage9Checks.RunNetworkAsync(Check);
    }

#if !STRICT_ONLINE_ONLY
    if (!args.Contains("--no-stage10"))
    {
        await Stage10Checks.RunNetworkAsync(Check);
    }
#endif
}

Console.WriteLine();
Console.WriteLine($"通过 {passed} 项，失败 {failures.Count} 项。");
if (failures.Count > 0)
{
    Console.WriteLine("失败项：");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    return 1;
}

return 0;

// ------------------------------------------------------------------ 阶段 2

void RunAbstractionChecks()
{
    const string manifestJson = """
    {
      "latest": { "release": "1.21.1", "snapshot": "24w33a" },
      "versions": [
        {
          "id": "1.21.1",
          "type": "release",
          "url": "https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json",
          "time": "2024-08-08T11:06:32+00:00",
          "releaseTime": "2024-08-08T11:06:32+00:00",
          "sha1": "abc123",
          "complianceLevel": 1
        },
        {
          "id": "24w33a",
          "type": "snapshot",
          "url": "https://piston-meta.mojang.com/v1/packages/def/24w33a.json"
        }
      ]
    }
    """;

    const string versionJson = """
    {
      "id": "1.21.1",
      "type": "release",
      "mainClass": "net.minecraft.client.main.Main",
      "assets": "17",
      "assetIndex": {
        "id": "17",
        "sha1": "aaa111",
        "size": 410000,
        "totalSize": 527000000,
        "url": "https://piston-meta.mojang.com/v1/packages/aaa111/17.json"
      },
      "downloads": {
        "client": {
          "sha1": "bbb222",
          "size": 26000000,
          "url": "https://piston-data.mojang.com/v1/objects/bbb222/client.jar"
        }
      },
      "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
      "arguments": {
        "game": [
          "--username",
          "${auth_player_name}",
          {
            "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ],
            "value": "--demo"
          }
        ],
        "jvm": [ "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" ]
      },
      "libraries": [
        {
          "name": "org.lwjgl:lwjgl:3.3.3",
          "downloads": {
            "artifact": {
              "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
              "sha1": "ccc333",
              "size": 723000,
              "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"
            },
            "classifiers": {
              "natives-windows": {
                "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
                "sha1": "ddd444",
                "size": 150000,
                "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar"
              }
            }
          }
        },
        {
          "name": "ca.weblite:java-objc-bridge:1.1",
          "rules": [ { "action": "allow", "os": { "name": "osx" } } ]
        },
        {
          "name": "net.java:jinput-platform:2.0.5:natives-windows@jar",
          "url": "https://libraries.minecraft.net/",
          "natives": { "windows": "natives-windows" },
          "extract": { "exclude": [ "META-INF/" ] }
        }
      ]
    }
    """;

    Console.WriteLine("== 1. VersionManifest ==");
    var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestJson)!;
    Check("Latest.Release == 1.21.1", manifest.Latest?.Release == "1.21.1");
    Check("Latest.Snapshot == 24w33a", manifest.Latest?.Snapshot == "24w33a");
    Check("Versions.Count == 2", manifest.Versions.Count == 2);
    Check("Versions[0].Type == release", manifest.Versions[0].Type == "release");
    Check("Versions[0].Sha1 反序列化", manifest.Versions[0].Sha1 == "abc123");
    Check("Versions[0].ReleaseTime 反序列化", manifest.Versions[0].ReleaseTime is not null);
    Check("Versions[1].Url 反序列化", manifest.Versions[1].Url?.EndsWith("24w33a.json") == true);

    Console.WriteLine("== 2. VersionDetail ==");
    var detail = JsonSerializer.Deserialize<VersionDetail>(versionJson)!;
    Check("Id / Type", detail.Id == "1.21.1" && detail.Type == "release");
    Check("MainClass", detail.MainClass == "net.minecraft.client.main.Main");
    Check("Assets == 17", detail.Assets == "17");
    Check("AssetIndex.TotalSize", detail.AssetIndex?.TotalSize == 527000000);
    Check("Downloads.Client.Url", detail.Downloads?.Client?.Url?.EndsWith("client.jar") == true);
    Check("JavaVersion.MajorVersion == 21", detail.JavaVersion?.MajorVersion == 21);
    Check("Libraries.Count == 3", detail.Libraries.Count == 3);
    Check("Game 参数 3 个", detail.Arguments?.Game.Count == 3);
    Check("Game[2] 为条件对象", detail.Arguments?.Game[2].ValueKind == JsonValueKind.Object);
    Check("Jvm 参数 3 个", detail.Arguments?.Jvm.Count == 3);

    Console.WriteLine("== 3. Library ==");
    var lwjgl = detail.Libraries[0];
    Check("Artifact.Path", lwjgl.Downloads?.Artifact?.Path == "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
    Check("Classifiers 命中 natives-windows",
        lwjgl.Downloads?.Classifiers?.TryGetValue("natives-windows", out var natives) == true &&
        natives.Path?.EndsWith("natives-windows.jar") == true);
    Check("Rules[0].Os.Name == osx", detail.Libraries[1].Rules?[0].Os?.Name == "osx");
    Check("Natives 字典", detail.Libraries[2].Natives?["windows"] == "natives-windows");
    Check("Extract.Exclude", detail.Libraries[2].Extract?.Exclude.Count == 1);
    Check("旧格式库可推导 Maven 路径",
        detail.Libraries[2].MavenRelativePath ==
        "net/java/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar");

    Console.WriteLine("== 4. MavenCoordinate ==");
    Check("三段坐标解析",
        MavenCoordinate.TryParse("org.lwjgl:lwjgl:3.3.3", out var c1) &&
        c1.GroupId == "org.lwjgl" && c1.ArtifactId == "lwjgl" &&
        c1.Version == "3.3.3" && c1.Classifier is null &&
        c1.RelativePath == "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
    Check("四段 + @ext 解析",
        MavenCoordinate.TryParse("net.java:jinput-platform:2.0.5:natives-windows@jar", out var c2) &&
        c2.Classifier == "natives-windows" && c2.Extension == "jar");
    Check("非法坐标被拒绝",
        !MavenCoordinate.TryParse("", out _) &&
        !MavenCoordinate.TryParse("bad:name", out _));

    Console.WriteLine("== 5. Download ==");
    Check("DownloadUrls.Combine 双侧斜杠归一",
        DownloadUrls.Combine("https://bmclapi2.bangbang93.com/", "/version.json") ==
        "https://bmclapi2.bangbang93.com/version.json");

    var task = new DownloadTask
    {
        Url = "https://piston-data.mojang.com/v1/objects/ab/client.jar",
        SavePath = Path.Combine(Path.GetTempPath(), "client.jar"),
        ExpectedSize = 26000000,
        Sha1 = "bbb222",
    };
    Check("DownloadTask 构造与默认值", task.ExpectedSize == 26000000 && task.Sha1 == "bbb222");

    DownloadSource[] sources =
    [
        new() { Name = "Mojang", BaseUrl = "https://piston-meta.mojang.com/", IsOfficial = true },
        new() { Name = "BMCLAPI", BaseUrl = "https://bmclapi2.bangbang93.com/" },
    ];
    Check("DownloadSource 列表", sources.Length == 2 && !sources[1].IsOfficial);

    Console.WriteLine("== 6. Auth ==");
    var profile = new UserProfile
    {
        Username = "Steve",
        Uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5",
    };
    Check("UserProfile 默认类型为 Offline", profile.Type == AuthTypes.Offline);
    Check("AuthTypes.All 含三种类型",
        AuthTypes.All.Count == 3 &&
        AuthTypes.All.Contains(AuthTypes.Microsoft) &&
        AuthTypes.All.Contains(AuthTypes.LittleSkin));
}

// ------------------------------------------------------------------ 阶段 3

async Task RunDownloadChecksAsync()
{
    const string officialManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    var mirrorManifestUrl = BmclApiPathMapper.Map(officialManifestUrl);

    Console.WriteLine("== 7. 路径映射规则 ==");
    Check("BMCLAPI 保留 /mc/game/ 前缀（实测）",
        mirrorManifestUrl == "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
    Check("libraries → /maven/",
        BmclApiPathMapper.Map("https://libraries.minecraft.net/a/b.jar") ==
        "https://bmclapi2.bangbang93.com/maven/a/b.jar");
    Check("resources → /assets/",
        BmclApiPathMapper.Map("https://resources.download.minecraft.net/ab/cdef") ==
        "https://bmclapi2.bangbang93.com/assets/ab/cdef");
    Check("未知 Host 原样返回",
        BmclApiPathMapper.Map("https://maven.fabricmc.net/a.jar") == "https://maven.fabricmc.net/a.jar");
    Check("镜像地址幂等",
        BmclApiPathMapper.Map(mirrorManifestUrl) == mirrorManifestUrl);

    Console.WriteLine("== 8. 镜像测速（并发 HEAD，3s 超时）==");
    using var tester = new MirrorSpeedTester();
    var latencies = await tester.MeasureAsync([officialManifestUrl, mirrorManifestUrl]);
    foreach (var item in latencies)
    {
        var status = item.Reachable ? "OK  " : "FAIL";
        Console.WriteLine($"   [{status}] {item.Elapsed?.TotalMilliseconds,7:0} ms  {item.Url}  {item.Error}");
    }

    Check("至少一个源可达", latencies.Any(x => x.Reachable));
    Check("测速结果按耗时升序", latencies.Zip(latencies.Skip(1)).All(p => (p.First.Elapsed ?? TimeSpan.MaxValue) <= (p.Second.Elapsed ?? TimeSpan.MaxValue)));
    Check("单源故障不影响整体（不可达项也返回结果）", latencies.Count == 2);

    Console.WriteLine("== 9. 源提供者 ==");
    using var provider = new BmclApiSourceProvider();
    var fastestBase = await provider.GetFastestSourceAsync();
    Console.WriteLine($"   相对最快源：{fastestBase}");
    Check("源列表含 BMCLAPI 与官方", provider.Sources.Count == 2);
    Check("最快源基地址以 / 结尾", fastestBase.EndsWith('/'));
    var cachedBase = await provider.GetFastestSourceAsync();
    Check("测速结果被缓存（两次一致）", cachedBase == fastestBase);

    var useMirror = fastestBase.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"   实际下载走：{(useMirror ? "BMCLAPI 镜像" : "Mojang 官方")}");

    Console.WriteLine("== 10. 真实下载：3MB Guava jar ==");
    using var http = OmlHttpClientFactory.Create();
    var manifestText = await http.GetStringAsync(officialManifestUrl);
    var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestText)!;
    var summary = manifest.Versions.First(v => v.Id == "1.21.1");
    var detailText = await http.GetStringAsync(BmclApiPathMapper.Map(summary.Url!));
    var detail = JsonSerializer.Deserialize<VersionDetail>(detailText)!;

    var guava = detail.Libraries.First(l => l.Name.StartsWith("com.google.guava:guava:", StringComparison.Ordinal));
    var artifact = guava.Downloads!.Artifact!;
    var url = useMirror ? BmclApiPathMapper.Map(artifact.Url!) : artifact.Url!;

    // 分块用例走官方源：BMCLAPI 对突发并发 Range 会 429，连测四轮会把公共镜像打限流，
    // 而官方源对 8 并发 Range 稳定返回 206。镜像能力已由单流用例覆盖。
    var officialReachable = latencies.Any(x => x.Reachable && x.Url == officialManifestUrl);
    var chunkTestUrl = officialReachable ? artifact.Url! : url;

    Console.WriteLine($"   {guava.Name}  {artifact.Size:N0} 字节  sha1={artifact.Sha1}");
    Console.WriteLine($"   单流源：{url}");
    Console.WriteLine($"   分块源：{chunkTestUrl}");

    var work = Path.Combine(Path.GetTempPath(), "oml-stage3");
    if (Directory.Exists(work))
    {
        Directory.Delete(work, true);
    }

    Directory.CreateDirectory(work);

    DownloadTask MakeTask(string path, string? sourceUrl = null) => new()
    {
        Url = sourceUrl ?? url,
        SavePath = path,
        ExpectedSize = artifact.Size,
        Sha1 = artifact.Sha1,
    };

    // ---- 10.1 单流（3MB < 5MB 阈值）
    var singlePath = Path.Combine(work, "guava-single.jar");
    var chunkedPath = Path.Combine(work, "guava-chunked.jar");
    var resumePath = Path.Combine(work, "guava-resume.jar");

    // 单组失败不能炸掉整轮自检：记录失败后继续跑后面的组。
    async Task Guarded(string label, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  [FAIL] {label} 执行中断：{ex.GetType().Name}: {ex.Message}");
        }
    }

    await Guarded("单流下载", async () =>
    {
        var progress = new SyncProgress();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var downloader = new PclStyleDownloader())
        {
            await downloader.DownloadAsync(MakeTask(singlePath), progress);
        }

        watch.Stop();
        var values = progress.Snapshot();
        Check("单流：SHA-1 与版本 JSON 一致", await Sha1OfAsync(singlePath) == artifact.Sha1);
        Check("单流：进度单调不减且收于 1.0",
            values.Count > 0 && IsMonotonic(values) && values[^1] == 1.0);
        Check("单流：无残留 .part 文件", !File.Exists(singlePath + ".part"));
        Console.WriteLine($"   耗时 {watch.ElapsedMilliseconds} ms，进度上报 {values.Count} 次");
    });

    await Task.Delay(1200);

    // ---- 10.2 分块（把阈值压到 256KB，强制 8 块并发）
    await Guarded("分块下载", async () =>
    {
        var progress = new SyncProgress();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // 旁证：采样临时文件，确认下载期间确实同时存在 8 个分块，而不是悄悄退化成单流。
        var observedPartCount = 0;
        using var watcherCts = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watcherCts.IsCancellationRequested)
            {
                try
                {
                    var count = Directory.GetFiles(work, "guava-chunked.jar.part*").Length;
                    if (count > observedPartCount)
                    {
                        observedPartCount = count;
                    }
                }
                catch (IOException)
                {
                    // 目录在变化，忽略。
                }

                await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
            }
        });

        using (var downloader = new PclStyleDownloader(chunkThresholdBytes: 256 * 1024, chunkCount: 8))
        {
            await downloader.DownloadAsync(MakeTask(chunkedPath, chunkTestUrl), progress);
        }

        await watcherCts.CancelAsync();
        await watcher;

        watch.Stop();
        var values = progress.Snapshot();
        Check("分块：同时观察到 8 个 .part 文件（真并发，非退化单流）", observedPartCount == 8);
        Check("分块：SHA-1 一致", await Sha1OfAsync(chunkedPath) == artifact.Sha1);
        Check("分块：无残留 .partN 文件", Directory.GetFiles(work, "guava-chunked.jar.part*").Length == 0);
        Check("分块：进度收于 1.0", values.Count > 0 && values[^1] == 1.0);
        Check("分块：与单流下载的内容完全一致",
            File.Exists(singlePath) && new FileInfo(chunkedPath).Length == new FileInfo(singlePath).Length);
        Console.WriteLine($"   耗时 {watch.ElapsedMilliseconds} ms，进度上报 {values.Count} 次，峰值分块数 {observedPartCount}");
    });

    await Task.Delay(1500);

    // ---- 10.3 断点续传：预置"每块已下前一半中的 96KB"，验证服务器按 Range 补齐
    Console.WriteLine("== 11. 断点续传 ==");
    await Guarded("断点续传", async () =>
    {
        const int chunkCount = 8;
        var chunkSize = artifact.Size / chunkCount;
        var seededBytes = 0L;

        await using (var source = File.OpenRead(singlePath))
        {
            for (var i = 0; i < chunkCount; i++)
            {
                var start = i * chunkSize;
                var length = i == chunkCount - 1 ? artifact.Size - start : chunkSize;
                var take = (int)Math.Min(length / 2, 96 * 1024);
                var buffer = new byte[take];
                source.Position = start;
                await source.ReadExactlyAsync(buffer);
                await File.WriteAllBytesAsync(resumePath + $".part{i}", buffer);
                seededBytes += take;
            }
        }

        Console.WriteLine($"   预置 {seededBytes:N0}/{artifact.Size:N0} 字节（{seededBytes * 100.0 / artifact.Size:0.0}%）");

        var progress = new SyncProgress();
        using (var downloader = new PclStyleDownloader(chunkThresholdBytes: 256 * 1024, chunkCount: 8))
        {
            await downloader.DownloadAsync(MakeTask(resumePath, chunkTestUrl), progress);
        }

        var values = progress.Snapshot();
        Check("续传：SHA-1 与原件一致", await Sha1OfAsync(resumePath) == artifact.Sha1);
        Check("续传：首帧进度 > 0（未从零开始）", values.Count > 0 && values[0] > 0.05);
        Check("续传：最终进度为 1.0", values.Count > 0 && values[^1] == 1.0);
        Check("续传：残留 .partN 已清理", Directory.GetFiles(work, "guava-resume.jar.part*").Length == 0);
    });

    await Task.Delay(1200);

    // ---- 10.4 完整性校验失败
    Console.WriteLine("== 12. SHA-1 校验失败处理 ==");
    await Guarded("完整性校验", async () =>
    {
        var assetIndexText = await http.GetStringAsync(detail.AssetIndex!.Url!);
        using var assetIndex = JsonDocument.Parse(assetIndexText);
        var firstObject = assetIndex.RootElement.GetProperty("objects").EnumerateObject().First();
        var assetHash = firstObject.Value.GetProperty("hash").GetString()!;
        var assetSize = firstObject.Value.GetProperty("size").GetInt64();
        var assetUrl = $"https://resources.download.minecraft.net/{assetHash[..2]}/{assetHash}";

        var badPath = Path.Combine(work, "asset-bad.bin");
        var threw = false;
        try
        {
            using var downloader = new PclStyleDownloader();
            await downloader.DownloadAsync(new DownloadTask
            {
                Url = assetUrl,
                SavePath = badPath,
                ExpectedSize = assetSize,
                Sha1 = new string('0', 40),
            });
        }
        catch (DownloadIntegrityException)
        {
            threw = true;
        }

        Check("SHA-1 不匹配时抛出 DownloadIntegrityException", threw);
        Check("校验失败后不留下最终文件", !File.Exists(badPath));
    });

    // ---- 10.5 重复下载短路：文件已存在且校验通过时不应重下
    await Guarded("短路跳过", async () =>
    {
        var progress = new SyncProgress();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var downloader = new PclStyleDownloader())
        {
            await downloader.DownloadAsync(MakeTask(singlePath), progress);
        }

        watch.Stop();
        Check("已存在且校验通过时直接短路", progress.Snapshot().SequenceEqual([1.0]));
        Check("短路耗时 < 1s", watch.ElapsedMilliseconds < 1000);
    });

    Console.WriteLine();
    Console.WriteLine($"下载产物目录：{work}");
}

/// <summary>同步进度收集器。<see cref="Progress{T}"/> 在控制台宿主会走线程池，断言时机不可控。</summary>
internal sealed class SyncProgress : IProgress<double>
{
    private readonly List<double> _values = new();
    private readonly object _gate = new();

    public void Report(double value)
    {
        lock (_gate)
        {
            _values.Add(value);
        }
    }

    public List<double> Snapshot()
    {
        lock (_gate)
        {
            return new List<double>(_values);
        }
    }
}

/// <summary>返回固定 JSON 的 HttpMessageHandler，用于授权解析测试。</summary>
internal sealed class StubJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });
}
