using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OML.Core.Java;

/// <summary>一个已发现的 Java 运行时。</summary>
/// <param name="Path">java.exe 的绝对路径。</param>
/// <param name="MajorVersion">主版本号（8 / 17 / 21 / 25）。</param>
/// <param name="Source">发现来源，便于排障（java_home / registry / common-dir / path）。</param>
public sealed record JavaInstallation(string Path, int MajorVersion, string Source);

/// <summary>
/// Java 探测：启动器绝不能写死 Java 版本——1.21.1 要 21，而较新的版本已经要 25。
/// <para>发现顺序：<c>JAVA_HOME</c> → 注册表 → 常见安装目录 → <c>PATH</c>。</para>
/// </summary>
public sealed class JavaScanner
{
    private static readonly Regex VersionPattern = new(
        "version\\s+\"(?<major>\\d+)(?:\\.(?<minor>\\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] RegistryRoots =
    {
        @"SOFTWARE\JavaSoft\JDK",
        @"SOFTWARE\JavaSoft\Java Development Kit",
        @"SOFTWARE\JavaSoft\JRE",
        @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\Eclipse Adoptium\JDK",
        @"SOFTWARE\Eclipse Foundation\JDK",
        @"SOFTWARE\Microsoft\JDK",
        @"SOFTWARE\Azul Systems\Zulu",
    };

    private static readonly string[] CommonParents =
    {
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
    };

    private readonly Dictionary<string, int> _majorVersionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private IReadOnlyList<JavaInstallation>? _scanned;

    /// <summary>扫描本机全部 JRE/JDK（结果会缓存，重复调用不会重复执行 java.exe）。</summary>
    public async Task<IReadOnlyList<JavaInstallation>> ScanAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_scanned is not null)
            {
                return _scanned;
            }
        }

        var candidates = new List<(string Path, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? javaExe, string source)
        {
            if (string.IsNullOrWhiteSpace(javaExe))
            {
                return;
            }

            var full = javaExe;
            try
            {
                full = Path.GetFullPath(javaExe);
            }
            catch
            {
                return;
            }

            if (File.Exists(full) && seen.Add(full))
            {
                candidates.Add((full, source));
            }
        }

        // 1. JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            Add(Path.Combine(javaHome, "bin", "java.exe"), "java_home");
        }

        // 2. 注册表
        foreach (var home in EnumerateRegistryJavaHomes())
        {
            Add(Path.Combine(home, "bin", "java.exe"), "registry");
        }

        // 3. 常见安装目录
        foreach (var parent in CommonParents)
        {
            foreach (var directory in EnumerateSubDirectories(parent))
            {
                Add(Path.Combine(directory, "bin", "java.exe"), "common-dir");
            }
        }

        // 4. PATH
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(Path.Combine(entry, "java.exe"), "path");
        }

        var installations = new List<JavaInstallation>(candidates.Count);
        foreach (var (path, source) in candidates)
        {
            var major = await GetJavaMajorVersionAsync(path, cancellationToken).ConfigureAwait(false);
            if (major is not null)
            {
                installations.Add(new JavaInstallation(path, major.Value, source));
            }
        }

        var ordered = installations
            .OrderByDescending(i => i.MajorVersion)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _scanned = ordered;
        }

        return ordered;
    }

    /// <summary>
    /// 执行 <c>java -version</c> 并解析主版本号。
    /// <para>注意：Java 把版本信息写到 <b>stderr</b>，只读 stdout 会得到空字符串。</para>
    /// <para>Java 8 的格式是 <c>"1.8.0_381"</c>，需要折叠成 8；Java 9+ 直接是 <c>"21.0.2"</c>。</para>
    /// </summary>
    public async Task<int?> GetJavaMajorVersionAsync(string javaExePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaExePath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(javaExePath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        lock (_gate)
        {
            if (_majorVersionCache.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }
        }

        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-version");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }

        var output = string.Concat(await stderr.ConfigureAwait(false), "\n", await stdout.ConfigureAwait(false));
        var major = ParseMajorVersion(output);

        if (major is not null)
        {
            lock (_gate)
            {
                _majorVersionCache[fullPath] = major.Value;
            }
        }

        return major;
    }

    /// <summary>
    /// 判断某个 java.exe 是不是 32 位。
    /// <para>
    /// 32 位 JVM 的最大堆通常只有 1.5–2GB，分配超过 2048MB 会直接抛
    /// <c>Invalid maximum heap size</c> 并退出，所以启动前必须先拦截。
    /// 用 <c>-XshowSettings:properties</c> 读 <c>os.arch</c>（32 位为 x86）。
    /// </para>
    /// </summary>
    public async Task<bool> Is32BitAsync(string javaExePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(javaExePath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(javaExePath);
        }
        catch
        {
            return false;
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-XshowSettings:properties");
        startInfo.ArgumentList.Add("-version");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return false;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return false;
        }

        var output = string.Concat(await stderr.ConfigureAwait(false), "\n", await stdout.ConfigureAwait(false));
        var archLine = output
            .Split('\n')
            .FirstOrDefault(l => l.Contains("os.arch", StringComparison.OrdinalIgnoreCase));

        return archLine is not null &&
               archLine.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
               !archLine.Contains("64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 找一台满足最低版本要求的 Java：优先精确匹配，其次"够用的最低版本"。
    /// 找不到返回 <c>null</c>（由上层决定是提示用户还是自动下载）。
    /// </summary>
    public async Task<string?> FindSuitableJavaAsync(int requiredMajorVersion, CancellationToken cancellationToken = default)
    {
        var installations = await ScanAsync(cancellationToken).ConfigureAwait(false);

        var exact = installations.FirstOrDefault(i => i.MajorVersion == requiredMajorVersion);
        if (exact is not null)
        {
            return exact.Path;
        }

        return installations
            .Where(i => i.MajorVersion >= requiredMajorVersion)
            .OrderBy(i => i.MajorVersion)
            .FirstOrDefault()
            ?.Path;
    }

    /// <summary>清空扫描与版本缓存（用户手动切换 Java 后调用）。</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _scanned = null;
            _majorVersionCache.Clear();
        }
    }

    /// <summary>从 <c>java -version</c> 输出中提取主版本号。</summary>
    public static int? ParseMajorVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        var match = VersionPattern.Match(versionOutput);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major))
        {
            return null;
        }

        // Java 8 及更早："1.8.0_381" → 8
        if (major == 1 && match.Groups["minor"].Success && int.TryParse(match.Groups["minor"].Value, out var minor))
        {
            return minor;
        }

        return major;
    }

    private static IEnumerable<string> EnumerateRegistryJavaHomes()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var root in RegistryRoots)
            {
                var homes = new List<string>();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(root);
                    if (key is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("JavaHome") is string home && !string.IsNullOrWhiteSpace(home))
                        {
                            homes.Add(home);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // 注册表权限不足：跳过这一支，其余来源仍可工作。
                }

                foreach (var home in homes)
                {
                    yield return home;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSubDirectories(string parent)
    {
        if (!Directory.Exists(parent))
        {
            yield break;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已退出。
        }
    }
}
