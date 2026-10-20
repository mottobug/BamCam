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
    public void Slope_side_profile_widens_the_offset_towards_the_top()
    {
        var op = Profile(-10, 2);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.Slope, 45));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        foreach (var z in new[] { -2.0, -4, -6, -8, -10 })
            Assert.Equal(40 + 2 + (z + 10), MaxXAt(tp, z), 2);
    }

    [Fact]
    public void Convex_radius_rounds_over_the_top_edge()
    {
        var op = Profile(-6, 1);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.ConvexRadius, 4));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        // h = 2 below the top: offset reduced by R - sqrt(R² - (R-h)²).
        Assert.Equal(42 - (4 - Math.Sqrt(16 - 4)), MaxXAt(tp, -2), 2);
        Assert.Equal(42, MaxXAt(tp, -4), 2);
        Assert.Equal(42, MaxXAt(tp, -6), 2);
        Assert.True(MaxXAt(tp, -1) < MaxXAt(tp, -2));
    }

    [Fact]
    public void Concave_radius_forms_a_cove_at_the_bottom()
    {
        var op = Profile(-10, 2);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.ConcaveRadius, 4));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        Assert.Equal(42 + 4, MaxXAt(tp, -10), 2);
        Assert.Equal(42 + (4 - Math.Sqrt(16 - 4)), MaxXAt(tp, -8), 2);
        Assert.Equal(42, MaxXAt(tp, -6), 2);
    }

    [Fact]
    public void Adjust_cut_width_clears_the_stock_above_a_slope()
    {
        var op = Profile(-10, 2);
        op.StepOver.Set(0.5);
        op.SideProfile.Set(new SideProfile(SideProfileMethod.Slope, 45, AdjustCutWidth: true));
        var (doc, _) = Setup(op, RectEntity.FromCorners(new(0, 0), new(40, 40)));
        var tp = Generate(doc, op);
        // At the bottom the profile is at +0, but passes reach out to the widest offset (+8 at the top level).
        var xs = Cuts(tp).Where(m => Math.Abs(m.To.Z + 10) < 1e-9).Select(m => Math.Round(m.To.X, 3)).Distinct().ToList();
        Assert.Contains(42.0, xs);
        Assert.Contains(50.0, xs);
        Assert.True(xs.Count(x => x > 42 && x <= 50) >= 4);
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
