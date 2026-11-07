using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

public readonly record struct Triangle(Point3 A, Point3 B, Point3 C)
{
    /// <summary>Unit normal by right-hand winding (A→B→C).</summary>
    public Point3 Normal => Point3.Cross(B - A, C - A).Normalized();

    public double MinZ => Math.Min(A.Z, Math.Min(B.Z, C.Z));
    public double MaxZ => Math.Max(A.Z, Math.Max(B.Z, C.Z));
}

/// <summary>Cutter shape used by drop-cutter calculations (Bull = flat end with rounded corners of a corner radius).</summary>
public enum CutterShape { Flat, Ball, Bull }

/// <summary>
/// Triangle mesh with an XY bucket grid for fast "drop cutter" queries: the lowest tool tip Z at
/// which a cutter positioned over (x, y) touches the mesh.
/// </summary>
public sealed class Mesh
{
    /// <summary>Most cells of the grid, to limit its memory.</summary>
    private const long MaxCells = 4_000_000;

    private readonly List<Triangle> _tris;
    // The triangles of cell c are _items[_starts[c] .. _starts[c + 1]].
    private readonly int[] _starts;
    private readonly int[] _items;
    private readonly double _cell;
    private readonly double _x0, _y0;
    private readonly int _nx, _ny;

    /// <summary>Marks the triangles a query has already looked at. One per thread: queries may run in parallel.</summary>
    private sealed class QueryState(int triangles)
    {
        public readonly int[] Stamp = new int[triangles];
        public int Query;
    }

    private readonly ThreadLocal<QueryState> _state;

