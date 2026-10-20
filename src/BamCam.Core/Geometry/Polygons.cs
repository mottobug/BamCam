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
        double tolerance = Tolerance.Chord, double z = 0)
    {
        var src = Normalize(ToClipper(paths, tolerance));
        var res = Clipper.InflatePaths(src, delta, Map(join), EndType.Polygon, 2.0, Precision, tolerance);
        return res.Select(p => FromClipper(p, z)).ToList();
    }

    /// <summary>Offsets an open path into a closed outline (e.g. for slots and engraving widths).</summary>
    public static List<Path2> OffsetOpen(Path2 path, double delta, double tolerance = Tolerance.Chord)
    {
        var flat = path.Flatten(tolerance);
        var src = new PathsD { new PathD(flat.Select(p => new PointD(p.X, p.Y))) };
        var res = Clipper.InflatePaths(src, delta, JoinType.Round, EndType.Round, 2.0, Precision, tolerance);
        return res.Select(p => FromClipper(p)).ToList();
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

    /// <summary>Same as <see cref="BuildShapes"/> but for already-offset Clipper output.</summary>
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
    public static List<Shape> InsetShape(Shape shape, double distance, double tolerance = Tolerance.Chord)
    {
        var src = new PathsD { ToClipper(EnsureOrientation(shape.Outer, true), tolerance) };
        foreach (var h in shape.Holes) src.Add(ToClipper(EnsureOrientation(h, false), tolerance));
        var res = Clipper.InflatePaths(src, -distance, JoinType.Round, EndType.Polygon, 2.0, Precision, tolerance);
        return BuildShapes(res);
    }

    public static Path2 EnsureOrientation(Path2 p, bool ccw) => p.IsClockwise == !ccw ? p : p.Reversed();
}
