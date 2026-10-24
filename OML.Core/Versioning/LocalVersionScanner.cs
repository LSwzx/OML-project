using System.Text.Json;
using OML.Abstractions.Models;
using OML.Core.Serialization;

namespace OML.Core.Versioning;

/// <summary>本地已安装的版本。</summary>
/// <param name="Id">版本 ID（目录名）。</param>
/// <param name="Type">版本类型：release / snapshot / old_beta ...</param>
/// <param name="ReleaseTime">正式发布时间。</param>
/// <param name="IsModded">是否为模组版本（有 inheritsFrom 或 Forge/Fabric 库）。</param>
/// <param name="Loader">模组加载器名称：Forge / NeoForge / Fabric / Quilt；原版为 <c>null</c>。</param>
/// <param name="HasClientJar">是否存在客户端 jar。</param>
/// <param name="JsonPath">版本 JSON 的绝对路径。</param>
public sealed record LocalVersionInfo(
    string Id,
    string Type,
    DateTimeOffset? ReleaseTime,
    bool IsModded,
    string? Loader,
    bool HasClientJar,
    string JsonPath,
    string? InheritsFrom = null,
    string? InstanceName = null,
    string? IconUrl = null)
{
    /// <summary>
    /// 给 UI 显示的名字：整合包实例优先用 instance.json 里的名字，
    /// 否则用 <c>id (Loader)</c>。
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(InstanceName)
        ? InstanceName!
        : Loader is null ? Id : $"{Id} ({Loader})";
}

/// <summary>
/// 本地版本扫描：遍历 <c>versions/*/&lt;id&gt;.json</c>。
/// <para>
/// 扫描必须容忍坏文件：用户装 Forge 失败、手改 JSON、磁盘写到一半都会留下残缺文件，
/// 不能因为其中一个解析失败就让整个版本列表打不开。
/// </para>
/// </summary>
public sealed class LocalVersionScanner
{
    private readonly GameDirectory _gameDirectory;

    public LocalVersionScanner(GameDirectory gameDirectory)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
    }

    /// <summary>扫描全部本地版本，按发布时间倒序（同时间按 ID 倒序）。</summary>
    public IReadOnlyList<LocalVersionInfo> Scan()
    {
        if (!Directory.Exists(_gameDirectory.VersionsDirectory))
        {
            return Array.Empty<LocalVersionInfo>();
        }

        var results = new List<LocalVersionInfo>();

        foreach (var directory in Directory.EnumerateDirectories(_gameDirectory.VersionsDirectory))
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var jsonPath = Path.Combine(directory, $"{id}.json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            VersionDetail? detail;
            try
            {
                var json = File.ReadAllText(jsonPath);
                detail = JsonSerializer.Deserialize<VersionDetail>(json, OmlJsonOptions.Default);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            if (detail is null)
            {
                continue;
            }

            var (isModded, loader) = DetectLoader(detail);
            var jarPath = Path.Combine(directory, $"{id}.jar");
            var (instanceName, iconUrl) = ReadInstanceMetadata(Path.Combine(directory, "instance.json"));

            // 本地图标优先：断网时主页也不会裂图。
            var localIcon = Path.Combine(directory, "icon.png");
            if (File.Exists(localIcon))
            {
                iconUrl = new Uri(localIcon).AbsoluteUri;
            }

            results.Add(new LocalVersionInfo(
                string.IsNullOrWhiteSpace(detail.Id) ? id : detail.Id,
                string.IsNullOrWhiteSpace(detail.Type) ? "unknown" : detail.Type!,
                detail.ReleaseTime,
                isModded,
                loader,
                File.Exists(jarPath),
                jsonPath,
                detail.InheritsFrom,
                instanceName,
                iconUrl));
        }

        return results
            .OrderByDescending(v => v.ReleaseTime ?? DateTimeOffset.MinValue)
            .ThenByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>按 ID 查找（大小写不敏感）。</summary>
    public LocalVersionInfo? Find(string versionId) =>
        Scan().FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 判断是不是模组版本。优先看加载器特征库（最可靠），
    /// 其次看 <c>inheritsFrom</c>（Forge/Fabric 一定继承原版）。
    /// </summary>
    /// <summary>
    /// 读取整合包实例元数据（<c>versions/&lt;id&gt;/instance.json</c>）。
    /// 缺失或损坏都返回空，不影响扫描。
    /// </summary>
    private static (string? Name, string? IconUrl) ReadInstanceMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var icon = root.TryGetProperty("icon", out var iconElement) ? iconElement.GetString() : null;

            return (string.IsNullOrWhiteSpace(name) ? null : name, string.IsNullOrWhiteSpace(icon) ? null : icon);
        }
        catch
        {
            return (null, null);
        }
    }

    public static (bool IsModded, string? Loader) DetectLoader(VersionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        string? loader = null;

        foreach (var library in detail.Libraries)
        {
            var name = library.Name ?? string.Empty;

            if (name.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))
            {
                loader ??= "Forge";
            }
            else if (name.StartsWith("net.neoforged:", StringComparison.OrdinalIgnoreCase))
            {
                loader ??= "NeoForge";
            }
            else if (name.StartsWith("net.fabricmc:", StringComparison.OrdinalIgnoreCase))
            {
                loader ??= "Fabric";
            }
            else if (name.StartsWith("org.quiltmc:", StringComparison.OrdinalIgnoreCase))
            {
                loader ??= "Quilt";
            }
        }

        if (loader is not null)
        {
            return (true, loader);
        }

        if (!string.IsNullOrWhiteSpace(detail.InheritsFrom))
        {
            return (true, "Modded");
        }

        return (false, null);
    }
}
