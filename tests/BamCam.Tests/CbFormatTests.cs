using BamCam.Core.Cam;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>
/// Round-trip tests against sample drawings installed with a licensed CamBam (skipped when not present).
/// The samples are read from the local installation and never copied into this repository.
/// </summary>
public class CbFormatTests
{
    private static readonly string SampleDir = @"C:\ProgramData\CamBam plus 1.0";

    /// <summary>The user's examples in the repository, so the theory has data on machines without CamBam (CI).</summary>
    private static readonly string ExampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    public static IEnumerable<object[]> SampleFiles()
    {
        foreach (var dir in new[] { SampleDir, ExampleDir }.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(dir, "*.cb", SearchOption.AllDirectories))
                yield return [f];
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Sample_roundtrips_without_losing_content(string path)
    {
        var doc = CbReader.Load(path);
        var xml = CbWriter.ToString(doc);
        var again = CbReader.Parse(xml);

        Assert.Equal(doc.Layers.Count, again.Layers.Count);
        Assert.Equal(doc.AllEntities().Count(), again.AllEntities().Count());
        Assert.Equal(doc.AllEntities().Select(e => e.Id), again.AllEntities().Select(e => e.Id));
        Assert.Equal(doc.Parts.Count, again.Parts.Count);
        for (var i = 0; i < doc.Parts.Count; i++)
        {
            var a = doc.Parts[i].Operations;
            var b = again.Parts[i].Operations;
            Assert.Equal(a.Count, b.Count);
            for (var k = 0; k < a.Count; k++)
            {
                Assert.Equal(a[k].Name, b[k].Name);
                Assert.Equal(a[k].PrimitiveIds, b[k].PrimitiveIds);
                foreach (var (name, p, _) in a[k].Parameters())
                {
                    var q = b[k].FindParameter(name)!;
                    Assert.Equal(p.State, q.State);
                    Assert.Equal(p.BoxedValue, q.BoxedValue);
                }
            }
        }
        // Writing again must be stable.
        Assert.Equal(xml, CbWriter.ToString(again));
    }

    [Fact]
    public void Snap_box_sample_is_parsed_correctly()
    {
        var path = Path.Combine(SampleDir, "samples", "snap-together-box.cb");
        if (!File.Exists(path)) return;
        var doc = CbReader.Load(path);
        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        Assert.Equal(244, pl.Id);
        Assert.True(pl.Closed);
        Assert.Equal(44, pl.Points.Count);

        var op = Assert.IsType<ProfileOp>(Assert.Single(doc.Parts.Single().Operations));
        Assert.Equal(-3.5, op.TargetDepth.Value);
        Assert.Equal(ParamState.Default, op.TargetDepth.State);
        Assert.Equal(ParamState.Value, op.CornerOvercut.State);
        Assert.True(op.CornerOvercut.Value);
        Assert.Equal(TabMethod.Manual, op.HoldingTabs.Value.TabMethod);
        Assert.Equal(6, op.Tabs.Count);
        Assert.Equal(StepoverFeedrate.PlungeFeedrate, op.StepoverFeedrate.Value);
        Assert.Equal(0.2, doc.Machining.FastPlungeHeight);
        Assert.Equal(6, doc.Parts[0].NestingItems.Count);
    }

    [Fact]
    public void New_document_writes_and_reads_back()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(new CircleEntity { Center = new(10, 20, 0), Diameter = 5 });
        doc.AddEntity(RectEntity.FromCorners(new(0, 0), new(30, 20), 2));
        var op = new ProfileOp { Name = "Profile1" };
        op.PrimitiveIds.Add(1);
        op.TargetDepth.Set(-2);
        doc.ActivePart.Operations.Add(op);

        var again = CbReader.Parse(CbWriter.ToString(doc));
        var c = Assert.IsType<CircleEntity>(again.FindEntity(1));
        Assert.Equal(5, c.Diameter);
        var r = Assert.IsType<RectEntity>(again.FindEntity(2));
        Assert.Equal(2, r.CornerRadius);
        var op2 = Assert.IsType<ProfileOp>(again.Parts[0].Operations[0]);
        Assert.Equal(-2, op2.TargetDepth.Value);
        Assert.Equal(ParamState.Value, op2.TargetDepth.State);
    }

