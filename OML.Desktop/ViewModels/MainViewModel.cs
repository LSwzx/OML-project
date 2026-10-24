using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Auth;
using OML.Abstractions.Configuration;
using OML.Abstractions.Launching;
using OML.Core.Auth;
using OML.Core.Configuration;
using OML.Core.Launching;
using OML.Core.Versioning;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
/// <para>
/// 线程约定：所有可绑定属性只在 UI 线程修改。
/// 游戏日志来自后台线程，先进入缓冲队列，由 <see cref="DispatcherTimer"/> 每 150ms 批量刷到
/// <see cref="Logs"/>——逐行触发 PropertyChanged 会让 TextBox 在高频日志下卡死。
/// </para>
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLength = 200_000;

    private readonly ConfigManager _configManager;
    private readonly string? _gameDirectoryOverride;
    private readonly LaunchSession _session = new();
    private readonly StringBuilder _pendingLogs = new();
    private readonly object _logGate = new();
    private readonly DispatcherTimer _logTimer;

    private GameDirectory? _gameDirectory;

    public MainViewModel(ConfigManager configManager, string? gameDirectoryOverride = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _gameDirectoryOverride = gameDirectoryOverride;

        _session.LogReceived += OnGameLog;

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _logTimer.Tick += (_, _) => FlushLogs();
        _logTimer.Start();

        _ = InitializeAsync();
    }

    /// <summary>本地版本列表。</summary>
    public ObservableCollection<LocalVersionInfo> Versions { get; } = new();

    /// <summary>配置文件路径（底部状态栏展示）。</summary>
    public string ConfigPath => _configManager.ConfigPath;

    [ObservableProperty]
    private LocalVersionInfo? selectedVersion;

    [ObservableProperty]
    private string logs = string.Empty;

    [ObservableProperty]
    private string statusText = "正在扫描本地版本…";

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>按钮可用性：没选版本或正在启动时禁用。</summary>
    public bool CanLaunch => !IsBusy && SelectedVersion is not null;

    partial void OnIsBusyChanged(bool value) => LaunchCommand.NotifyCanExecuteChanged();

    partial void OnSelectedVersionChanged(LocalVersionInfo? value) => LaunchCommand.NotifyCanExecuteChanged();

    private async Task InitializeAsync()
    {
        try
        {
            var config = await _configManager.LoadAsync();

            var path = _gameDirectoryOverride;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = string.IsNullOrWhiteSpace(config.GameDirectory) ? null : config.GameDirectory;
            }

            _gameDirectory = string.IsNullOrWhiteSpace(path)
                ? GameDirectory.LocateDefault()
                : new GameDirectory(path!, config.VersionIsolation);

            ScanVersions(config.SelectedVersion);

            AppendLog($"[OML] 游戏目录：{_gameDirectory.Root}");
            AppendLog($"[OML] 账户：{config.UserProfile?.Username ?? "未配置（启动时将创建离线账号）"}");
            StatusText = Versions.Count == 0
                ? "未找到本地版本，请先把版本安装到游戏目录"
                : $"找到 {Versions.Count} 个本地版本";
        }
        catch (Exception ex)
        {
            StatusText = "初始化失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ScanVersions(string? preferredId)
    {
        if (_gameDirectory is null)
        {
            return;
        }

        var scanner = new LocalVersionScanner(_gameDirectory);
        var found = scanner.Scan();

        Versions.Clear();
        foreach (var version in found)
        {
            Versions.Add(version);
        }

        SelectedVersion = Versions.FirstOrDefault(v =>
                              string.Equals(v.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                          ?? Versions.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedVersion is null || _gameDirectory is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var config = await _configManager.LoadAsync();
            config.SelectedVersion = SelectedVersion.Id;
            config.GameDirectory = _gameDirectory.Root;
            config.UserProfile ??= OfflineAuthService.CreateProfile("Player");
            await _configManager.SaveAsync(config);

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

    /// <summary>把缓冲区刷到绑定属性（只在 UI 线程调用）。</summary>
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
