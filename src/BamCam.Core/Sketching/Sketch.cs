using System.Text.Json;
using System.Text.Json.Serialization;
using BamCam.Core.Geometry;

namespace BamCam.Core.Sketching;

/// <summary>Kinds of sketch geometry.</summary>
public enum SketchGeometryKind
{
    /// <summary>A single point (Points = [p]).</summary>
    Point,
    /// <summary>A straight line (Points = [start, end]).</summary>
    Line,
    /// <summary>A full circle (Points = [centre]; its radius is <see cref="SketchGeometry.Radius"/>).</summary>
    Circle,
    /// <summary>An arc counter-clockwise from start to end (Points = [centre, start, end]).</summary>
    Arc,
}

/// <summary>Kinds of sketch constraints and dimensions; see <see cref="SketchConstraint"/> for what each refers to.</summary>
public enum ConstraintKind
{
    Coincident,
    Horizontal,
    Vertical,
    Parallel,
    Perpendicular,
    Tangent,
    Equal,
    Fix,
    PointOnObject,
    Midpoint,
    Symmetric,
    Distance,
    HorizontalDistance,
    VerticalDistance,
    Radius,
    Diameter,
    Angle,
}

/// <summary>A point of a sketch; lines, arcs and circles refer to their points by id.</summary>
public sealed class SketchPoint
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Never moved by the solver (the sketch origin).</summary>
    public bool Locked { get; set; }

    [JsonIgnore] public Point2 Position
    {
        get => new(X, Y);
        set { X = value.X; Y = value.Y; }
    }
}

/// <summary>A point, line, circle or arc of a sketch.</summary>
public sealed class SketchGeometry
{
    public int Id { get; set; }
    public SketchGeometryKind Kind { get; set; }

    /// <summary>Ids of its points: point [p], line [start, end], circle [centre], arc [centre, start, end].</summary>
    public int[] Points { get; set; } = [];

    /// <summary>Radius of a circle (arcs take it from their points).</summary>
    public double Radius { get; set; }

    /// <summary>Construction geometry helps with constraints but is not part of the outlines.</summary>
    public bool Construction { get; set; }
}

/// <summary>
/// A geometric constraint or a dimension. <see cref="Refs"/> holds ids of points and geometry:
/// <list type="bullet">
/// <item>Coincident [point, point]; Fix [point] with Data = [x, y]</item>
/// <item>Horizontal, Vertical [line] or [point, point]</item>
/// <item>Parallel, Perpendicular, Angle [line, line]</item>
/// <item>Tangent [line or circle or arc, circle or arc]; Data = sign of the side (line) or [external +1 / internal −1, sign of r1 − r2]</item>
/// <item>Equal [line, line] (lengths) or [circle/arc, circle/arc] (radii)</item>
/// <item>PointOnObject [point, line/circle/arc]; Midpoint [point, line]; Symmetric [point, point, line]</item>
/// <item>Distance [point, point], [point, line] (Data = side) or [line]; HorizontalDistance, VerticalDistance
/// [point, point] or [line] (Data = direction)</item>
/// <item>Radius, Diameter [circle or arc]</item>
/// </list>
/// Dimensions take their value from <see cref="Expression"/> (a number or a formula of the sketch's variables);
/// a reference dimension only shows the measured value.
/// </summary>
public sealed class SketchConstraint
{
    public int Id { get; set; }
    public ConstraintKind Kind { get; set; }
    public int[] Refs { get; set; } = [];
    public double[] Data { get; set; } = [];

    /// <summary>Value of a dimension: a number or a formula ("Width / 2"). Angles in degrees.</summary>
    public string? Expression { get; set; }

    /// <summary>A reference dimension shows the measured value and does not drive the geometry.</summary>
    public bool Reference { get; set; }

    /// <summary>Where the label of a dimension sits, relative to the middle of what it measures.</summary>
    public double LabelX { get; set; }
    public double LabelY { get; set; }

    [JsonIgnore] public bool IsDimension => Kind is ConstraintKind.Distance or ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance
        or ConstraintKind.Radius or ConstraintKind.Diameter or ConstraintKind.Angle;
}

/// <summary>A named value of a sketch that dimensions can use in their formulas.</summary>
public sealed class SketchVariable
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "0";
}

/// <summary>
/// A 2D sketch: points, lines, circles and arcs held in place by geometric constraints and driving dimensions, solved
/// by <see cref="SketchSolver"/>. Coordinates are in drawing units in the sketch's own coordinates.
/// </summary>
public sealed class Sketch
{
    public const int OriginId = 1;

    public List<SketchPoint> Points { get; set; } = [];
    public List<SketchGeometry> Geometry { get; set; } = [];
    public List<SketchConstraint> Constraints { get; set; } = [];
    public List<SketchVariable> Variables { get; set; } = [];
    public int NextId { get; set; } = OriginId + 1;