    /// <param name="cellSize">Largest grid cell. A dense mesh gets smaller cells, so that a query looks at few triangles.</param>
    public Mesh(IEnumerable<Triangle> triangles, double cellSize)
    {
        _tris = triangles.Where(t => t.Normal.Length > 0.5).ToList();
        Bounds = BoundingBox.FromPoints(_tris.SelectMany(t => new[] { t.A, t.B, t.C }));
        var triangleCount = _tris.Count;
        _state = new ThreadLocal<QueryState>(() => new QueryState(triangleCount));
        _cell = CellSize(cellSize);
        _x0 = Bounds.IsEmpty ? 0 : Bounds.Min.X;
        _y0 = Bounds.IsEmpty ? 0 : Bounds.Min.Y;
        _nx = Bounds.IsEmpty ? 1 : Math.Max(1, (int)Math.Ceiling(Bounds.Width / _cell) + 1);
        _ny = Bounds.IsEmpty ? 1 : Math.Max(1, (int)Math.Ceiling(Bounds.Height / _cell) + 1);

        // Two passes: how many triangles per cell, then the triangles themselves in one array.
        _starts = new int[_nx * _ny + 1];
        void Cells(Triangle t, Action<int> action)
        {
            var (ix0, iy0) = CellOf(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)));
            var (ix1, iy1) = CellOf(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)), Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
            for (var x = ix0; x <= ix1; x++)
            for (var y = iy0; y <= iy1; y++)
                action(x * _ny + y);
        }
        foreach (var t in _tris) Cells(t, c => _starts[c + 1]++);
        for (var c = 0; c < _nx * _ny; c++) _starts[c + 1] += _starts[c];
        _items = new int[_starts[^1]];
        var next = new int[_nx * _ny];
        for (var i = 0; i < _tris.Count; i++)
        {
            var index = i;
            Cells(_tris[i], c => _items[_starts[c] + next[c]++] = index);
        }
    }

    private double CellSize(double largest)
    {
        var cell = Math.Max(largest, 1e-3);
        if (Bounds.IsEmpty || _tris.Count == 0) return cell;
        // About the size of a triangle, and not so small that a query has to walk many empty cells.
        var average = _tris.Average(t => Math.Max(
            Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)) - Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)),
            Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)) - Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))));
        cell = Math.Clamp(average, cell / 8, cell);
        var limit = Math.Sqrt(Math.Max(Bounds.Width, 1e-3) * Math.Max(Bounds.Height, 1e-3) / MaxCells);
        return Math.Max(cell, limit);
    }

    public IReadOnlyList<Triangle> Triangles => _tris;
    public BoundingBox Bounds { get; }

    private (int, int) CellOf(double x, double y) =>
        (Math.Clamp((int)Math.Floor((x - _x0) / _cell), 0, _nx - 1), Math.Clamp((int)Math.Floor((y - _y0) / _cell), 0, _ny - 1));

    /// <summary>
    /// Lowest tool-tip Z for a cutter of <paramref name="radius"/> centred at (x, y), or null when the
    /// cutter does not touch the mesh there. Safe to call from several threads at once.
    /// </summary>
    public double? DropCutter(double x, double y, double radius, CutterShape shape, double cornerRadius = 0)
    {
        if (shape == CutterShape.Bull)
        {
            cornerRadius = Math.Clamp(cornerRadius, 0, radius);
            if (cornerRadius < 1e-9) shape = CutterShape.Flat;
            else if (cornerRadius > radius - 1e-9) shape = CutterShape.Ball;
        }
        if (Bounds.IsEmpty) return null;
        var (ix0, iy0) = CellOf(x - radius, y - radius);
        var (ix1, iy1) = CellOf(x + radius, y + radius);
        if (x + radius < Bounds.Min.X || x - radius > Bounds.Max.X || y + radius < Bounds.Min.Y || y - radius > Bounds.Max.Y) return null;
        var state = _state.Value!;
        var query = ++state.Query;
        var stamp = state.Stamp;
        double best = double.NegativeInfinity;
        var p = new Point2(x, y);
        for (var gx = ix0; gx <= ix1; gx++)
        for (var gy = iy0; gy <= iy1; gy++)
        {
            var cell = gx * _ny + gy;
            for (var k = _starts[cell]; k < _starts[cell + 1]; k++)
            {
                var i = _items[k];
                if (stamp[i] == query) continue;
                stamp[i] = query;
                var t = _tris[i];
                if (OutOfReach(t, x, y, radius)) continue;
                var z = shape switch
                {
                    CutterShape.Ball => BallTriangle(p, radius, t),
                    CutterShape.Bull => BullTriangle(p, radius, cornerRadius, t),
                    _ => FlatTriangle(p, radius, t),
                };
                if (z > best) best = z;
            }
        }
        return double.IsNegativeInfinity(best) ? null : best;
    }

    /// <summary>The triangle lies beside the cutter: every contact would need a point of it within the radius.</summary>
    private static bool OutOfReach(in Triangle t, double x, double y, double radius)
    {
        var dx = Math.Max(Math.Max(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)) - x, x - Math.Max(t.A.X, Math.Max(t.B.X, t.C.X))), 0);
        var dy = Math.Max(Math.Max(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)) - y, y - Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y))), 0);
        var reach = radius + 1e-6;
        return dx * dx + dy * dy > reach * reach;
    }

    // ---- flat end mill -------------------------------------------------------------------

    private static double FlatTriangle(Point2 p, double r, Triangle t)
    {
        var best = double.NegativeInfinity;
        // Vertices.
        if (Point2.Distance(t.A.XY, p) <= r) best = Math.Max(best, t.A.Z);
        if (Point2.Distance(t.B.XY, p) <= r) best = Math.Max(best, t.B.Z);
        if (Point2.Distance(t.C.XY, p) <= r) best = Math.Max(best, t.C.Z);
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
        var best = BallVertex(p, r, t.A);
        best = Math.Max(best, BallVertex(p, r, t.B));
        best = Math.Max(best, BallVertex(p, r, t.C));
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

    private static double BallVertex(Point2 p, double r, Point3 v)
    {
        var d2 = (v.XY - p).LengthSquared;
        return d2 <= r * r ? v.Z + Math.Sqrt(r * r - d2) - r : double.NegativeInfinity;
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

    // ---- bull nose (torus) ---------------------------------------------------------------
    //
    // The cutter is a ball of the corner radius rc swept over a flat disc of radius R - rc: its tip height over p
    // is the highest ball tip height over all centres q of that disc. Vertices and facets are solved exactly,
    // edges exactly under the flat part and numerically (sampling + refinement) on the rounded rim.

    private static double BullTriangle(Point2 p, double r, double rc, Triangle t)
    {
        var flat = r - rc;
        var best = BullVertex(p, r, flat, rc, t.A);
        best = Math.Max(best, BullVertex(p, r, flat, rc, t.B));
        best = Math.Max(best, BullVertex(p, r, flat, rc, t.C));
        best = Math.Max(best, BullEdge(p, flat, rc, t.A, t.B));
        best = Math.Max(best, BullEdge(p, flat, rc, t.B, t.C));
        best = Math.Max(best, BullEdge(p, flat, rc, t.C, t.A));
        var n = t.Normal;
        if (Math.Abs(n.Z) > 1e-9)
        {
            if (n.Z < 0) n = n * -1;
            var nxy = new Point2(n.X, n.Y);
            // Ball centre at the rim of the flat disc towards the uphill side, contact one corner radius along -n.
            var q = nxy.Length < 1e-12 ? p : p - nxy.Normalized() * flat;
            var contact = new Point2(q.X - rc * n.X, q.Y - rc * n.Y);
            if (InsideXY(contact, t)) best = Math.Max(best, PlaneZ(t, n, contact) + rc * n.Z - rc);
        }
        return best;
    }

    private static double BullVertex(Point2 p, double r, double flat, double rc, Point3 v)
    {
        var d = Point2.Distance(v.XY, p);
        if (d <= flat) return v.Z;
        if (d > r) return double.NegativeInfinity;
        var e = d - flat;
        return v.Z + Math.Sqrt(rc * rc - e * e) - rc;
    }

    private static double BullEdge(Point2 p, double flat, double rc, Point3 a, Point3 b)
    {
        // Under the flat end: the highest edge point within the disc touches the bottom.
        var best = FlatEdge(p, flat, a, b);
        // Rounded rim: the ball of radius rc centred on the circle of radius 'flat' around p.
        if (Path2.DistanceToSegment(p, a.XY, b.XY) > flat + rc) return best;
        double At(double angle) => BallEdge(p + Point2.FromAngle(angle, flat), rc, a, b);
        const int samples = 24;
        var bestAngle = 0.0;
        var bestValue = double.NegativeInfinity;
        for (var i = 0; i < samples; i++)
        {
            var angle = 2 * Math.PI * i / samples;
            var v = At(angle);
            if (v > bestValue) (bestValue, bestAngle) = (v, angle);
        }
        if (double.IsNegativeInfinity(bestValue)) return best;
        // Golden section search around the best sample.
        var lo = bestAngle - 2 * Math.PI / samples;
        var hi = bestAngle + 2 * Math.PI / samples;
        const double g = 0.6180339887498949;
        var x1 = hi - g * (hi - lo);
        var x2 = lo + g * (hi - lo);
        var f1 = At(x1);
        var f2 = At(x2);
        for (var k = 0; k < 30; k++)
        {
            if (f1 >= f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = At(x1); }
            else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = At(x2); }
        }
        return Math.Max(best, Math.Max(bestValue, Math.Max(f1, f2)));
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
