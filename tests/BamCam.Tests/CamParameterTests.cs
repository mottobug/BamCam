using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Machining parameters that change the generated toolpath geometry.</summary>
public class CamParameterTests
{
    private const double Tol = 1e-3;

    private static (CadDocument Doc, T Op) Setup<T>(T op, params Entity[] entities) where T : MachineOp
    {
        var doc = CadDocument.CreateNew();
        foreach (var e in entities) doc.AddEntity(e);
        op.Name = "Op";
        op.PrimitiveIds.AddRange(doc.AllEntities().Select(e => e.Id));
        op.ToolDiameter.Set(4);
        op.StockSurface.Set(0);
        op.ClearancePlane.Set(5);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    private static List<Move> Cuts(Toolpath tp) => tp.Moves.Where(m => m.Kind is MoveKind.Feed or MoveKind.ArcCW or MoveKind.ArcCCW).ToList();

    private static Toolpath Generate(CadDocument doc, MachineOp op)
    {
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.True(tp.Warnings.Count == 0, string.Join("; ", tp.Warnings));
        return tp;
    }

    /// <summary>Max X reached by cutting moves at level z (XY extent of the profile at that depth).</summary>
    private static double MaxXAt(Toolpath tp, double z) =>
        Cuts(tp).Where(m => Math.Abs(m.To.Z - z) < 1e-9).Max(m => m.To.X);

    private static ProfileOp Profile(double target, double inc)
    {
        var op = new ProfileOp();
        op.TargetDepth.Set(target);
        op.DepthIncrement.Set(inc);
        return op;
    }

    // ------------------------------------------------------------------ depth levels

    [Fact]
    public void Final_depth_increment_adds_a_light_last_pass()
    {
        var op = Profile(-3, 1);
        op.FinalDepthIncrement.Set(0.2);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var zs = Cuts(Generate(doc, op)).Select(m => Math.Round(m.To.Z, 6)).Distinct().OrderDescending().ToList();
        Assert.Equal([-1.0, -2.0, -2.8, -3.0], zs);
    }

    [Fact]
    public void Final_depth_increment_larger_than_depth_gives_single_pass()
    {
        var op = Profile(-1, 0.4);
        op.FinalDepthIncrement.Set(5);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var zs = Cuts(Generate(doc, op)).Select(m => Math.Round(m.To.Z, 6)).Distinct().ToList();
        Assert.Equal([-1.0], zs);
    }

    // ------------------------------------------------------------------ side profile

    [Fact]
    public void Slope_side_profile_widens_the_offset_with_depth()
    {
        var op = Profile(-10, 2);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.Slope, 45));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        foreach (var z in new[] { -2.0, -4, -6, -8, -10 })
            Assert.Equal(40 + 2 - z, MaxXAt(tp, z), 2);
    }

    [Fact]
    public void Convex_radius_rounds_over_the_top_edge()
    {
        var op = Profile(-6, 1);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.ConvexRadius, 4));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        // h = 2 below the top: offset grows by sqrt(R² - (R-h)²), and by R from h = R on.
        Assert.Equal(42 + Math.Sqrt(16 - 4), MaxXAt(tp, -2), 2);
        Assert.Equal(46, MaxXAt(tp, -4), 2);
        Assert.Equal(46, MaxXAt(tp, -6), 2);
        Assert.True(MaxXAt(tp, -1) < MaxXAt(tp, -2));
    }

    [Fact]
    public void Concave_radius_forms_a_cove_below_the_top_edge()
    {
        var op = Profile(-10, 2);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.ConcaveRadius, 4));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        Assert.Equal(42 + (4 - Math.Sqrt(16 - 4)), MaxXAt(tp, -2), 2);
        Assert.Equal(42 + 4, MaxXAt(tp, -4), 2);
        Assert.Equal(42 + 4, MaxXAt(tp, -10), 2);
    }

    [Fact]
    public void Adjust_cut_width_clears_the_stock_above_a_slope()
    {
        var op = Profile(-10, 2);
        op.StepOver.Set(0.5);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.Slope, 45, AdjustCutWidth: true));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        // At the first level the wall is at +2, but passes reach out to the widest offset (+10 at the bottom level).
        var xs = Cuts(tp).Where(m => Math.Abs(m.To.Z + 2) < 1e-9).Select(m => Math.Round(m.To.X, 3)).Distinct().ToList();
        Assert.Contains(44.0, xs);
        Assert.Contains(52.0, xs);
        Assert.True(xs.Count(x => x > 44 && x <= 52) >= 4);
    }

    [Fact]
    public void Cut_width_ends_each_level_at_the_wall_without_retracting_between_near_passes()
    {
        var op = Profile(-4, 2);
        op.CutWidth.Set(15);
        op.StepOver.Set(0.5);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 30)));
        op.ToolDiameter.Set(6);
        var moves = Generate(doc, op).Moves.Where(m => m.IsMotion).ToList();

        // The straight side at +X, per level from the far pass to the wall (offset 3).
        var sides = moves.Where(m => m.To.Z < -Tol && m.To.Y > 29 && m.To.Y < 31 && m.To.X > 40).Select(m => (Math.Round(m.To.Z, 3), Math.Round(m.To.X, 3))).ToList();
        Assert.Equal([(-2.0, 52.0), (-2.0, 49.0), (-2.0, 46.0), (-2.0, 43.0), (-4.0, 52.0), (-4.0, 49.0), (-4.0, 46.0), (-4.0, 43.0)], sides);
        // One retract between the levels, and one at the end of the operation.
        Assert.Equal(3, moves.Count(m => m.Kind == MoveKind.Rapid && Math.Abs(m.To.Z - 5) < Tol));
    }

    // ------------------------------------------------------------------ milling direction

    private static double SignedAreaAt(Toolpath tp, double z)
    {
        var pts = Cuts(tp).Where(m => Math.Abs(m.To.Z - z) < 1e-9).Select(m => m.To.XY).ToList();
        double a = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            a += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
        return a / 2;
    }

    [Fact]
    public void Mixed_milling_alternates_direction_per_level()
    {
        var op = Profile(-3, 1);
        op.MillingDirection.Set(MillingDirection.Mixed);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        Assert.True(SignedAreaAt(tp, -1) > 0);  // conventional outside = CCW
        Assert.True(SignedAreaAt(tp, -2) < 0);
        Assert.True(SignedAreaAt(tp, -3) > 0);
    }

    [Fact]
    public void Mixed_pocket_alternates_direction_per_level()
    {
        var op = new PocketOp();
        op.TargetDepth.Set(-2);
        op.DepthIncrement.Set(1);
        op.MillingDirection.Set(MillingDirection.Mixed);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(20, 20)));
        var tp = Generate(doc, op);
        // Wall pass (last loop of each level) runs clockwise first (conventional), then the other way.
        Point2 LastLoopTurn(double z)
        {
            var pts = Cuts(tp).Where(m => Math.Abs(m.To.Z - z) < 1e-9).Select(m => m.To.XY).ToList();
            var tail = pts.Skip(pts.Count - 5).ToList();
            return new Point2(Point2.Cross(tail[1] - tail[0], tail[2] - tail[1]), 0);
        }
        Assert.True(LastLoopTurn(-1).X * LastLoopTurn(-2).X < 0);
    }

    // ------------------------------------------------------------------ pockets

    private static PocketOp Pocket(double finish, bool atTargetOnly = false)
    {
        var op = new PocketOp();
        op.TargetDepth.Set(-2);
        op.DepthIncrement.Set(1);
        op.StepOver.Set(0.5);
        op.RegionFillStyle.Set(RegionFillStyle.OutsideOffsets);
        op.FinishStepover.Set(finish);
        op.FinishStepoverAtTargetDepth.Set(atTargetOnly);
        return op;
    }

    private static HashSet<double> XsAt(Toolpath tp, double z) =>
        Cuts(tp).Where(m => Math.Abs(m.To.Z - z) < 1e-9).Select(m => Math.Round(m.To.X, 3)).ToHashSet();

    [Fact]
    public void Finish_stepover_leaves_a_light_wall_pass()
    {
        var op = Pocket(0.1);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(30, 20)));
        var tp = Generate(doc, op);
        // Wall at x = 28 (tool radius 2); the ring before it is only 0.4 (0.1 × 4) inside.
        var xs = XsAt(tp, -2);
        Assert.Contains(28.0, xs);
        Assert.Contains(27.6, xs);
        Assert.DoesNotContain(26.0, xs);
    }

    [Fact]
    public void Finish_stepover_at_target_depth_only_affects_the_last_level()
    {
        var op = Pocket(0.1, atTargetOnly: true);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(30, 20)));
        var tp = Generate(doc, op);
        Assert.Contains(26.0, XsAt(tp, -1));
        Assert.DoesNotContain(27.6, XsAt(tp, -1));
        Assert.Contains(27.6, XsAt(tp, -2));
    }

    private static (CadDocument, PocketOp) PocketWithIsland(RegionFillStyle style)
    {
        var op = new PocketOp();
        op.TargetDepth.Set(-1);
        op.DepthIncrement.Set(1);
        op.StepOver.Set(0.5);
        op.RegionFillStyle.Set(style);
        return Setup(op, RectEntity.FromCorners(new(0, 0), new(60, 40)), new CircleEntity { Center = new(30, 20, 0), Diameter = 10 });
    }

    /// <summary>Distinct radii of arcs centred on the island (rings that follow the island).</summary>
    private static HashSet<double> IslandRingRadii(Toolpath tp)
    {
        var radii = new HashSet<double>();
        Point3? pos = null;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.IsArc && pos is { } p && Point2.Distance(m.Center, new Point2(30, 20)) < 0.05)
                radii.Add(Math.Round(Point2.Distance(p.XY, m.Center), 1));
            pos = m.To;
        }
        return radii;
    }

    [Fact]
    public void Inside_offsets_grow_rings_around_islands()
    {
        var (doc, op) = PocketWithIsland(RegionFillStyle.InsideOffsets);
        var radii = IslandRingRadii(Generate(doc, op));
        Assert.Contains(7.0, radii);          // island wall: 5 + tool radius 2
        Assert.True(radii.Count >= 3, string.Join(",", radii));
    }

    [Fact]
    public void Outside_offsets_follow_the_outer_wall_and_cut_the_island_once()
    {
        var (doc, op) = PocketWithIsland(RegionFillStyle.OutsideOffsets);
        var tp = Generate(doc, op);
        Assert.Equal([7.0], IslandRingRadii(tp));
        // Rectangular rings: the walls at x = 2 (outermost) and x = 4 (next ring) are both cut.
        var xs = XsAt(tp, -1);
        Assert.Contains(2.0, xs);
        Assert.Contains(4.0, xs);
    }

    [Fact]
    public void Pocket_tangent_lead_out_adds_an_exit_arc()
    {
        var op = new PocketOp();
        op.TargetDepth.Set(-1);
        op.DepthIncrement.Set(1);
        op.LeadOutMove.Set(new LeadMove(LeadMoveType.Tangent, 0, 1.5));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(30, 20)));
        var tp = Generate(doc, op);
        Point3? pos = null;
        var found = false;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.IsArc && pos is { } p && Math.Abs(Point2.Distance(p.XY, m.Center) - 1.5) < 1e-6) found = true;
            pos = m.To;
        }
        Assert.True(found);
        // Exit arcs stay inside the pocket.
        Assert.All(Cuts(tp), m => Assert.InRange(m.To.X, 2 - Tol, 28 + Tol));
    }

    /// <summary>Chord length of the feed moves that start and end at <paramref name="z"/> (flat laps).</summary>
    private static double FlatLengthAt(Toolpath tp, double z)
    {
        Point3? pos = null;
        double len = 0;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (pos is { } p && m.Kind is MoveKind.Feed or MoveKind.ArcCW or MoveKind.ArcCCW &&
                Math.Abs(p.Z - z) < 1e-6 && Math.Abs(m.To.Z - z) < 1e-6)
                len += Point2.Distance(p.XY, m.To.XY);
            pos = m.To;
        }
        return len;
    }

    [Fact]
    public void Spiral_lead_with_angle_0_runs_one_helix_down_to_the_final_lap()
    {
        // As in CamBam: one helix lap per depth increment without a flat lap in between; flat only at the target depth.
        var op = Profile(-12, 6);
        op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 0));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(30, 20)));
        var tp = Generate(doc, op);
        Assert.Equal(0, FlatLengthAt(tp, -6), 6);
        var lap = FlatLengthAt(tp, -12);
        Assert.InRange(lap, 100, 130); // one lap around the offset rectangle
        // No retract or plunge between the levels: the depth only goes down while cutting.
        var below = tp.Moves.Where(m => m.IsMotion).SkipWhile(m => m.To.Z > -1e-9).TakeWhile(m => m.Kind != MoveKind.Rapid).ToList();
        Assert.Equal(-12, below[^1].To.Z, 6);
        Assert.All(below.Zip(below.Skip(1)), p => Assert.True(p.Second.To.Z <= p.First.To.Z + 1e-9));
    }

    [Fact]
    public void Spiral_lead_with_an_angle_keeps_the_lap_at_every_level()
    {
        // A steeper ramp is shorter than one lap: each level is finished at its depth.
        var op = Profile(-12, 6);
        op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 30));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(30, 20)));
        var tp = Generate(doc, op);
        Assert.True(FlatLengthAt(tp, -6) > 100);
    }

    [Fact]
    public void Corner_overcut_reaches_the_corners_of_an_inside_profile()
    {
        var op = Profile(-1, 1);
        op.InsideOutside.Set(InsideOutside.Inside);
        op.CornerOvercut.Set(true);
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 30)));
        op.ToolDiameter.Set(6);
        var cuts = Cuts(Generate(doc, op)).Where(m => m.To.Z < -Tol).ToList();

        // All four corners: the tool centre goes 3 * (sqrt(2) - 1) beyond the loop corner, and its edge reaches the corner.
        foreach (var (x, y) in new[] { (0.0, 0.0), (40.0, 0.0), (40.0, 30.0), (0.0, 30.0) })
            Assert.Contains(cuts, m => Math.Abs(Point2.Distance(m.To.XY, new Point2(x, y)) - 3) < Tol);
        // Nothing goes further into the part than the tool radius beyond the walls.
        Assert.All(cuts, m => Assert.InRange(m.To.X, 3 - 3 * (Math.Sqrt(2) - 1) / Math.Sqrt(2) - Tol, 37 + 3 * (Math.Sqrt(2) - 1) / Math.Sqrt(2) + Tol));
    }

    [Fact]
    public void Corner_overcut_leaves_the_outer_corners_of_an_outside_profile_alone()
    {
        var op = Profile(-1, 1);
        op.CornerOvercut.Set(true);
        // An L: only the notch at (20, 15) is an inside corner of the part.
        var (doc, _) = Setup(op, new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 15), new(20, 15), new(20, 30), new(0, 30)], closed: true)));
        op.ToolDiameter.Set(6);
        var cuts = Cuts(Generate(doc, op)).Where(m => m.To.Z < -Tol).ToList();

        Assert.Contains(cuts, m => Math.Abs(Point2.Distance(m.To.XY, new Point2(20, 15)) - 3) < Tol);
        // The round outer corners keep their distance of the tool radius.
        Assert.All(cuts, m => Assert.True(Point2.Distance(m.To.XY, new Point2(0, 0)) >= 3 - Tol));
        Assert.All(cuts, m => Assert.True(Point2.Distance(m.To.XY, new Point2(40, 15)) >= 3 - Tol));
    }

    [Fact]
    public void Roughing_clearance_leaves_stock_for_a_finishing_operation_also_with_a_cut_width()
    {
        Point3[] Wall(double clearance)
        {
            var op = Profile(-1, 1);
            op.CutWidth.Set(15);
            op.StepOver.Set(0.5);
            op.RoughingClearance.Set(clearance);
            var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 30)));
            op.ToolDiameter.Set(6);
            return Cuts(Generate(doc, op)).Select(m => m.To).ToArray();
        }
        Assert.Equal(43, Wall(0).Min(p => p.X > 40 ? p.X : double.MaxValue), 6);
        Assert.Equal(43.5, Wall(0.5).Min(p => p.X > 40 ? p.X : double.MaxValue), 6);
    }

    // ------------------------------------------------------------------ open shapes

    [Fact]
    public void Open_profile_spiral_lead_ramps_along_the_path()
    {
        var op = Profile(-2, 1);
        op.LeadInMove.Set(new LeadMove(LeadMoveType.Spiral, 0));
        var line = new LineEntity();
        line.Points.AddRange([new Point3(0, 0, 0), new Point3(50, 0, 0)]);
        var (doc, _) = Setup(op, line);
        var tp = Generate(doc, op);
        // The first move below the stock surface travels along the path while descending (no vertical plunge):
        // angle 0 = ramp over the whole path length, here the single 50 mm segment.
        var cuts = Cuts(tp);
        var firstBelow = cuts.First(m => m.To.Z < -1e-9);
        Assert.Equal(50, firstBelow.To.X, 6);
        Assert.Equal(-1, firstBelow.To.Z, 6);
        // ...then back to the start at depth before the actual pass.
        Assert.Equal(0, cuts[cuts.IndexOf(firstBelow) + 1].To.X, 6);
    }

    [Fact]
    public void Open_profile_tangent_lead_goes_to_the_right_of_travel()
    {
        var op = Profile(-1, 1);
        op.LeadInMove.Set(new LeadMove(LeadMoveType.Tangent, 0, 2));
        var line = new LineEntity();
        line.Points.AddRange([new Point3(0, 0, 0), new Point3(50, 0, 0)]);
        var (doc, _) = Setup(op, line);
        var tp = Generate(doc, op);
        var arc = tp.Moves.First(m => m.IsArc);
        Assert.Equal(-2, arc.Center.Y, 6); // travelling +X, right side is -Y
    }
}
