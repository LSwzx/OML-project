using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;
using OML.Core.Assets;
using OML.Core.Downloads;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Test;

/// <summary>阶段 5：规则求值、库解析、natives 解压、资源补全。</summary>
internal static class Stage5Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 19. RuleEvaluator ==");
        var platform = new PlatformInfo("windows", "x86_64", "10.0.26100");
        var rules = new RuleEvaluator(platform);

        check("无 rules 默认允许", rules.Evaluate(null) && rules.Evaluate(Array.Empty<Rule>()));
        check("allow windows 生效", rules.Evaluate([Allow(os: "windows")]));
        check("allow osx 不生效", !rules.Evaluate([Allow(os: "osx")]));
        check("仅有 disallow 且匹配 → 拒绝", !rules.Evaluate([Disallow(os: "windows")]));
        check("前一条不匹配、后一条匹配 → 允许",
            rules.Evaluate([Allow(os: "osx"), Allow(os: "windows")]));
        check("顺序敏感：allow 之后 disallow → 拒绝",
            !rules.Evaluate([Allow(os: "windows"), Disallow(os: "windows")]));
        check("架构匹配 x86_64", rules.Evaluate([Allow(os: "windows", arch: "x86_64")]));
        check("架构别名 amd64 归一", rules.Evaluate([Allow(arch: "amd64")]));
        check("架构不匹配（x86 规则跑在 x64 上）→ 拒绝",
            !rules.Evaluate([Allow(os: "windows", arch: "x86")]));
        check("操作系统版本正则匹配", rules.Evaluate([Allow(os: "windows", version: "^10\\.")]));
        check("操作系统版本正则不匹配", !rules.Evaluate([Allow(os: "windows", version: "^6\\.")]));

        var featureRule = Allow(features: new Dictionary<string, bool> { ["has_custom_resolution"] = true });
        check("features 未声明按 false 处理", !rules.Evaluate([featureRule]));
        var withFeatures = new RuleEvaluator(platform, new Dictionary<string, bool> { ["has_custom_resolution"] = true });
        check("features 声明为 true 后匹配", withFeatures.Evaluate([featureRule]));

        Console.WriteLine("== 20. NativesExtractor ==");

        var work = Path.Combine(Path.GetTempPath(), "oml-stage5-local");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(work);

        var jarPath = Path.Combine(work, "natives.jar");
        using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0");
            AddEntry(archive, "lwjgl.dll", "fake-dll");
            AddEntry(archive, "lib.so", "not-for-windows");
            AddEntry(archive, "sub/helper.dll", "fake-helper");
            AddEntry(archive, "../evil.dll", "zip-slip");
        }

        var nativesDir = Path.Combine(work, "natives");
        var extracted = NativesExtractor.Extract([jarPath], nativesDir);

        check("只解压 .dll", extracted.Count == 2 && extracted.All(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        check("跳过 META-INF 与非 dll", !extracted.Any(p => p.Contains("META-INF") || p.EndsWith(".so")));
        check("子目录 dll 被压平到根目录",
            File.Exists(Path.Combine(nativesDir, "helper.dll")) &&
            !Directory.Exists(Path.Combine(nativesDir, "sub")));
        check("Zip Slip 被拦截", !File.Exists(Path.Combine(work, "evil.dll")));

        var nestedDir = Path.Combine(work, "natives-nested");
        NativesExtractor.Extract([jarPath], nestedDir, flatten: false);
        check("flatten=false 时保留原目录结构", File.Exists(Path.Combine(nestedDir, "sub", "helper.dll")));

        Console.WriteLine("== 21. LibraryResolver ==");

        const string libsJson = """
        {
          "id": "test",
          "libraries": [
            {
              "name": "org.lwjgl:lwjgl:3.3.3",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", "sha1": "a1", "size": 100, "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar" } }
            },
            {
              "name": "org.lwjgl:lwjgl:3.3.3:natives-windows",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar", "sha1": "a2", "size": 10, "url": "https://libraries.minecraft.net/x/natives-windows.jar" } },
              "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
            },
            {
              "name": "org.lwjgl:lwjgl:3.3.3:natives-windows-x86",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-x86.jar", "sha1": "a3", "size": 10, "url": "https://libraries.minecraft.net/x/natives-windows-x86.jar" } },
              "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
            },
            {
              "name": "org.lwjgl:lwjgl:3.3.3:natives-windows-arm64",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-arm64.jar", "sha1": "a4", "size": 10, "url": "https://libraries.minecraft.net/x/natives-windows-arm64.jar" } },
              "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
            },
            {
              "name": "net.java:jinput-platform:2.0.5",
              "url": "https://libraries.minecraft.net/",
              "natives": { "windows": "natives-windows" },
              "downloads": {
                "artifact": { "path": "net/java/jinput-platform/2.0.5/jinput-platform-2.0.5.jar", "sha1": "a5", "size": 20, "url": "https://libraries.minecraft.net/net/java/jinput-platform/2.0.5/jinput-platform-2.0.5.jar" },
                "classifiers": { "natives-windows": { "path": "net/java/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar", "sha1": "a6", "size": 5, "url": "https://libraries.minecraft.net/net/java/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar" } }
              }
            },
            {
              "name": "org.lwjgl:lwjgl-platform:2.9.4",
              "url": "https://libraries.minecraft.net/",
              "natives": { "windows": "natives-windows-${arch}" },
              "downloads": {
                "artifact": { "path": "org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4.jar", "sha1": "a7", "size": 30, "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4.jar" },
                "classifiers": { "natives-windows-64": { "path": "org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows-64.jar", "sha1": "a8", "size": 6, "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows-64.jar" } }
              }
            },
            {
              "name": "ca.weblite:java-objc-bridge:1.1",
              "downloads": { "artifact": { "path": "ca/weblite/java-objc-bridge/1.1/java-objc-bridge-1.1.jar", "sha1": "a9", "size": 40, "url": "https://libraries.minecraft.net/x/osx.jar" } },
              "rules": [ { "action": "allow", "os": { "name": "osx" } } ]
            },
            {
              "name": "com.example:arch-only:1.0",
              "downloads": { "artifact": { "path": "com/example/arch-only/1.0/arch-only-1.0.jar", "sha1": "b1", "size": 50, "url": "https://libraries.minecraft.net/x/arch.jar" } },
              "rules": [ { "action": "allow", "os": { "name": "windows", "arch": "x86" } } ]
            }
          ]
        }
        """;

        var game = new GameDirectory(Path.Combine(work, ".minecraft"));
        var version = JsonSerializer.Deserialize<VersionDetail>(libsJson, OmlJsonOptions.Default)!;
        var resolution = new LibraryResolver(game, new RuleEvaluator(platform)).Resolve(version);

        check("过滤掉 osx 专属库", resolution.Classpath.All(p => !p.Contains("objc-bridge")));
        check("过滤掉非本机架构的库", resolution.Classpath.All(p => !p.Contains("arch-only")));
        check("classpath 含主构件与现代 natives jar",
            resolution.Classpath.Count == 4 &&
            resolution.Classpath.Any(p => p.Contains("jinput-platform-2.0.5.jar")) &&
            resolution.Classpath.Any(p => p.Contains("lwjgl-3.3.3-natives-windows.jar")) &&
            resolution.Classpath.All(p => !p.Contains("-native") || p.Contains("-natives-windows.jar")) &&
            !resolution.Classpath.Any(p => p.Contains("natives-windows-64")));
        check("natives 只选 x64 通用变体",
            resolution.Natives.Count == 3 &&
            resolution.Natives.Any(n => n.Classifier == "natives-windows" && n.JarPath.Contains("lwjgl-3.3.3-natives-windows.jar")) &&
            resolution.Natives.All(n => !n.JarPath.Contains("-x86") && !n.JarPath.Contains("-arm64")));
        check("旧格式 ${arch} 替换为 64",
            resolution.Natives.Any(n => n.Classifier == "natives-windows-64" && n.JarPath.Contains("natives-windows-64.jar")));
        check("下载任务数 = 3 主构件 + 3 natives", resolution.Downloads.Count == 6);
        check("旧格式无 artifact.url 时按 Maven 基地址拼出地址",
            resolution.Downloads.Any(d => d.Url == "https://libraries.minecraft.net/net/java/jinput-platform/2.0.5/jinput-platform-2.0.5.jar"));

        Console.WriteLine("== 22. AssetDownloader 路径规则 ==");

        var indexJson = """
        {
          "id": "17",
          "objects": {
            "icons/icon_16x16.png": { "hash": "abcdef0123456789abcdef0123456789abcdef01", "size": 781 },
            "minecraft/sounds/a.ogg": { "hash": "1234567890abcdef1234567890abcdef12345678", "size": 2048 }
          }
        }
        """;

        var index = JsonSerializer.Deserialize<AssetIndex>(indexJson, OmlJsonOptions.Default)!;
        using var assets = new AssetDownloader(game);
        var tasks = assets.CreateTasks(index);

        check("解析出 2 个对象", index.Objects.Count == 2 && tasks.Count == 2);
        check("落点为 assets/objects/<h2>/<hash>",
            tasks.All(t => t.SavePath.Contains(Path.Combine("assets", "objects")) &&
                           Path.GetFileName(Path.GetDirectoryName(t.SavePath)) == Path.GetFileName(t.SavePath)[..2]));
        check("URL 为 resources.download.minecraft.net/<h2>/<hash>",
            tasks.Any(t => t.Url == "https://resources.download.minecraft.net/ab/abcdef0123456789abcdef0123456789abcdef01"));
        check("limit 生效", assets.CreateTasks(index, limit: 1).Count == 1);
        check("镜像改写为 /assets/ 前缀",
            assets.CreateTasks(index, preferMirror: true).Any(t =>
                t.Url.StartsWith("https://bmclapi2.bangbang93.com/assets/", StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine("== 22b. 批量下载并发与失败隔离 ==");

        var probe = new ConcurrencyProbe();
        var batchTasks = Enumerable.Range(0, 40)
            .Select(i => new DownloadTask
            {
                Url = i == 7 ? "fail" : "ok",
                SavePath = Path.Combine(work, $"batch-{i}.bin"),
            })
            .ToList();

        var batchResult = DownloadBatchRunner.RunAsync(batchTasks, probe, maxConcurrency: 4).GetAwaiter().GetResult();

        check("并发不超过上限 4", probe.MaxConcurrent <= 4);
        check("单点失败不影响整批", batchResult.Succeeded == 39 && batchResult.FailedCount == 1);
        check("失败任务被记录", batchResult.Failures.Single().Task.Url == "fail");
    }

    public static async Task RunNetworkAsync(Action<string, bool> check, string gameRoot)
    {
        // 不删除已有目录：库文件按 SHA-1 短路，重复运行只补缺失项，避免每次重下 66MB。
        Directory.CreateDirectory(gameRoot);
        var game = new GameDirectory(gameRoot);

        using var repo = new VersionRepository(game);
        var detail = await repo.GetVersionDetailAsync("1.21.1", forceRefresh: true);

        Console.WriteLine("== 23. 解析 1.21.1 依赖 ==");
        var resolver = new LibraryResolver(game, RuleEvaluator.ForCurrentPlatform());
        var resolution = resolver.Resolve(detail);

        check("库解析无警告", resolution.Warnings.Count == 0);
        if (resolution.Warnings.Count > 0)
        {
            foreach (var warning in resolution.Warnings.Take(5))
            {
                Console.WriteLine($"   [WARN] {warning}");
            }
        }

        check("classpath 条目数合理（> 40）", resolution.Classpath.Count > 40);
        check("natives 任务 > 0", resolution.Natives.Count > 0);
        check("natives 全部是 windows 变体",
            resolution.Natives.All(n => n.Classifier.StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase)));

        var cp = resolution.ToClasspathString();
        Console.WriteLine($"   libraries={detail.Libraries.Count} classpath={resolution.Classpath.Count} 条目 / {cp.Length:N0} 字符");
        Console.WriteLine($"   下载任务={resolution.Downloads.Count}（含 natives），natives 组={resolution.Natives.Count}");
        foreach (var sample in resolution.Classpath.Take(3))
        {
            Console.WriteLine($"   cp: {Path.GetFileName(sample)}");
        }

        check("classpath 每个条目都在 libraries 下",
            resolution.Classpath.All(p => p.StartsWith(game.LibrariesDirectory, StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine("== 24. 下载全部库（并发 8）==");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new PclStyleDownloader();
        var batch = await DownloadBatchRunner.RunAsync(resolution.Downloads, engine, maxConcurrency: 8);
        watch.Stop();

        check("库文件全部下载成功", batch.AllSucceeded);
        foreach (var failure in batch.Failures.Take(5))
        {
            Console.WriteLine($"   [FAIL] {Path.GetFileName(failure.Task.SavePath)}: {failure.Error.Message}");
        }

        var missing = resolution.Downloads.Where(d => !File.Exists(d.SavePath)).ToList();
        check("所有库文件存在", missing.Count == 0);

        var checksumMismatch = 0;
        foreach (var task in resolution.Downloads.Where(d => d.Sha1 is not null && File.Exists(d.SavePath)))
        {
            var actual = await Sha1Async(task.SavePath);
            if (!string.Equals(actual, task.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                checksumMismatch++;
            }
        }

        check("全部库文件 SHA-1 校验通过", checksumMismatch == 0);
        Console.WriteLine($"   耗时 {watch.Elapsed.TotalSeconds:0.0} s，失败 {batch.FailedCount} 个");

        Console.WriteLine("== 25. 解压 natives ==");
        var nativesDirectory = game.GetVersionNativesDirectory("1.21.1");
        var extracted = NativesExtractor.Extract(resolution.Natives.Select(n => n.JarPath), nativesDirectory);

        check("解压出 dll", extracted.Count > 0);
        check("全部是 .dll", extracted.All(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        check("落点在 versions/1.21.1/natives 下", extracted.All(p => p.StartsWith(nativesDirectory, StringComparison.OrdinalIgnoreCase)));
        check("dll 压平后直接位于 natives 根目录（java.library.path 可发现）",
            extracted.All(p => string.Equals(
                Path.GetDirectoryName(p), nativesDirectory, StringComparison.OrdinalIgnoreCase)));
        check("包含 lwjgl.dll", extracted.Any(p => Path.GetFileName(p).Equals("lwjgl.dll", StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine($"   解压 {extracted.Count} 个文件，例如：{string.Join(", ", extracted.Select(Path.GetFileName).Take(6))}");

        Console.WriteLine("== 26. 资源补全（前 20 个）==");
        using var assets = new AssetDownloader(game);
        var index = await assets.GetIndexAsync(detail.AssetIndex!);
        check("资源索引对象数 > 3000", index.Objects.Count > 3000);

        var assetTasks = assets.CreateTasks(index, limit: 20);
        check("limit=20 生效", assetTasks.Count == 20);

        // 资源走官方源：BMCLAPI 对并发资源请求会限流，连测会持续 429；
        // 镜像改写能力已由离线用例断言。
        var assetBatch = await assets.DownloadAsync(index, engine, maxConcurrency: 8, limit: 20, preferMirror: false);
        check("20 个资源全部下载成功", assetBatch.AllSucceeded);
        foreach (var failure in assetBatch.Failures.Take(5))
        {
            Console.WriteLine($"   [FAIL] {Path.GetFileName(failure.Task.SavePath)}: {failure.Error.Message}");
        }
        check("资源落盘路径为 <h2>/<hash>",
            assetTasks.All(t =>
                File.Exists(t.SavePath) &&
                Path.GetFileName(Path.GetDirectoryName(t.SavePath)) == Path.GetFileName(t.SavePath)[..2]));

        var assetHashMismatch = 0;
        foreach (var task in assetTasks)
        {
            if (!File.Exists(task.SavePath))
            {
                assetHashMismatch++;
                continue;
            }

            var actual = await Sha1Async(task.SavePath);
            if (!string.Equals(actual, Path.GetFileName(task.SavePath), StringComparison.OrdinalIgnoreCase))
            {
                assetHashMismatch++;
            }
        }

        check("资源内容 SHA-1 与文件名一致", assetHashMismatch == 0);
        Console.WriteLine($"   索引对象 {index.Objects.Count} 个，示例：{Path.GetFileName(assetTasks[0].SavePath)}");

        // 镜像路径端到端：用独立目录 + 低并发，避免把公共镜像打到限流。
        using var mirrorAssets = new AssetDownloader(new GameDirectory(gameRoot + "-mirror"));
        var mirrorBatch = await mirrorAssets.DownloadAsync(
            index, engine, maxConcurrency: 2, limit: 5, preferMirror: true, retryPasses: 2);
        check("镜像源资源补全可用（并发 2，前 5 个）", mirrorBatch.AllSucceeded);

        Console.WriteLine($"   游戏目录：{gameRoot}");
    }

    private static Rule Allow(string? os = null, string? arch = null, string? version = null, Dictionary<string, bool>? features = null) =>
        new()
        {
            Action = "allow",
            Os = os is null && arch is null && version is null
                ? null
                : new RuleOs { Name = os, Arch = arch, Version = version },
            Features = features,
        };

    private static Rule Disallow(string? os = null) =>
        new() { Action = "disallow", Os = new RuleOs { Name = os } };

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    /// <summary>并发探针：记录峰值并发，并可按 URL 注入失败。</summary>
    private sealed class ConcurrencyProbe : IDownloadEngine
    {
        private int _current;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public async Task DownloadAsync(
            DownloadTask task,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _current);
            int observed;
            do
            {
                observed = Volatile.Read(ref _max);
            }
            while (now > observed && Interlocked.CompareExchange(ref _max, now, observed) != observed);

            try
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                if (task.Url == "fail")
                {
                    throw new InvalidOperationException("注入的失败");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private static async Task<string> Sha1Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
