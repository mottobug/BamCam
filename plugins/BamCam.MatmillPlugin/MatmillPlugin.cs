// Trochoidal pocket and profile for BamCam: a port of matmill by jkmnt (https://github.com/jkmnt/matmill),
// MIT License, Copyright (c) 2016 jkmnt. See LICENSE-matmill.txt. The operations keep matmill's names and parameters
// (MOPTrochopock, MOPTrochoprof), so that CamBam drawings with matmill operations open in BamCam and the other way round.
// This file stands in for mop.cs, mop_pocket.cs, mop_profile.cs and plug.cs, which were written against CamBam.

using System.ComponentModel;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;
using Matmill;

namespace MatmillPlugin;

/// <summary>Registers the trochoidal pocket and profile.</summary>
public sealed class Plugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        host.AddOperation<TrochoPocketOp>(TrochoGenerator.Pocket);
        host.AddOperation<TrochoProfileOp>(TrochoGenerator.Profile);
    }
}

/// <summary>What both operations share (matmill's Trochomop).</summary>
public abstract class TrochoOp : DepthOp
{
    [Param(50, "Cutting"), DisplayName("Cut Ordering"), Description("Controls whether to cut to depth first or all cuts on this level first.")]
    public CamParam<CutOrdering> CutOrdering { get; set; } = new(BamCam.Core.Cam.CutOrdering.DepthFirst);

    [Param(52, "Step Over"), DisplayName("Step Over"),
     Description("Largest width of new material per slice, as a share of the tool diameter (0 - 1). Trochoidal milling uses small values, such as 0.1 to 0.25.")]
    public CamParam<double> StepOver { get; set; } = new(0.2);

    [Param(55, "Cutting"), DisplayName("Milling Direction"),
     Description("Climb or Conventional: all slices in the same direction, linked by chords through the cleared area. Mixed: the direction changes from slice to slice, which leaves out the chords.")]
    public CamParam<MillingDirection> MillingDirection { get; set; } = new(BamCam.Core.Cam.MillingDirection.Climb);

    [Param(58, "Lead Moves"), DisplayName("Lead In Move"), Description("Spiral: the tool goes down on a helix at the start. None: it plunges.")]
    public CamParam<LeadMove> LeadInMove { get; set; } = new(LeadMove.None);

    [Param(70, "Feedrates", XmlName = "Chord_feedrate"), DisplayName("Chord Feedrate"),
     Description("The feed rate to use for the chords and movements inside the milled pocket. If 0 use cutting feedrate.")]
    public CamParam<double> ChordFeedrate { get; set; } = new(0);

    [Param(71, "Feedrates", XmlName = "Spiral_feedrate"), DisplayName("Spiral Feedrate"),
     Description("The feed rate to use for the spiral opening the pocket. If 0 use cutting feedrate.")]
    public CamParam<double> SpiralFeedrate { get; set; } = new(0);

    [Param(72, "Options", XmlName = "Should_smooth_chords"), DisplayName("Smooth Chords"),
     Description("Replace straight chords with smooth arcs to form a continuous toolpath. This may be useful on machines with slow acceleration. Not applied to the mixed milling direction.")]
    public CamParam<bool> SmoothChords { get; set; } = new(false);

    /// <summary>Only shown in CamBam; kept so that the value goes back into the drawing.</summary>
    [Param(73, "Options", XmlName = "Should_draw_chords"), DisplayName("Show Chords")]
    public CamParam<bool> ShowChords { get; set; } = new(false);

    public override IReadOnlySet<string> HiddenParameters => Hidden;

    private static readonly HashSet<string> Hidden = [nameof(ShowChords)];
}

/// <summary>Trochoidal pocket (matmill's TrochoPock): clears closed shapes with slices along their medial axis.</summary>
public sealed class TrochoPocketOp : TrochoOp
{
    public override string XmlName => "MOPTrochopock";
    public override string DisplayType => "Trochoidal Pocket (matmill)";

    [Param(74, "Step Over", XmlName = "Min_stepover"), DisplayName("Minimum Stepover"),
     Description("Minimum allowed stepover as a share of the nominal stepover (0.05 - 0.95). Larger values may leave uncut corners.")]
    public CamParam<double> MinStepover { get; set; } = new(0.9);
}

