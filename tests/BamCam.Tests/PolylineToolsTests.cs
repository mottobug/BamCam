using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Edit ▸ Polyline: Set Start Point, Break at Points, Arc Fit, Remove Overlaps; Edit ▸ Intersection Points.</summary>
public class PolylineToolsTests
{
    private static PolylineEntity Poly(bool closed, params (double X, double Y)[] pts) =>
        new(Path2.FromPoints(pts.Select(p => new Point2(p.X, p.Y)).ToList(), closed));

    private static PolylineEntity Square() => Poly(true, (0, 0), (10, 0), (10, 10), (0, 10));

    private static PointListEntity Points(params (double X, double Y)[] pts)
    {
        var list = new PointListEntity();
        list.Points.AddRange(pts.Select(p => new Point3(p.X, p.Y, 0)));
        return list;
    }

    [Fact]
    public void Set_start_point_on_a_vertex_rotates_the_points()
    {
        var square = Square();
        Assert.True(CadOps.SetStartPoint(square, new Point2(10.005, 9.996), 0.01));
        Assert.Equal(4, square.Points.Count);
        Assert.Equal(new Point2(10, 10), square.Points[0].XY);
        Assert.Equal(new Point2(0, 10), square.Points[1].XY);
        Assert.True(square.Closed);
    }

    [Fact]
    public void Set_start_point_between_vertices_adds_one_there()
    {
        var square = Square();
        Assert.True(CadOps.SetStartPoint(square, new Point2(4, 0.3), 0.01));
        Assert.Equal(5, square.Points.Count);
        Assert.True(square.Points[0].XY.AlmostEquals(new Point2(4, 0), 1e-9));
        Assert.Equal(new Point2(10, 0), square.Points[1].XY);
        Assert.Equal(new Point2(0, 0), square.Points[^1].XY);
        Assert.Equal(40, square.ToPaths().Single().Length, 9);
    }

    [Fact]
    public void Set_start_point_on_an_arc_keeps_the_circle()
    {
        var circle = new PolylineEntity(new CircleEntity { Center = new Point3(0, 0, 0), Diameter = 10 }.ToPaths().Single());
        Assert.True(CadOps.SetStartPoint(circle, new Point2(0, 6), 0.01));
        var path = circle.ToPaths().Single();
        Assert.True(path.StartPoint.XY.AlmostEquals(new Point2(0, 5), 1e-9));
        Assert.Equal(Math.PI * 10, path.Length, 6);
    }

    [Fact]
    public void Set_start_point_of_an_open_polyline_does_nothing()
    {
        var line = Poly(false, (0, 0), (10, 0));
        Assert.False(CadOps.SetStartPoint(line, new Point2(5, 0), 0.01));
        Assert.Equal(2, line.Points.Count);
    }

    [Fact]
    public void Break_at_points_splits_where_the_points_lie_and_ignores_the_others()
    {
        var line = Poly(false, (0, 0), (10, 0), (10, 10));
        var broken = CadOps.BreakAtPoints([line, Points((3, 0), (10, 4), (5, 5))], 0.01);
        var pieces = broken.Single().Pieces.Select(p => p.ToPaths().Single()).ToList();
        Assert.Equal(new[] { 3.0, 11, 6 }, pieces.Select(p => Math.Round(p.Length, 9)));
    }

    [Fact]
    public void Break_at_one_point_opens_a_closed_shape_there()
    {
        var broken = CadOps.BreakAtPoints([Square(), Points((5, 10))], 0.01);
        var piece = broken.Single().Pieces.Single().ToPaths().Single();
        Assert.False(piece.Closed);
        Assert.True(piece.StartPoint.XY.AlmostEquals(new Point2(5, 10), 1e-9));
        Assert.True(piece.EndPoint.XY.AlmostEquals(new Point2(5, 10), 1e-9));
        Assert.Equal(40, piece.Length, 9);
    }

    [Fact]
    public void Break_at_points_at_the_ends_of_an_open_line_does_nothing()
    {
        Assert.Empty(CadOps.BreakAtPoints([Poly(false, (0, 0), (10, 0)), Points((0, 0), (10, 0))], 0.01));
    }

