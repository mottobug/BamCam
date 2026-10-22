using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class LatheTests
{
    // Step profile in lathe coordinates (Z, radius): radius 5 from Z 0 to -20, shoulder, radius 8 to Z -40.
    private static readonly List<Point2> Step = [new(0, 5), new(-20, 5), new(-20, 8), new(-40, 8)];

    [Fact]
    public void Tool_contour_keeps_the_nose_radius_from_the_profile()
    {
        var contour = LatheGenerator.ToolContour(Step, 1);
        Assert.Equal(0, contour[0].X, 6);
        Assert.Equal(6, contour[0].Y, 6);
        Assert.Equal(-40, contour[^1].X, 6);
        Assert.Equal(9, contour[^1].Y, 6);
        // Every contour point is exactly one nose radius away from the profile (never closer).
        var profile = Path2.FromPoints(Step, false);
        foreach (var p in contour) Assert.True(profile.DistanceTo(p) >= 1 - 1e-3, $"{p} too close");
        // In front of the shoulder the tool stops one radius before it and rises along the face.
        Assert.Contains(contour, p => Math.Abs(p.X + 19) < 1e-6 && Math.Abs(p.Y - 6) < 1e-6);
        Assert.Contains(contour, p => Math.Abs(p.X + 19) < 1e-6 && Math.Abs(p.Y - 8) < 1e-6);
    }

    [Fact]
    public void Roughing_levels_end_at_the_shoulder()
    {
        var contour = LatheGenerator.ToolContour(Step, 1);
        // Right hand: cut towards -Z. Level 7.5 lies between the small (6) and the large (9) diameter.
        var intervals = LatheGenerator.Intervals(contour, 7.5, -1);
        var single = Assert.Single(intervals);
        Assert.True(single.From > 0, "starts beyond the end of the part");
        Assert.InRange(single.To, -19.01, -18.99);
        // Above the large diameter nothing is cut, below the small one there is no material either.
        Assert.Single(LatheGenerator.Intervals(contour, 9.5, -1));
        Assert.Empty(LatheGenerator.Intervals(contour, 5.5, -1));
    }

    private static CadDocument LatheDrawing(RoughingFinishing mode)
    {
        var doc = new CadDocument();
        // Profile below the spindle axis (drawing Y = -radius), part end at drawing X 40 (lathe Z 0).
        var profile = new PolylineEntity(Path2.FromPoints(Step.Select(p => new Point2(p.X + 40, -p.Y)), false));
        doc.AddEntity(profile);
        doc.Machining.MachiningOrigin = new OptionalPoint(new Point3(40, 0, 0));
        var op = new LatheOp { Name = "turn" };
        op.PrimitiveIds.Add(profile.Id);
        op.ToolDiameter.Value = 2;
        op.ToolDiameter.State = ParamState.Value;
        op.StockSurface.Value = 10;
        op.StockSurface.State = ParamState.Value;
        op.ClearancePlane.Value = 12;
        op.RoughingFinishing.Value = mode;
        op.DepthIncrement.Value = 1;
        doc.ActivePart.Operations.Add(op);
        return doc;
    }

    [Fact]
    public void Turning_gcode_uses_x_and_z_with_diameters()
    {
        var doc = LatheDrawing(RoughingFinishing.Roughing);
        var op = doc.ActivePart.Operations.Single();
        var paths = new Dictionary<MachineOp, Toolpath> { [op] = ToolpathGenerator.Generate(doc, op) };
        Assert.Empty(paths[op].Warnings);
        var post = new PostProcessorDefinition
        {
            ClearancePlaneAxis = "X", LatheXMode = LatheXMode.Diameter,
            Rapid = "{$g0} {$_x} {$_z}", FeedMove = "{$g1} {$_f} {$_x} {$_z}",
            ArcCW = "{$g2} {$_f} {$_x} {$_z} {$i} {$k}", ArcCCW = "{$g3} {$_f} {$_x} {$_z} {$i} {$k}",
        };
        var lines = GCodePoster.Post(doc, paths, post).Text.Split("\r\n");
        Assert.DoesNotContain(lines, l => l.Contains(" Y") || l.StartsWith("Y"));

        double[] Values(char reg) => lines.SelectMany(l => l.Split(' ')).Where(w => w.Length > 1 && w[0] == reg)
            .Select(w => double.Parse(w[1..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var x = Values('X');
        var z = Values('Z');
        // Diameters: clearance 2 x 12, finished small diameter 2 x 6 (radius 5 + nose radius 1).
        Assert.Equal(24, x.Max(), 6);
        Assert.Equal(12, x.Min(), 3);
        Assert.InRange(z.Max(), 0, 2);
        Assert.Equal(-40, z.Min(), 3);
        // Roughing levels every millimetre of radius from 9 down to 6.
        foreach (var level in new[] { 18.0, 16.0, 14.0 }) Assert.Contains(level, x);
    }

    [Fact]
    public void Finishing_makes_only_the_contour_pass()
    {
        var doc = LatheDrawing(RoughingFinishing.Finishing);
        var op = doc.ActivePart.Operations.Single();
        var tp = ToolpathGenerator.Generate(doc, op);
        // Rapids: to clearance/start and the final retract; everything else follows the contour.
        Assert.True(tp.Moves.Count(m => m.Kind == MoveKind.Rapid) <= 3);
        Assert.Contains(tp.Moves, m => m.IsArc);
    }

    [Fact]
    public void Lathe_operations_round_trip_in_the_CamBam_format()
    {
        var file = @"C:\ProgramData\CamBam plus 1.0\samples\lathe-test.cb";
        if (!File.Exists(file)) return;
        var doc = CbReader.Load(file);
        var ops = doc.Parts.SelectMany(p => p.Operations).OfType<LatheOp>().ToList();
        Assert.Equal(2, ops.Count);
        var rough = ops.Single(o => o.Name == "rough");
        Assert.Equal(0.25, rough.DepthIncrement.Value);
        Assert.Equal(0.5, rough.LatheLeadInLength.Value);
        Assert.Equal(WorkPlane.XZ, rough.WorkPlane.Value);

        var xml = CbWriter.ToString(doc);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "<mop xsi:type=\"MOPLathe\">").Count);
        // The finishing operation has no lathe-specific values in the file and must not gain them.
        var finish = xml[xml.LastIndexOf("<mop", StringComparison.Ordinal)..];
        Assert.DoesNotContain("DepthIncrement", finish[..finish.IndexOf("</mop>", StringComparison.Ordinal)]);
        var again = CbReader.Parse(xml).Parts.SelectMany(p => p.Operations).OfType<LatheOp>().Single(o => o.Name == "rough");
        Assert.Equal(0.5, again.LatheLeadOutLength.Value);
    }
}
