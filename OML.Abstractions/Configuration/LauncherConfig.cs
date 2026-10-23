using System.Text.Json.Serialization;
using OML.Abstractions.Auth;

namespace OML.Abstractions.Configuration;

/// <summary>
/// 启动器的持久化配置。落在<b>启动器根目录</b>的 <c>launcher_config.json</c>，
/// 不放进 <c>.minecraft</c>——游戏目录可能被玩家清理/迁移，配置不该跟着丢。
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>配置文件默认文件名。</summary>
    public const string FileName = "launcher_config.json";

    /// <summary>当前选择的版本 ID，例如 <c>1.21.1</c>。</summary>
    [JsonPropertyName("selectedVersion")]
    public string SelectedVersion { get; set; } = string.Empty;

    /// <summary>Java 路径；为空时由启动器自动探测。</summary>
    [JsonPropertyName("javaPath")]
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>最大堆内存（MB）。</summary>
    [JsonPropertyName("maxMemoryMB")]
    public int MaxMemoryMB { get; set; } = 4096;

    /// <summary>游戏目录；默认 <c>.minecraft</c>。</summary>
    [JsonPropertyName("gameDirectory")]
    public string GameDirectory { get; set; } = ".minecraft";

    /// <summary>当前选择的账号。</summary>
    [JsonPropertyName("userProfile")]
    public UserProfile? UserProfile { get; set; }

    /// <summary>是否启用版本隔离（存档/模组落在 <c>versions/&lt;id&gt;</c>）。</summary>
    [JsonPropertyName("versionIsolation")]
    public bool VersionIsolation { get; set; }

    /// <summary>
    /// 启动前是否补全全部资源文件。
    /// <para>
    /// 1.21.1 的资源约 800MB、3900+ 个文件，首次启动全量补全会非常久；
    /// UI 可以把它做成"启动前自动补全"的开关，测试里则关掉以免每次跑几分钟。
    /// </para>
    /// </summary>
    [JsonPropertyName("completeAssets")]
    public bool CompleteAssets { get; set; } = true;

    /// <summary>批量下载并发上限。</summary>
    [JsonPropertyName("maxDownloadConcurrency")]
    public int MaxDownloadConcurrency { get; set; } = 8;

    /// <summary>创建一份默认配置。</summary>
    public static LauncherConfig CreateDefault() => new();
}
