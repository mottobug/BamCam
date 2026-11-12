using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Thread milling and tapping (issue 9).</summary>
public class ThreadMillTests
{
    /// <summary>An M8 hole (circle of diameter 8 at 20, 10), 10 deep, and the operation on it with a 6 mm cutter.</summary>
    private static (CadDocument Doc, T Op) Setup<T>(Action<T>? set = null) where T : DepthOp, new()
    {
        var doc = CadDocument.CreateNew();
        var hole = doc.AddEntity(new CircleEntity { Center = new Point3(20, 10, 0), Diameter = 8 });
        var op = new T { Name = "thread" };
        op.PrimitiveIds.Add(hole.Id);
        op.ToolDiameter.Set(6);
        op.StockSurface.Set(0);
        op.TargetDepth.Set(-10);
        op.ClearancePlane.Set(5);
        op.SpindleSpeed.Set(1000);
        set?.Invoke(op);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    /// <summary>The helical cutting arcs: from where, to where, which way.</summary>
    private static List<(Point3 From, Move Move)> Helix(Toolpath tp)
    {
        var result = new List<(Point3, Move)>();
        Point3? at = null;
        foreach (var m in tp.Moves)
        {
            if (at is { } from && m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW && Math.Abs(m.To.Z - from.Z) > 1e-9
                && Point2.Distance(m.Center, new Point2(20, 10)) < 1e-9)
                result.Add((from, m));
            if (m.IsMotion) at = m.To;
        }
        return result;
    }

    private static double Radius(Move m) => Point2.Distance(m.To.XY, new Point2(20, 10));

    [Theory]
    [InlineData(ThreadHand.Right, ThreadMillDirection.BottomUp, MoveKind.ArcCCW, 1)]
    [InlineData(ThreadHand.Right, ThreadMillDirection.TopDown, MoveKind.ArcCW, -1)]
    [InlineData(ThreadHand.Left, ThreadMillDirection.BottomUp, MoveKind.ArcCW, 1)]
    [InlineData(ThreadHand.Left, ThreadMillDirection.TopDown, MoveKind.ArcCCW, -1)]
    public void The_helix_turns_and_climbs_for_the_hand_of_the_thread(ThreadHand hand, ThreadMillDirection direction, MoveKind turn, int climb)
    {
        var (doc, op) = Setup<ThreadMillOp>(o =>
        {
            o.Pitch.Set(1.25);
            o.ThreadHand.Set(hand);
            o.CutDirection.Set(direction);
        });

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Warnings);
        var helix = Helix(tp);
        // 10 deep at 1.25: 8 turns, as half turns.
        Assert.Equal(16, helix.Count);
        Assert.All(helix, h => Assert.Equal(turn, h.Move.Kind));
        Assert.All(helix, h => Assert.Equal(climb, Math.Sign(h.Move.To.Z - h.From.Z)));
        Assert.All(helix, h => Assert.Equal(1.25 / 2, Math.Abs(h.Move.To.Z - h.From.Z), 9));
        // Internal M8 with a 6 mm cutter, full depth in one pass: the tool centre runs at 4 - 3 = 1 from the axis.
        Assert.All(helix, h => Assert.Equal(1, Radius(h.Move), 9));
        Assert.Equal(-10, helix.Min(h => Math.Min(h.From.Z, h.Move.To.Z)), 9);
    }

    [Fact]
    public void An_internal_thread_comes_in_from_the_centre_and_leaves_to_it()
    {
        var (doc, op) = Setup<ThreadMillOp>(o => o.Pitch.Set(1.25));

        var cuts = ToolpathGenerator.Generate(doc, op).Moves.Where(m => m.IsMotion && m.Kind != MoveKind.Rapid).ToList();

        // Down at the centre of the hole, a half circle onto the thread, ..., a half circle back to the centre.
        var firstArc = cuts.First(m => m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW);
        var plunge = cuts[cuts.IndexOf(firstArc) - 1];
        Assert.Equal(new Point2(20, 10), plunge.To.XY);
        Assert.Equal(-10, plunge.To.Z, 9);
        Assert.Equal(new Point2(21, 10), firstArc.To.XY);
        Assert.Equal(new Point2(20.5, 10), firstArc.Center);
        Assert.Equal(new Point2(20, 10), cuts.Last(m => m.Kind is MoveKind.ArcCW or MoveKind.ArcCCW).To.XY);
    }

    [Fact]
    public void Radial_passes_go_out_to_the_full_depth()
    {
        var (doc, op) = Setup<ThreadMillOp>(o =>
        {
            o.Pitch.Set(1);
            o.RadialPasses.Set(3);
        });

        var radii = Helix(ToolpathGenerator.Generate(doc, op)).Select(h => Math.Round(Radius(h.Move), 6)).Distinct().ToList();

        // ISO internal depth 0.5413 for pitch 1, in thirds, ending at 4 - 3 = 1.
        Assert.Equal(new[] { 1 - 0.5413 * 2 / 3, 1 - 0.5413 / 3, 1 }.Select(r => Math.Round(r, 6)), radii);
    }

