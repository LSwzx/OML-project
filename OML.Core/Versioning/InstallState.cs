using System.IO;

namespace OML.Core.Versioning;

/// <summary>
/// 安装状态标记。
/// <para>
/// 安装/下载到一半就退出（取消、崩溃、断电）会留下"半个版本"：
/// JSON 在、jar 缺、natives 缺，启动时报一堆看不懂的错。
/// 这里用 <c>versions/&lt;id&gt;/.installing</c> 标记"未完成的安装"，
/// 下次启动或重装时先清理启动器管理的产物，再重新装。
/// </para>
/// <para>
/// 清理刻意<b>不碰</b> mods/saves/config/资源包——版本隔离下这些目录就在版本目录里，
/// 整目录删除会连带删掉用户存档。
/// </para>
/// </summary>
public static class InstallState
{
    /// <summary>标记文件名。</summary>
    public const string MarkerFileName = ".installing";

    public static string GetMarkerPath(GameDirectory gameDirectory, string versionId) =>
        Path.Combine(gameDirectory.GetVersionDirectory(versionId), MarkerFileName);

    /// <summary>标记该版本正在安装。</summary>
    public static void Begin(GameDirectory gameDirectory, string versionId)
    {
        var marker = GetMarkerPath(gameDirectory, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
    }

    public static bool IsInstalling(GameDirectory gameDirectory, string versionId) =>
        File.Exists(GetMarkerPath(gameDirectory, versionId));

    /// <summary>安装成功后清除标记。</summary>
    public static void Complete(GameDirectory gameDirectory, string versionId) => TryDelete(GetMarkerPath(gameDirectory, versionId));

    /// <summary>
    /// 清理半成品安装，只删启动器自己写的东西。
    /// </summary>
    public static void CleanupPartial(GameDirectory gameDirectory, string versionId)
    {
        var versionDirectory = gameDirectory.GetVersionDirectory(versionId);

        TryDelete(Path.Combine(versionDirectory, versionId + ".json"));
        TryDelete(Path.Combine(versionDirectory, versionId + ".jar"));
        TryDeleteDirectory(Path.Combine(versionDirectory, "natives"));
        TryDelete(GetMarkerPath(gameDirectory, versionId));
    }

    /// <summary>扫描并清理所有带标记的版本，返回清理数量。</summary>
    public static int CleanupAll(GameDirectory gameDirectory)
    {
        if (!Directory.Exists(gameDirectory.VersionsDirectory))
        {
            return 0;
        }

        var cleaned = 0;

        foreach (var directory in Directory.EnumerateDirectories(gameDirectory.VersionsDirectory))
        {
            if (!File.Exists(Path.Combine(directory, MarkerFileName)))
            {
                continue;
            }

            CleanupPartial(gameDirectory, Path.GetFileName(directory));
            cleaned++;
        }

        return cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 被占用就留给下一次。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 同上。
        }
    }
}
