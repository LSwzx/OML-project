using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OML.Core.Configuration;
using OML.Desktop.ViewModels;
using Wpf.Ui.Appearance;

namespace OML.Desktop;

/// <summary>
/// 应用入口。手工组装依赖（配置管理器 → ViewModel → 窗口），
/// 不引入 DI 容器——目前只有三个对象，容器反而增加理解成本。
/// </summary>
public partial class App : Application
{
    /// <summary>品牌色。不跟随系统强调色，避免用户把系统主题换成绿色后整个启动器变荧光绿。</summary>
    private static readonly Color BrandAccent = Color.FromRgb(0x4C, 0x8B, 0xF5);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Dark);

        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OML");

        var configManager = new ConfigManager(launcherRoot);
        var gameDirectoryOverride = ReadArgument(e.Args, "--game-dir");

        var viewModel = new MainViewModel(configManager, gameDirectoryOverride);
        var window = new MainWindow { DataContext = viewModel };

        window.Show();

        // 调试：自动触发一次启动，用于验证"点击 → 进度 → 日志"整条界面链路。
        if (e.Args.Contains("--auto-launch"))
        {
            var launchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            launchTimer.Tick += (_, _) =>
            {
                launchTimer.Stop();
                viewModel.LaunchCommand.Execute(null);
            };
            launchTimer.Start();
        }

        // 冒烟模式：用于无交互环境验证"窗口能起来、绑定不炸"，2.5 秒后自动退出。
        if (e.Args.Contains("--smoke"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
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
