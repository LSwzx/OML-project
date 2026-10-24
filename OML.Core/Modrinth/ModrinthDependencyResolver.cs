using System.Text;
using OML.Abstractions.Modrinth;
using OML.Core.Modrinth;

namespace OML.Core.Modrinth;

/// <summary>依赖树节点。</summary>
public sealed record DependencyNode(
    string ProjectId,
    string Title,
    string VersionNumber,
    string FileName,
    string Url,
    string? Sha1,
    long Size,
    IReadOnlyList<DependencyNode> Children);

/// <summary>
/// Modrinth 依赖递归解析。
/// <para>
/// 只解析 <c>required</c>；按 projectId 去重（防止 A→B→A 的死循环）；
/// 深度上限 8 层，超出只记录不展开。
/// </para>
/// </summary>
public sealed class ModrinthDependencyResolver
{
    private const int MaxDepth = 8;

    private readonly ModrinthClient _client;

    public ModrinthDependencyResolver(ModrinthClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>解析某个模组版本的完整必需依赖树。</summary>
    public async Task<IReadOnlyList<DependencyNode>> ResolveAsync(
        ModrinthVersion root,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ProjectId };
        return await ResolveChildrenAsync(root, gameVersion, loader, visited, depth: 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DependencyNode>> ResolveChildrenAsync(
        ModrinthVersion version,
        string gameVersion,
        string loader,
        HashSet<string> visited,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxDepth)
        {
            return Array.Empty<DependencyNode>();
        }

        var nodes = new List<DependencyNode>();

        foreach (var dependency in version.Dependencies)
        {
            if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(dependency.ProjectId))
            {
                continue;
            }

            var projectId = dependency.ProjectId!;
            if (!visited.Add(projectId))
            {
                continue;
            }

            ModrinthVersion? dependencyVersion;
            string title;
            try
            {
                dependencyVersion = await _client.GetLatestVersionAsync(projectId, gameVersion, loader, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                title = dependencyVersion is null
                    ? projectId
                    : (await _client.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)).Title;
            }
            catch
            {
                continue;
            }

            if (dependencyVersion is null)
            {
                continue;
            }

            var file = ModrinthClient.GetPrimaryFile(dependencyVersion);
            if (file is null)
            {
                continue;
            }

            var children = await ResolveChildrenAsync(dependencyVersion, gameVersion, loader, visited, depth + 1, cancellationToken)
                .ConfigureAwait(false);

            nodes.Add(new DependencyNode(
                projectId,
                title,
                dependencyVersion.VersionNumber,
                file.Filename,
                file.Url,
                file.Hashes.Sha1,
                file.Size,
                children));
        }

        return nodes;
    }

    /// <summary>把依赖树渲染成可直接放进对话框的缩进文本。</summary>
    public static string ToTreeText(string rootLabel, IReadOnlyList<DependencyNode> nodes)
    {
        var builder = new StringBuilder();
        builder.AppendLine(rootLabel);

        for (var i = 0; i < nodes.Count; i++)
        {
            AppendNode(builder, nodes[i], string.Empty, i == nodes.Count - 1);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendNode(StringBuilder builder, DependencyNode node, string indent, bool last)
    {
        builder.AppendLine($"{indent}{(last ? "└─ " : "├─ ")}{node.Title} {node.VersionNumber}  ({node.FileName})");

        var childIndent = indent + (last ? "   " : "│  ");
        for (var i = 0; i < node.Children.Count; i++)
        {
            AppendNode(builder, node.Children[i], childIndent, i == node.Children.Count - 1);
        }
    }

    /// <summary>按"先依赖后本体"的顺序把树拍平，用于顺序下载。</summary>
    public static IReadOnlyList<DependencyNode> Flatten(IReadOnlyList<DependencyNode> nodes)
    {
        var result = new List<DependencyNode>();

        void Walk(DependencyNode node)
        {
            foreach (var child in node.Children)
            {
                Walk(child);
            }

            result.Add(node);
        }

        foreach (var node in nodes)
        {
            Walk(node);
        }

        return result;
    }
}