/// <summary>Trochoidal profile (matmill's TrochoProf): a slot along the shapes, cut with slices.</summary>
public sealed class TrochoProfileOp : TrochoOp
{
    public override string XmlName => "MOPTrochoprof";
    public override string DisplayType => "Trochoidal Profile (matmill)";

    [Param(51, "Cutting"),
     Description("Whether to cut Inside or Outside the selected shapes. For open shapes the point order determines the side: Outside is to the right.")]
    public CamParam<InsideOutside> InsideOutside { get; set; } = new(BamCam.Core.Cam.InsideOutside.Outside);

    [Param(53, "Step Over"), DisplayName("Cut Width"), Description("The total width of the sliced cut. If 0 the width of the cut is 2 * tool diameter.")]
    public CamParam<double> CutWidth { get; set; } = new(0);

    [Param(57, "Options"), DisplayName("Corner Overcut"), Description("Not supported in BamCam yet: the profile is cut without it, with a warning.")]
    public CamParam<bool> CornerOvercut { get; set; } = new(false);

    [Param(74, "Step Over", XmlName = "Min_stepover"), DisplayName("Minimum Stepover"),
     Description("Minimum allowed stepover as a share of the nominal stepover (0.05 - 0.95). Used only with Clear Inside Corners.")]
    public CamParam<double> MinStepover { get; set; } = new(0.9);

    [Param(75, "Options", XmlName = "Clear_inside_corners"), DisplayName("Clear Inside Corners"),
     Description("Adds moves to clear inside corners: the cut is then made like a pocket between the shape and an offset of it. Closed shapes only.")]
    public CamParam<bool> ClearInsideCorners { get; set; } = new(false);
}

/// <summary>Turns matmill's flat trajectories into toolpaths at each depth (matmill's Trochomop, without CamBam).</summary>
internal static class TrochoGenerator
{
    /// <summary>A trajectory at one depth (matmill's Toolpath).</summary>
    private sealed record Level(Sliced_path Trajectory, double Bottom, double Top);

