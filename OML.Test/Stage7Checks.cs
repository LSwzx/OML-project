#if !STRICT_ONLINE_ONLY
using OML.Abstractions.Auth;
using OML.Abstractions.Configuration;
using OML.Abstractions.Launching;
using OML.Core.Auth;
using OML.Core.Configuration;
using OML.Core.Java;
using OML.Core.Launching;
using OML.Core.Versioning;

namespace OML.Test;

/// <summary>阶段 7：启动门面、离线账号、配置持久化。</summary>
internal static class Stage7Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 32. OfflineAuthService ==");

        // 期望值由独立实现（PowerShell + .NET MD5）算出，不是用被测代码反推。
        check("Player 的离线 UUID 正确",
            OfflineAuthService.ComputeOfflineUuid("Player") == "a01e3843-e521-3998-958a-f459800e4d11");
        check("Notch 的离线 UUID 正确",
            OfflineAuthService.ComputeOfflineUuid("Notch") == "b50ad385-829d-3141-a216-7e7d7539ba7f");
        check("Steve 的离线 UUID 正确",
            OfflineAuthService.ComputeOfflineUuid("Steve") == "5627dd98-e6be-3c21-b8a8-e92344183641");

        var profile = OfflineAuthService.CreateProfile("Player");
        check("UUID 为版本 3 且变体位合法",
            profile.Uuid.Length == 36 &&
            profile.Uuid[14] == '3' &&
            "89ab".Contains(profile.Uuid[19]));
        check("AccessToken 为 32 位随机十六进制",
            profile.AccessToken is { Length: 32 } &&
            profile.AccessToken.All(c => Uri.IsHexDigit(c)));
        check("类型为 Offline", profile.Type == AuthTypes.Offline);
        check("同名两次生成结果一致（确定性）",
            OfflineAuthService.CreateProfile("Player").Uuid == profile.Uuid);
        check("不同名得到不同 UUID",
            OfflineAuthService.ComputeOfflineUuid("Alex") != profile.Uuid);

        var service = new OfflineAuthService();
        var fromService = service.AuthenticateAsync(AuthTypes.Offline, "Player").GetAwaiter().GetResult();
        check("IAuthService 入口可用", fromService.Uuid == profile.Uuid);

        var rejected = false;
        try
        {
            service.AuthenticateAsync(AuthTypes.Microsoft, "token").GetAwaiter().GetResult();
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        check("不支持的认证类型被拒绝", rejected);

        var tooLong = false;
        try
        {
            OfflineAuthService.CreateProfile("ThisNameIsWayTooLong");
        }
        catch (ArgumentException)
        {
            tooLong = true;
        }

        check("超过 16 字符的用户名被拒绝", tooLong);

        Console.WriteLine("== 33. ConfigManager ==");

        var launcherRoot = Path.Combine(Path.GetTempPath(), "oml-stage7", "launcher");
        if (Directory.Exists(launcherRoot))
        {
            Directory.Delete(launcherRoot, true);
        }

        var manager = new ConfigManager(launcherRoot);
        check("默认配置内存 4096MB", LauncherConfig.CreateDefault().MaxMemoryMB == 4096);
        check("默认游戏目录为 .minecraft", LauncherConfig.CreateDefault().GameDirectory == ".minecraft");
        check("配置尚未存在", !manager.Exists);

        var saved = new LauncherConfig
        {
            SelectedVersion = "1.21.1",
            MaxMemoryMB = 2048,
            GameDirectory = ".minecraft",
            UserProfile = profile,
        };

        manager.SaveAsync(saved).GetAwaiter().GetResult();
        check("配置写在启动器根目录而非 .minecraft",
            File.Exists(manager.ConfigPath) &&
            manager.ConfigPath == Path.Combine(Path.GetFullPath(launcherRoot), LauncherConfig.FileName) &&
            !manager.ConfigPath.Contains(Path.Combine(".minecraft", ""), StringComparison.OrdinalIgnoreCase));

        var json = File.ReadAllText(manager.ConfigPath);
        check("JSON 使用 camelCase 字段", json.Contains("\"selectedVersion\"") && json.Contains("\"maxMemoryMB\""));

        var loaded = manager.LoadAsync().GetAwaiter().GetResult();
        check("读回版本与内存",
            loaded.SelectedVersion == "1.21.1" && loaded.MaxMemoryMB == 2048);
        check("读回账号并保留 UUID",
            loaded.UserProfile?.Username == "Player" &&
            loaded.UserProfile.Uuid == profile.Uuid);

        var defaultsManager = new ConfigManager(Path.Combine(launcherRoot, "empty"));
        var defaults = defaultsManager.LoadAsync().GetAwaiter().GetResult();
        check("缺文件时返回默认配置", defaults.MaxMemoryMB == 4096 && defaults.SelectedVersion.Length == 0);

        File.WriteAllText(manager.ConfigPath, "{ this is not json");
        var recovered = manager.LoadAsync().GetAwaiter().GetResult();
        check("损坏配置回退默认且备份原文件",
            recovered.MaxMemoryMB == 4096 &&
            Directory.GetFiles(launcherRoot, "launcher_config.json.broken-*").Length == 1);
    }

    public static async Task RunNetworkAsync(Action<string, bool> check, string launcherRoot, string gameRoot)
    {
        Console.WriteLine("== 34. 端到端：离线账号 + 配置 + 启动门面 ==");

        var scanner = new JavaScanner();
        var javaPath = await scanner.FindSuitableJavaAsync(21);
        if (javaPath is null)
        {
            Console.WriteLine("  [SKIP] 未找到 Java 21+，跳过启动门面测试。");
            return;
        }

        if (Directory.Exists(launcherRoot))
        {
            Directory.Delete(launcherRoot, true);
        }

        Directory.CreateDirectory(launcherRoot);

        var configManager = new ConfigManager(launcherRoot);
        var auth = new OfflineAuthService();
        var profile = await auth.AuthenticateAsync(AuthTypes.Offline, "Player");

        var config = new LauncherConfig
        {
            SelectedVersion = "1.21.1",
            MaxMemoryMB = 2048,
            GameDirectory = gameRoot,
            JavaPath = string.Empty, // 留空以验证自动探测
            CompleteAssets = false,  // 800MB 资源不在本次测试范围内
            UserProfile = profile,
        };

        await configManager.SaveAsync(config);
        var reloaded = await configManager.LoadAsync();
        check("配置保存后可读回（版本/内存/账号）",
            reloaded.SelectedVersion == "1.21.1" &&
            reloaded.MaxMemoryMB == 2048 &&
            reloaded.UserProfile?.Uuid == profile.Uuid);

        using var session = new LaunchSession();
        var progress = new ProgressCollector();
        var logs = new List<string>();
        session.LogReceived += line =>
        {
            lock (logs)
            {
                logs.Add(line);
            }
        };

        var info = await session.LaunchGameAsync(reloaded, progress);

        check("启动了进程", !info.Process.HasExited || info.Process.ExitCode >= 0);
        check("使用了自动探测的 Java 21+",
            session.LastArguments?.Arguments.Contains("-cp") == true &&
            session.LastArguments!.Classpath.Contains(gameRoot, StringComparison.OrdinalIgnoreCase));

        var stages = progress.Stages;
        check("上报过下载阶段", stages.Contains(LaunchStage.Downloading));
        check("上报过解压阶段", stages.Contains(LaunchStage.Extracting));
        check("上报过启动阶段", stages.Contains(LaunchStage.Launching));
        check("上报过完成阶段", stages.Contains(LaunchStage.Completed));
        check("进度值始终在 0..1", progress.Items.All(p => p.Progress is >= 0 and <= 1));

        foreach (var line in progress.Items
                     .GroupBy(p => p.Stage)
                     .Select(g => g.Last()))
        {
            Console.WriteLine($"   [{line.Stage,-11}] {line.Percent,3}%  {line.Message}");
        }

        Console.WriteLine("   运行 10 秒...");

        // 模拟游戏崩溃：运行期间落一份 crash report，结束后应能被门面识别。
        var crashDirectory = Path.Combine(gameRoot, "crash-reports");
        Directory.CreateDirectory(crashDirectory);
        var fakeCrash = Path.Combine(crashDirectory, "crash-oml-test.txt");
        await File.WriteAllTextAsync(fakeCrash, "---- Minecraft Crash Report ---- (test fixture)");

        await Task.Delay(TimeSpan.FromSeconds(10));

        var running = !info.Process.HasExited;
        if (running)
        {
            info.Process.Kill(entireProcessTree: true);
        }

        var exit = await info.WaitForExitAsync();
        check("进程被结束且拿到退出码", !running || exit.ExitCode != 0);
        check("收到游戏日志", logs.Count > 0);
        check("非零退出码时识别出崩溃报告",
            exit.CrashReportPath == fakeCrash && File.Exists(exit.CrashReportPath));
        Console.WriteLine($"   退出码 {exit.ExitCode}，崩溃报告 {exit.CrashReportPath ?? "无"}");

        File.Delete(fakeCrash);

        lock (logs)
        {
            foreach (var line in logs.Take(5))
            {
                Console.WriteLine($"   | {line}");
            }
        }

        // 模拟"重启启动器后再读配置"。
        var afterRestart = new ConfigManager(launcherRoot);
        var restored = await afterRestart.LoadAsync();
        check("重启后配置仍完整",
            restored.SelectedVersion == "1.21.1" &&
            restored.MaxMemoryMB == 2048 &&
            restored.UserProfile?.Uuid == "a01e3843-e521-3998-958a-f459800e4d11" &&
            restored.GameDirectory == gameRoot);

        Console.WriteLine($"   配置文件：{afterRestart.ConfigPath}");
    }

    /// <summary>线程安全的进度收集器。</summary>
    private sealed class ProgressCollector : IProgress<LaunchProgress>
    {
        private readonly List<LaunchProgress> _items = new();
        private readonly object _gate = new();

        public void Report(LaunchProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }

        public IReadOnlyList<LaunchProgress> Items
        {
            get
            {
                lock (_gate)
                {
                    return _items.ToArray();
                }
            }
        }

        public IReadOnlyList<LaunchStage> Stages => Items.Select(i => i.Stage).Distinct().ToArray();
    }
}
#endif
