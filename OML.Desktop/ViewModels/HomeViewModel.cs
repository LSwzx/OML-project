using CommunityToolkit.Mvvm.ComponentModel;
using OML.Abstractions.Auth;
using OML.Abstractions.Launching;
using OML.Core.Auth;
using OML.Core.Launching;
using OML.Core.Versioning;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 主页：版本列表 + 启动 + 日志。
/// <para>
/// 所有可绑定属性只在 UI 线程修改；游戏日志走缓冲区 + 150ms 批量刷新。
/// </para>
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private const int MaxLogLength = 200_000;

    private readonly LauncherContext _context;
    private readonly LaunchSession _session = new();
    private readonly System.Text.StringBuilder _pendingLogs = new();
    private readonly object _logGate = new();
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;

    public HomeViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _session.LogReceived += OnGameLog;

        _logTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _logTimer.Tick += (_, _) => FlushLogs();
        _logTimer.Start();
    }

    public System.Collections.ObjectModel.ObservableCollection<LocalVersionInfo> Versions { get; } = new();

    [ObservableProperty]
    private LocalVersionInfo? selectedVersion;

    [ObservableProperty]
    private string logs = string.Empty;

    [ObservableProperty]
    private string statusText = "就绪";

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>当前选中的平台（Java / 基岩）。</summary>
    [ObservableProperty]
    private CurrentPlatform currentPlatform = CurrentPlatform.Java;

    [ObservableProperty]
    private bool isVersionPickerOpen;

    [ObservableProperty]
    private string bedrockVersionText = "未安装基岩版，点此下载";

    [ObservableProperty]
    private string accountName = "未登录";

    [ObservableProperty]
    private string accountTypeText = "点击登录";

    [ObservableProperty]
    private string accountInitial = "?";

    /// <summary>2D 皮肤头像；无皮肤/离线账号时为内置 Steve。</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? skinHead;

    /// <summary>3D 预览用的皮肤 PNG 本地路径；为空时控件用内置 Steve。</summary>
    [ObservableProperty]
    private string? skinImagePath;

    /// <summary>3D 预览的皮肤位图（第三方 SkinView 控件直接使用）。</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? skinBitmap;

    public bool IsJavaSelected => CurrentPlatform == CurrentPlatform.Java;

    public bool IsBedrockSelected => CurrentPlatform == CurrentPlatform.Bedrock;

    public string LaunchButtonText => IsJavaSelected ? "启动 Java 游戏" : "启动基岩版";

    public string SelectedVersionTitle => SelectedVersion?.DisplayName ?? "未选择版本";

    public string SelectedVersionSubtitle => SelectedVersion is null
        ? "还没有安装任何版本"
        : $"{(SelectedVersion.IsModded ? SelectedVersion.Loader ?? "Modded" : "原版")} · {SelectedVersion.Type}";

    public bool CanLaunch => !IsBusy && (CurrentPlatform == CurrentPlatform.Bedrock || SelectedVersion is not null);

    partial void OnIsBusyChanged(bool value) => LaunchCommand.NotifyCanExecuteChanged();

    partial void OnCurrentPlatformChanged(CurrentPlatform value)
    {
        OnPropertyChanged(nameof(IsJavaSelected));
        OnPropertyChanged(nameof(IsBedrockSelected));
        OnPropertyChanged(nameof(LaunchButtonText));
        LaunchCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVersionChanged(LocalVersionInfo? value)
    {
        OnPropertyChanged(nameof(SelectedVersionTitle));
        OnPropertyChanged(nameof(SelectedVersionSubtitle));
        LaunchCommand.NotifyCanExecuteChanged();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SelectJava() => CurrentPlatform = CurrentPlatform.Java;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SelectBedrock() => CurrentPlatform = CurrentPlatform.Bedrock;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleVersionPicker() => IsVersionPickerOpen = !IsVersionPickerOpen;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseVersionPicker() => IsVersionPickerOpen = false;

    /// <summary>账号页登录/切换后刷新右侧账号卡片。</summary>
    public void RefreshAccount()
    {
        var profile = _context.Config.UserProfile;

        AccountName = string.IsNullOrWhiteSpace(profile?.Username) ? "未登录" : profile!.Username;
        AccountInitial = string.IsNullOrWhiteSpace(profile?.Username)
            ? "?"
            : profile!.Username[..1].ToUpperInvariant();

        AccountTypeText = profile?.Type switch
        {
            AuthTypes.Microsoft => "微软正版",
            AuthTypes.LittleSkin => "外置登录",
            AuthTypes.Offline => "离线账号",
            _ => "点击登录",
        };

        _ = RefreshSkinAsync();
    }

    /// <summary>下载（或读缓存）皮肤，并同时准备 3D 预览用的位图；失败回退 Steve。</summary>
    public async Task RefreshSkinAsync()
    {
        var profile = _context.Config.UserProfile;

        if (profile is null || string.IsNullOrWhiteSpace(profile.SkinUrl))
        {
            SkinHead = SkinImageLoader.SteveHead;
            SkinImagePath = null;
            SkinBitmap = LoadSkinBitmap(null);
            return;
        }

        try
        {
            var cacheRoot = System.IO.Path.GetDirectoryName(_context.ConfigPath) ?? _context.ConfigPath;
            using var downloader = new OML.Core.Skins.SkinDownloader(cacheRoot);
            var path = await downloader.GetSkinPathAsync(profile.SkinUrl, profile.Uuid);

            DispatcherHelper.Run(() =>
            {
                SkinImagePath = path;
                SkinHead = SkinImageLoader.LoadHead(path);
                SkinBitmap = LoadSkinBitmap(path);
            });
        }
        catch
        {
            DispatcherHelper.Run(() =>
            {
                SkinImagePath = null;
                SkinHead = SkinImageLoader.SteveHead;
                SkinBitmap = LoadSkinBitmap(null);
            });
        }
    }

    /// <summary>把皮肤路径读成 BitmapSource；空/失败时使用内置 Steve 皮肤资源。</summary>
    private static System.Windows.Media.Imaging.BitmapSource LoadSkinBitmap(string? path)
    {
        var uri = !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)
            ? new Uri(path, UriKind.Absolute)
            : new Uri("pack://application:,,,/Assets/steve_skin.png", UriKind.Absolute);

        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            var fallback = new System.Windows.Media.Imaging.BitmapImage();
            fallback.BeginInit();
            fallback.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            fallback.UriSource = new Uri("pack://application:,,,/Assets/steve_skin.png", UriKind.Absolute);
            fallback.EndInit();
            fallback.Freeze();
            return fallback;
        }
    }

    /// <summary>扫描本地版本（由 Shell 在初始化后调用）。</summary>
    public void ReloadVersions(string? preferredId = null)    {
        try
        {
            var game = _context.CreateGameDirectory();
            var found = new LocalVersionScanner(game).Scan();

            Versions.Clear();
            foreach (var version in found)
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault(v =>
                                  string.Equals(v.Id, preferredId ?? _context.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase))
                              ?? Versions.FirstOrDefault();

            StatusText = Versions.Count == 0
                ? "未找到本地版本，请先把版本安装到游戏目录"
                : $"找到 {Versions.Count} 个本地版本";

            RefreshAccount();
        }
        catch (Exception ex)
        {
            StatusText = "扫描版本失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RefreshVersions() => ReloadVersions();

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (CurrentPlatform == CurrentPlatform.Bedrock)
        {
            System.Windows.MessageBox.Show(
                "基岩版需要微软正版账号登录，且当前版本尚未内置基岩版运行时。\n\n请切换到 Java 版启动。",
                "暂不支持启动基岩版",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (SelectedVersion is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 关键：用共享配置的当前值，账号页刚登录的档案必须生效。
            var config = _context.Config;
            config.SelectedVersion = SelectedVersion.Id;
            config.GameDirectory = _context.CreateGameDirectory().Root;
            config.UserProfile ??= OfflineAuthService.CreateProfile("Player");
            await _context.SaveAsync();

            StatusText = "准备启动…";
            ProgressValue = 0;

            var progress = new Progress<LaunchProgress>(p =>
            {
                StatusText = p.Message;
                ProgressValue = p.Percent;
            });

            var info = await _session.LaunchGameAsync(config, progress);
            AppendLog($"[OML] 进程已启动，PID {info.Process.Id}");
            StatusText = "游戏运行中…";

            var exit = await info.WaitForExitAsync();
            StatusText = exit.Crashed
                ? $"游戏崩溃，崩溃报告：{exit.CrashReportPath}"
                : $"游戏已退出（退出码 {exit.ExitCode}）";
            AppendLog($"[OML] {StatusText}");
        }
        catch (Exception ex)
        {
            StatusText = "启动失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    private void OnGameLog(string line)
    {
        lock (_logGate)
        {
            _pendingLogs.AppendLine(line);
        }
    }

    private void AppendLog(string line)
    {
        lock (_logGate)
        {
            _pendingLogs.AppendLine(line);
        }
    }

    private void FlushLogs()
    {
        string chunk;
        lock (_logGate)
        {
            if (_pendingLogs.Length == 0)
            {
                return;
            }

            chunk = _pendingLogs.ToString();
            _pendingLogs.Clear();
        }

        var text = Logs + chunk;
        if (text.Length > MaxLogLength)
        {
            text = "[OML] 日志过长，已截断较早内容…" + Environment.NewLine + text[^150_000..];
        }

        Logs = text;
    }
}