    /// <summary>A new sketch with its origin point (locked at 0, 0).</summary>
    public static Sketch CreateNew()
    {
        var s = new Sketch();
        s.Points.Add(new SketchPoint { Id = OriginId, Locked = true });
        s.Geometry.Add(new SketchGeometry { Id = s.NextId++, Kind = SketchGeometryKind.Point, Points = [OriginId], Construction = true });
        return s;
    }

    // ---------------------------------------------------------------- lookup

    public SketchPoint? Point(int id) => Points.FirstOrDefault(p => p.Id == id);
    public SketchGeometry? Geo(int id) => Geometry.FirstOrDefault(g => g.Id == id);
    public SketchConstraint? Constraint(int id) => Constraints.FirstOrDefault(c => c.Id == id);
    public bool IsPoint(int id) => Points.Any(p => p.Id == id);

    public Point2 P(int id) => Point(id)?.Position ?? default;

    /// <summary>The point id of a sketch geometry of kind Point, else the id itself (for constraints taking points).</summary>
    public int PointOf(int id) => Geo(id) is { Kind: SketchGeometryKind.Point } g ? g.Points[0] : id;

    /// <summary>Centre and radius of a circle or arc.</summary>
    public (Point2 Center, double Radius) CircleOf(SketchGeometry g) =>
        g.Kind == SketchGeometryKind.Circle ? (P(g.Points[0]), g.Radius) : (P(g.Points[0]), Point2.Distance(P(g.Points[0]), P(g.Points[1])));

    // ---------------------------------------------------------------- building

    public SketchPoint AddPoint(Point2 p)
    {
        var point = new SketchPoint { Id = NextId++, X = p.X, Y = p.Y };
        Points.Add(point);
        return point;
    }

    public SketchGeometry AddLine(Point2 a, Point2 b, bool construction = false)
    {
        var g = new SketchGeometry { Id = NextId++, Kind = SketchGeometryKind.Line, Points = [AddPoint(a).Id, AddPoint(b).Id], Construction = construction };
        Geometry.Add(g);
        return g;
    }

    public SketchGeometry AddCircle(Point2 center, double radius, bool construction = false)
    {
        var g = new SketchGeometry { Id = NextId++, Kind = SketchGeometryKind.Circle, Points = [AddPoint(center).Id], Radius = radius, Construction = construction };
        Geometry.Add(g);
        return g;
    }

    /// <summary>Arc counter-clockwise from <paramref name="start"/> to <paramref name="end"/> (the end is moved onto the circle).</summary>
    public SketchGeometry AddArc(Point2 center, Point2 start, Point2 end, bool construction = false)
    {
        var r = Point2.Distance(center, start);
        var e = center + (end - center).Normalized() * r;
        var g = new SketchGeometry
        {
            Id = NextId++, Kind = SketchGeometryKind.Arc,
            Points = [AddPoint(center).Id, AddPoint(start).Id, AddPoint(e).Id], Construction = construction,
        };
        Geometry.Add(g);
        return g;
    }

    public SketchGeometry AddPointGeometry(Point2 p, bool construction = false)
    {
        var g = new SketchGeometry { Id = NextId++, Kind = SketchGeometryKind.Point, Points = [AddPoint(p).Id], Construction = construction };
        Geometry.Add(g);
        return g;
    }

    public SketchConstraint AddConstraint(ConstraintKind kind, int[] refs, double[]? data = null, string? expression = null)
    {
        var c = new SketchConstraint { Id = NextId++, Kind = kind, Refs = refs, Data = data ?? [], Expression = expression };
        Constraints.Add(c);
        return c;
    }

    /// <summary>
    /// Removes geometry or constraints by id, with the constraints that refer to removed things and the points no
    /// longer used by any geometry. The origin stays.
    /// </summary>
    public void Remove(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        set.Remove(OriginId);
        Geometry.RemoveAll(g => set.Contains(g.Id) && !g.Points.Contains(OriginId));
        var usedPoints = Geometry.SelectMany(g => g.Points).Append(OriginId).ToHashSet();
        Points.RemoveAll(p => !usedPoints.Contains(p.Id));
        var alive = Points.Select(p => p.Id).Concat(Geometry.Select(g => g.Id)).ToHashSet();
        Constraints.RemoveAll(c => set.Contains(c.Id) || c.Refs.Any(r => !alive.Contains(r)));
    }

    /// <summary>The geometry that uses a point.</summary>
    public IEnumerable<SketchGeometry> GeometryOf(int pointId) => Geometry.Where(g => g.Points.Contains(pointId));

    // ---------------------------------------------------------------- measuring

