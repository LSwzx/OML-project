using System.Text.Json;
using OML.Abstractions.Models;

namespace OML.Core.Versioning;

/// <summary>
/// <c>inheritsFrom</c> 继承合并。
/// <para>
/// Forge / Fabric / NeoForge 的版本 JSON 只描述"增量"：继承原版，追加自己的库、参数和主类。
/// 启动器必须把父子合成一份"完整版本"才能构建 classpath 与命令行。
/// </para>
/// <para>
/// 合并语义（与 HMCL 一致）：
/// <list type="bullet">
/// <item><b>libraries</b>：以 <c>group:artifact[:classifier]</c> 为身份键（<b>刻意不带版本号</b>），
/// 子级同键条目覆盖父级，但保留父级原有位置；子级全新条目追加到末尾。</item>
/// <item><b>arguments</b>：父在前、子在后的顺序拼接。</item>
/// <item><b>mainClass</b>：子级非空则覆盖。</item>
/// <item><b>assetIndex / assets / javaVersion / downloads / logging</b>：子级缺省时继承父级。</item>
/// </list>
/// </para>
/// </summary>
public static class VersionInheritor
{
    /// <summary>
    /// 合并父版本与子版本，返回新对象（不修改入参）。
    /// </summary>
    public static VersionDetail Merge(VersionDetail parent, VersionDetail child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        return new VersionDetail
        {
            Id = child.Id,
            Type = string.IsNullOrWhiteSpace(child.Type) ? parent.Type : child.Type,
            MainClass = string.IsNullOrWhiteSpace(child.MainClass) ? parent.MainClass : child.MainClass,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Libraries = MergeLibraries(parent.Libraries, child.Libraries),
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Downloads = MergeDownloads(parent.Downloads, child.Downloads),
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            InheritsFrom = child.InheritsFrom,
            MinimumLauncherVersion = MaxNullable(parent.MinimumLauncherVersion, child.MinimumLauncherVersion),
            ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
            Time = child.Time ?? parent.Time,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
        };
    }

    /// <summary>
    /// 库合并。身份键不含版本号：Forge 用"同名不同版本"表达升级，
    /// 若按完整 <c>name</c> 判重，classpath 里会同时出现 guava 的两个版本，运行时炸 <c>NoSuchMethodError</c>。
    /// </summary>
    public static List<Library> MergeLibraries(
        IReadOnlyList<Library> parent,
        IReadOnlyList<Library> child)
    {
        var merged = new List<Library>(parent.Count + child.Count);
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in parent)
        {
            var key = LibraryKey(library);
            if (indexByKey.TryAdd(key, merged.Count))
            {
                merged.Add(library);
            }
            else
            {
                merged[indexByKey[key]] = library;
            }
        }

        foreach (var library in child)
        {
            var key = LibraryKey(library);
            if (indexByKey.TryGetValue(key, out var existing))
            {
                merged[existing] = library;
            }
            else
            {
                indexByKey[key] = merged.Count;
                merged.Add(library);
            }
        }

        return merged;
    }

    /// <summary>参数合并：父级在前、子级在后。</summary>
    public static Arguments? MergeArguments(Arguments? parent, Arguments? child)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is null)
        {
            return parent;
        }

        return new Arguments
        {
            Game = [.. parent.Game, .. child.Game],
            Jvm = [.. parent.Jvm, .. child.Jvm],
        };
    }

    /// <summary>下载信息合并：逐字段取子级优先。</summary>
    public static VersionDownloads? MergeDownloads(VersionDownloads? parent, VersionDownloads? child)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is null)
        {
            return parent;
        }

        return new VersionDownloads
        {
            Client = child.Client ?? parent.Client,
            Server = child.Server ?? parent.Server,
            ClientMappings = child.ClientMappings ?? parent.ClientMappings,
            ServerMappings = child.ServerMappings ?? parent.ServerMappings,
        };
    }

    /// <summary>库身份键：<c>group:artifact[:classifier]</c>，解析失败时退化为原始 name。</summary>
    private static string LibraryKey(Library library)
    {
        if (MavenCoordinate.TryParse(library.Name, out var coordinate))
        {
            return coordinate.Classifier is null
                ? $"{coordinate.GroupId}:{coordinate.ArtifactId}"
                : $"{coordinate.GroupId}:{coordinate.ArtifactId}:{coordinate.Classifier}";
        }

        return library.Name;
    }

    private static int? MaxNullable(int? left, int? right) => (left, right) switch
    {
        (null, null) => null,
        (null, _) => right,
        (_, null) => left,
        _ => Math.Max(left.Value, right.Value),
    };

    /// <summary>
    /// 读取并合并 <c>inheritsFrom</c> 链。仅供测试/离线场景使用；
    /// 需要网络拉取请走 <see cref="VersionRepository.GetResolvedVersionAsync"/>。
    /// </summary>
    public static VersionDetail MergeChain(IEnumerable<VersionDetail> chainFromChildToParent)
    {
        var chain = chainFromChildToParent.ToList();
        if (chain.Count == 0)
        {
            throw new ArgumentException("继承链为空。", nameof(chainFromChildToParent));
        }

        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            merged = Merge(merged, chain[i]);
        }

        return merged;
    }
}
