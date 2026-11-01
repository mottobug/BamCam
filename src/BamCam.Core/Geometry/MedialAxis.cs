using DelaunatorSharp;

namespace BamCam.Core.Geometry;

/// <summary>A point of the medial axis and the radius of the largest circle around it inside the shape.</summary>
public readonly record struct MedialPoint(Point2 P, double R);

/// <summary>
/// Approximate medial axis (centres of maximal inscribed circles) of a shape with holes. The outlines are sampled
/// densely; the Voronoi vertices of the samples (circumcentres of their Delaunay triangles) that lie inside the
/// shape approximate the axis, with the circumradius as clearance. Voronoi edges crossing the outline are dropped.
/// </summary>
public static class MedialAxis
{
    /// <summary>Medial axis as chains between branch points and ends (closed chains repeat their first point).</summary>
    /// <param name="loops">Closed outlines of one shape (outer outline and holes).</param>
    /// <param name="spacing">Distance of the samples along the outlines: the accuracy of the axis.</param>
    public static List<List<MedialPoint>> Compute(IReadOnlyList<Path2> loops, double spacing)
    {
        spacing = Math.Max(spacing, 1e-4);
        var points = new List<Point2>();
        var loopStart = new List<int>();
        var loopOf = new List<int>();
        var polygons = new List<List<Point2>>();
        foreach (var loop in loops.Where(l => l.Closed && l.Count > 1))
        {
            var flat = loop.Flatten(Math.Min(Tolerance.Chord, spacing / 10));
            var dense = new List<Point2>();
            for (var i = 0; i < flat.Count; i++)
            {
                var a = flat[i];
                var b = flat[(i + 1) % flat.Count];
                var n = Math.Max(1, (int)Math.Ceiling(Point2.Distance(a, b) / spacing));
                for (var k = 0; k < n; k++) dense.Add(a + (b - a) * ((double)k / n));
            }
            if (dense.Count < 3) continue;
            loopStart.Add(points.Count);
            foreach (var p in dense) loopOf.Add(polygons.Count);
            points.AddRange(dense);
            polygons.Add(dense);
        }
        var result = new List<List<MedialPoint>>();
        if (points.Count < 3) return result;

        Delaunator del;
        try { del = new Delaunator(points.Select(p => (IPoint)new DelaunatorSharp.Point(p.X, p.Y)).ToArray()); }
        catch (Exception) { return result; } // all samples on one line: no area

        var inside = new EvenOdd(polygons);
        var triangles = del.Triangles.Length / 3;
        var node = new int[triangles];
        var nodes = new List<MedialPoint>();
        for (var t = 0; t < triangles; t++)
        {
            node[t] = -1;
            var a = points[del.Triangles[3 * t]];
            var b = points[del.Triangles[3 * t + 1]];
            var c = points[del.Triangles[3 * t + 2]];
            if (Circumcentre(a, b, c) is not { } centre || !inside.Contains(centre)) continue;
            node[t] = nodes.Count;
            nodes.Add(new MedialPoint(centre, Point2.Distance(centre, a)));
        }

        // A Delaunay edge between neighbouring samples of one outline lies on the outline: the Voronoi edge across
        // it would cross the outline.
        bool OnOutline(int i, int j)
        {
            if (loopOf[i] != loopOf[j]) return false;
            var l = loopOf[i];
            var count = polygons[l].Count;
            var d = Math.Abs((i - loopStart[l]) - (j - loopStart[l]));
            return d == 1 || d == count - 1;
        }

        var adjacent = nodes.Select(_ => new List<int>(3)).ToArray();
        for (var e = 0; e < del.Halfedges.Length; e++)
        {
            var o = del.Halfedges[e];
            if (o < e) continue; // each edge once; -1 = convex hull
            int n1 = node[e / 3], n2 = node[o / 3];
            if (n1 < 0 || n2 < 0 || n1 == n2) continue;
            var i = del.Triangles[e];
            var j = del.Triangles[e % 3 == 2 ? e - 2 : e + 1];
            if (OnOutline(i, j)) continue;
            adjacent[n1].Add(n2);
            adjacent[n2].Add(n1);
        }
        return Chains(nodes, adjacent);
    }

