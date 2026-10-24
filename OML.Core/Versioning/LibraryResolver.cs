using OML.Abstractions.Downloads;
using OML.Abstractions.Models;

namespace OML.Core.Versioning;

/// <summary>一个待解压的 natives jar。</summary>
/// <param name="LibraryName">来源库的 Maven 坐标。</param>
/// <param name="Classifier">分类器，例如 <c>natives-windows</c>。</param>
/// <param name="JarPath">jar 在 <c>libraries/</c> 下的绝对路径。</param>
/// <param name="Download">对应的下载任务（文件已在本地时仍然给出，由引擎短路）。</param>
public sealed record NativeJar(string LibraryName, string Classifier, string JarPath, DownloadTask Download);

/// <summary>库解析结果。</summary>
public sealed class LibraryResolution
{
    /// <summary>classpath 条目（绝对路径，按版本 JSON 中的顺序）。</summary>
    public IReadOnlyList<string> Classpath { get; init; } = Array.Empty<string>();

    /// <summary>需要下载的全部文件（普通库 + natives jar）。</summary>
    public IReadOnlyList<DownloadTask> Downloads { get; init; } = Array.Empty<DownloadTask>();

    /// <summary>需要解压的 natives jar。</summary>
    public IReadOnlyList<NativeJar> Natives { get; init; } = Array.Empty<NativeJar>();

    /// <summary>无法解析的库（缺 URL / 缺坐标等），供上层展示或记录日志。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>拼成 <c>-cp</c> 参数值。</summary>
    public string ToClasspathString() =>
        string.Join(Path.PathSeparator, Classpath);
}

/// <summary>
/// 把 <see cref="VersionDetail.Libraries"/> 解析成「classpath + 下载任务 + natives 任务」。
/// <para>
/// 同时兼容两代清单格式：
/// <list type="bullet">
/// <item><b>≤1.18</b>：库带 <c>natives</c> 字典与 <c>downloads.classifiers</c>，按平台取分类器；</item>
/// <item><b>≥1.19</b>：<c>natives</c> 字段消失，本地库变成独立条目，分类器写在 <c>name</c> 里
/// （如 <c>org.lwjgl:lwjgl:3.3.3:natives-windows</c>），靠 <c>rules</c> 过滤平台。</item>
/// </list>
/// </para>
/// <para>
/// <b>必须额外做架构筛选：</b>1.21.1 里 <c>natives-windows</c>、<c>natives-windows-x86</c>、
/// <c>natives-windows-arm64</c> 三条的 rules 都只写了 <c>os.name == windows</c>，
/// 光靠规则求值会把三种架构的 dll 全部解压到同一个目录，互相覆盖后必然
/// <c>UnsatisfiedLinkError</c>。因此这里按分类器后缀二次筛选，每组只留一个。
/// </para>
/// </summary>
public sealed class LibraryResolver
{
    private static readonly char Separator = Path.DirectorySeparatorChar;

    private readonly GameDirectory _gameDirectory;
    private readonly RuleEvaluator _rules;
    private readonly Func<string, string> _urlMapper;

