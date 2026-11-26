using BamCam.Core.Geometry;
using BamCam.Core.Sketching;

namespace BamCam.Tests;

/// <summary>Edit ▸ Convert To ▸ Sketch: drawing objects as a sketch with joins, directions and tangents.</summary>
public class SketchConversionTests
{
    private static readonly double Quarter = Math.Tan(Math.PI / 8); // bulge of a 90° arc

    private static Path2 RoundedRectangle(double z = 0) => new(
    [
        new Vertex(new Point3(5, 0, z)), new Vertex(new Point3(35, 0, z), Quarter),
        new Vertex(new Point3(40, 5, z)), new Vertex(new Point3(40, 15, z), Quarter),
        new Vertex(new Point3(35, 20, z)), new Vertex(new Point3(5, 20, z), Quarter),
        new Vertex(new Point3(0, 15, z)), new Vertex(new Point3(0, 5, z), Quarter),
    ], closed: true);

    [Fact]
    public void A_rounded_rectangle_keeps_its_joins_directions_and_tangents()
    {
        var path = RoundedRectangle();
        var s = SketchConversion.FromPaths([path], out var z, out var error);
        Assert.NotNull(s);
        Assert.Null(error);
        Assert.Equal(0, z);
        Assert.Equal(4, s.Geometry.Count(g => g.Kind == SketchGeometryKind.Line));
        Assert.Equal(4, s.Geometry.Count(g => g.Kind == SketchGeometryKind.Arc));
        Assert.Equal(2, s.Constraints.Count(c => c.Kind == ConstraintKind.Horizontal));
        Assert.Equal(2, s.Constraints.Count(c => c.Kind == ConstraintKind.Vertical));
        Assert.Equal(8, s.Constraints.Count(c => c.Kind == ConstraintKind.Tangent));
        Assert.Equal(8, s.Constraints.Count(c => c.Kind == ConstraintKind.Coincident));
        Assert.DoesNotContain(s.Constraints, c => c.IsDimension);

        var r = SketchSolver.Solve(s);
        Assert.Equal(SketchStatus.UnderConstrained, r.Status);
        var outline = Assert.Single(s.Outlines());
        Assert.True(outline.Closed);
        Assert.Equal(path.Length, outline.Length, 6);
    }

    [Fact]
    public void A_closed_path_of_arcs_around_one_centre_becomes_a_circle()
    {
        var circle = new Path2([new Vertex(new Point3(15, 10, 0), 1), new Vertex(new Point3(5, 10, 0), 1)], closed: true);
        var s = SketchConversion.FromPaths([circle], out _, out _);
        var g = Assert.Single(s!.Geometry, g => g.Kind == SketchGeometryKind.Circle);
        Assert.Equal(5, g.Radius, 9);
        Assert.True(s.P(g.Points[0]).AlmostEquals(new Point2(10, 10), 1e-9));
    }

    [Fact]
    public void A_clockwise_arc_is_stored_counter_clockwise_and_an_end_at_the_origin_joins_it()
    {
        // From the origin along X, then a clockwise half circle up and over to (20, 0).
        var path = new Path2([new Vertex(new Point3(0, 0, 0)), new Vertex(new Point3(10, 0, 0), -1), new Vertex(new Point3(20, 0, 0))], closed: false);
        var s = SketchConversion.FromPaths([path], out _, out _)!;
        var line = Assert.Single(s.Geometry, g => g.Kind == SketchGeometryKind.Line);
        var arc = Assert.Single(s.Geometry, g => g.Kind == SketchGeometryKind.Arc);
        Assert.Contains(s.Constraints, c => c.Kind == ConstraintKind.Coincident && c.Refs.Contains(Sketch.OriginId) && c.Refs.Contains(line.Points[0]));
        Assert.Contains(s.Constraints, c => c.Kind == ConstraintKind.Coincident && c.Refs.Contains(line.Points[1]) && c.Refs.Contains(arc.Points[2]));
        // Counter-clockwise in the sketch: from (20, 0) back to (10, 0), still above the X axis.
        Assert.True(s.P(arc.Points[1]).AlmostEquals(new Point2(20, 0), 1e-9));
        Assert.True(s.P(arc.Points[2]).AlmostEquals(new Point2(10, 0), 1e-9));
        Assert.Equal(10 + Math.PI * 5, s.Outlines().Sum(o => o.Length), 6);
        Assert.True(s.Outlines().SelectMany(o => o.Flatten(0.01)).Max(p => p.Y) > 4.9);
    }

    [Fact]
    public void Objects_at_one_height_keep_it_and_different_heights_are_refused()
    {
        Assert.NotNull(SketchConversion.FromPaths([RoundedRectangle(3)], out var z, out _));
        Assert.Equal(3, z);

        Assert.Null(SketchConversion.FromPaths([RoundedRectangle(0), RoundedRectangle(2)], out _, out var error));
        Assert.Contains("Z", error);
    }

    [Fact]
    public void Two_outlines_sharing_points_at_the_origin_are_joined_there()
    {
        // The two halves of a clip, mirrored about the Y axis, with rounding errors of the mirroring (1e-15).
        Point2[] right = [new(0, 0), new(38, 0), new(38, 4), new(39, 4), new(39, 7), new(38, 7), new(38, 11), new(35, 11), new(35, 13), new(41, 13), new(41, -2), new(0, -2)];
        var left = right.Select(p => new Point2(-p.X + 9.8e-16, p.Y + 4.6e-15 * (p.X == 38 && p.Y == 0 ? 1 : 0))).ToArray();
        var s = SketchConversion.FromPaths([Path2.FromPoints(right, closed: true), Path2.FromPoints(left, closed: true)], out _, out var error);
        Assert.Null(error);
        Assert.Equal(24, s!.Geometry.Count(g => g.Kind == SketchGeometryKind.Line));
        Assert.All(s.Geometry.Where(g => g.Kind == SketchGeometryKind.Line), g =>
            Assert.Contains(s.Constraints, c => c.Refs.Length == 1 && c.Refs[0] == g.Id && c.Kind is ConstraintKind.Horizontal or ConstraintKind.Vertical));
        var r = SketchSolver.Solve(s);
        Assert.NotEqual(SketchStatus.OverConstrained, r.Status);
        Assert.Equal(2, s.Outlines().Count);
        // Both outlines start and end at the origin, which holds them (a join the others already give is dropped).
        Assert.True(s.Constraints.Count(c => c.Kind == ConstraintKind.Coincident && c.Refs[0] == Sketch.OriginId) >= 2);
        Assert.All(s.Outlines(), o => Assert.True(o.StartPoint.XY.AlmostEquals(new Point2(0, 0), 1e-9)));
    }

    [Fact]
    public void A_polygon_with_many_sides_converts()
    {
        var points = Enumerable.Range(0, 200).Select(i => Point2.FromAngle(i * 2 * Math.PI / 200, 50) + new Point2(100, 100));
        var s = SketchConversion.FromPaths([Path2.FromPoints(points, closed: true)], out _, out var error);
        Assert.Null(error);
        Assert.Equal(200, s!.Geometry.Count(g => g.Kind == SketchGeometryKind.Line));
        Assert.Equal(200, s.Constraints.Count(c => c.Kind == ConstraintKind.Coincident));
        Assert.Single(s.Outlines());
    }
}
