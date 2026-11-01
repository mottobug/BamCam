using System.ComponentModel;
using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>
/// Trochoidal (high speed) pocketing: the tool clears the pocket in circles along the centre lines (medial axis) of
/// the area it can reach. Each circle reaches at most Step Over beyond the previous one, so the tool never cuts with
/// more than that width, also in corners and slots: suited to full depth cuts with a small step over. A helix enters
/// at the widest place; a final pass along the walls removes what the circles leave.
/// </summary>
public sealed class TrochoidalPocketOp : DepthOp
{
    public override string XmlName => "BamCamTrochoidalPocket";
    public override string DisplayType => "Trochoidal Pocket";

    [Param(52, "Cutting"), DisplayName("Step Over"),
     Description("Largest width of new material per circle, as a fraction of the tool diameter (e.g. 0.1 to 0.25).")]
    public CamParam<double> StepOver { get; set; } = new(0.15);

    [Param(55, "Cutting"), DisplayName("Milling Direction"),
     Description("Climb milling is usual for trochoidal milling.")]
    public CamParam<MillingDirection> MillingDirection { get; set; } = new(Cam.MillingDirection.Climb);

    [Param(60, "Trochoidal"), DisplayName("Min Circle Radius"),
     Description("Smallest radius of the circles, as a fraction of the tool radius; narrower places (corners, thin slots) are left to the final pass.")]
    public CamParam<double> MinCircleRadius { get; set; } = new(0.1);

    [Param(61, "Trochoidal"), DisplayName("Ramp Angle"),
     Description("Angle of the helix that enters each depth level, in degrees.")]
    public CamParam<double> RampAngle { get; set; } = new(3);

    [Param(62, "Trochoidal"), DisplayName("Final Pass"),
     Description("Cut along the walls at each depth level after the circles.")]
    public CamParam<bool> FinalPass { get; set; } = new(true);
}

internal static class TrochoidalGenerator
{
    private sealed class Chain
    {
        public required List<MedialPoint> Points;
        public int A, B; // end nodes
        public bool Done;
    }

    public static void Generate(GenContext ctx, TrochoidalPocketOp op, Toolpath tp)
    {
        var rt = ctx.ToolRadius;
        if (rt <= 0)
        {
            ctx.Warnings.Add(Localization.Tr.F("{0}: no tool diameter.", op.Name));
            ctx.Finish(tp);
            return;
        }
        var step = Math.Clamp(op.StepOver.Value, 0.01, 1) * 2 * rt;
        var minR = Math.Max(0, op.MinCircleRadius.Value) * rt;
        var ccw = op.MillingDirection.Value != MillingDirection.Conventional; // pocket walls: climb = counter-clockwise
        var closed = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Closed && p.Count > 1).ToList();
        var areas = Polygons.BuildShapes(closed, ToolpathGenerator.ChordTolerance)
            .SelectMany(s => Polygons.InsetShape(s, rt + op.RoughingClearance.Value, ToolpathGenerator.ChordTolerance))
            .ToList();
        if (areas.Count == 0)
        {
            ctx.Warnings.Add(Localization.Tr.F("{0}: the tool does not fit into the shapes.", op.Name));
            ctx.Finish(tp);
            return;
        }
        var resolution = Math.Clamp(step / 4, 0.005, 0.25);
        var plans = areas.Select(a => (Area: a, Axis: MedialAxis.Compute([a.Outer, .. a.Holes], resolution)
            .Select(c => MedialAxis.Simplify(c, resolution / 4)).ToList())).ToList();

        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var levels = ctx.Levels(op);
        foreach (var (area, axis) in plans)
        {
            var (chains, start) = Graph(axis);
            if (start < 0) continue;
            var prev = ctx.StockSurface;
            foreach (var z in levels)
            {
                foreach (var c in chains) c.Done = false;
                var startPoint = chains.SelectMany(c => c.Points).MaxBy(p => p.R);
                Enter(ctx, op, mb, startPoint, z, prev, step, rt, ccw);
                var cutter = new Cutter(ctx, mb, z, step, minR, ccw) { Last = startPoint };
                Visit(cutter, chains, start);
                if (op.FinalPass.Value) Walls(ctx, mb, area, z, ccw);
                mb.Retract();
                prev = z;
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>Chains as graph edges; the start node is split off at the widest point.</summary>
    private static (List<Chain> Chains, int Start) Graph(List<List<MedialPoint>> axis)
    {
        var chains = new List<Chain>();
        var keys = new Dictionary<(long, long), int>();
        int Node(Point2 p)
        {
            var key = ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5));
            if (!keys.TryGetValue(key, out var n)) keys[key] = n = keys.Count;
            return n;
        }
        // Split the chain with the widest point there, so that the tool starts in the widest place.
        var widest = axis.Where(c => c.Count > 1).SelectMany(c => c).DefaultIfEmpty().MaxBy(p => p.R);
        foreach (var c in axis.Where(c => c.Count > 1))
        {
            var k = c.FindIndex(p => p == widest);
            var parts = k > 0 && k < c.Count - 1 ? new[] { c.Take(k + 1).ToList(), c.Skip(k).ToList() } : new[] { c };
            foreach (var part in parts)
                chains.Add(new Chain { Points = part, A = Node(part[0].P), B = Node(part[^1].P) });
        }
        return (chains, chains.Count == 0 ? -1 : Node(widest.P));
    }

