using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>Holding tab placement on a loop: arc-length interval [Start, End].</summary>
internal readonly record struct TabInterval(double Start, double End)
{
    public double Mid => (Start + End) / 2;
}

/// <summary>
/// Emits the moves for cutting one loop at one depth level, including lead moves and holding tabs.
/// </summary>
internal sealed class LoopCutter(GenContext ctx, MoveBuilder mb)
{
    // Offset points are accurate to ~1e-5 (fine flattening), so arcs can be fitted tightly; OffsetSnapper then
    // makes them exact.
    private const double FitTolerance = 0.003;

    public double TabTop { get; init; } = double.NegativeInfinity;
    public TabStyle TabStyle { get; init; } = TabStyle.Square;
    public LeadMove LeadIn { get; init; } = LeadMove.None;
    public LeadMove LeadOut { get; init; } = LeadMove.None;

    /// <summary>Normal pointing away from the material at the loop start (for tangent leads).</summary>
    public Func<Loop, Point2, Point2>? AwayNormal { get; init; }

    /// <summary>
    /// Cuts <paramref name="loop"/> at <paramref name="z"/>. <paramref name="prevZ"/> is the previous cut level
    /// (or stock surface) used for the rapid-down rule. Linking feed is used when the tool is already close.
    /// </summary>
    public void Cut(Loop loop, double z, double prevZ, IReadOnlyList<TabInterval> tabs, double? linkFeed = null)
    {
        _loop = loop;
        var start = loop.Start.ToPoint3(z);
        var leadInStart = TangentLeadStart(loop, LeadIn);

        if (linkFeed is { } lf && mb.Position is { } pos && Math.Abs(pos.Z - z) < 1e-9)
        {
            // Stepover move at the current level.
            mb.Line(start, lf);
        }
        else if (leadInStart is { } ls && LeadIn.LeadMoveType == LeadMoveType.Tangent)
        {
            mb.MoveToAndPlunge(ls.Start.ToPoint3(z), ctx.PlungeFeed, prevZ);
            var feed = LeadIn.LeadMoveFeedrate > 0 ? LeadIn.LeadMoveFeedrate : ctx.CutFeed;
            mb.Arc(start, ls.Center, ls.Clockwise, feed);
        }
        else if (LeadIn.LeadMoveType == LeadMoveType.Spiral && loop.Closed && z < prevZ - 1e-9)
        {
            mb.MoveToAndPlunge(loop.Start.ToPoint3(prevZ), ctx.PlungeFeed, prevZ);
            Ramp(loop, prevZ, z);
        }
        else if (LeadIn.LeadMoveType == LeadMoveType.Spiral && !loop.Closed && loop.Points.Count > 1 && z < prevZ - 1e-9)
        {
            mb.MoveToAndPlunge(loop.Start.ToPoint3(prevZ), ctx.PlungeFeed, prevZ);
            RampOpen(loop, prevZ, z);
        }
        else
        {
            mb.MoveToAndPlunge(start, ctx.PlungeFeed, prevZ);
        }

        FollowWithTabs(loop, z, prevZ, z < TabTop - 1e-9 ? tabs : []);

        var leadOut = TangentLeadEnd(loop, LeadOut);
        if (leadOut is { } lo && LeadOut.LeadMoveType == LeadMoveType.Tangent)
        {
            var feed = LeadOut.LeadMoveFeedrate > 0 ? LeadOut.LeadMoveFeedrate : ctx.CutFeed;
            mb.Arc(lo.End.ToPoint3(z), lo.Center, lo.Clockwise, feed);
        }
    }

    /// <summary>
    /// Ramped entry on an open path: descend along the path (one path length for angle 0, otherwise by the
    /// ramp angle), then feed back to the start at depth.
    /// </summary>
    private void RampOpen(Loop loop, double fromZ, double toZ)
    {
        var pts = loop.Points;
        var len = loop.Length;
        if (len < 1e-9) return;
        var angle = LeadIn.SpiralAngle;
        var rampLen = Math.Min(angle > 0.01 ? (fromZ - toZ) / Math.Tan(Angles.ToRadians(angle)) : len, len);
        var feed = LeadIn.LeadMoveFeedrate > 0 ? LeadIn.LeadMoveFeedrate : ctx.CutFeed;
        var back = new List<Point2> { pts[0] };
        double s = 0;
        for (var i = 1; i < pts.Count && s < rampLen - 1e-9; i++)
        {
            var seg = Point2.Distance(pts[i - 1], pts[i]);
            var p = s + seg > rampLen ? Point2.Lerp(pts[i - 1], pts[i], (rampLen - s) / seg) : pts[i];
            s = Math.Min(s + seg, rampLen);
            mb.Line(p.ToPoint3(fromZ - (fromZ - toZ) * s / rampLen), feed);
            back.Add(p);
        }
        for (var i = back.Count - 2; i >= 0; i--) mb.Line(back[i].ToPoint3(toZ), ctx.CutFeed);
    }

