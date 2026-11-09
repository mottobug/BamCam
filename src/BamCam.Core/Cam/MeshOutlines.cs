using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>
/// The outline of a mesh above a level, seen from above. From level to level downwards the area only grows, so a large
/// mesh adds what lies between two levels to the previous outline, instead of uniting up to a million triangles again
/// for each level.
/// </summary>
public sealed class MeshSilhouettes(Mesh mesh, CancellationToken cancel = default)
{
    private List<Path2>? _outline;
    private double _z;

    /// <summary>XY outline of everything at or above <paramref name="z"/> (ask for falling levels to profit from the previous one).</summary>
    public List<Path2> Above(double z)
    {
        if (mesh.Triangles.Count < Polygons.LargeUnionCount) return Polygons.UnionLarge(mesh.SliceAbove(z), cancel);
        _outline = _outline != null && z <= _z
            ? Polygons.UnionAdd(_outline, mesh.SliceAbove(z, _z), cancel)
            : Polygons.UnionLarge(mesh.SliceAbove(z), cancel);
        _z = z;
        return _outline;
    }
}

/// <summary>Outlines drawn from meshes (Edit > Surface): the silhouette seen from above and the sharp or open edges.</summary>
public static class MeshOutlines
{
    /// <summary>The triangles of surfaces in drawing coordinates.</summary>
    public static List<Triangle> Triangles(IEnumerable<SurfaceEntity> surfaces)
    {
        var tris = new List<Triangle>();
        foreach (var s in surfaces)
        {
            var v = s.WorldVertices().ToList();
            for (var i = 0; i + 2 < s.Faces.Count; i += 3)
                tris.Add(new Triangle(v[s.Faces[i]], v[s.Faces[i + 1]], v[s.Faces[i + 2]]));
        }
        return tris;
    }

    /// <summary>
    /// Closed outlines of the meshes seen from above. With an interval of 0 one outline of everything, at the top of the
    /// meshes. Otherwise one set of outlines for each level from the top down to the bottom, in steps of the interval,
    /// each at its height: what lies above the level is projected down onto it (the area a tool from above can reach
    /// around the model at that height).
    /// </summary>
    public static List<Path2> Silhouette(IReadOnlyList<Triangle> triangles, double interval, CancellationToken cancel = default)
    {
        var mesh = new Mesh(triangles, 10);
        if (mesh.Triangles.Count == 0) return [];
        var top = mesh.Bounds.Max.Z;
        var bottom = mesh.Bounds.Min.Z;
        var levels = new List<double>();
        if (interval > 0 && top - bottom > 1e-9)
        {
            for (var k = 1; top - k * interval > bottom + 1e-9; k++) levels.Add(top - k * interval);
            levels.Add(bottom);
        }
        var result = new List<Path2>();
        if (levels.Count == 0)
        {
            result.AddRange(AtHeight(Polygons.UnionLarge(mesh.SliceAbove(double.NegativeInfinity), cancel), top));
            return result;
        }
        var silhouettes = new MeshSilhouettes(mesh, cancel);
        foreach (var z in levels)
        {
            cancel.ThrowIfCancellationRequested();
            result.AddRange(AtHeight(silhouettes.Above(z), z));
        }
        return result;
    }

    private static IEnumerable<Path2> AtHeight(IEnumerable<Path2> paths, double z) =>
        paths.Where(p => p.Count >= 3).Select(p => p.Transformed(Matrix4.Translation(0, 0, z)));

    /// <summary>
    /// Edges of the meshes as 3D polylines: open borders (an edge of only one triangle), edges of more than two triangles,
    /// and edges where the two triangles meet at more than <paramref name="angleDegrees"/> (0 to 180; the angle between
    /// their normals, so 0 on a flat face). Vertices closer than <paramref name="weld"/> count as one, as in STL files,
    /// where each triangle has its own corners.
    /// </summary>
    public static List<Path2> Edges(IReadOnlyList<Triangle> triangles, double angleDegrees, double weld = 1e-6)
    {
        // Weld the corners: a grid key per point.
        var index = new Dictionary<(long, long, long), int>();
        var points = new List<Point3>();
        int Id(Point3 p)
        {
            var key = ((long)Math.Round(p.X / weld), (long)Math.Round(p.Y / weld), (long)Math.Round(p.Z / weld));
            if (index.TryGetValue(key, out var id)) return id;
            index[key] = points.Count;
            points.Add(p);
            return points.Count - 1;
        }
        // The triangles at each edge (smaller corner first).
        var edges = new Dictionary<(int, int), List<int>>();
        var normals = new List<Point3>();
        foreach (var t in triangles)
        {
            var n = Point3.Cross(t.B - t.A, t.C - t.A);
            if (n.Length < 1e-15) continue; // no area
            int a = Id(t.A), b = Id(t.B), c = Id(t.C);
            if (a == b || b == c || c == a) continue;
            var face = normals.Count;
            normals.Add(n.Normalized());
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = p < q ? (p, q) : (q, p);
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>(2);
                list.Add(face);
            }
        }
        var cos = Math.Cos(Angles.ToRadians(Math.Clamp(angleDegrees, 0, 180)));
        var sharp = new List<(int, int)>();
        foreach (var (key, faces) in edges)
        {
            if (faces.Count != 2) sharp.Add(key);
            else if (Point3.Dot(normals[faces[0]], normals[faces[1]]) < cos - 1e-12) sharp.Add(key);
        }
        return Chain(sharp, points);
    }

    /// <summary>Edges joined into polylines: from the ends and branch points first, then the closed loops that remain.</summary>
    private static List<Path2> Chain(List<(int A, int B)> edges, List<Point3> points)
    {
        var adjacent = new Dictionary<int, List<int>>();
        void Link(int p, int q)
        {
            if (!adjacent.TryGetValue(p, out var list)) adjacent[p] = list = [];
            list.Add(q);
        }
        foreach (var (a, b) in edges)
        {
            Link(a, b);
            Link(b, a);
        }
        var used = new HashSet<(int, int)>();
        bool Use(int p, int q) => used.Add(p < q ? (p, q) : (q, p));
        bool Free(int p, int q) => !used.Contains(p < q ? (p, q) : (q, p));
        var result = new List<Path2>();

        List<int> Walk(int start, int next)
        {
            var chain = new List<int> { start, next };
            Use(start, next);
            var current = next;
            // Go on through points where exactly two edges meet.
            while (adjacent[current].Count == 2)
            {
                var following = adjacent[current].FirstOrDefault(n => Free(current, n), -1);
                if (following < 0) break;
                Use(current, following);
                chain.Add(following);
                current = following;
            }
            return chain;
        }
        Path2 ToPath(List<int> chain)
        {
            var closed = chain.Count > 3 && chain[0] == chain[^1];
            var pts = closed ? chain.Take(chain.Count - 1) : chain;
            return new Path2(pts.Select(i => new Vertex(points[i])).ToList(), closed);
        }

        // Open chains from ends and branch points, in a stable order.
        foreach (var p in adjacent.Keys.Where(p => adjacent[p].Count != 2).OrderBy(p => p).ToList())
            foreach (var n in adjacent[p])
                if (Free(p, n)) result.Add(ToPath(Walk(p, n)));
        // Closed loops.
        foreach (var p in adjacent.Keys.OrderBy(p => p).ToList())
            foreach (var n in adjacent[p])
                if (Free(p, n)) result.Add(ToPath(Walk(p, n)));
        return result;
    }
}
