using BamCam.Core.Geometry;
using BamCam.Core.Localization;

namespace BamCam.Core.Cam;

/// <summary>
/// Toolpaths of a lathe (turning) operation.
/// The drawing shows the part profile on one side of the spindle axis, which runs through the machining origin
/// parallel to drawing X. Computation happens in lathe coordinates: Z along the axis (drawing X - origin X) and
/// X as radius (distance from the axis). The tool nose centre keeps Tool Diameter / 2 + Roughing Clearance away
/// from the material, i.e. it follows the outer offset of the region between axis and profile.
/// Moves are stored in drawing coordinates (Z = 0); the post processor maps them to lathe X/Z.
/// </summary>
internal static class LatheGenerator
{
    public static void Generate(GenContext ctx, LatheOp op, Toolpath tp)
    {
        var sources = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Count >= 2).ToList();
        if (sources.Count == 0) return;
        var origin = ctx.Part?.MachiningOrigin.Point?.XY ?? ctx.Doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
        var leadIn = Math.Max(0, op.LatheLeadInLength.Value) / Math.Sqrt(2);
        var leadOut = Math.Max(0, op.LatheLeadOutLength.Value) / Math.Sqrt(2);
        var rightHand = op.LatheCutDirection.Value == LatheCutDirection.RightHand;
        var roughing = op.RoughingFinishing.Value == RoughingFinishing.Roughing;
        var clearanceX = ctx.Clearance;
        var surfaceX = ctx.StockSurface;
        var increment = op.DepthIncrement.Value;

