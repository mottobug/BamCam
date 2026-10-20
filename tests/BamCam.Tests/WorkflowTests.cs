using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using BamCam.IO.Dxf;

namespace BamCam.Tests;

public class WorkflowTests
{
    private static CadDocument PlateWithHoles()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(RectEntity.FromCorners(new(0, 0), new(80, 50), 5));       // 1: outline
        doc.AddEntity(new CircleEntity { Center = new(20, 25, 0), Diameter = 12 }); // 2: pocket
        var pts = new PointListEntity();
        pts.Points.AddRange([new(60, 15, 0), new(60, 35, 0)]);
        doc.AddEntity(pts);                                                         // 3: drill
        return doc;
    }

    [Fact]
    public void Draw_pocket_drill_profile_and_post()
    {
        var doc = PlateWithHoles();
        var part = doc.ActivePart;
        var pocket = new PocketOp { Name = "Pocket1" };
        pocket.PrimitiveIds.Add(2);
        pocket.TargetDepth.Set(-3);
        pocket.DepthIncrement.Set(1.5);
        pocket.ToolDiameter.Set(3);
        var drill = new DrillOp { Name = "Drill1" };
        drill.PrimitiveIds.Add(3);
        drill.TargetDepth.Set(-6);
        drill.ToolNumber.Set(2);
        drill.ToolDiameter.Set(4);
        drill.RetractHeight.Set(1);
        var profile = new ProfileOp { Name = "Profile1" };
        profile.PrimitiveIds.Add(1);
        profile.TargetDepth.Set(-6);
        profile.ToolDiameter.Set(3);
        profile.ToolNumber.Set(1);
        pocket.ToolNumber.Set(1);
        profile.HoldingTabs.Set(new HoldingTabSettings(TabMethod.Automatic, 4, 1.5, 4, 4, 0, 0, false, TabStyle.Square));
        part.Operations.Add(pocket);
        part.Operations.Add(drill);
        part.Operations.Add(profile);

        var paths = part.Operations.ToDictionary(o => o, o => ToolpathGenerator.Generate(doc, o));
        foreach (var tp in paths.Values) Assert.Empty(tp.Warnings);

        // Pocket stays inside the circle (radius 6 minus tool radius 1.5).
        var pb = paths[pocket].Bounds();
        Assert.True(pb.Max.X <= 20 + 4.5 + 1e-3 && pb.Min.X >= 20 - 4.5 - 1e-3);
        Assert.Equal(-3, pb.Min.Z, 6);

        // Outside profile is offset by the tool radius around the 80 x 50 plate.
        var prb = paths[profile].Bounds();
        Assert.Equal(-1.5, prb.Min.X, 2);
        Assert.Equal(81.5, prb.Max.X, 2);
        // Four tabs lift the last levels to -6 + 1.5.
        Assert.Contains(paths[profile].Moves, m => m.Kind == MoveKind.Rapid && Math.Abs(m.To.Z - (-4.5)) < 1e-9);

        var result = GCodePoster.Post(doc, paths, PostProcessorDefinition.BuiltIn().First(p => p.Name == "Mach3"), now: new DateTime(2026, 1, 1));
        Assert.Empty(result.Warnings);
        var lines = result.Text.Split("\r\n");
        Assert.Contains("G91.1", lines);
        Assert.Contains(lines, l => l.StartsWith("G81") && l.Contains("Z-6.0 R1.0 F"));
        Assert.Contains("T2 M6", lines);
        Assert.Contains(lines, l => l.StartsWith("G2 ") || l.StartsWith("G3 "));
        Assert.Equal("M30", lines[^2]);

        // The G-code back-plots to the same extents.
        var back = GCodeParser.Parse(result.Text);
        var bb = back.Bounds();
        Assert.Equal(-1.5, bb.Min.X, 2);
        Assert.Equal(-6, bb.Min.Z, 6);
    }

    [Fact]
    public void Dxf_roundtrip_keeps_geometry()
    {
        var doc = PlateWithHoles();
        doc.AddEntity(new ArcEntity { Center = new(40, 60, 0), Radius = 10, StartAngle = 0, Sweep = 120 });
        var file = Path.Combine(Path.GetTempPath(), $"bamcam-test-{Guid.NewGuid():N}.dxf");
        try
        {
            DxfWriter.Save(doc, file);
            var back = DxfReader.Load(file, out var warnings);
            Assert.Empty(warnings);
            // The point list (2 points) is exported as two DXF POINTs.
            Assert.Equal(doc.AllEntities().Count() + 1, back.AllEntities().Count());
            var a = doc.Bounds();
            var b = back.Bounds();
            Assert.Equal(a.Min.X, b.Min.X, 6);
            Assert.Equal(a.Max.Y, b.Max.Y, 6);
            var arcLen = back.AllEntities().OfType<PolylineEntity>().Max(p => p.LocalPath.Length);
            Assert.True(arcLen > 0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Join_connects_open_segments_into_closed_shape()
    {
        var lines = new[] { (new Point2(0, 0), new Point2(10, 0)), (new Point2(10, 10), new Point2(10, 0)), (new Point2(10, 10), new Point2(0, 0)) }
            .Select(s =>
            {
                var l = new LineEntity();
                l.Points.Add(s.Item1.ToPoint3());
                l.Points.Add(s.Item2.ToPoint3());
                return (Entity)l;
            }).ToList();
        var joined = Core.Cad.CadOps.Join(lines, 0.01);
        var p = Assert.Single(joined);
        Assert.True(p.Closed);
        Assert.Equal(3, p.Points.Count);
        Assert.Equal(50, Math.Abs(p.LocalPath.SignedArea()), 6);
    }

    [Fact]
    public void Style_library_defaults_are_applied()
    {
        var styles = @"C:\ProgramData\CamBam plus 1.0\styles\Standard-mm.xml";
        if (!File.Exists(styles)) return;
        CamLibraries.StyleLibraries.Clear();
        CamLibraries.StyleLibraries.Add(LibraryFiles.LoadStyles(styles));
        try
        {
            var doc = PlateWithHoles();
            var op = new ProfileOp();
            op.CutFeedrate.Value = 1;
            doc.ActivePart.Operations.Add(op);
            LibraryFiles.ApplyStyle(doc, op);
            Assert.Equal(800, op.CutFeedrate.Value);
            Assert.Equal(ParamState.Default, op.CutFeedrate.State);
            Assert.Equal(ParamState.Auto, op.TargetDepth.State);

            op.Style = "cutout+tabs";
            LibraryFiles.ApplyStyle(doc, op);
            Assert.NotEqual(TabMethod.None, op.HoldingTabs.Value.TabMethod);
        }
        finally
        {
            CamLibraries.StyleLibraries.Clear();
        }
    }
}
