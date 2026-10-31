using BamCam.Core.Geometry;
using BamCam.Core.Localization;

namespace BamCam.Core.Cam;

/// <summary>
/// Lathe coordinates of a turning operation: Z along the spindle axis (drawing X minus origin X), X as radius on the
/// side of the axis where the drawing objects lie.
/// </summary>
internal sealed class LatheFrame
{
    public Point2 Origin { get; }
    public double Side { get; }

    private LatheFrame(Point2 origin, double side)
    {
        Origin = origin;
        Side = side;
    }

    public static LatheFrame For(GenContext ctx, IReadOnlyList<Path2> sources)
    {
        var origin = ctx.Part?.MachiningOrigin.Point?.XY ?? ctx.Doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
        var ys = sources.SelectMany(s => s.Flatten(ToolpathGenerator.ChordTolerance)).Select(p => p.Y - origin.Y).ToList();
        return new LatheFrame(origin, ys.Count > 0 && ys.Average() < 0 ? -1 : 1);
    }

    public Point3 ToDrawing(double z, double x) => new(Origin.X + z, Origin.Y + x * Side, 0);

    /// <summary>Extent of the sources in lathe coordinates (Z range, radius range).</summary>
    public (double ZMin, double ZMax, double XMin, double XMax) Extent(IEnumerable<Path2> sources)
    {
        var pts = sources.SelectMany(s => s.Flatten(ToolpathGenerator.ChordTolerance))
            .Select(p => new Point2(p.X - Origin.X, Math.Abs(p.Y - Origin.Y))).ToList();
        return (pts.Min(p => p.X), pts.Max(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.Y));
    }

    /// <summary>Stock end in lathe Z (the stock of turned parts is given around the machining origin), or null.</summary>
    public static double? StockEnd(GenContext ctx) => ctx.Stock.IsEmpty ? null : ctx.Stock.PMax.X;
}

/// <summary>Move output in lathe coordinates (Z, X radius) converted to drawing coordinates.</summary>
internal sealed class TurningWriter(Toolpath tp, LatheFrame frame)
{
    public (double Z, double X)? Position { get; private set; }

    public void Rapid(double z, double x)
    {
        if (Position is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
        tp.Add(Move.Rapid(frame.ToDrawing(z, x)));
        Position = (z, x);
    }

    public void Feed(double z, double x, double feed)
    {
        if (Position is { } p && Math.Abs(p.Z - z) < 1e-9 && Math.Abs(p.X - x) < 1e-9) return;
        tp.Add(Move.Line(frame.ToDrawing(z, x), feed));
        Position = (z, x);
    }

    /// <summary>Radial retract to the clearance (if below), then travel along Z at the clearance.</summary>
    public void ToClearance(double z, double clearanceX)
    {
        if (Position is { } p && p.X < clearanceX) Rapid(p.Z, clearanceX);
        Rapid(z, clearanceX);
    }

    public void Add(Move m, double z, double x)
    {
        tp.Add(m);
        Position = (z, x);
    }
}

/// <summary>Toolpaths of facing, grooving, threading and axial drilling.</summary>
internal static class TurningGenerators
{
    private static List<Path2>? Sources(GenContext ctx, MachineOp op, Toolpath tp)
    {
        var sources = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Count >= 1).ToList();
        return sources.Count > 0 ? sources : null;
    }

    /// <summary>Facing passes from the stock end to the end face of the part, radially towards the axis.</summary>
    public static void Facing(GenContext ctx, LatheFacingOp op, Toolpath tp)
    {
        if (Sources(ctx, op, tp) is not { } sources) return;
        var frame = LatheFrame.For(ctx, sources);
        var (_, face, _, _) = frame.Extent(sources);
        face += Math.Max(0, op.RoughingClearance.Value);
        var step = Math.Max(1e-3, op.DepthIncrement.Value);
        var start = LatheFrame.StockEnd(ctx) ?? face + step;
        if (start <= face + 1e-9)
        {
            tp.Warnings.Add(Tr.F("{0}: the stock does not reach beyond the end face; one pass at the face.", op.Name));
            start = face + 1e-9;
        }
        var r = ctx.ToolRadius;
        var outer = ctx.StockSurface + 1;              // start outside the stock radius
        var inner = Math.Max(0, op.InnerRadius.Value) - r; // the nose passes the inner radius (the axis)
        var w = new TurningWriter(tp, frame);
        var levels = new List<double>();
        for (var z = start - step; z > face + 1e-9; z -= step) levels.Add(z);
        levels.Add(face);
        foreach (var z in levels)
        {
            w.ToClearance(z, ctx.Clearance);
            w.Rapid(z, Math.Min(ctx.Clearance, outer));
            w.Feed(z, inner, ctx.CutFeed);
            // Lift off the face before the rapid retract.
            w.Feed(z + 0.5, inner + 0.5, ctx.CutFeed);
            w.Rapid(z + 0.5, ctx.Clearance);
        }
    }

