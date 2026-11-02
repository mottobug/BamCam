using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BamCam.Core.Cam;
using BamCam.Core.Simulation;
using SkiaSharp;

namespace BamCam.App.Controls;

/// <summary>
/// Shaded 3D view of a <see cref="HeightField"/> (software rendered with Skia: triangles sorted back to front).
/// Left drag rotates, right or middle drag pans, the wheel zooms, a double click resets the view. Material above the
/// target height (rest material) is yellow to red, material cut below it magenta.
/// </summary>
public sealed class SimulationView : Control
{
    private HeightField? _field;
    private double _yaw = -Math.PI / 6, _pitch = Math.PI / 4, _zoom = 1;
    private Vector _pan;
    private Point _dragStart;
    private (double Yaw, double Pitch, Vector Pan) _dragFrom;
    private bool _rotating, _panning;
    private WriteableBitmap? _bitmap;

    public SimulationView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Material left above the target that is shown as rest material.</summary>
    public double Tolerance { get; set; } = 0.05;

    public HeightField? Field
    {
        get => _field;
        set
        {
            var reset = _field == null || value == null || value.Nx != _field.Nx || value.Ny != _field.Ny;
            _field = value;
            if (reset) ResetView();
            InvalidateVisual();
        }
    }

    public void ResetView()
    {
        _yaw = -Math.PI / 6;
        _pitch = Math.PI / 4;
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var p = e.GetCurrentPoint(this);
        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }
        _dragStart = p.Position;
        _dragFrom = (_yaw, _pitch, _pan);
        _rotating = p.Properties.IsLeftButtonPressed;
        _panning = p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var d = e.GetPosition(this) - _dragStart;
        if (_rotating)
        {
            _yaw = _dragFrom.Yaw - d.X * 0.01;
            _pitch = Math.Clamp(_dragFrom.Pitch - d.Y * 0.01, 0, Math.PI / 2);
            InvalidateVisual();
        }
        else if (_panning)
        {
            _pan = _dragFrom.Pan + d;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _rotating = _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.2 : 1 / 1.2), 0.1, 50);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(46, 48, 58)), new Rect(Bounds.Size));
        var w = (int)Bounds.Width;
        var h = (int)Bounds.Height;
        if (_field == null || w < 2 || h < 2) return;
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var buffer = _bitmap.Lock())
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, buffer.Address, buffer.RowBytes);
            surface.Canvas.Clear(new SKColor(46, 48, 58));
            Draw(surface.Canvas, _field, w, h);
        }
        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }

    private void Draw(SKCanvas canvas, HeightField f, int width, int height)
    {
        // Display grid: at most about 300 x 300 nodes; each node shows the lowest cell of its block, so that
        // narrow grooves stay visible.
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(f.Nx, f.Ny) / 300.0));
        var nx = (f.Nx - 1) / step + 1;
        var ny = (f.Ny - 1) / step + 1;
        var z = new float[nx * ny];
        var target = new float[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
            var low = float.MaxValue;
            var tgt = float.NaN;
            for (var bj = j * step; bj < Math.Min(f.Ny, (j + 1) * step); bj++)
            for (var bi = i * step; bi < Math.Min(f.Nx, (i + 1) * step); bi++)
            {
                var k = bj * f.Nx + bi;
                if (f.Z[k] < low) low = f.Z[k];
                if (!float.IsNaN(f.Target[k]) && (float.IsNaN(tgt) || f.Target[k] < tgt)) tgt = f.Target[k];
            }
            z[j * nx + i] = low;
            target[j * nx + i] = tgt;
        }

        // Orthographic camera: yaw about Z, then tilt by pitch (0 = from above).
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw), cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        var cell = f.Cell * step;
        double sizeX = (nx - 1) * cell, sizeY = (ny - 1) * cell, midZ = (f.Top + f.Bottom) / 2;
        var scale = _zoom * 0.8 * Math.Min(width, height) / Math.Max(Math.Max(sizeX, sizeY), 1e-6);
        (float X, float Y, float Depth) Project(double x, double y, double zz)
        {
            x -= sizeX / 2;
            y -= sizeY / 2;
            zz -= midZ;
            var rx = cy * x - sy * y;
            var ry = sy * x + cy * y;
            var up = ry * cp + zz * sp;
            var depth = -ry * sp + zz * cp;
            return ((float)(width / 2 + _pan.X + rx * scale), (float)(height / 2 + _pan.Y - up * scale), (float)depth);
        }
        // Light from the viewer, a little from the upper left (world coordinates of the view axes).
        (double X, double Y, double Z) toViewer = (-sy * sp, -cy * sp, cp), up = (sy * cp, cy * cp, sp), right = (cy, -sy, 0);
        var light = Normalize(0.8 * toViewer.X + 0.4 * up.X - 0.3 * right.X, 0.8 * toViewer.Y + 0.4 * up.Y - 0.3 * right.Y,
            0.8 * toViewer.Z + 0.4 * up.Z - 0.3 * right.Z);

        var tris = new List<(float Depth, SKPoint A, SKPoint B, SKPoint C, SKColor Color)>(nx * ny * 2 + 4 * (nx + ny));
        var px = new (float X, float Y, float Depth)[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
            px[j * nx + i] = Project(i * cell, j * cell, z[j * nx + i]);

        SKColor Shade(double nx_, double ny_, double nz, SKColor baseColor)
        {
            var n = Normalize(nx_, ny_, nz);
            var lambert = 0.35 + 0.65 * Math.Max(0, n.X * light.X + n.Y * light.Y + n.Z * light.Z);
            return new SKColor((byte)(baseColor.Red * lambert), (byte)(baseColor.Green * lambert), (byte)(baseColor.Blue * lambert));
        }

        // Height above the target that all corners of a quad with a target share (walls, which reach the target at
        // their foot, stay material coloured); NaN without target.
        double Deviation(params int[] corners)
        {
            var result = double.NaN;
            foreach (var k in corners)
            {
                if (float.IsNaN(target[k])) continue;
                var e = z[k] - target[k];
                if (double.IsNaN(result) || Math.Abs(e) < Math.Abs(result)) result = e;
            }
            return result;
        }

        for (var j = 0; j + 1 < ny; j++)
        for (var i = 0; i + 1 < nx; i++)
        {
            int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
            var dzdx = (z[b] + z[d] - z[a] - z[c]) / (2 * cell);
            var dzdy = (z[c] + z[d] - z[a] - z[b]) / (2 * cell);
            var color = Shade(-dzdx, -dzdy, 1, ColorOf(Deviation(a, b, c, d), f));
            var depth = (px[a].Depth + px[b].Depth + px[c].Depth + px[d].Depth) / 4;
            tris.Add((depth, P(px[a]), P(px[b]), P(px[d]), color));
            tris.Add((depth, P(px[a]), P(px[d]), P(px[c]), color));
        }
        // Sides of the stock block down to the bottom.
        var sideColor = new SKColor(150, 140, 120);
        void Side(int from, int to, double x0, double y0, double x1, double y1, double nxs, double nys)
        {
            var bottom0 = Project(x0, y0, f.Bottom);
            var bottom1 = Project(x1, y1, f.Bottom);
            var color = Shade(nxs, nys, 0, sideColor);
            var depth = (px[from].Depth + px[to].Depth + bottom0.Depth + bottom1.Depth) / 4;
            tris.Add((depth, P(px[from]), P(px[to]), P(bottom1), color));
            tris.Add((depth, P(px[from]), P(bottom1), P(bottom0), color));
        }
        for (var i = 0; i + 1 < nx; i++)
        {
            Side(i, i + 1, i * cell, 0, (i + 1) * cell, 0, 0, -1);
            Side((ny - 1) * nx + i, (ny - 1) * nx + i + 1, i * cell, sizeY, (i + 1) * cell, sizeY, 0, 1);
        }
        for (var j = 0; j + 1 < ny; j++)
        {
            Side(j * nx, (j + 1) * nx, 0, j * cell, 0, (j + 1) * cell, -1, 0);
            Side(j * nx + nx - 1, (j + 1) * nx + nx - 1, sizeX, j * cell, sizeX, (j + 1) * cell, 1, 0);
        }

        tris.Sort((p, q) => p.Depth.CompareTo(q.Depth)); // far first
        var positions = new SKPoint[tris.Count * 3];
        var colors = new SKColor[tris.Count * 3];
        for (var t = 0; t < tris.Count; t++)
        {
            positions[3 * t] = tris[t].A;
            positions[3 * t + 1] = tris[t].B;
            positions[3 * t + 2] = tris[t].C;
            colors[3 * t] = colors[3 * t + 1] = colors[3 * t + 2] = tris[t].Color;
        }
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawVertices(SKVertexMode.Triangles, positions, null, colors, paint);
    }

    private static SKPoint P((float X, float Y, float Depth) p) => new(p.X, p.Y);

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        return l < 1e-12 ? (0, 0, 1) : (x / l, y / l, z / l);
    }

    /// <summary>Material colour, or the deviation from the target: rest material yellow to red, cut too deep magenta.</summary>
    private SKColor ColorOf(double excess, HeightField f)
    {
        var material = new SKColor(205, 200, 190);
        if (double.IsNaN(excess)) return material;
        if (excess > Tolerance)
        {
            var t = Math.Clamp(excess / Math.Max(4 * Tolerance, (f.Top - f.Bottom) / 4), 0, 1);
            return new SKColor(255, (byte)(220 * (1 - t)), 0);
        }
        if (excess < -Tolerance) return new SKColor(220, 0, 220);
        return material;
    }
}
