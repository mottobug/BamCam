using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Issue 19: every entry of the starting cut data tables gives a toolpath, in millimetres and in inches.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public class PlasmaCutDataTests
{
    public static IEnumerable<object[]> Entries()
    {
        foreach (var inchesTable in new[] { false, true })
        foreach (var entry in PlasmaCutTable.Template(inchesTable).Entries)
        foreach (var inchesDrawing in new[] { false, true })
            yield return [inchesTable, entry.Name, inchesDrawing];
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_starting_entry_gives_a_toolpath(bool inchesTable, string entry, bool inchesDrawing)
    {
        var saved = CamLibraries.PlasmaTables.ToList();
        try
        {
            CamLibraries.PlasmaTables.Clear();
            CamLibraries.PlasmaTables.Add(PlasmaCutTable.Template(false));
            CamLibraries.PlasmaTables.Add(PlasmaCutTable.Template(true));
            var doc = new CadDocument { Units = inchesDrawing ? DrawingUnits.Inches : DrawingUnits.Millimeters };
            var size = inchesDrawing ? 2.0 : 50.0;
            var rect = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)], closed: true)));
            var op = new PlasmaOp { Name = "Plasma1" };
            op.PrimitiveIds.Add(rect.Id);
            op.CutData.Set(entry);
            doc.ActivePart.Operations.Add(op);
            LibraryFiles.ApplyStyle(doc, op);

            // Issue 20: the values arrive in the units of the drawing, also from a table in the other units.
            var data = PlasmaCutTable.Template(inchesTable).Find(entry)!;
            var scale = inchesTable == inchesDrawing ? 1 : inchesTable ? 25.4 : 1 / 25.4;
            Assert.Equal(data.KerfWidth * scale, op.KerfWidth.Value, 9);
            Assert.Equal(data.CutFeedrate * scale, op.CutFeedrate.Value, 9);
            Assert.Equal(data.PierceHeight * scale, op.PierceHeight.Value, 9);
            Assert.Equal(data.CutHeight * scale, op.CutHeight.Value, 9);
            Assert.Equal(data.PierceDelay, op.PierceDelay.Value, 9);

            var tp = ToolpathGenerator.Generate(doc, op);

            Assert.True(tp.Moves.Count > 0, $"{entry} ({(inchesTable ? "in" : "mm")} table, {(inchesDrawing ? "in" : "mm")} drawing): {string.Join(" | ", tp.Warnings)}");
            // The outline is cut outside, half a kerf from the part: no cut reaches into it.
            var half = op.KerfWidth.Value / 2;
            Assert.True(half > 0);
            Assert.DoesNotContain(tp.Moves, m => m.Kind is MoveKind.Feed or MoveKind.ArcCW or MoveKind.ArcCCW
                && m.To.X > half * 0.99 && m.To.X < size - half * 0.99 && m.To.Y > half * 0.99 && m.To.Y < size - half * 0.99);
        }
        finally
        {
            CamLibraries.PlasmaTables.Clear();
            CamLibraries.PlasmaTables.AddRange(saved);
        }
    }
}
