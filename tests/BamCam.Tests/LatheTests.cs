using System.Globalization;
using System.Text.RegularExpressions;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using Xunit.Abstractions;

namespace BamCam.Tests;

public partial class LatheTests(ITestOutputHelper output)
{
    private static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));
    private const string CamBamData = @"C:\ProgramData\CamBam plus 1.0";

    // Step profile in lathe coordinates (Z, radius), drawn from the chuck side: radius 8 from Z -40 to -20,
    // shoulder, radius 5 to the part end at Z 0.
    private static readonly Path2 Step = Path2.FromPoints([new(-40, 8), new(-20, 8), new(-20, 5), new(0, 5)], false);

    [Fact]
    public void Contour_is_the_exact_offset_with_round_outer_corners()
    {
        var contour = LatheGenerator.AnalyticOffset(Step, 1)!;
        Assert.NotNull(contour);
        // From the chuck side: along radius 9, around the shoulder corner, down the face, along radius 6.
        var v = contour.Vertices;
        Assert.Equal(new Point2(-40, 9), v[0].XY);
        Assert.Equal(new Point2(0, 6), v[^1].XY);
        var arc = Assert.Single(contour.Segments(), s => s.IsArc).ToArc();
        Assert.Equal(new Point2(-20, 8), arc.Center);
        Assert.Equal(1, arc.Radius, 9);
        foreach (var p in contour.Flatten(0.001)) Assert.True(Step.DistanceTo(p) >= 1 - 1e-9);
    }

    [Fact]
    public void Roughing_stretches_stop_where_the_nose_meets_the_material()
    {
        var outline = LatheGenerator.ExactOutline(Step, LatheGenerator.Outline(Step, 1)!, 1);
        // Nose centre at radius 7.5: free from the stock end down to one radius in front of the shoulder face.
        var right = LatheGenerator.FreeIntervals(outline, 7.5, -40, 6, -1);
        var single = Assert.Single(right);
        Assert.Equal(6, single.From, 9);
        Assert.Equal(-19, single.To, 6);
        // Left hand cuts the same stretch in the other direction.
        var left = Assert.Single(LatheGenerator.FreeIntervals(outline, 7.5, -40, 6, 1));
        Assert.Equal(-19, left.From, 6);
        Assert.Equal(6, left.To, 9);
        // Inside the material nothing is cut; beyond the part end (Z > 1) the stock is turned down to the axis.
        var low = Assert.Single(LatheGenerator.FreeIntervals(outline, 0.5, -40, 6, -1));
        Assert.Equal(1, low.To, 6);
    }

    private static CadDocument LatheDrawing(RoughingFinishing mode)
    {
        var doc = new CadDocument();
        // Profile below the spindle axis (drawing Y = -radius), part end at drawing X 40 (lathe Z 0).
        var profile = new PolylineEntity(Path2.FromPoints(Step.Vertices.Select(v => new Point2(v.Point.X + 40, -v.Point.Y)), false));
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
            .Select(w => double.Parse(w[1..], CultureInfo.InvariantCulture)).ToArray();
        var x = Values('X');
        // Diameters of the tool tip: clearance 2 x 12, roughing levels every 2 mm of diameter below 2 x 10,
        // finished small diameter 2 x 5 (the tip touches the profile).
        Assert.Equal(24, x.Max(), 6);
        foreach (var level in new[] { 18.0, 16.0, 14.0, 12.0, 10.0 }) Assert.Contains(level, x);
        Assert.Contains(lines, l => l.StartsWith("G1") && l.Contains("X10.0"));
        Assert.Contains(lines, l => l.StartsWith("G0") && l.Contains("Z-1.0"));
    }

    [Fact]
    public void Finishing_makes_only_the_contour_pass()
    {
        var doc = LatheDrawing(RoughingFinishing.Finishing);
        var op = doc.ActivePart.Operations.Single();
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.True(tp.Moves.Count(m => m.Kind == MoveKind.Rapid) <= 3);
        Assert.Contains(tp.Moves, m => m.IsArc);
    }

    [Fact]
    public void Lathe_operations_round_trip_in_the_CamBam_format()
    {
        var file = Path.Combine(Dir, "lathe-test.cb");
        if (!File.Exists(file)) return;
        var doc = CbReader.Load(file);
        var ops = doc.Parts.SelectMany(p => p.Operations).OfType<LatheOp>().ToList();
        Assert.Equal(2, ops.Count);
        var rough = ops.Single(o => o.Name == "rough");
        Assert.Equal(0.25, rough.DepthIncrement.Value);
        Assert.Equal(0.5, rough.LatheLeadInLength.Value);
        Assert.Equal(WorkPlane.XZ, rough.WorkPlane.Value);

        var xml = CbWriter.ToString(doc);
        Assert.Equal(2, Regex.Matches(xml, "<mop xsi:type=\"MOPLathe\">").Count);
        // The finishing operation has no lathe-specific values in the file and must not gain them.
        var finish = xml[xml.LastIndexOf("<mop", StringComparison.Ordinal)..];
        Assert.DoesNotContain("DepthIncrement", finish[..finish.IndexOf("</mop>", StringComparison.Ordinal)]);
        var again = CbReader.Parse(xml).Parts.SelectMany(p => p.Operations).OfType<LatheOp>().Single(o => o.Name == "rough");
        Assert.Equal(0.5, again.LatheLeadOutLength.Value);

        var leftFile = Path.Combine(Dir, "lathe-test-left.cb");
        if (!File.Exists(leftFile)) return;
        Assert.All(CbReader.Load(leftFile).Parts.SelectMany(p => p.Operations).OfType<LatheOp>(),
            o => Assert.Equal(LatheCutDirection.LeftHand, o.LatheCutDirection.Value));
    }

    [GeneratedRegex(@"([A-Z])(-?\d+(?:\.\d+)?)")]
    private static partial Regex Word();

    /// <summary>
    /// G-code of the CamBam sample lathe-test (right and left hand) with CamBam's turning post: identical up to
    /// 0.0002 in coordinates. Needs the CamBam data folder (post processor, styles, tools) and local copies of the
    /// sample and CamBam's output in examples_to_test (not in the repository); skipped without them.
    /// </summary>
    [Theory]
    [InlineData("lathe-test")]
    [InlineData("lathe-test-left")]
    public void Turning_gcode_matches_CamBam(string name)
    {
        var postFile = Path.Combine(CamBamData, "post", "Mach3-Turn-CutViewer.cbpp");
        var cb = Path.Combine(Dir, name + ".cb");
        if (!File.Exists(postFile) || !File.Exists(cb)) return;
        CamLibraries.StyleLibraries.Clear();
        CamLibraries.ToolLibraries.Clear();
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(CamBamData, "styles"), "*.xml")) CamLibraries.StyleLibraries.Add(LibraryFiles.LoadStyles(f));
            foreach (var f in Directory.GetFiles(Path.Combine(CamBamData, "tools"), "*.xml")) CamLibraries.ToolLibraries.Add(LibraryFiles.LoadTools(f));
            var doc = CbReader.Load(cb);
            var paths = new Dictionary<MachineOp, Toolpath>();
            foreach (var op in doc.Parts.SelectMany(p => p.Operations))
            {
                LibraryFiles.ApplyStyle(doc, op);
                paths[op] = ToolpathGenerator.Generate(doc, op);
            }
            var ours = GCodePoster.Post(doc, paths, CbppFile.Load(postFile)).Text.Split("\r\n").Where(l => l.Length > 0).ToArray();
            var theirs = File.ReadAllLines(Path.Combine(Dir, name + ".Part1.nc")).Where(l => l.Length > 0).ToArray();
            Assert.Equal(theirs.Length, ours.Length);
            var mismatches = 0;
            for (var i = 0; i < ours.Length; i++)
            {
                if (theirs[i].StartsWith("( lathe-test") || theirs[i].Contains("Post processor")) continue;
                if (SameLine(ours[i], theirs[i])) continue;
                mismatches++;
                output.WriteLine($"{i + 1}: ours   {ours[i]}\n{i + 1}: CamBam {theirs[i]}");
            }
            Assert.Equal(0, mismatches);
        }
        finally
        {
            CamLibraries.StyleLibraries.Clear();
            CamLibraries.ToolLibraries.Clear();
        }
    }

    private static bool SameLine(string a, string b)
    {
        if (a == b) return true;
        var wa = Word().Matches(a).Select(m => (m.Groups[1].Value, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        var wb = Word().Matches(b).Select(m => (m.Groups[1].Value, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        return wa.Count == wb.Count && wa.Zip(wb).All(p => p.First.Item1 == p.Second.Item1 && Math.Abs(p.First.Item2 - p.Second.Item2) <= 2e-4);
    }
}