    /// <summary>Plunges side by side across the groove (pecking), then optionally one pass along the bottom.</summary>
    public static void Grooving(GenContext ctx, LatheGrooveOp op, Toolpath tp)
    {
        if (Sources(ctx, op, tp) is not { } sources) return;
        var frame = LatheFrame.For(ctx, sources);
        var (z0, z1, bottom, _) = frame.Extent(sources);
        var c = Math.Max(0, op.RoughingClearance.Value);
        z0 += c;
        z1 -= c;
        bottom += c;
        var width = ctx.ToolDiameter;
        if (z1 - z0 < width - 1e-9)
        {
            tp.Warnings.Add(Tr.F("{0}: the groove is narrower than the tool width ({1}).", op.Name, width));
            return;
        }
        var top = ctx.StockSurface;
        var approach = Math.Min(ctx.Clearance, top + 0.5);
        var stepOver = Math.Clamp(op.StepOver.Value, 0.05, 1) * width;
        var positions = new List<double>();
        for (var z = z0; z < z1 - width - 1e-9; z += stepOver) positions.Add(z);
        positions.Add(z1 - width);

        var w = new TurningWriter(tp, frame);
        var peck = op.DepthIncrement.Value;
        for (var i = 0; i < positions.Count; i++)
        {
            var z = positions[i];
            w.ToClearance(z, ctx.Clearance);
            w.Rapid(z, approach);
            var x = top;
            while (x > bottom + 1e-9)
            {
                x = peck > 0 ? Math.Max(bottom, x - peck) : bottom;
                w.Feed(z, x, ctx.PlungeFeed);
                if (x > bottom + 1e-9 && op.PeckRetract.Value > 0) w.Rapid(z, x + op.PeckRetract.Value);
            }
            var last = i == positions.Count - 1;
            if (last && op.FinishPass.Value && positions.Count > 1)
            {
                // Clean the bottom back to the first plunge, then leave the groove.
                w.Feed(z0, bottom, ctx.CutFeed);
            }
            w.Rapid(w.Position!.Value.Z, approach);
        }
    }

    /// <summary>One threading cycle from the drive line (in front of the thread) to the end of the thread.</summary>
    public static void Threading(GenContext ctx, LatheThreadOp op, Toolpath tp)
    {
        if (Sources(ctx, op, tp) is not { } sources) return;
        var frame = LatheFrame.For(ctx, sources);
        var (z0, z1, xMin, xMax) = frame.Extent(sources);
        var pitch = op.Pitch.Value;
        if (pitch <= 0)
        {
            tp.Warnings.Add(Tr.F("{0}: the pitch must be greater than 0.", op.Name));
            return;
        }
        var external = op.ThreadType.Value == ThreadType.External;
        var depth = op.ThreadDepth.Value > 0 ? op.ThreadDepth.Value : pitch * (external ? 0.6134 : 0.5413);
        // External threads are cut into the largest diameter of the object, internal ones out of the bore.
        var peak = external ? xMax : xMin;
        var drive = external ? peak + 0.5 : Math.Max(0, peak - 0.5);
        var root = external ? peak - depth : peak + depth;
        var start = z1 + Math.Max(0, op.RunIn.Value) * pitch;

        var w = new TurningWriter(tp, frame);
        w.ToClearance(start, ctx.Clearance);
        w.Rapid(start, drive);
        var cycle = new ThreadCycle(pitch, peak - drive, Math.Min(op.FirstCutDepth.Value, depth), depth,
            Math.Max(1, op.DepthDegression.Value), op.InfeedAngle.Value, Math.Max(0, op.SpringPasses.Value));
        // The cycle ends back at the drive line start.
        w.Add(Move.ThreadCut(frame.ToDrawing(z0, root), cycle), start, drive);
    }

    /// <summary>Drilling along the axis with optional pecks (full retract between pecks).</summary>
    public static void Drilling(GenContext ctx, LatheDrillOp op, Toolpath tp)
    {
        if (Sources(ctx, op, tp) is not { } sources) return;
        var frame = LatheFrame.For(ctx, sources);
        var (bottom, entry, _, _) = frame.Extent(sources);
        var retract = entry + Math.Max(0.1, op.RetractHeight.Value);
        var w = new TurningWriter(tp, frame);
        w.ToClearance(retract, ctx.Clearance);
        w.Rapid(retract, 0);
        var peck = op.PeckDistance.Value;
        var z = entry;
        while (z > bottom + 1e-9)
        {
            z = peck > 0 ? Math.Max(bottom, z - peck) : bottom;
            w.Feed(z, 0, ctx.PlungeFeed);
            if (z > bottom + 1e-9)
            {
                // Full retract to clear the chips, rapid back to just above the last depth.
                w.Rapid(retract, 0);
                w.Rapid(Math.Min(retract, z + 0.2), 0);
            }
        }
        w.Rapid(retract, 0);
    }
}
