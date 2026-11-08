// Part of the port of matmill (https://github.com/jkmnt/matmill), MIT License, Copyright (c) 2016 jkmnt.
// See LICENSE-matmill.txt. Stands in for what matmill used of CamBam's Polyline, on top of BamCam's Path2.
#nullable disable

using BamCam.Core.Geometry;

namespace Matmill;

/// <summary>A polyline of lines and arcs (a BamCam <see cref="Path2"/>) with what matmill asks of it.</summary>
internal sealed class Contour
{
    private readonly List<object> _segments = []; // Line2F or Arc2F
    private readonly List<double> _offsets = [];  // length up to the start of each segment
    private List<Point2> _flat;

    public Path2 Path { get; }
    public bool Closed => Path.Closed;
    public double Length { get; }

    public Contour(Path2 path)
    {
        Path = path;
        var length = 0.0;
        foreach (var s in path.Segments())
        {
            if (s.Start.XY.AlmostEquals(s.End.XY, 1e-12)) continue;
            object seg = s.IsArc
                ? new Arc2F(s.ToArc().Center, s.Start.XY, s.End.XY, s.Bulge > 0 ? RotationDirection.CCW : RotationDirection.CW)
                : new Line2F(s.Start.XY, s.End.XY);
            _offsets.Add(length);
            _segments.Add(seg);
            length += s.Length;
        }
        Length = length;
    }

    public int NumSegments => _segments.Count;

    public object GetSegment(int i) => _segments[i];

    public Point2 FirstPoint => Path.StartPoint.XY;

    public Point2 LastPoint => Path.EndPoint.XY;

    private List<Point2> Flat => _flat ??= Path.Flatten(1e-4);

    public void GetExtrema(ref Point2 min, ref Point2 max)
    {
        var b = Path.Bounds();
        min = b.Min.XY;
        max = b.Max.XY;
    }

    /// <summary>Inside the closed polyline (even-odd); a point on it counts as inside within the tolerance.</summary>
    public bool PointInPolyline(Point2 p, double tolerance)
    {
        var pts = Flat;
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if (Path2.DistanceToSegment(p, pts[j], pts[i]) <= tolerance) return true;
            if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y) &&
                p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Whether the line crosses the polyline anywhere away from its own ends.</summary>
    public bool LineIntersects(Line2F line, double tolerance)
    {
        var pts = Flat;
        var n = Closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (Crossing(line.p1, line.p2, a, b) is not { } x) continue;
            if (x.DistanceTo(line.p1) <= tolerance || x.DistanceTo(line.p2) <= tolerance) continue;
            return true;
        }
        return false;
    }

    private static Point2? Crossing(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var r = b - a;
        var s = d - c;
        var denominator = Point2.Cross(r, s);
        if (Math.Abs(denominator) < 1e-18) return null;
        var t = Point2.Cross(c - a, s) / denominator;
        var u = Point2.Cross(c - a, r) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? a + r * t : null;
    }

    /// <summary>The point at a distance along the polyline from its start.</summary>
    public Point2 PointAt(double distance)
    {
        if (_segments.Count == 0) return FirstPoint;
        distance = Math.Clamp(distance, 0, Length);
        var i = _offsets.BinarySearch(distance);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, _segments.Count - 1);
        var along = distance - _offsets[i];
        switch (_segments[i])
        {
            case Line2F line:
            {
                var len = line.Length();
                return len <= 0 ? line.p1 : line.p1 + (line.p2 - line.p1) * (along / len);
            }
            case Arc2F arc:
            {
                var a = arc.ToArc2();
                return a.Length <= 0 ? arc.P1 : a.PointAt(along / a.Length);
            }
            default:
                throw new InvalidOperationException("unknown segment type");
        }
    }

    /// <summary>
    /// The polyline divided into <paramref name="nsegs"/> parts of equal length: their ends (a closed polyline does
    /// not repeat its start at the end).
    /// </summary>
    public List<Point2> Divide(int nsegs)
    {
        var points = new List<Point2>(nsegs + 1);
        var last = Closed ? nsegs - 1 : nsegs;
        for (var k = 0; k <= last; k++) points.Add(PointAt(Length * k / nsegs));
        return points;
    }

    /// <summary>Points every <paramref name="step"/> along the polyline from its start (not its end).</summary>
    public List<Point2> DivideByStep(double step)
    {
        var points = new List<Point2>();
        for (var d = 0.0; d < Length - 1e-12; d += step) points.Add(PointAt(d));
        if (points.Count == 0) points.Add(FirstPoint);
        return points;
    }
}
