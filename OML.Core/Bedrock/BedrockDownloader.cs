using OML.Abstractions.Downloads;
using OML.Core.Downloads;

namespace OML.Core.Bedrock;

/// <summary>把解析出的 Store 安装包下载到本地缓存。</summary>
public sealed class BedrockDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDownloadEngine? _engine;

    public BedrockDownloader(HttpClient? httpClient = null, IDownloadEngine? engine = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        _engine = engine;
    }

    /// <summary>
    /// 依次下载主包与依赖，返回文件与本地路径。
    /// <para>已存在且非空的文件会跳过，便于中断后续传。</para>
    /// </summary>
    public async Task<IReadOnlyList<(BedrockDownloadFile File, string Path)>> DownloadAsync(
        IReadOnlyList<BedrockDownloadFile> files,
        string targetDirectory,
        IProgress<double>? progress = null,
        Action<DownloadLogEntry>? log = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        Directory.CreateDirectory(targetDirectory);

        using var downloader = new PclStyleDownloader(_http)
        {
            Log = log,
            Status = status,
        };

        var results = new List<(BedrockDownloadFile File, string Path)>(files.Count);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];
            var savePath = Path.Combine(targetDirectory, file.Name);
            results.Add((file, savePath));

            if (File.Exists(savePath) && new FileInfo(savePath).Length > 0)
            {
                progress?.Report((index + 1d) / files.Count);
                continue;
            }

            status?.Invoke($"下载基岩版安装包（{index + 1}/{files.Count}）：{file.Name}");

            var baseIndex = index;
            await downloader.DownloadAsync(
                new DownloadTask
                {
                    Url = file.Url,
                    SavePath = savePath,
                },
                new Progress<double>(value =>
                    progress?.Report((baseIndex + value) / files.Count)),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(1d);
        return results;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        (_engine as IDisposable)?.Dispose();
    }
}
