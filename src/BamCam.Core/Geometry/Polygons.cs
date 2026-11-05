using Clipper2Lib;

namespace BamCam.Core.Geometry;

/// <summary>A closed outer boundary together with the holes (islands) inside it.</summary>
public sealed class Shape
{
    public Path2 Outer { get; }
    public List<Path2> Holes { get; } = [];

    public Shape(Path2 outer, IEnumerable<Path2>? holes = null)
    {
        Outer = outer;
        if (holes != null) Holes.AddRange(holes);
    }

    public IEnumerable<Path2> AllPaths() => Holes.Prepend(Outer);
}

public enum JoinStyle { Round, Miter, Square }

/// <summary>Polygon offsetting and boolean operations (wrapping Clipper2).</summary>
public static class Polygons
{
    private const int Precision = 6;

    public static PathD ToClipper(Path2 path, double tolerance = Tolerance.Chord)
    {
        var flat = path.Flatten(tolerance);
        var result = new PathD(flat.Count);
        foreach (var p in flat) result.Add(new PointD(p.X, p.Y));
        return result;
    }

    public static PathsD ToClipper(IEnumerable<Path2> paths, double tolerance = Tolerance.Chord)
    {
        var result = new PathsD();
        foreach (var p in paths)
            if (p.Closed && p.Count >= 2) result.Add(ToClipper(p, tolerance));
        return result;
    }

    public static Path2 FromClipper(PathD path, double z = 0) =>
        Path2.FromPoints(path.Select(p => new Point2(p.x, p.y)), closed: true, z);

    private static JoinType Map(JoinStyle j) => j switch
    {
        JoinStyle.Miter => JoinType.Miter,
        JoinStyle.Square => JoinType.Square,
        _ => JoinType.Round,
    };

    /// <summary>
    /// Offsets closed paths. Positive <paramref name="delta"/> grows outward, negative shrinks.
    /// Input orientation is ignored (non-zero fill after normalisation by even-odd union).
    /// </summary>
    public static List<Path2> Offset(IEnumerable<Path2> paths, double delta, JoinStyle join = JoinStyle.Round,
        double tolerance = Tolerance.Chord, double z = 0, double simplify = 0)
    {
        // Collinear vertices are kept: a vertex in the middle of a straight edge stays a vertex of the
        // offset, as in the reference G-code (one G1 per source segment).
        var scale = Math.Pow(10, Precision);
        var src = Clipper.ScalePaths64(ToClipper(paths, tolerance), scale);
        var normalized = new Paths64();
        var union = new Clipper64 { PreserveCollinear = true };
        union.AddSubject(src);
        union.Execute(ClipType.Union, FillRule.EvenOdd, normalized);
        // Round joins need less precision than the source flattening (exact corner arcs come from OffsetSnapper);
        // a tiny arc tolerance would put many points into every micro corner at large offsets (very slow).
        var co = new ClipperOffset(2.0, JoinArcTolerance(tolerance) * scale, preserveCollinear: true);
        co.AddPaths(normalized, Map(join), EndType.Polygon);
        var res = new Paths64();
        co.Execute(delta * scale, res);
        if (simplify > 0) res = Clipper.SimplifyPaths(res, simplify * scale, true);
        return Clipper.ScalePathsD(res, 1 / scale).Select(p => FromClipper(p, z)).ToList();
    }

