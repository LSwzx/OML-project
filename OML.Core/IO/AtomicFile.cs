namespace OML.Core.IO;

/// <summary>
/// 原子写文件：先写 <c>.tmp</c> 再替换。
/// <para>崩溃/断电不会留下半截内容把 JSON 缓存或配置永久污染。</para>
/// </summary>
internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
