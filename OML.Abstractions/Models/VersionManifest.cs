using System.Text.Json.Serialization;

namespace OML.Abstractions.Models;

/// <summary>
/// 版本清单根节点，对应 <c>version_manifest_v2.json</c>。
/// 下载地址：<c>https://launchermeta.mojang.com/mc/game/version_manifest_v2.json</c>
/// </summary>
public sealed class VersionManifest
{
    /// <summary>最新版本指针（正式版 / 快照）。</summary>
    [JsonPropertyName("latest")]
    public LatestVersions? Latest { get; set; }

    /// <summary>全部可用版本，按发布时间倒序。</summary>
    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = new();
}

/// <summary>清单中的 <c>latest</c> 节点。</summary>
public sealed class LatestVersions
{
    /// <summary>最新正式版版本号，例如 <c>1.21.1</c>。</summary>
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    /// <summary>最新快照版本号，例如 <c>24w33a</c>。</summary>
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

/// <summary>清单 <c>versions</c> 数组中的单个条目（只有摘要信息，不含 libraries 等细节）。</summary>
public sealed class VersionSummary
{
    /// <summary>版本号，例如 <c>1.21.1</c>、<c>24w33a</c>。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>版本类型：<c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>该版本详情 JSON 的下载地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>入库时间（UTC，ISO 8601）。</summary>
    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    /// <summary>正式发布时间（UTC，ISO 8601）。</summary>
    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }

    /// <summary>详情 JSON 的 SHA-1（v2 清单新增）。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>合规等级（v2 清单新增），客户端行为变更时递增。</summary>
    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }
}
