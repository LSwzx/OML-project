using System.Text.Json.Serialization;

namespace OML.Abstractions.Models;

/// <summary>
/// 依赖库条目。
/// <para>
/// 存在两种互斥的寻址方式：新版本靠 <see cref="Downloads"/> 里的绝对 <c>url</c>，
/// 旧版本（或部分第三方库）只有 <see cref="Url"/> 作为 Maven 仓库基地址，需要靠
/// <see cref="Name"/> 拼出相对路径。抽象层只负责如实描述，不做决策。
/// </para>
/// </summary>
public sealed class Library
{
    /// <summary>Maven 坐标，格式 <c>group:artifact:version[:classifier][@ext]</c>。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>新格式下载信息（含 artifact 与 natives 分类器）。</summary>
    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    /// <summary>旧格式：操作系统 → natives 分类器后缀，例如 <c>{"windows":"natives-windows"}</c>。</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    /// <summary>生效规则；为空表示无条件生效。</summary>
    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    /// <summary>旧格式：解压排除项，存在即表示该库需解压 natives。</summary>
    [JsonPropertyName("extract")]
    public LibraryExtract? Extract { get; set; }

    /// <summary>旧格式：Maven 仓库基地址，没有 <see cref="Downloads"/> 时使用。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 是否可直接按坐标拼出相对路径（用于旧格式库）。
    /// 解析失败（坐标非法）时返回 <c>null</c>，调用方应视为不可用。
    /// </summary>
    [JsonIgnore]
    public string? MavenRelativePath =>
        MavenCoordinate.TryParse(Name, out var coordinate) ? coordinate.RelativePath : null;
}

/// <summary>库的下载信息。</summary>
public sealed class LibraryDownloads
{
    /// <summary>主构件（无分类器）。</summary>
    [JsonPropertyName("artifact")]
    public LibraryArtifact? Artifact { get; set; }

    /// <summary>分类器构件，键为分类器名（如 <c>natives-windows</c>、<c>sources</c>）。</summary>
    [JsonPropertyName("classifiers")]
    public Dictionary<string, LibraryArtifact>? Classifiers { get; set; }
}

/// <summary>单个库文件。</summary>
public sealed class LibraryArtifact
{
    /// <summary>相对于 <c>libraries/</c> 的路径，例如 <c>org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar</c>。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>解压配置。</summary>
public sealed class LibraryExtract
{
    /// <summary>解压时排除的路径前缀（通常是 <c>META-INF/</c>）。</summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = new();
}

/// <summary>单条生效规则。</summary>
public sealed class Rule
{
    /// <summary><c>allow</c> 或 <c>disallow</c>。</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    /// <summary>操作系统条件。</summary>
    [JsonPropertyName("os")]
    public RuleOs? Os { get; set; }

    /// <summary>特性条件；键为特性名，值为期望状态（如 <c>has_custom_resolution: true</c>）。</summary>
    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }
}

/// <summary>规则中的操作系统条件。字段缺省即"不限"。</summary>
public sealed class RuleOs
{
    /// <summary>操作系统名：<c>windows</c> / <c>osx</c> / <c>linux</c>。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>正则匹配的操作系统版本。</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>架构：<c>x86</c> / <c>x86_64</c> / <c>arm64</c>。</summary>
    [JsonPropertyName("arch")]
    public string? Arch { get; set; }
}
