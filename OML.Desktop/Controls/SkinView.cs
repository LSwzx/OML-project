// Source: https://github.com/mokkignomov-design/WPF-Minecraft-Skin-Viewer
// File: TestSolution/SkinView.cs
// License: MIT (see THIRD_PARTY_NOTICES.md)
//
// 仅做了两处改动：
//   1. 命名空间改为 OML.Desktop.Controls
//   2. 纹理采样显式设置 NearestNeighbor，避免缩放时糊边

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace OML.Desktop.Controls;

public class SkinView : Control
{
    private Viewport3D _viewport;
    private Model3DGroup _modelGroup;
    private QuaternionRotation3D _yRotation;
    private QuaternionRotation3D _xRotation;

    private DispatcherTimer _autoRotateTimer;
    private DateTime _lastMoveTime;
    private double _currentAngleX = 0;
    private double _currentAngleY = 0;

    public static readonly DependencyProperty SkinSourceProperty = DependencyProperty.Register(
        nameof(SkinSource), typeof(BitmapSource), typeof(SkinView),
        new PropertyMetadata(null, (d, e) => ((SkinView)d).UpdateModel()));

    public BitmapSource SkinSource { get => (BitmapSource)GetValue(SkinSourceProperty); set => SetValue(SkinSourceProperty, value); }