    /// <param name="gameDirectory">游戏目录（决定 libraries 落点）。</param>
    /// <param name="ruleEvaluator">规则求值器；默认按当前平台。</param>
    /// <param name="urlMapper">URL 改写钩子（接入镜像时传 <c>BmclApiPathMapper.Map</c>）。</param>
    public LibraryResolver(
        GameDirectory gameDirectory,
        RuleEvaluator? ruleEvaluator = null,
        Func<string, string>? urlMapper = null)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
        _rules = ruleEvaluator ?? RuleEvaluator.ForCurrentPlatform();
        _urlMapper = urlMapper ?? (url => url);
    }

    /// <summary>解析版本依赖。</summary>
    public LibraryResolution Resolve(VersionDetail version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var warnings = new List<string>();
        var downloads = new List<DownloadTask>();
        var natives = new List<NativeJar>();
        var classpath = new List<string>();
        var classpathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeGroups = new Dictionary<string, List<(string Name, string Classifier, LibraryArtifact Artifact)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in version.Libraries)
        {
            if (!_rules.Evaluate(library.Rules))
            {
                continue;
            }

            // 第一代格式：natives 字典 + classifiers。
            // 注意不能 continue：旧版库往往同时有主构件（如 jinput-platform 的 Java 类），
            // 主构件要进 classpath，只有分类器 jar 才是纯 dll。
            var legacyNative = ResolveLegacyNative(library, warnings);
            if (legacyNative is { } nativeJar)
            {
                natives.Add(nativeJar);
                downloads.Add(nativeJar.Download);
            }

            if (!MavenCoordinate.TryParse(library.Name, out var coordinate))
            {
                warnings.Add($"无法解析 Maven 坐标：{library.Name}");
                continue;
            }

            // 第二代格式：natives 作为独立库条目，分类器在 name 里。
            if (IsNativeClassifier(coordinate.Classifier))
            {
                var artifact = library.Downloads?.Artifact;
                if (artifact?.Url is null || artifact.Path is null)
                {
                    warnings.Add($"natives 库缺少下载信息：{library.Name}");
                    continue;
                }

                var groupKey = $"{coordinate.GroupId}:{coordinate.ArtifactId}";
                if (!nativeGroups.TryGetValue(groupKey, out var list))
                {
                    list = new List<(string, string, LibraryArtifact)>();
                    nativeGroups[groupKey] = list;
                }

                list.Add((library.Name, coordinate.Classifier!, artifact));
                continue;
            }

            // 普通库。
            // PCL2 / OptiFine 安装器生成的 JSON 里，optifine:OptiFine、optifine:launchwrapper-of
            // 这类库既没有 downloads 也没有 url，但 jar 已经放在 libraries/ 下。
            // 只看 JSON 会把它漏出 classpath，启动时报
            // ClassNotFoundException: net.minecraft.launchwrapper.Launch。
            var localPath = CombineLibraries(coordinate.RelativePath);
            var resolved = ResolveArtifact(
                library,
                coordinate,
                warnings,
                suppressWarning: legacyNative is not null || File.Exists(localPath));

            if (resolved is null)
            {
                if (File.Exists(localPath) && classpathSet.Add(localPath))
                {
                    classpath.Add(localPath);
                }

                continue;
            }

            downloads.Add(resolved.Value.Task);
            if (classpathSet.Add(resolved.Value.Path))
            {
                classpath.Add(resolved.Value.Path);
            }
        }

        foreach (var (groupKey, candidates) in nativeGroups)
        {
            var chosen = ChooseNativeVariant(candidates, _rules.Platform.Arch);
            if (chosen is null)
            {
                warnings.Add($"没有适用于 {_rules.Platform.Arch} 的 natives 变体：{groupKey}");
                continue;
            }

            var artifact = chosen.Value.Artifact;
            var savePath = CombineLibraries(artifact.Path!);
            var task = new DownloadTask
            {
                Url = _urlMapper(artifact.Url!),
                SavePath = savePath,
                ExpectedSize = artifact.Size,
                Sha1 = artifact.Sha1,
            };

            downloads.Add(task);
            natives.Add(new NativeJar(chosen.Value.Name, chosen.Value.Classifier, savePath, task));

            // 官方启动器会把 natives jar 一并放进 classpath：LWJGL 3 的 SharedLibraryLoader
            // 支持直接从 jar 内加载 dll（嵌套路径也能定位），这是 1.19+ 的实际加载方式。
            // 压平解压到 natives 目录是给 -Djava.library.path 兜底，两条路都留着更稳。
            if (classpathSet.Add(savePath))
            {
                classpath.Add(savePath);
            }
        }

        return new LibraryResolution
        {
            Classpath = classpath,
            Downloads = downloads,
            Natives = natives,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 从同组的 <c>natives-windows</c> / <c>-x86</c> / <c>-arm64</c> 里挑出当前架构的那一个。
    /// 优先精确匹配架构后缀；没有精确项时回退到通用版（arm64 上跑 x64 的模拟场景）。
    /// </summary>
    private static (string Name, string Classifier, LibraryArtifact Artifact)? ChooseNativeVariant(
        List<(string Name, string Classifier, LibraryArtifact Artifact)> candidates,
        string arch)
    {
        var normalizedArch = PlatformInfo.NormalizeArch(arch);

        foreach (var candidate in candidates)
        {
            if (string.Equals(ArchSuffixOf(candidate.Classifier), normalizedArch, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        foreach (var candidate in candidates)
        {
            if (ArchSuffixOf(candidate.Classifier) is null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>取 <c>natives-windows-arm64</c> 里的 <c>arm64</c>；通用版返回 <c>null</c>。</summary>
    private static string? ArchSuffixOf(string? classifier)
    {
        if (classifier is null || !classifier.StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = classifier.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? PlatformInfo.NormalizeArch(parts[^1]) : null;
    }

    private static bool IsNativeClassifier(string? classifier) =>
        classifier is not null && classifier.StartsWith("natives-", StringComparison.OrdinalIgnoreCase);

    private NativeJar? ResolveLegacyNative(Library library, List<string> warnings)
    {
        if (library.Natives is null || library.Natives.Count == 0)
        {
            return null;
        }

        if (!library.Natives.TryGetValue(_rules.Platform.OsName, out var template) || string.IsNullOrWhiteSpace(template))
        {
            return null; // 该库没有当前平台的 natives，按普通库继续处理。
        }

        var classifier = template.Replace("${arch}", LegacyArchToken(_rules.Platform.Arch), StringComparison.Ordinal);

        if (library.Downloads?.Classifiers is not { } classifiers ||
            !classifiers.TryGetValue(classifier, out var artifact) ||
            artifact.Url is null)
        {
            warnings.Add($"旧格式 natives 缺少分类器 {classifier}：{library.Name}");
            return null;
        }

        var relativePath = artifact.Path
            ?? (MavenCoordinate.TryParse(library.Name, out var coordinate)
                ? new MavenCoordinate(coordinate.GroupId, coordinate.ArtifactId, coordinate.Version, classifier, coordinate.Extension).RelativePath
                : null);

        if (relativePath is null)
        {
            warnings.Add($"旧格式 natives 无法推导路径：{library.Name}");
            return null;
        }

        var jarPath = CombineLibraries(relativePath);
        var task = new DownloadTask
        {
            Url = _urlMapper(artifact.Url),
            SavePath = jarPath,
            ExpectedSize = artifact.Size,
            Sha1 = artifact.Sha1,
        };

        return new NativeJar(library.Name, classifier, jarPath, task);
    }

    private (string Path, DownloadTask Task)? ResolveArtifact(
        Library library,
        MavenCoordinate coordinate,
        List<string> warnings,
        bool suppressWarning = false)
    {
        var artifact = library.Downloads?.Artifact;

        if (artifact?.Url is not null)
        {
            var relativePath = artifact.Path ?? coordinate.RelativePath;
            var savePath = CombineLibraries(relativePath);
            return (savePath, new DownloadTask
            {
                Url = _urlMapper(artifact.Url),
                SavePath = savePath,
                ExpectedSize = artifact.Size,
                Sha1 = artifact.Sha1,
            });
        }

        // 旧格式：只有 Maven 仓库基地址，路径靠坐标拼。
        if (!string.IsNullOrWhiteSpace(library.Url))
        {
            var relativePath = coordinate.RelativePath;
            var savePath = CombineLibraries(relativePath);
            return (savePath, new DownloadTask
            {
                Url = _urlMapper(DownloadUrls.Combine(library.Url, relativePath)),
                SavePath = savePath,
                ExpectedSize = -1,
            });
        }

        if (!suppressWarning)
        {
            warnings.Add($"库缺少下载地址：{library.Name}");
        }

        return null;
    }

    private string CombineLibraries(string relativePath) =>
        Path.Combine(_gameDirectory.LibrariesDirectory, relativePath.Replace('/', Separator));

    /// <summary>旧版 <c>${arch}</c> 占位符只认 32/64 两种写法。</summary>
    private static string LegacyArchToken(string arch) => PlatformInfo.NormalizeArch(arch) switch
    {
        "x86_64" => "64",
        "x86" => "32",
        "arm64" => "arm64",
        var other => other,
    };

}
