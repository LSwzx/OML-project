using System.Text;
using OML.Abstractions.Auth;
using OML.Abstractions.Models;
using OML.Core.Versioning;

namespace OML.Core.Launching;

/// <summary>启动所需的全部输入。</summary>
public sealed class LaunchOptions
{
    /// <summary>游戏目录（决定 game_directory / natives / assets 的落点）。</summary>
    public required GameDirectory GameDirectory { get; init; }

    /// <summary>已合并 inheritsFrom 的版本详情。</summary>
    public required VersionDetail Version { get; init; }

    /// <summary>库解析结果（classpath + natives）。</summary>
    public required LibraryResolution Libraries { get; init; }

    /// <summary>当前用户档案（离线/微软/LittleSkin）。</summary>
    public required UserProfile User { get; init; }

    /// <summary>java.exe 路径。</summary>
    public string JavaPath { get; init; } = "java";

    /// <summary>最大堆内存（MB）。</summary>
    public int MaxMemoryMb { get; init; } = 2048;

    /// <summary>初始堆内存（MB），0 表示不传 <c>-Xms</c>。</summary>
    public int MinMemoryMb { get; init; }

    /// <summary>额外 JVM 参数（用户在设置里填的）。</summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = Array.Empty<string>();

    /// <summary>额外游戏参数。</summary>
    public IReadOnlyList<string> ExtraGameArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是否把 <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c> 追加到 classpath 首位（默认开启）。
    /// Forge 等继承版本可能没有独立 jar，此时由上层关闭。
    /// </summary>
    public bool IncludeClientJarInClasspath { get; init; } = true;

    /// <summary>特性开关，供条件参数求值（如 <c>has_custom_resolution</c>）。</summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    /// <summary>窗口宽（仅 <c>has_custom_resolution</c> 为 true 时生效）。</summary>
    public int ResolutionWidth { get; init; } = 854;

    /// <summary>窗口高。</summary>
    public int ResolutionHeight { get; init; } = 480;

    /// <summary>启动器品牌名（写入 <c>-Dminecraft.launcher.brand</c>）。</summary>
    public string LauncherName { get; init; } = "OML";

    /// <summary>启动器版本。</summary>
    public string LauncherVersion { get; init; } = "0.1.0";
}

/// <summary>构建完成的命令行。</summary>
public sealed class LaunchArguments
{
    /// <summary>java.exe 路径（不包含在 <see cref="Arguments"/> 中）。</summary>
    public string JavaPath { get; init; } = "java";

    /// <summary>完整参数列表（已按 <c>ProcessStartInfo.ArgumentList</c> 语义切分，不含引号）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>主类，便于日志与排障。</summary>
    public string MainClass { get; init; } = string.Empty;

    /// <summary>工作目录（版本隔离时为 <c>versions/&lt;id&gt;</c>）。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>拼好的 classpath 字符串。</summary>
    public string Classpath { get; init; } = string.Empty;

    /// <summary>构建过程中的警告（未定义变量等）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>用于日志展示的可读命令行（带引号，<b>不要</b>直接交给进程）。</summary>
    public string ToCommandLine()
    {
        var builder = new StringBuilder(Quote(JavaPath));

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();

        static string Quote(string value) =>
            value.Contains(' ') || value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }
}
