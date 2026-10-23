using OML.Abstractions.Models;

namespace OML.Core.Versioning;

/// <summary>
/// <c>.minecraft</c> 目录布局。
/// <para>
/// 只负责"路径计算"，不负责创建。所有写入点自己 <c>Directory.CreateDirectory</c>，
/// 这样单元测试可以用一个不存在的临时目录实例化它而不产生副作用。
/// </para>
/// </summary>
public sealed class GameDirectory
{
    private const string ManifestFileName = "version_manifest_v2.json";

    /// <param name="root">游戏根目录（通常为 <c>%APPDATA%\.minecraft</c>）。</param>
    /// <param name="versionIsolation">
    /// 是否启用版本隔离：为 <c>true</c> 时存档、模组等落在 <c>versions/&lt;id&gt;/</c> 下，
    /// 多个版本互不干扰（PCL2 的"版本隔离"开关）。
    /// </param>
    public GameDirectory(string root, bool versionIsolation = false)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("游戏根目录不能为空。", nameof(root));
        }

        Root = Path.GetFullPath(root);
        VersionIsolation = versionIsolation;
    }

    /// <summary>游戏根目录的绝对路径。</summary>
    public string Root { get; }

    /// <summary>是否启用版本隔离。</summary>
    public bool VersionIsolation { get; }

    /// <summary><c>&lt;root&gt;/versions</c>。</summary>
    public string VersionsDirectory => Path.Combine(Root, "versions");

    /// <summary><c>&lt;root&gt;/libraries</c>。</summary>
    public string LibrariesDirectory => Path.Combine(Root, "libraries");

    /// <summary><c>&lt;root&gt;/assets</c>。</summary>
    public string AssetsDirectory => Path.Combine(Root, "assets");

    /// <summary><c>&lt;root&gt;/assets/indexes</c>。</summary>
    public string AssetsIndexesDirectory => Path.Combine(AssetsDirectory, "indexes");

    /// <summary><c>&lt;root&gt;/assets/objects</c>。</summary>
    public string AssetsObjectsDirectory => Path.Combine(AssetsDirectory, "objects");

    /// <summary>版本清单缓存路径：<c>&lt;root&gt;/versions/version_manifest_v2.json</c>。</summary>
    public string ManifestCachePath => Path.Combine(VersionsDirectory, ManifestFileName);

    /// <summary><c>versions/&lt;id&gt;</c>。</summary>
    public string GetVersionDirectory(string versionId) =>
        Path.Combine(VersionsDirectory, NormalizeVersionId(versionId));

    /// <summary><c>versions/&lt;id&gt;/&lt;id&gt;.json</c>。</summary>
    public string GetVersionJsonPath(string versionId)
    {
        var id = NormalizeVersionId(versionId);
        return Path.Combine(VersionsDirectory, id, $"{id}.json");
    }

    /// <summary><c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>（原版客户端 jar 的标准位置）。</summary>
    public string GetVersionJarPath(string versionId)
    {
        var id = NormalizeVersionId(versionId);
        return Path.Combine(VersionsDirectory, id, $"{id}.jar");
    }

    /// <summary><c>versions/&lt;id&gt;/natives</c>（解压后的本地库）。</summary>
    public string GetVersionNativesDirectory(string versionId) =>
        Path.Combine(GetVersionDirectory(versionId), "natives");

    /// <summary><c>assets/indexes/&lt;id&gt;.json</c>。</summary>
    public string GetAssetIndexPath(string assetIndexId) =>
        Path.Combine(AssetsIndexesDirectory, $"{NormalizeVersionId(assetIndexId)}.json");

    /// <summary>
    /// 按 Maven 坐标计算库文件路径，例：
    /// <c>org.lwjgl:lwjgl:3.3.3</c> → <c>libraries/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar</c>。
    /// </summary>
    public string GetLibraryPath(string mavenName)
    {
        if (!MavenCoordinate.TryParse(mavenName, out var coordinate))
        {
            throw new ArgumentException($"非法的 Maven 坐标：{mavenName}", nameof(mavenName));
        }

        return GetLibraryPath(coordinate);
    }

    /// <summary>按已解析的坐标计算库文件路径。</summary>
    public string GetLibraryPath(MavenCoordinate coordinate) =>
        Path.Combine(LibrariesDirectory, coordinate.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 玩家数据根目录：开启版本隔离时是 <c>versions/&lt;id&gt;</c>，否则是游戏根目录。
    /// 存档、模组、配置都以此为准。
    /// </summary>
    public string GetGameRoot(string versionId) =>
        VersionIsolation ? GetVersionDirectory(versionId) : Root;

    /// <summary>存档目录（<c>saves</c>）。</summary>
    public string GetSavesDirectory(string versionId) => Path.Combine(GetGameRoot(versionId), "saves");

    /// <summary>模组目录（<c>mods</c>）。</summary>
    public string GetModsDirectory(string versionId) => Path.Combine(GetGameRoot(versionId), "mods");

    /// <summary>日志目录（<c>logs</c>）。</summary>
    public string GetLogsDirectory(string versionId) => Path.Combine(GetGameRoot(versionId), "logs");

    /// <summary>
    /// 定位默认游戏目录：Windows 为 <c>%APPDATA%\.minecraft</c>，其余平台为 <c>~/.minecraft</c>。
    /// </summary>
    public static GameDirectory LocateDefault(bool versionIsolation = false)
    {
        var home = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new GameDirectory(Path.Combine(home, ".minecraft"), versionIsolation);
    }

    /// <summary>
    /// 校验并规范化版本 ID。
    /// <para>
    /// 必须做：版本 ID 会被直接拼进文件路径，若含 <c>../</c> 就能把文件写到游戏目录之外，
    /// 这是启动器被恶意版本 JSON 利用的经典路径穿越。
    /// </para>
    /// </summary>
    private static string NormalizeVersionId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("版本 ID 不能为空。", nameof(versionId));
        }

        if (versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionId.Contains("..", StringComparison.Ordinal) ||
            versionId.Contains('/') ||
            versionId.Contains('\\'))
        {
            throw new ArgumentException($"非法的版本 ID：{versionId}", nameof(versionId));
        }

        return versionId;
    }
}
