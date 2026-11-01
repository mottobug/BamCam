using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>Kinds of object snap points (flags: each can be switched on and off).</summary>
[Flags]
public enum SnapKinds
{
    None = 0,
    /// <summary>End points of lines, arcs and polyline segments; points of point lists.</summary>
    EndPoint = 1,
    /// <summary>Middle of lines, arcs and polyline segments.</summary>
    MidPoint = 2,
    /// <summary>Centre of circles, arcs and arc segments.</summary>
    Center = 4,
    /// <summary>0°, 90°, 180° and 270° points of circles (and of arcs where the arc passes them).</summary>
    Quadrant = 8,
    /// <summary>Crossings of two drawing objects (or of two segments of one object).</summary>
    Intersection = 16,
    All = EndPoint | MidPoint | Center | Quadrant | Intersection,
}

/// <summary>A snap point and the kind of point it is (for the marker shape).</summary>
public readonly record struct SnapPoint(Point2 Point, SnapKinds Kind);

/// <summary>Object snap: the characteristic points of drawing objects near the cursor.</summary>
public static class ObjectSnap
{
    /// <summary>
    /// The snap point nearest to <paramref name="cursor"/> within <paramref name="radius"/> (drawing units), or null.
    /// At equal distance the simpler kind wins (end point before intersection, ...).
    /// </summary>
    public static SnapPoint? Find(IEnumerable<Entity> entities, Point2 cursor, double radius, SnapKinds kinds)
    {
        SnapPoint? best = null;
        var bestDistance = radius;
        void Consider(Point2 p, SnapKinds kind)
        {
            var d = Point2.Distance(p, cursor);
            if (d < bestDistance - 1e-9 || d <= bestDistance + 1e-9 && best is { } b && kind < b.Kind)
            {
                best = new SnapPoint(p, kind);
                bestDistance = d;
            }
        }

        bool Near(BoundingBox box) => !box.IsEmpty &&
            cursor.X >= box.Min.X - radius && cursor.X <= box.Max.X + radius &&
            cursor.Y >= box.Min.Y - radius && cursor.Y <= box.Max.Y + radius;

        var near = entities.Where(e => Near(e.Bounds())).ToList();
        foreach (var e in near)
        foreach (var sp in Points(e, kinds))
            Consider(sp.Point, sp.Kind);

        if (kinds.HasFlag(SnapKinds.Intersection))
        {
            var segments = near.SelectMany(e => e.ToPaths()).SelectMany(p => p.Segments())
                .Where(s => Near(SegmentBounds(s))).ToList();
            for (var i = 0; i < segments.Count; i++)
            for (var j = i + 1; j < segments.Count; j++)
            foreach (var p in Intersections(segments[i], segments[j]))
                Consider(p, SnapKinds.Intersection);
        }
        return best;
    }

    /// <summary>End, middle, centre and quadrant points of one drawing object (in drawing coordinates).</summary>
    public static IEnumerable<SnapPoint> Points(Entity e, SnapKinds kinds)
    {
        switch (e)
        {
            case TextEntity:
            case SurfaceEntity:
                yield break;
            case PointListEntity pl:
                if (kinds.HasFlag(SnapKinds.EndPoint))
                    foreach (var p in pl.WorldPoints()) yield return new SnapPoint(p.XY, SnapKinds.EndPoint);
                yield break;
            case SplineEntity:
                // The flattened curve has many short segments: only its ends are meaningful.
                if (!kinds.HasFlag(SnapKinds.EndPoint)) yield break;
                foreach (var path in e.ToPaths().Where(p => p.Count > 0))
                {
                    yield return new SnapPoint(path.StartPoint.XY, SnapKinds.EndPoint);
                    if (!path.Closed) yield return new SnapPoint(path.EndPoint.XY, SnapKinds.EndPoint);
                }
                yield break;
        }

        // A circle consists of two half arcs: its split points are quadrants, not end or middle points.
        var circle = e is CircleEntity;
        foreach (var path in e.ToPaths())
        foreach (var s in path.Segments())
        {
            if (!circle && kinds.HasFlag(SnapKinds.EndPoint))
            {
                yield return new SnapPoint(s.Start.XY, SnapKinds.EndPoint);
                yield return new SnapPoint(s.End.XY, SnapKinds.EndPoint);
            }
            if (!s.IsArc)
            {
                if (kinds.HasFlag(SnapKinds.MidPoint)) yield return new SnapPoint((s.Start.XY + s.End.XY) / 2, SnapKinds.MidPoint);
                continue;
            }
            var arc = s.ToArc();
            if (!circle && kinds.HasFlag(SnapKinds.MidPoint)) yield return new SnapPoint(arc.PointAt(0.5), SnapKinds.MidPoint);
            if (kinds.HasFlag(SnapKinds.Center)) yield return new SnapPoint(arc.Center, SnapKinds.Center);
            if (kinds.HasFlag(SnapKinds.Quadrant))
                for (var q = 0; q < 4; q++)
                {
                    var angle = q * Math.PI / 2;
                    if (arc.ContainsAngle(angle)) yield return new SnapPoint(arc.Center + Point2.FromAngle(angle, arc.Radius), SnapKinds.Quadrant);
                }
        }
        // Single-vertex paths (points) have no segments.
        if (kinds.HasFlag(SnapKinds.EndPoint))
            foreach (var path in e.ToPaths().Where(p => p.Count == 1))
                yield return new SnapPoint(path.StartPoint.XY, SnapKinds.EndPoint);
    }

