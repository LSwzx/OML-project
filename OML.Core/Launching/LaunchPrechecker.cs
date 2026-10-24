using OML.Abstractions.Launching;

namespace OML.Core.Launching;

/// <summary>预检结果。</summary>
public sealed class LaunchPrecheckResult
{
    /// <summary>是否可启动。</summary>
    public bool IsValid => Problems.Count == 0;

    /// <summary>全部问题。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>缺失的 classpath 文件。</summary>
    public IReadOnlyList<string> MissingClasspathEntries { get; init; } = Array.Empty<string>();

    /// <summary>缺失的主 jar。</summary>
    public string? MissingClientJar { get; init; }

    /// <summary>natives 目录问题（目录不存在或没有 dll）。</summary>
    public string? NativesProblem { get; init; }
}

/// <summary>
/// 启动前完整性预检。
/// <para>
/// 目的是把"用户能看懂的错误"提前到启动瞬间：缺 3 个 jar 时直接告诉他缺哪 3 个，
/// 而不是让 Java 跑两秒后抛 <c>ClassNotFoundException</c> 或 <c>UnsatisfiedLinkError</c>。
/// </para>
/// </summary>
public sealed class LaunchPrechecker
{
    /// <summary>是否要求客户端 jar 存在（Forge 等继承版本可能没有独立 jar）。</summary>
    public bool RequireClientJar { get; init; } = true;

    /// <summary>是否要求 natives 目录里有 dll。</summary>
    public bool RequireNatives { get; init; } = true;

    /// <summary>检查并返回结果，不抛异常。</summary>
    public LaunchPrecheckResult Check(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var missingClasspath = new List<string>();

        foreach (var entry in options.Libraries.Classpath)
        {
            if (!File.Exists(entry))
            {
                missingClasspath.Add(entry);
            }
        }

        if (options.Libraries.Classpath.Count == 0)
        {
            problems.Add("classpath 为空：库解析可能失败。");
        }
        else if (missingClasspath.Count > 0)
        {
            problems.Add($"classpath 缺失 {missingClasspath.Count} 个文件，例如：{string.Join(", ", missingClasspath.Take(3).Select(Path.GetFileName))}");
        }

        string? missingClientJar = null;
        if (RequireClientJar)
        {
            var jarPath = string.IsNullOrWhiteSpace(options.ClientJarPath)
                ? options.GameDirectory.GetVersionJarPath(options.Version.Id)
                : options.ClientJarPath!;

            if (!File.Exists(jarPath))
            {
                missingClientJar = jarPath;
                problems.Add($"客户端 jar 不存在：{jarPath}");
            }
        }

        string? nativesProblem = null;
        if (RequireNatives && options.Libraries.Natives.Count > 0)
        {
            var nativesDirectory = options.GameDirectory.GetVersionNativesDirectory(options.Version.Id);
            if (!Directory.Exists(nativesDirectory))
            {
                nativesProblem = $"natives 目录不存在：{nativesDirectory}";
            }
            else if (!Directory.EnumerateFiles(nativesDirectory, "*.dll", SearchOption.TopDirectoryOnly).Any())
            {
                nativesProblem = $"natives 目录内没有 .dll：{nativesDirectory}";
            }

            if (nativesProblem is not null)
            {
                problems.Add(nativesProblem);
            }
        }

        return new LaunchPrecheckResult
        {
            Problems = problems,
            MissingClasspathEntries = missingClasspath,
            MissingClientJar = missingClientJar,
            NativesProblem = nativesProblem,
        };
    }

    /// <summary>检查并在有问题时抛出 <see cref="LaunchPrecheckException"/>。</summary>
    public void Ensure(LaunchOptions options)
    {
        var result = Check(options);
        if (!result.IsValid)
        {
            throw new LaunchPrecheckException(result.Problems);
        }
    }
}
