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
        if (FullCircle(path, tolerance) is { } circle) return circle;

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
            // Greedy: the shortest candidate must fit, then extend while it keeps fitting.
            // Extension grows in doubling steps with a binary search back, keeping this near-linear.
            var best = -1;
            Arc2 bestArc = default;
            var first = i + minPoints - 1;
            if (first < pts.Count && TryFit(pts, i, first, tolerance, out var arc0))
            {
                best = first;
                bestArc = arc0;
                var step = 1;
                var lo = first;
                var hi = -1;
                while (true)
                {
                    var j = Math.Min(lo + step, pts.Count - 1);
                    if (j == lo) break;
                    if (TryFit(pts, i, j, tolerance, out var a))
                    {
                        lo = j;
                        best = j;
                        bestArc = a;
                        step *= 2;
                    }
                    else
                    {
                        hi = j;
                        break;
                    }
                }
                while (hi > 0 && hi - lo > 1)
                {
                    var mid = (lo + hi) / 2;
                    if (TryFit(pts, i, mid, tolerance, out var a))
                    {
                        lo = mid;
                        best = mid;
                        bestArc = a;
                    }
                    else hi = mid;
                }
            }
            if (best >= 0)
            {
                // Split arcs larger than 180° for robust bulge representation.
                var sweep = bestArc.Sweep;
                // Up to and including 180° stays one arc (a semicircle is one G2/G3 in the reference output).
                var parts = Math.Abs(sweep) > Math.PI * 1.01 ? 2 : 1;
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

    /// <summary>
    /// A complete circle (closed, or open with coincident ends) becomes three 120° arcs starting at its first
    /// point, which is how circular passes appear in the reference G-code.
    /// </summary>
    private static Path2? FullCircle(Path2 path, double tolerance)
    {
        var pts = path.Vertices.Select(v => v.Point).ToList();
        var closedRun = !path.Closed && pts[0].XY.AlmostEquals(pts[^1].XY, tolerance);
        if (!path.Closed && !closedRun) return null;
        if (pts.Any(p => Math.Abs(p.Z - pts[0].Z) > 1e-9)) return null;
        var n = closedRun ? pts.Count - 1 : pts.Count;
        if (n < 8) return null;
        var fit = Arc2.FromThreePoints(pts[0].XY, pts[n / 3].XY, pts[2 * n / 3].XY);
        if (fit is not { } c || c.Radius > 1e5) return null;
        double turn = 0;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i].XY;
            var b = pts[(i + 1) % n].XY;
            if (Math.Abs(Point2.Distance(a, c.Center) - c.Radius) > tolerance) return null;
            var half = Point2.Distance(a, b) / 2;
            if (half > c.Radius) return null;
            if (Math.Abs(Point2.Distance(Point2.Lerp(a, b, 0.5), c.Center) - Math.Sqrt(c.Radius * c.Radius - half * half)) > tolerance) return null;
            turn += Point2.Cross(a - c.Center, b - c.Center);
        }
        var ccw = turn > 0;
        var z = pts[0].Z;
        var start = (pts[0].XY - c.Center).Angle;
        var sweep = (ccw ? 1 : -1) * Angles.TwoPi / 3;
        var bulge = Math.Tan(sweep / 4);
        var verts = new List<Vertex>();
        for (var k = 0; k < 3; k++)
            verts.Add(new Vertex((c.Center + Point2.FromAngle(start + sweep * k, c.Radius)).ToPoint3(z), bulge));
        if (path.Closed) return new Path2(verts, true);
        verts.Add(new Vertex(pts[0]));
        return new Path2(verts, false);
    }

    /// <summary>True when the closed point loop lies on one circle (within <paramref name="tolerance"/>).</summary>
    public static bool TryCircle(IReadOnlyList<Point2> pts, double tolerance, out Point2 center, out double radius)
    {
        center = default;
        radius = 0;
        var n = pts.Count;
        if (n < 8) return false;
        if (Arc2.FromThreePoints(pts[0], pts[n / 3], pts[2 * n / 3]) is not { } c || c.Radius > 1e5) return false;
        foreach (var p in pts)
            if (Math.Abs(Point2.Distance(p, c.Center) - c.Radius) > tolerance) return false;
        center = c.Center;
        radius = c.Radius;
        return true;
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
        // Huge radii are near-straight runs (e.g. a line plus a few corner points), not arcs.
        if (f.Radius > 1e3) return false;

        // For a closed loop (a == c) FromThreePoints fails; require distinct endpoints.
        if (a.AlmostEquals(c, tol)) return false;

        var ccw = f.Sweep > 0;
        var prevAngle = f.StartAngle;
        double travelled = 0;
        // Arcs from offsetting/flattening are sampled evenly. A long chord among short ones is a straight
        // segment next to an arc and must not be absorbed into a huge-radius arc.
        // Compared with the median chord: offsetting inserts tiny join chords at every polygon vertex, which must
        // not count as irregular sampling.
        var chords = new double[j - i];
        for (var k = i + 1; k <= j; k++) chords[k - i - 1] = Point2.Distance(pts[k - 1].XY, pts[k].XY);
        var maxChord = chords.Max();
        Array.Sort(chords);
        var median = chords[chords.Length / 2];
        if (maxChord > 4 * median && maxChord > 10 * tol) return false;

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
