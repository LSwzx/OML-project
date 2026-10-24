using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OML.Desktop.Infrastructure;

/// <summary>
/// 2D 皮肤头像加载。
/// <para>
/// 只取头部正面 8×8 区域（<c>8,8</c> 起点）。64×64（现版）与 64×32（旧版）里
/// 头部正面的坐标是相同的，所以不需要按格式分支。取不到就回退到内置 Steve 资源。
/// </para>
/// </summary>
public static class SkinImageLoader
{
    private static readonly Lazy<ImageSource> Steve = new(LoadSteve, isThreadSafe: true);

    /// <summary>内置 Steve 头像（离线账号/无皮肤/下载失败时使用）。</summary>
    public static ImageSource SteveHead => Steve.Value;

    public static ImageSource LoadHead(string? skinPath)
    {
        if (string.IsNullOrWhiteSpace(skinPath) || !File.Exists(skinPath))
        {
            return SteveHead;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(skinPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16)
            {
                return SteveHead;
            }

            var head = new CroppedBitmap(bitmap, new Int32Rect(8, 8, 8, 8));
            head.Freeze();
            return head;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return SteveHead;
        }
    }

    private static ImageSource LoadSteve()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // 作为 Resource 编进程序集：单文件发布后依然可读。
        image.UriSource = new Uri("pack://application:,,,/Assets/steve_head.png", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