    /// <summary>The current value of a dimension (angles in degrees), from the positions as they are.</summary>
    public double Measure(SketchConstraint c)
    {
        var r = c.Refs;
        switch (c.Kind)
        {
            case ConstraintKind.Distance when r.Length == 1 && Geo(r[0]) is { Kind: SketchGeometryKind.Line } l:
                return Point2.Distance(P(l.Points[0]), P(l.Points[1]));
            case ConstraintKind.Distance when r.Length == 2 && Geo(r[1]) is { Kind: SketchGeometryKind.Line } line:
                return Math.Abs(SignedDistance(P(PointOf(r[0])), P(line.Points[0]), P(line.Points[1])));
            case ConstraintKind.Distance:
                return Point2.Distance(P(PointOf(r[0])), P(PointOf(r[1])));
            case ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance:
            {
                var (a, b) = Ends(r);
                var d = c.Kind == ConstraintKind.HorizontalDistance ? b.X - a.X : b.Y - a.Y;
                return Math.Abs(d);
            }
            case ConstraintKind.Radius when Geo(r[0]) is { } g:
                return CircleOf(g).Radius;
            case ConstraintKind.Diameter when Geo(r[0]) is { } g:
                return 2 * CircleOf(g).Radius;
            case ConstraintKind.Angle when Geo(r[0]) is { } l1 && Geo(r[1]) is { } l2:
            {
                var d1 = P(l1.Points[1]) - P(l1.Points[0]);
                var d2 = P(l2.Points[1]) - P(l2.Points[0]);
                var a = Math.Atan2(d1.X * d2.Y - d1.Y * d2.X, Point2.Dot(d1, d2)) * 180 / Math.PI;
                return Math.Abs(a);
            }
        }
        return 0;
    }

    /// <summary>The two points a horizontal / vertical distance or a distance between points refers to.</summary>
    public (Point2 A, Point2 B) Ends(int[] refs) =>
        refs.Length == 1 && Geo(refs[0]) is { Kind: SketchGeometryKind.Line } l ? (P(l.Points[0]), P(l.Points[1])) : (P(PointOf(refs[0])), P(PointOf(refs[1])));

    /// <summary>Distance of <paramref name="p"/> from the line through a and b, positive on its left.</summary>
    public static double SignedDistance(Point2 p, Point2 a, Point2 b)
    {
        var d = b - a;
        var len = d.Length;
        return len < 1e-12 ? Point2.Distance(p, a) : (d.X * (p.Y - a.Y) - d.Y * (p.X - a.X)) / len;
    }

    // ---------------------------------------------------------------- outlines

    /// <summary>
    /// The outlines of the sketch (without construction geometry and points): lines and arcs that meet at their ends
    /// joined into polylines, circles as closed paths.
    /// </summary>
    public List<Path2> Outlines()
    {
        var pieces = new List<Path2>();
        var result = new List<Path2>();
        foreach (var g in Geometry.Where(g => !g.Construction))
        {
            switch (g.Kind)
            {
                case SketchGeometryKind.Line:
                    if (Point2.Distance(P(g.Points[0]), P(g.Points[1])) > 1e-9)
                        pieces.Add(Path2.FromPoints([P(g.Points[0]), P(g.Points[1])], false));
                    break;
                case SketchGeometryKind.Arc:
                {
                    var c = P(g.Points[0]);
                    var s = P(g.Points[1]);
                    var e = P(g.Points[2]);
                    var sweep = ArcSweep(c, s, e);
                    if (sweep < 1e-9) break;
                    // At most a half circle per segment, so the bulge stays well conditioned.
                    var parts = sweep > Math.PI + 1e-9 ? 2 : 1;
                    var r = Point2.Distance(c, s);
                    var a0 = (s - c).Angle;
                    var v = new List<Vertex>();
                    for (var k = 0; k < parts; k++)
                        v.Add(new Vertex((c + Point2.FromAngle(a0 + sweep * k / parts, r)).ToPoint3(), Math.Tan(sweep / parts / 4)));
                    v.Add(new Vertex(e.ToPoint3()));
                    pieces.Add(new Path2(v, false));
                    break;
                }
                case SketchGeometryKind.Circle when g.Radius > 1e-9:
                {
                    var c = P(g.Points[0]);
                    result.Add(new Path2([new Vertex((c + new Point2(g.Radius, 0)).ToPoint3(), 1), new Vertex((c - new Point2(g.Radius, 0)).ToPoint3(), 1)], true));
                    break;
                }
            }
        }
        result.InsertRange(0, JoinPieces(pieces, 1e-6));
        return result;
    }

