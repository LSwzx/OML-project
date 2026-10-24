using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 设置页：直接读写共享配置。
/// <para>
/// 滑块拖动会高频触发变更，所以保存做了 500ms 防抖——
/// 否则拖一次内存条会写几十次磁盘、触发几十次 ConfigChanged。
/// </para>
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherContext _context;
    private readonly DispatcherTimer _saveTimer;

    private bool _suppressSave;

    public SettingsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveAsync();
        };
    }

    [ObservableProperty]
    private string gameDirectory = string.Empty;

    [ObservableProperty]
    private double maxMemoryMB = 4096;

    [ObservableProperty]
    private bool completeAssets = true;

    [ObservableProperty]
    private string javaPath = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    public string ConfigPath => _context.ConfigPath;

    /// <summary>从共享配置同步到界面（初始化时调用）。</summary>
    public void LoadFromConfig()
    {
        _suppressSave = true;
        try
        {
            GameDirectory = _context.Config.GameDirectory;
            MaxMemoryMB = Math.Clamp(_context.Config.MaxMemoryMB, 1024, 8192);
            CompleteAssets = _context.Config.CompleteAssets;
            JavaPath = _context.Config.JavaPath;
        }
        finally
        {
            _suppressSave = false;
        }
    }

    partial void OnGameDirectoryChanged(string value) => QueueSave();

    partial void OnMaxMemoryMBChanged(double value) => QueueSave();

    partial void OnCompleteAssetsChanged(bool value) => QueueSave();

    partial void OnJavaPathChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_suppressSave)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        try
        {
            var config = _context.Config;
            config.GameDirectory = GameDirectory?.Trim() ?? string.Empty;
            config.MaxMemoryMB = (int)Math.Round(MaxMemoryMB);
            config.CompleteAssets = CompleteAssets;
            config.JavaPath = JavaPath?.Trim() ?? string.Empty;

            await _context.SaveAsync();
            StatusText = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏目录（.minecraft）",
            Multiselect = false,
        };

        if (Directory.Exists(GameDirectory))
        {
            dialog.InitialDirectory = GameDirectory;
        }

        if (dialog.ShowDialog() == true)
        {
            GameDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseJava()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java 可执行文件",
            Filter = "Java 可执行文件|java.exe;javaw.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            JavaPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearJava()
    {
        JavaPath = string.Empty;
        StatusText = "已清空 Java 路径，启动时将自动探测";
    }
}
