using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Draw ▸ More shapes and Draw ▸ Point List patterns.</summary>
public class ShapesTests
{
    [Fact]
    public void Ellipse_stays_within_the_tolerance_and_has_the_axis_ends_as_vertices()
    {
        var path = Shapes.Ellipse(new Point2(0, 0), new Point2(40, 20), 0.005)!;
        Assert.True(path.Closed);
        Assert.True(path.Vertices.All(v => Math.Abs(v.Bulge) > 1e-9), "only arcs");
        Assert.True(path.SegmentCount < 60, $"{path.SegmentCount} arcs");
        foreach (var p in path.Flatten(0.0005))
        {
            // Distance to the ellipse, approximately: the deviation of (x/a)² + (y/b)² from 1 scaled by the radius there.
            var x = (p.X - 20) / 20;
            var y = (p.Y - 10) / 10;
            var r = Math.Sqrt(x * x + y * y);
            var onEllipse = new Point2(20 + 20 * x / r, 10 + 10 * y / r);
            Assert.True(Point2.Distance(p, onEllipse) < 0.01, $"{p} is {Point2.Distance(p, onEllipse)} off");
        }
        foreach (var end in new Point2[] { new(40, 10), new(20, 20), new(0, 10), new(20, 0) })
            Assert.Contains(path.Vertices, v => v.XY.AlmostEquals(end, 1e-9));
    }

    [Fact]
    public void Ellipse_of_a_square_is_a_circle()
    {
        var path = Shapes.Ellipse(new Point2(-5, -5), new Point2(5, 5), 0.005)!;
        Assert.Equal(Math.PI * 10, path.Length, 4);
    }

    [Fact]
    public void Flat_spiral_ends_on_the_radius_with_turns_a_stepover_apart()
    {
        var path = Shapes.FlatSpiral(new Point2(10, 10), 10, 3, clockwise: false)!;
        Assert.True(path.StartPoint.XY.AlmostEquals(new Point2(10, 10), 1e-9));
        Assert.Equal(10, Point2.Distance(path.EndPoint.XY, new Point2(10, 10)), 9);
        Assert.All(path.Vertices.SkipLast(1), v => Assert.Equal(1, v.Bulge));
        // 7 half turns of 10 / 7 each (stepover 3 → 20 / 3 = 6.7 halves, rounded up), then the full circle.
        Assert.Equal(7 + 2, path.SegmentCount);
        // Along the X axis, the crossings of the spiral's turns on each side are 2 · 10 / 7 apart, less than the stepover.
        var spiral = path.Vertices.Take(8).Select(v => v.Point.X - 10).ToList();
        foreach (var side in new[] { 1, -1 })
        {
            var xs = spiral.Where(x => side * x > 1e-9).Select(Math.Abs).Order().ToList();
            for (var i = 1; i < xs.Count; i++) Assert.Equal(20.0 / 7, xs[i] - xs[i - 1], 9);
        }
    }

    [Fact]
    public void Flat_spiral_clockwise_turns_the_other_way()
    {
        var path = Shapes.FlatSpiral(new Point2(0, 0), 5, 1, clockwise: true)!;
        Assert.All(path.Vertices.SkipLast(1), v => Assert.Equal(-1, v.Bulge));
    }

    [Fact]
    public void Trochoidal_path_puts_a_full_circle_every_stepover()
    {
        var line = Path2.FromPoints([new(0, 0), new(10, 0)], false);
        var path = Shapes.TrochoidalAlong(line, 2, 2.5, clockwise: false)!;
        // Circles at 0, 2.5, 5, 7.5 and 10: three vertices each, starting left of the line (y = 2).
        Assert.Equal(15, path.Count);
        Assert.True(path.StartPoint.XY.AlmostEquals(new Point2(0, 2), 1e-9));
        Assert.True(path.EndPoint.XY.AlmostEquals(new Point2(10, 2), 1e-9));
        Assert.Equal(5 * 2 * Math.PI * 2 + 10, path.Length, 6);
    }

    [Fact]
    public void Trochoidal_path_adds_a_circle_at_the_end_when_the_steps_fall_short()
    {
        var line = Path2.FromPoints([new(0, 0), new(10, 0)], false);
        var path = Shapes.TrochoidalAlong(line, 1, 3, clockwise: true)!;
        // 0, 3, 6, 9 and the end at 10.
        Assert.Equal(15, path.Count);
        Assert.True(path.EndPoint.XY.AlmostEquals(new Point2(10, 1), 1e-9));
        Assert.Equal(-1, path.Vertices[0].Bulge);
    }