    public SkinView()
    {
        _viewport = new Viewport3D();
        RenderOptions.SetBitmapScalingMode(_viewport, BitmapScalingMode.NearestNeighbor);

        _modelGroup = new Model3DGroup();
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, 0, 50), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45);

        var modelVisual = new ModelVisual3D { Content = _modelGroup };
        var transformGroup = new Transform3DGroup();
        _yRotation = new QuaternionRotation3D();
        _xRotation = new QuaternionRotation3D();
        transformGroup.Children.Add(new RotateTransform3D(_yRotation));
        transformGroup.Children.Add(new RotateTransform3D(_xRotation));
        modelVisual.Transform = transformGroup;

        _viewport.Children.Add(modelVisual);
        this.AddVisualChild(_viewport);

        _autoRotateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoRotateTimer.Tick += (s, e) => {
            if (IsMouseCaptured) return;            if ((DateTime.Now - _lastMoveTime).TotalSeconds > 4)
            {
                _currentAngleY += 0.4;
                UpdateRotation();
            }
        };
        _autoRotateTimer.Start();

        // 导航离开页面时停掉 16ms 定时器，避免控件被计时器长期引用（也省 CPU）。
        Loaded += (_, _) => _autoRotateTimer.Start();
        Unloaded += (_, _) => _autoRotateTimer.Stop();

        Point lastPos = new Point();
        MouseDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) { lastPos = e.GetPosition(this); CaptureMouse(); } };
        MouseMove += (s, e) => {
            if (IsMouseCaptured)
            {
                var pos = e.GetPosition(this);
                _currentAngleY += pos.X - lastPos.X;
                _currentAngleX = Math.Clamp(_currentAngleX + (pos.Y - lastPos.Y), -70, 70);

                UpdateRotation();
                lastPos = pos;
                _lastMoveTime = DateTime.Now;
            }
        };
        MouseUp += (s, e) => ReleaseMouseCapture();
    }

    private void UpdateRotation()
    {
        _yRotation.Quaternion = new Quaternion(new Vector3D(0, 1, 0), _currentAngleY);
        _xRotation.Quaternion = new Quaternion(new Vector3D(1, 0, 0), _currentAngleX);
    }

    private void UpdateModel()
    {
        if (SkinSource == null) return;
        _modelGroup.Children.Clear();
        _modelGroup.Children.Add(new AmbientLight(Colors.White));

        var img = new Image { Source = SkinSource };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        var brush = new VisualBrush(img) { ViewportUnits = BrushMappingMode.Absolute };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        var material = new DiffuseMaterial(brush);

        var baseMesh = new MeshGeometry3D();
        var overlayMesh = new MeshGeometry3D();

        bool isSlim = false;
        if (SkinSource.PixelWidth >= 64)
        {
            try
            {
                var cb = new CroppedBitmap(SkinSource, new Int32Rect(42, 48, 1, 1));
                byte[] pixel = new byte[4];
                cb.CopyPixels(pixel, 4, 0);
                if (pixel[3] < 128) isSlim = true;
            }
            catch { }
        }

        void AddPart(double x, double y, double z, double w, double h, double d, int u, int v, double inf, bool isOverlay, bool isArm = false, bool isLeft = false)
        {
            double actualW = (isArm && isSlim) ? 3 : w;
            double renderX = x + (isArm && isSlim ? (isLeft ? 0.5 : -0.5) : 0);
            AddBoxToMesh(isOverlay ? overlayMesh : baseMesh, renderX, y, z, actualW, h, d, u, v, inf);
        }

        // 头
        AddPart(0, 10, 0, 8, 8, 8, 8, 8, 1.0, false);
        AddPart(0, 10, 0, 8, 8, 8, 40, 8, 1.12, true);
        // 身体
        AddPart(0, 0, 0, 8, 12, 4, 20, 20, 1.0, false);
        AddPart(0, 0, 0, 8, 12, 4, 20, 36, 1.06, true);

        if (SkinSource.PixelHeight == 64)
        {
            // 手臂
            AddPart(-6, 0, 0, 4, 12, 4, 44, 20, 1.0, false, true, true);
            AddPart(-6, 0, 0, 4, 12, 4, 44, 36, 1.08, true, true, true);
            AddPart(6, 0, 0, 4, 12, 4, 36, 52, 1.0, false, true, false);
            AddPart(6, 0, 0, 4, 12, 4, 52, 52, 1.08, true, true, false);
            // 腿
            AddPart(-2, -12, 0, 4, 12, 4, 4, 20, 1.0, false);
            AddPart(-2, -12, 0, 4, 12, 4, 4, 36, 1.08, true);
            AddPart(2, -12, 0, 4, 12, 4, 20, 52, 1.0, false);
            AddPart(2, -12, 0, 4, 12, 4, 4, 52, 1.08, true);
        }

        _modelGroup.Children.Add(new GeometryModel3D(baseMesh, material));
        _modelGroup.Children.Add(new GeometryModel3D(overlayMesh, material));
    }

    private void AddBoxToMesh(MeshGeometry3D mesh, double x, double y, double z, double w, double h, double d, int u, int v, double inf)
    {
        double hw = w / 2 * inf, hh = h / 2 * inf, hd = d / 2 * inf;
        Point3D[] p = {
            new (x-hw, y+hh, z+hd), new (x+hw, y+hh, z+hd), new (x+hw, y-hh, z+hd), new (x-hw, y-hh, z+hd),
            new (x+hw, y+hh, z-hd), new (x-hw, y+hh, z-hd), new (x-hw, y-hh, z-hd), new (x+hw, y-hh, z-hd)
        };

        double tw = SkinSource.PixelWidth, th = SkinSource.PixelHeight;
        double e = 0.001;

        void AddFace(Point3D p1, Point3D p2, Point3D p3, Point3D p4, double uS, double vS, double uW, double vH, bool flipUV = false)
        {
            int b = mesh.Positions.Count;
            mesh.Positions.Add(p1); mesh.Positions.Add(p2); mesh.Positions.Add(p3); mesh.Positions.Add(p4);
            double u1 = (uS + e) / tw, v1 = (vS + e) / th, u2 = (uS + uW - e) / tw, v2 = (vS + vH - e) / th;
            if (flipUV)
            {
                mesh.TextureCoordinates.Add(new Point(u1, v2)); mesh.TextureCoordinates.Add(new Point(u1, v1));
                mesh.TextureCoordinates.Add(new Point(u2, v1)); mesh.TextureCoordinates.Add(new Point(u2, v2));
            }
            else
            {
                mesh.TextureCoordinates.Add(new Point(u1, v1)); mesh.TextureCoordinates.Add(new Point(u2, v1));
                mesh.TextureCoordinates.Add(new Point(u2, v2)); mesh.TextureCoordinates.Add(new Point(u1, v2));
            }
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 1);
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 3); mesh.TriangleIndices.Add(b + 2);
        }

        AddFace(p[0], p[1], p[2], p[3], u, v, w, h);             // Front
        AddFace(p[1], p[4], p[7], p[2], u + w, v, d, h);         // Right
        AddFace(p[4], p[5], p[6], p[7], u + w + d, v, w, h);     // Back
        AddFace(p[5], p[0], p[3], p[6], u - d, v, d, h);         // Left
        AddFace(p[5], p[4], p[1], p[0], u, v - d, w, d);         // Top
        AddFace(p[2], p[7], p[6], p[3], u + w, v - d, w, d, true); // Bottom
    }

    protected override Visual GetVisualChild(int index) => _viewport;
    protected override int VisualChildrenCount => 1;
    protected override Size ArrangeOverride(Size sz) { _viewport.Arrange(new Rect(sz)); return sz; }
}
