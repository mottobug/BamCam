using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class BreakTests
{
    private static PolylineEntity Line(double x1, double y1, double x2, double y2) =>
        new(Path2.FromPoints([new(x1, y1), new(x2, y2)], closed: false));

    [Fact]
    public void Two_crossing_lines_become_four_pieces()
    {
        var a = Line(0, 0, 10, 10);
        var b = Line(0, 10, 10, 0);
        var broken = CadOps.BreakAtIntersections([a, b]);
        Assert.Equal(2, broken.Count);
        foreach (var (_, pieces) in broken)
        {
            Assert.Equal(2, pieces.Count);
            Assert.All(pieces, p => Assert.True(p.ToPaths().Single().StartPoint.XY.AlmostEquals(new(5, 5), 1e-9) ||
                                                 p.ToPaths().Single().EndPoint.XY.AlmostEquals(new(5, 5), 1e-9)));
        }
    }

    [Fact]
    public void Circle_crossed_by_a_line_splits_into_two_arcs_with_their_length()
    {
        var circle = new CircleEntity { Center = new Point3(0, 0, 0), Diameter = 10 };
        var line = Line(-10, 0, 10, 0);
        var broken = CadOps.BreakAtIntersections([circle, line]);
        var arcs = broken.Single(b => b.Source == circle).Pieces;
        Assert.Equal(2, arcs.Count);
        Assert.All(arcs, p => Assert.Equal(Math.PI * 5, p.ToPaths().Single().Length, 6));
        // The line: three pieces (outside, inside, outside).
        Assert.Equal(new[] { 5.0, 10, 5 }, broken.Single(b => b.Source == line).Pieces.Select(p => Math.Round(p.ToPaths().Single().Length, 6)));
    }

    [Fact]
    public void Objects_that_only_meet_at_their_ends_stay_whole()
    {
        var a = Line(0, 0, 10, 0);
        var b = Line(10, 0, 10, 10);
        var far = Line(20, 20, 30, 30);
        Assert.Empty(CadOps.BreakAtIntersections([a, b, far]));
    }

    [Fact]
    public void A_self_crossing_polyline_is_split_at_its_crossing()
    {
        var bowtie = new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 10), new(10, 0), new(0, 10)], closed: false));
        var (_, pieces) = Assert.Single(CadOps.BreakAtIntersections([bowtie]));
        Assert.Equal(3, pieces.Count);
        var total = pieces.Sum(p => p.ToPaths().Single().Length);
        Assert.Equal(bowtie.ToPaths().Single().Length, total, 9);
    }

    [Fact]
    public void Closed_polyline_repeating_its_first_point_is_not_cut_there()
    {
        // Closed, and the last point repeats the first (as from some DXF files or drawing back to the start).
        var rect = new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)], closed: true));
        var cut = Line(5, -5, 5, 15);
        var pieces = CadOps.BreakAtIntersections([rect, cut]).Single(b => b.Source == rect).Pieces;
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(20, p.ToPaths().Single().Length, 9));
    }

    [Fact]
    public void Closed_polyline_crossed_once_is_opened_there()
    {
        var rect = new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], closed: true));
        var inside = Line(5, 5, 15, 6); // starts inside: one crossing
        var broken = CadOps.BreakAtIntersections([rect, inside]);
        var piece = Assert.Single(broken.Single(b => b.Source == rect).Pieces).ToPaths().Single();
        Assert.False(piece.Closed);
        Assert.Equal(40, piece.Length, 9);
        Assert.Equal(10, piece.StartPoint.X, 9);
        Assert.Equal(2, broken.Single(b => b.Source == inside).Pieces.Count);
    }

    [Fact]
    public void Closed_rectangle_crossed_twice_becomes_two_open_pieces()
    {
        var rect = new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], closed: true));
        var cut = Line(5, -5, 5, 15);
        var pieces = CadOps.BreakAtIntersections([rect, cut]).Single(b => b.Source == rect).Pieces;
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.False(p.ToPaths().Single().Closed));
        Assert.All(pieces, p => Assert.Equal(20, p.ToPaths().Single().Length, 9));
    }
}
