using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OML.Core.Configuration;
using OML.Desktop.Infrastructure;
using OML.Desktop.ViewModels;
using Velopack;
using Wpf.Ui.Appearance;

namespace OML.Desktop;

/// <summary>
/// 应用入口。手工组装依赖（配置上下文 → 外壳 ViewModel → 窗口），
/// 初始化显式执行并捕获异常——构造函数里的 async void 会把错误吞进日志。
/// </summary>
public partial class App : Application
{
    /// <summary>品牌色。不跟随系统强调色，避免用户换绿色主题后整个启动器变荧光绿。</summary>
    private static readonly Color BrandAccent = Color.FromRgb(0x1B, 0xD9, 0x6A);

    /// <summary>全局外壳 ViewModel（导航页从这里取自己的 DataContext）。</summary>
    public static ShellViewModel? Shell { get; private set; }

    public App()
    {
        // 必须是整个进程里最早执行的逻辑之一：Velopack 要接管安装/卸载/更新参数。
        VelopackApp.Build().Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Dark);

        // 配置放在 Roaming，和安装目录彻底分开：
        // Velopack 默认装在 %LOCALAPPDATA%\OML，卸载时会删掉整个目录，
        // 配置若也在里面就会跟着一起没。
        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OML");

        var legacyConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OML",
            "launcher_config.json");

        var configPath = Path.Combine(launcherRoot, "launcher_config.json");
        if (!File.Exists(configPath) && File.Exists(legacyConfig))
        {
            try
            {
                Directory.CreateDirectory(launcherRoot);
                File.Copy(legacyConfig, configPath);
            }
            catch (IOException)
            {
                // 迁移失败就按新路径重新开始，不阻塞启动。
            }
        }

        var context = new LauncherContext(new ConfigManager(launcherRoot));
        var shell = new ShellViewModel(context);
        Shell = shell;
        var window = new MainWindow { DataContext = shell };

        // 页面内按钮（版本设置/模组管理…）通过 Shell 请求导航。
        shell.NavigationRequested += tag => window.NavigateTo(tag);

        var gameDirectoryOverride = ReadArgument(e.Args, "--game-dir");
        var initialPage = ReadArgument(e.Args, "--nav");
        var offlineAccount = ReadArgument(e.Args, "--create-offline");
        var autoInstall = ReadArgument(e.Args, "--install");
        var autoFabric = ReadArgument(e.Args, "--install-fabric");
        var autoForge = ReadArgument(e.Args, "--install-forge");
        var autoSearch = ReadArgument(e.Args, "--search");
        var autoModpack = ReadArgument(e.Args, "--modpack");

        window.Loaded += async (_, _) =>
        {
            try
            {
                await shell.InitializeAsync();

                if (!string.IsNullOrWhiteSpace(gameDirectoryOverride))
                {
                    // 临时覆盖目录：只影响本次运行，不写回配置。
                    context.Config.GameDirectory = gameDirectoryOverride;
                    shell.Home.ReloadVersions();
                }

                window.NavigateTo(string.IsNullOrWhiteSpace(initialPage) ? "home" : initialPage);

                if (!string.IsNullOrWhiteSpace(offlineAccount))
                {
                    await shell.Accounts.CreateOfflineAccountAsync(offlineAccount);
                }

                if (!string.IsNullOrWhiteSpace(autoInstall))
                {
                    window.NavigateTo("install");
                    shell.Install.AutoConfirmAssets = true;
                    await shell.Install.InstallVersionAsync(autoInstall);
                }

                if (!string.IsNullOrWhiteSpace(autoFabric))
                {
                    window.NavigateTo("install");
                    await shell.Install.InstallLoaderForAsync(autoFabric, "Fabric");
                }

                if (!string.IsNullOrWhiteSpace(autoForge))
                {
                    window.NavigateTo("install");
                    await shell.Install.InstallLoaderForAsync(autoForge, "Forge");
                }

                if (!string.IsNullOrWhiteSpace(autoModpack))
                {
                    window.NavigateTo("mods");
                    await shell.Mods.InstallModpackBySlugAsync(autoModpack);
                }

                if (!string.IsNullOrWhiteSpace(autoSearch))
                {
                    window.NavigateTo("mods");

                    var category = ReadArgument(e.Args, "--category");
                    shell.Mods.SelectedCategoryIndex = category?.ToLowerInvariant() switch
                    {
                        "shader" or "光影" => 1,
                        "modpack" or "整合包" => 2,
                        _ => 0,
                    };

                    await shell.Mods.SearchAsync(autoSearch);

                    if (e.Args.Contains("--download-first"))
                    {
                        // 自动化场景没有交互，直接按"一并下载依赖"处理。
                        shell.Mods.AutoConfirmDependencies = e.Args.Contains("--download-with-deps");
                        await shell.Mods.DownloadSelectedAsync();
                    }
                }

                if (e.Args.Contains("--auto-launch"))
                {
                    shell.Home.LaunchCommand.Execute(null);
                }

                // 静默检查更新：不阻塞启动，失败也不提示。
                _ = shell.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"初始化失败：{ex.Message}\n\n配置文件：{context.ConfigPath}",
                    "OML",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        };

        window.Show();

        // 开发/自动化：把窗口内容离屏渲染成 PNG（不依赖桌面会话是否可见）。
        var screenshotPath = ReadArgument(e.Args, "--screenshot");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (window.Content is System.Windows.FrameworkElement root)
                    {
                        root.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            Math.Max(1, (int)root.ActualWidth),
                            Math.Max(1, (int)root.ActualHeight),
                            96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(root);

                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var stream = File.Create(screenshotPath);
                        encoder.Save(stream);
                    }
                }
                catch
                {
                    // 截图失败不影响启动。
                }

                if (e.Args.Contains("--screenshot-exit"))
                {
                    Shutdown(0);
                }
            };
            timer.Start();
        }

        if (e.Args.Contains("--smoke"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Shutdown(0);
            };
            timer.Start();
        }
    }

    /// <summary>读取 <c>--name value</c> 形式的参数。</summary>
    private static string? ReadArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
