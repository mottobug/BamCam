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

    public static IEnumerable<object[]> SampleFiles()
    {
        if (!Directory.Exists(SampleDir)) yield break;
        foreach (var f in Directory.GetFiles(SampleDir, "*.cb", SearchOption.AllDirectories))
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
}
