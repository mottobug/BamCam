using BamCam.Core.Cad;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

public class TrimExtendTests
{
    private static Path2 Line(double x1, double y1, double x2, double y2) => Path2.FromPoints([new(x1, y1), new(x2, y2)], false);

    private static Path2 Circle(double cx, double cy, double r) => new([
        new Vertex(new Point3(cx + r, cy, 0), 1), new Vertex(new Point3(cx - r, cy, 0), 1)], true);

    [Fact]
    public void Line_is_extended_at_the_clicked_end_to_the_next_boundary()
    {
        var line = Line(0, 0, 5, 0);
        Path2[] walls = [Line(8, -5, 8, 5), Line(12, -5, 12, 5), Line(-3, -5, -3, 5)];
        var right = TrimExtend.Extend(line, new(4.5, 0.1), walls)!;
        Assert.True(right.EndPoint.XY.AlmostEquals(new(8, 0)));
        Assert.True(right.StartPoint.XY.AlmostEquals(new(0, 0)));
        var left = TrimExtend.Extend(line, new(0.5, 0), walls)!;
        Assert.True(left.StartPoint.XY.AlmostEquals(new(-3, 0)));
        // Nothing in the way upwards.
        Assert.Null(TrimExtend.Extend(Line(0, 0, 0, 5), new(0, 5), walls));
    }

    [Fact]
    public void Line_is_extended_to_a_circle()
    {
        var hit = TrimExtend.Extend(Line(0, 0, 2, 0), new(2, 0), [Circle(10, 0, 3)])!;
        Assert.True(hit.EndPoint.XY.AlmostEquals(new(7, 0)), hit.EndPoint.ToString());
    }

    [Fact]
    public void Arc_is_extended_around_its_circle()
    {
        // Quarter arc around the origin from 0° to 90° (counter-clockwise), radius 5; a wall at x = -3.
        var arc = new Path2([new Vertex(new Point3(5, 0, 0), Math.Tan(Math.PI / 8)), new Vertex(new Point3(0, 5, 0))], false);
        var extended = TrimExtend.Extend(arc, new(0.2, 5), [Line(-3, -10, -3, 10)])!;
        Assert.True(extended.EndPoint.XY.AlmostEquals(new(-3, 4)), extended.EndPoint.ToString());
        var a = extended.Segments().Single().ToArc();
        Assert.Equal(5, a.Radius, 9);
        Assert.True(a.Center.AlmostEquals(new(0, 0)));
        Assert.True(a.Sweep > Math.PI / 2);
    }

    [Fact]
    public void Trim_removes_the_clicked_piece_between_crossings()
    {
        var line = Line(0, 0, 20, 0);
        Path2[] walls = [Line(5, -5, 5, 5), Line(12, -5, 12, 5)];
        // Middle piece: two pieces remain.
        var pieces = TrimExtend.Trim(line, new(8, 0.3), walls)!;
        Assert.Equal(2, pieces.Count);
        Assert.True(pieces[0].EndPoint.XY.AlmostEquals(new(5, 0)));
        Assert.True(pieces[1].StartPoint.XY.AlmostEquals(new(12, 0)));
        Assert.True(pieces[1].EndPoint.XY.AlmostEquals(new(20, 0)));
        // End piece: one piece remains.
        var end = TrimExtend.Trim(line, new(18, 0), walls)!;
        Assert.True(Assert.Single(end).EndPoint.XY.AlmostEquals(new(12, 0)));
        // No crossing: nothing to trim.
        Assert.Null(TrimExtend.Trim(line, new(8, 0), [Line(30, -5, 30, 5)]));
    }

    [Fact]
    public void Trim_cuts_an_arc_at_a_line()
    {
        // Half circle from (5,0) over the top to (-5,0); a vertical line at x = 0 crosses it at the top.
        var arc = new Path2([new Vertex(new Point3(5, 0, 0), 1), new Vertex(new Point3(-5, 0, 0))], false);
        var pieces = TrimExtend.Trim(arc, new(-4, 3), [Line(0, -10, 0, 10)])!;
        var rest = Assert.Single(pieces);
        Assert.True(rest.EndPoint.XY.AlmostEquals(new(0, 5)), rest.EndPoint.ToString());
        Assert.Equal(Math.PI / 2, rest.Segments().Single().ToArc().Sweep, 9);
    }
}
