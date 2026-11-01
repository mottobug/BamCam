using BamCam.Core.Geometry;

namespace BamCam.Core.Cad;

/// <summary>
/// Extend and trim open paths (lines, arcs, open polylines) against boundary paths: extending moves the end nearest
/// to the click along the line or around the arc up to the next boundary; trimming removes the clicked piece between
/// the neighbouring boundary crossings (or the path end).
/// </summary>
public static class TrimExtend
{
    /// <summary>Position on a path: segment index plus 0 … 1 within it (by length for lines, by angle for arcs).</summary>
    private readonly record struct Position(int Segment, double T) : IComparable<Position>
    {
        public int CompareTo(Position other) => Segment != other.Segment ? Segment.CompareTo(other.Segment) : T.CompareTo(other.T);
        public double Value => Segment + T;
    }

    /// <summary>
    /// Extends the end of the open <paramref name="path"/> nearest to <paramref name="click"/> to the nearest crossing
    /// with a boundary beyond it; null when nothing lies in the way.
    /// </summary>
    public static Path2? Extend(Path2 path, Point2 click, IReadOnlyList<Path2> boundaries)
    {
        if (path.Closed || path.SegmentCount == 0) return null;
        var atEnd = Point2.Distance(click, path.EndPoint.XY) < Point2.Distance(click, path.StartPoint.XY);
        var work = atEnd ? path : path.Reversed();
        var last = work.Segments().Last();
        var end = last.End.XY;
        var boundarySegments = boundaries.SelectMany(b => b.Segments()).ToList();

        var vertices = work.Vertices.ToList();
        if (!last.IsArc)
        {
            var dir = (end - last.Start.XY).Normalized();
            var ray = new Segment(end.ToPoint3(), (end + dir * 1e6).ToPoint3(), 0);
            var hit = boundarySegments.SelectMany(s => ObjectSnap.Intersections(ray, s))
                .Where(p => Point2.Distance(p, end) > 1e-7).OrderBy(p => Point2.Distance(p, end)).Cast<Point2?>().FirstOrDefault();
            if (hit is not { } h) return null;
            vertices[^1] = vertices[^1] with { Point = h.ToPoint3(last.End.Z) };
        }
        else
        {
            // Around the circle in the arc's direction, at most up to its start.
            var arc = last.ToArc();
            var remaining = Math.Sign(arc.Sweep) * (2 * Math.PI - Math.Abs(arc.Sweep)) * 0.999;
            var rest = new Arc2(arc.Center, arc.Radius, arc.EndAngle, remaining);
            var halves = new[] { (0.0, 0.5), (0.5, 1.0) }.Select(h => new Segment(
                rest.PointAt(h.Item1).ToPoint3(), rest.PointAt(h.Item2).ToPoint3(), Math.Tan(rest.Sweep / 2 / 4))).ToList();
            double? best = null;
            foreach (var half in halves)
            foreach (var s in boundarySegments)
            foreach (var p in ObjectSnap.Intersections(half, s))
            {
                var turned = Angles.Normalize((p - arc.Center).Angle - arc.EndAngle);
                if (arc.Sweep < 0) turned = Angles.Normalize(-turned);
                if (turned > 1e-7 && (best == null || turned < best)) best = turned;
            }
            if (best is not { } extra) return null;
            var sweep = arc.Sweep + Math.Sign(arc.Sweep) * extra;
            var newEnd = arc.Center + Point2.FromAngle(arc.StartAngle + sweep, arc.Radius);
            if (Math.Abs(sweep) >= 2 * Math.PI - 1e-6) return null;
            vertices[^2] = vertices[^2] with { Bulge = Math.Tan(sweep / 4) };
            vertices[^1] = vertices[^1] with { Point = newEnd.ToPoint3(last.End.Z) };
        }
        var result = new Path2(vertices, false);
        return atEnd ? result : result.Reversed();
    }