    /// <summary>Helix down at the widest place, then a spiral out to the full circle there.</summary>
    private static void Enter(GenContext ctx, TrochoidalPocketOp op, MoveBuilder mb, MedialPoint at, double z, double fromZ,
        double step, double rt, bool ccw)
    {
        var helix = Math.Min(at.R, 0.8 * rt);
        var c = at.P;
        var sign = ccw ? 1 : -1;
        var first = c + new Point2(helix, 0);
        mb.Retract();
        mb.Rapid(first.ToPoint3(mb.ClearanceZ));
        if (helix < 0.05 * rt)
        {
            mb.PlungeTo(z, ctx.PlungeFeed, fromZ);
            return;
        }
        mb.PlungeTo(fromZ, ctx.PlungeFeed, fromZ);
        var perTurn = Math.Max(2 * Math.PI * helix * Math.Tan(Angles.ToRadians(Math.Clamp(op.RampAngle.Value, 0.5, 45))), 1e-3);
        var turns = Math.Max(1, (int)Math.Ceiling((fromZ - z) / perTurn));
        var depth = fromZ;
        for (var t = 0; t < turns; t++)
        {
            // Two half circles per turn, descending linearly.
            var mid = depth - (fromZ - z) / turns / 2;
            depth -= (fromZ - z) / turns;
            mb.Arc((c - (first - c)).ToPoint3(mid), c, !ccw, ctx.PlungeFeed);
            mb.Arc(first.ToPoint3(depth), c, !ccw, ctx.PlungeFeed);
        }
        mb.Arc((c - (first - c)).ToPoint3(z), c, !ccw, ctx.CutFeed);
        mb.Arc(first.ToPoint3(z), c, !ccw, ctx.CutFeed);
        // Spiral out to the full radius, growing by step per turn.
        var r = helix;
        var angle = 0.0;
        while (r < at.R - 1e-9)
        {
            angle += sign * Math.PI / 18;
            r = Math.Min(at.R, r + step / 36);
            mb.Line((c + Point2.FromAngle(angle, r)).ToPoint3(z), ctx.CutFeed);
        }
        Circle(mb, c, mb.Position!.Value.XY, z, ccw, ctx.CutFeed);
    }

    private static void Circle(MoveBuilder mb, Point2 centre, Point2 start, double z, bool ccw, double feed)
    {
        mb.Arc((centre - (start - centre)).ToPoint3(z), centre, !ccw, feed);
        mb.Arc(start.ToPoint3(z), centre, !ccw, feed);
    }