    /// <summary>Spiral (ramped) entry along the loop from <paramref name="fromZ"/> down to <paramref name="toZ"/>.</summary>
    private void Ramp(Loop loop, double fromZ, double toZ)
    {
        var len = loop.Length;
        if (len < 1e-9) return;
        var angle = LeadIn.SpiralAngle;
        var rampLen = angle > 0.01 ? (fromZ - toZ) / Math.Tan(Angles.ToRadians(angle)) : len;
        var feed = LeadIn.LeadMoveFeedrate > 0 ? LeadIn.LeadMoveFeedrate : ctx.CutFeed;
        _loop = loop;
        // Ramp along the fitted (exact) geometry: lines and helical arcs, not the dense polygon.
        var segments = Fitted(new List<Point2>(loop.Points) { loop.Points[0] }).Segments().ToList();
        if (segments.Count == 0) return;
        double s = 0;
        double ZAt(double dist) => fromZ - (fromZ - toZ) * Math.Min(dist, rampLen) / rampLen;
        for (var guard = 0; guard < 10000; guard++)
        {
            foreach (var seg in segments)
            {
                var segLen = seg.Length;
                if (s + segLen < rampLen - 1e-9)
                {
                    s += segLen;
                    mb.Segment(seg with { End = seg.End with { Z = ZAt(s) } }, feed);
                    continue;
                }
                // The ramp reaches depth inside this segment: split it there, then finish the lap at depth.
                var t = segLen < 1e-12 ? 1 : (rampLen - s) / segLen;
                var (first, rest) = Split(seg, t);
                mb.Segment(first with { End = first.End with { Z = toZ } }, feed);
                if (rest is { } r) mb.Segment(r with { End = r.End with { Z = toZ } }, ctx.CutFeed);
                var idx = segments.IndexOf(seg);
                for (var k = idx + 1; k < segments.Count; k++)
                    mb.Segment(segments[k] with { End = segments[k].End with { Z = toZ } }, ctx.CutFeed);
                return;
            }
        }
    }

    /// <summary>Splits a line or arc segment at fraction <paramref name="t"/> of its length.</summary>
    private static (Segment First, Segment? Remainder) Split(Segment seg, double t)
    {
        if (t >= 1 - 1e-12) return (seg, null);
        if (!seg.IsArc)
        {
            var p = seg.Start + (seg.End - seg.Start) * t;
            return (new Segment(seg.Start, p, 0), new Segment(p, seg.End, 0));
        }
        var arc = seg.ToArc();
        var mid = arc.PointAt(t).ToPoint3(seg.Start.Z + (seg.End.Z - seg.Start.Z) * t);
        return (new Segment(seg.Start, mid, Math.Tan(arc.Sweep * t / 4)), new Segment(mid, seg.End, Math.Tan(arc.Sweep * (1 - t) / 4)));
    }

    private readonly record struct LeadArc(Point2 Start, Point2 End, Point2 Center, bool Clockwise);

    private LeadArc? TangentLeadStart(Loop loop, LeadMove lead)
    {
        if (lead.LeadMoveType != LeadMoveType.Tangent || lead.TangentRadius <= 0 || loop.Points.Count < 2 || AwayNormal == null) return null;
        var p = loop.Start;
        var t = (loop.Points[1] - p).Normalized();
        var n = AwayNormal(loop, t);
        var r = lead.TangentRadius;
        var c = p + n * r;
        var s = c - t * r;
        var clockwise = Point2.Cross(s - c, p - c) < 0;
        return new LeadArc(s, p, c, clockwise);
    }

    private LeadArc? TangentLeadEnd(Loop loop, LeadMove lead)
    {
        if (lead.LeadMoveType != LeadMoveType.Tangent || lead.TangentRadius <= 0 || loop.Points.Count < 2 || AwayNormal == null) return null;
        var p = loop.End;
        var prev = loop.Closed ? loop.Points[^1] : loop.Points[^2];
        var t = (p - prev).Normalized();
        var n = AwayNormal(loop, t);
        var r = lead.TangentRadius;
        var c = p + n * r;
        var e = c + t * r;
        var clockwise = Point2.Cross(p - c, e - c) < 0;
        return new LeadArc(p, e, c, clockwise);
    }

