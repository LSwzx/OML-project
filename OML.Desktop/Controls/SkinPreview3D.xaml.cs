using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace OML.Desktop.Controls;

/// <summary>
/// 2D 皮肤贴图渲染成 3D 小人。
/// <para>
/// 用 6 个长方体（头 8×8×8、身 8×12×4、四肢 4×12×4）拼出 Steve 模型，
/// 每个面按 Minecraft 皮肤的 UV 区域取贴图。64×64 用新版左右肢区域，
/// 64×32 旧皮肤没有左肢区域，直接镜像右肢。
/// </para>
/// </summary>
public partial class SkinPreview3D : UserControl
{
    private const string DefaultSkinUri = "pack://application:,,,/Assets/steve_skin.png";

    private AxisAngleRotation3D? _yaw;
    private AxisAngleRotation3D? _pitch;
    private Point _dragStart;
    private bool _dragging;

    public SkinPreview3D()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath),
        typeof(string),
        typeof(SkinPreview3D),
        new PropertyMetadata(null, OnSkinPathChanged));

    /// <summary>皮肤 PNG 的本地路径；为空/读取失败时使用内置 Steve 皮肤。</summary>
    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    private static void OnSkinPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkinPreview3D preview && preview.IsLoaded)
        {
            preview.Build();
        }
    }

    private void Build()
    {
        var bitmap = LoadBitmap(SkinPath);

        var group = new Transform3DGroup();
        _yaw = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 18);
        _pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        group.Children.Add(new RotateTransform3D(_pitch, 0, 18, 0));
        group.Children.Add(new RotateTransform3D(_yaw, 0, 18, 0));
        group.Children.Add(new ScaleTransform3D(1.35, 1.35, 1.35));

        // 皮肤像素放大 8 倍（最近邻）：WPF 3D 的纹理过滤固定为线性，
        // 原尺寸采样时相邻像素/相邻部件会互相渗色，边缘发灰甚至采到透明像素
        // （看起来像玻璃）。放大后再配合 UV 半像素内缩，渗色基本不可见。
        var texture = Upscale(bitmap, 8);

        var material = new DiffuseMaterial(new ImageBrush(texture)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
        });

        var geometry = BuildGeometry(bitmap.PixelWidth, bitmap.PixelHeight);
        var model = new GeometryModel3D(geometry, material)
        {
            Transform = group,
        };

        var visual = new ModelVisual3D();
        visual.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(0x9A, 0x9A, 0x9A)) });
        visual.Children.Add(new ModelVisual3D
        {
            Content = new DirectionalLight(Color.FromRgb(0xF0, 0xF0, 0xF0), new Vector3D(-0.4, -0.7, -1)),
        });
        visual.Children.Add(new ModelVisual3D { Content = model });

        ModelRoot.Children.Clear();
        ModelRoot.Children.Add(visual);
    }

    private static BitmapImage LoadBitmap(string? path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;

        var source = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? new Uri(path, UriKind.Absolute)
            : new Uri(DefaultSkinUri, UriKind.Absolute);

        try
        {
            image.UriSource = source;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            var fallback = new BitmapImage();
            fallback.BeginInit();
            fallback.CacheOption = BitmapCacheOption.OnLoad;
            fallback.UriSource = new Uri(DefaultSkinUri, UriKind.Absolute);
            fallback.EndInit();
            fallback.Freeze();
            return fallback;
        }
    }

    /// <summary>最近邻放大，保持像素硬边缘（不让插值把相邻像素混在一起）。</summary>
    private static BitmapSource Upscale(BitmapSource source, int factor)
    {
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
        {
            src = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var width = src.PixelWidth;
        var height = src.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        src.CopyPixels(pixels, stride, 0);

        var newWidth = width * factor;
        var newHeight = height * factor;
        var newStride = newWidth * 4;
        var scaled = new byte[newStride * newHeight];

        for (var y = 0; y < newHeight; y++)
        {
            var sourceRow = (y / factor) * stride;
            var targetRow = y * newStride;

            for (var x = 0; x < newWidth; x++)
            {
                Buffer.BlockCopy(pixels, sourceRow + (x / factor) * 4, scaled, targetRow + x * 4, 4);
            }
        }

        var result = BitmapSource.Create(
            newWidth,
            newHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            scaled,
            newStride);
        result.Freeze();
        return result;
    }

    // ---------------------------------------------------------------- 几何

    private static MeshGeometry3D BuildGeometry(int width, int height)
    {
        var mesh = new MeshGeometry3D();
        var legacy = height < 64;

        AddBox(mesh, width, height,
            new Point3D(-4, 24, -4), new Point3D(4, 32, 4),
            front: new Rect(8, 8, 8, 8), back: new Rect(24, 8, 8, 8),
            right: new Rect(0, 8, 8, 8), left: new Rect(16, 8, 8, 8),
            top: new Rect(8, 0, 8, 8), bottom: new Rect(16, 0, 8, 8));

        AddBox(mesh, width, height,
            new Point3D(-4, 12, -2), new Point3D(4, 24, 2),
            front: new Rect(20, 20, 8, 12), back: new Rect(32, 20, 8, 12),
            right: new Rect(16, 20, 4, 12), left: new Rect(28, 20, 4, 12),
            top: new Rect(20, 16, 8, 4), bottom: new Rect(28, 16, 8, 4));

        AddBox(mesh, width, height,
            new Point3D(4, 12, -2), new Point3D(8, 24, 2),
            front: new Rect(44, 20, 4, 12), back: new Rect(52, 20, 4, 12),
            right: new Rect(40, 20, 4, 12), left: new Rect(48, 20, 4, 12),
            top: new Rect(44, 16, 4, 4), bottom: new Rect(48, 16, 4, 4));

        var leftArmFront = legacy ? new Rect(44, 20, 4, 12) : new Rect(36, 52, 4, 12);
        var leftArmBack = legacy ? new Rect(52, 20, 4, 12) : new Rect(44, 52, 4, 12);
        var leftArmRight = legacy ? new Rect(40, 20, 4, 12) : new Rect(32, 52, 4, 12);
        var leftArmLeft = legacy ? new Rect(48, 20, 4, 12) : new Rect(40, 52, 4, 12);
        var leftArmTop = legacy ? new Rect(44, 16, 4, 4) : new Rect(36, 48, 4, 4);
        var leftArmBottom = legacy ? new Rect(48, 16, 4, 4) : new Rect(40, 48, 4, 4);

        AddBox(mesh, width, height,
            new Point3D(-8, 12, -2), new Point3D(-4, 24, 2),
            leftArmFront, leftArmBack, leftArmRight, leftArmLeft, leftArmTop, leftArmBottom);

        AddBox(mesh, width, height,
            new Point3D(0, 0, -2), new Point3D(4, 12, 2),
            front: new Rect(4, 20, 4, 12), back: new Rect(12, 20, 4, 12),
            right: new Rect(0, 20, 4, 12), left: new Rect(8, 20, 4, 12),
            top: new Rect(4, 16, 4, 4), bottom: new Rect(8, 16, 4, 4));

        var leftLegFront = legacy ? new Rect(4, 20, 4, 12) : new Rect(20, 52, 4, 12);
        var leftLegBack = legacy ? new Rect(12, 20, 4, 12) : new Rect(28, 52, 4, 12);
        var leftLegRight = legacy ? new Rect(0, 20, 4, 12) : new Rect(16, 52, 4, 12);
        var leftLegLeft = legacy ? new Rect(8, 20, 4, 12) : new Rect(24, 52, 4, 12);
        var leftLegTop = legacy ? new Rect(4, 16, 4, 4) : new Rect(20, 48, 4, 4);
        var leftLegBottom = legacy ? new Rect(8, 16, 4, 4) : new Rect(24, 48, 4, 4);

        AddBox(mesh, width, height,
            new Point3D(-4, 0, -2), new Point3D(0, 12, 2),
            leftLegFront, leftLegBack, leftLegRight, leftLegLeft, leftLegTop, leftLegBottom);

        return mesh;
    }

    private static void AddBox(
        MeshGeometry3D mesh,
        int skinWidth,
        int skinHeight,
        Point3D min,
        Point3D max,
        Rect front,
        Rect back,
        Rect right,
        Rect left,
        Rect top,
        Rect bottom)
    {
        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(min.X, max.Y, max.Z),
            new Point3D(min.X, min.Y, max.Z),
            new Point3D(max.X, min.Y, max.Z),
            new Point3D(max.X, max.Y, max.Z), front);

        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(max.X, max.Y, min.Z),
            new Point3D(max.X, min.Y, min.Z),
            new Point3D(min.X, min.Y, min.Z),
            new Point3D(min.X, max.Y, min.Z), back);

        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(max.X, max.Y, max.Z),
            new Point3D(max.X, min.Y, max.Z),
            new Point3D(max.X, min.Y, min.Z),
            new Point3D(max.X, max.Y, min.Z), right);

        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(min.X, max.Y, min.Z),
            new Point3D(min.X, min.Y, min.Z),
            new Point3D(min.X, min.Y, max.Z),
            new Point3D(min.X, max.Y, max.Z), left);

        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(min.X, max.Y, min.Z),
            new Point3D(min.X, max.Y, max.Z),
            new Point3D(max.X, max.Y, max.Z),
            new Point3D(max.X, max.Y, min.Z), top);

        AddQuad(mesh, skinWidth, skinHeight,
            new Point3D(min.X, min.Y, max.Z),
            new Point3D(min.X, min.Y, min.Z),
            new Point3D(max.X, min.Y, min.Z),
            new Point3D(max.X, min.Y, max.Z), bottom);
    }

    private static void AddQuad(
        MeshGeometry3D mesh,
        int skinWidth,
        int skinHeight,
        Point3D topLeft,
        Point3D bottomLeft,
        Point3D bottomRight,
        Point3D topRight,
        Rect region)
    {
        var index = mesh.Positions.Count;

        // Minecraft 皮肤的 UV 表是按 64×64 布局定义的（像素坐标）。
        // 高清皮肤（128/256）是同一布局的整数倍放大，所以归一化时除数固定是 64，
        // 而不是图片实际宽度——用 /skinWidth 会让所有区域偏移一半/四分之一。
        // 64×32 旧皮肤只有上半张，归一化 V 的除数是 32。
        var legacy = skinHeight <= 32;
        var uBase = 64d;
        var vBase = legacy ? 32d : 64d;

        // 采样内缩半个像素：WPF 3D 的纹理过滤是线性的，
        // 不内缩时面的边缘会采到区域外（相邻部件，甚至透明 overlay），
        // 表现为边缘发灰、发黑、半透明（看起来像玻璃）。
        const double InsetPixels = 0.5;

        // 纹理已经被放大 8 倍，半像素内缩也按放大后的尺寸算。
        var texWidth = skinWidth * 8d;
        var texHeight = skinHeight * 8d;

        var u0 = region.X / uBase + InsetPixels / texWidth;
        var u1 = (region.X + region.Width) / uBase - InsetPixels / texWidth;
        var v0 = region.Y / vBase + InsetPixels / texHeight;
        var v1 = (region.Y + region.Height) / vBase - InsetPixels / texHeight;

        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(bottomLeft);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(topRight);

        // V 轴原点在左上（与像素坐标一致），不需要翻转。
        mesh.TextureCoordinates.Add(new Point(u0, v0));
        mesh.TextureCoordinates.Add(new Point(u0, v1));
        mesh.TextureCoordinates.Add(new Point(u1, v1));
        mesh.TextureCoordinates.Add(new Point(u1, v0));

        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 1);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index + 3);
    }

    // ---------------------------------------------------------------- 交互

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _yaw is null || _pitch is null)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        _dragStart = current;

        _yaw.Angle = (_yaw.Angle + dx * 0.6) % 360;
        _pitch.Angle = Math.Clamp(_pitch.Angle - dy * 0.3, -40, 40);
    }
}
