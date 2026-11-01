using System.Globalization;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class TurningOpsTests
{
    /// <summary>Drawing with the spindle axis through (40, 0); lathe Z = drawing X - 40, radius = -drawing Y.</summary>
    private static (CadDocument Doc, Entity Shape) Drawing(params Point2[] latheZX)
    {
        var doc = new CadDocument();
        doc.Machining.MachiningOrigin = new OptionalPoint(new Point3(40, 0, 0));
        var shape = new PolylineEntity(Path2.FromPoints(latheZX.Select(p => new Point2(p.X + 40, -p.Y)), false));
        doc.AddEntity(shape);
        return (doc, shape);
    }

    private static T Add<T>(CadDocument doc, Entity shape, T op) where T : TurningOp
    {
        op.Name = "op";
        op.PrimitiveIds.Add(shape.Id);
        op.ToolDiameter.Set(2);
        op.StockSurface.Set(10);
        op.ClearancePlane.Set(12);
        doc.ActivePart.Operations.Add(op);
        return op;
    }

    /// <summary>(lathe Z, radius) of the moves.</summary>
    private static List<(MoveKind Kind, double Z, double X)> Lathe(Toolpath tp) =>
        tp.Moves.Where(m => m.IsMotion).Select(m => (m.Kind, m.To.X - 40, -m.To.Y)).ToList();

    [Fact]
    public void Facing_steps_from_the_stock_end_to_the_face_across_the_axis()
    {
        var (doc, shape) = Drawing(new(-30, 8), new(0, 8));
        doc.Machining.Stock = new Stock { PMin = new Point3(-40, -10, -10), PMax = new Point3(3, 10, 10) };
        var op = Add(doc, shape, new LatheFacingOp());
        op.DepthIncrement.Set(1);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = Lathe(tp).Where(m => m.Kind == MoveKind.Feed && Math.Abs(m.X + 1) < 1e-9).ToList();
        // Faces at Z 2, 1, 0 (stock end 3, step 1), each radially past the axis (tip at -nose radius).
        Assert.Equal(new[] { 2.0, 1.0, 0.0 }, cuts.Select(c => Math.Round(c.Z, 9)));
        Assert.True(Lathe(tp).Where(m => m.Kind == MoveKind.Feed).All(m => m.Z >= -1e-9), "never cuts into the part");
    }

    [Fact]
    public void Grooving_plunges_side_by_side_with_pecks_and_cleans_the_bottom()
    {
        // Groove from Z -20 to -14, bottom radius 6; blade width 2 (tool diameter).
        var (doc, shape) = Drawing(new(-20, 10), new(-20, 6), new(-14, 6), new(-14, 10));
        var op = Add(doc, shape, new LatheGrooveOp());
        op.DepthIncrement.Set(1.5);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var moves = Lathe(tp);
        var feeds = moves.Where(m => m.Kind == MoveKind.Feed).ToList();
        // The left blade corner stays within [-20, -16] so that the 2 mm blade stays inside the groove.
        Assert.Equal(-20, feeds.Min(f => f.Z), 9);
        Assert.Equal(-16, feeds.Max(f => f.Z), 9);
        Assert.Equal(6, feeds.Min(f => f.X), 9);
        // Pecks of 1.5 from the stock surface 10: 8.5, 7, 6 at each plunge.
        Assert.Contains(feeds, f => Math.Abs(f.X - 8.5) < 1e-9);
        // Finish pass along the bottom back to the first plunge.
        Assert.Equal((MoveKind.Feed, -20.0, 6.0), (feeds[^1].Kind, Math.Round(feeds[^1].Z, 9), Math.Round(feeds[^1].X, 9)));
    }

    [Fact]
    public void Grooves_narrower_than_the_blade_are_refused()
    {
        var (doc, shape) = Drawing(new(-10, 10), new(-10, 6), new(-9, 6), new(-9, 10));
        var op = Add(doc, shape, new LatheGrooveOp());
        Assert.NotEmpty(ToolpathGenerator.Generate(doc, op).Warnings);
    }

    private static (CadDocument Doc, LatheThreadOp Op, Dictionary<MachineOp, Toolpath> Paths) Thread()
    {
        // M12 x 1.5 outside thread over 20 mm at the end of the part.
        var (doc, shape) = Drawing(new(-20, 6), new(0, 6));
        var op = Add(doc, shape, new LatheThreadOp());
        op.Pitch.Set(1.5);
        return (doc, op, new() { [op] = ToolpathGenerator.Generate(doc, op) });
    }

    private static PostProcessorDefinition TurnPost(string cycle) => new()
    {
        ClearancePlaneAxis = "X", LatheXMode = LatheXMode.Diameter,
        Rapid = "{$g0} {$_x} {$_z}", FeedMove = "{$g1} {$_f} {$_x} {$_z}", ThreadCycle = cycle,
    };

    /// <summary>Finishing pass over a cylinder of radius 7 with a 1 mm chamfer at the face (nose radius 0.4).</summary>
    private static string[] ChamferedShaft(NoseCompensation comp, LatheCutDirection direction = LatheCutDirection.RightHand)
    {
        var (doc, shape) = Drawing(new(0, 0), new(0, 6), new(-1, 7), new(-20, 7), new(-20, 10));
        var op = Add(doc, shape, new LatheOp());
        op.ToolDiameter.Set(0.8);
        op.RoughingFinishing.Set(RoughingFinishing.Finishing);
        op.NoseCompensation.Set(comp);
        op.LatheCutDirection.Set(direction);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var post = TurnPost("");
        post.ArcCW = "{$g2} {$_f} {$_x} {$_z} {$i} {$k}";
        post.ArcCCW = "{$g3} {$_f} {$_x} {$_z} {$i} {$k}";
        return GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post).Text.Split("\r\n");
    }

    [Fact]
    public void Machine_nose_compensation_outputs_the_part_contour_between_G42_and_G40()
    {
        var lines = ChamferedShaft(NoseCompensation.Machine);
        var on = Array.IndexOf(lines, "G42");
        var off = Array.IndexOf(lines, "G40");
        Assert.True(on >= 0 && off > on, string.Join("\n", lines));
        var positions = Positions(lines);
        // Programmed points on the part contour (diameter): axis at the face, chamfer, cylinder, shoulder.
        Assert.Equal([(0.0, 0.0), (12.0, 0.0), (14.0, -1.0), (14.0, -20.0), (20.0, -20.0)], positions.Skip(on + 1).Take(off - on - 1));
        // Axial start-up move: the tool waits in front of the face, at least two nose radii away.
        Assert.Equal(0, positions[on].X, 9);
        Assert.True(positions[on].Z >= 0.9, $"start-up from Z {positions[on].Z}");
        // G40 is followed by the radial retract.
        Assert.StartsWith("G0 X", lines[off + 1]);

        // Computed: no compensation codes, the tip path is offset at the chamfer.
        var computed = ChamferedShaft(NoseCompensation.Computed);
        Assert.DoesNotContain(computed, l => l is "G40" or "G41" or "G42");
        Assert.DoesNotContain(Positions(computed), p => p == (14.0, -1.0));
    }

    /// <summary>Position (diameter X, Z) after each line, following modal (omitted) axis words.</summary>
    private static List<(double X, double Z)> Positions(string[] lines)
    {
        double x = double.NaN, z = double.NaN;
        var result = new List<(double, double)>();
        foreach (var line in lines)
        {
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word[0] == 'X') x = double.Parse(word[1..], CultureInfo.InvariantCulture);
                if (word[0] == 'Z') z = double.Parse(word[1..], CultureInfo.InvariantCulture);
            }
            result.Add((Math.Round(x, 4), Math.Round(z, 4)));
        }
        return result;
    }

    [Fact]
    public void Left_hand_cutting_uses_G41()
    {
        var lines = ChamferedShaft(NoseCompensation.Machine, LatheCutDirection.LeftHand);
        Assert.Contains("G41", lines);
        Assert.DoesNotContain("G42", lines);
    }

    [Fact]
    public void Threads_are_output_as_G76_cycle()
    {
        var (doc, op, paths) = Thread();
        Assert.Empty(paths[op].Warnings);
        var lines = GCodePoster.Post(doc, paths, TurnPost("G76 {$_z} {$p} {$i} {$j} {$k} {$r} {$q} {$h}")).Text.Split("\r\n");
        var g76 = Assert.Single(lines, l => l.StartsWith("G76"));
        // Drive line 0.5 above the major diameter, metric depth 0.6134 x 1.5, ends at Z -20.
        Assert.Equal("G76 Z-20.0 P1.5 I-0.5 J0.1 K0.9201 R2.0 Q29.5 H1.0", g76);
        // Before the cycle the tool stands at the drive line (diameter 13), two pitches in front of the thread.
        var beforeCycle = string.Join(" ", lines.Take(Array.IndexOf(lines, g76)));
        Assert.Contains("X13.0", beforeCycle);
        Assert.Contains("Z3.0", beforeCycle);
    }

    [Fact]
    public void Without_a_cycle_threads_are_cut_in_single_passes()
    {
        var (doc, _, paths) = Thread();
        var lines = GCodePoster.Post(doc, paths, TurnPost("")).Text.Split("\r\n");
        var passes = lines.Where(l => l.StartsWith("G33")).ToList();
        // Depths 0.1 x sqrt(n) up to 0.9201, plus the full depth and one spring pass.
        Assert.True(passes.Count > 50 && passes.All(p => p == "G33 Z-20.0 K1.5"), string.Join("\n", passes.Take(3)));
        var depthLines = lines.Where(l => l.StartsWith("G0 X")).Select(l => double.Parse(l[4..].Split(' ')[0], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(2 * (6 - 0.9201), depthLines.Min(), 3);
    }

    [Fact]
    public void Drilling_pecks_along_the_axis_with_full_retract()
    {
        var (doc, shape) = Drawing(new(-15, 0), new(0, 0));
        var op = Add(doc, shape, new LatheDrillOp());
        op.PeckDistance.Set(5);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var moves = Lathe(tp);
        Assert.Equal(new[] { -5.0, -10.0, -15.0 }, moves.Where(m => m.Kind == MoveKind.Feed).Select(m => Math.Round(m.Z, 9)));
        Assert.All(moves.SkipWhile(m => m.X > 1e-9), m => Assert.True(Math.Abs(m.X) < 1e-9 || m.Kind == MoveKind.Rapid));
        // At 2 mm in front of the hole: approach, full retract after the first two pecks, final retract.
        Assert.Equal(4, moves.Count(m => m.Kind == MoveKind.Rapid && Math.Abs(m.Z - 2) < 1e-9 && Math.Abs(m.X) < 1e-9));
    }

    [Fact]
    public void Turning_operations_are_saved_with_their_own_types()
    {
        var (doc, shape) = Drawing(new(-20, 6), new(0, 6));
        var thread = Add(doc, shape, new LatheThreadOp());
        thread.Pitch.Set(2);
        Add(doc, shape, new LatheFacingOp());
        Add(doc, shape, new LatheGrooveOp());
        Add(doc, shape, new LatheDrillOp());
        var xml = CbWriter.ToString(doc);
        foreach (var type in new[] { "BamCamLatheThread", "BamCamLatheFacing", "BamCamLatheGroove", "BamCamLatheDrill" })
            Assert.Contains($"<mop xsi:type=\"{type}\">", xml);
        var ops = CbReader.Parse(xml).Parts.SelectMany(p => p.Operations).ToList();
        Assert.Equal(2, ops.OfType<LatheThreadOp>().Single().Pitch.Value);
        Assert.Single(ops.OfType<LatheFacingOp>());
        Assert.Single(ops.OfType<LatheGrooveOp>());
        Assert.Single(ops.OfType<LatheDrillOp>());
        Assert.All(ops, o => Assert.Equal(WorkPlane.XZ, o.WorkPlane.Value));
    }

    [Fact]
    public void Simulation_leaves_the_turned_part()
    {
        // Step: radius 8 from Z -40 to -20, radius 5 to Z 0; stock radius 10 from Z -40 to 3.
        var (doc, shape) = Drawing(new(-40, 8), new(-20, 8), new(-20, 5), new(0, 5));
        doc.Machining.Stock = new Stock { PMin = new Point3(-40, -10, -10), PMax = new Point3(3, 10, 10) };
        var turn = Add(doc, shape, new LatheOp());
        turn.DepthIncrement.Set(0.5);
        var face = Add(doc, shape, new LatheFacingOp());
        var paths = doc.ActivePart.Operations.ToDictionary(o => o, o => ToolpathGenerator.Generate(doc, o));
        var remaining = TurningSimulation.Remaining(doc, paths);
        Assert.NotEmpty(remaining);

        bool Material(double z, double radius) => remaining.Any(p => p.Contains(new Point2(40 + z, -radius)));
        Assert.True(Material(-30, 7.5), "large diameter stays");
        Assert.True(Material(-10, 4.5), "small diameter stays");
        Assert.False(Material(-30, 9.5), "stock above the large diameter is turned off");
        Assert.False(Material(-10, 7), "stock above the small diameter is turned off");
        Assert.False(Material(1.5, 2), "the stock in front of the end face is faced off");
        // Shown on both sides of the axis.
        Assert.Contains(remaining, p => p.Contains(new Point2(40 - 30, 7.5)));
        _ = face;
    }
}