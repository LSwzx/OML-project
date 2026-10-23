using System.Diagnostics.CodeAnalysis;

namespace OML.Abstractions.Models;

/// <summary>
/// Maven 坐标解析器。
/// <para>
/// 支持 <c>group:artifact:version</c>、<c>group:artifact:version:classifier</c>
/// 以及可选的 <c>@扩展名</c> 后缀（如 <c>net.java:jinput-platform:2.0.5:natives-windows@jar</c>）。
/// </para>
/// <para>
/// 放在抽象层是为了让 <see cref="Library"/> 能自带路径推导能力，
/// 而不必让上层为了一条依赖去重复实现字符串切割。
/// </para>
/// </summary>
public readonly record struct MavenCoordinate(
    string GroupId,
    string ArtifactId,
    string Version,
    string? Classifier,
    string Extension)
{
    /// <summary>
    /// 相对于 Maven 仓库根（或 <c>libraries/</c> 目录）的路径。
    /// 例：<c>org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar</c>
    /// </summary>
    public string RelativePath
    {
        get
        {
            var directory = GroupId.Replace('.', '/');
            var fileName = Classifier is null
                ? $"{ArtifactId}-{Version}.{Extension}"
                : $"{ArtifactId}-{Version}-{Classifier}.{Extension}";

            return $"{directory}/{ArtifactId}/{Version}/{fileName}";
        }
    }

    /// <summary>解析 Maven 坐标。空串、段数不足或版本号缺失均视为失败。</summary>
    public static bool TryParse([NotNullWhen(true)] string? name, out MavenCoordinate coordinate)
    {
        coordinate = default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // 剥离 "@jar" 之类的扩展名后缀，缺省为 jar。
        var body = name;
        var extension = "jar";
        var atIndex = name.LastIndexOf('@');
        if (atIndex > 0)
        {
            extension = name[(atIndex + 1)..];
            body = name[..atIndex];

            if (extension.Length == 0)
            {
                extension = "jar";
            }
        }

        var parts = body.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return false;
        }

        var classifier = parts.Length == 4 && parts[3].Length > 0 ? parts[3] : null;
        coordinate = new MavenCoordinate(parts[0], parts[1], parts[2], classifier, extension);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Classifier is null
            ? $"{GroupId}:{ArtifactId}:{Version}"
            : $"{GroupId}:{ArtifactId}:{Version}:{Classifier}";
}
