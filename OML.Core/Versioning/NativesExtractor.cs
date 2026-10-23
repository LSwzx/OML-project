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
    /// <returns>实际落盘的文件绝对路径。</returns>
    public static IReadOnlyList<string> Extract(
        IEnumerable<string> jarPaths,
        string targetDirectory,
        IEnumerable<string>? allowedExtensions = null)
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

                var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));

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
