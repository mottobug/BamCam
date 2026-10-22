using BamCam.Core.Geometry;
using BamCam.Core.Localization;

namespace BamCam.Core.Cam;

/// <summary>
/// Toolpaths of a lathe (turning) operation, following CamBam's output (verified against G-code of lathe-test.cb).
/// <para>
/// The drawing shows the part profile on one side of the spindle axis, which runs through the machining origin
/// parallel to drawing X. Computation happens in lathe coordinates: Z along the axis (drawing X - origin X) and
/// X as radius. The tool nose centre keeps nose radius + Roughing Clearance away from the material (the region
/// between axis and profile, closed down to the axis at both profile ends); the output point is the virtual tool
/// tip, one nose radius from the centre against X and against the cutting direction.
/// </para>
/// <para>
/// Roughing: tip levels every Depth Increment below Stock Surface down to the axis; each level is cut where the
/// centre stays outside the material, from the stock end (plus one nose radius) towards the profile start, with
/// a radial plunge and a 45° lead out. Then (and for finishing only) one pass along the contour.
/// </para>
/// Moves are stored in drawing coordinates (Z = 0); the post processor maps them to lathe X/Z.
/// </summary>
internal static class LatheGenerator
{
    public static void Generate(GenContext ctx, LatheOp op, Toolpath tp)
    {
        var sources = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Count >= 2).ToList();
        if (sources.Count == 0) return;
        var origin = ctx.Part?.MachiningOrigin.Point?.XY ?? ctx.Doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
        var r = ctx.ToolRadius;
        var clearance = Math.Max(0, op.RoughingClearance.Value);
        var leadOut = Math.Max(0, op.LatheLeadOutLength.Value) * Math.Sqrt(2);
        var dir = op.LatheCutDirection.Value == LatheCutDirection.RightHand ? -1.0 : 1.0; // cutting direction along Z
        var roughing = op.RoughingFinishing.Value == RoughingFinishing.Roughing;
        var increment = op.DepthIncrement.Value;
        // Rapid approach ends this far above the cutting height: Depth Increment + 0.1 (CamBam reference output,
        // roughing with increment 0.25 → 0.35, finishing with 0.4 → 0.5).
        var approach = Math.Max(0, increment) + 0.1;
        var stock = ctx.Stock;

