using BamCam.Core.Cam;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using BamCam.IO.Dxf;
using Xunit.Abstractions;

namespace BamCam.Tests;

/// <summary>
/// Drawings with the G-code CamBam produced for them (examples_to_test). Compares our output line by line
/// from the first operation on. Skipped when the folder is absent.
/// </summary>
public class UserExampleTests(ITestOutputHelper output)
{
    private static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    public static IEnumerable<object[]> Examples()
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var cb in Directory.GetFiles(Dir, "*.cb"))
        {
            var nc = Directory.GetFiles(Dir, Path.GetFileNameWithoutExtension(cb) + ".*.nc").FirstOrDefault();
            if (nc != null) yield return [Path.GetFileName(cb), Path.GetFileName(nc)];
        }
    }

    private (int Mismatches, string[] Ours, string[] Theirs) Compare(string cbName, string ncName)
    {
        var doc = CbReader.Load(Path.Combine(Dir, cbName));
        var paths = new Dictionary<MachineOp, Toolpath>();
        foreach (var op in doc.Parts.SelectMany(p => p.Operations))
        {
            var tp = ToolpathGenerator.Generate(doc, op);
            Assert.True(tp.Warnings.Count == 0, string.Join("; ", tp.Warnings));
            paths[op] = tp;
        }
        var result = GCodePoster.Post(doc, paths, new PostProcessorDefinition());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bamcam-" + Path.GetFileNameWithoutExtension(cbName) + ".nc"), result.Text);

        // Operation comment: "( Name )" but not the tool table ("( T0 : 2.0 )") or the file title with date.
        static bool IsOp(string l) => l.StartsWith("( ") && !l.Contains("Made using") && !l.Contains("Post processor:") &&
                                      !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\( T\d+ :");
        var theirs = File.ReadAllLines(Path.Combine(Dir, ncName)).SkipWhile(l => !IsOp(l) || System.Text.RegularExpressions.Regex.IsMatch(l, @"\d+:\d+:\d+")).ToArray();
        var ours = result.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        ours = ours.SkipWhile(l => l != theirs.FirstOrDefault()).ToArray();
        var mismatches = 0;
        for (var i = 0; i < Math.Max(ours.Length, theirs.Length); i++)
        {
            var a = i < ours.Length ? ours[i] : "<eof>";
            var b = i < theirs.Length ? theirs[i] : "<eof>";
            if (a == b) continue;
            if (mismatches++ < 25) output.WriteLine($"{i}: CamBam '{b}'  BamCam '{a}'");
        }
        output.WriteLine($"{cbName}: CamBam {theirs.Length} lines, BamCam {ours.Length} lines, {mismatches} differences");
        return (mismatches, ours, theirs);
    }

    /// <summary>Line-by-line identical where CamBam's segmentation can be reproduced (lines, arcs, circles).</summary>
    [Fact]
    public void Akkubruecke_gcode_is_identical_to_cambam()
    {
        if (!File.Exists(Path.Combine(Dir, "Akkubrücke 2.cb"))) return;
        var (mismatches, _, _) = Compare("Akkubrücke 2.cb", "Akkubrücke 2.Teil1.nc");
        Assert.Equal(0, mismatches);
    }

    /// <summary>
    /// Geometric comparison for every example: back-plot both G-code files and require the same plunge points
    /// (order of features) and a toolpath that never deviates from CamBam's by more than 0.01 mm.
    /// (Spline-derived outlines are segmented differently: CamBam inserts micro corner arcs, BamCam merges them.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Examples))]
    public void Gcode_follows_the_same_path_as_cambam(string cb, string nc)
    {
        var (_, ours, theirs) = Compare(cb, nc);
        var a = GCodeParser.Parse(string.Join("\n", ours));
        var b = GCodeParser.Parse(string.Join("\n", theirs));

        static List<Core.Geometry.Point3> Plunges(Toolpath tp) => tp.Moves
            .Select((m, i) => (m, i))
            .Where(x => x.m.Kind == MoveKind.Feed && x.i > 0 && tp.Moves[x.i - 1].To.XY.AlmostEquals(x.m.To.XY, 1e-6) && x.m.To.Z < tp.Moves[x.i - 1].To.Z)
            .Select(x => x.m.To).ToList();
        var pa = Plunges(a);
        var pb = Plunges(b);
        Assert.Equal(pb.Count, pa.Count);
        for (var i = 0; i < pa.Count; i++)
            Assert.True(Core.Geometry.Point3.Distance(pa[i], pb[i]) < 0.05, $"plunge {i}: CamBam {pb[i]} BamCam {pa[i]}");

        var dev = Math.Max(MaxDeviation(a, b), MaxDeviation(b, a));
        output.WriteLine($"{cb}: max deviation between toolpaths {dev:0.#####} mm");
        Assert.True(dev < 0.01, $"max deviation {dev} mm");
    }

    /// <summary>Largest distance from any point of <paramref name="a"/>'s cutting moves to <paramref name="b"/>'s cutting moves (same Z).</summary>
    private static double MaxDeviation(Toolpath a, Toolpath b)
    {
        var segsB = b.DisplaySegments(0.001).Where(s => s.Kind != MoveKind.Rapid).ToList();
        double worst = 0;
        foreach (var (from, to, kind) in a.DisplaySegments(0.001))
        {
            if (kind == MoveKind.Rapid || Math.Abs(from.Z - to.Z) > 1e-9) continue;
            foreach (var t in new[] { 0.0, 0.5, 1.0 })
            {
                var p = Core.Geometry.Point3.Zero + (from + (to - from) * t);
                var best = double.PositiveInfinity;
                foreach (var (f2, t2, _) in segsB)
                {
                    if (Math.Abs(f2.Z - p.Z) > 1e-6 || Math.Abs(t2.Z - p.Z) > 1e-6) continue;
                    best = Math.Min(best, Core.Geometry.Path2.DistanceToSegment(p.XY, f2.XY, t2.XY));
                }
                if (!double.IsPositiveInfinity(best)) worst = Math.Max(worst, best);
            }
        }
        return worst;
    }

    [Fact]
    public void Arcs_are_read_with_centre_radius_start_and_sweep()
    {
        var path = Path.Combine(Dir, "Akkubrücke 2.cb");
        if (!File.Exists(path)) return;
        var doc = CbReader.Load(path);
        var arc = Assert.IsType<Core.Model.ArcEntity>(doc.FindEntity(5));
        Assert.Equal(4, arc.Center.X, 6);
        Assert.Equal(59, arc.Center.Y, 6);
        Assert.Equal(3, arc.Radius, 6);
        Assert.Equal(390, arc.StartAngle);
        Assert.Equal(300, arc.Sweep);
        // Written back in the same form.
        var xml = CbWriter.ToString(doc);
        Assert.Contains("<arc id=\"5\" p=\"4.00000000000001,59,0\" r=\"3.000000000000007\" s=\"390\" w=\"300\">", xml);
        // Localised value is kept verbatim.
        Assert.Contains("<StepoverFeedrate state=\"Default\">Eintauch-Vorschub</StepoverFeedrate>", xml);
        var pocket = doc.Parts[0].Operations.OfType<PocketOp>().First();
        Assert.Equal(StepoverFeedrate.PlungeFeedrate, pocket.StepoverFeedrate.Value);
    }

    [Fact]
    public void Dxf_import_has_the_size_of_the_cambam_drawing()
    {
        // The .cb was rotated by 90° in CamBam after importing the DXF: compare the sorted extents.
        var dxf = Path.Combine(Dir, "Akkubrücke 2.DXF");
        var cb = Path.ChangeExtension(dxf, ".cb");
        if (!File.Exists(dxf) || !File.Exists(cb)) return;
        var fromDxf = DxfReader.Load(dxf, out var warnings);
        Assert.Empty(warnings);
        var a = fromDxf.Bounds();
        var b = CbReader.Load(cb).Bounds();
        var sa = new[] { a.Width, a.Height }.Order().ToArray();
        var sb = new[] { b.Width, b.Height }.Order().ToArray();
        Assert.Equal(sb[0], sa[0], 3);
        Assert.Equal(sb[1], sa[1], 3);
    }

    [Fact]
    public void Dxf_splines_import_without_warnings()
    {
        var dxf = Path.Combine(Dir, "T-Bar F102.DXF");
        if (!File.Exists(dxf)) return;
        var doc = DxfReader.Load(dxf, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(6, doc.AllEntities().OfType<Core.Model.CircleEntity>().Count());
        Assert.Equal(26, doc.AllEntities().Count(e => e is Core.Model.PolylineEntity or Core.Model.LineEntity));
    }
}
