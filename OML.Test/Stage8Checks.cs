using OML.Abstractions.Launching;
using OML.Core.Java;
using OML.Core.Versioning;

namespace OML.Test;

/// <summary>阶段 8：Java 运行时自动下载、版本安装、本地扫描。</summary>
internal static class Stage8Checks
{
    public static void RunLocal(Action<string, bool> check)
    {
        Console.WriteLine("== 35. Java 平台映射 ==");

        check("windows x64 → windows-x64",
            JavaRuntimeDownloader.GetPlatformKey(new PlatformInfo("windows", "x86_64", "10")) == "windows-x64");
        check("windows arm64 → windows-arm64",
            JavaRuntimeDownloader.GetPlatformKey(new PlatformInfo("windows", "arm64", "10")) == "windows-arm64");
        check("windows x86 → windows-x86",
            JavaRuntimeDownloader.GetPlatformKey(new PlatformInfo("windows", "x86", "10")) == "windows-x86");
        check("linux x64 → linux",
            JavaRuntimeDownloader.GetPlatformKey(new PlatformInfo("linux", "x86_64", "")) == "linux");
        check("mac arm64 → mac-os-arm64",
            JavaRuntimeDownloader.GetPlatformKey(new PlatformInfo("osx", "arm64", "")) == "mac-os-arm64");

        Console.WriteLine("== 36. LocalVersionScanner ==");

        var root = Path.Combine(Path.GetTempPath(), "oml-stage8-local", ".minecraft");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        var game = new GameDirectory(root);
        Directory.CreateDirectory(game.VersionsDirectory);

        WriteVersion(game, "1.20.1", """
        {
          "id": "1.20.1",
          "type": "release",
          "releaseTime": "2023-06-12T13:25:51+00:00",
          "mainClass": "net.minecraft.client.main.Main",
          "libraries": []
        }
        """, withJar: true);

        WriteVersion(game, "1.20.1-forge", """
        {
          "id": "1.20.1-forge",
          "type": "release",
          "releaseTime": "2023-07-01T00:00:00+00:00",
          "inheritsFrom": "1.20.1",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "libraries": [ { "name": "net.minecraftforge:forge:47.2.0" } ]
        }
        """, withJar: false);

        WriteVersion(game, "1.20.1-fabric", """
        {
          "id": "1.20.1-fabric",
          "type": "release",
          "releaseTime": "2023-06-20T00:00:00+00:00",
          "inheritsFrom": "1.20.1",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "libraries": [ { "name": "net.fabricmc:fabric-loader:0.15.11" } ]
        }
        """, withJar: true);

        // 坏文件必须被跳过，而不是让整个列表打不开。
        var brokenDir = Path.Combine(game.VersionsDirectory, "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "broken.json"), "{ not json");

        var scanner = new LocalVersionScanner(game);
        var versions = scanner.Scan();

        check("扫描到 3 个有效版本（坏文件被跳过）", versions.Count == 3);
        check("按发布时间倒序",
            versions[0].Id == "1.20.1-forge" &&
            versions[1].Id == "1.20.1-fabric" &&
            versions[2].Id == "1.20.1");

        var vanilla = versions.Single(v => v.Id == "1.20.1");
        check("原版：非模组、有客户端 jar、发布时间正确",
            !vanilla.IsModded && vanilla.HasClientJar && vanilla.ReleaseTime?.Year == 2023);

        var forge = versions.Single(v => v.Id == "1.20.1-forge");
        check("Forge 版本被识别且无独立 jar",
            forge.IsModded && forge.Loader == "Forge" && !forge.HasClientJar);

        var fabric = versions.Single(v => v.Id == "1.20.1-fabric");
        check("Fabric 版本被识别", fabric.IsModded && fabric.Loader == "Fabric");

        check("DisplayName 带加载器标签", forge.DisplayName.Contains("Forge"));

        check("Find 能按 ID 命中", scanner.Find("1.20.1")?.Id == "1.20.1");
        check("Find 未命中返回 null", scanner.Find("9.9.9") is null);

        Console.WriteLine("== 37. InstallOptions 默认值 ==");
        var defaults = new InstallOptions();
        check("默认安装资源、解压 natives、不自动下载 Java",
            defaults.InstallAssets && defaults.ExtractNatives && !defaults.EnsureJava && defaults.AssetLimit is null);
    }

    public static async Task RunNetworkAsync(Action<string, bool> check, string launcherRoot, string gameRoot)
    {
        Console.WriteLine("== 38. Java 运行时自动下载 ==");

        Directory.CreateDirectory(launcherRoot);
        using var javaDownloader = new JavaRuntimeDownloader(launcherRoot);

        const string component = "java-runtime-gamma";
        JavaRuntimeInfo runtime;

        if (javaDownloader.IsInstalled(component))
        {
            runtime = new JavaRuntimeInfo(component, "cached", javaDownloader.GetJavaPath(component),
                javaDownloader.GetRuntimeDirectory(component), 0);
            Console.WriteLine("   已安装，跳过下载（复用缓存）");
        }
        else
        {
            var progress = new PercentProgress();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            runtime = await javaDownloader.DownloadAsync(component, progress);
            watch.Stop();
            Console.WriteLine($"   下载 {runtime.FileCount} 个文件，耗时 {watch.Elapsed.TotalSeconds:0.0} s");
        }

        check("java 可执行文件已落盘", File.Exists(runtime.JavaPath));
        check("运行时位于 runtimes/<component>/ 下",
            runtime.JavaPath.StartsWith(Path.Combine(Path.GetFullPath(launcherRoot), "runtimes", component),
                StringComparison.OrdinalIgnoreCase));

        var scanner = new JavaScanner();
        var major = await scanner.GetJavaMajorVersionAsync(runtime.JavaPath);
        check("下载的运行时能执行 java -version", major is not null);
        check("运行时为 Java 17（java-runtime-gamma）", major == 17);
        Console.WriteLine($"   {runtime.JavaPath} → Java {major}");

        Console.WriteLine("== 39. 安装 1.20.1 全流程 ==");

        // 清理测试目录：模拟全新安装。
        if (Directory.Exists(gameRoot))
        {
            Directory.Delete(gameRoot, true);
        }

        Directory.CreateDirectory(gameRoot);
        var game = new GameDirectory(gameRoot);

        using var installer = new VersionInstaller(game, launcherRoot: launcherRoot);
        var installProgress = new PrintProgress();

        var watchInstall = System.Diagnostics.Stopwatch.StartNew();
        var result = await installer.InstallVersionAsync(
            "1.20.1",
            new InstallOptions
            {
                InstallAssets = true,
                AssetLimit = 200, // 全量约 3900 个对象 / 650MB；这里限制数量以控制测试时长
                ExtractNatives = true,
                EnsureJava = false,
                MaxConcurrency = 8,
            },
            installProgress);
        watchInstall.Stop();

        check("客户端 jar 已安装",
            File.Exists(game.GetVersionJarPath("1.20.1")) &&
            new FileInfo(game.GetVersionJarPath("1.20.1")).Length > 1_000_000);
        check("依赖库已下载", result.LibraryFiles > 20 &&
            Directory.GetFiles(game.LibrariesDirectory, "*.jar", SearchOption.AllDirectories).Length > 20);
        check("资源索引已缓存", File.Exists(game.GetAssetIndexPath("5")));
        check("生成了完整的资源任务列表（> 2000）", result.AssetsTotal > 2000);
        check("实际下载了 200 个资源", result.AssetsDownloaded == 200);
        check("natives 已解压出 dll", result.NativesExtracted > 0);
        check("上报过 Downloading / Extracting / Completed",
            installProgress.Stages.Contains(LaunchStage.Downloading) &&
            installProgress.Stages.Contains(LaunchStage.Extracting) &&
            installProgress.Stages.Contains(LaunchStage.Completed));

        Console.WriteLine($"   库 {result.LibraryFiles} 个 / 资源 {result.AssetsDownloaded}/{result.AssetsTotal} / " +
                          $"natives {result.NativesExtracted} / 耗时 {watchInstall.Elapsed.TotalSeconds:0.0} s");

        Console.WriteLine("== 40. 安装后扫描 ==");

        var installedScanner = new LocalVersionScanner(game);
        var localVersions = installedScanner.Scan();

        check("扫描到 1.20.1", localVersions.Any(v => v.Id == "1.20.1"));
        var installed = localVersions.Single(v => v.Id == "1.20.1");
        check("识别为正式版且非模组",
            installed.Type == "release" && !installed.IsModded && installed.Loader is null);
        check("有客户端 jar 且发布时间正确",
            installed.HasClientJar && installed.ReleaseTime?.Year == 2023);

        Console.WriteLine($"   版本：{installed.Id} / {installed.Type} / {installed.ReleaseTime:yyyy-MM-dd} / jar={installed.HasClientJar}");
        Console.WriteLine($"   游戏目录：{gameRoot}");
    }

    private static void WriteVersion(GameDirectory game, string id, string json, bool withJar)
    {
        var directory = game.GetVersionDirectory(id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{id}.json"), json);

        if (withJar)
        {
            File.WriteAllBytes(Path.Combine(directory, $"{id}.jar"), new byte[] { 1, 2, 3 });
        }
    }

    /// <summary>按 10% 分桶打印的百分比进度。</summary>
    private sealed class PercentProgress : IProgress<double>
    {
        private int _lastBucket = -1;

        public void Report(double value)
        {
            var bucket = (int)(Math.Clamp(value, 0d, 1d) * 10);
            if (bucket == _lastBucket)
            {
                return;
            }

            _lastBucket = bucket;
            Console.WriteLine($"   Java 运行时下载 {bucket * 10,3}%");
        }
    }

    /// <summary>打印进度（按阶段与 10% 分桶节流），并记录全部阶段。</summary>
    private sealed class PrintProgress : IProgress<LaunchProgress>
    {
        private readonly List<LaunchProgress> _items = new();
        private readonly object _gate = new();
        private LaunchStage? _lastStage;
        private int _lastBucket = -1;

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

        public void Report(LaunchProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);

                var bucket = (int)(value.Progress * 10);
                if (_lastStage != value.Stage || bucket != _lastBucket)
                {
                    _lastStage = value.Stage;
                    _lastBucket = bucket;
                    Console.WriteLine($"   [{value.Stage,-11}] {value.Percent,3}%  {value.Message}");
                }
            }
        }
    }
}
