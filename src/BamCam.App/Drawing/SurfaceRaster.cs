using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
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

    private readonly PixelLayer _layer = new();
    private Key? _key;
    private float[] _xs = [];
    private float[] _ys = [];

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
                PixelLayer.DrawLine(pixels, width, height, colour, xs[a], ys[a], xs[b], ys[b]);
            }
        });
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
        _layer.Resize(width, height);
        if (_xs.Length != surface.Vertices.Count)
        {
            _xs = new float[surface.Vertices.Count];
            _ys = new float[surface.Vertices.Count];
        }
        Rasterize(surface.Vertices, surface.Transform, EdgesOf(surface), view, scaling, width, height, colour, _layer.Pixels, _xs, _ys);
        _layer.MarkChanged();
        _key = key;
    }

    /// <summary>Shows the mesh; it is only drawn again when something it depends on changed.</summary>
    public void Draw(DrawingContext dc, SurfaceEntity surface, ViewTransform view, Size size, double scaling, Color colour)
    {
        Update(surface, view, size, scaling, PixelLayer.Pixel(colour));
        _layer.Draw(dc, scaling);
    }

    /// <summary>Distance in view units from a screen position to the drawn mesh, within the tolerance; otherwise infinity.</summary>
    public double DistanceTo(SurfaceEntity surface, ViewTransform view, Size size, double scaling, Point screen, double tolerance)
    {
        // A hit test before the first repaint, or after a change of the view without one.
        if (_key is not { } key || key.Surface != surface) Update(surface, view, size, scaling, PixelLayer.Pixel(Colors.White));
        else Update(surface, view, size, scaling, key.Colour);
        var radius = (int)Math.Ceiling(tolerance * scaling);
        return NearestDrawn(_layer.Pixels, _layer.Width, _layer.Height, screen.X * scaling, screen.Y * scaling, radius) / scaling;
    }
}
