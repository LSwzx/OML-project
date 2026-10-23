using System.Text.Json;
using System.Text.Json.Serialization;

namespace OML.Abstractions.Models;

/// <summary>
/// 单个版本的完整描述，对应 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>。
/// 字段覆盖 1.13+ 新格式，同时保留 <c>minecraftArguments</c> 以兼容 1.12- 旧版本。
/// </summary>
public sealed class VersionDetail
{
    /// <summary>版本号。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>版本类型：<c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>主类，例如 <c>net.minecraft.client.main.Main</c>。</summary>
    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    /// <summary>资源索引版本号（资源对象目录名），例如 <c>17</c> 或 <c>legacy</c>。</summary>
    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    /// <summary>资源索引文件元数据。</summary>
    [JsonPropertyName("assetIndex")]
    public AssetIndex? AssetIndex { get; set; }

    /// <summary>依赖库列表。</summary>
    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = new();

    /// <summary>1.13+ 的参数分组。</summary>
    [JsonPropertyName("arguments")]
    public Arguments? Arguments { get; set; }

    /// <summary>1.12 及更早版本的扁平参数字符串，按空格切分。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>客户端 / 服务端核心 jar 元数据。</summary>
    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    /// <summary>Java 运行时要求。</summary>
    [JsonPropertyName("javaVersion")]
    public JavaVersion? JavaVersion { get; set; }

    /// <summary>日志配置（log4j 等）。</summary>
    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; set; }

    /// <summary>父版本号（Forge / Fabric 等派生版本使用）。</summary>
    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    /// <summary>发布者声明的最低启动器版本。</summary>
    [JsonPropertyName("minimumLauncherVersion")]
    public int? MinimumLauncherVersion { get; set; }

    /// <summary>合规等级。</summary>
    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }

    /// <summary>入库时间（UTC）。</summary>
    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    /// <summary>正式发布时间（UTC）。</summary>
    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }
}

/// <summary>
/// 启动参数。
/// 每个元素既可能是纯字符串，也可能是带 <c>rules</c> 的条件对象，因此保留为 <see cref="JsonElement"/>
/// 由上层（参数构建器）按需解释——这是 HMCL 解耦思路的关键：抽象层不猜语义。
/// </summary>
public sealed class Arguments
{
    /// <summary>游戏参数。</summary>
    [JsonPropertyName("game")]
    public List<JsonElement> Game { get; set; } = new();

    /// <summary>JVM 参数。</summary>
    [JsonPropertyName("jvm")]
    public List<JsonElement> Jvm { get; set; } = new();
}

/// <summary>资源索引元数据。</summary>
public sealed class AssetIndex
{
    /// <summary>资源索引 ID，同时是 <c>assets/indexes/&lt;id&gt;.json</c> 的文件名。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>SHA-1 校验值。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>压缩后大小（字节）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>解压后资源总大小（字节）。</summary>
    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    /// <summary>下载地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>版本核心文件（client.jar / server.jar）。</summary>
public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public VersionFile? Client { get; set; }

    [JsonPropertyName("server")]
    public VersionFile? Server { get; set; }

    /// <summary>部分旧版本会提供客户端映射表。</summary>
    [JsonPropertyName("client_mappings")]
    public VersionFile? ClientMappings { get; set; }

    [JsonPropertyName("server_mappings")]
    public VersionFile? ServerMappings { get; set; }
}

/// <summary>一个可下载文件的元数据。</summary>
public sealed class VersionFile
{
    /// <summary>SHA-1 校验值。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>文件大小（字节）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>下载地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>Java 运行时版本要求。</summary>
public sealed class JavaVersion
{
    /// <summary>组件名，例如 <c>java-runtime-delta</c>。</summary>
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    /// <summary>大版本号，例如 <c>21</c>。</summary>
    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

/// <summary>日志配置根节点。</summary>
public sealed class LoggingConfig
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; set; }
}

/// <summary>客户端日志配置。</summary>
public sealed class LoggingClient
{
    /// <summary>注入到 JVM 的参数模板，含 <c>${path}</c> 占位符。</summary>
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    /// <summary>日志配置文件元数据。</summary>
    [JsonPropertyName("file")]
    public VersionFile? File { get; set; }

    /// <summary>配置类型，例如 <c>log4j2-xml</c>。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
