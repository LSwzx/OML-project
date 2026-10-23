namespace OML.Abstractions.Downloads;

/// <summary>
/// 下载源描述。PCL2 风格的多源策略以此为基本单位：官方源 + 若干镜像源，
/// 由 <see cref="IDownloadSourceProvider"/> 负责测速与择优。
/// </summary>
public sealed class DownloadSource
{
    /// <summary>显示名称，例如 <c>Mojang</c>、<c>BMCLAPI</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>基地址，必须以 <c>/</c> 结尾，例如 <c>https://bmclapi2.bangbang93.com/</c>。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>是否为官方源。官方源通常作为兜底而非首选。</summary>
    public bool IsOfficial { get; init; }

    /// <summary>静态优先级，数值越小越优先（测速结果相同或测速失败时的决断依据）。</summary>
    public int Priority { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({BaseUrl})";
}