        foreach (var source in sources)
        {
            var side = source.Flatten(ToolpathGenerator.ChordTolerance).Average(p => p.Y - origin.Y) < 0 ? -1.0 : 1.0;
            Point3 ToDrawing(double z, double x) => new(origin.X + z, origin.Y + x * side, 0);
            var mirror = Matrix4.Translation(-origin.X, -origin.Y, 0) * Matrix4.Scale(1, side, 1);
            var profile = source.Transformed(mirror);

            var d = r + clearance;
            var outline = Outline(profile, d);
            var contour = outline == null ? null : CenterContour(profile, outline, d);
            if (outline == null || contour == null)
            {
                tp.Warnings.Add(Tr.F("{0}: the profile could not be offset for the tool.", op.Name));
                continue;
            }
            if (dir > 0) contour = contour.Reversed();

            var moves = new LatheMoves(tp, ToDrawing, ctx.Clearance, ctx.PlungeFeed, ctx.CutFeed, r, dir, side);
            if (roughing && increment > 0)
            {
                var pts = profile.Flatten(ToolpathGenerator.ChordTolerance);
                var zMin = pts.Min(p => p.X);
                var zMax = (stock.IsEmpty ? pts.Max(p => p.X) + d : stock.PMax.X) + r;
                var ring = ExactOutline(profile, outline, d);
                for (var tip = ctx.StockSurface - increment; tip + r > 1e-9; tip -= increment)
                {
                    foreach (var (from, to) in FreeIntervals(ring, tip + r, zMin, zMax, dir))
                        moves.StraightPass(from, to, tip, approach, leadOut);
                }
            }
            moves.ContourPass(contour, approach, leadOut);
        }
    }

    /// <summary>Material region (profile closed to the axis) grown by <paramref name="d"/>: where the nose centre must not go.</summary>
    internal static Path2? Outline(Path2 profile, double d)
    {
        var start = profile.Vertices[0].Point;
        var end = profile.Vertices[^1].Point;
        var region = new List<Vertex>(profile.Vertices.Take(profile.Count - 1))
        {
            new(end), new(end with { Y = 0 }), new(start with { Y = 0 }),
        };
        var grown = Polygons.Offset([new Path2(region, true)], Math.Max(d, 1e-6));
        return grown.OrderByDescending(p => Math.Abs(p.SignedArea())).FirstOrDefault();
    }

    /// <summary>The grown outline with exact arcs and lines (for exact pass end points).</summary>
    internal static Path2 ExactOutline(Path2 profile, Path2 outline, double d)
    {
        var start = profile.Vertices[0].Point;
        var end = profile.Vertices[^1].Point;
        var region = new Path2(new List<Vertex>(profile.Vertices.Take(profile.Count - 1))
            { new(end), new(end with { Y = 0 }), new(start with { Y = 0 }) }, true);
        var ring = outline.Flatten(ToolpathGenerator.ChordTolerance);
        var fitted = ArcFitter.Fit(Path2.FromPoints(Densify([.. ring, ring[0]], 0.25).SkipLast(1), true), 0.001);
        return OffsetSnapper.Snap(fitted, [region], d, 0.005);
    }

    /// <summary>
    /// Nose centre path along the profile (lathe coordinates Z, X), ordered by decreasing Z: the part of the grown
    /// outline between the profile end points, each moved by <paramref name="d"/> perpendicular to the profile.
    /// Arcs and lines are snapped to the exact offset geometry.
    /// </summary>
    internal static Path2? CenterContour(Path2 profile, Path2 outline, double d)
    {
        var pts = profile.Flatten(ToolpathGenerator.ChordTolerance);
        if (pts.Count < 2) return null;
        // The profile may be drawn in either direction; the outer side is away from the axis.
        var increasing = pts[^1].X >= pts[0].X;
        // Exact offset where possible (tiny corner arcs exactly like CamBam), the polygon offset otherwise.
        if (AnalyticOffset(profile, d) is { } exact) return increasing ? exact.Reversed() : exact;
        Point2 Normal(Point2 a, Point2 b)
        {
            var t = (b - a).Normalized();
            return increasing ? new Point2(-t.Y, t.X) : new Point2(t.Y, -t.X);
        }
        var startOffset = pts[0] + Normal(pts[0], pts[1]) * d;
        var endOffset = pts[^1] + Normal(pts[^2], pts[^1]) * d;
        var right = increasing ? endOffset : startOffset; // larger Z
        var left = increasing ? startOffset : endOffset;

        var ring = outline.Flatten(ToolpathGenerator.ChordTolerance);
        if (Path2.FromPoints(ring, true).IsClockwise) ring.Reverse();
        var n = ring.Count;
        int Nearest(Point2 q) => Enumerable.Range(0, n).OrderBy(i => Point2.Distance(ring[i], q)).First();
        var i0 = Nearest(right);
        var i1 = Nearest(left);
        // Counter-clockwise in (Z right, X up): from the right end the ring runs over the top to the left end.
        var chain = new List<Point2> { right };
        for (var i = (i0 + 1) % n; i != i1; i = (i + 1) % n) chain.Add(ring[i]);
        chain.Add(left);
        chain = chain.Where((p, k) => k == 0 || !p.AlmostEquals(chain[k - 1], 1e-9)).ToList();
        if (chain.Count < 2) return null;

        var fitted = ArcFitter.Fit(Path2.FromPoints(Densify(chain, 0.25), false), 0.001);
        return MergeLines(OffsetSnapper.Snap(fitted, [profile], d, 0.005), 1e-4);
    }

    /// <summary>
    /// Stretches of the horizontal line X = <paramref name="centerX"/> outside the outline (where the nose centre can
    /// cut), limited to [zMin, zMax], as (start Z, end Z) in cutting direction, starting at the stock end.
    /// </summary>
    internal static List<(double From, double To)> FreeIntervals(Path2 outline, double centerX, double zMin, double zMax, double dir)
    {
        var crossings = new List<double>();
        foreach (var s in outline.Segments())
        {
            var a = s.Start.XY;
            var b = s.End.XY;
            if (!s.IsArc)
            {
                // Half-open rule: an end point exactly on the line counts once.
                if ((a.Y > centerX) != (b.Y > centerX)) crossings.Add(a.X + (b.X - a.X) * (centerX - a.Y) / (b.Y - a.Y));
                continue;
            }
            var arc = s.ToArc();
            var dy = centerX - arc.Center.Y;
            if (Math.Abs(dy) >= arc.Radius) continue;
            var dz = Math.Sqrt(arc.Radius * arc.Radius - dy * dy);
            foreach (var z in new[] { arc.Center.X - dz, arc.Center.X + dz })
            {
                var p = new Point2(z, centerX);
                // Points exactly at an arc end are left to the neighbouring element (equal heights are practically never hit).
                if (p.AlmostEquals(a, 1e-12) || p.AlmostEquals(b, 1e-12)) continue;
                if (arc.ContainsAngle((p - arc.Center).Angle)) crossings.Add(z);
            }
        }        crossings.Sort();
        // Inside the outline between pairs of crossings; outside before the first and after the last.
        var free = new List<(double Lo, double Hi)>();
        var lo = double.NegativeInfinity;
        for (var k = 0; k + 1 < crossings.Count; k += 2)
        {
            free.Add((lo, crossings[k]));
            lo = crossings[k + 1];
        }
        free.Add((lo, double.PositiveInfinity));
        var result = new List<(double, double)>();
        foreach (var (a, b) in free)
        {
            var from = Math.Max(a, zMin);
            var to = Math.Min(b, zMax);
            if (to - from < 1e-6) continue;
            result.Add(dir < 0 ? (to, from) : (from, to));
        }
        return dir < 0 ? result.OrderByDescending(p => p.Item1).ToList() : result.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>One element of an analytic offset: a line or an arc (centre, direction).</summary>
    private sealed class OffsetElement
    {
        public Point2 Start;
        public Point2 End;
        public bool IsArc;
        public Point2 Center;
        public bool Ccw;

        public double Radius => Point2.Distance(Start, Center);

        /// <summary>Unit tangent in travel direction at a point of the element.</summary>
        public Point2 Tangent(Point2 at)
        {
            if (!IsArc) return (End - Start).Normalized();
            var r = (at - Center).Normalized();
            return Ccw ? new Point2(-r.Y, r.X) : new Point2(r.Y, -r.X);
        }
    }

    /// <summary>
    /// Exact offset of an open profile by <paramref name="d"/> to its outer side (away from the axis): segments moved
    /// in parallel, arcs around convex corners, intersections at concave corners. Returns null when an element
    /// collapses or the result comes closer than <paramref name="d"/> to the profile (narrow grooves), so that the
    /// caller can fall back to the polygon offset.
    /// </summary>
    internal static Path2? AnalyticOffset(Path2 profile, double d)
    {
        var segs = profile.Segments().Where(s => !s.Start.XY.AlmostEquals(s.End.XY, 1e-9)).ToList();
        if (segs.Count == 0) return null;
        var pts = profile.Flatten(ToolpathGenerator.ChordTolerance);
        // Outer side: left of the travel direction when the profile is drawn with increasing Z, right otherwise.
        var left = pts[^1].X >= pts[0].X ? 1.0 : -1.0;
        Point2 Outward(Point2 t) => left > 0 ? new Point2(-t.Y, t.X) : new Point2(t.Y, -t.X);

        var elements = new List<OffsetElement>();
        foreach (var s in segs)
        {
            var a = s.Start.XY;
            var b = s.End.XY;
            if (!s.IsArc)
            {
                var n = Outward((b - a).Normalized()) * d;
                elements.Add(new OffsetElement { Start = a + n, End = b + n });
                continue;
            }
            var arc = s.ToArc();
            var ccw = arc.Sweep > 0;
            // Outer side away from the centre: the radius grows, otherwise it shrinks.
            var away = Point2.Dot(Outward(new OffsetElement { Start = a, End = b, IsArc = true, Center = arc.Center, Ccw = ccw }.Tangent(a)), a - arc.Center) > 0;
            var radius = arc.Radius + (away ? d : -d);
            if (radius <= 1e-9) return null;
            elements.Add(new OffsetElement
            {
                IsArc = true, Center = arc.Center, Ccw = ccw,
                Start = arc.Center + (a - arc.Center).Normalized() * radius,
                End = arc.Center + (b - arc.Center).Normalized() * radius,
            });
        }

        var result = new List<OffsetElement> { elements[0] };
        for (var i = 1; i < elements.Count; i++)
        {
            var prev = result[^1];
            var next = elements[i];
            var corner = segs[i].Start.XY;
            if (prev.End.AlmostEquals(next.Start, 1e-9))
            {
                result.Add(next);
                continue;
            }
            var turn = Point2.Cross(prev.Tangent(prev.End), next.Tangent(next.Start));
            if (turn * left < 0)
            {
                // Convex corner (turning away from the outer side): round it with the offset distance.
                result.Add(new OffsetElement { IsArc = true, Center = corner, Ccw = left < 0, Start = prev.End, End = next.Start });
                result.Add(next);
            }
            else
            {
                // Concave corner: both elements end at their intersection next to the corner.
                if (Intersect(prev, next, corner) is not { } x) return null;
                prev.End = x;
                next.Start = x;
                result.Add(next);
            }
        }

        var vertices = new List<Vertex>();
        foreach (var e in result)
        {
            var bulge = 0.0;
            if (e.IsArc)
            {
                var a0 = (e.Start - e.Center).Angle;
                var a1 = (e.End - e.Center).Angle;
                var sweep = e.Ccw ? Angles.Normalize(a1 - a0) : -Angles.Normalize(a0 - a1);
                bulge = Math.Tan(sweep / 4);
            }
            vertices.Add(new Vertex(e.Start.ToPoint3(), bulge));
        }
        vertices.Add(new Vertex(result[^1].End.ToPoint3()));
        var path = new Path2(vertices, false);

        // Valid only if the tool never comes closer than d to the profile (no self-overlapping offset).
        foreach (var p in path.Flatten(0.001))
            if (profile.DistanceTo(p) < d - 1e-6) return null;
        return path;
    }

    private static Point2? Intersect(OffsetElement a, OffsetElement b, Point2 near)
    {
        var candidates = new List<Point2>();
        if (!a.IsArc && !b.IsArc)
        {
            var da = a.End - a.Start;
            var db = b.End - b.Start;
            var den = Point2.Cross(da, db);
            if (Math.Abs(den) < 1e-15) return null;
            candidates.Add(a.Start + da * (Point2.Cross(b.Start - a.Start, db) / den));
        }
        else if (a.IsArc && b.IsArc)
        {
            candidates.AddRange(CircleCircle(a.Center, a.Radius, b.Center, b.Radius));
        }
        else
        {
            var line = a.IsArc ? b : a;
            var circle = a.IsArc ? a : b;
            candidates.AddRange(LineCircle(line.Start, line.End, circle.Center, circle.Radius));
        }
        return candidates.Count == 0 ? null : candidates.OrderBy(c => Point2.Distance(c, near)).First();
    }

    private static IEnumerable<Point2> LineCircle(Point2 p, Point2 q, Point2 c, double r)
    {
        var d = (q - p).Normalized();
        var f = p - c;
        var b = Point2.Dot(f, d);
        var disc = b * b - (f.LengthSquared - r * r);
        if (disc < 0) yield break;
        var s = Math.Sqrt(disc);
        yield return p + d * (-b - s);
        yield return p + d * (-b + s);
    }

    private static IEnumerable<Point2> CircleCircle(Point2 c0, double r0, Point2 c1, double r1)
    {
        var dist = Point2.Distance(c0, c1);
        if (dist < 1e-12 || dist > r0 + r1 || dist < Math.Abs(r0 - r1)) yield break;
        var a = (r0 * r0 - r1 * r1 + dist * dist) / (2 * dist);
        var h = Math.Sqrt(Math.Max(0, r0 * r0 - a * a));
        var u = (c1 - c0) / dist;
        var m = c0 + u * a;
        yield return m + new Point2(-u.Y, u.X) * h;
        yield return m - new Point2(-u.Y, u.X) * h;
    }

    /// <summary>Inserts points so that no segment is longer than <paramref name="maxLength"/>.</summary>
    internal static List<Point2> Densify(List<Point2> pts, double maxLength)
    {
        var result = new List<Point2> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var n = (int)Math.Ceiling(Point2.Distance(a, b) / maxLength);
            for (var k = 1; k < n; k++) result.Add(Point2.Lerp(a, b, (double)k / n));
            result.Add(b);
        }
        return result;
    }

    /// <summary>Joins consecutive straight segments whose inner points lie within <paramref name="tol"/> of one line.</summary>
    internal static Path2 MergeLines(Path2 path, double tol)
    {
        var v = path.Vertices;
        var result = new List<Vertex> { v[0] };
        var i = 0;
        while (i < v.Count - 1)
        {
            if (Math.Abs(v[i].Bulge) > 1e-12)
            {
                result.Add(v[i + 1]);
                i++;
                continue;
            }
            var j = i + 1;
            while (j + 1 < v.Count && Math.Abs(v[j].Bulge) < 1e-12)
            {
                var a = v[i].Point.XY;
                var c = v[j + 1].Point.XY;
                var ok = true;
                for (var k = i + 1; k <= j && ok; k++) ok = Path2.DistanceToSegment(v[k].Point.XY, a, c) <= tol;
                if (!ok) break;
                j++;
            }
            result.Add(v[j]);
            i = j;
        }
        return new Path2(result, false);
    }

    /// <summary>Move output: nose centre positions in lathe coordinates, written as tool tip in drawing coordinates.</summary>
    private sealed class LatheMoves(Toolpath tp, Func<double, double, Point3> toDrawing, double clearanceX, double plungeFeed,
        double cutFeed, double noseRadius, double dir, double side)
    {
        private (double Z, double X)? _tip;

        // Virtual tool tip: one nose radius below the centre and ahead of it in cutting direction.
        private double TipZ(double centerZ) => centerZ + dir * noseRadius;

        private void Rapid(double z, double x)
        {
            if (_tip is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
            tp.Add(Move.Rapid(toDrawing(z, x)));
            _tip = (z, x);
        }

        private void Feed(double z, double x, double feed)
        {
            if (_tip is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
            tp.Add(Move.Line(toDrawing(z, x), feed));
            _tip = (z, x);
        }

        /// <summary>Radial retract, travel along Z, rapid down to just above the cut, then plunge.</summary>
        private void Approach(double z, double x, double approach)
        {
            if (_tip is { } p && p.X < clearanceX) Rapid(p.Z, clearanceX);
            Rapid(z, clearanceX);
            Rapid(z, Math.Min(clearanceX, x + approach));
            Feed(z, x, plungeFeed);
        }

        private void LeadOut(double leadOut)
        {
            if (leadOut <= 0 || _tip is not { } p) return;
            Feed(p.Z - dir * leadOut, p.X + leadOut, cutFeed);
        }

        public void StraightPass(double fromCenterZ, double toCenterZ, double tipX, double approach, double leadOut)
        {
            Approach(TipZ(fromCenterZ), tipX, approach);
            Feed(TipZ(toCenterZ), tipX, cutFeed);
            LeadOut(leadOut);
        }

        public void ContourPass(Path2 contour, double approach, double leadOut)
        {
            var first = contour.Vertices[0].Point;
            Approach(TipZ(first.X), first.Y - noseRadius, approach);
            foreach (var s in contour.Segments())
            {
                var tip = (Z: TipZ(s.End.X), X: s.End.Y - noseRadius);
                var end = toDrawing(tip.Z, tip.X);
                if (s.IsArc)
                {
                    var arc = s.ToArc();
                    var center = toDrawing(TipZ(arc.Center.X), arc.Center.Y - noseRadius).XY;
                    // Mapping to the drawing mirrors the lathe view when the profile lies below the axis.
                    tp.Add(Move.Arc(end, center, arc.IsClockwise ^ (side < 0), cutFeed));
                }
                else
                {
                    tp.Add(Move.Line(end, cutFeed));
                }
                _tip = tip;
            }
            LeadOut(leadOut);
        }
    }
}
