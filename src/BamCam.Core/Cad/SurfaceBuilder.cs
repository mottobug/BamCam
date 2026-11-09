using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>
/// Meshes and fills made from 2D outlines: Draw > Surface > Extrude, Extrude Solid and Fill, and Draw > Fill Region.
/// </summary>
public static class SurfaceBuilder
{
    /// <summary>Chord tolerance for arcs and splines of the outlines.</summary>
    public const double ArcTolerance = 0.01;

    // ------------------------------------------------------------------ fill region

    /// <summary>
    /// Polylines that fill the closed outlines (outer shapes with their holes) in a pattern, <paramref name="margin"/>
    /// inside the outlines and <paramref name="step"/> apart: offsets from the outer wall inwards, from the holes outwards,
    /// both, or hatch lines along X or Y, with the boundary pass. At the height of each outline.
    /// </summary>
    public static List<Path2> FillRegion(IEnumerable<Path2> outlines, double margin, double step, RegionFillStyle style)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        var result = new List<Path2>();
        foreach (var (shape, z) in ShapesByHeight(outlines))
        {
            var regions = margin > 0 ? Polygons.InsetShape(shape, margin) : [shape];
            foreach (var region in regions)
            foreach (var loop in PocketGenerator.FillPattern(region, step, style))
            {
                if (loop.Points.Count < 2) continue;
                result.Add(new Path2(loop.Points.Select(p => new Vertex(new Point3(p.X, p.Y, z))).ToList(), loop.Closed && loop.Points.Count > 2));
            }
        }
        return result;
    }

    /// <summary>The closed outlines grouped into shapes (outer boundary with holes), with the height of their first point.</summary>
    private static List<(Shape Shape, double Z)> ShapesByHeight(IEnumerable<Path2> outlines)
    {
        var result = new List<(Shape, double)>();
        foreach (var group in outlines.Where(p => p.Closed && p.Count >= 2).GroupBy(p => Math.Round(p.Vertices[0].Point.Z, 6)))
            foreach (var shape in Polygons.BuildShapes(group, ArcTolerance))
                result.Add((shape, group.Key));
        return result;
    }

    // ------------------------------------------------------------------ meshes

    /// <summary>
    /// The walls the outlines sweep when moved by <paramref name="height"/> along Z (+ upwards), as an open mesh without
    /// top and bottom, in <paramref name="steps"/> rows of triangles. Open outlines give a single wall.
    /// </summary>
    public static SurfaceEntity Extrude(IEnumerable<Path2> outlines, double height, int steps = 1)
    {
        steps = Math.Max(1, steps);
        var mesh = new MeshWriter();
        foreach (var path in outlines.Where(p => p.Count >= 2))
        {
            var z0 = path.Vertices[0].Point.Z;
            var pts = Clean(path.Flatten(ArcTolerance), path.Closed);
            if (pts.Count < 2) continue;
            var closed = path.Closed && pts.Count >= 3;
            for (var k = 0; k < steps; k++)
                mesh.Wall(pts, closed, z0 + height * k / steps, z0 + height * (k + 1) / steps);
        }
        return mesh.ToEntity();
    }

    /// <summary>
    /// A closed body: the closed outlines (with their holes) moved by <paramref name="height"/> along Z, with walls, top
    /// and bottom. The triangles face outwards, so that it can be saved as STL.
    /// </summary>
    public static SurfaceEntity ExtrudeSolid(IEnumerable<Path2> outlines, double height)
    {
        var mesh = new MeshWriter();
        foreach (var (shape, z) in ShapesByHeight(outlines))
        {
            var (low, high) = height >= 0 ? (z, z + height) : (z + height, z);
            var outer = Polygons.EnsureOrientation(shape.Outer, ccw: true);
            var holes = shape.Holes.Select(h => Polygons.EnsureOrientation(h, ccw: false)).ToList();
            var tris = Triangulate(outer, holes);
            foreach (var (a, b, c) in tris)
            {
                mesh.Triangle(new Point3(a.X, a.Y, high), new Point3(b.X, b.Y, high), new Point3(c.X, c.Y, high));
                mesh.Triangle(new Point3(a.X, a.Y, low), new Point3(c.X, c.Y, low), new Point3(b.X, b.Y, low));
            }
            // Outer wall counter-clockwise and holes clockwise: walls face away from the material.
            foreach (var ring in holes.Prepend(outer))
                mesh.Wall(Clean(ring.Flatten(ArcTolerance), true), true, low, high);
        }
        return mesh.ToEntity();
    }

    /// <summary>The closed outlines (with their holes) as flat meshes without thickness, facing up.</summary>
    public static SurfaceEntity Fill(IEnumerable<Path2> outlines)
    {
        var mesh = new MeshWriter();
        foreach (var (shape, z) in ShapesByHeight(outlines))
        {
            var outer = Polygons.EnsureOrientation(shape.Outer, ccw: true);
            var holes = shape.Holes.Select(h => Polygons.EnsureOrientation(h, ccw: false)).ToList();
            foreach (var (a, b, c) in Triangulate(outer, holes))
                mesh.Triangle(new Point3(a.X, a.Y, z), new Point3(b.X, b.Y, z), new Point3(c.X, c.Y, z));
        }
        return mesh.ToEntity();
    }

    /// <summary>Collects triangles of a mesh, sharing the corners of walls.</summary>
    private sealed class MeshWriter
    {
        private readonly List<Point3> _vertices = [];
        private readonly List<int> _faces = [];

        private int Add(Point3 p)
        {
            _vertices.Add(p);
            return _vertices.Count - 1;
        }

        public void Triangle(Point3 a, Point3 b, Point3 c)
        {
            _faces.Add(Add(a));
            _faces.Add(Add(b));
            _faces.Add(Add(c));
        }

        /// <summary>A band of quads between the points at <paramref name="z0"/> and at <paramref name="z1"/>.</summary>
        public void Wall(List<Point2> pts, bool closed, double z0, double z1)
        {
            if (pts.Count < 2 || Math.Abs(z1 - z0) < 1e-12) return;
            var bottom = pts.Select(p => Add(new Point3(p.X, p.Y, z0))).ToList();
            var top = pts.Select(p => Add(new Point3(p.X, p.Y, z1))).ToList();
            var n = closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % pts.Count;
                // With the outline counter-clockwise and z1 above z0 the normal points to the right of the outline (outwards).
                if (z1 > z0)
                {
                    _faces.AddRange([bottom[i], bottom[j], top[j]]);
                    _faces.AddRange([bottom[i], top[j], top[i]]);
                }
                else
                {
                    _faces.AddRange([bottom[i], top[j], bottom[j]]);
                    _faces.AddRange([bottom[i], top[i], top[j]]);
                }
            }
        }

        public SurfaceEntity ToEntity()
        {
            var s = new SurfaceEntity();
            s.ShareMesh(_vertices, _faces);
            return s;
        }
    }

    /// <summary>The points without repeats (and without the closing repeat of the first point of a closed outline).</summary>
    private static List<Point2> Clean(IReadOnlyList<Point2> pts, bool closed)
    {
        var result = new List<Point2>(pts.Count);
        foreach (var p in pts)
            if (result.Count == 0 || Point2.Distance(result[^1], p) > 1e-9) result.Add(p);
        if (closed && result.Count > 1 && Point2.Distance(result[0], result[^1]) <= 1e-9) result.RemoveAt(result.Count - 1);
        return result;
    }

    // ------------------------------------------------------------------ triangulation

    /// <summary>
    /// Triangles that cover the area inside <paramref name="outer"/> (counter-clockwise) and outside the
    /// <paramref name="holes"/> (clockwise), counter-clockwise themselves. Each hole is joined to the outline by a cut
    /// to a vertex it can see, then ears are clipped.
    /// </summary>
    public static List<(Point2 A, Point2 B, Point2 C)> Triangulate(Path2 outer, IReadOnlyList<Path2> holes)
    {
        var polygon = Clean(outer.Flatten(ArcTolerance), true);
        if (polygon.Count < 3) return [];
        var rings = holes.Select(h => Clean(h.Flatten(ArcTolerance), true)).Where(h => h.Count >= 3)
            .OrderByDescending(h => h.Max(p => p.X)).ToList();
        foreach (var hole in rings) polygon = Bridge(polygon, hole);
        return EarClip(polygon);
    }

    private static double Cross(Point2 o, Point2 a, Point2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>The hole spliced into the polygon through a cut from its rightmost vertex to a visible polygon vertex.</summary>
    private static List<Point2> Bridge(List<Point2> polygon, List<Point2> hole)
    {
        var m = 0;
        for (var i = 1; i < hole.Count; i++)
            if (hole[i].X > hole[m].X || (hole[i].X == hole[m].X && hole[i].Y < hole[m].Y)) m = i;
        var mp = hole[m];

        // Nearest crossing of a ray from mp towards +X with an edge of the polygon.
        var bestX = double.PositiveInfinity;
        var candidate = -1;
        for (int i = 0, n = polygon.Count; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];
            // The material lies left of every edge (outline counter-clockwise, holes clockwise), so a ray from inside
            // first meets an edge running upwards.
            if (!(a.Y <= mp.Y && b.Y >= mp.Y && b.Y > a.Y)) continue;
            var x = a.X + (mp.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
            if (x < mp.X - 1e-12 || x >= bestX) continue;
            bestX = x;
            // The end of the edge on the ray, or else the one further to the right.
            candidate = Math.Abs(a.Y - mp.Y) < 1e-12 ? i
                : Math.Abs(b.Y - mp.Y) < 1e-12 ? (i + 1) % n
                : a.X > b.X ? i : (i + 1) % n;
        }
        if (candidate < 0)
        {
            // Should not happen for a hole inside the outline: take the nearest vertex.
            candidate = Enumerable.Range(0, polygon.Count).MinBy(i => Point2.Distance(polygon[i], mp));
        }
        else
        {
            // A reflex vertex inside the triangle (mp, crossing, candidate) would hide the candidate: take the one with
            // the smallest angle to the ray instead.
            var hit = new Point2(bestX, mp.Y);
            var cp = polygon[candidate];
            var bestAngle = double.PositiveInfinity;
            var bestDistance = double.PositiveInfinity;
            for (int i = 0, n = polygon.Count; i < n; i++)
            {
                var p = polygon[i];
                if (i == candidate || p.X < mp.X) continue;
                var prev = polygon[(i + n - 1) % n];
                var next = polygon[(i + 1) % n];
                if (Cross(prev, p, next) > 0) continue; // convex
                if (!InTriangle(p, mp, hit, cp) && !InTriangle(p, mp, cp, hit)) continue;
                var angle = Math.Abs(Math.Atan2(p.Y - mp.Y, p.X - mp.X));
                var distance = Point2.Distance(p, mp);
                if (angle < bestAngle - 1e-12 || (Math.Abs(angle - bestAngle) <= 1e-12 && distance < bestDistance))
                {
                    bestAngle = angle;
                    bestDistance = distance;
                    candidate = i;
                }
            }
            // Of several copies of the same point (from earlier cuts) take the one whose corner the cut enters.
            candidate = PickCopy(polygon, candidate, mp);
        }

        var result = new List<Point2>(polygon.Count + hole.Count + 2);
        for (var i = 0; i <= candidate; i++) result.Add(polygon[i]);
        for (var k = 0; k <= hole.Count; k++) result.Add(hole[(m + k) % hole.Count]);
        result.Add(polygon[candidate]);
        for (var i = candidate + 1; i < polygon.Count; i++) result.Add(polygon[i]);
        return result;
    }

    /// <summary>
    /// When a point occurs more than once (the ends of an earlier cut), the copy whose corner (between its neighbours)
    /// contains the direction to <paramref name="target"/>.
    /// </summary>
    private static int PickCopy(List<Point2> polygon, int index, Point2 target)
    {
        var p = polygon[index];
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            if (Point2.Distance(polygon[i], p) > 1e-12) continue;
            var prev = polygon[(i + n - 1) % n];
            var next = polygon[(i + 1) % n];
            if (InCorner(prev, p, next, target)) return i;
        }
        return index;
    }

    /// <summary>Whether the direction from <paramref name="p"/> to <paramref name="t"/> lies inside the corner prev-p-next of a counter-clockwise polygon.</summary>
    private static bool InCorner(Point2 prev, Point2 p, Point2 next, Point2 t)
    {
        var convex = Cross(prev, p, next) >= 0;
        var leftOfIn = Cross(prev, p, t) >= 0;
        var leftOfOut = Cross(p, next, t) >= 0;
        return convex ? leftOfIn && leftOfOut : leftOfIn || leftOfOut;
    }

    private static bool InTriangle(Point2 p, Point2 a, Point2 b, Point2 c) =>
        Cross(a, b, p) >= -1e-12 && Cross(b, c, p) >= -1e-12 && Cross(c, a, p) >= -1e-12;

    /// <summary>Ear clipping of a counter-clockwise polygon (which may touch itself along the cuts to its holes).</summary>
    private static List<(Point2, Point2, Point2)> EarClip(List<Point2> polygon)
    {
        var result = new List<(Point2, Point2, Point2)>();
        var idx = Enumerable.Range(0, polygon.Count).ToList();
        var guard = 0;
        var i = 0;
        while (idx.Count > 3 && guard < idx.Count * 2 + 10)
        {
            var n = idx.Count;
            var ip = idx[(i + n - 1) % n];
            var ic = idx[i % n];
            var iN = idx[(i + 1) % n];
            Point2 a = polygon[ip], b = polygon[ic], c = polygon[iN];
            var cross = Cross(a, b, c);
            var scale = Point2.Distance(a, b) * Point2.Distance(b, c);
            if (Math.Abs(cross) <= 1e-12 * Math.Max(scale, 1e-12))
            {
                // A repeat or a point on a straight line: a triangle without area, dropped.
                idx.RemoveAt(i % n);
                guard = 0;
                if (idx.Count > 0) i %= idx.Count;
                continue;
            }
            if (cross > 0 && IsEar(polygon, idx, ip, ic, iN))
            {
                result.Add((a, b, c));
                idx.RemoveAt(i % n);
                guard = 0;
                i = Math.Max(0, (i % n) - 1);
                continue;
            }
            i = (i + 1) % n;
            guard++;
        }
        if (idx.Count == 3 && Cross(polygon[idx[0]], polygon[idx[1]], polygon[idx[2]]) > 1e-14)
            result.Add((polygon[idx[0]], polygon[idx[1]], polygon[idx[2]]));
        return result;
    }

    private static bool IsEar(List<Point2> polygon, List<int> idx, int ip, int ic, int iN)
    {
        Point2 a = polygon[ip], b = polygon[ic], c = polygon[iN];
        var minX = Math.Min(a.X, Math.Min(b.X, c.X));
        var maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        var minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        var maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        foreach (var k in idx)
        {
            if (k == ip || k == ic || k == iN) continue;
            var p = polygon[k];
            if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;
            // Copies of the corners (ends of a cut) do not block the ear.
            if (Point2.Distance(p, a) < 1e-12 || Point2.Distance(p, b) < 1e-12 || Point2.Distance(p, c) < 1e-12) continue;
            if (InTriangle(p, a, b, c)) return false;
        }
        return true;
    }
}
