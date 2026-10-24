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

    public bool CanLaunch => !IsBusy && SelectedVersion is not null;

    partial void OnIsBusyChanged(bool value) => LaunchCommand.NotifyCanExecuteChanged();

    partial void OnSelectedVersionChanged(LocalVersionInfo? value) => LaunchCommand.NotifyCanExecuteChanged();

    /// <summary>扫描本地版本（由 Shell 在初始化后调用）。</summary>
    public void ReloadVersions(string? preferredId = null)
    {
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
        }
        catch (Exception ex)
        {
            StatusText = "扫描版本失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
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
