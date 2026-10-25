using OML.Abstractions.Configuration;
using OML.Core.Configuration;
using OML.Core.Versioning;

namespace OML.Desktop.Infrastructure;

/// <summary>
/// 页面之间共享的启动器状态。
/// <para>
/// 配置只有一份：设置页改了内存，主页启动时就必须看到；账号页登录后，主页启动必须用新账号。
/// 与其让每个 ViewModel 各存一份副本再同步，不如共用一个上下文对象 + 变更事件。
/// </para>
/// </summary>
public sealed class LauncherContext
{
    private readonly ConfigManager _configManager;

    public LauncherContext(ConfigManager configManager)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
    }

    /// <summary>当前配置（唯一实例，页面直接读写）。</summary>
    public LauncherConfig Config { get; private set; } = LauncherConfig.CreateDefault();

    /// <summary>配置路径（界面展示）。</summary>
    public string ConfigPath => _configManager.ConfigPath;

    /// <summary>从磁盘加载配置。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Config = await _configManager.LoadAsync(cancellationToken);
    }

    /// <summary>保存配置（令牌会被 DPAPI 加密）。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _configManager.SaveAsync(Config, cancellationToken);
    }

    /// <summary>按当前配置创建游戏目录对象。</summary>
    public GameDirectory CreateGameDirectory() =>
        GameDirectory.FromConfig(Config.GameDirectory, Config.VersionIsolation);
}
