using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OML.Core.Configuration;
using OML.Desktop.Infrastructure;
using OML.Desktop.ViewModels;
using Wpf.Ui.Appearance;

namespace OML.Desktop;

/// <summary>
/// 应用入口。手工组装依赖（配置上下文 → 外壳 ViewModel → 窗口），
/// 初始化显式执行并捕获异常——构造函数里的 async void 会把错误吞进日志。
/// </summary>
public partial class App : Application
{
    /// <summary>品牌色。不跟随系统强调色，避免用户换绿色主题后整个启动器变荧光绿。</summary>
    private static readonly Color BrandAccent = Color.FromRgb(0x4C, 0x8B, 0xF5);

    /// <summary>全局外壳 ViewModel（导航页从这里取自己的 DataContext）。</summary>
    public static ShellViewModel? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Dark);

        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OML");

        var context = new LauncherContext(new ConfigManager(launcherRoot));
        var shell = new ShellViewModel(context);
        Shell = shell;
        var window = new MainWindow { DataContext = shell };

        var gameDirectoryOverride = ReadArgument(e.Args, "--game-dir");
        var initialPage = ReadArgument(e.Args, "--nav");
        var offlineAccount = ReadArgument(e.Args, "--create-offline");
        var autoInstall = ReadArgument(e.Args, "--install");
        var autoFabric = ReadArgument(e.Args, "--install-fabric");
        var autoForge = ReadArgument(e.Args, "--install-forge");
        var autoSearch = ReadArgument(e.Args, "--search");

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

                if (!string.IsNullOrWhiteSpace(autoSearch))
                {
                    window.NavigateTo("mods");
                    await shell.Mods.SearchAsync(autoSearch);

                    if (e.Args.Contains("--download-first"))
                    {
                        await shell.Mods.DownloadSelectedAsync();
                    }
                }

                if (e.Args.Contains("--auto-launch"))
                {
                    shell.Home.LaunchCommand.Execute(null);
                }
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
