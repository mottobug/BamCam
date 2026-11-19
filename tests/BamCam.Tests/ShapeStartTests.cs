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
    public void Start_points_are_saved_with_the_drawing()
    {
        var (doc, op, plate) = Plate();
        op.WriteShapeStarts([(plate.Id, new Point2(10, 40)), (plate.Id, new Point2(0.5, 20.25))]);

        var again = CbReader.Parse(CbWriter.ToString(doc));
        var read = again.ActivePart.Operations.OfType<PlasmaOp>().Single().ReadShapeStarts();

        Assert.Equal([(plate.Id, new Point2(10, 40)), (plate.Id, new Point2(0.5, 20.25))], read);
    }
}
