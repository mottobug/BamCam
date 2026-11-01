using System.ComponentModel;
using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

/// <summary>
/// V-carving: a V-bit follows the medial axis of closed shapes (letters, regions) and goes as deep as the shape is
/// wide there, so that its cone touches both sides. Corners become sharp, strokes get their drawn width. Where a
/// shape is wider than the cone at the maximum depth (Target Depth), the bottom stays flat at that depth: its outline
/// is cut and its inside cleared with the V-bit (or left for a pocket with another tool).
/// </summary>
public sealed class VEngraveOp : DepthOp
{
    public override string XmlName => "BamCamVEngrave";
    public override string DisplayType => "V-Engrave";

    public VEngraveOp()
    {
        TargetDepth = new(-3);
        DepthIncrement = new(0);
    }

    /// <summary>Starting values for inch drawings.</summary>
    public void UseInchDefaults()
    {
        TargetDepth.Value = -0.12;
        FlatStepover.Value = 0.008;
        Resolution.Value = 0.002;
    }

    [Param(60, "V-Engrave"), DisplayName("V Angle"),
     Description("Included angle of the V-bit in degrees (e.g. 60 or 90).")]
    public CamParam<double> VAngle { get; set; } = new(90);

    [Param(61, "V-Engrave"), DisplayName("Tip Diameter"),
     Description("Diameter of the flat tip of the V-bit (0 = pointed).")]
    public CamParam<double> TipDiameter { get; set; } = new(0);

    [Param(62, "V-Engrave"), DisplayName("Clear Flat Areas"),
     Description("Clear the flat bottom of areas that are wider than the cone at the target depth with the V-bit. Off: only their outline is cut; clear them with a pocket and another tool.")]
    public CamParam<bool> ClearFlatAreas { get; set; } = new(true);

    [Param(63, "V-Engrave"), DisplayName("Flat Stepover"),
     Description("Distance between the passes that clear flat areas. A pointed V-bit leaves ridges of stepover / 2 / tan(V Angle / 2).")]
    public CamParam<double> FlatStepover { get; set; } = new(0.2);

    [Param(64, "V-Engrave"),
     Description("Sampling distance along the outlines for finding the centre lines: smaller is more exact and slower.")]
    public CamParam<double> Resolution { get; set; } = new(0.05);
}

internal static class VEngraveGenerator
{
    public static void Generate(GenContext ctx, VEngraveOp op, Toolpath tp)
    {
        var half = Angles.ToRadians(Math.Clamp(op.VAngle.Value, 1, 179) / 2);
        var tan = Math.Tan(half);
        var tipRadius = Math.Max(0, op.TipDiameter.Value) / 2;
        var top = ctx.StockSurface;
        var resolution = Math.Max(op.Resolution.Value, 1e-4);
        // Depth below the surface at which the cone touches a circle of radius r, and back.
        double DepthOf(double r) => Math.Max(0, (r - tipRadius) / tan);
        double RadiusAt(double depth) => tipRadius + depth * tan;

        var closed = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Closed && p.Count > 1).ToList();
        if (closed.Count == 0)
        {
            ctx.Warnings.Add(Localization.Tr.F("{0}: V-engraving needs closed shapes (text, regions, closed polylines).", op.Name));
            ctx.Finish(tp);
            return;
        }
        var shapes = Polygons.BuildShapes(closed, ToolpathGenerator.ChordTolerance);
        var axes = shapes.Select(s => MedialAxis.Compute([s.Outer, .. s.Holes], resolution)
            .Select(c => MedialAxis.Simplify(c, resolution / 4)).ToList()).ToList();

