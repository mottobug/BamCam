using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

public readonly record struct Triangle(Point3 A, Point3 B, Point3 C)
{
    /// <summary>Unit normal by right-hand winding (A→B→C).</summary>
    public Point3 Normal => Point3.Cross(B - A, C - A).Normalized();

    public double MinZ => Math.Min(A.Z, Math.Min(B.Z, C.Z));
    public double MaxZ => Math.Max(A.Z, Math.Max(B.Z, C.Z));
}

/// <summary>Cutter shape used by drop-cutter calculations.</summary>
public enum CutterShape { Flat, Ball }

/// <summary>
/// Triangle mesh with an XY bucket grid for fast "drop cutter" queries: the lowest tool tip Z at
/// which a cutter positioned over (x, y) touches the mesh.
/// </summary>
public sealed class Mesh
{
    private readonly List<Triangle> _tris;
    private readonly List<int>[,] _grid;
    private readonly double _cell;
    private readonly double _x0, _y0;
    private readonly int _nx, _ny;
    private readonly int[] _stamp;
    private int _query;

    public Mesh(IEnumerable<Triangle> triangles, double cellSize)
    {
        _tris = triangles.Where(t => t.Normal.Length > 0.5).ToList();
        Bounds = BoundingBox.FromPoints(_tris.SelectMany(t => new[] { t.A, t.B, t.C }));
        _cell = Math.Max(cellSize, 1e-3);
        _x0 = Bounds.IsEmpty ? 0 : Bounds.Min.X;
        _y0 = Bounds.IsEmpty ? 0 : Bounds.Min.Y;
        _nx = Bounds.IsEmpty ? 1 : Math.Max(1, (int)Math.Ceiling(Bounds.Width / _cell) + 1);
        _ny = Bounds.IsEmpty ? 1 : Math.Max(1, (int)Math.Ceiling(Bounds.Height / _cell) + 1);
        _grid = new List<int>[_nx, _ny];
        _stamp = new int[_tris.Count];
        for (var i = 0; i < _tris.Count; i++)
        {
            var t = _tris[i];
            var (ix0, iy0) = CellOf(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)));
            var (ix1, iy1) = CellOf(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)), Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
            for (var x = ix0; x <= ix1; x++)
            for (var y = iy0; y <= iy1; y++)
                (_grid[x, y] ??= []).Add(i);
        }
    }

    public IReadOnlyList<Triangle> Triangles => _tris;
    public BoundingBox Bounds { get; }

    private (int, int) CellOf(double x, double y) =>
        (Math.Clamp((int)Math.Floor((x - _x0) / _cell), 0, _nx - 1), Math.Clamp((int)Math.Floor((y - _y0) / _cell), 0, _ny - 1));

    /// <summary>
    /// Lowest tool-tip Z for a cutter of <paramref name="radius"/> centred at (x, y), or null when the
    /// cutter does not touch the mesh there.
    /// </summary>
    public double? DropCutter(double x, double y, double radius, CutterShape shape)
    {
        if (Bounds.IsEmpty) return null;
        var (ix0, iy0) = CellOf(x - radius, y - radius);
        var (ix1, iy1) = CellOf(x + radius, y + radius);
        if (x + radius < Bounds.Min.X || x - radius > Bounds.Max.X || y + radius < Bounds.Min.Y || y - radius > Bounds.Max.Y) return null;
        _query++;
        double best = double.NegativeInfinity;
        var p = new Point2(x, y);
        for (var gx = ix0; gx <= ix1; gx++)
        for (var gy = iy0; gy <= iy1; gy++)
        {
            var list = _grid[gx, gy];
            if (list == null) continue;
            foreach (var i in list)
            {
                if (_stamp[i] == _query) continue;
                _stamp[i] = _query;
                var z = shape == CutterShape.Ball ? BallTriangle(p, radius, _tris[i]) : FlatTriangle(p, radius, _tris[i]);
                if (z > best) best = z;
            }
        }
        return double.IsNegativeInfinity(best) ? null : best;
    }

    // ---- flat end mill -------------------------------------------------------------------

    private static double FlatTriangle(Point2 p, double r, Triangle t)
    {
        var best = double.NegativeInfinity;
        // Vertices.
        foreach (var v in new[] { t.A, t.B, t.C })
            if (Point2.Distance(v.XY, p) <= r) best = Math.Max(best, v.Z);
        // Edges: highest point of the edge inside the cutter disc.
        best = Math.Max(best, FlatEdge(p, r, t.A, t.B));
        best = Math.Max(best, FlatEdge(p, r, t.B, t.C));
        best = Math.Max(best, FlatEdge(p, r, t.C, t.A));
        // Facet: highest point of the plane within the disc, if inside the triangle.
        var n = t.Normal;
        if (Math.Abs(n.Z) > 1e-9)
        {
            if (n.Z < 0) n = n * -1;
            var nxy = new Point2(n.X, n.Y);
            var q = nxy.Length < 1e-12 ? p : p - nxy.Normalized() * r;
            if (InsideXY(q, t)) best = Math.Max(best, PlaneZ(t, n, q));
        }
        return best;
    }

    private static double FlatEdge(Point2 p, double r, Point3 a, Point3 b)
    {
        var d = b.XY - a.XY;
        var len2 = d.LengthSquared;
        if (len2 < 1e-18) return double.NegativeInfinity;
        // Solve |a + t d - p| = r for t.
        var f = a.XY - p;
        var bq = 2 * Point2.Dot(f, d);
        var c = f.LengthSquared - r * r;
        var disc = bq * bq - 4 * len2 * c;
        if (disc < 0) return double.NegativeInfinity;
        var s = Math.Sqrt(disc);
        var t0 = Math.Max(0, (-bq - s) / (2 * len2));
        var t1 = Math.Min(1, (-bq + s) / (2 * len2));
        if (t0 > t1) return double.NegativeInfinity;
        return Math.Max(a.Z + (b.Z - a.Z) * t0, a.Z + (b.Z - a.Z) * t1);
    }

    // ---- ball nose -----------------------------------------------------------------------

    private static double BallTriangle(Point2 p, double r, Triangle t)
    {
        // Returns tool tip Z (= ball centre Z - r).
        var best = double.NegativeInfinity;
        foreach (var v in new[] { t.A, t.B, t.C })
        {
            var d2 = (v.XY - p).LengthSquared;
            if (d2 <= r * r) best = Math.Max(best, v.Z + Math.Sqrt(r * r - d2) - r);
        }
        best = Math.Max(best, BallEdge(p, r, t.A, t.B));
        best = Math.Max(best, BallEdge(p, r, t.B, t.C));
        best = Math.Max(best, BallEdge(p, r, t.C, t.A));
        var n = t.Normal;
        if (Math.Abs(n.Z) > 1e-9)
        {
            if (n.Z < 0) n = n * -1;
            var q = new Point2(p.X - r * n.X, p.Y - r * n.Y);
            if (InsideXY(q, t)) best = Math.Max(best, PlaneZ(t, n, q) + r * n.Z - r);
        }
        return best;
    }

    private static double BallEdge(Point2 p, double r, Point3 a, Point3 b)
    {
        var dxy = b.XY - a.XY;
        var len = dxy.Length;
        if (len < 1e-12) return double.NegativeInfinity;
        var u = dxy / len;
        var w = Point2.Dot(p - a.XY, u);
        var h = Math.Abs(Point2.Cross(u, p - a.XY));
        if (h > r) return double.NegativeInfinity;
        var rr = Math.Sqrt(r * r - h * h);
        var s = (b.Z - a.Z) / len;
        var lo = Math.Max(0, w - rr);
        var hi = Math.Min(len, w + rr);
        if (lo > hi) return double.NegativeInfinity;
        var l = Math.Clamp(w + s * rr / Math.Sqrt(1 + s * s), lo, hi);
        var dl = w - l;
        var centre = a.Z + s * l + Math.Sqrt(Math.Max(rr * rr - dl * dl, 0));
        return centre - r;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static double PlaneZ(Triangle t, Point3 n, Point2 q) =>
        t.A.Z - (n.X * (q.X - t.A.X) + n.Y * (q.Y - t.A.Y)) / n.Z;

    private static bool InsideXY(Point2 q, Triangle t)
    {
        var d1 = Point2.Cross(t.B.XY - t.A.XY, q - t.A.XY);
        var d2 = Point2.Cross(t.C.XY - t.B.XY, q - t.B.XY);
        var d3 = Point2.Cross(t.A.XY - t.C.XY, q - t.C.XY);
        var neg = d1 < -1e-12 || d2 < -1e-12 || d3 < -1e-12;
        var pos = d1 > 1e-12 || d2 > 1e-12 || d3 > 1e-12;
        return !(neg && pos);
    }

    /// <summary>XY polygons of the parts of each triangle at or above <paramref name="z"/> (for waterlines).</summary>
    public IEnumerable<Path2> SliceAbove(double z)
    {
        foreach (var t in _tris)
        {
            if (t.MaxZ < z) continue;
            var pts = ClipAbove([t.A, t.B, t.C], z);
            if (pts.Count < 3) continue;
            var path = Path2.FromPoints(pts.Select(p => p.XY), true);
            var area = path.SignedArea();
            if (Math.Abs(area) < 1e-10) continue;
            // Consistent CCW orientation so a non-zero union doesn't cancel overlapping faces.
            yield return area < 0 ? path.Reversed() : path;
        }
    }

    private static List<Point3> ClipAbove(Point3[] poly, double z)
    {
        var result = new List<Point3>(4);
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var aIn = a.Z >= z;
            var bIn = b.Z >= z;
            if (aIn) result.Add(a);
            if (aIn != bIn)
            {
                var t = (z - a.Z) / (b.Z - a.Z);
                result.Add(new Point3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, z));
            }
        }
        return result;
    }
}
