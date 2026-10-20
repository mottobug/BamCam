namespace BamCam.Core.Geometry;

/// <summary>Polyline vertex. <see cref="Bulge"/> describes the segment from this vertex to the next.</summary>
public readonly record struct Vertex(Point3 Point, double Bulge = 0)
{
    public Point2 XY => Point.XY;
}

/// <summary>A segment of a <see cref="Path2"/>: either a straight line or an arc.</summary>
public readonly record struct Segment(Point3 Start, Point3 End, double Bulge)
{
    public bool IsArc => Math.Abs(Bulge) > 1e-12 && !Start.XY.AlmostEquals(End.XY);
    public Arc2 ToArc() => Arc2.FromBulge(Start.XY, End.XY, Bulge);

    public double Length => IsArc ? ToArc().Length : Point3.Distance(Start, End);
}

/// <summary>
/// Open or closed polyline made of line and arc segments (bulge encoding).
/// This is the geometric workhorse used by CAD entities and toolpath generation.
/// </summary>
public sealed class Path2
{
    public List<Vertex> Vertices { get; } = [];
    public bool Closed { get; set; }

    public Path2() { }

    public Path2(IEnumerable<Vertex> vertices, bool closed)
    {
        Vertices.AddRange(vertices);
        Closed = closed;
    }

    public static Path2 FromPoints(IEnumerable<Point2> pts, bool closed, double z = 0) =>
        new(pts.Select(p => new Vertex(p.ToPoint3(z))), closed);

    public int Count => Vertices.Count;

    public int SegmentCount => Vertices.Count < 2 ? 0 : Closed ? Vertices.Count : Vertices.Count - 1;

    public IEnumerable<Segment> Segments()
    {
        for (var i = 0; i < SegmentCount; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % Vertices.Count];
            yield return new Segment(a.Point, b.Point, a.Bulge);
        }
    }

    public Point3 StartPoint => Vertices[0].Point;
    public Point3 EndPoint => Closed ? Vertices[0].Point : Vertices[^1].Point;

    public double Length => Segments().Sum(s => s.Length);

    /// <summary>Signed area (positive = counter-clockwise). Arc segments are included exactly.</summary>
    public double SignedArea()
    {
        if (!Closed || Vertices.Count < 2) return 0;
        double area = 0;
        foreach (var s in Segments())
        {
            area += (s.Start.X * s.End.Y - s.End.X * s.Start.Y) / 2;
            if (s.IsArc)
            {
                var arc = s.ToArc();
                // Circular segment area between chord and arc.
                var seg = arc.Radius * arc.Radius / 2 * (Math.Abs(arc.Sweep) - Math.Sin(Math.Abs(arc.Sweep)));
                area += Math.Sign(arc.Sweep) * seg;
            }
        }
        return area;
    }

    public bool IsClockwise => SignedArea() < 0;

    public Path2 Reversed()
    {
        var n = Vertices.Count;
        var result = new Path2 { Closed = Closed };
        if (n == 0) return result;
        if (Closed)
        {
            // Segment i (v[i] -> v[i+1]) becomes v[i+1] -> v[i] with negated bulge.
            for (var k = 0; k < n; k++)
            {
                var idx = (n - k) % n;
                var prev = (idx - 1 + n) % n;
                result.Vertices.Add(new Vertex(Vertices[idx].Point, -Vertices[prev].Bulge));
            }
        }
        else
        {
            for (var k = n - 1; k >= 0; k--)
            {
                var bulge = k > 0 ? -Vertices[k - 1].Bulge : 0;
                result.Vertices.Add(new Vertex(Vertices[k].Point, bulge));
            }
        }
        return result;
    }

    public Path2 Clone() => new(Vertices, Closed);

    public Path2 Transformed(Matrix4 m)
    {
        var flip = m.DeterminantXY < 0;
        return new Path2(Vertices.Select(v => new Vertex(m.Transform(v.Point), flip ? -v.Bulge : v.Bulge)), Closed);
    }

    public Path2 WithZ(double z) => new(Vertices.Select(v => v with { Point = v.Point with { Z = z } }), Closed);

    /// <summary>Approximates arcs with line segments.</summary>
    public List<Point2> Flatten(double tolerance = Tolerance.Chord)
    {
        var pts = new List<Point2>();
        if (Vertices.Count == 0) return pts;
        pts.Add(Vertices[0].XY);
        foreach (var s in Segments())
        {
            if (s.IsArc) pts.AddRange(s.ToArc().Flatten(tolerance, includeStart: false));
            else pts.Add(s.End.XY);
        }
        if (Closed && pts.Count > 1 && pts[^1].AlmostEquals(pts[0])) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    public BoundingBox Bounds()
    {
        var b = BoundingBox.Empty;
        foreach (var v in Vertices) b = b.Include(v.Point);
        foreach (var s in Segments())
            if (s.IsArc) b = b.Union(s.ToArc().Bounds());
        return b;
    }

    /// <summary>Point-in-polygon test (even-odd) against the flattened outline.</summary>
    public bool Contains(Point2 p, double tolerance = Tolerance.Chord)
    {
        if (!Closed) return false;
        var pts = Flatten(tolerance);
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y) &&
                p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Shortest distance from <paramref name="p"/> to the outline.</summary>
    public double DistanceTo(Point2 p)
    {
        var best = double.PositiveInfinity;
        foreach (var s in Segments())
        {
            double d;
            if (s.IsArc)
            {
                var arc = s.ToArc();
                var ang = (p - arc.Center).Angle;
                d = arc.ContainsAngle(ang)
                    ? Math.Abs(Point2.Distance(p, arc.Center) - arc.Radius)
                    : Math.Min(Point2.Distance(p, arc.Start), Point2.Distance(p, arc.End));
            }
            else
            {
                d = DistanceToSegment(p, s.Start.XY, s.End.XY);
            }
            best = Math.Min(best, d);
        }
        if (Vertices.Count == 1) best = Point2.Distance(p, Vertices[0].XY);
        return best;
    }

    public static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 < Tolerance.Epsilon) return Point2.Distance(p, a);
        var t = Math.Clamp(Point2.Dot(p - a, ab) / len2, 0, 1);
        return Point2.Distance(p, a + ab * t);
    }

    /// <summary>Rotates the start of a closed path to the vertex nearest <paramref name="p"/>.</summary>
    public Path2 StartNearest(Point2 p)
    {
        if (!Closed || Vertices.Count < 2) return Clone();
        var idx = 0;
        var best = double.PositiveInfinity;
        for (var i = 0; i < Vertices.Count; i++)
        {
            var d = Point2.Distance(Vertices[i].XY, p);
            if (d < best) { best = d; idx = i; }
        }
        return new Path2(Vertices.Skip(idx).Concat(Vertices.Take(idx)), true);
    }
}
