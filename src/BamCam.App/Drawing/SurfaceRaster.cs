using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>
/// A large mesh drawn into a bitmap of its own. As vector lines, a repaint of a million triangles takes several seconds
/// and most of them are smaller than a pixel. The bitmap is only redrawn when the view or the colour changes, so that
/// everything else that repaints (the mouse with object snap, tools) just shows it again.
/// </summary>
public sealed class SurfaceRaster
{
    private readonly record struct Key(SurfaceEntity Surface, int Width, int Height, double Scale, Point Pan, double Yaw, double Pitch, Matrix4 Transform, double Scaling, int Colour);

    private static readonly ConditionalWeakTable<List<int>, int[]> EdgeCache = [];

    private Key? _key;
    private int[] _pixels = [];
    private float[] _xs = [];
    private float[] _ys = [];
    private WriteableBitmap? _bitmap;
    private bool _uploaded;

    /// <summary>Each edge once, as pairs of vertex indices: a shared edge of two triangles is not drawn twice.</summary>
    public static int[] UniqueEdges(IReadOnlyList<int> faces)
    {
        var triangles = faces.Count / 3;
        var keys = new long[triangles * 3];
        for (var t = 0; t < triangles; t++)
        {
            int a = faces[3 * t], b = faces[3 * t + 1], c = faces[3 * t + 2];
            keys[3 * t] = EdgeKey(a, b);
            keys[3 * t + 1] = EdgeKey(b, c);
            keys[3 * t + 2] = EdgeKey(c, a);
        }
        Array.Sort(keys);
        var edges = new List<int>(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0 && keys[i] == keys[i - 1]) continue;
            edges.Add((int)(keys[i] >> 32));
            edges.Add((int)(keys[i] & 0xFFFFFFFF));
        }
        return edges.ToArray();
    }

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static int[] EdgesOf(SurfaceEntity surface)
    {
        if (EdgeCache.TryGetValue(surface.Faces, out var edges)) return edges;
        edges = UniqueEdges(surface.Faces);
        EdgeCache.AddOrUpdate(surface.Faces, edges);
        return edges;
    }

    /// <summary>The colour as a premultiplied BGRA pixel.</summary>
    public static int Pixel(Color c)
    {
        int Mul(byte v) => v * c.A / 255;
        return (c.A << 24) | (Mul(c.R) << 16) | (Mul(c.G) << 8) | Mul(c.B);
    }

    /// <summary>Draws all edges into <paramref name="pixels"/> (width x height, cleared first); what lies outside is clipped.</summary>
    public static void Rasterize(IReadOnlyList<Point3> vertices, Matrix4 transform, int[] edges, ViewTransform view, double scaling,
        int width, int height, int colour, int[] pixels, float[]? xs = null, float[]? ys = null)
    {
        var count = vertices.Count;
        xs ??= new float[count];
        ys ??= new float[count];
        var identity = transform.IsIdentity;
        Parallel.ForEach(Partitioner.Create(0, count), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var p = identity ? vertices[i] : transform.Transform(vertices[i]);
                var s = view.ToScreen(p);
                xs[i] = (float)(s.X * scaling);
                ys[i] = (float)(s.Y * scaling);
            }
        });
        Array.Clear(pixels);
        Parallel.ForEach(Partitioner.Create(0, edges.Length / 2), range =>
        {
            for (var e = range.Item1; e < range.Item2; e++)
            {
                int a = edges[2 * e], b = edges[2 * e + 1];
                DrawLine(pixels, width, height, colour, xs[a], ys[a], xs[b], ys[b]);
            }
        });
    }

    private static void DrawLine(int[] pixels, int width, int height, int colour, float x0, float y0, float x1, float y1)
    {
        // Both ends in one pixel: the usual case for a large mesh in a small view.
        int ix0 = (int)MathF.Floor(x0), iy0 = (int)MathF.Floor(y0);
        if (ix0 == (int)MathF.Floor(x1) && iy0 == (int)MathF.Floor(y1))
        {
            if (ix0 >= 0 && iy0 >= 0 && ix0 < width && iy0 < height) pixels[iy0 * width + ix0] = colour;
            return;
        }
        // Clipped to the bitmap (Liang-Barsky).
        float dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
        if (!Clip(-dx, x0, ref t0, ref t1) || !Clip(dx, width - x0, ref t0, ref t1) ||
            !Clip(-dy, y0, ref t0, ref t1) || !Clip(dy, height - y0, ref t0, ref t1)) return;
        float sx = x0 + dx * t0, sy = y0 + dy * t0, ex = x0 + dx * t1, ey = y0 + dy * t1;
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(ex - sx), MathF.Abs(ey - sy)));
        if (steps <= 0) steps = 1;
        float stepX = (ex - sx) / steps, stepY = (ey - sy) / steps;
        for (var i = 0; i <= steps; i++)
        {
            int x = (int)MathF.Floor(sx + stepX * i), y = (int)MathF.Floor(sy + stepY * i);
            if (x >= 0 && y >= 0 && x < width && y < height) pixels[y * width + x] = colour;
        }
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    /// <summary>Distance in pixels from a position to the nearest drawn pixel within <paramref name="radius"/>, or infinity.</summary>
    public static double NearestDrawn(int[] pixels, int width, int height, double x, double y, int radius)
    {
        var best = double.PositiveInfinity;
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        for (var py = Math.Max(cy - radius, 0); py <= Math.Min(cy + radius, height - 1); py++)
        for (var px = Math.Max(cx - radius, 0); px <= Math.Min(cx + radius, width - 1); px++)
        {
            if (pixels[py * width + px] == 0) continue;
            double dx = px - cx, dy = py - cy;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    private void Update(SurfaceEntity surface, ViewTransform view, Size size, double scaling, int colour)
    {
        var width = Math.Max((int)Math.Ceiling(size.Width * scaling), 1);
        var height = Math.Max((int)Math.Ceiling(size.Height * scaling), 1);
        var key = new Key(surface, width, height, view.Scale, view.Pan, view.Yaw, view.Pitch, surface.Transform, scaling, colour);
        if (_key == key) return;
        if (_pixels.Length != width * height) _pixels = new int[width * height];
        if (_xs.Length != surface.Vertices.Count)
        {
            _xs = new float[surface.Vertices.Count];
            _ys = new float[surface.Vertices.Count];
        }
        Rasterize(surface.Vertices, surface.Transform, EdgesOf(surface), view, scaling, width, height, colour, _pixels, _xs, _ys);
        _key = key;
        _uploaded = false;
    }

    /// <summary>Shows the mesh; it is only drawn again when something it depends on changed.</summary>
    public void Draw(DrawingContext dc, SurfaceEntity surface, ViewTransform view, Size size, double scaling, Color colour)
    {
        Update(surface, view, size, scaling, Pixel(colour));
        var (width, height) = (_key!.Value.Width, _key.Value.Height);
        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            // 96 dpi whatever the display: the bitmap's size then equals its pixels, which is what DrawImage reads as the
            // source area. With the display's dpi it would show only a part of the bitmap, enlarged.
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _uploaded = false;
        }
        if (!_uploaded)
        {
            using var frame = _bitmap.Lock();
            if (frame.RowBytes == width * 4)
            {
                Marshal.Copy(_pixels, 0, frame.Address, width * height);
            }
            else
            {
                for (var y = 0; y < height; y++) Marshal.Copy(_pixels, y * width, frame.Address + y * frame.RowBytes, width);
            }
            _uploaded = true;
        }
        dc.DrawImage(_bitmap, new Rect(0, 0, width / scaling, height / scaling));
    }

    /// <summary>Distance in view units from a screen position to the drawn mesh, within the tolerance; otherwise infinity.</summary>
    public double DistanceTo(SurfaceEntity surface, ViewTransform view, Size size, double scaling, Point screen, double tolerance)
    {
        // A hit test before the first repaint, or after a change of the view without one.
        if (_key is not { } key || key.Surface != surface) Update(surface, view, size, scaling, Pixel(Colors.White));
        else Update(surface, view, size, scaling, key.Colour);
        var radius = (int)Math.Ceiling(tolerance * scaling);
        return NearestDrawn(_pixels, _key!.Value.Width, _key.Value.Height, screen.X * scaling, screen.Y * scaling, radius) / scaling;
    }
}