    /// <summary>Points where two segments (lines or arcs) cross or touch.</summary>
    public static IEnumerable<Point2> Intersections(Segment a, Segment b)
    {
        const double eps = 1e-9;
        if (!a.IsArc && !b.IsArc)
        {
            var p = a.Start.XY;
            var r = a.End.XY - p;
            var q = b.Start.XY;
            var s = b.End.XY - q;
            var cross = Point2.Cross(r, s);
            if (Math.Abs(cross) < 1e-12) yield break; // parallel or collinear: no single crossing point
            var t = Point2.Cross(q - p, s) / cross;
            var u = Point2.Cross(q - p, r) / cross;
            if (t >= -eps && t <= 1 + eps && u >= -eps && u <= 1 + eps) yield return p + r * t;
            yield break;
        }
        if (a.IsArc && b.IsArc)
        {
            var arcA = a.ToArc();
            var arcB = b.ToArc();
            foreach (var p in CircleCircle(arcA.Center, arcA.Radius, arcB.Center, arcB.Radius))
                if (OnArc(arcA, p) && OnArc(arcB, p)) yield return p;
            yield break;
        }
        var line = a.IsArc ? b : a;
        var arc = (a.IsArc ? a : b).ToArc();
        var start = line.Start.XY;
        var dir = line.End.XY - start;
        var len2 = Point2.Dot(dir, dir);
        if (len2 < 1e-24) yield break;
        // |start + dir t - c|² = r²
        var f = start - arc.Center;
        var bq = 2 * Point2.Dot(f, dir);
        var cq = Point2.Dot(f, f) - arc.Radius * arc.Radius;
        var disc = bq * bq - 4 * len2 * cq;
        if (disc < -1e-12) yield break;
        var root = Math.Sqrt(Math.Max(0, disc));
        foreach (var t in disc <= 1e-12 ? [-bq / (2 * len2)] : new[] { (-bq - root) / (2 * len2), (-bq + root) / (2 * len2) })
        {
            if (t < -eps || t > 1 + eps) continue;
            var p = start + dir * t;
            if (OnArc(arc, p)) yield return p;
        }
    }

    private static bool OnArc(Arc2 arc, Point2 p) => arc.ContainsAngle(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X));

    private static IEnumerable<Point2> CircleCircle(Point2 c1, double r1, Point2 c2, double r2)
    {
        var d = Point2.Distance(c1, c2);
        if (d < 1e-12 || d > r1 + r2 + 1e-9 || d < Math.Abs(r1 - r2) - 1e-9) yield break;
        var a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        var h = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
        var u = (c2 - c1) / d;
        var m = c1 + u * a;
        var n = new Point2(-u.Y, u.X);
        yield return m + n * h;
        if (h > 1e-9) yield return m - n * h;
    }

    private static BoundingBox SegmentBounds(Segment s) =>
        s.IsArc ? s.ToArc().Bounds() : BoundingBox.FromCorners(s.Start.XY, s.End.XY);
}
