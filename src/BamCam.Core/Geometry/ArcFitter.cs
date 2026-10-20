namespace BamCam.Core.Geometry;

/// <summary>
/// Replaces runs of short line segments with circular arcs (bulges) where the points lie on a
/// circle within tolerance. Runs must have constant Z. Used after polygon offsetting so that
/// curved toolpaths are output as G2/G3 instead of many G1 moves.
/// </summary>
public static class ArcFitter
{
    public static Path2 Fit(Path2 path, double tolerance = 0.002, int minPoints = 4)
    {
        var src = path.Vertices;
        if (src.Count < minPoints || src.Any(v => Math.Abs(v.Bulge) > 1e-12)) return path.Clone();

        var pts = src.Select(v => v.Point).ToList();
        if (path.Closed)
        {
            // Rotate so that we start at a corner (not mid-arc) for better fitting.
            var start = FindCorner(pts);
            pts = pts.Skip(start).Concat(pts.Take(start)).ToList();
            pts.Add(pts[0]);
        }

        var result = new List<Vertex>();
        var i = 0;
        while (i < pts.Count - 1)
        {
            var best = -1;
            Arc2 bestArc = default;
            for (var j = i + minPoints - 1; j < pts.Count; j++)
            {
                if (TryFit(pts, i, j, tolerance, out var arc)) { best = j; bestArc = arc; }
                else if (best >= 0) break;
            }
            if (best >= 0)
            {
                // Split arcs larger than 180° for robust bulge representation.
                var sweep = bestArc.Sweep;
                var parts = Math.Abs(sweep) > Math.PI * 0.999 ? 2 : 1;
                for (var k = 0; k < parts; k++)
                {
                    var sub = new Arc2(bestArc.Center, bestArc.Radius, bestArc.StartAngle + sweep * k / parts, sweep / parts);
                    var p = k == 0 ? pts[i] : sub.Start.ToPoint3(pts[i].Z);
                    result.Add(new Vertex(p, sub.ToBulge()));
                }
                i = best;
            }
            else
            {
                result.Add(new Vertex(pts[i]));
                i++;
            }
        }
        if (!path.Closed) result.Add(new Vertex(pts[^1]));
        return new Path2(result, path.Closed);
    }

    private static int FindCorner(List<Point3> pts)
    {
        var n = pts.Count;
        var bestIdx = 0;
        var bestTurn = -1.0;
        for (var i = 0; i < n; i++)
        {
            var a = pts[(i - 1 + n) % n].XY;
            var b = pts[i].XY;
            var c = pts[(i + 1) % n].XY;
            var d1 = (b - a).Normalized();
            var d2 = (c - b).Normalized();
            var turn = Math.Abs(Math.Atan2(Point2.Cross(d1, d2), Point2.Dot(d1, d2)));
            if (turn > bestTurn) { bestTurn = turn; bestIdx = i; }
        }
        return bestIdx;
    }

    private static bool TryFit(List<Point3> pts, int i, int j, double tol, out Arc2 arc)
    {
        arc = default;
        var z = pts[i].Z;
        for (var k = i + 1; k <= j; k++)
            if (Math.Abs(pts[k].Z - z) > 1e-9) return false;

        var a = pts[i].XY;
        var c = pts[j].XY;
        var b = pts[(i + j) / 2].XY;
        if ((i + j) / 2 == i || (i + j) / 2 == j) return false;
        var fit = Arc2.FromThreePoints(a, b, c);
        if (fit is not { } f) return false;
        if (f.Radius > 1e5) return false;

        // For a closed loop (a == c) FromThreePoints fails; require distinct endpoints.
        if (a.AlmostEquals(c, tol)) return false;

        var ccw = f.Sweep > 0;
        var prevAngle = f.StartAngle;
        double travelled = 0;
        // Successive chords must turn gently and in the arc's direction (rejects corners and zig-zags).
        for (var k = i + 1; k < j; k++)
        {
            var d1 = (pts[k].XY - pts[k - 1].XY).Normalized();
            var d2 = (pts[k + 1].XY - pts[k].XY).Normalized();
            var turn = Math.Atan2(Point2.Cross(d1, d2), Point2.Dot(d1, d2));
            if (Math.Abs(turn) > Angles.ToRadians(30) || (Math.Abs(turn) > 1e-6 && (turn > 0) != ccw)) return false;
        }
        for (var k = i + 1; k <= j; k++)
        {
            var p = pts[k].XY;
            if (Math.Abs(Point2.Distance(p, f.Center) - f.Radius) > tol) return false;
            // Midpoints of chords must also be on the arc (catches zig-zags).
            var prev = pts[k - 1].XY;
            var half = Point2.Distance(prev, p) / 2;
            if (half > f.Radius) return false;
            var expected = Math.Sqrt(f.Radius * f.Radius - half * half);
            if (Math.Abs(Point2.Distance(Point2.Lerp(prev, p, 0.5), f.Center) - expected) > tol) return false;
            var ang = (p - f.Center).Angle;
            var step = ccw ? Angles.Normalize(ang - prevAngle) : Angles.Normalize(prevAngle - ang);
            if (step > Math.PI / 2) return false; // direction reversal or huge gap
            travelled += step;
            prevAngle = ang;
        }
        if (Math.Abs(travelled - Math.Abs(f.Sweep)) > 1e-3) return false;
        arc = f;
        return true;
    }
}