    /// <summary>
    /// Offsets the same closed paths by several distances (all with the same sign). Computed incrementally from
    /// the nearest smaller distance with light simplification: one large offset of a dense outline costs far
    /// more than a chain of small ones. Results are returned in the order of <paramref name="deltas"/>.
    /// </summary>
    public static List<List<Path2>> OffsetSeries(IReadOnlyList<Path2> paths, IReadOnlyList<double> deltas,
        JoinStyle join = JoinStyle.Round, double tolerance = Tolerance.Chord)
    {
        var results = new List<Path2>[deltas.Count];
        var mixedSigns = deltas.Any(d => d > 0) && deltas.Any(d => d < 0);
        var order = Enumerable.Range(0, deltas.Count).OrderBy(i => Math.Abs(deltas[i])).ToList();
        List<Path2>? previous = null;
        double previousDelta = 0;
        foreach (var i in order)
        {
            var d = deltas[i];
            if (previous != null && Math.Abs(d - previousDelta) < 1e-12)
            {
                results[i] = previous;
                continue;
            }
            results[i] = previous == null || mixedSigns
                ? Offset(paths, d, join, tolerance)
                : Offset(previous, d - previousDelta, join, tolerance, simplify: tolerance);
            previous = results[i];
            previousDelta = d;
        }
        return results.ToList();
    }

    /// <summary>Offsets an open path into a closed outline (e.g. for slots and engraving widths).</summary>
    public static List<Path2> OffsetOpen(Path2 path, double delta, double tolerance = Tolerance.Chord)
    {
        var flat = path.Flatten(tolerance);
        var src = new PathsD { new PathD(flat.Select(p => new PointD(p.X, p.Y))) };
        var res = Clipper.InflatePaths(src, delta, JoinType.Round, EndType.Round, 2.0, Precision, tolerance);
        return res.Select(p => FromClipper(p)).ToList();
    }

    /// <summary>
    /// One side of an open path, in its direction: round at outer corners, cut off at inner ones, and ending exactly
    /// beside the path's ends. It is the matching stretch of the band around the path. A negative distance gives the
    /// other side.
    /// </summary>
    public static List<Point2> OffsetOpenSide(Path2 path, double distance, bool right, double tolerance = Tolerance.Chord)
    {
        var flat = path.Flatten(tolerance);
        if (flat.Count < 2 || Math.Abs(distance) < 1e-9) return flat.ToList();
        if (distance < 0) (distance, right) = (-distance, !right);

        static Point2 Normal(Point2 a, Point2 b)
        {
            var d = (b - a).Normalized();
            return new Point2(d.Y, -d.X);
        }
        var sign = right ? 1 : -1;
        var startNormal = Normal(flat[0], flat[1]) * (sign * distance);
        var endNormal = Normal(flat[^2], flat[^1]) * (sign * distance);
        var (start, end) = (flat[0] + startNormal, flat[^1] + endNormal);
        var otherStart = flat[0] - startNormal;

        var src = new PathsD { new PathD(flat.Select(p => new PointD(p.X, p.Y))) };
        var band = Clipper.InflatePaths(src, distance, JoinType.Round, EndType.Butt, 2.0, Precision, JoinArcTolerance(tolerance))
            .OrderByDescending(p => Math.Abs(Clipper.Area(p))).FirstOrDefault();
        if (band == null || band.Count < 4) return [start, end];

        var pts = band.Select(p => new Point2(p.x, p.y)).ToList();
        int Nearest(Point2 target) => Enumerable.Range(0, pts.Count).MinBy(i => Point2.Distance(pts[i], target));
        var (from, to, avoid) = (Nearest(start), Nearest(end), Nearest(otherStart));

        // Of the two ways round the band, the one that does not pass the other side of the start.
        List<Point2> Walk(int step)
        {
            var result = new List<Point2>();
            for (var i = from; ; i = (i + step + pts.Count) % pts.Count)
            {
                if (i == avoid) return [];
                result.Add(pts[i]);
                if (i == to) return result;
            }
        }
        var side = Walk(1);
        if (side.Count == 0) side = Walk(-1);
        if (side.Count < 2) return [start, end];
        side[0] = start;
        side[^1] = end;
        return side;
    }

    /// <summary>Resolves self-overlaps / nesting with an even-odd union.</summary>
    private static PathsD Normalize(PathsD paths) =>
        Clipper.BooleanOp(ClipType.Union, paths, null, FillRule.EvenOdd, Precision);

