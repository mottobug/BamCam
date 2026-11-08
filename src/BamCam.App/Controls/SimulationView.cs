using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BamCam.Core.Cam;
using BamCam.Core.Simulation;
using System.Runtime.InteropServices;

namespace BamCam.App.Controls;

/// <summary>
/// Shaded 3D view of a <see cref="HeightField"/> (software rendered with a depth buffer).
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

    /// <summary>Tool tip position and the tool, drawn as its outline with the shape it cuts with (null = none).</summary>
    public (Core.Geometry.Point3 Tip, SimTool Tool)? Tool { get; set; }

    public HeightField? Field
    {
        get => _field;
        set
        {
            var reset = _field == null || value == null || value.Nx != _field.Nx || value.Ny != _field.Ny;
            _field = value;
            if (value != null) _turned = null;
            if (reset) ResetView();
            InvalidateVisual();
        }
    }

    /// <summary>A turned part: triangles in drawing coordinates (nine floats each) inside a box that sets the view.</summary>
    public sealed record TurnedModel(float[] Triangles, Core.Geometry.Point3 Min, Core.Geometry.Point3 Max);

    private TurnedModel? _turned;

    /// <summary>The turned part, shown instead of the height field (null = none).</summary>
    public TurnedModel? Turned
    {
        get => _turned;
        set
        {
            var reset = _turned == null || value == null || _turned.Min != value.Min || _turned.Max != value.Max;
            _turned = value;
            if (value != null) _field = null;
            if (reset) ResetView();
            InvalidateVisual();
        }
    }

    /// <summary>The outline of the turning tool, as lines in drawing coordinates (with <see cref="Turned"/>).</summary>
    public List<(Core.Geometry.Point3 From, Core.Geometry.Point3 To)>? TurnedTool { get; set; }

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
            // A turned part can also be seen from below.
            _pitch = Math.Clamp(_dragFrom.Pitch - d.Y * 0.01, _turned != null ? -Math.PI / 2 : 0, Math.PI / 2);
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
        if ((_field == null && _turned == null) || w < 2 || h < 2) return;
        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        int[] pixels;
        List<(Point, Point)> toolLines;
        try
        {
            (pixels, toolLines) = _turned is { } turned ? DrawTurned(turned, w, h) : Draw(_field!, w, h);
        }
        catch (Exception ex)
        {
            // A drawing error must never close BamCam: show it instead.
            context.DrawText(new FormattedText(ex.Message, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, Brushes.OrangeRed), new Point(10, 10));
            return;
        }
        using (var buffer = _bitmap.Lock())
            for (var y = 0; y < h; y++)
                Marshal.Copy(pixels, y * w, buffer.Address + y * buffer.RowBytes, w);
        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 220, 255)), 1.5);
        foreach (var (a, b) in toolLines) context.DrawLine(pen, a, b);
    }

    private static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    /// <summary>
    /// Renders the height field into BGRA pixels with a depth buffer (own rasteriser: robust for any view, also
    /// with very large or degenerate triangles), and returns the outline of the tool as screen lines.
    /// </summary>
    private (int[] Pixels, List<(Point, Point)> ToolLines) Draw(HeightField f, int width, int height)
    {
        // Display grid: at most about 300 x 300 nodes; each node shows the lowest cell of its block, so that
        // narrow grooves stay visible. The heights may change meanwhile (simulation thread): copied once here.
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
                var v = f.Z[k];
                if (v < low) low = v;
                if (!float.IsNaN(f.Target[k]) && (float.IsNaN(tgt) || f.Target[k] < tgt)) tgt = f.Target[k];
            }
            z[j * nx + i] = float.IsFinite(low) ? low : (float)f.Top;
            target[j * nx + i] = tgt;
        }

        // Orthographic camera: yaw about Z, then tilt by pitch (0 = from above).
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw), cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        var cell = f.Cell * step;
        double sizeX = (nx - 1) * cell, sizeY = (ny - 1) * cell, midZ = (f.Top + f.Bottom) / 2;
        var scale = _zoom * 0.8 * Math.Min(width, height) / Math.Max(Math.Max(sizeX, sizeY), 1e-6);
        (double X, double Y, double Depth) Project(double x, double y, double zz)
        {
            x -= sizeX / 2;
            y -= sizeY / 2;
            zz -= midZ;
            var rx = cy * x - sy * y;
            var ry = sy * x + cy * y;
            var up = ry * cp + zz * sp;
            var depth = -ry * sp + zz * cp;
            return (width / 2.0 + _pan.X + rx * scale, height / 2.0 + _pan.Y - up * scale, depth);
        }
        // Light from the viewer, a little from the upper left (world coordinates of the view axes).
        (double X, double Y, double Z) toViewer = (-sy * sp, -cy * sp, cp), up = (sy * cp, cy * cp, sp), right = (cy, -sy, 0);
        var light = Normalize(0.8 * toViewer.X + 0.4 * up.X - 0.3 * right.X, 0.8 * toViewer.Y + 0.4 * up.Y - 0.3 * right.Y,
            0.8 * toViewer.Z + 0.4 * up.Z - 0.3 * right.Z);

        var pixels = new int[width * height];
        Array.Fill(pixels, Argb(46, 48, 58));
        var depthBuffer = new float[width * height];
        Array.Fill(depthBuffer, float.NegativeInfinity);

        void Triangle((double X, double Y, double Depth) p0, (double X, double Y, double Depth) p1, (double X, double Y, double Depth) p2, int color) =>
            Raster(pixels, depthBuffer, width, height, p0, p1, p2, color);

        var projected = new (double X, double Y, double Depth)[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
            projected[j * nx + i] = Project(i * cell, j * cell, z[j * nx + i]);

        int Shade(double nx_, double ny_, double nz, (int R, int G, int B) baseColor)
        {
            var n = Normalize(nx_, ny_, nz);
            var lambert = 0.35 + 0.65 * Math.Max(0, n.X * light.X + n.Y * light.Y + n.Z * light.Z);
            return Argb((int)(baseColor.R * lambert), (int)(baseColor.G * lambert), (int)(baseColor.B * lambert));
        }

        // Height above the target that all corners of a quad with a target share (walls, which reach the target at
        // their foot, stay material coloured); NaN without target.
        double Deviation(int a, int b, int c, int d)
        {
            var result = double.NaN;
            foreach (var k in (ReadOnlySpan<int>)[a, b, c, d])
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
            Triangle(projected[a], projected[b], projected[d], color);
            Triangle(projected[a], projected[d], projected[c], color);
        }
        // Sides of the stock block down to the bottom.
        void Side(int from, int to, double x0, double y0, double x1, double y1, double nxs, double nys)
        {
            var bottom0 = Project(x0, y0, f.Bottom);
            var bottom1 = Project(x1, y1, f.Bottom);
            var color = Shade(nxs, nys, 0, (150, 140, 120));
            Triangle(projected[from], projected[to], bottom1, color);
            Triangle(projected[from], bottom1, bottom0, color);
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

        var lines = new List<(Point, Point)>();
        if (Tool is { } tool)
        {
            var (tip, simTool) = tool;
            Point Screen(double dx, double dy, double zz)
            {
                var q = Project(tip.X - f.X0 - f.Cell / 2 + dx, tip.Y - f.Y0 - f.Cell / 2 + dy, zz);
                return new Point(q.X, q.Y);
            }
            lines.AddRange(ToolOutline(simTool, Math.Max(4 * simTool.Radius, (f.Top - f.Bottom) * 1.5), (cy, -sy))
                .Select(l => (Screen(l.From.X, l.From.Y, tip.Z + l.From.Z), Screen(l.To.X, l.To.Y, tip.Z + l.To.Z))));
            lines.RemoveAll(l => !double.IsFinite(l.Item1.X + l.Item1.Y + l.Item2.X + l.Item2.Y));
        }
        return (pixels, lines);
    }

    /// <summary>Flat shaded triangle into the pixels; the nearer surface (larger depth) wins.</summary>
    private static void Raster(int[] pixels, float[] depthBuffer, int width, int height,
        (double X, double Y, double Depth) p0, (double X, double Y, double Depth) p1, (double X, double Y, double Depth) p2, int color)
    {
        if (!double.IsFinite(p0.X + p0.Y + p1.X + p1.Y + p2.X + p2.Y + p0.Depth + p1.Depth + p2.Depth)) return;
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
        if (Math.Abs(area) < 1e-12) return;
        var minX = (int)Math.Max(0, Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = (int)Math.Min(width - 1, Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = (int)Math.Max(0, Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = (int)Math.Min(height - 1, Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY) return;
        var inv = 1 / area;
        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5;
                var w0 = ((p1.X - px) * (p2.Y - py) - (p1.Y - py) * (p2.X - px)) * inv;
                var w1 = ((p2.X - px) * (p0.Y - py) - (p2.Y - py) * (p0.X - px)) * inv;
                var w2 = 1 - w0 - w1;
                if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                var d = (float)(w0 * p0.Depth + w1 * p1.Depth + w2 * p2.Depth);
                var idx = y * width + x;
                if (d <= depthBuffer[idx]) continue;
                depthBuffer[idx] = d;
                pixels[idx] = color;
            }
        }
    }

    /// <summary>
    /// Renders a turned part (triangles in drawing coordinates) with the same camera as the height field, and the
    /// outline of the turning tool as screen lines.
    /// </summary>
    private (int[] Pixels, List<(Point, Point)> ToolLines) DrawTurned(TurnedModel model, int width, int height)
    {
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw), cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double midX = (model.Min.X + model.Max.X) / 2, midY = (model.Min.Y + model.Max.Y) / 2, midZ = (model.Min.Z + model.Max.Z) / 2;
        var size = Math.Max(Math.Max(model.Max.X - model.Min.X, model.Max.Y - model.Min.Y), model.Max.Z - model.Min.Z);
        var scale = _zoom * 0.8 * Math.Min(width, height) / Math.Max(size, 1e-6);
        (double X, double Y, double Depth) Project(double x, double y, double zz)
        {
            x -= midX;
            y -= midY;
            zz -= midZ;
            var rx = cy * x - sy * y;
            var ry = sy * x + cy * y;
            var up = ry * cp + zz * sp;
            var depth = -ry * sp + zz * cp;
            return (width / 2.0 + _pan.X + rx * scale, height / 2.0 + _pan.Y - up * scale, depth);
        }
        (double X, double Y, double Z) toViewer = (-sy * sp, -cy * sp, cp), upV = (sy * cp, cy * cp, sp), right = (cy, -sy, 0);
        var light = Normalize(0.8 * toViewer.X + 0.4 * upV.X - 0.3 * right.X, 0.8 * toViewer.Y + 0.4 * upV.Y - 0.3 * right.Y,
            0.8 * toViewer.Z + 0.4 * upV.Z - 0.3 * right.Z);

        var pixels = new int[width * height];
        Array.Fill(pixels, Argb(46, 48, 58));
        var depthBuffer = new float[width * height];
        Array.Fill(depthBuffer, float.NegativeInfinity);

        var t = model.Triangles;
        for (var i = 0; i + 8 < t.Length; i += 9)
        {
            double ax = t[i], ay = t[i + 1], az = t[i + 2], bx = t[i + 3], by = t[i + 4], bz = t[i + 5], qx = t[i + 6], qy = t[i + 7], qz = t[i + 8];
            // Normal, turned towards the viewer (both sides of a triangle are lit alike).
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = qx - ax, vy = qy - ay, vz = qz - az;
            var n = Normalize(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            if (n.X * toViewer.X + n.Y * toViewer.Y + n.Z * toViewer.Z < 0) n = (-n.X, -n.Y, -n.Z);
            var lambert = 0.35 + 0.65 * Math.Max(0, n.X * light.X + n.Y * light.Y + n.Z * light.Z);
            var color = Argb((int)(205 * lambert), (int)(200 * lambert), (int)(190 * lambert));
            Raster(pixels, depthBuffer, width, height, Project(ax, ay, az), Project(bx, by, bz), Project(qx, qy, qz), color);
        }

        var lines = new List<(Point, Point)>();
        if (TurnedTool is { } tool)
            foreach (var (a, b) in tool)
            {
                var pa = Project(a.X, a.Y, a.Z);
                var pb = Project(b.X, b.Y, b.Z);
                if (double.IsFinite(pa.X + pa.Y + pb.X + pb.Y)) lines.Add((new Point(pa.X, pa.Y), new Point(pb.X, pb.Y)));
            }
        return (pixels, lines);
    }

    /// <summary>
    /// Lines of a tool around its tip at (0, 0, 0): its profile across the view (ball, corner radius, cone or flat end),
    /// a circle where the cutting part ends, the sides of the shank and a circle at its top. <paramref name="across"/> is
    /// the direction in XY that runs across the screen.
    /// </summary>
    public static List<(Core.Geometry.Point3 From, Core.Geometry.Point3 To)> ToolOutline(SimTool tool, double length, (double X, double Y) across)
    {
        var lines = new List<(Core.Geometry.Point3, Core.Geometry.Point3)>();
        var r = tool.Radius;
        var shoulder = tool.HeightAt(r); // where the cutting part meets the shank
        var top = Math.Max(length, shoulder + r);
        Core.Geometry.Point3 Across(double d, double z) => new(d * across.X, d * across.Y, z);
        Core.Geometry.Point3 Round(double angle, double z) => new(r * Math.Cos(angle), r * Math.Sin(angle), z);

        // The profile, from one side through the tip to the other.
        const int Steps = 24;
        for (var k = 0; k < 2 * Steps; k++)
        {
            double d0 = r * (k - Steps) / Steps, d1 = r * (k + 1 - Steps) / Steps;
            lines.Add((Across(d0, tool.HeightAt(Math.Abs(d0))), Across(d1, tool.HeightAt(Math.Abs(d1)))));
        }
        for (var k = 0; k < 32; k++)
        {
            double a0 = k * Math.PI / 16, a1 = (k + 1) * Math.PI / 16;
            lines.Add((Round(a0, shoulder), Round(a1, shoulder)));
            lines.Add((Round(a0, top), Round(a1, top)));
            // Rings on the cutting part, so that a ball, a corner radius or a cone shows from every side (the profile
            // alone lies on the shoulder ring when seen from 45 degrees).
            if (shoulder > 1e-9)
                foreach (var part in new[] { 0.35, 0.65, 0.85 })
                {
                    var d = r * part;
                    var z = tool.HeightAt(d);
                    lines.Add((new(d * Math.Cos(a0), d * Math.Sin(a0), z), new(d * Math.Cos(a1), d * Math.Sin(a1), z)));
                }
        }
        foreach (var side in new[] { 1, -1 }) lines.Add((Across(side * r, shoulder), Across(side * r, top)));
        return lines;
    }

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        return l < 1e-12 || !double.IsFinite(l) ? (0, 0, 1) : (x / l, y / l, z / l);
    }

    /// <summary>Material colour, or the deviation from the target: rest material yellow to red, cut too deep magenta.</summary>
    private (int R, int G, int B) ColorOf(double excess, HeightField f)
    {
        if (double.IsNaN(excess)) return (205, 200, 190);
        if (excess > Tolerance)
        {
            var t = Math.Clamp(excess / Math.Max(4 * Tolerance, (f.Top - f.Bottom) / 4), 0, 1);
            return (255, (int)(220 * (1 - t)), 0);
        }
        if (excess < -Tolerance) return (220, 0, 220);
        return (205, 200, 190);
    }
}