    private sealed class Cutter(GenContext ctx, MoveBuilder mb, double z, double step, double minR, bool ccw)
    {
        public MedialPoint Last;

        /// <summary>Walks along the points, cutting a circle wherever the next one reaches step over beyond the last.</summary>
        public void Follow(IEnumerable<MedialPoint> points)
        {
            MedialPoint? prev = null;
            foreach (var p in points)
            {
                if (prev is { } q)
                {
                    // Look at points in between too (a long simplified segment).
                    var n = Math.Max(1, (int)Math.Ceiling(Point2.Distance(q.P, p.P) / (step / 4)));
                    for (var k = 1; k <= n; k++)
                    {
                        var t = (double)k / n;
                        Consider(new MedialPoint(q.P + (p.P - q.P) * t, q.R + (p.R - q.R) * t));
                    }
                }
                else Consider(p);
                prev = p;
            }
        }

        private void Consider(MedialPoint p)
        {
            if (p.R < minR) return;
            // How far the new circle reaches beyond the last one.
            var reach = Point2.Distance(p.P, Last.P) + p.R - Last.R;
            if (reach < step) return;
            var pos = mb.Position!.Value.XY;
            var dir = pos - p.P;
            var start = dir.Length < 1e-9 ? p.P + new Point2(p.R, 0) : p.P + dir.Normalized() * p.R;
            // The direct link is safe where it stays inside the two circles (each lies in the free area); otherwise,
            // e.g. around the corner of an island, it goes through the centres along the centre line.
            var last = Last;
            bool Safe(Point2 x) => Point2.Distance(x, last.P) <= last.R + 1e-6 || Point2.Distance(x, p.P) <= p.R + 1e-6;
            if (!Enumerable.Range(0, 11).All(k => Safe(pos + (start - pos) * (k / 10.0))))
            {
                mb.Line(last.P.ToPoint3(z), ctx.CutFeed);
                mb.Line(p.P.ToPoint3(z), ctx.CutFeed);
            }
            mb.Line(start.ToPoint3(z), ctx.CutFeed);
            Circle(mb, p.P, start, z, ccw, ctx.CutFeed);
            Last = p;
        }

        /// <summary>Back along cleared centre lines to a branch point.</summary>
        public void Return(IEnumerable<MedialPoint> points)
        {
            foreach (var p in points) mb.Line(p.P.ToPoint3(z), ctx.CutFeed);
        }
    }

    /// <summary>Depth first through the centre line graph; after each branch the tool returns along it.</summary>
    private static void Visit(Cutter cutter, List<Chain> chains, int node)
    {
        var branches = chains.Where(c => !c.Done && (c.A == node || c.B == node))
            .OrderByDescending(c => c.Points.Max(p => p.R)).ToList();
        foreach (var chain in branches)
        {
            if (chain.Done) continue;
            chain.Done = true;
            var points = chain.A == node ? chain.Points : Enumerable.Reverse(chain.Points).ToList();
            var other = chain.A == node ? chain.B : chain.A;
            cutter.Return([points[0]]);
            cutter.Follow(points);
            if (other != node) Visit(cutter, chains, other);
            cutter.Return(Enumerable.Reverse(points));
            cutter.Last = points[0];
        }
    }

    /// <summary>Final pass along the walls (outline counter-clockwise for climb milling, islands the other way).</summary>
    private static void Walls(GenContext ctx, MoveBuilder mb, Shape area, double z, bool ccw)
    {
        foreach (var loop in new[] { Polygons.EnsureOrientation(area.Outer, ccw) }
                     .Concat(area.Holes.Select(h => Polygons.EnsureOrientation(h, !ccw))))
        {
            var from = mb.Position?.XY ?? loop.StartPoint.XY;
            var path = ArcFitter.Fit(loop, 0.001).StartNearest(from).WithZ(z);
            mb.MoveToAndPlunge(path.StartPoint, ctx.PlungeFeed, z);
            mb.Follow(path, ctx.CutFeed);
        }
    }
}
