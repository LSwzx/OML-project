using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OML.Desktop.Infrastructure;

/// <summary>
/// 网络图片缓存。
/// <para>
/// Modrinth 的图标是远程 URL。直接用 <c>Image.Source="https://..."</c> 的字符串绑定：
/// 每次滚动都会重新发起请求，几十个卡片就能把列表拖卡。
/// 这里做两层缓存：内存（同一进程内不重复解码）+ 磁盘（跨次启动不重复下载），
/// 并且按显示尺寸 <c>DecodePixelWidth</c> 解码，避免把 512px 的图标全解成原图。
/// </para>
/// </summary>
public static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConcurrentDictionary<string, ImageSource> Memory = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OML", "cache", "images");

    /// <summary>取图（失败返回 <c>null</c>，调用方显示占位即可）。</summary>
    public static async Task<ImageSource?> GetAsync(string? url, int decodeWidth = 64)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Memory.TryGetValue(url, out var cached))
        {
            return cached;
        }

        try
        {
            var cachePath = Path.Combine(CacheDirectory, Hash(url) + ".img");
            byte[] bytes;

            if (File.Exists(cachePath))
            {
                bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
            }

            var image = Decode(bytes, decodeWidth);
            if (image is not null)
            {
                Memory[url] = image;
            }

            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static ImageSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
