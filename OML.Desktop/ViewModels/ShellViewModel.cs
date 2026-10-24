using CommunityToolkit.Mvvm.ComponentModel;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 外壳 ViewModel：持有全部页面并负责导航。
/// <para>初始化显式调用，不放在构造函数里——异常必须能被 App 捕获并提示用户。</para>
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly LauncherContext _context;

    public ShellViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Home = new HomeViewModel(context);
        Install = new InstallViewModel(context);
        Mods = new ModsViewModel(context);
        Accounts = new AccountsViewModel(context);
        Settings = new SettingsViewModel(context);

        // 安装成功后主页的版本列表要立刻看到新版本。
        Install.InstallCompleted += versionId => Home.ReloadVersions(versionId);
    }

    public HomeViewModel Home { get; }

    public InstallViewModel Install { get; }

    public ModsViewModel Mods { get; }

    public AccountsViewModel Accounts { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>当前导航标签（home / install / mods / accounts / settings）。</summary>
    [ObservableProperty]
    private string currentTag = "home";

    public string ConfigPath => _context.ConfigPath;

    /// <summary>
    /// 加载配置并让各页面同步。任何异常向上抛，由 App 统一提示。
    /// </summary>
    public async Task InitializeAsync()
    {
        await _context.InitializeAsync();

        Settings.LoadFromConfig();
        Accounts.RefreshFromConfig();
        Home.ReloadVersions();

        Navigate("home");
    }

    /// <summary>切换页面（导航由 NavigationView 的原生 Page 机制完成，这里只记录标签）。</summary>
    public void Navigate(string tag)
    {
        CurrentTag = tag;
    }
}