        var levels = ctx.Levels(op).Select(z => top - z).ToList(); // depths below the surface
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var pos = op.StartPoint.Value.Point?.XY ?? Point2.Zero;
        var previous = 0.0;
        foreach (var depth in levels)
        {
            var rLevel = RadiusAt(depth);
            var rPrevious = previous <= 0 ? tipRadius : RadiusAt(previous);
            var pieces = new List<List<Point3>>();
            // Centre lines where the full V-depth lies between the previous level and this one (deeper parts are cut
            // at this level, clamped; parts done by the previous level are skipped).
            foreach (var chain in axes.SelectMany(a => a))
                foreach (var run in Runs(chain, rPrevious, double.PositiveInfinity))
                    pieces.Add(run.Select(m => new Point3(m.P.X, m.P.Y, top - Math.Min(DepthOf(m.R), depth))).ToList());
            // Flat bottom at this level: the shape shrunk by the cone radius at this depth.
            foreach (var shape in shapes)
            foreach (var flat in Polygons.InsetShape(shape, rLevel, ToolpathGenerator.ChordTolerance))
            {
                foreach (var loop in new[] { flat.Outer }.Concat(flat.Holes))
                    pieces.Add(Closed(loop, top - depth));
                if (!op.ClearFlatAreas.Value) continue;
                var step = Math.Max(op.FlatStepover.Value, resolution);
                var ring = new List<Shape> { flat };
                while (ring.Count > 0)
                {
                    ring = ring.SelectMany(s => Polygons.InsetShape(s, step, ToolpathGenerator.ChordTolerance, step / 20)).ToList();
                    foreach (var s in ring)
                    foreach (var loop in new[] { s.Outer }.Concat(s.Holes))
                        pieces.Add(Closed(loop, top - depth));
                }
            }
            pos = CutPieces(ctx, mb, pieces, pos);
            previous = depth;
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>Parts of a chain where the clearance radius is above <paramref name="rFrom"/>, cut at the crossings.</summary>
    private static IEnumerable<List<MedialPoint>> Runs(List<MedialPoint> chain, double rFrom, double rTo)
    {
        var run = new List<MedialPoint>();
        for (var i = 0; i < chain.Count; i++)
        {
            var m = chain[i];
            var inRange = m.R > rFrom && m.R <= rTo;
            if (i > 0)
            {
                var prev = chain[i - 1];
                var prevIn = prev.R > rFrom && prev.R <= rTo;
                if (prevIn != inRange)
                {
                    // Crossing of rFrom (or rTo) between the two points.
                    var limit = (prev.R > rFrom) != (m.R > rFrom) ? rFrom : rTo;
                    var t = (limit - prev.R) / (m.R - prev.R);
                    var cross = new MedialPoint(prev.P + (m.P - prev.P) * t, limit);
                    run.Add(cross);
                    if (!inRange)
                    {
                        if (run.Count > 1) yield return run;
                        run = [];
                    }
                }
            }
            if (inRange) run.Add(m);
        }
        if (run.Count > 1) yield return run;
    }

    private static List<Point3> Closed(Path2 loop, double z)
    {
        var pts = loop.Flatten(ToolpathGenerator.ChordTolerance).Select(p => p.ToPoint3(z)).ToList();
        if (pts.Count > 0) pts.Add(pts[0]);
        return pts;
    }

    /// <summary>Cuts the pieces nearest first (open ones from the nearer end, closed ones from the nearest point).</summary>
    private static Point2 CutPieces(GenContext ctx, MoveBuilder mb, List<List<Point3>> pieces, Point2 pos)
    {
        pieces = pieces.Where(p => p.Count > 1).ToList();
        while (pieces.Count > 0)
        {
            var best = 0;
            var bestDist = double.PositiveInfinity;
            var reverse = false;
            var rotate = -1;
            for (var i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                var isClosed = p[0].XY.AlmostEquals(p[^1].XY, 1e-9);
                if (isClosed)
                {
                    for (var k = 0; k < p.Count - 1; k++)
                    {
                        var d = Point2.Distance(p[k].XY, pos);
                        if (d < bestDist) (best, bestDist, reverse, rotate) = (i, d, false, k);
                    }
                    continue;
                }
                var ds = Point2.Distance(p[0].XY, pos);
                var de = Point2.Distance(p[^1].XY, pos);
                if (ds < bestDist) (best, bestDist, reverse, rotate) = (i, ds, false, -1);
                if (de < bestDist) (best, bestDist, reverse, rotate) = (i, de, true, -1);
            }
            var piece = pieces[best];
            pieces.RemoveAt(best);
            if (rotate > 0) piece = piece.Skip(rotate).Take(piece.Count - 1 - rotate).Concat(piece.Take(rotate + 1)).ToList();
            if (reverse) piece.Reverse();

            var start = piece[0];
            // Continue without lifting when the piece starts where the tool is (branch points of the centre lines).
            if (mb.Position is { } at && at.XY.AlmostEquals(start.XY, 1e-6) && at.Z <= ctx.StockSurface)
                mb.Line(start, ctx.PlungeFeed);
            else
                mb.MoveToAndPlunge(start, ctx.PlungeFeed, ctx.StockSurface);
            for (var k = 1; k < piece.Count; k++) mb.Line(piece[k], ctx.CutFeed);
            pos = piece[^1].XY;
        }
        return pos;
    }
}
