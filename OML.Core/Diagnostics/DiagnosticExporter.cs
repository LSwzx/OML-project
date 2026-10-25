using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OML.Core.Java;

namespace OML.Core.Diagnostics;

/// <summary>导出诊断报告：日志、崩溃报告、环境信息与脱敏配置。</summary>
public sealed class DiagnosticExporter
{
    private const long MaxCrashReportBytes = 10 * 1024 * 1024;
    private const int MaxCrashReports = 5;

    private readonly string _configPath;
    private readonly string _launcherRoot;

    public DiagnosticExporter(string configPath, string launcherRoot)
    {
        _configPath = configPath;
        _launcherRoot = launcherRoot;
    }

    /// <summary>生成诊断 zip，返回最终路径。</summary>
    public async Task<string> ExportAsync(
        string outputZipPath,
        string? gameDirectory,
        string? versionId,
        string launcherLog,
        string? javaPath,
        string launcherVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);

        var staging = Path.Combine(Path.GetTempPath(), "oml-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(staging, "launcher-log.txt"),
                string.IsNullOrWhiteSpace(launcherLog) ? "（本次运行还没有日志）" : launcherLog,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(staging, "launcher-config.json"),
                ReadSanitizedConfig(),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(staging, "system-info.txt"),
                BuildSystemInfo(launcherVersion),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(staging, "java-info.txt"),
                await BuildJavaInfoAsync(javaPath, cancellationToken).ConfigureAwait(false),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(gameDirectory))
            {
                CopyGameLogs(gameDirectory, versionId, Path.Combine(staging, "game"));
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(staging, "game"));
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "game", "缺少游戏目录.txt"),
                    "配置中的游戏目录不存在或未设置。",
                    cancellationToken).ConfigureAwait(false);
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputZipPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(outputZipPath))
            {
                File.Delete(outputZipPath);
            }

            ZipFile.CreateFromDirectory(staging, outputZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return outputZipPath;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响导出结果。
            }
        }
    }

    /// <summary>读取配置并脱敏；文件缺失时返回说明文本。</summary>
    public string ReadSanitizedConfig()
    {
        if (!File.Exists(_configPath))
        {
            return "launcher_config.json 不存在。";
        }

        var json = File.ReadAllText(_configPath);
        return SanitizeConfigJson(json);
    }

    /// <summary>
    /// 把 accessToken / refreshToken / secret / password 一类的字段替换为 <c>***</c>。
    /// 递归处理任意层级，避免以后加字段时漏掉。
    /// </summary>
    public static string SanitizeConfigJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return "launcher_config.json 解析失败（已跳过脱敏，未包含原文）。";
        }

        SanitizeNode(root);

        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
    }

    private static void SanitizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.Select(pair => pair.Key).ToArray())
                {
                    if (IsSensitive(property))
                    {
                        obj[property] = "***";
                    }
                    else
                    {
                        SanitizeNode(obj[property]);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    SanitizeNode(item);
                }

                break;
        }
    }

    private static bool IsSensitive(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static string BuildSystemInfo(string launcherVersion)
    {
        var gc = GC.GetGCMemoryInfo();

        return $"""
            OML 诊断报告
            生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            启动器版本：{launcherVersion}
            操作系统：{RuntimeInformation.OSDescription}
            系统架构：{RuntimeInformation.OSArchitecture}
            进程架构：{RuntimeInformation.ProcessArchitecture}
            .NET 运行时：{RuntimeInformation.FrameworkDescription}
            处理器数量：{Environment.ProcessorCount}
            物理内存：{BytesToGb(gc.TotalAvailableMemoryBytes)}
            """;
    }

    private async Task<string> BuildJavaInfoAsync(string? javaPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            return "未配置 Java 路径（启动器会自动探测）。";
        }

        var scanner = new JavaScanner();
        var major = await scanner.GetJavaMajorVersionAsync(javaPath, cancellationToken).ConfigureAwait(false);
        var is32Bit = await scanner.Is32BitAsync(javaPath, cancellationToken).ConfigureAwait(false);

        return $"""
            Java 路径：{javaPath}
            主版本：{(major is null ? "未知" : major.ToString())}
            架构：{(is32Bit ? "32 位 (x86)" : "64 位")}
            """;
    }

    private static void CopyGameLogs(string gameDirectory, string? versionId, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        CopyIfExists(Path.Combine(gameDirectory, "logs", "latest.log"), Path.Combine(targetDirectory, "latest.log"));

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            CopyIfExists(
                Path.Combine(gameDirectory, "versions", versionId, "logs", "latest.log"),
                Path.Combine(targetDirectory, "version-latest.log"));
        }

        var crashDirectories = new List<string> { Path.Combine(gameDirectory, "crash-reports") };
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            crashDirectories.Add(Path.Combine(gameDirectory, "versions", versionId, "crash-reports"));
        }

        var index = 0;
        foreach (var crashDirectory in crashDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(crashDirectory, "*.txt")
                         .Select(path => new FileInfo(path))
                         .Where(info => info.Length <= MaxCrashReportBytes)
                         .OrderByDescending(info => info.LastWriteTimeUtc)
                         .Take(MaxCrashReports))
            {
                index++;
                CopyIfExists(file.FullName, Path.Combine(targetDirectory, $"crash-report-{index}-{file.Name}"));
            }
        }
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static string BytesToGb(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
}
