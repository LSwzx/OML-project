using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;
using OML.Core.Downloads;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Test;

/// <summary>阶段 4：清单、目录布局、继承合并的验证。</summary>
internal static class Stage4Checks
{
    private enum TestKind
    {
        Release,
        Snapshot,
    }

    /// <summary>离线断言：不依赖网络。</summary>
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 13. Json 配置 ==");

        const string upperCaseJson = """
        {
          "ID": "1.21.1",
          "TYPE": "release",
          "MAINCLASS": "net.minecraft.client.main.Main",
          "assets": "17"
        }
        """;

        var detail = JsonSerializer.Deserialize<VersionDetail>(upperCaseJson, OmlJsonOptions.Default)!;
        check("大小写不敏感：ID/TYPE/MAINCLASS 均能解析",
            detail.Id == "1.21.1" &&
            detail.Type == "release" &&
            detail.MainClass == "net.minecraft.client.main.Main");

        var enumJson = JsonSerializer.Serialize(TestKind.Snapshot, OmlJsonOptions.Default);
        var enumBack = JsonSerializer.Deserialize<TestKind>("\"release\"", OmlJsonOptions.Default);
        check("枚举序列化为字符串", enumJson == "\"snapshot\"" && enumBack == TestKind.Release);
        check("配置已只读（防运行期篡改）", OmlJsonOptions.Default.IsReadOnly);
        check("已注册 JsonStringEnumConverter",
            OmlJsonOptions.Default.Converters.OfType<JsonStringEnumConverter>().Any());