    [Fact]
    public void An_external_thread_runs_outside_the_minor_diameter()
    {
        var (doc, op) = Setup<ThreadMillOp>(o =>
        {
            o.Pitch.Set(1.25);
            o.ThreadType.Set(ThreadType.External);
        });

        var helix = Helix(ToolpathGenerator.Generate(doc, op));

        // Minor diameter 8 - 2 x 0.6134 x 1.25, plus the cutter radius 3.
        Assert.All(helix, h => Assert.Equal(4 - 0.6134 * 1.25 + 3, Radius(h.Move), 9));
    }

    [Fact]
    public void A_cutter_with_several_teeth_turns_once_per_cutter_length()
    {
        var (doc, op) = Setup<ThreadMillOp>(o =>
        {
            o.Pitch.Set(1.25);
            o.CutterLength.Set(5);
        });

        var helix = Helix(ToolpathGenerator.Generate(doc, op));

        // Two turns of one revolution each, at -10 and -5 (bottom up), all on one helix (whole pitches apart).
        Assert.Equal(4, helix.Count);
        Assert.Equal([-10.0, -5.0], helix.Where((_, i) => i % 2 == 0).Select(h => Math.Round(h.From.Z, 9)));
        // A cutter longer than the thread: one turn at the bottom does it all.
        Assert.Equal([(-10.0, -8.75)], ThreadMillGenerator.Levels(0, -10, 1.25, 20, up: true));
        // Top down: the same turns from the top one down, each turning downwards.
        Assert.Equal([(-3.75, -5.0), (-8.75, -10.0)], ThreadMillGenerator.Levels(0, -10, 1.25, 5, up: false));
    }

    [Fact]
    public void A_tool_too_large_for_the_thread_is_reported()
    {
        var (doc, op) = Setup<ThreadMillOp>(o => o.ToolDiameter.Set(9));

        Assert.Contains(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("too large"));
    }

    [Fact]
    public void Thread_milling_and_tapping_are_saved_and_read_back()
    {
        var (doc, op) = Setup<ThreadMillOp>(o =>
        {
            o.Pitch.Set(1.25);
            o.ThreadHand.Set(ThreadHand.Left);
            o.CutDirection.Set(ThreadMillDirection.TopDown);
            o.RadialPasses.Set(2);
        });
        var tap = new TapOp { Name = "tap" };
        tap.Pitch.Set(0.8);
        doc.ActivePart.Operations.Add(tap);

        var xml = CbWriter.ToString(doc);
        var again = CbReader.Parse(xml).ActivePart.Operations;

        Assert.Contains("xsi:type=\"BamCamThreadMill\"", xml);
        var thread = Assert.IsType<ThreadMillOp>(again[0]);
        Assert.Equal(ThreadHand.Left, thread.ThreadHand.Value);
        Assert.Equal(ThreadMillDirection.TopDown, thread.CutDirection.Value);
        Assert.Equal(2, thread.RadialPasses.Value);
        Assert.Equal(0.8, Assert.IsType<TapOp>(again[1]).Pitch.Value);
        Assert.True(thread.OnlyInBamCam);
    }

    [Fact]
    public void Tapping_posts_G84_with_pitch_times_speed_as_feed()
    {
        var (doc, op) = Setup<TapOp>(o => o.Pitch.Set(1.25));
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);

        var post = PostProcessorDefinition.BuiltIn().Single(p => p.Name == "Mach3");
        var result = GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post);

        var lines = result.Text.Split("\r\n");
        var line = lines.Single(l => l.StartsWith("G84"));
        double Word(string l, char letter) => double.Parse(l.Split(' ').Single(w => w.StartsWith(letter))[1..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(-10, Word(line, 'Z'));
        Assert.Equal(1.5, Word(line, 'R'));
        Assert.Equal(1250, Word(line, 'F')); // 1.25 pitch × 1000 rpm
        // Over the hole before the cycle (X and Y are modal, as for G81).
        var before = lines.Take(Array.IndexOf(lines, line)).Last(l => l.Contains('X'));
        Assert.Equal(20, Word(before, 'X'));
        Assert.Equal(10, Word(before, 'Y'));
    }

    [Fact]
    public void A_post_without_a_tapping_cycle_leaves_the_holes_out_and_says_so()
    {
        var (doc, op) = Setup<TapOp>(o => o.Pitch.Set(1.25));
        var tp = ToolpathGenerator.Generate(doc, op);

        var post = PostProcessorDefinition.BuiltIn().Single(p => p.Name == "LinuxCNC");
        var result = GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post);

        Assert.DoesNotContain(result.Text.Split("\r\n"), l => l.StartsWith("G8"));
        Assert.Contains(result.Warnings, w => w.Contains("no tapping cycle"));
    }

    [Fact]
    public void Tapping_needs_a_spindle_speed()
    {
        var (doc, op) = Setup<TapOp>(o => o.SpindleSpeed.Set(0));

        Assert.Contains(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("spindle speed"));
    }
}