    /// <summary>
    /// Removes the piece of the open <paramref name="path"/> around <paramref name="click"/> that lies between two
    /// boundary crossings (or a crossing and the path end). Returns the remaining pieces (0, 1 or 2); null when the
    /// path does not cross any boundary.
    /// </summary>
    public static List<Path2>? Trim(Path2 path, Point2 click, IReadOnlyList<Path2> boundaries)
    {
        if (path.Closed || path.SegmentCount == 0) return null;
        var segments = path.Segments().ToList();
        var boundarySegments = boundaries.SelectMany(b => b.Segments()).ToList();
        var cuts = new List<Position>();
        for (var i = 0; i < segments.Count; i++)
        foreach (var s in boundarySegments)
        foreach (var p in ObjectSnap.Intersections(segments[i], s))
            cuts.Add(new Position(i, ParameterOf(segments[i], p)));
        var start = new Position(0, 0);
        var finish = new Position(segments.Count - 1, 1);
        // Crossings at the very ends do not divide the path.
        cuts = cuts.Where(c => c.Value > start.Value + 1e-7 && c.Value < finish.Value - 1e-7).Distinct().Order().ToList();
        if (cuts.Count == 0) return null;

        var at = Nearest(segments, click);
        var before = cuts.Where(c => c.CompareTo(at) < 0).Select(c => (Position?)c).LastOrDefault();
        var after = cuts.Where(c => c.CompareTo(at) > 0).Select(c => (Position?)c).FirstOrDefault();
        var pieces = new List<Path2>();
        if (before is { } b) pieces.Add(Piece(segments, start, b));
        if (after is { } a) pieces.Add(Piece(segments, a, finish));
        return pieces;
    }

    /// <summary>Position on the segment of a point lying on it.</summary>
    private static double ParameterOf(Segment s, Point2 p)
    {
        if (!s.IsArc)
        {
            var d = s.End.XY - s.Start.XY;
            return Math.Clamp(Point2.Dot(p - s.Start.XY, d) / Point2.Dot(d, d), 0, 1);
        }
        var arc = s.ToArc();
        var turned = Angles.Normalize((p - arc.Center).Angle - arc.StartAngle);
        if (arc.Sweep < 0) turned = Angles.Normalize(-turned);
        return Math.Clamp(turned / Math.Abs(arc.Sweep), 0, 1);
    }

    /// <summary>Position on the path nearest to a point.</summary>
    private static Position Nearest(List<Segment> segments, Point2 p)
    {
        var best = new Position(0, 0);
        var bestDistance = double.MaxValue;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            double t;
            if (s.IsArc)
            {
                var arc = s.ToArc();
                // Nearest point on the arc: the projection onto the circle if the arc passes there, else an end.
                var onCircle = arc.Center + (p - arc.Center).Normalized() * arc.Radius;
                t = arc.ContainsAngle((p - arc.Center).Angle) ? ParameterOf(s, onCircle)
                    : Point2.Distance(p, arc.Start) < Point2.Distance(p, arc.End) ? 0 : 1;
            }
            else
            {
                t = ParameterOf(s, p);
            }
            var d = Point2.Distance(p, PointAt(s, t));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = new Position(i, t);
            }
        }
        return best;
    }

    private static Point2 PointAt(Segment s, double t) =>
        s.IsArc ? s.ToArc().PointAt(t) : s.Start.XY + (s.End.XY - s.Start.XY) * t;

    /// <summary>The part of the path between two positions as an open path.</summary>
    private static Path2 Piece(List<Segment> segments, Position from, Position to)
    {
        var vertices = new List<Vertex>();
        for (var i = from.Segment; i <= to.Segment; i++)
        {
            var s = segments[i];
            var t0 = i == from.Segment ? from.T : 0;
            var t1 = i == to.Segment ? to.T : 1;
            if (t1 - t0 < 1e-12) continue;
            var bulge = s.IsArc ? Math.Tan(s.ToArc().Sweep * (t1 - t0) / 4) : 0;
            vertices.Add(new Vertex(PointAt(s, t0).ToPoint3(s.Start.Z), bulge));
        }
        var lastSegment = segments[to.Segment];
        vertices.Add(new Vertex(PointAt(lastSegment, to.T).ToPoint3(lastSegment.End.Z)));
        return new Path2(vertices, false);
    }
}
