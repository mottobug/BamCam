using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Holding tab editing, toolpath path splitting (view filter) and grid nesting.</summary>
public class CamDisplayTests
{
    private const string SamplesDir = @"C:\ProgramData\CamBam plus 1.0\samples";

    private static (CadDocument Doc, ProfileOp Op) SquareProfile(TabMethod method)
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(RectEntity.FromCorners(new(0, 0), new(60, 60)));
        var op = new ProfileOp { Name = "Profile1" };
        op.PrimitiveIds.Add(1);
        op.TargetDepth.Set(-6);
        op.DepthIncrement.Set(2);
        op.ToolDiameter.Set(2);
        op.HoldingTabs.Set(new HoldingTabSettings(method, 4, 1.5, 4, 4, 0, 0, false, TabStyle.Square));
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void Tab_fraction_and_normal_on_square()
    {
        var path = Path2.FromPoints([new(0, 0), new(60, 0), new(60, 60), new(0, 60)], true);
        var tab = HoldingTabTools.CreateTab(1, path, new Point2(30, -5));
        Assert.Equal(30.0 / 240, tab.ParametricPoint, 9);
        Assert.Equal(0, tab.Normal.X, 9);
        Assert.Equal(-1, tab.Normal.Y, 9);

        HoldingTabTools.MoveTab(tab, path, new Point2(70, 20));
        Assert.Equal(80.0 / 240, tab.ParametricPoint, 9);
        Assert.Equal(1, tab.Normal.X, 9);

        // Clockwise outline: the outward normal still points out.
        var cw = path.Reversed();
        Assert.Equal(-1, HoldingTabTools.CreateTab(1, cw, new Point2(-3, 30)).Normal.X, 9);
    }

    [Fact]
    public void Automatic_tabs_are_recorded_and_become_manual_when_edited()
    {
        var (doc, op) = SquareProfile(TabMethod.Automatic);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Equal(4, tp.TabPlacements.Count);
        var markers = HoldingTabTools.Markers(doc, op, tp);
        Assert.Equal(4, markers.Count);
        // Markers sit on the toolpath (1 mm outside the shape) and are Width + tool long.
        foreach (var m in markers)
        {
            var d = Math.Min(Math.Min(Math.Abs(m.Center.X + 1), Math.Abs(m.Center.X - 61)),
                Math.Min(Math.Abs(m.Center.Y + 1), Math.Abs(m.Center.Y - 61)));
            Assert.True(d < 1e-6, $"marker at {m.Center}");
            Assert.Equal(6, m.Length, 9);
            Assert.False(m.Pending);
        }

        var before = ToolpathGenerator.Generate(doc, op).Moves;
        HoldingTabTools.MakeManual(op, tp);
        Assert.Equal(TabMethod.Manual, op.HoldingTabs.Value.TabMethod);
        Assert.Equal(4, op.Tabs.Count);
        // Converting to manual keeps the tabs where they were.
        var manual = ToolpathGenerator.Generate(doc, op);
        Assert.Equal(before.Count, manual.Moves.Count);
        for (var i = 0; i < before.Count; i++) Assert.True(Point3.Distance(before[i].To, manual.Moves[i].To) < 1e-3);

        // Moving a tab marks it pending until the toolpath is regenerated.
        var shape = HoldingTabTools.SourceShapes(doc, op).First().Path;
        HoldingTabTools.MoveTab(op.Tabs[0], shape, new Point2(-10, 10));
        Assert.Single(HoldingTabTools.Markers(doc, op, manual), m => m.Pending);
        var regenerated = ToolpathGenerator.Generate(doc, op);
        Assert.DoesNotContain(HoldingTabTools.Markers(doc, op, regenerated), m => m.Pending);
    }

    [Fact]
    public void Toolpath_paths_split_by_shape_and_level()
    {
        var (doc, op) = SquareProfile(TabMethod.None);
        var tp = ToolpathGenerator.Generate(doc, op);
        var paths = tp.Paths();
        // Three depth levels of one shape (-2, -4, -6).
        Assert.Equal(3, paths.Count);
        Assert.Equal([-2.0, -4.0, -6.0], paths.Select(p => Math.Round(p.Z, 6)).ToArray());
        Assert.Equal(tp.DisplaySegments().Count(), paths.Sum(p => p.Segments.Count));

        // Hops over holding tabs stay within their path.
        var (tabDoc, tabOp) = SquareProfile(TabMethod.Automatic);
        var withTabs = ToolpathGenerator.Generate(tabDoc, tabOp);
        Assert.Contains(withTabs.Moves, m => m.Kind == MoveKind.Rapid && Math.Abs(m.To.Z - (-4.5)) < 1e-9);
        Assert.Equal(3, withTabs.Paths().Count);
    }

    [Fact]
    public void Grid_nesting_serpentine_order()
    {
        var n = new NestingSettings { NestMethod = NestMethod.Grid, Rows = 2, Columns = 3, Spacing = 8 };
        var box = BoundingBox.FromCorners(new(-1, -1), new(61, 61));
        var items = NestingLayout.Grid(n, box).Select(i => i.Location).ToArray();
        Assert.Equal([new(0, 0), new(70, 0), new(140, 0), new(140, 70), new(70, 70), new(0, 70)], items);

        n.GridAlternate = false;
        n.GridOrder = NestGridOrder.UpRight;
        items = NestingLayout.Grid(n, box).Select(i => i.Location).ToArray();
        Assert.Equal([new(0, 0), new(0, 70), new(70, 0), new(70, 70), new(140, 0), new(140, 70)], items);

        n.NestMethod = NestMethod.ISOGrid;
        n.GridOrder = NestGridOrder.RightUp;
        items = NestingLayout.Grid(n, box).Select(i => i.Location).ToArray();
        Assert.Equal(new Point2(35, 70), items[3]);
    }

    [Fact]
    public void Grid_nesting_reproduces_snap_box_copies()
    {
        var cb = Path.Combine(SamplesDir, "snap-together-box.cb");
        if (!File.Exists(cb)) return;
        var doc = CbReader.Load(cb);
        var part = doc.Parts[0];
        var expected = part.NestingItems.Select(i => i.Location).ToArray();
        var toolpaths = part.Operations.ToDictionary(o => o, o => ToolpathGenerator.Generate(doc, o));
        Assert.False(NestingLayout.Update(doc, part, toolpaths));
        Assert.Equal(expected, part.NestingItems.Select(i => i.Location).ToArray());
    }
}