    [Fact]
    public void Spiral_path_loops_round_the_line_at_the_radius()
    {
        var line = Path2.FromPoints([new(0, 0), new(20, 0)], false);
        var path = Shapes.SpiralAlong(line, 3, 2, clockwise: false, 0.005)!;
        Assert.True(path.StartPoint.XY.AlmostEquals(new Point2(3, 0), 1e-6));
        Assert.True(path.EndPoint.XY.AlmostEquals(new Point2(23, 0), 1e-6));
        var pts = path.Flatten(0.001);
        Assert.Equal(3, pts.Max(p => p.Y), 2);
        Assert.Equal(-3, pts.Min(p => p.Y), 2);
    }

    [Fact]
    public void Tangents_of_two_circles_outer_and_inner()
    {
        var lines = Shapes.Tangents(new Point2(0, 0), 2, new Point2(10, 0), 2);
        Assert.Equal(4, lines.Count);
        // The outer ones run along y = ±2.
        Assert.Contains(lines, l => l.A.AlmostEquals(new Point2(0, 2), 1e-9) && l.B.AlmostEquals(new Point2(10, 2), 1e-9));
        Assert.Contains(lines, l => l.A.AlmostEquals(new Point2(0, -2), 1e-9) && l.B.AlmostEquals(new Point2(10, -2), 1e-9));
        // Every line touches both: distance of each centre to the line equals the radius.
        foreach (var (a, b) in lines)
        {
            Assert.Equal(2, DistanceToLine(new Point2(0, 0), a, b), 9);
            Assert.Equal(2, DistanceToLine(new Point2(10, 0), a, b), 9);
        }
    }

    [Fact]
    public void Tangents_from_a_point_to_a_circle()
    {
        var lines = Shapes.Tangents(new Point2(0, 0), 0, new Point2(10, 0), 5);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.A.AlmostEquals(new Point2(0, 0), 1e-9)));
        Assert.All(lines, l => Assert.Equal(Math.Sqrt(100 - 25), Point2.Distance(l.A, l.B), 9));
    }

    [Fact]
    public void No_tangent_when_one_circle_lies_inside_the_other()
    {
        Assert.Empty(Shapes.Tangents(new Point2(0, 0), 10, new Point2(1, 0), 2));
    }

    [Fact]
    public void Step_around_a_closed_square_and_an_open_line()
    {
        var square = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], true);
        var points = Shapes.StepAround(square, 5);
        Assert.Equal(8, points.Count);
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(10, 5), 1e-9));

        var line = Path2.FromPoints([new(0, 0), new(10, 0)], false);
        Assert.Equal(new[] { 0.0, 3, 6, 9, 10 }, Shapes.StepAround(line, 3).Select(p => Math.Round(p.X, 9)));
        Assert.Equal(new[] { 0.0, 4, 8 }, Shapes.StepAround(Path2.FromPoints([new(0, 0), new(8.2, 0)], false), 4).Select(p => Math.Round(p.X, 9)));
    }

    [Fact]
    public void Fill_grid_leaves_holes_empty_and_offset_rows_move_by_half_a_step()
    {
        var outer = Path2.FromPoints([new(0, 0), new(20, 0), new(20, 20), new(0, 20)], true);
        var hole = Path2.FromPoints([new(7, 7), new(13, 7), new(13, 13), new(7, 13)], true);
        var points = Shapes.FillGrid([outer, hole], 4, offsetRows: false);
        Assert.DoesNotContain(points, p => p.X > 7 && p.X < 13 && p.Y > 7 && p.Y < 13);
        Assert.All(points, p => Assert.True(p.X >= 0 && p.X <= 20 && p.Y >= 0 && p.Y <= 20));
        // Centred: 2, 6, 10, 14, 18 in both directions, without the hole's 10, 10.
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(2, 2), 1e-9));
        Assert.Equal(25 - 1, points.Count);

        var offset = Shapes.FillGrid([outer], 4, offsetRows: true);
        var rows = offset.GroupBy(p => Math.Round(p.Y, 6)).OrderBy(g => g.Key).ToList();
        Assert.Equal(2, Math.Abs(rows[0].Min(p => p.X) - rows[1].Min(p => p.X)), 9);
    }

    [Fact]
    public void Extents_are_the_corners_and_the_centre()
    {
        var rect = RectEntity.FromCorners(new Point2(0, 0), new Point2(10, 4), 0);
        var points = Shapes.Extents(rect.Bounds());
        Assert.Equal(5, points.Count);
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(10, 4), 1e-9));
        Assert.Contains(points, p => p.XY.AlmostEquals(new Point2(5, 2), 1e-9));
    }

    private static double DistanceToLine(Point2 p, Point2 a, Point2 b)
    {
        var d = b - a;
        return Math.Abs(d.X * (p.Y - a.Y) - d.Y * (p.X - a.X)) / d.Length;
    }
}
