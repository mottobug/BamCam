using BamCam.Core.Geometry;
using BamCam.Core.Sketching;

namespace BamCam.Tests;

/// <summary>Constraints and dimensions made from a selection in the sketch mode.</summary>
public class SketchCommandsTests
{
    [Fact]
    public void Horizontal_on_a_line_and_coincident_on_two_points()
    {
        var s = Sketch.CreateNew();
        var a = s.AddLine(new(0, 0), new(10, 1));
        var b = s.AddLine(new(10.5, 1.2), new(12, 8));
        var made = SketchCommands.AddConstraint(s, ConstraintKind.Horizontal, [a.Id], out var error);
        Assert.Null(error);
        Assert.Single(made);
        made = SketchCommands.AddConstraint(s, ConstraintKind.Coincident, [a.Points[1], b.Points[0]], out error);
        Assert.Null(error);
        var r = SketchCommands.Settle(s, made, out error);
        Assert.Null(error);
        Assert.True(r.Solved);
        Assert.True(s.P(a.Points[1]).AlmostEquals(s.P(b.Points[0]), 1e-9));
        Assert.Equal(s.P(a.Points[0]).Y, s.P(a.Points[1]).Y, 9);
    }

    [Fact]
    public void Point_and_line_with_coincident_puts_the_point_on_the_line()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(0, 0), new(20, 0));
        var p = s.AddPointGeometry(new(5, 3));
        var made = SketchCommands.AddConstraint(s, ConstraintKind.Coincident, [p.Id, line.Id], out _);
        Assert.Equal(ConstraintKind.PointOnObject, s.Constraint(Assert.Single(made))!.Kind);
        SketchCommands.Settle(s, made, out _);
        Assert.Equal(0, Sketch.SignedDistance(s.P(p.Points[0]), s.P(line.Points[0]), s.P(line.Points[1])), 9);
    }

    [Fact]
    public void Wrong_selection_gives_a_reason()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(0, 0), new(20, 0));
        Assert.Empty(SketchCommands.AddConstraint(s, ConstraintKind.Tangent, [line.Id], out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Null(SketchCommands.AddDimension(s, [], null, out error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Dimension_chooses_by_selection_and_measures()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(0, 0), new(30, 40));
        var circle = s.AddCircle(new(100, 0), 7);
        var arc = s.AddArc(new(0, 100), new(5, 100), new(0, 105));
        var other = s.AddLine(new(0, 0), new(10, 0));

        var length = SketchCommands.AddDimension(s, [line.Id], null, out _)!;
        Assert.Equal(ConstraintKind.Distance, length.Kind);
        Assert.Equal("50", length.Expression);

        var diameter = SketchCommands.AddDimension(s, [circle.Id], null, out _)!;
        Assert.Equal(ConstraintKind.Diameter, diameter.Kind);
        Assert.Equal("14", diameter.Expression);

        var radius = SketchCommands.AddDimension(s, [arc.Id], null, out _)!;
        Assert.Equal(ConstraintKind.Radius, radius.Kind);
        Assert.Equal("5", radius.Expression);

        var angle = SketchCommands.AddDimension(s, [other.Id, line.Id], null, out _)!;
        Assert.Equal(ConstraintKind.Angle, angle.Kind);
        Assert.Equal(53.1301, double.Parse(angle.Expression!, System.Globalization.CultureInfo.InvariantCulture), 4);

        var horizontal = SketchCommands.AddDimension(s, [line.Id], ConstraintKind.HorizontalDistance, out _)!;
        Assert.Equal("30", horizontal.Expression);
    }

    [Fact]
    public void Dimension_that_over_constrains_becomes_a_reference()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(0, 0), new(30, 0));
        s.AddConstraint(ConstraintKind.Coincident, [line.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Horizontal, [line.Id]);
        s.AddConstraint(ConstraintKind.Distance, [line.Id], expression: "30");
        SketchSolver.Solve(s);
        var extra = SketchCommands.AddDimension(s, [line.Id], ConstraintKind.HorizontalDistance, out _)!;
        var r = SketchCommands.Settle(s, [extra.Id], out var error);
        Assert.NotNull(error);
        Assert.True(extra.Reference);
        Assert.Equal(SketchStatus.FullyConstrained, r.Status);
    }

    [Fact]
    public void Constraint_that_repeats_others_is_not_added()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(0, 0), new(30, 0));
        s.AddConstraint(ConstraintKind.Horizontal, [line.Id]);
        SketchSolver.Solve(s);
        var made = SketchCommands.AddConstraint(s, ConstraintKind.Horizontal, [line.Id], out _);
        SketchCommands.Settle(s, made, out var error);
        Assert.NotNull(error);
        Assert.Single(s.Constraints);
    }

    /// <summary>A fully constrained 60 x 30 rectangle with its lower left corner on the origin.</summary>
    private static (Sketch S, SketchGeometry[] Lines) Rectangle()
    {
        var s = Sketch.CreateNew();
        Point2[] p = [new(0, 0), new(60, 0), new(60, 30), new(0, 30)];
        var l = new SketchGeometry[4];
        for (var i = 0; i < 4; i++) l[i] = s.AddLine(p[i], p[(i + 1) % 4]);
        for (var i = 0; i < 4; i++) s.AddConstraint(ConstraintKind.Coincident, [l[i].Points[1], l[(i + 1) % 4].Points[0]]);
        s.AddConstraint(ConstraintKind.Horizontal, [l[0].Id]);
        s.AddConstraint(ConstraintKind.Horizontal, [l[2].Id]);
        s.AddConstraint(ConstraintKind.Vertical, [l[1].Id]);
        s.AddConstraint(ConstraintKind.Vertical, [l[3].Id]);
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "60");
        s.AddConstraint(ConstraintKind.Distance, [l[1].Id], expression: "30");
        Assert.Equal(SketchStatus.FullyConstrained, SketchSolver.Solve(s).Status);
        return (s, l);
    }

    [Fact]
    public void Fillet_all_corners_keeps_the_rectangle_determined()
    {
        var (s, l) = Rectangle();
        var corners = l.Select(x => x.Points[0]).ToList();
        var made = SketchCommands.RoundCorners(s, corners, "5", chamfer: false, out var error);
        Assert.Null(error);
        Assert.NotEmpty(made);
        var r = SketchCommands.Settle(s, made, out error);
        Assert.Null(error);
        Assert.True(r.Solved);
        Assert.Equal(SketchStatus.FullyConstrained, r.Status);
        var outline = Assert.Single(s.Outlines());
        Assert.True(outline.Closed);
        Assert.Equal(2 * (60 + 30) - 8 * 5 + 2 * Math.PI * 5, outline.Length, 6);
        // The corner on the origin stays there as a construction point; the rounded outline starts 5 from it.
        Assert.Equal(0, outline.Flatten(0.001).Min(p => p.X), 6);
        Assert.Equal(0, outline.Flatten(0.001).Min(p => p.Y), 6);
    }

    [Fact]
    public void Fillet_radius_follows_a_variable()
    {
        var (s, l) = Rectangle();
        s.Variables.Add(new SketchVariable { Name = "R", Expression = "4" });
        var made = SketchCommands.RoundCorners(s, [l[1].Points[0]], "R", chamfer: false, out _);
        SketchCommands.Settle(s, made, out _);
        s.Variables[0].Expression = "8";
        Assert.True(SketchSolver.Solve(s).Solved);
        var arc = s.Geometry.Single(g => g.Kind == SketchGeometryKind.Arc);
        Assert.Equal(8, s.CircleOf(arc).Radius, 7);
        Assert.Equal(60 - 8, s.P(l[0].Points[1]).X, 7);
    }

    [Fact]
    public void Chamfer_cuts_the_same_length_off_both_lines()
    {
        var (s, l) = Rectangle();
        var made = SketchCommands.RoundCorners(s, [l[1].Id, l[2].Id], "3", chamfer: true, out var error);
        Assert.Null(error);
        var r = SketchCommands.Settle(s, made, out error);
        Assert.Null(error);
        Assert.Equal(SketchStatus.FullyConstrained, r.Status);
        Assert.True(s.P(l[1].Points[1]).AlmostEquals(new Point2(60, 27), 1e-7));
        Assert.True(s.P(l[2].Points[0]).AlmostEquals(new Point2(57, 30), 1e-7));
        Assert.Equal(2 * 90 - 6 + 3 * Math.Sqrt(2), Assert.Single(s.Outlines()).Length, 6);
    }

    [Fact]
    public void Fillet_too_large_or_without_corner_gives_a_reason()
    {
        var (s, l) = Rectangle();
        Assert.Empty(SketchCommands.RoundCorners(s, [l[0].Points[0]], "40", chamfer: false, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(SketchCommands.RoundCorners(s, [l[0].Id], "2", chamfer: false, out error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Tangent_data_keeps_the_side_and_the_kind_of_touching()
    {
        var s = Sketch.CreateNew();
        var big = s.AddCircle(new(0, 0), 10);
        var inside = s.AddCircle(new(3, 0), 6);
        var outside = s.AddCircle(new(18, 0), 7);
        Assert.Equal(-1, SketchCommands.TangentData(s, big.Id, inside.Id)[0]);
        Assert.Equal(1, SketchCommands.TangentData(s, big.Id, outside.Id)[0]);
        var made = SketchCommands.AddConstraint(s, ConstraintKind.Tangent, [big.Id, inside.Id], out _);
        var r = SketchCommands.Settle(s, made, out _);
        Assert.True(r.Solved);
        var (c1, r1) = s.CircleOf(big);
        var (c2, r2) = s.CircleOf(inside);
        Assert.Equal(Math.Abs(r1 - r2), Point2.Distance(c1, c2), 7);
    }
}
