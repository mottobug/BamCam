using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class ObjectSnapTests
{
    private static LineEntity Line(double x1, double y1, double x2, double y2)
    {
        var line = new LineEntity();
        line.Points.AddRange([new Point3(x1, y1, 0), new Point3(x2, y2, 0)]);
        return line;
    }

    private static SnapPoint? Find(Point2 cursor, SnapKinds kinds, params Entity[] entities) =>
        ObjectSnap.Find(entities, cursor, 1, kinds);

    [Fact]
    public void Line_end_and_middle()
    {
        var line = Line(0, 0, 10, 0);
        Assert.Equal(new SnapPoint(new(10, 0), SnapKinds.EndPoint), Find(new(9.6, 0.3), SnapKinds.All, line));
        Assert.Equal(new SnapPoint(new(5, 0), SnapKinds.MidPoint), Find(new(5.2, -0.4), SnapKinds.All, line));
        // Switched off: no middle point.
        Assert.Null(Find(new(5.2, -0.4), SnapKinds.EndPoint, line));
    }

    [Fact]
    public void Circle_centre_and_quadrants_but_no_end_points()
    {
        var circle = new CircleEntity { Center = new Point3(20, 10, 0), Diameter = 8 };
        Assert.Equal(new SnapPoint(new(20, 10), SnapKinds.Center), Find(new(20.3, 9.8), SnapKinds.All, circle));
        Assert.Equal(new SnapPoint(new(20, 14), SnapKinds.Quadrant), Find(new(20.2, 13.7), SnapKinds.All, circle));
        Assert.Equal(new SnapPoint(new(16, 10), SnapKinds.Quadrant), Find(new(16.3, 10.4), SnapKinds.All, circle));
        // The split points of the circle (0° and 180°) are quadrants only.
        Assert.Null(Find(new(24.1, 10), SnapKinds.EndPoint | SnapKinds.MidPoint, circle));
    }

    [Fact]
    public void Arc_quadrants_only_where_the_arc_passes()
    {
        // Quarter arc from 0° to 90° around the origin, radius 5.
        var arc = new ArcEntity { Center = new Point3(0, 0, 0), Radius = 5, StartAngle = 0, Sweep = 90 };
        var points = ObjectSnap.Points(arc, SnapKinds.Quadrant).Select(p => p.Point).ToList();
        Assert.Equal(2, points.Count);
        Assert.Contains(points, p => p.AlmostEquals(new(5, 0)));
        Assert.Contains(points, p => p.AlmostEquals(new(0, 5)));
        var mid = Assert.Single(ObjectSnap.Points(arc, SnapKinds.MidPoint)).Point;
        Assert.True(mid.AlmostEquals(Point2.FromAngle(Math.PI / 4, 5)), mid.ToString());
    }

    [Theory]
    [InlineData(1e-16)]
    [InlineData(-1e-16)]
    [InlineData(1e-12)]
    public void Arc_contains_its_start_angle_despite_rounding(double startError)
    {
        // A quarter arc starting at 0° whose start angle came out a rounding error off (platform dependent).
        var arc = new Arc2(Point2.Zero, 5, startError, Math.PI / 2);
        Assert.True(arc.ContainsAngle(0));
        Assert.True(arc.ContainsAngle(Math.PI / 2));
        Assert.False(arc.ContainsAngle(Math.PI));
        Assert.False(arc.ContainsAngle(-0.1));
    }

    [Fact]
    public void Polyline_segments_have_middle_points_and_arc_centres()
    {
        // Square 10 x 10 with a rounded corner (arc segment, radius 2) at the top right.
        var path = new Path2([
            new Vertex(new Point3(0, 0, 0)), new Vertex(new Point3(10, 0, 0)),
            new Vertex(new Point3(10, 8, 0), Math.Tan(Math.PI / 8)), new Vertex(new Point3(8, 10, 0)), new Vertex(new Point3(0, 10, 0)),
        ], true);
        var poly = new PolylineEntity(path);
        Assert.Equal(new SnapPoint(new(5, 0), SnapKinds.MidPoint), Find(new(5, 0.2), SnapKinds.All, poly));
        var centre = Find(new(8.2, 8.1), SnapKinds.All, poly);
        Assert.Equal(SnapKinds.Center, centre!.Value.Kind);
        Assert.True(centre.Value.Point.AlmostEquals(new(8, 8)));
    }

    [Fact]
    public void Intersections_of_lines_line_and_circle_and_two_circles()
    {
        var a = Line(0, 0, 10, 10);
        var b = Line(0, 6, 9, 0); // crosses y = x at (3.6, 3.6), away from both middle points
        var crossing = Find(new(3.9, 3.4), SnapKinds.All, a, b);
        Assert.Equal(SnapKinds.Intersection, crossing!.Value.Kind);
        Assert.True(crossing.Value.Point.AlmostEquals(new(3.6, 3.6)), crossing.ToString());

        var circle = new CircleEntity { Center = new Point3(0, 0, 0), Diameter = 10 };
        var horizontal = Line(-10, 3, 10, 3);
        var hit = Find(new(4.2, 3.1), SnapKinds.Intersection, circle, horizontal);
        Assert.True(hit!.Value.Point.AlmostEquals(new(4, 3)), hit.ToString());

        var other = new CircleEntity { Center = new Point3(8, 0, 0), Diameter = 10 };
        var cross = Find(new(4.1, 3.2), SnapKinds.Intersection, circle, other);
        Assert.True(cross!.Value.Point.AlmostEquals(new(4, 3)), cross.ToString());

        // Lines that would cross only when extended do not snap.
        Assert.Null(Find(new(20, 20), SnapKinds.Intersection, Line(0, 0, 10, 10), Line(30, 10, 20, 20)));
    }

    [Fact]
    public void End_point_wins_over_an_intersection_at_the_same_place()
    {
        // Two lines meeting at a corner: end point, not intersection.
        var hit = Find(new(10.2, 0.1), SnapKinds.All, Line(0, 0, 10, 0), Line(10, 0, 10, 10));
        Assert.Equal(SnapKinds.EndPoint, hit!.Value.Kind);
    }

    [Fact]
    public void Nothing_outside_the_catch_radius()
    {
        Assert.Null(Find(new(5, 3), SnapKinds.All, Line(0, 0, 10, 0)));
    }
}