    /// <summary>Follows the loop from its start, lifting over holding tabs.</summary>
    private void FollowWithTabs(Loop loop, double z, double prevZ, IReadOnlyList<TabInterval> tabs)
    {
        var pts = new List<Point2>(loop.Points);
        if (loop.Closed) pts.Add(pts[0]);

        if (tabs.Count == 0)
        {
            EmitRun(pts, z);
            return;
        }

        // Build a list of (point, z) samples with breaks at tab boundaries.
        var samples = new List<(Point2 P, double Z)>();
        var sorted = tabs.OrderBy(t => t.Start).ToList();
        double ZAt(double s)
        {
            foreach (var t in sorted)
            {
                if (s < t.Start - 1e-9 || s > t.End + 1e-9) continue;
                if (TabStyle == TabStyle.Triangle)
                {
                    var half = (t.End - t.Start) / 2;
                    var f = 1 - Math.Abs(s - t.Mid) / half;
                    return z + (TabTop - z) * Math.Clamp(f, 0, 1);
                }
                return TabTop;
            }
            return z;
        }

        double acc = 0;
        samples.Add((pts[0], ZAt(0)));
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var len = Point2.Distance(a, b);
            if (len < 1e-12) continue;
            var cuts = new List<double>();
            foreach (var t in sorted)
            {
                foreach (var s in TabStyle == TabStyle.Triangle ? new[] { t.Start, t.Mid, t.End } : [t.Start, t.End])
                    if (s > acc + 1e-9 && s < acc + len - 1e-9) cuts.Add(s);
            }
            cuts.Sort();
            foreach (var s in cuts)
            {
                var p = Point2.Lerp(a, b, (s - acc) / len);
                if (TabStyle == TabStyle.Triangle)
                {
                    samples.Add((p, ZAt(s)));
                }
                else
                {
                    // Square tab boundary: vertical step.
                    var inside = sorted.Any(t => Math.Abs(t.Start - s) < 1e-9);
                    samples.Add((p, inside ? z : TabTop));
                    samples.Add((p, inside ? TabTop : z));
                }
            }
            acc += len;
            samples.Add((b, ZAt(acc)));
        }

        // Emit: runs of constant Z are arc-fitted; vertical steps become rapid-up / plunge-down.
        var run = new List<Point2> { samples[0].P };
        var runZ = samples[0].Z;
        for (var i = 1; i < samples.Count; i++)
        {
            var (p, sz) = samples[i];
            var samePos = p.AlmostEquals(samples[i - 1].P, 1e-9);
            if (samePos && Math.Abs(sz - runZ) > 1e-9)
            {
                EmitRun(run, runZ);
                if (sz > runZ) mb.Rapid(p.ToPoint3(sz));
                else mb.PlungeTo(sz, ctx.PlungeFeed, prevZ);
                run = [p];
                runZ = sz;
            }
            else if (Math.Abs(sz - runZ) > 1e-9)
            {
                // Sloped (triangle tab) segment.
                EmitRun(run, runZ);
                mb.Line(p.ToPoint3(sz), ctx.CutFeed);
                run = [p];
                runZ = sz;
            }
            else
            {
                run.Add(p);
            }
        }
        EmitRun(run, runZ);
    }

    private Loop? _loop;
    private readonly Dictionary<(Point2, Point2, Point2, Point2, int, double), Path2> _fitCache = [];

    private void EmitRun(List<Point2> run, double z)
    {
        if (run.Count < 2) return;
        mb.Follow(Fitted(run).WithZ(z), ctx.CutFeed);
    }

    /// <summary>Arc-fitted and snapped run at Z = 0. The same loop is cut at every level with the same start: cached.</summary>
    private Path2 Fitted(List<Point2> run)
    {
        var key = (run[0], run[1], run[^2], run[^1], run.Count, _loop?.SnapOffset ?? 0);
        if (_fitCache.TryGetValue(key, out var flat)) return flat;
        flat = ArcFitter.Fit(Path2.FromPoints(run, closed: false, 0), FitTolerance);
        if (_loop?.SnapSources is { Count: > 0 } sources) flat = OffsetSnapper.Snap(flat, sources, _loop.SnapOffset);
        _fitCache[key] = flat;
        return flat;
    }
}