    public static List<Path2> Union(IEnumerable<Path2> a, IEnumerable<Path2>? b = null) =>
        Boolean(ClipType.Union, a, b ?? []);

    public static List<Path2> Difference(IEnumerable<Path2> a, IEnumerable<Path2> b) => Boolean(ClipType.Difference, a, b);
    public static List<Path2> Intersection(IEnumerable<Path2> a, IEnumerable<Path2> b) => Boolean(ClipType.Intersection, a, b);
    public static List<Path2> Xor(IEnumerable<Path2> a, IEnumerable<Path2> b) => Boolean(ClipType.Xor, a, b);

    private static List<Path2> Boolean(ClipType type, IEnumerable<Path2> a, IEnumerable<Path2> b)
    {
        var res = Clipper.BooleanOp(type, ToClipper(a), ToClipper(b), FillRule.NonZero, Precision);
        return res.Select(p => FromClipper(p)).ToList();
    }

    /// <summary>
    /// Groups closed paths into shapes (outer boundary + holes) using even-odd nesting,
    /// which is how CAM pockets interpret nested outlines.
    /// </summary>
    public static List<Shape> BuildShapes(IEnumerable<Path2> paths, double tolerance = Tolerance.Chord)
    {
        var tree = new PolyTreeD();
        var clipper = new ClipperD(Precision);
        clipper.AddSubject(ToClipper(paths, tolerance));
        clipper.Execute(ClipType.Union, FillRule.EvenOdd, tree);
        var shapes = new List<Shape>();
        CollectShapes(tree, shapes);
        return shapes;
    }

    /// <summary>Same as <see cref="BuildShapes(IEnumerable{Path2}, double)"/> but for already-offset Clipper output.</summary>
    public static List<Shape> BuildShapes(PathsD paths)
    {
        var tree = new PolyTreeD();
        var clipper = new ClipperD(Precision);
        clipper.AddSubject(paths);
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        var shapes = new List<Shape>();
        CollectShapes(tree, shapes);
        return shapes;
    }

    private static void CollectShapes(PolyPathD node, List<Shape> shapes)
    {
        for (var i = 0; i < node.Count; i++)
        {
            var child = (PolyPathD)node[i];
            if (child.IsHole) continue;
            var shape = new Shape(EnsureOrientation(FromClipper(child.Polygon!), ccw: true));
            for (var h = 0; h < child.Count; h++)
            {
                var hole = (PolyPathD)child[h];
                shape.Holes.Add(EnsureOrientation(FromClipper(hole.Polygon!), ccw: false));
                // Islands inside holes become new shapes.
                CollectShapes(hole, shapes);
            }
            shapes.Add(shape);
        }
    }

    /// <summary>Offsets a shape (outer + holes) inward by <paramref name="distance"/>.</summary>
    /// <param name="simplify">If > 0, vertices that deviate less than this are dropped from the result. Keeps point
    /// counts stable when results are offset again (each offset adds join points at every vertex).</param>
    public static List<Shape> InsetShape(Shape shape, double distance, double tolerance = Tolerance.Chord, double simplify = 0)
    {
        var src = new PathsD { ToClipper(EnsureOrientation(shape.Outer, true), tolerance) };
        foreach (var h in shape.Holes) src.Add(ToClipper(EnsureOrientation(h, false), tolerance));
        var res = Clipper.InflatePaths(src, -distance, JoinType.Round, EndType.Polygon, 2.0, Precision, JoinArcTolerance(tolerance));
        if (simplify > 0) res = Clipper.SimplifyPaths(res, simplify, true);
        return BuildShapes(res);
    }

    private static double JoinArcTolerance(double tolerance) => Math.Max(tolerance, 2e-4);

    public static Path2 EnsureOrientation(Path2 p, bool ccw) => p.IsClockwise == !ccw ? p : p.Reversed();
}