    public static void Pocket(ToolpathContext ctx, TrochoPocketOp op, Toolpath tp) => Run(ctx, op, tp, () =>
    {
        var paths = ctx.SourcePaths().Select(s => s.Path).ToList();
        if (paths.Any(p => !p.Closed))
            Logger.warn("ignoring open polylines");
        var trajectories = new List<Sliced_path>();
        // A circle has two vertices (two half arcs).
        foreach (var shape in Polygons.BuildShapes(paths.Where(p => p.Closed && p.Count >= 2), 1e-4))
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var gen = new Pocket_generator(new Contour(shape.Outer), [.. shape.Holes.Select(h => new Contour(h))])
            {
                General_tolerance = Tolerance(ctx),
                Tool_d = ctx.ToolDiameter,
                Max_ted = ctx.ToolDiameter * op.StepOver.Value,
                Min_ted = ctx.ToolDiameter * op.StepOver.Value * Math.Clamp(op.MinStepover.Value, 0.05, 0.95),
                Startpoint = StartPoint(op),
                Margin = op.RoughingClearance.Value,
                Cancel = ctx.Cancel,
            };
            SetDirection(gen, op);
            if (gen.run() is { } trajectory) trajectories.Add(trajectory);
        }
        return trajectories;
    });

    public static void Profile(ToolpathContext ctx, TrochoProfileOp op, Toolpath tp) => Run(ctx, op, tp, () =>
    {
        var d = ctx.ToolDiameter;
        if (op.CutWidth.Value != 0 && op.CutWidth.Value < d * 1.05)
            throw new Problem("cut width is too small");
        if (op.CornerOvercut.Value && op.ClearInsideCorners.Value)
            throw new Problem("Corner Overcut and Clear Inside Corners can't be used together");
        if (op.CornerOvercut.Value)
            Logger.warn("Corner Overcut is not supported in BamCam yet; the profile is cut without it");

        var paths = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Count > 1).ToList();
        var isInside = op.InsideOutside.Value == InsideOutside.Inside;
        var startpoint = StartPoint(op);
        var trajectories = new List<Sliced_path>();
        Point2 Next() => trajectories.Count > 0 && trajectories[^1].Count > 0 ? trajectories[^1][^1].LastPoint : startpoint;

        // Closed shapes as regions: the outline on the chosen side, its holes on the other (as matmill).
        foreach (var shape in Polygons.BuildShapes(paths.Where(p => p.Closed), 1e-4))
        {
            trajectories.AddRange(ProfileOf(ctx, op, shape.Outer, isInside, Next()));
            foreach (var hole in shape.Holes)
                trajectories.AddRange(ProfileOf(ctx, op, hole, !isInside, Next()));
        }
        foreach (var open in paths.Where(p => !p.Closed))
            trajectories.AddRange(ProfileOf(ctx, op, open, isInside, Next()));
        return trajectories;
    });

    /// <summary>A message for the user that ends the calculation.</summary>
    private sealed class Problem(string message) : Exception(message);

    private static double Tolerance(ToolpathContext ctx) => ctx.Document.Units == DrawingUnits.Inches ? 0.001 / 25.4 : 0.001;

    private static Point2 StartPoint(MachineOp op) => op.StartPoint.Value.Point is { } p ? p.XY : Pt.Undefined;

    /// <summary>
    /// Direction of the slices. Their material lies outside the arc, as at the wall of a pocket: with a clockwise
    /// spindle, climb milling runs counter-clockwise. Mixed (or the spindle off) lets each slice choose.
    /// </summary>
    private static RotationDirection Direction(TrochoOp op)
    {
        if (op.MillingDirection.Value == MillingDirection.Mixed || op.SpindleDirection.Value == SpindleDirection.Off)
            return RotationDirection.Unknown;
        var ccw = (op.MillingDirection.Value == MillingDirection.Climb) == (op.SpindleDirection.Value == SpindleDirection.CW);
        return ccw ? RotationDirection.CCW : RotationDirection.CW;
    }

    private static void SetDirection(Pocket_generator gen, TrochoOp op)
    {
        gen.Mill_direction = Direction(op);
        gen.Should_smooth_chords = Direction(op) != RotationDirection.Unknown && op.SmoothChords.Value;
    }

    private static void SetDirection(Engrave_generator gen, TrochoOp op)
    {
        gen.Mill_direction = Direction(op);
        gen.Should_smooth_chords = Direction(op) != RotationDirection.Unknown && op.SmoothChords.Value;
    }

    // ---------------------------------------------------------------- profile (mop_profile.cs)

    private static List<Sliced_path> ProfileOf(ToolpathContext ctx, TrochoProfileOp op, Path2 poly, bool isInside, Point2 startpoint)
    {
        if (op.ClearInsideCorners.Value)
        {
            if (poly.Closed) return FineProfile(ctx, op, poly, isInside, startpoint);
            Logger.warn("corner clearing for open polylines is not implemented. proceeding without clearing");
        }
        return CoarseProfile(ctx, op, poly, isInside, startpoint);
    }

    private static double CutWidth(ToolpathContext ctx, TrochoProfileOp op) => op.CutWidth.Value == 0 ? ctx.ToolDiameter * 2 : op.CutWidth.Value;

    /// <summary>The slot along the shape: slices of a fixed radius along an offset of it.</summary>
    private static List<Sliced_path> CoarseProfile(ToolpathContext ctx, TrochoProfileOp op, Path2 poly, bool isInside, Point2 startpoint)
    {
        if (!startpoint.IsUndefined())
        {
            if (poly.Closed)
            {
                poly = StartAt(poly, startpoint);
            }
            else if (startpoint.DistanceTo(poly.EndPoint.XY) < startpoint.DistanceTo(poly.StartPoint.XY))
            {
                poly = poly.Reversed();
                isInside = !isInside;    // preserve side if flipped !
            }
        }

        var cutWidth = CutWidth(ctx, op);
        var offset = cutWidth / 2 + op.RoughingClearance.Value;
        if (isInside) offset = -offset;

        List<Path2> array;
        if (Math.Abs(offset) < 1e-6)
            array = [poly]; // as matmill: an engraving, exactly along the line
        else if (poly.Closed)
            array = [.. Polygons.Offset([poly], offset).Where(p => p.Count > 2).Select(p => Like(p, poly))];
        else
            array = [Path2.FromPoints(Polygons.OffsetOpenSide(poly, Math.Abs(offset), right: offset > 0), closed: false)];

        var startTangent = StartTangent(poly);
        var trajectories = new List<Sliced_path>();
        foreach (var p in array)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var gen = new Engrave_generator(new Contour(p))
            {
                General_tolerance = Tolerance(ctx),
                Tool_d = ctx.ToolDiameter,
                Max_ted = ctx.ToolDiameter * op.StepOver.Value,
                Slice_radius = (cutWidth - ctx.ToolDiameter) / 2,
                Spiral_tangent = startTangent,
                Cancel = ctx.Cancel,
            };
            SetDirection(gen, op);
            if (gen.run() is { } trajectory) trajectories.Add(trajectory);
        }
        return trajectories;
    }

    /// <summary>With Clear Inside Corners: a pocket between the wall and an offset of it at the cut width.</summary>
    private static List<Sliced_path> FineProfile(ToolpathContext ctx, TrochoProfileOp op, Path2 poly, bool isInside, Point2 startpoint)
    {
        if (!startpoint.IsUndefined()) poly = StartAt(poly, startpoint);

        var cutWidth = CutWidth(ctx, op);
        var clearanceOffset = op.RoughingClearance.Value;
        var offset = cutWidth + clearanceOffset;
        if (isInside)
        {
            offset = -offset;
            clearanceOffset = -clearanceOffset;
        }

        var profileWalls = clearanceOffset != 0 ? Polygons.Offset([poly], clearanceOffset) : [poly];
        var pseudoWalls = Polygons.Offset([poly], offset);
        var startTangent = StartTangent(poly);
        if (startpoint.IsUndefined()) startpoint = poly.StartPoint.XY;

        var trajectories = new List<Sliced_path>();
        foreach (var shape in Polygons.BuildShapes(profileWalls.Concat(pseudoWalls), 1e-4))
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            if (trajectories.Count != 0) startpoint = trajectories[^1][^1].LastPoint;
            var gen = new Pocket_generator(new Contour(shape.Outer), [.. shape.Holes.Select(h => new Contour(h))])
            {
                General_tolerance = Tolerance(ctx),
                Tool_d = ctx.ToolDiameter,
                Max_ted = ctx.ToolDiameter * op.StepOver.Value,
                Min_ted = ctx.ToolDiameter * op.StepOver.Value * Math.Clamp(op.MinStepover.Value, 0.05, 0.95),
                Startpoint = startpoint,
                Startpoint_is_a_hint = true,
                Margin = 0,
                Spiral_tangent = startTangent,
                Cancel = ctx.Cancel,
            };
            SetDirection(gen, op);
            if (gen.run() is { } trajectory) trajectories.Add(trajectory);
        }
        return trajectories;
    }

    /// <summary>Direction of the first segment of the shape (matmill's calc_start_tangent).</summary>
    private static Vector2d StartTangent(Path2 poly)
    {
        var first = poly.Segments().FirstOrDefault(s => !s.Start.XY.AlmostEquals(s.End.XY, 1e-12));
        if (first == default) return new Vector2d();
        if (!first.IsArc) return new Vector2d(first.Start.XY, first.End.XY);
        var arc = first.ToArc();
        var tangent = new Vector2d(arc.Center, first.Start.XY).Normal();
        return arc.IsClockwise ? tangent.Inverted() : tangent;
    }

    /// <summary>An offset with the direction of the shape it came from, starting nearest to the shape's start.</summary>
    private static Path2 Like(Path2 offset, Path2 shape)
    {
        if (offset.IsClockwise != shape.IsClockwise) offset = offset.Reversed();
        return StartAt(offset, shape.StartPoint.XY);
    }

    /// <summary>A closed polyline that starts at its point nearest to <paramref name="p"/> (a new vertex there).</summary>
    internal static Path2 StartAt(Path2 path, Point2 p)
    {
        if (!path.Closed || path.Count < 2) return path;
        var segments = path.Segments().ToList();
        var best = (Index: 0, Distance: double.PositiveInfinity, Point: path.StartPoint.XY);
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            Point2 nearest;
            if (s.IsArc)
            {
                var arc = s.ToArc();
                var a = new Arc2F(arc.Center, s.Start.XY, s.End.XY, arc.IsClockwise ? RotationDirection.CW : RotationDirection.CCW);
                double d = 0;
                nearest = a.NearestPoint(p, ref d);
            }
            else
            {
                double d = 0;
                nearest = new Line2F(s.Start.XY, s.End.XY).NearestPoint(p, ref d);
            }
            var distance = nearest.DistanceTo(p);
            if (distance < best.Distance) best = (i, distance, nearest);
        }

        // Split the segment there, then start with the second part.
        var seg = segments[best.Index];
        var vertices = new List<Vertex>();
        double firstBulge = 0, secondBulge = 0;
        if (seg.IsArc)
        {
            var arc = seg.ToArc();
            var split = Angles.Normalize(((best.Point - arc.Center).Angle - arc.StartAngle) * Math.Sign(arc.Sweep)) * Math.Sign(arc.Sweep);
            firstBulge = Math.Tan(split / 4);
            secondBulge = Math.Tan((arc.Sweep - split) / 4);
        }
        var atStart = best.Point.AlmostEquals(seg.Start.XY, 1e-9);
        var atEnd = best.Point.AlmostEquals(seg.End.XY, 1e-9);
        var n = segments.Count;
        if (atStart || atEnd)
        {
            var from = atStart ? best.Index : (best.Index + 1) % n;
            for (var k = 0; k < n; k++) vertices.Add(path.Vertices[(from + k) % path.Count]);
            return new Path2(vertices, true);
        }
        var z = seg.Start.Z;
        vertices.Add(new Vertex(best.Point.ToPoint3(z), secondBulge));
        for (var k = 1; k < n; k++) vertices.Add(path.Vertices[(best.Index + k) % path.Count]);
        vertices.Add(path.Vertices[best.Index] with { Bulge = firstBulge });
        return new Path2(vertices, true);
    }

    // ---------------------------------------------------------------- toolpath (mop.cs)

    private static void Run(ToolpathContext ctx, TrochoOp op, Toolpath tp, Func<List<Sliced_path>> trajectories)
    {
        var messages = Logger.Collect();
        try
        {
            if (ctx.ToolDiameter <= 0)
                throw new Problem("tool diameter is zero");
            if (op.StepOver.Value <= 0 || op.StepOver.Value > 1)
                throw new Problem("stepover should be > 0 and <= 1");

            var found = trajectories();
            if (found.Count > 0) Emit(ctx, op, tp, Order(ctx, op, found));
        }
        catch (Problem problem)
        {
            messages.Add(problem.Message);
        }
        finally
        {
            foreach (var m in messages.Distinct()) tp.Warnings.Add($"{op.Name}: {m}");
        }
    }

    /// <summary>Depth levels in the order of Cut Ordering (matmill's gen_ordered_toolpath).</summary>
    private static List<Level> Order(ToolpathContext ctx, TrochoOp op, List<Sliced_path> trajectories)
    {
        var bottoms = ctx.DepthLevels(op);
        var levels = new List<Level>();
        // cut ordering == level first is meaningful only for a several pockets
        if (trajectories.Count < 2 || op.CutOrdering.Value == CutOrdering.DepthFirst)
        {
            foreach (var trajectory in trajectories)
            {
                var surface = ctx.StockSurface;
                foreach (var bottom in bottoms)
                {
                    levels.Add(new Level(trajectory, bottom, surface));
                    surface = bottom;
                }
            }
        }
        else
        {
            var surface = ctx.StockSurface;
            foreach (var bottom in bottoms)
            {
                foreach (var trajectory in trajectories)
                    levels.Add(new Level(trajectory, bottom, surface));
                surface = bottom;
            }
        }
        return levels;
    }

    private static void Emit(ToolpathContext ctx, TrochoOp op, Toolpath tp, List<Level> levels)
    {
        var cutFeed = ctx.CutFeed;
        var chordFeed = op.ChordFeedrate.Value > 0 ? op.ChordFeedrate.Value : cutFeed;
        var spiralFeed = op.SpiralFeedrate.Value > 0 ? op.SpiralFeedrate.Value : cutFeed;
        var lead = op.LeadInMove.Value ?? LeadMove.None;
        var leadFeed = lead.LeadMoveFeedrate > 0 ? lead.LeadMoveFeedrate : cutFeed;
        var threshold = op.MaxCrossoverDistance.Value * ctx.ToolDiameter;
        Point3? at = null;

        foreach (var level in levels)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var trajectory = level.Trajectory;
            if (trajectory.Count == 0) continue;
            var start = trajectory[0].FirstPoint;

            // To the start: through the cleared area when it is near (the end of the level above), else over the top.
            if (at is { } p && Point2.Distance(p.XY, start) <= threshold + 1e-9 && level.Bottom <= p.Z + 1e-9)
            {
                tp.Add(Move.Line(start.ToPoint3(p.Z), chordFeed));
                tp.Add(Move.Line(start.ToPoint3(level.Bottom), ctx.PlungeFeed));
            }
            else
            {
                if (at != null) tp.Add(Move.Rapid(at.Value with { Z = ctx.Clearance }));
                tp.Add(Move.Rapid(start.ToPoint3(ctx.Clearance)));
                var near = Math.Min(ctx.Clearance, level.Top + ctx.FastPlungeHeight);
                if (near < ctx.Clearance - 1e-9) tp.Add(Move.Rapid(start.ToPoint3(near)));
                LeadIn(tp, trajectory, start, level, lead, leadFeed, ctx.PlungeFeed, ctx.ToolRadius);
            }

            var position = start;
            foreach (var item in trajectory)
            {
                var feed = item.Item_type switch
                {
                    Sliced_path_item_type.SPIRAL => spiralFeed,
                    Sliced_path_item_type.SLICE => cutFeed,
                    _ => chordFeed,
                };
                if (item.Start.DistanceTo(position) > 1e-9) tp.Add(Move.Line(item.Start.ToPoint3(level.Bottom), feed));
                position = item.Start;
                foreach (var piece in item.Pieces)
                {
                    if (piece.To.DistanceTo(position) <= 1e-12) continue;
                    tp.Add(piece.IsArc
                        ? Move.Arc(piece.To.ToPoint3(level.Bottom), piece.Center, clockwise: !piece.Ccw, feed)
                        : Move.Line(piece.To.ToPoint3(level.Bottom), feed));
                    position = piece.To;
                }
            }
            at = position.ToPoint3(level.Bottom);
        }
        if (at is { } end) tp.Add(Move.Rapid(end with { Z = ctx.Clearance }));
    }

    /// <summary>Down to the level: a helix through the start (Spiral lead in), otherwise a plunge.</summary>
    private static void LeadIn(Toolpath tp, Sliced_path trajectory, Point2 start, Level level, LeadMove lead, double leadFeed, double plungeFeed, double toolR)
    {
        var top = tp.Moves.Count > 0 ? tp.Moves[^1].To.Z : level.Top;
        // The root slice is the cleared circle the spiral opens: the helix stays within it.
        var room = trajectory.FirstOrDefault(i => i.Item_type == Sliced_path_item_type.SPIRAL) is { } spiral
            ? spiral.LastPoint.DistanceTo(spiral.FirstPoint) + toolR
            : toolR;
        var radius = Math.Min(0.45 * toolR, room / 2);
        if (lead.LeadMoveType != LeadMoveType.Spiral || radius < 0.05 * toolR || top <= level.Bottom + 1e-9)
        {
            tp.Add(Move.Line(start.ToPoint3(level.Bottom), plungeFeed));
            return;
        }
        var centre = start + new Point2(radius, 0);
        var angle = Math.Clamp(lead.SpiralAngle > 0 ? lead.SpiralAngle : 3, 0.5, 45);
        var perTurn = 2 * Math.PI * radius * Math.Tan(angle * Math.PI / 180);
        var turns = Math.Max(1, (int)Math.Ceiling((top - level.Bottom) / perTurn));
        var opposite = centre + (centre - start);
        for (var t = 0; t < turns; t++)
        {
            var z0 = top - (top - level.Bottom) * t / turns;
            var z1 = top - (top - level.Bottom) * (t + 1) / turns;
            tp.Add(Move.Arc(opposite.ToPoint3((z0 + z1) / 2), centre, clockwise: false, leadFeed));
            tp.Add(Move.Arc(start.ToPoint3(z1), centre, clockwise: false, leadFeed));
        }
    }
}