        Console.WriteLine("== 14. GameDirectory ==");

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "oml-gd-test", ".minecraft"));
        var game = new GameDirectory(root);

        check("versions 目录", game.VersionsDirectory == Path.Combine(root, "versions"));
        check("libraries 目录", game.LibrariesDirectory == Path.Combine(root, "libraries"));
        check("assets/indexes 目录",
            game.AssetsIndexesDirectory == Path.Combine(root, "assets", "indexes"));
        check("客户端 jar 路径",
            game.GetVersionJarPath("1.21.1") == Path.Combine(root, "versions", "1.21.1", "1.21.1.jar"));
        check("版本 json 路径",
            game.GetVersionJsonPath("1.21.1") == Path.Combine(root, "versions", "1.21.1", "1.21.1.json"));
        check("库文件路径按 Maven 坐标展开",
            game.GetLibraryPath("org.lwjgl:lwjgl:3.3.3") ==
            Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3.jar"));
        check("清单缓存路径",
            game.ManifestCachePath == Path.Combine(root, "versions", "version_manifest_v2.json"));

        var traversalBlocked = false;
        try
        {
            game.GetVersionJsonPath("../../evil");
        }
        catch (ArgumentException)
        {
            traversalBlocked = true;
        }

        check("路径穿越被拒绝（../evil）", traversalBlocked);

        var isolated = new GameDirectory(root, versionIsolation: true);
        check("版本隔离：游戏根为 versions/<id>",
            isolated.GetGameRoot("1.21.1") == isolated.GetVersionDirectory("1.21.1"));
        check("非隔离：游戏根为 .minecraft",
            game.GetGameRoot("1.21.1") == root);

        Console.WriteLine("== 15. inheritsFrom 合并 ==");

        const string parentJson = """
        {
          "id": "1.21.1",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "17",
          "assetIndex": { "id": "17", "sha1": "aa", "size": 1, "totalSize": 2, "url": "https://example/17.json" },
          "downloads": { "client": { "sha1": "bb", "size": 10, "url": "https://example/client.jar" } },
          "arguments": {
            "game": [ "--username", "${auth_player_name}" ],
            "jvm": [ "-cp", "${classpath}" ]
          },
          "libraries": [
            {
              "name": "com.google.guava:guava:32.1.2-jre",
              "downloads": { "artifact": { "path": "com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar", "sha1": "old", "size": 1, "url": "https://example/guava-old.jar" } }
            },
            { "name": "org.ow2.asm:asm:9.6" }
          ]
        }
        """;

        const string childJson = """
        {
          "id": "1.21.1-forge-51.0.0",
          "inheritsFrom": "1.21.1",
          "type": "release",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "arguments": {
            "game": [ "--launchTarget", "forge_client" ],
            "jvm": [ "-DlibraryDirectory=${library_directory}" ]
          },
          "libraries": [
            {
              "name": "com.google.guava:guava:33.0.0-jre",
              "downloads": { "artifact": { "path": "com/google/guava/guava/33.0.0-jre/guava-33.0.0-jre.jar", "sha1": "new", "size": 2, "url": "https://example/guava-new.jar" } }
            },
            { "name": "net.minecraftforge:forge:51.0.0" },
            { "name": "org.ow2.asm:asm:9.7" }
          ]
        }
        """;

        var parent = JsonSerializer.Deserialize<VersionDetail>(parentJson, OmlJsonOptions.Default)!;
        var child = JsonSerializer.Deserialize<VersionDetail>(childJson, OmlJsonOptions.Default)!;
        var merged = VersionInheritor.Merge(parent, child);

        check("mainClass 由子级覆盖", merged.MainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher");
        check("id 取子级", merged.Id == "1.21.1-forge-51.0.0");
        check("库数量 = 子级新增 1 个（同名覆盖不增加）", merged.Libraries.Count == 3);
        check("同名库被覆盖但保留父级位置",
            merged.Libraries[0].Name == "com.google.guava:guava:33.0.0-jre" &&
            merged.Libraries[0].Downloads?.Artifact?.Sha1 == "new");
        check("同 group:artifact 不同版本视为覆盖（asm 9.6 → 9.7）",
            merged.Libraries[1].Name == "org.ow2.asm:asm:9.7");
        check("子级新增库追加到末尾",
            merged.Libraries[2].Name == "net.minecraftforge:forge:51.0.0");
        check("game 参数父前子后拼接",
            merged.Arguments?.Game.Count == 4 &&
            merged.Arguments.Game[0].GetString() == "--username" &&
            merged.Arguments.Game[2].GetString() == "--launchTarget");
        check("jvm 参数父前子后拼接",
            merged.Arguments?.Jvm.Count == 3 &&
            merged.Arguments.Jvm[2].GetString() == "-DlibraryDirectory=${library_directory}");
        check("assetIndex / assets / downloads 从父级继承",
            merged.AssetIndex?.Id == "17" &&
            merged.Assets == "17" &&
            merged.Downloads?.Client?.Sha1 == "bb");
        check("合并不修改父对象", parent.Libraries.Count == 2 && parent.MainClass == "net.minecraft.client.main.Main");

        var chained = VersionInheritor.MergeChain(new[] { child, parent });
        check("MergeChain 与单次 Merge 结果一致",
            chained.MainClass == merged.MainClass &&
            chained.Libraries.Count == merged.Libraries.Count &&
            chained.Arguments?.Game.Count == merged.Arguments?.Game.Count);

        var stringArgs = VersionInheritor.EnumerateStringArguments(merged.Arguments!.Game).ToList();
        check("可平铺取出字符串参数", stringArgs.Count == 4 && stringArgs.Contains("--username"));
    }

    /// <summary>联网断言：真实抓取清单、下载 client.jar、验证离线回退。</summary>
    public static async Task RunNetworkAsync(Action<string, bool> check, string gameRoot)
    {
        if (Directory.Exists(gameRoot))
        {
            Directory.Delete(gameRoot, true);
        }

        Directory.CreateDirectory(gameRoot);
        var game = new GameDirectory(gameRoot);
        var workRoot = Path.GetDirectoryName(gameRoot)!;

        Console.WriteLine("== 16. VersionRepository（联网）==");
        using var repo = new VersionRepository(game);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var manifest = await repo.GetManifestAsync(forceRefresh: true);
        started.Stop();

        check("清单抓取成功", manifest.Versions.Count > 50);
        check("清单已缓存到本地",
            File.Exists(game.ManifestCachePath) && new FileInfo(game.ManifestCachePath).Length > 0);
        Console.WriteLine($"   版本数 {manifest.Versions.Count}，耗时 {started.ElapsedMilliseconds} ms");

        var latest = await repo.GetLatestReleaseAsync();
        check("能取到最新正式版", !string.IsNullOrWhiteSpace(latest));
        check("最新正式版存在于清单", manifest.Versions.Any(v => v.Id == latest));
        Console.WriteLine($"   最新正式版：{latest}（最新快照 {manifest.Latest?.Snapshot}）");

        var detail = await repo.GetVersionDetailAsync(latest, forceRefresh: true);
        check("版本详情抓取成功", detail.Id == latest);
        check("详情已缓存到 versions/<id>/<id>.json", File.Exists(game.GetVersionJsonPath(latest)));
        check("详情含 client 下载信息",
            detail.Downloads?.Client?.Url is not null && detail.Downloads.Client.Sha1 is not null);

        var resolved = await repo.GetResolvedVersionAsync(latest);
        check("无继承版本解析后仍为原版",
            resolved.MainClass == detail.MainClass && resolved.Libraries.Count == detail.Libraries.Count);
        check("本地版本列表包含最新版", repo.EnumerateLocalVersionIds().Contains(latest));

        Console.WriteLine("== 17. 下载 client.jar 到标准位置 ==");
        var client = detail.Downloads!.Client!;
        var jarPath = game.GetVersionJarPath(latest);
        var mirrorJarUrl = BmclApiPathMapper.Map(client.Url!);
        check("client.jar 可映射到 BMCLAPI", mirrorJarUrl.Contains("bmclapi", StringComparison.OrdinalIgnoreCase));

        var jarProgress = new SyncProgress();
        var downloadWatch = System.Diagnostics.Stopwatch.StartNew();
        using (var downloader = new PclStyleDownloader())
        {
            await downloader.DownloadAsync(new DownloadTask
            {
                Url = client.Url!,
                SavePath = jarPath,
                ExpectedSize = client.Size,
                Sha1 = client.Sha1,
            }, jarProgress);
        }

        downloadWatch.Stop();

        var jarInfo = new FileInfo(jarPath);
        var jarSha1 = await Sha1Async(jarPath);

        check("client.jar 已落到 versions/<id>/<id>.jar",
            File.Exists(jarPath) && jarPath.EndsWith(Path.Combine(latest, $"{latest}.jar"), StringComparison.OrdinalIgnoreCase));
        check("client.jar 大小与清单一致", jarInfo.Length == client.Size);
        check("client.jar SHA-1 与清单一致", jarSha1 == client.Sha1);
        check("下载过程有进度上报", jarProgress.Snapshot().Count > 0);
        Console.WriteLine($"   大小 {jarInfo.Length:N0} 字节，耗时 {downloadWatch.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"   SHA-1 {jarSha1}");

        Console.WriteLine("== 18. 离网可用（模拟断网）==");
        using var offlineRepo = new VersionRepository(
            game,
            new HttpClient(new ThrowingHandler()),
            new BmclApiSourceProvider());

        var cachedManifest = await offlineRepo.GetManifestAsync(forceRefresh: true);
        check("断网时回退本地清单缓存", cachedManifest.Versions.Count == manifest.Versions.Count);

        var cachedDetail = await offlineRepo.GetVersionDetailAsync(latest, forceRefresh: true);
        check("断网时回退本地版本详情缓存", cachedDetail.Id == latest && cachedDetail.MainClass == detail.MainClass);

        Console.WriteLine($"   游戏目录：{workRoot}");
    }

    private static async Task<string> Sha1Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA1.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>模拟断网：任何请求立即失败，用于验证缓存回退路径。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("模拟断网（测试用）");
    }
}
