using System.IO.Compression;

namespace OML.Core.Versioning;

/// <summary>
/// 本地库解压器。
/// <para>
/// 只解压平台相关的动态库（Windows 为 <c>.dll</c>），跳过 <c>META-INF/</c>：
/// 后者含签名与清单，解压出来既没用，又可能被游戏当成类路径资源误读。
/// </para>
/// </summary>
public static class NativesExtractor
{
    /// <summary>Windows 上唯一需要的后缀。</summary>
    public static readonly IReadOnlyList<string> WindowsExtensions = new[] { ".dll" };

    /// <summary>
    /// 解压一组 natives jar 到目标目录。
    /// </summary>
    /// <param name="jarPaths">natives jar 路径（不存在会被跳过）。</param>
    /// <param name="targetDirectory">目标目录，通常是 <c>versions/&lt;id&gt;/natives</c>。</param>
    /// <param name="allowedExtensions">允许的后缀，默认 <c>.dll</c>。</param>
    /// <param name="flatten">
    /// 是否把文件直接放到目标目录根部（默认 <c>true</c>）。
    /// <para>
    /// 必须压平：LWJGL 3 的 natives jar 内部结构是
    /// <c>windows/x64/org/lwjgl/lwjgl.dll</c>，按原路径解压会得到四层嵌套目录，
    /// 而 <c>-Djava.library.path=&lt;natives&gt;</c> 不会递归子目录，游戏仍然找不到 dll。
    /// 旧版（≤1.18）的 natives jar 里 dll 本来就在根部，压平不影响。
    /// </para>
    /// </param>
    /// <returns>实际落盘的文件绝对路径。</returns>
    public static IReadOnlyList<string> Extract(
        IEnumerable<string> jarPaths,
        string targetDirectory,
        IEnumerable<string>? allowedExtensions = null,
        bool flatten = true)
    {
        ArgumentNullException.ThrowIfNull(jarPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var extensions = new HashSet<string>(
            allowedExtensions ?? WindowsExtensions,
            StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(targetDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var extracted = new List<string>();

        foreach (var jarPath in jarPaths)
        {
            if (!File.Exists(jarPath))
            {
                continue;
            }

            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue; // 目录项
                }

                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("META-INF\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!extensions.Contains(Path.GetExtension(entry.Name)))
                {
                    continue;
                }

                // 含 ".." 段的条目直接丢弃：压平模式下它虽然写不出目标目录，
                // 但也没必要让恶意 jar 在 natives 里凭空多出一个文件。
                if (entry.FullName.Split('/', '\\').Contains("..", StringComparer.Ordinal))
                {
                    continue;
                }

                var relativeName = flatten ? Path.GetFileName(entry.FullName) : entry.FullName;
                var destination = Path.GetFullPath(Path.Combine(targetDirectory, relativeName));

                // Zip Slip 防护：压缩包内可能藏 "../../evil.dll"。
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                extracted.Add(destination);
            }
        }

        return extracted;
    }
}