    /// <summary>The path of one geometry (a point gives an empty path), in sketch coordinates.</summary>
    public Path2 PathOf(SketchGeometry g)
    {
        switch (g.Kind)
        {
            case SketchGeometryKind.Line:
                return Path2.FromPoints([P(g.Points[0]), P(g.Points[1])], false);
            case SketchGeometryKind.Circle:
            {
                var c = P(g.Points[0]);
                var r = Math.Max(g.Radius, 1e-9);
                return new Path2([new Vertex((c + new Point2(r, 0)).ToPoint3(), 1), new Vertex((c - new Point2(r, 0)).ToPoint3(), 1)], true);
            }
            case SketchGeometryKind.Arc:
            {
                var c = P(g.Points[0]);
                var s = P(g.Points[1]);
                var sweep = ArcSweep(c, s, P(g.Points[2]));
                var r = Point2.Distance(c, s);
                var a0 = (s - c).Angle;
                var parts = sweep > Math.PI + 1e-9 ? 2 : 1;
                var v = new List<Vertex>();
                for (var k = 0; k < parts; k++)
                    v.Add(new Vertex((c + Point2.FromAngle(a0 + sweep * k / parts, r)).ToPoint3(), Math.Tan(sweep / parts / 4)));
                v.Add(new Vertex((c + Point2.FromAngle(a0 + sweep, r)).ToPoint3()));
                return new Path2(v, false);
            }
            default:
                return new Path2([], false);
        }
    }

    /// <summary>Distance of a point from a geometry (its line segment, circle, arc or point).</summary>
    public double DistanceTo(SketchGeometry g, Point2 p)
    {
        switch (g.Kind)
        {
            case SketchGeometryKind.Point:
                return Point2.Distance(p, P(g.Points[0]));
            case SketchGeometryKind.Line:
            {
                var a = P(g.Points[0]);
                var d = P(g.Points[1]) - a;
                var len2 = Point2.Dot(d, d);
                var t = len2 < 1e-24 ? 0 : Math.Clamp(Point2.Dot(p - a, d) / len2, 0, 1);
                return Point2.Distance(p, a + d * t);
            }
            case SketchGeometryKind.Circle:
                return Math.Abs(Point2.Distance(p, P(g.Points[0])) - g.Radius);
            case SketchGeometryKind.Arc:
            {
                var c = P(g.Points[0]);
                var s = P(g.Points[1]);
                var e = P(g.Points[2]);
                var turned = Angles.Normalize((p - c).Angle - (s - c).Angle);
                if (turned <= ArcSweep(c, s, e)) return Math.Abs(Point2.Distance(p, c) - Point2.Distance(s, c));
                return Math.Min(Point2.Distance(p, s), Point2.Distance(p, e));
            }
        }
        return double.MaxValue;
    }

    /// <summary>Counter-clockwise sweep from start to end around the centre, in (0, 2π].</summary>
    public static double ArcSweep(Point2 c, Point2 s, Point2 e)
    {
        var sweep = Angles.Normalize((e - c).Angle - (s - c).Angle);
        return sweep < 1e-12 ? 2 * Math.PI : sweep;
    }

    private static List<Path2> JoinPieces(List<Path2> pieces, double tol)
    {
        var open = pieces.ToList();
        var chains = new List<Path2>();
        static bool Near(Point3 a, Point3 b, double t) => Point2.Distance(a.XY, b.XY) <= t;
        static Path2 Concat(Path2 a, Path2 b)
        {
            var v = new List<Vertex>(a.Vertices);
            v[^1] = b.Vertices[0] with { Point = v[^1].Point };
            v.AddRange(b.Vertices.Skip(1));
            return new Path2(v, false);
        }
        while (open.Count > 0)
        {
            var chain = open[0];
            open.RemoveAt(0);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < open.Count; i++)
                {
                    var o = open[i];
                    Path2? merged = null;
                    if (Near(chain.EndPoint, o.StartPoint, tol)) merged = Concat(chain, o);
                    else if (Near(chain.EndPoint, o.EndPoint, tol)) merged = Concat(chain, o.Reversed());
                    else if (Near(chain.StartPoint, o.EndPoint, tol)) merged = Concat(o, chain);
                    else if (Near(chain.StartPoint, o.StartPoint, tol)) merged = Concat(o.Reversed(), chain);
                    if (merged == null) continue;
                    chain = merged;
                    open.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            if (chain.SegmentCount > 1 && Near(chain.StartPoint, chain.EndPoint, tol))
            {
                chain.Vertices.RemoveAt(chain.Count - 1);
                chain.Closed = true;
            }
            chains.Add(chain);
        }
        return chains;
    }

    // ---------------------------------------------------------------- saving

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>The sketch from its saved text; an empty new sketch when the text cannot be read.</summary>
    public static Sketch Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return CreateNew();
        try
        {
            var s = JsonSerializer.Deserialize<Sketch>(text, Json) ?? CreateNew();
            if (s.Point(OriginId) == null) s.Points.Insert(0, new SketchPoint { Id = OriginId, Locked = true });
            return s;
        }
        catch (JsonException)
        {
            return CreateNew();
        }
    }

    public Sketch Clone() => Deserialize(Serialize());
}