    private static Point2? Circumcentre(Point2 a, Point2 b, Point2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-18) return null;
        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        return new Point2((a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
    }

    /// <summary>Splits the graph into chains between nodes that are not simple path nodes (degree 2).</summary>
    private static List<List<MedialPoint>> Chains(List<MedialPoint> nodes, List<int>[] adjacent)
    {
        var chains = new List<List<MedialPoint>>();
        var used = new HashSet<(int, int)>();
        bool Use(int a, int b) => used.Add(a < b ? (a, b) : (b, a));

        List<MedialPoint> Walk(int from, int next)
        {
            var chain = new List<MedialPoint> { nodes[from] };
            var prev = from;
            var cur = next;
            while (true)
            {
                chain.Add(nodes[cur]);
                if (adjacent[cur].Count != 2 || cur == from) break;
                var following = adjacent[cur][0] == prev ? adjacent[cur][1] : adjacent[cur][0];
                if (!Use(cur, following)) break;
                prev = cur;
                cur = following;
            }
            return chain;
        }

        for (var n = 0; n < nodes.Count; n++)
        {
            if (adjacent[n].Count == 2) continue;
            foreach (var m in adjacent[n])
                if (Use(n, m)) chains.Add(Walk(n, m));
        }
        // Remaining closed loops (e.g. the axis of a ring).
        for (var n = 0; n < nodes.Count; n++)
            foreach (var m in adjacent[n])
                if (Use(n, m)) chains.Add(Walk(n, m));
        return chains;
    }

    /// <summary>
    /// Drops chain points that deviate less than <paramref name="tolerance"/> from the line between their
    /// neighbours, in position and radius (Douglas-Peucker).
    /// </summary>
    public static List<MedialPoint> Simplify(List<MedialPoint> chain, double tolerance)
    {
        if (chain.Count < 3) return chain;
        var keep = new bool[chain.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, chain.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            var best = -1;
            var bestDist = tolerance;
            for (var i = s + 1; i < e; i++)
            {
                var d = Deviation(chain[s], chain[e], chain[i]);
                if (d > bestDist) { bestDist = d; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true;
            stack.Push((s, best));
            stack.Push((best, e));
        }
        return chain.Where((_, i) => keep[i]).ToList();
    }

    private static double Deviation(MedialPoint a, MedialPoint b, MedialPoint p)
    {
        var ab = b.P - a.P;
        var len2 = Point2.Dot(ab, ab);
        var t = len2 < 1e-18 ? 0 : Math.Clamp(Point2.Dot(p.P - a.P, ab) / len2, 0, 1);
        var onLine = a.P + ab * t;
        var r = a.R + (b.R - a.R) * t;
        return Math.Max(Point2.Distance(onLine, p.P), Math.Abs(r - p.R));
    }

    /// <summary>Even-odd point-in-polygon test with the edges sorted into horizontal bands.</summary>
    private sealed class EvenOdd
    {
        private readonly List<(Point2 A, Point2 B)>[] _bands;
        private readonly double _minY, _bandHeight;

        public EvenOdd(List<List<Point2>> polygons)
        {
            var all = polygons.SelectMany(p => p).ToList();
            _minY = all.Min(p => p.Y);
            var maxY = all.Max(p => p.Y);
            var count = Math.Clamp(all.Count / 8, 1, 4096);
            _bandHeight = Math.Max((maxY - _minY) / count, 1e-12);
            _bands = Enumerable.Range(0, count).Select(_ => new List<(Point2, Point2)>()).ToArray();
            foreach (var poly in polygons)
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    var (a, b) = (poly[j], poly[i]);
                    for (var k = Band(Math.Min(a.Y, b.Y)); k <= Band(Math.Max(a.Y, b.Y)); k++) _bands[k].Add((a, b));
                }
        }

        private int Band(double y) => Math.Clamp((int)((y - _minY) / _bandHeight), 0, _bands.Length - 1);

        public bool Contains(Point2 p)
        {
            if (p.Y < _minY || p.Y > _minY + _bandHeight * _bands.Length) return false;
            var inside = false;
            foreach (var (a, b) in _bands[Band(p.Y)])
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            return inside;
        }
    }
}
