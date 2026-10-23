using System.Text.Json;
using System.Text.Json.Serialization;
using OML.Abstractions.Configuration;

namespace OML.Core.Configuration;

/// <summary>
/// 配置读写：<c>&lt;启动器根目录&gt;/launcher_config.json</c>。
/// <para>写入采用"临时文件 + 替换"，避免断电/崩溃留下半截 JSON 导致配置永远读不出来。</para>
/// </summary>
public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="launcherRoot">
    /// 启动器根目录。默认使用程序所在目录（<see cref="AppContext.BaseDirectory"/>），
    /// 而不是游戏目录。
    /// </param>
    public ConfigManager(string? launcherRoot = null)
    {
        LauncherRoot = Path.GetFullPath(launcherRoot ?? AppContext.BaseDirectory);
        ConfigPath = Path.Combine(LauncherRoot, LauncherConfig.FileName);
    }

    /// <summary>启动器根目录。</summary>
    public string LauncherRoot { get; }

    /// <summary>配置文件绝对路径。</summary>
    public string ConfigPath { get; }

    /// <summary>配置文件是否已存在。</summary>
    public bool Exists => File.Exists(ConfigPath);

    /// <summary>
    /// 读取配置。文件不存在时返回默认配置；文件损坏时备份后返回默认配置，
    /// 而不是让启动器因为一个坏文件彻底打不开。
    /// </summary>
    public async Task<LauncherConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPath))
        {
            return LauncherConfig.CreateDefault();
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, SerializerOptions);
            return config ?? LauncherConfig.CreateDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var brokenPath = ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                File.Move(ConfigPath, brokenPath, overwrite: true);
            }
            catch
            {
                // 备份失败也不影响返回默认配置。
            }

            return LauncherConfig.CreateDefault();
        }
    }

    /// <summary>保存配置（原子写入）。</summary>
    public async Task SaveAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        Directory.CreateDirectory(LauncherRoot);

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        var tempPath = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }
}
