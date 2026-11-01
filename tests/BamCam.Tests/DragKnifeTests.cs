using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class DragKnifeTests
{
    private static Path2 Square() => Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], closed: true);

    [Fact]
    public void Lines_are_moved_forward_and_corners_get_swivel_arcs()
    {
        // Open L: right 10, then up 10; blade offset 0.5.
        var shape = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10)], closed: false);
        var axis = DragKnifeGenerator.AxisPath(shape, 0.5, Angles.ToRadians(10), 0);
        var segments = axis.Segments().ToList();
        Assert.Equal(3, segments.Count);
        // First cut: the axis runs 0.5 ahead of the blade.
        Assert.True(segments[0].Start.XY.AlmostEquals(new(0.5, 0)));
        Assert.True(segments[0].End.XY.AlmostEquals(new(10.5, 0)));
        // Swivel around the corner point (10, 0) with the offset as radius, a quarter turn to the left.
        var swivel = segments[1].ToArc();
        Assert.True(swivel.Center.AlmostEquals(new(10, 0)), swivel.Center.ToString());
        Assert.Equal(0.5, swivel.Radius, 9);
        Assert.Equal(Math.PI / 2, swivel.Sweep, 9);
        Assert.True(segments[2].End.XY.AlmostEquals(new(10, 10.5)));
    }

    [Fact]
    public void Small_direction_changes_are_cut_straight_on()
    {
        // 5° bend below the swivel angle of 10°.
        var bend = Point2.FromAngle(Angles.ToRadians(5), 10);
        var shape = Path2.FromPoints([new(0, 0), new(10, 0), new Point2(10, 0) + bend], closed: false);
        var axis = DragKnifeGenerator.AxisPath(shape, 0.5, Angles.ToRadians(10), 0);
        Assert.DoesNotContain(axis.Segments(), s => s.IsArc);
    }

    [Fact]
    public void Arcs_become_arcs_through_the_axis_positions()
    {
        // Half circle radius 5 around the origin, counter-clockwise from (5, 0) to (-5, 0).
        var shape = new Path2([new Vertex(new Point3(5, 0, 0), 1), new Vertex(new Point3(-5, 0, 0))], false);
        var axis = DragKnifeGenerator.AxisPath(shape, 0.5, Angles.ToRadians(10), 0);
        var arc = axis.Segments().Single().ToArc();
        Assert.True(arc.Center.AlmostEquals(new(0, 0)), arc.Center.ToString());
        Assert.Equal(Math.Sqrt(25 + 0.25), arc.Radius, 9);
        Assert.Equal(Math.PI, arc.Sweep, 9);
    }

    [Fact]
    public void Closed_shapes_start_with_a_swivel_and_end_with_an_overcut()
    {
        var axis = DragKnifeGenerator.AxisPath(Square(), 0.5, Angles.ToRadians(10), 1);
        var segments = axis.Segments().ToList();
        // Plunge with the blade pointing down the last edge (0,10 → 0,0), swivel into the first edge.
        Assert.True(axis.StartPoint.XY.AlmostEquals(new(0, -0.5)), axis.StartPoint.ToString());
        Assert.True(segments[0].IsArc);
        Assert.Equal(5, segments.Count(s => s.IsArc)); // start + 4 corners (the last one before the overcut)
        // Overcut: 1 beyond the start along the first edge, axis 0.5 ahead.
        Assert.True(axis.EndPoint.XY.AlmostEquals(new(1.5, 0)), axis.EndPoint.ToString());
    }

    [Fact]
    public void Operation_generates_at_cut_depth_and_round_trips_through_cb_files()
    {
        var doc = new CadDocument();
        var square = new PolylineEntity(Square());
        doc.AddEntity(square);
        var op = new DragKnifeOp { Name = "knife" };
        op.PrimitiveIds.Add(square.Id);
        op.KnifeOffset.Set(0.3);
        doc.ActivePart.Operations.Add(op);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.IsMotion).ToList();
        Assert.Contains(cuts, m => Math.Abs(m.To.Z + 0.1) < 1e-9);
        Assert.Contains(cuts, m => m.IsArc);

        var back = CbReader.Read(System.Xml.Linq.XDocument.Parse(CbWriter.ToString(doc)));
        var knife = Assert.IsType<DragKnifeOp>(Assert.Single(back.ActivePart.Operations));
        Assert.Equal(0.3, knife.KnifeOffset.Value, 9);
        Assert.Equal(-0.1, knife.TargetDepth.Value, 9);
    }
}
