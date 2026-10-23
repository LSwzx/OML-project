namespace OML.Abstractions.Modrinth;

/// <summary>Modrinth 搜索响应。</summary>
public sealed class ModrinthSearchResponse
{
    public List<ModrinthSearchHit> Hits { get; set; } = new();

    public int Offset { get; set; }

    public int Limit { get; set; }

    public int TotalHits { get; set; }
}

/// <summary>搜索结果条目。</summary>
public sealed class ModrinthSearchHit
{
    public string ProjectId { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? IconUrl { get; set; }

    public long Downloads { get; set; }

    public long Follows { get; set; }

    public string? Author { get; set; }

    public string ProjectType { get; set; } = string.Empty;

    public List<string> Categories { get; set; } = new();

    public List<string> Versions { get; set; } = new();
}

/// <summary>项目详情。</summary>
public sealed class ModrinthProject
{
    public string Id { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? IconUrl { get; set; }

    public long Downloads { get; set; }

    public long Followers { get; set; }

    public string ProjectType { get; set; } = string.Empty;

    public string? ClientSide { get; set; }

    public string? ServerSide { get; set; }

    public List<string> Categories { get; set; } = new();

    public List<string> GameVersions { get; set; } = new();

    public List<string> Loaders { get; set; } = new();

    public List<string>? AdditionalCategories { get; set; }

    public string? SourceUrl { get; set; }

    public string? IssuesUrl { get; set; }
}

/// <summary>某个项目的具体版本。</summary>
public sealed class ModrinthVersion
{
    public string Id { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string VersionNumber { get; set; } = string.Empty;

    /// <summary><c>release</c> / <c>beta</c> / <c>alpha</c>。</summary>
    public string VersionType { get; set; } = string.Empty;

    public DateTimeOffset DatePublished { get; set; }

    public long Downloads { get; set; }

    public List<string> GameVersions { get; set; } = new();

    public List<string> Loaders { get; set; } = new();

    public List<ModrinthVersionFile> Files { get; set; } = new();

    public List<ModrinthDependency> Dependencies { get; set; } = new();
}

/// <summary>版本里的一个文件。</summary>
public sealed class ModrinthVersionFile
{
    public string Url { get; set; } = string.Empty;

    public string Filename { get; set; } = string.Empty;

    public bool Primary { get; set; }

    public long Size { get; set; }

    public ModrinthHashes Hashes { get; set; } = new();
}

/// <summary>文件哈希。</summary>
public sealed class ModrinthHashes
{
    public string? Sha1 { get; set; }

    public string? Sha512 { get; set; }
}

/// <summary>依赖关系。</summary>
public sealed class ModrinthDependency
{
    public string? VersionId { get; set; }

    public string? ProjectId { get; set; }

    public string? FileName { get; set; }

    /// <summary><c>required</c> / <c>optional</c> / <c>incompatible</c> / <c>embedded</c>。</summary>
    public string DependencyType { get; set; } = string.Empty;
}
