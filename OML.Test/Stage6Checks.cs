using System.Diagnostics;
using System.Text.Json;
using OML.Abstractions.Auth;
using OML.Abstractions.Downloads;
using OML.Abstractions.Launching;
using OML.Abstractions.Models;
using OML.Core.Downloads;
using OML.Core.Java;
using OML.Core.Launching;
using OML.Core.Serialization;
using OML.Core.Versioning;

namespace OML.Test;

/// <summary>阶段 6：Java 探测、参数构建、预检、真实拉起进程。</summary>
internal static class Stage6Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 27. Java 版本解析 ==");
        check("Java 8 的 1.8.0_381 → 8",
            JavaScanner.ParseMajorVersion("java version \"1.8.0_381\"\nJava(TM) SE Runtime Environment") == 8);
        check("Java 17 → 17",
            JavaScanner.ParseMajorVersion("openjdk version \"17.0.10\" 2024-01-16") == 17);
        check("Java 21 → 21",
            JavaScanner.ParseMajorVersion("openjdk version \"21.0.2\" 2024-01-16 LTS") == 21);
        check("Java 25 → 25",
            JavaScanner.ParseMajorVersion("java version \"25\" 2025-09-16") == 25);
        check("无法解析时返回 null", JavaScanner.ParseMajorVersion("not a java output") is null);

        Console.WriteLine("== 28. LaunchArgumentBuilder ==");

        const string versionJson = """
        {
          "id": "1.21.1",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "17",
          "arguments": {
            "jvm": [ "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" ],
            "game": [
              "--username", "${auth_player_name}",
              "--version", "${version_name}",
              "--gameDir", "${game_directory}",
              "--assetsDir", "${assets_root}",
              "--assetIndex", "${assets_index_name}",
              "--uuid", "${auth_uuid}",
              "--accessToken", "${auth_access_token}",
              "--userType", "${user_type}",
              "--versionType", "${version_type}",
              {
                "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ],
                "value": "--demo"
              },
              {
                "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
                "value": [ "--width", "${resolution_width}", "--height", "${resolution_height}" ]
              }
            ]
          }
        }
        """;

        var work = Path.Combine(Path.GetTempPath(), "oml-stage6-local");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(work);

        var root = Path.Combine(work, ".minecraft");
        var game = new GameDirectory(root);
        var version = JsonSerializer.Deserialize<VersionDetail>(versionJson, OmlJsonOptions.Default)!;
        var libraries = new LibraryResolution
        {
            Classpath = new[] { Path.Combine(root, "libraries", "a.jar"), Path.Combine(root, "libraries", "b.jar") },
        };

        var user = new UserProfile
        {
            Username = "Player",
            Uuid = "00000000-0000-0000-0000-000000000000",
            AccessToken = null,
            Type = AuthTypes.Offline,
        };

        var options = new LaunchOptions
        {
            GameDirectory = game,
            Version = version,
            Libraries = libraries,
            User = user,
            JavaPath = @"C:\fake\java.exe",
            MaxMemoryMb = 2048,
        };

        var built = new LaunchArgumentBuilder().Build(options);

        check("无未定义变量警告", built.Warnings.Count == 0);
        check("含 -Xmx 内存参数", built.Arguments.Contains("-Xmx2048M"));
        check("含品牌参数", built.Arguments.Any(a => a.StartsWith("-Dminecraft.launcher.brand=", StringComparison.Ordinal)));
        check("-Djava.library.path 指向 versions/<id>/natives",
            built.Arguments.Any(a => a == @"-Djava.library.path=" + Path.Combine(root, "versions", "1.21.1", "natives")));
        check("-cp 后紧跟 classpath（分号分隔）",
            built.Arguments.Contains("-cp") &&
            built.Arguments[built.Arguments.ToList().IndexOf("-cp") + 1] == libraries.ToClasspathString());

        var mainIndex = built.Arguments.ToList().IndexOf("net.minecraft.client.main.Main");
        check("主类位于 -cp 之后", mainIndex > built.Arguments.ToList().IndexOf("-cp"));

        check("离线身份与路径变量已替换",
            built.Arguments.Contains("Player") &&
            built.Arguments.Contains("00000000-0000-0000-0000-000000000000") &&
            built.Arguments.Contains("0") &&
            built.Arguments.Contains("legacy"));
        check("默认不含 --demo 与分辨率参数",
            !built.Arguments.Contains("--demo") && !built.Arguments.Contains("--width"));
        check("--gameDir 指向游戏根", built.Arguments[built.Arguments.ToList().IndexOf("--gameDir") + 1] == root);
        check("--assetsDir 指向 assets", built.Arguments[built.Arguments.ToList().IndexOf("--assetsDir") + 1] == game.AssetsDirectory);

        var withFeatures = new LaunchArgumentBuilder().Build(new LaunchOptions
        {
            GameDirectory = game,
            Version = version,
            Libraries = libraries,
            User = user,
            Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true },
        });

        check("特性开启后出现 --width/--height",
            withFeatures.Arguments.Contains("--width") &&
            withFeatures.Arguments[withFeatures.Arguments.ToList().IndexOf("--width") + 1] == "854");

        var isolated = new LaunchArgumentBuilder().Build(new LaunchOptions
        {
            GameDirectory = new GameDirectory(root, versionIsolation: true),
            Version = version,
            Libraries = libraries,
            User = user,
        });

        check("版本隔离时 game_directory 指向 versions/<id>",
            isolated.Arguments[isolated.Arguments.ToList().IndexOf("--gameDir") + 1] ==
            Path.Combine(root, "versions", "1.21.1") &&
            isolated.WorkingDirectory == Path.Combine(root, "versions", "1.21.1"));

        // 旧版本：没有 arguments，只有 minecraftArguments。
        var legacy = JsonSerializer.Deserialize<VersionDetail>("""
        {
          "id": "1.7.10",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory} --width 800"
        }
        """, OmlJsonOptions.Default)!;

        var legacyBuilt = new LaunchArgumentBuilder().Build(new LaunchOptions
        {
            GameDirectory = game,
            Version = legacy,
            Libraries = libraries,
            User = user,
        });

        check("旧格式参数被拆分并替换",
            legacyBuilt.Arguments.Contains("--username") &&
            legacyBuilt.Arguments.Contains("Player") &&
            legacyBuilt.Arguments.Contains("800"));
        check("旧格式自动补 -cp 与 java.library.path",
            legacyBuilt.Arguments.Contains("-cp") &&
            legacyBuilt.Arguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)));

        Console.WriteLine("== 29. LaunchPrechecker ==");
        var versionDir = Path.Combine(root, "versions", "1.21.1");
        Directory.CreateDirectory(Path.Combine(root, "libraries"));
        Directory.CreateDirectory(Path.Combine(versionDir, "natives"));
        Directory.CreateDirectory(game.AssetsDirectory);

        foreach (var entry in libraries.Classpath)
        {
            File.WriteAllText(entry, "jar");
        }

        File.WriteAllText(Path.Combine(versionDir, "1.21.1.jar"), "jar");
        File.WriteAllText(Path.Combine(versionDir, "natives", "lwjgl.dll"), "dll");

        var withJar = new LaunchArgumentBuilder().Build(options);
        check("客户端 jar 进入 classpath 首位",
            withJar.Classpath.StartsWith(game.GetVersionJarPath("1.21.1") + Path.PathSeparator, StringComparison.OrdinalIgnoreCase));

        var prechecker = new LaunchPrechecker();
        var ok = prechecker.Check(options);
        check("文件齐全时通过", ok.IsValid);
        prechecker.Ensure(options);

        File.Delete(libraries.Classpath[0]);
        var missingLib = prechecker.Check(options);
        check("缺库文件时报错并列出文件名",
            !missingLib.IsValid && missingLib.MissingClasspathEntries.Count == 1);

        File.WriteAllText(libraries.Classpath[0], "jar");
        File.Delete(Path.Combine(versionDir, "1.21.1.jar"));
        var missingJar = prechecker.Check(options);
        check("缺客户端 jar 时报错", !missingJar.IsValid && missingJar.MissingClientJar is not null);

        var threw = false;
        try
        {
            prechecker.Ensure(options);
        }
        catch (LaunchPrecheckException ex)
        {
            threw = ex.Problems.Count > 0;
        }

        check("Ensure 抛出 LaunchPrecheckException", threw);
    }

    public static async Task RunNetworkAsync(Action<string, bool> check, string gameRoot)
    {
        Console.WriteLine("== 30. 扫描本机 Java ==");
        var scanner = new JavaScanner();
        var installations = await scanner.ScanAsync();

        foreach (var installation in installations)
        {
            Console.WriteLine($"   [{installation.Source}] Java {installation.MajorVersion}: {installation.Path}");
        }

        check("至少发现一个 Java", installations.Count > 0);

        var java21 = await scanner.FindSuitableJavaAsync(21);
        if (java21 is null)
        {
            Console.WriteLine("  [SKIP] 未找到 Java 21+，跳过真实启动测试。");
            return;
        }

        Console.WriteLine($"   选用：{java21}");

        var game = new GameDirectory(gameRoot);
        using var repo = new VersionRepository(game);
        var detail = await repo.GetVersionDetailAsync("1.21.1", forceRefresh: true);

        var resolver = new LibraryResolver(game, RuleEvaluator.ForCurrentPlatform());
        var resolution = resolver.Resolve(detail);

        var clientJar = game.GetVersionJarPath("1.21.1");
        if (!File.Exists(clientJar))
        {
            var client = detail.Downloads!.Client!;
            using var downloader = new PclStyleDownloader();
            await downloader.DownloadAsync(new DownloadTask
            {
                Url = client.Url!,
                SavePath = clientJar,
                ExpectedSize = client.Size,
                Sha1 = client.Sha1,
            });
        }

        check("客户端 jar 已就绪", File.Exists(clientJar));

        var user = new UserProfile
        {
            Username = "Player",
            Uuid = "00000000-0000-0000-0000-000000000000",
            AccessToken = null,
            Type = AuthTypes.Offline,
        };

        var options = new LaunchOptions
        {
            GameDirectory = game,
            Version = detail,
            Libraries = resolution,
            User = user,
            JavaPath = java21,
            MaxMemoryMb = 1024,
        };

        new LaunchPrechecker().Ensure(options);
        check("启动预检通过", true);

        var arguments = new LaunchArgumentBuilder().Build(options);
        check("参数构建无未定义变量", arguments.Warnings.Count == 0);
        foreach (var warning in arguments.Warnings.Take(5))
        {
            Console.WriteLine($"   [WARN] {warning}");
        }

        Console.WriteLine($"   参数条数 {arguments.Arguments.Count}，日志目录 {game.GetLogsDirectory("1.21.1")}");

        var logsDirectory = game.GetLogsDirectory("1.21.1");
        if (Directory.Exists(logsDirectory))
        {
            Directory.Delete(logsDirectory, true);
        }

        var logLines = new List<string>();
        using var launcher = new JvmLauncher(logsDirectory);
        launcher.OnLogReceived = line =>
        {
            lock (logLines)
            {
                logLines.Add(line);
            }
        };

        Console.WriteLine("== 31. 拉起 Minecraft（离线身份，10 秒后结束）==");
        using var process = await launcher.LaunchAsync(arguments);
        check("进程已启动", !process.HasExited || process.ExitCode >= 0);

        await Task.Delay(TimeSpan.FromSeconds(10));

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();

        string[] snapshot;
        lock (logLines)
        {
            snapshot = logLines.ToArray();
        }

        Console.WriteLine($"   收到 {snapshot.Length} 行日志，退出码 {SafeExitCode(process)}");
        foreach (var line in snapshot.Take(20))
        {
            Console.WriteLine($"   | {line}");
        }

        check("收到游戏日志", snapshot.Length > 0);
        check("捕获日志已写入 oml-launch.log", File.Exists(launcher.LogFilePath) && new FileInfo(launcher.LogFilePath).Length > 0);
        check("游戏自身写出了 logs/latest.log",
            File.Exists(Path.Combine(game.GetLogsDirectory("1.21.1"), "latest.log")));
        check("未与游戏争抢 latest.log（无 log4j 占用错误）",
            !snapshot.Any(l => l.Contains("Unable to delete file", StringComparison.OrdinalIgnoreCase)));
        check("日志出现 Minecraft/LWJGL 特征",
            snapshot.Any(l => l.Contains("LWJGL", StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Any(l => l.Contains("Setting user", StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Any(l => l.Contains("Backend library", StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Any(l => l.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)));

        Console.WriteLine($"   游戏目录：{gameRoot}");
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "still running";
        }
        catch
        {
            return "unknown";
        }
    }
}