    /// <summary>Values as CamBam saved them in a drawing where each was chosen in its user interface.</summary>
    [Theory]
    [InlineData("ToolProfile", "BallNose")]
    [InlineData("SpindleDirection", "CCW")]
    [InlineData("SpindleDirection", "Off")]
    [InlineData("VelocityMode", "ConstantVelocity")]
    [InlineData("VelocityMode", "ExactStop")]
    [InlineData("WorkPlane", "YZ")]
    [InlineData("OptimisationMode", "Standard")]
    [InlineData("StepoverFeedrate", "Cut Feedrate")]
    [InlineData("TabStyle", "Skip")]
    [InlineData("DrillingMethod", "CustomScript")]
    [InlineData("BoundaryMethod", "BoundingBox")]
    [InlineData("Profile3DMethod", "Vertical")]
    [InlineData("Profile3DMethod", "WaterlineFinish")]
    [InlineData("StartCorner", "BottomRight")]
    [InlineData("StartCorner", "TopLeft")]
    [InlineData("StartCorner", "TopRight")]
    [InlineData("LatheCutDirection", "LeftHand")]
    public void Values_saved_by_cambam_are_written_back_unchanged(string element, string value)
    {
        var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test/Schreibweisen CamBam.cb"));
        if (!File.Exists(file)) return;
        static bool Has(string xml, string element, string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(xml, $@"<{element}(\s[^>]*)?>{System.Text.RegularExpressions.Regex.Escape(value)}</{element}>");
        Assert.True(Has(File.ReadAllText(file), element, value), "not in CamBam's file");
        Assert.True(Has(CbWriter.ToString(CbReader.Load(file)), element, value));
    }

    /// <summary>A drawing with these opened in CamBam without them, while the operations of CamBam itself stayed.</summary>
    [Fact]
    public void Operations_cambam_does_not_know_are_marked()
    {
        MachineOp[] onlyHere = [new PlasmaOp(), new VEngraveOp(), new TrochoidalPocketOp(), new DragKnifeOp(), new LatheFacingOp(), new LatheGrooveOp(), new LatheThreadOp(), new LatheDrillOp()];
        MachineOp[] known = [new ProfileOp(), new PocketOp(), new EngraveOp(), new DrillOp(), new Profile3DOp(), new NcFileOp(), new LatheOp(), new UnsupportedOp("from a plugin")];
        Assert.All(onlyHere, op => Assert.True(op.OnlyInBamCam, op.XmlName));
        Assert.All(known, op => Assert.False(op.OnlyInBamCam, op.XmlName));
    }

    /// <summary>The same for nesting, saved by CamBam; it leaves out Grid Order for Right Up, its default.</summary>
    [Theory]
    [InlineData("NestMethod", "Grid")]
    [InlineData("NestMethod", "IsoGrid")]
    [InlineData("NestMethod", "PointList")]
    [InlineData("GridOrder", "RightDown")]
    [InlineData("GridOrder", "LeftUp")]
    [InlineData("GridOrder", "LeftDown")]
    [InlineData("GridOrder", "UpRight")]
    [InlineData("GridOrder", "UpLeft")]
    [InlineData("GridOrder", "DownRight")]
    [InlineData("GridOrder", "DownLeft")]
    public void Nesting_values_saved_by_cambam_are_written_back_unchanged(string element, string value)
    {
        var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test/Schreibweisen Nesting CamBam.cb"));
        if (!File.Exists(file)) return;
        Assert.Contains($"<{element}>{value}</{element}>", File.ReadAllText(file));
        Assert.Contains($"<{element}>{value}</{element}>", CbWriter.ToString(CbReader.Load(file)));
    }

    [Theory]
    [InlineData("Right Down", NestGridOrder.RightDown)]
    [InlineData("Up Left", NestGridOrder.UpLeft)]
    public void Older_grid_order_spellings_can_still_be_read(string older, NestGridOrder order)
    {
        var doc = CadDocument.CreateNew();
        doc.ActivePart.Nesting.NestMethod = NestMethod.Grid;
        doc.ActivePart.Nesting.GridOrder = order;
        var xml = CbWriter.ToString(doc).Replace($"<GridOrder>{order}<", $"<GridOrder>{older}<");
        Assert.Contains(older, xml);
        Assert.Equal(order, CbReader.Parse(xml).ActivePart.Nesting.GridOrder);
    }

    [Fact]
    public void Older_iso_grid_spelling_can_still_be_read()
    {
        var doc = CadDocument.CreateNew();
        doc.ActivePart.Nesting.NestMethod = NestMethod.IsoGrid;
        var xml = CbWriter.ToString(doc).Replace("<NestMethod>IsoGrid<", "<NestMethod>ISOGrid<");
        Assert.Equal(NestMethod.IsoGrid, CbReader.Parse(xml).ActivePart.Nesting.NestMethod);
    }

    [Theory]
    [InlineData(ArcCenterMode.Absolute, "<ArcCenterMode>Absolute</ArcCenterMode>")]
    [InlineData(ArcCenterMode.IncrementalFromP1, "<ArcCenterMode>IncrementalFromP1</ArcCenterMode>")]
    [InlineData(ArcCenterMode.IncrementalToP1, "<ArcCenterMode>IncrementalToP1</ArcCenterMode>")]
    [InlineData(ArcCenterMode.IncrementalFromP2, "<ArcCenterMode>IncrementalFromP2</ArcCenterMode>")]
    [InlineData(ArcCenterMode.IncrementalToP2, "<ArcCenterMode>IncrementalToP2</ArcCenterMode>")]
    public void Arc_center_modes_are_written_as_cambam_does(ArcCenterMode mode, string expected)
    {
        var doc = CadDocument.CreateNew();
        doc.Machining.ArcCenterMode = mode;
        var xml = CbWriter.ToString(doc);
        Assert.Contains(expected, xml);
        Assert.Equal(mode, CbReader.Parse(xml).Machining.ArcCenterMode);
    }

    [Theory]
    [InlineData("Incremental (C-P1)", ArcCenterMode.IncrementalFromP1)]
    [InlineData("Incremental (P1-C)", ArcCenterMode.IncrementalToP1)]
    [InlineData("Incremental (C-P2)", ArcCenterMode.IncrementalFromP2)]
    [InlineData("Incremental (P2-C)", ArcCenterMode.IncrementalToP2)]
    public void Older_arc_center_spellings_can_still_be_read(string older, ArcCenterMode mode)
    {
        var doc = CadDocument.CreateNew();
        doc.Machining.ArcCenterMode = ArcCenterMode.Absolute;
        var xml = CbWriter.ToString(doc).Replace("<ArcCenterMode>Absolute<", $"<ArcCenterMode>{older}<");
        Assert.Equal(mode, CbReader.Parse(xml).Machining.ArcCenterMode);
    }

    [Fact]
    public void An_older_legacy_optimisation_mode_becomes_standard()
    {
        var doc = CadDocument.CreateNew();
        var shape = doc.AddEntity(RectEntity.FromCorners(new(0, 0), new(40, 30)));
        var op = new ProfileOp { Name = "p" };
        op.PrimitiveIds.Add(shape.Id);
        op.OptimisationMode.Set(OptimisationMode.Standard);
        doc.ActivePart.Operations.Add(op);
        var xml = CbWriter.ToString(doc).Replace(">Standard</OptimisationMode>", ">Legacy</OptimisationMode>");
        Assert.Equal(OptimisationMode.Standard, CbReader.Parse(xml).Parts[0].Operations[0].OptimisationMode.Value);
    }

    /// <summary>CamBam leaves the element out for Always, so a drawing without it means Always, and If Needed must be written.</summary>
    [Fact]
    public void Rebuild_before_post_follows_cambams_default()
    {
        var doc = CadDocument.CreateNew();
        Assert.Equal(RebuildToolpath.IfNeeded, doc.Machining.RebuildToolpathBeforePost);
        var xml = CbWriter.ToString(doc);
        Assert.Contains("<RebuildToolpathBeforePost>IfNeeded</RebuildToolpathBeforePost>", xml);
        Assert.Equal(RebuildToolpath.IfNeeded, CbReader.Parse(xml).Machining.RebuildToolpathBeforePost);

        var withoutElement = xml.Replace("<RebuildToolpathBeforePost>IfNeeded</RebuildToolpathBeforePost>", "");
        var read = CbReader.Parse(withoutElement);
        Assert.Equal(RebuildToolpath.Always, read.Machining.RebuildToolpathBeforePost);
        Assert.DoesNotContain("RebuildToolpathBeforePost", CbWriter.ToString(read));
    }

    /// <summary>
    /// The spelling CamBam saves. BamCam used to write "Automatic (Outer)", and CamBam refused such a whole file with an
    /// XML error; these older files can still be read.
    /// </summary>
    [Theory]
    [InlineData(TabMethod.AutomaticOuter, "AutomaticOuter", "Automatic (Outer)")]
    [InlineData(TabMethod.AutomaticInner, "AutomaticInner", "Automatic (Inner)")]
    public void Holding_tab_methods_are_written_as_cambam_does(TabMethod method, string spelling, string older)
    {
        var doc = CadDocument.CreateNew();
        var shape = doc.AddEntity(RectEntity.FromCorners(new(0, 0), new(40, 30)));
        var op = new ProfileOp { Name = "p" };
        op.PrimitiveIds.Add(shape.Id);
        op.HoldingTabs.Set(new HoldingTabSettings(method));
        doc.ActivePart.Operations.Add(op);

        var xml = CbWriter.ToString(doc);
        Assert.Contains($"<TabMethod>{spelling}</TabMethod>", xml);
        Assert.Equal(method, Assert.IsType<ProfileOp>(CbReader.Parse(xml).Parts[0].Operations[0]).HoldingTabs.Value.TabMethod);

        var oldFile = CbReader.Parse(xml.Replace(spelling, older));
        Assert.Equal(method, Assert.IsType<ProfileOp>(oldFile.Parts[0].Operations[0]).HoldingTabs.Value.TabMethod);
    }
}
