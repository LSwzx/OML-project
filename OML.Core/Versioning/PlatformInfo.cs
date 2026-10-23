using System.Runtime.InteropServices;

namespace OML.Core.Versioning;

/// <summary>
/// 运行平台描述，使用 Mojang 的命名口径（<c>windows</c> / <c>osx</c> / <c>linux</c>，
/// <c>x86</c> / <c>x86_64</c> / <c>arm64</c>）。
/// </summary>
public sealed record PlatformInfo(string OsName, string Arch, string OsVersion)
{
    /// <summary>当前机器。</summary>
    public static PlatformInfo Current { get; } = CreateCurrent();

    /// <summary>按当前进程所在地解析平台。</summary>
    public static PlatformInfo CreateCurrent()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "unknown";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return new PlatformInfo(os, arch, Environment.OSVersion.Version.ToString());
    }

    /// <summary>把各种写法的架构名归一，无法识别时原样返回小写。</summary>
    public static string NormalizeArch(string arch) => arch.Trim().ToLowerInvariant() switch
    {
        "amd64" or "x64" or "x86_64" or "x86-64" => "x86_64",
        "aarch64" or "arm64" => "arm64",
        "x86" or "i386" or "i686" or "x86_32" => "x86",
        "arm" or "arm32" => "arm",
        var other => other,
    };
}
