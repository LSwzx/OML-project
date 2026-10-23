using System.Text.Json.Serialization;

namespace OML.Abstractions.Models;

/// <summary>
/// 资源索引。
/// <para>
/// 同一个类型承担两种形态：
/// 1. 版本 JSON 里的<b>引用</b>（<c>id/sha1/size/totalSize/url</c>），此时 <see cref="Objects"/> 为空；
/// 2. 下载下来的<b>索引文件</b>（<c>assets/indexes/&lt;id&gt;.json</c>），此时 <see cref="Objects"/> 有几千项。
/// </para>
/// <para>
/// 之所以不拆成两个类型：两者是同一实体的不同切片，拆开后调用方得记住"该传哪种"，
/// 而合并后只用判断 <see cref="Objects"/> 是否为空即可。
/// </para>
/// </summary>
public sealed class AssetIndex
{
    /// <summary>资源索引 ID，同时是 <c>assets/indexes/&lt;id&gt;.json</c> 的文件名。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>索引文件本身的 SHA-1。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>索引文件压缩后大小（字节）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>全部资源解压后的总大小（字节）。</summary>
    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    /// <summary>索引文件下载地址（仅"引用"形态有值）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 资源对象表：虚拟路径 → 对象。键形如 <c>minecraft/sounds/ambient/cave/cave1.ogg</c>。
    /// 仅"索引文件"形态有值。
    /// </summary>
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

/// <summary>单个资源对象。内容寻址：同一 hash 的文件在所有版本间共享。</summary>
public sealed class AssetObject
{
    /// <summary>SHA-1，同时也是文件名。</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}
