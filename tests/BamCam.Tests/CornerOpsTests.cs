using BamCam.Core.Cad;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

public class CornerOpsTests
{
    /// <summary>Square 10 x 10, counter-clockwise from the origin.</summary>
    private static Path2 Square() => Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], closed: true);

    private static Path2 Apply(Path2 path, int index, CornerKind kind, double value)
    {
        var (result, error) = CornerOps.Apply(path, index, kind, value);
        Assert.True(result != null, error);
        return result!;
    }

    [Fact]
    public void Fillet_replaces_the_corner_by_a_tangent_arc()
    {
        var p = Apply(Square(), 1, CornerKind.Fillet, 2);
        Assert.Equal(5, p.Count);
        Assert.True(p.Vertices[1].XY.AlmostEquals(new(8, 0)));
        Assert.True(p.Vertices[2].XY.AlmostEquals(new(10, 2)));
        var arc = p.Segments().ElementAt(1).ToArc();
        Assert.True(arc.Center.AlmostEquals(new(8, 2)), arc.Center.ToString());
        Assert.Equal(2, arc.Radius, 9);
        Assert.Equal(Math.PI / 2, arc.Sweep, 9); // counter-clockwise like the square
        // Area: square minus the corner outside the quarter circle.
        Assert.Equal(100 - (4 - Math.PI), p.SignedArea(), 9);
    }

    [Fact]
    public void Negative_radius_gives_an_inverted_arc_around_the_corner()
    {
        var p = Apply(Square(), 1, CornerKind.Fillet, -2);
        var arc = p.Segments().ElementAt(1).ToArc();
        Assert.True(arc.Center.AlmostEquals(new(10, 0)), arc.Center.ToString());
        Assert.Equal(-Math.PI / 2, arc.Sweep, 9);
        Assert.Equal(100 - Math.PI, p.SignedArea(), 9); // a quarter circle cut out of the corner
    }

    [Fact]
    public void Chamfer_cuts_the_corner_straight()
    {
        var p = Apply(Square(), 2, CornerKind.Chamfer, 3);
        Assert.Equal(100 - 4.5, p.SignedArea(), 9);
        Assert.All(p.Vertices, v => Assert.Equal(0, v.Bulge));
    }

    [Fact]
    public void Overcut_circle_passes_through_the_corner()
    {
        // Tool radius 1 in the corner (10, 10).
        var p = Apply(Square(), 2, CornerKind.Overcut, 1);
        var arc = p.Segments().Single(s => s.IsArc).ToArc();
        Assert.True(arc.Center.AlmostEquals(new(10 - Math.Sqrt(0.5), 10 - Math.Sqrt(0.5))), arc.Center.ToString());
        Assert.Equal(1, arc.Radius, 9);
        // The arc reaches the corner point and bulges beyond the square.
        Assert.True(Point2.Distance(arc.Center, new(10, 10)) - 1 < 1e-9);
        Assert.True(p.SignedArea() > 100);
    }

    [Fact]
    public void Corners_that_cannot_be_changed_are_refused()
    {
        Assert.Null(CornerOps.Apply(Square(), 1, CornerKind.Fillet, 20).Path); // tangent points beyond the edges
        var open = Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10)], closed: false);
        Assert.Null(CornerOps.Apply(open, 0, CornerKind.Fillet, 1).Path); // end of an open path
        Assert.NotNull(CornerOps.Apply(open, 1, CornerKind.Chamfer, 10).Path); // value equal to the edge: allowed
        Assert.Equal(-1, CornerOps.NearestVertex(open, new(5, 5), 1));
        Assert.Equal(1, CornerOps.NearestVertex(open, new(10.3, 0.2), 1));
    }
}
