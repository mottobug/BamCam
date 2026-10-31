using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>
/// Geometry from raster images, independent of the image format: <c>gray[x, y]</c> holds the brightness
/// (0 = black, 1 = white) of pixel column x, row y (row 0 at the top of the image).
/// </summary>
public static class ImageGeometry
{
    /// <summary>
    /// Height map: a surface (triangle mesh) whose height follows the brightness. The image covers
    /// <paramref name="width"/> × (width × rows / columns) in XY with its lower left corner at the origin;
    /// white is <paramref name="topZ"/>, black <paramref name="topZ"/> - <paramref name="depth"/> (reversed with
    /// <paramref name="invert"/>). <paramref name="resolution"/> is the sample spacing in drawing units.
    /// </summary>
    public static SurfaceEntity HeightMap(float[,] gray, double width, double depth, double topZ, double resolution, bool invert)
    {
        int w = gray.GetLength(0), h = gray.GetLength(1);
        if (w < 2 || h < 2) throw new ArgumentException("The image is too small.");
        var height = width * h / w;
        var nx = Math.Clamp((int)Math.Round(width / Math.Max(resolution, 1e-6)) + 1, 2, 2000);
        var ny = Math.Clamp((int)Math.Round(height / Math.Max(resolution, 1e-6)) + 1, 2, 2000);
        var s = new SurfaceEntity();
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
            // Bilinear sample at the grid point (image row 0 = top = largest Y).
            var u = (double)i / (nx - 1) * (w - 1);
            var v = (1 - (double)j / (ny - 1)) * (h - 1);
            var g = Sample(gray, u, v);
            if (invert) g = 1 - g;
            s.Vertices.Add(new Point3(width * i / (nx - 1), height * j / (ny - 1), topZ - depth * (1 - g)));
        }
        for (var j = 0; j + 1 < ny; j++)
        for (var i = 0; i + 1 < nx; i++)
        {
            int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
            s.Faces.AddRange([a, b, d, a, d, c]);
        }
        return s;
    }

    private static double Sample(float[,] gray, double u, double v)
    {
        int w = gray.GetLength(0), h = gray.GetLength(1);
        var x0 = Math.Clamp((int)Math.Floor(u), 0, w - 1);
        var y0 = Math.Clamp((int)Math.Floor(v), 0, h - 1);
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var fx = u - x0;
        var fy = v - y0;
        var top = gray[x0, y0] * (1 - fx) + gray[x1, y0] * fx;
        var bottom = gray[x0, y1] * (1 - fx) + gray[x1, y1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>
    /// Outlines of the dark areas (brightness below <paramref name="threshold"/>; the light ones with
    /// <paramref name="invert"/>) as closed paths in drawing units: marching squares with sub-pixel interpolation,
    /// then simplified within <paramref name="simplify"/> pixels. Holes come as separate loops (even-odd nesting).
    /// </summary>
    public static List<Path2> TraceOutlines(float[,] gray, double width, double threshold, bool invert, double simplify)
    {
        int w = gray.GetLength(0), h = gray.GetLength(1);
        var scale = width / w;
        var height = scale * h;
        // Value > 0 inside; padded by one pixel of background so that every outline closes.
        double Value(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return -1;
            var g = gray[x, y];
            return invert ? g - threshold : threshold - g;
        }

        // Segments between edge crossings of each cell (cell = 2x2 pixel centres), keyed by quantised end points.
        var next = new Dictionary<(long, long), List<(double X, double Y)>>();
        var segments = new List<((double X, double Y) A, (double X, double Y) B)>();
        (double, double) Edge(int x0, int y0, int x1, int y1)
        {
            var a = Value(x0, y0);
            var b = Value(x1, y1);
            var t = Math.Abs(a - b) < 1e-12 ? 0.5 : a / (a - b);
            return (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
        }
        for (var y = -1; y < h; y++)
        for (var x = -1; x < w; x++)
        {
            // Corners: tl (x,y), tr (x+1,y), br (x+1,y+1), bl (x,y+1).
            var code = (Value(x, y) > 0 ? 8 : 0) | (Value(x + 1, y) > 0 ? 4 : 0) | (Value(x + 1, y + 1) > 0 ? 2 : 0) | (Value(x, y + 1) > 0 ? 1 : 0);
            if (code is 0 or 15) continue;
            var top = Edge(x, y, x + 1, y);
            var right = Edge(x + 1, y, x + 1, y + 1);
            var bottom = Edge(x, y + 1, x + 1, y + 1);
            var left = Edge(x, y, x, y + 1);
            // Segments oriented with the inside on the left (consistent loops).
            switch (code)
            {
                case 1: segments.Add((left, bottom)); break;
                case 2: segments.Add((bottom, right)); break;
                case 3: segments.Add((left, right)); break;
                case 4: segments.Add((right, top)); break;
                case 5: segments.Add((left, top)); segments.Add((right, bottom)); break;
                case 6: segments.Add((bottom, top)); break;
                case 7: segments.Add((left, top)); break;
                case 8: segments.Add((top, left)); break;
                case 9: segments.Add((top, bottom)); break;
                case 10: segments.Add((top, right)); segments.Add((bottom, left)); break;
                case 11: segments.Add((top, right)); break;
                case 12: segments.Add((right, left)); break;
                case 13: segments.Add((right, bottom)); break;
                case 14: segments.Add((bottom, left)); break;
            }
        }

        static (long, long) Key((double X, double Y) p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6));
        var byStart = new Dictionary<(long, long), Queue<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            var k = Key(segments[i].A);
            if (!byStart.TryGetValue(k, out var q)) byStart[k] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var used = new bool[segments.Count];
        var loops = new List<Path2>();
        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;
            var pts = new List<(double X, double Y)>();
            var current = i;
            while (true)
            {
                used[current] = true;
                pts.Add(segments[current].A);
                var k = Key(segments[current].B);
                if (!byStart.TryGetValue(k, out var q)) break;
                var found = -1;
                while (q.Count > 0)
                {
                    var candidate = q.Dequeue();
                    if (!used[candidate]) { found = candidate; break; }
                }
                if (found < 0) break;
                current = found;
            }
            if (pts.Count < 3) continue;
            // Pixel centres → drawing units (Y up, image row 0 at the top).
            var drawing = pts.Select(p => new Point2((p.X + 0.5) * scale, height - (p.Y + 0.5) * scale)).ToList();
            var simplified = Simplify(drawing, Math.Max(simplify, 0) * scale);
            if (simplified.Count < 3) continue;
            var path = Path2.FromPoints(simplified, closed: true);
            if (Math.Abs(path.SignedArea()) < scale * scale * 0.5) continue; // specks smaller than half a pixel
            loops.Add(path);
        }
        return loops;
    }

    /// <summary>Douglas–Peucker simplification of a closed point loop.</summary>
    public static List<Point2> Simplify(List<Point2> loop, double tolerance)
    {
        if (tolerance <= 0 || loop.Count < 4) return loop;
        // Split the loop at its two farthest points and simplify both halves.
        var far = 0;
        var best = 0.0;
        for (var i = 1; i < loop.Count; i++)
        {
            var d = Point2.Distance(loop[0], loop[i]);
            if (d > best) (best, far) = (d, i);
        }
        var first = Dp(loop.Take(far + 1).ToList(), tolerance);
        var second = Dp(loop.Skip(far).Append(loop[0]).ToList(), tolerance);
        return first.Concat(second.Skip(1).SkipLast(1)).ToList();
    }

    private static List<Point2> Dp(List<Point2> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var index = -1;
            var max = tol;
            for (var i = a + 1; i < b; i++)
            {
                var d = Path2.DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > max) (max, index) = (d, i);
            }
            if (index < 0) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }
}
