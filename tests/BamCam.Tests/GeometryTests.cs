using BamCam.Core.Geometry;

namespace BamCam.Tests;

public class GeometryTests
{
    private const double Tol = 1e-6;

    [Fact]
    public void Bulge_one_is_counter_clockwise_half_circle()
    {
        var arc = Arc2.FromBulge(new Point2(1, 0), new Point2(-1, 0), 1);
        Assert.Equal(0, arc.Center.X, Tol);
        Assert.Equal(0, arc.Center.Y, Tol);
        Assert.Equal(1, arc.Radius, Tol);
        Assert.Equal(Math.PI, arc.Sweep, Tol);
        // CCW from (1,0) passes through (0,1).
        var mid = arc.PointAt(0.5);
        Assert.Equal(0, mid.X, Tol);
        Assert.Equal(1, mid.Y, Tol);
    }

    [Fact]
    public void Negative_bulge_is_clockwise()
    {
        var arc = Arc2.FromBulge(new Point2(1, 0), new Point2(-1, 0), -1);
        Assert.True(arc.IsClockwise);
        Assert.Equal(-1, arc.PointAt(0.5).Y, Tol);
    }

    [Fact]
    public void Bulge_roundtrip()
    {
        var arc = Arc2.FromBulge(new Point2(0, 0), new Point2(3, 1), 0.37);
        Assert.Equal(0.37, arc.ToBulge(), Tol);
        Assert.True(arc.End.AlmostEquals(new Point2(3, 1), 1e-9));
    }

    [Fact]
    public void Circle_path_area_matches_pi_r2()
    {
        var p = new Path2([new Vertex(new Point3(2, 0, 0), 1), new Vertex(new Point3(-2, 0, 0), 1)], true);
        Assert.Equal(Math.PI * 4, p.SignedArea(), 1e-9);
        Assert.False(p.IsClockwise);
        Assert.Equal(Math.PI * 4, p.Length, 1e-9);
    }

    [Fact]
    public void Reverse_flips_orientation_and_keeps_shape()
    {
        var p = new Path2([
            new Vertex(new Point3(0, 0, 0)),
            new Vertex(new Point3(10, 0, 0), 0.5),
            new Vertex(new Point3(10, 10, 0)),
            new Vertex(new Point3(0, 10, 0)),
        ], true);
        var r = p.Reversed();
        Assert.Equal(-p.SignedArea(), r.SignedArea(), 1e-9);
        Assert.Equal(p.Length, r.Length, 1e-9);
        var open = new Path2(p.Vertices, false);
        Assert.Equal(open.Length, open.Reversed().Length, 1e-9);
        Assert.Equal(open.StartPoint, open.Reversed().EndPoint);
    }

    [Fact]
    public void Arc_through_three_points()
    {
        var arc = Arc2.FromThreePoints(new Point2(1, 0), new Point2(0, 1), new Point2(-1, 0))!.Value;
        Assert.Equal(1, arc.Radius, Tol);
        Assert.Equal(Math.PI, arc.Sweep, Tol);
        var cw = Arc2.FromThreePoints(new Point2(-1, 0), new Point2(0, 1), new Point2(1, 0))!.Value;
        Assert.Equal(-Math.PI, cw.Sweep, Tol);
    }

    [Fact]
    public void Arc_bounds_include_quadrant_points()
    {
        var arc = new Arc2(Point2.Zero, 5, Angles.ToRadians(-45), Angles.ToRadians(90));
        var b = arc.Bounds();
        Assert.Equal(5, b.Max.X, Tol);
        Assert.True(b.Max.Y < 5);
    }

    [Fact]
    public void Offset_square_outward_with_round_corners()
    {
        var sq = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], true);
        var res = Polygons.Offset([sq], 1);
        Assert.Single(res);
        var area = Math.Abs(res[0].SignedArea());
        // Square grown by 1 with round corners: 100 + 4*10 + pi.
        Assert.Equal(100 + 40 + Math.PI, area, 0.05);
    }

    [Fact]
    public void Inset_shape_keeps_island()
    {
        var outer = Path2.FromPoints([new(0, 0), new(30, 0), new(30, 30), new(0, 30)], true);
        var island = Path2.FromPoints([new(10, 10), new(20, 10), new(20, 20), new(10, 20)], true);
        var shapes = Polygons.BuildShapes([outer, island]);
        Assert.Single(shapes);
        Assert.Single(shapes[0].Holes);
        var inset = Polygons.InsetShape(shapes[0], 2);
        Assert.Single(inset);
        Assert.Single(inset[0].Holes);
    }

    [Fact]
    public void Matrix_mirror_flips_determinant()
    {
        var m = Matrix4.Mirror(new Point2(0, 0), new Point2(0, 1));
        var p = m.Transform(new Point2(3, 4));
        Assert.Equal(-3, p.X, Tol);
        Assert.Equal(4, p.Y, Tol);
        Assert.True(m.DeterminantXY < 0);
    }

    [Fact]
    public void Rotation_about_center()
    {
        var m = Matrix4.RotationZ(Math.PI / 2, new Point2(1, 1));
        var p = m.Transform(new Point2(2, 1));
        Assert.Equal(1, p.X, Tol);
        Assert.Equal(2, p.Y, Tol);
    }
}
