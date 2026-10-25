using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Core.Versioning;
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

        // 安装成功后主页的版本列表要立刻看到新版本（任务在后台线程完成，必须切回 UI 线程）。
        Install.InstallCompleted += versionId => DispatcherHelper.Run(() => Home.ReloadVersions(versionId));
        Mods.ContentInstalled += versionId => DispatcherHelper.Run(() => Home.ReloadVersions(versionId));
    }

    public HomeViewModel Home { get; }

    public InstallViewModel Install { get; }

    public ModsViewModel Mods { get; }

    public AccountsViewModel Accounts { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>全局任务队列（底部状态条绑定它）。</summary>
    public BackgroundTaskManager Tasks => BackgroundTaskManager.Instance;

    /// <summary>页面内请求导航（主页的"版本设置/模组管理"等按钮用）。</summary>
    public event Action<string>? NavigationRequested;

    public void RequestNavigation(string tag) => NavigationRequested?.Invoke(tag);

    /// <summary>自动更新（Velopack）。</summary>
    public UpdateService Updates { get; } = new();

    [ObservableProperty]
    private bool updateAvailable;

    [ObservableProperty]
    private string updateText = string.Empty;

    private Velopack.UpdateInfo? _pendingUpdate;

    /// <summary>启动时静默检查更新；未开启自动检查或失败时都不影响使用。</summary>
    public async Task CheckForUpdatesAsync(bool force = false)
    {
        if (!force && !_context.Config.AutoCheckUpdates)
        {
            return;
        }

        var update = await Updates.CheckAsync();
        if (update is null)
        {
            return;
        }

        _pendingUpdate = update;
        UpdateText = $"发现新版本 {update.TargetFullRelease.Version}（当前 {Updates.CurrentVersion}）";
        UpdateAvailable = true;
    }

    /// <summary>后台下载并重启到新版本。</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        UpdateText = "正在下载更新…";
        try
        {
            await Updates.DownloadAndRestartAsync(
                _pendingUpdate,
                percent => DispatcherHelper.Run(() => UpdateText = $"正在下载更新 {percent}%"),
                status => DispatcherHelper.Run(() => UpdateText = status));
        }
        catch (Exception ex)
        {
            var friendly = ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                ? "更新失败：另一个 OML 实例或更新进程正在占用更新锁。请关闭所有 OML 窗口后重试。"
                : $"更新失败：{ex.Message}";

            UpdateText = friendly;
            System.Windows.MessageBox.Show(
                friendly,
                "自动更新失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>当前导航标签（home / install / mods / accounts / settings）。</summary>
    [ObservableProperty]
    private string currentTag = "home";

    public string ConfigPath => _context.ConfigPath;

    /// <summary>程序集版本（发布时由 csproj 的 Version 决定）。</summary>
    public string AppVersion =>
        typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 加载配置并让各页面同步。任何异常向上抛，由 App 统一提示。
    /// </summary>
    public async Task InitializeAsync()
    {
        await _context.InitializeAsync();

        // 清理上次中断的安装（取消/崩溃留在 versions/<id>/.installing）。
        var cleaned = InstallState.CleanupAll(_context.CreateGameDirectory());

        Settings.LoadFromConfig();
        Accounts.RefreshFromConfig();
        Home.ReloadVersions();

        if (cleaned > 0)
        {
            Home.StatusText = $"已清理 {cleaned} 个未完成的安装";
        }

        Navigate("home");
    }

    /// <summary>切换页面（导航由 NavigationView 的原生 Page 机制完成，这里只记录标签）。</summary>
    public void Navigate(string tag)
    {
        CurrentTag = tag;

        if (string.Equals(tag, "home", StringComparison.OrdinalIgnoreCase))
        {
            Home.RefreshAccount();
        }
    }
}
