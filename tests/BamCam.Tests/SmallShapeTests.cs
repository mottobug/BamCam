using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>
/// Issue 30: traced drawings with many short segments. The overcut of plasma and drag knife is longer than a segment,
/// and holes can be narrower than the kerf.
/// </summary>
public class SmallShapeTests
{
    /// <summary>A circle of radius <paramref name="r"/> as a polygon of 64 straight segments (as traced from a bitmap).</summary>
    private static Path2 Polygon(double cx, double cy, double r) =>
        Path2.FromPoints(Enumerable.Range(0, 64).Select(i => new Point2(cx + r * Math.Cos(i * Math.PI / 32), cy + r * Math.Sin(i * Math.PI / 32))).ToList(), closed: true);

    [Fact]
    public void Plasma_overcut_longer_than_the_segments_is_cut()
    {
        var doc = new CadDocument();
        var shape = doc.AddEntity(new PolylineEntity(Polygon(0, 0, 5)));
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.Add(shape.Id);
        op.KerfWidth.Set(1.2);
        // On the line, so that the short segments stay (an offset would fit them with long arcs).
        op.KerfSide.Set(PlasmaKerfSide.OnLine);
        op.Overcut.Set(3); // each segment is about 0.5 long
        op.WithValues();
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Warnings);
        Assert.Contains(tp.Moves, m => m is { Kind: MoveKind.Torch, Torch: TorchAction.On });
    }

    [Fact]
    public void Drag_knife_overcut_longer_than_the_segments_is_cut()
    {
        var doc = new CadDocument();
        var shape = doc.AddEntity(new PolylineEntity(Polygon(0, 0, 5)));
        var op = new DragKnifeOp { Name = "knife" };
        op.PrimitiveIds.Add(shape.Id);
        op.KnifeOffset.Set(0.3);
        op.Overcut.Set(3);
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Warnings);
        Assert.Contains(tp.Moves, m => m.Kind == MoveKind.Feed);
    }

    [Fact]
    public void A_hole_narrower_than_the_kerf_is_reported_and_the_rest_is_cut()
    {
        var doc = new CadDocument();
        var outline = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true)));
        var tiny = doc.AddEntity(new PolylineEntity(Polygon(20, 20, 0.4))); // 0.8 wide, kerf 1.2
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.AddRange([outline.Id, tiny.Id]);
        op.KerfWidth.Set(1.2);
        op.WithValues();
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        var warning = Assert.Single(tp.Warnings);
        Assert.Contains($"object {tiny.Id}", warning);
        Assert.Contains("too small for the kerf width", warning);
        Assert.Single(tp.Moves, m => m is { Kind: MoveKind.Torch, Torch: TorchAction.On }); // the outline
    }
}