        foreach (var source in sources)
        {
            // Profile side: drawing Y below or above the axis.
            var pts = source.Flatten(ToolpathGenerator.ChordTolerance);
            var side = pts.Average(p => p.Y - origin.Y) < 0 ? -1.0 : 1.0;
            Point2 ToLathe(Point2 p) => new(p.X - origin.X, (p.Y - origin.Y) * side);
            Point3 ToDrawing(double z, double x) => new(origin.X + z, origin.Y + x * side, 0);

            var profile = pts.Select(ToLathe).ToList();
            var contour = ToolContour(profile, ctx.ToolRadius + op.RoughingClearance.Value);
            if (contour.Count < 2)
            {
                tp.Warnings.Add(Tr.F("{0}: the profile could not be offset for the tool.", op.Name));
                continue;
            }
            // Right hand cuts from +Z towards -Z: the contour is ordered by decreasing Z; left hand the other way.
            if (!rightHand) contour.Reverse();
            var dir = rightHand ? -1.0 : 1.0; // cutting direction along Z

            var moves = new LatheMoves(tp, ToDrawing, clearanceX, ctx.PlungeFeed, ctx.CutFeed);

            if (roughing && increment > 0)
            {
                var lowest = contour.Min(p => p.Y);
                var previous = surfaceX;
                for (var level = surfaceX - increment; ; level -= increment)
                {
                    if (level < lowest + 1e-9) break;
                    foreach (var (from, to) in Intervals(contour, level, dir))
                        moves.StraightPass(from, to, level, previous, dir, leadIn, leadOut);
                    previous = level;
                }
            }

            moves.ContourPass(contour, dir, leadIn, leadOut);
            moves.Finish();
        }
    }

    /// <summary>
    /// Path of the tool nose centre along the profile (lathe coordinates Z, X), ordered by decreasing Z:
    /// the upper boundary of the region between axis and profile, grown by <paramref name="distance"/>,
    /// limited to the Z range of the profile.
    /// </summary>
    internal static List<Point2> ToolContour(List<Point2> profile, double distance)
    {
        var zMin = profile.Min(p => p.X);
        var zMax = profile.Max(p => p.X);
        if (zMax - zMin < 1e-9) return [];
        // Material: profile closed down to the axis at both ends.
        var start = profile[0];
        var end = profile[^1];
        var region = new List<Point2>(profile) { new(end.X, 0), new(start.X, 0) };
        var grown = Polygons.Offset([Path2.FromPoints(region, true)], Math.Max(distance, 0));
        var outline = grown.OrderByDescending(p => Math.Abs(p.SignedArea())).FirstOrDefault();
        if (outline == null) return [];
        var ring = outline.Flatten(ToolpathGenerator.ChordTolerance);
        if (Path2.FromPoints(ring, true).IsClockwise) ring.Reverse();

        // Counter-clockwise in (Z right, X up): from the rightmost point the ring runs over the top to the left.
        var n = ring.Count;
        var right = Enumerable.Range(0, n).OrderByDescending(i => ring[i].X).ThenByDescending(i => ring[i].Y).First();
        var left = Enumerable.Range(0, n).OrderBy(i => ring[i].X).ThenByDescending(i => ring[i].Y).First();
        var chain = new List<Point2>();
        for (var i = right; ; i = (i + 1) % n)
        {
            chain.Add(ring[i]);
            if (i == left) break;
        }
        return ClipToZ(chain, zMin, zMax);
    }

    /// <summary>Cuts a chain (decreasing Z overall) to the Z range [zMin, zMax], interpolating at the limits.</summary>
    private static List<Point2> ClipToZ(List<Point2> chain, double zMin, double zMax)
    {
        var result = new List<Point2>();
        for (var i = 0; i < chain.Count; i++)
        {
            var p = chain[i];
            var inside = p.X <= zMax + 1e-9 && p.X >= zMin - 1e-9;
            if (i > 0)
            {
                var a = chain[i - 1];
                foreach (var limit in new[] { zMax, zMin })
                {
                    if ((a.X - limit) * (p.X - limit) < 0)
                    {
                        var t = (limit - a.X) / (p.X - a.X);
                        result.Add(Point2.Lerp(a, p, t));
                    }
                }
            }
            if (inside) result.Add(p);
        }
        // Keep the highest stretch when the chain dips out of range and back (rare overhangs at the ends).
        return result;
    }

    /// <summary>
    /// Stretches of a roughing level where the contour lies below it (material to remove), as (start Z, end Z)
    /// in cutting direction. A stretch that reaches the end of the contour starts beyond the profile.
    /// </summary>
    internal static List<(double From, double To)> Intervals(List<Point2> contour, double level, double dir)
    {
        var result = new List<(double, double)>();
        double? start = contour[0].Y < level ? contour[0].X - dir * 1.0 : null;
        for (var i = 1; i < contour.Count; i++)
        {
            var a = contour[i - 1];
            var b = contour[i];
            if ((a.Y - level) * (b.Y - level) < 0)
            {
                var z = a.X + (b.X - a.X) * (level - a.Y) / (b.Y - a.Y);
                if (b.Y < level) start = z;
                else if (start is { } s)
                {
                    result.Add((s, z));
                    start = null;
                }
            }
        }
        if (start is { } open) result.Add((open, contour[^1].X));
        return result.Where(r => (r.Item2 - r.Item1) * dir > 1e-6).ToList();
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
            // Extend the straight run from i as far as all skipped points stay on the line i -> j.
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

    /// <summary>Move output in lathe coordinates, converted to drawing coordinates.</summary>
    private sealed class LatheMoves(Toolpath tp, Func<double, double, Point3> toDrawing, double clearanceX, double plungeFeed, double cutFeed)
    {
        private (double Z, double X)? _pos;

        private void Rapid(double z, double x)
        {
            if (_pos is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
            tp.Add(Move.Rapid(toDrawing(z, x)));
            _pos = (z, x);
        }

        private void Feed(double z, double x, double feed)
        {
            if (_pos is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
            tp.Add(Move.Line(toDrawing(z, x), feed));
            _pos = (z, x);
        }

        /// <summary>Retract radially to the clearance, travel along Z and come down above the start.</summary>
        private void Approach(double z, double x)
        {
            if (_pos is { } p && p.X < clearanceX) Rapid(p.Z, clearanceX);
            Rapid(z, clearanceX);
            if (x < clearanceX) Rapid(z, x);
        }

        public void StraightPass(double fromZ, double toZ, double level, double previousLevel, double dir, double leadIn, double leadOut)
        {
            // 45° lead in from outside the material (behind the start, above the level).
            Approach(fromZ - dir * leadIn, Math.Min(clearanceX, previousLevel + leadIn));
            Feed(fromZ - dir * leadIn, level + leadIn, plungeFeed);
            Feed(fromZ, level, cutFeed);
            Feed(toZ, level, cutFeed);
            // 45° lead out away from the shoulder.
            if (leadOut > 0) Feed(toZ - dir * leadOut, level + leadOut, cutFeed);
            Rapid(_pos!.Value.Z, clearanceX);
        }

        public void ContourPass(List<Point2> contour, double dir, double leadIn, double leadOut)
        {
            var first = contour[0];
            // Rapid only to the clearance: after roughing the roughing clearance is still left at the start.
            Approach(first.X - dir * leadIn, clearanceX);
            Feed(first.X - dir * leadIn, first.Y + leadIn, plungeFeed);
            Feed(first.X, first.Y, plungeFeed);
            // Along the contour with arcs where the offset follows circles (tool nose around corners, round profiles).
            // Long straight segments are split first: the fitter only checks vertices, so two nearly collinear
            // lines would otherwise become one huge arc that leaves the contour between them.
            var path = MergeLines(ArcFitter.Fit(new Path2(Densify(contour, 0.25).Select(p => new Vertex(toDrawing(p.X, p.Y))), false), 0.001), 0.001);
            foreach (var s in path.Segments())
            {
                if (s.IsArc)
                {
                    var arc = s.ToArc();
                    tp.Add(Move.Arc(s.End, arc.Center, arc.IsClockwise, cutFeed));
                }
                else
                {
                    tp.Add(Move.Line(s.End, cutFeed));
                }
            }
            var last = contour[^1];
            _pos = (last.X, last.Y);
            if (leadOut > 0) Feed(last.X - dir * leadOut, last.Y + leadOut, cutFeed);
        }

        public void Finish()
        {
            if (_pos is { } p) Rapid(p.Z, clearanceX);
        }
    }
}
