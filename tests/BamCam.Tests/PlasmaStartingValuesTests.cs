using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Issue 33: a plasma operation without cut data or entered values makes no toolpath and says why.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public class PlasmaStartingValuesTests
{
    private static (CadDocument Doc, PlasmaOp Op) Square()
    {
        var doc = new CadDocument();
        var square = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true)));
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.Add(square.Id);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void Without_cut_data_no_toolpath_is_made_and_the_missing_values_are_named()
    {
        var (doc, op) = Square();
        op.KerfWidth.Set(1.2); // typed: no longer missing

        var tp = ToolpathGenerator.Generate(doc, op);

        var warning = Assert.Single(tp.Warnings);
        Assert.Contains("no Cut Data chosen", warning);
        Assert.Contains("no toolpath is generated", warning);
        Assert.Contains("Cut Feedrate", warning);
        Assert.DoesNotContain("Kerf Width", warning);
        Assert.Empty(tp.Moves);
    }

    [Fact]
    public void Toolpath_without_warning_when_all_values_are_typed_in()
    {
        var (doc, op) = Square();
        op.KerfWidth.Set(1.2);
        op.CutFeedrate.Set(2500);
        op.PierceHeight.Set(3.8);
        op.PierceDelay.Set(0.4);
        op.CutHeight.Set(1.5);

        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        Assert.Contains(tp.Moves, m => m is { Kind: MoveKind.Torch, Torch: TorchAction.On });
    }

    [Fact]
    public void Toolpath_with_cut_data_and_none_when_it_is_in_no_table()
    {
        var saved = CamLibraries.PlasmaTables.ToList();
        try
        {
            CamLibraries.PlasmaTables.Clear();
            CamLibraries.PlasmaTables.Add(PlasmaCutTable.Template(false));
            var (doc, op) = Square();
            op.CutData.Set("Mild steel 3 mm 45 A");
            LibraryFiles.ApplyStyle(doc, op);
            var tp = ToolpathGenerator.Generate(doc, op);
            Assert.Empty(tp.Warnings);
            Assert.NotEmpty(tp.Moves);

            // A file from another computer: the values from the table stay Default, so nothing is cut.
            var (doc2, op2) = Square();
            op2.CutData.Set("Steel from another computer");
            tp = ToolpathGenerator.Generate(doc2, op2);
            Assert.Contains("is in no cut data table", Assert.Single(tp.Warnings));
            Assert.Empty(tp.Moves);
        }
        finally
        {
            CamLibraries.PlasmaTables.Clear();
            CamLibraries.PlasmaTables.AddRange(saved);
        }
    }
}