    [Fact]
    public void Arc_fit_turns_a_flattened_circle_into_arcs()
    {
        var pts = Enumerable.Range(0, 72).Select(i => Point2.FromAngle(i * Math.PI * 2 / 72, 20)).ToList();
        var polyline = new PolylineEntity(Path2.FromPoints(pts, true));
        var (before, after) = CadOps.ArcFit(polyline, 0.01);
        Assert.Equal(72, before);
        Assert.True(after <= 4, $"{after} segments");
        Assert.All(polyline.ToPaths().Single().Flatten(0.001), p => Assert.Equal(20, p.Length, 2));
    }

    [Fact]
    public void Arc_fit_keeps_existing_arcs_and_fits_the_lines_between_them()
    {
        // A slot: straight sides as many short segments, round ends as real arcs.
        var v = new List<Vertex>();
        for (var i = 0; i < 20; i++) v.Add(new Vertex(new Point3(i, 0, 0)));
        v.Add(new Vertex(new Point3(20, 0, 0), 1));
        for (var i = 20; i > 0; i--) v.Add(new Vertex(new Point3(i, 10, 0)));
        v.Add(new Vertex(new Point3(0, 10, 0), 1));
        var slot = new PolylineEntity(new Path2(v, true));
        var length = slot.ToPaths().Single().Length;
        var (before, after) = CadOps.ArcFit(slot, 0.01);
        Assert.Equal(42, before);
        // Straight runs stay lines (no circle fits a straight row of points), the two arcs stay.
        Assert.Equal(42, after);
        Assert.Equal(length, slot.ToPaths().Single().Length, 9);
        Assert.Equal(2, slot.Points.Count(p => Math.Abs(p.Bulge) > 1e-9));
    }

    [Fact]
    public void Arc_fit_between_arcs_fits_a_flattened_quarter()
    {
        // An arc, then a quarter circle as 30 short lines.
        var v = new List<Vertex> { new(new Point3(-10, 0, 0), Math.Tan(Math.PI / 8)) };
        for (var i = 0; i <= 30; i++)
        {
            var a = Math.PI / 2 * i / 30;
            v.Add(new Vertex(new Point3(10 * Math.Cos(a), -10 * Math.Sin(a) + 0, 0)));
        }
        v[0] = new Vertex(new Point3(0, 10, 0), Math.Tan(-Math.PI / 8));
        var p = new PolylineEntity(new Path2(v, false));
        var (before, after) = CadOps.ArcFit(p, 0.01);
        Assert.Equal(31, before);
        Assert.True(after <= 3, $"{after} segments");
        // Two quarters of a circle of radius 10, now as true arcs.
        Assert.Equal(Math.PI * 10, p.ToPaths().Single().Length, 6);
    }

    [Fact]
    public void Remove_overlaps_takes_out_a_spike_and_a_z_glitch()
    {
        // Along the bottom, back 3 and forward again (Z glitch), then a spike up and down on the right side.
        var path = Path2.FromPoints([new(0, 0), new(8, 0), new(5, 0), new(10, 0), new(10, 4), new(10, 7), new(10, 4), new(10, 10), new(0, 10)], true);
        var (result, removed) = CadOps.RemoveOverlaps(path, 0.01);
        Assert.True(removed >= 2);
        Assert.Equal(40, result.Length, 9);
        Assert.True(result.Closed);
    }

    [Fact]
    public void Remove_overlaps_leaves_a_clean_shape_alone()
    {
        var path = Square().ToPaths().Single();
        var (result, removed) = CadOps.RemoveOverlaps(path, 0.01);
        Assert.Equal(0, removed);
        Assert.Same(path, result);
    }

    [Fact]
    public void Intersection_points_of_a_circle_and_two_lines()
    {
        var circle = new CircleEntity { Center = new Point3(0, 0, 0), Diameter = 10 };
        var h = Poly(false, (-10, 0), (10, 0));
        var v = Poly(false, (0, -10), (0, 10));
        var points = CadOps.IntersectionPoints([circle, h, v]);
        // Four on the circle and the crossing of the lines.
        Assert.Equal(5, points.Count);
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(0, 0), 1e-9));
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(5, 0), 1e-9));
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(0, -5), 1e-9));
    }

    [Fact]
    public void Intersection_points_include_a_polyline_crossing_itself()
    {
        var bow = Poly(true, (0, 0), (10, 10), (10, 0), (0, 10));
        var points = CadOps.IntersectionPoints([bow]);
        Assert.Single(points);
        Assert.True(points[0].XY.AlmostEquals(new Point2(5, 5), 1e-9));
    }
}
