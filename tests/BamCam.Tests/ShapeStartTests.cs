using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Issue 27: a start point per shape for plasma cuts, anywhere on the outline.</summary>
public class ShapeStartTests
{
    private static Path2 Square(double size) => Path2.FromPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)], closed: true);

    [Fact]
    public void A_closed_path_starts_in_the_middle_of_a_line_without_changing_its_shape()
    {
        var square = Square(10);

        var started = square.StartAt(new Point2(5, -1));

        Assert.True(started.Vertices[0].XY.AlmostEquals(new Point2(5, 0)), started.Vertices[0].XY.ToString());
        Assert.Equal(5, started.Vertices.Count);
        Assert.Equal(Math.Abs(square.SignedArea()), Math.Abs(started.SignedArea()), 9);
        Assert.Equal(square.Length, started.Length, 9);
    }

    [Fact]
    public void A_closed_path_starts_in_the_middle_of_an_arc_without_changing_its_shape()
    {
        // A circle of radius 5 as two half arcs.
        var circle = new Path2([new Vertex(new Point3(5, 0, 0), 1), new Vertex(new Point3(-5, 0, 0), 1)], true);
        var at = Point2.FromAngle(Math.PI / 4, 5);

        var started = circle.StartAt(at * 1.2);

        Assert.True(started.Vertices[0].XY.AlmostEquals(at, 1e-9), started.Vertices[0].XY.ToString());
        Assert.Equal(Math.PI * 25, Math.Abs(started.SignedArea()), 6);
        Assert.Equal(Math.PI * 10, started.Length, 6);
    }

    /// <summary>A 40 x 40 plate cut with a 2 mm kerf, no lead moves, so that the cut starts at the chosen point.</summary>
    private static (CadDocument Doc, PlasmaOp Op, Entity Plate) Plate()
    {
        var doc = new CadDocument();
        var plate = doc.AddEntity(new PolylineEntity(Square(40)));
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.Add(plate.Id);
        op.KerfWidth.Set(2);
        op.LeadIn.Set(PlasmaLead.None);
        op.Overcut.Set(0);
        op.WithValues();
        doc.ActivePart.Operations.Add(op);
        return (doc, op, plate);
    }

    private static Point3 CutStart(Toolpath tp)
    {
        var on = tp.Moves.FindIndex(m => m is { Kind: MoveKind.Torch, Torch: TorchAction.On });
        return tp.Moves.Skip(on).First(m => m.Kind == MoveKind.Feed).To;
    }

    [Fact]
    public void The_cut_starts_at_the_point_chosen_for_its_shape()
    {
        var (doc, op, plate) = Plate();
        op.WriteShapeStarts([(plate.Id, new Point2(10, 40))]);

        var start = CutStart(ToolpathGenerator.Generate(doc, op));

        // Half the kerf outside the top edge, above the chosen point.
        Assert.True(start.XY.AlmostEquals(new Point2(10, 41), 1e-6), start.ToString());
    }

    [Fact]
    public void A_point_that_is_no_longer_on_the_shape_is_ignored()
    {
        var (doc, op, plate) = Plate();
        op.WriteShapeStarts([(plate.Id, new Point2(10, 45))]);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Warnings);
        Assert.False(CutStart(tp).XY.AlmostEquals(new Point2(10, 41), 1e-3));
    }

    [Fact]
    public void Lead_moves_in_a_hole_are_shortened_to_stay_inside_it()
    {
        // Started close to either end of the slot: the arc lead-in runs back along the edge and, on one of them, would
        // reach past the end of the slot.
        var warnings = new List<string>();
        foreach (var startX in new[] { 10.8, 49.2 }) warnings.AddRange(SlotWarnings(startX));
        Assert.Contains(warnings, w => w.Contains("did not fit into the hole"));
    }

    [Theory]
    [InlineData(PlasmaLead.Arc, 20)]
    [InlineData(PlasmaLead.Line, 20)]
    [InlineData(PlasmaLead.Arc, 8)]
    [InlineData(PlasmaLead.Line, 8)]
    public void Lead_moves_that_fit_into_a_round_hole_are_kept(PlasmaLead lead, double diameter)
    {
        // Holes of diameter 20 and 8, as in examples_to_test/Plasma (bracket).cb: the lead moves are shortened to a
        // quarter of the diameter, and an arc of that radius fits inside.
        var doc = new CadDocument();
        var plate = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(60, 0), new(60, 40), new(0, 40)], closed: true)));
        var hole = doc.AddEntity(new CircleEntity { Center = new Point3(30, 20, 0), Diameter = diameter });
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.AddRange([plate.Id, hole.Id]);
        op.KerfWidth.Set(1.5);
        op.LeadIn.Set(lead);
        op.LeadInLength.Set(4);
        op.LeadOut.Set(lead);
        op.LeadOutLength.Set(2);
        op.WithValues();
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Warnings);
    }

    private static List<string> SlotWarnings(double startX)
    {
        // Issue 28: a slot 40 x 4 in a plate, the cut started near one end. The lead-in arc must not reach out of the slot.
        var doc = new CadDocument();
        var plate = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(60, 0), new(60, 24), new(0, 24)], closed: true)));
        var slot = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(10, 10), new(50, 10), new(50, 14), new(10, 14)], closed: true)));
        var op = new PlasmaOp { Name = "Plasma1" };
        op.PrimitiveIds.AddRange([plate.Id, slot.Id]);
        op.KerfWidth.Set(1.2);
        op.LeadIn.Set(PlasmaLead.Arc);
        op.LeadInLength.Set(5);
        op.LeadOut.Set(PlasmaLead.Arc);
        op.LeadOutLength.Set(5);
        op.Overcut.Set(0);
        op.WriteShapeStarts([(slot.Id, new Point2(startX, 10))]);
        op.WithValues();
        doc.ActivePart.Operations.Add(op);

        var tp = ToolpathGenerator.Generate(doc, op);

        // Every move of the slot's cut (from piercing to switching off) keeps the torch inside the slot's kerf path.
        var inSlot = false;
        Point3 last = default;
        foreach (var m in tp.Moves)
        {
            if (m.IsMotion && !inSlot) last = m.To;
            if (m is { Kind: MoveKind.Torch, Torch: TorchAction.On }) inSlot = last.Y is > 9 and < 15; // pierced in the slot
            else if (m is { Kind: MoveKind.Torch, Torch: TorchAction.Off }) inSlot = false;
            else if (inSlot && m.IsMotion)
            {
                Assert.InRange(m.To.X, 10.6 - 1e-6, 49.4 + 1e-6);
                Assert.InRange(m.To.Y, 10.6 - 1e-6, 13.4 + 1e-6);
            }
        }
        Assert.All(tp.Warnings, w => Assert.Contains($"object {slot.Id}", w));
        return tp.Warnings;
    }

    [Fact]
    public void Start_points_are_saved_with_the_drawing()
    {
        var (doc, op, plate) = Plate();
        op.WriteShapeStarts([(plate.Id, new Point2(10, 40)), (plate.Id, new Point2(0.5, 20.25))]);

        var again = CbReader.Parse(CbWriter.ToString(doc));
        var read = again.ActivePart.Operations.OfType<PlasmaOp>().Single().ReadShapeStarts();

        Assert.Equal([(plate.Id, new Point2(10, 40)), (plate.Id, new Point2(0.5, 20.25))], read);
    }
}
