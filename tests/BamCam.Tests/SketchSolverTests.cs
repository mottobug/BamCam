using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Sketching;

namespace BamCam.Tests;

/// <summary>The sketch: constraints, dimensions, variables, degrees of freedom and outlines.</summary>
public class SketchSolverTests
{
    /// <summary>A closed four-line outline, roughly a rectangle, with its corners made coincident.</summary>
    private static (Sketch S, SketchGeometry[] Lines) Quad(params Point2[] corners)
    {
        var s = Sketch.CreateNew();
        var lines = new SketchGeometry[4];
        for (var i = 0; i < 4; i++) lines[i] = s.AddLine(corners[i], corners[(i + 1) % 4]);
        for (var i = 0; i < 4; i++)
            s.AddConstraint(ConstraintKind.Coincident, [lines[i].Points[1], lines[(i + 1) % 4].Points[0]]);
        return (s, lines);
    }

    private static (Sketch S, SketchGeometry[] Lines) Rectangle()
    {
        var (s, l) = Quad(new(0.3, -0.2), new(41, 0.5), new(39.5, 19), new(-0.4, 21));
        s.AddConstraint(ConstraintKind.Horizontal, [l[0].Id]);
        s.AddConstraint(ConstraintKind.Horizontal, [l[2].Id]);
        s.AddConstraint(ConstraintKind.Vertical, [l[1].Id]);
        s.AddConstraint(ConstraintKind.Vertical, [l[3].Id]);
        return (s, l);
    }

    [Fact]
    public void Free_line_has_four_degrees_of_freedom()
    {
        var s = Sketch.CreateNew();
        s.AddLine(new(0, 0), new(10, 5));
        var r = SketchSolver.Solve(s);
        Assert.True(r.Solved);
        Assert.Equal(4, r.DegreesOfFreedom);
        Assert.Equal(SketchStatus.UnderConstrained, r.Status);
    }

    [Fact]
    public void Rectangle_becomes_fully_constrained_with_a_corner_on_the_origin_and_two_dimensions()
    {
        var (s, l) = Rectangle();
        Assert.Equal(4, SketchSolver.Solve(s).DegreesOfFreedom);
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "40");
        s.AddConstraint(ConstraintKind.Distance, [l[1].Id], expression: "20");
        var r = SketchSolver.Solve(s);
        Assert.True(r.Solved);
        Assert.Equal(0, r.DegreesOfFreedom);
        Assert.Equal(SketchStatus.FullyConstrained, r.Status);
        Assert.All(s.Geometry, g => Assert.Contains(g.Id, r.DeterminedGeometry));
        Assert.True(s.P(l[1].Points[1]).AlmostEquals(new Point2(40, 20), 1e-7));
        var outline = Assert.Single(s.Outlines());
        Assert.True(outline.Closed);
        Assert.Equal(120, outline.Length, 6);
    }

    [Fact]
    public void Dimensions_follow_variables_and_formulas()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        s.Variables.Add(new SketchVariable { Name = "Width", Expression = "60" });
        s.Variables.Add(new SketchVariable { Name = "Height", Expression = "Width / 3 + 5" });
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "Width");
        s.AddConstraint(ConstraintKind.Distance, [l[1].Id], expression: "Height");
        Assert.True(SketchSolver.Solve(s).Solved);
        Assert.True(s.P(l[1].Points[1]).AlmostEquals(new Point2(60, 25), 1e-7));

        s.Variables[0].Expression = "30";
        Assert.True(SketchSolver.Solve(s).Solved);
        Assert.True(s.P(l[1].Points[1]).AlmostEquals(new Point2(30, 15), 1e-7));
    }

    [Fact]
    public void Conflicting_dimension_is_reported()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "40");
        var bad = s.AddConstraint(ConstraintKind.Distance, [l[2].Id], expression: "30");
        var r = SketchSolver.Solve(s);
        Assert.False(r.Solved);
        Assert.Equal(SketchStatus.OverConstrained, r.Status);
        Assert.True(r.Conflicting.Contains(bad.Id) || r.Redundant.Contains(bad.Id));
    }

    [Fact]
    public void Repeated_constraint_is_redundant()
    {
        var (s, l) = Rectangle();
        // Opposite sides of a rectangle are parallel anyway.
        var extra = s.AddConstraint(ConstraintKind.Parallel, [l[0].Id, l[2].Id]);
        var r = SketchSolver.Solve(s);
        Assert.Contains(extra.Id, r.Redundant);
        Assert.Equal(SketchStatus.OverConstrained, r.Status);
    }

    [Fact]
    public void Tangent_line_and_circle_and_radius()
    {
        var s = Sketch.CreateNew();
        var line = s.AddLine(new(-20, 7), new(20, 6));
        var circle = s.AddCircle(new(0, 0), 4);
        s.AddConstraint(ConstraintKind.Horizontal, [line.Id]);
        s.AddConstraint(ConstraintKind.Coincident, [circle.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Radius, [circle.Id], expression: "5");
        // The circle lies to the right of the line's direction: side −1.
        var side = Math.Sign(Sketch.SignedDistance(new(0, 0), new(-20, 7), new(20, 6)));
        s.AddConstraint(ConstraintKind.Tangent, [line.Id, circle.Id], [side]);
        var r = SketchSolver.Solve(s);
        Assert.True(r.Solved);
        Assert.Equal(5, circle.Radius, 7);
        Assert.Equal(5, s.P(line.Points[0]).Y, 7);
        Assert.Equal(5, s.P(line.Points[1]).Y, 7);
    }

    [Fact]
    public void Arc_keeps_its_ends_on_one_circle_and_takes_a_radius()
    {
        var s = Sketch.CreateNew();
        var arc = s.AddArc(new(0, 0), new(10, 0), new(0, 10));
        s.AddConstraint(ConstraintKind.Coincident, [arc.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Radius, [arc.Id], expression: "7");
        var r = SketchSolver.Solve(s);
        Assert.True(r.Solved);
        Assert.Equal(7, Point2.Distance(s.P(arc.Points[0]), s.P(arc.Points[1])), 7);
        Assert.Equal(7, Point2.Distance(s.P(arc.Points[0]), s.P(arc.Points[2])), 7);
        // Centre fixed and radius given: each end can still slide round the circle.
        Assert.Equal(2, r.DegreesOfFreedom);
    }

    [Fact]
    public void Slot_of_two_arcs_and_two_lines_joins_into_one_outline()
    {
        var s = Sketch.CreateNew();
        var top = s.AddLine(new(0, 5), new(30, 5));
        var bottom = s.AddLine(new(30, -5), new(0, -5));
        var right = s.AddArc(new(30, 0), new(30, -5), new(30, 5));
        var left = s.AddArc(new(0, 0), new(0, 5), new(0, -5));
        s.AddConstraint(ConstraintKind.Coincident, [top.Points[1], right.Points[2]]);
        s.AddConstraint(ConstraintKind.Coincident, [right.Points[1], bottom.Points[0]]);
        s.AddConstraint(ConstraintKind.Coincident, [bottom.Points[1], left.Points[2]]);
        s.AddConstraint(ConstraintKind.Coincident, [left.Points[1], top.Points[0]]);
        Assert.True(SketchSolver.Solve(s).Solved);
        var outline = Assert.Single(s.Outlines());
        Assert.True(outline.Closed);
        Assert.Equal(60 + 10 * Math.PI, outline.Length, 6);
    }

    [Fact]
    public void Dragging_moves_the_point_and_the_constrained_rest_follows()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        Assert.True(SketchSolver.Solve(s).Solved);
        // Drag the opposite corner: width and height follow, the rectangle stays a rectangle.
        var corner = l[1].Points[1];
        var r = SketchSolver.Solve(s, new Dictionary<int, Point2> { [corner] = new(50, 30) });
        Assert.True(r.Solved);
        Assert.True(s.P(corner).AlmostEquals(new Point2(50, 30), 1e-7));
        Assert.Equal(30, s.P(l[3].Points[0]).Y, 7);
        Assert.Equal(50, s.P(l[0].Points[1]).X, 7);
    }

    [Fact]
    public void Dragging_a_determined_point_leaves_it_in_place()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "40");
        s.AddConstraint(ConstraintKind.Distance, [l[1].Id], expression: "20");
        SketchSolver.Solve(s);
        var corner = l[1].Points[1];
        var r = SketchSolver.Solve(s, new Dictionary<int, Point2> { [corner] = new(55, 33) });
        Assert.True(r.Solved);
        Assert.True(s.P(corner).AlmostEquals(new Point2(40, 20), 1e-7));
    }

    [Fact]
    public void Angle_symmetric_midpoint_and_equal()
    {
        var s = Sketch.CreateNew();
        var axis = s.AddLine(new(0, 0), new(0, 30), construction: true);
        s.AddConstraint(ConstraintKind.Coincident, [axis.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Vertical, [axis.Id]);
        var a = s.AddLine(new(0, 0), new(-12, 20));
        var b = s.AddLine(new(0, 0), new(9, 22));
        s.AddConstraint(ConstraintKind.Coincident, [a.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Coincident, [b.Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Symmetric, [a.Points[1], b.Points[1], axis.Id]);
        s.AddConstraint(ConstraintKind.Angle, [a.Id, b.Id], [-1], "60");
        s.AddConstraint(ConstraintKind.Distance, [a.Id], expression: "20");
        var m = s.AddPointGeometry(new(1, 1));
        s.AddConstraint(ConstraintKind.Midpoint, [m.Id, b.Id]);
        var r = SketchSolver.Solve(s);
        Assert.True(r.Solved, string.Join(", ", r.Conflicting));
        Assert.Equal(60, s.Measure(new SketchConstraint { Kind = ConstraintKind.Angle, Refs = [a.Id, b.Id] }), 6);
        Assert.Equal(20, Point2.Distance(s.P(b.Points[0]), s.P(b.Points[1])), 6);
        Assert.True(s.P(m.Points[0]).AlmostEquals((s.P(b.Points[0]) + s.P(b.Points[1])) * 0.5, 1e-7));
    }

    [Fact]
    public void Formulas_with_functions_and_errors()
    {
        double? None(string _) => null;
        Assert.Equal(7, SketchExpression.Evaluate("1 + 2 * 3", None), 12);
        Assert.Equal(12.5, SketchExpression.Evaluate("12,5", None), 12);
        Assert.Equal(2, SketchExpression.Evaluate("min(2, 5)", None), 12);
        Assert.Equal(0.5, SketchExpression.Evaluate("sin(30)", None), 12);
        Assert.Equal(-8, SketchExpression.Evaluate("-2^3", None), 12);
        Assert.Throws<FormatException>(() => SketchExpression.Evaluate("Width * 2", None));
        Assert.Throws<FormatException>(() => SketchExpression.Evaluate("(1 + 2", None));

        var values = SketchExpression.EvaluateVariables(
            [new SketchVariable { Name = "a", Expression = "b + 1" }, new SketchVariable { Name = "b", Expression = "a * 2" },
             new SketchVariable { Name = "c", Expression = "4" }], out var errors);
        Assert.Equal(4, values["c"]);
        Assert.Contains("a", errors.Keys);
        Assert.Contains("b", errors.Keys);
    }

    [Fact]
    public void Sketch_object_saves_and_restores_its_sketch()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "40");
        s.Variables.Add(new SketchVariable { Name = "W", Expression = "40" });
        SketchSolver.Solve(s);
        var entity = new SketchEntity();
        entity.SetSketch(s);
        var copy = (SketchEntity)entity.CloneEntity();
        var back = copy.GetSketch();
        Assert.Equal(s.Constraints.Count, back.Constraints.Count);
        Assert.Equal("W", back.Variables.Single().Name);
        Assert.Equal(Assert.Single(s.Outlines()).Length, Assert.Single(copy.ToPaths()).Length, 9);
        Assert.Contains(nameof(SketchEntity.Data), entity.HiddenParameters);
    }

    [Fact]
    public void Sketch_object_survives_a_cb_file_with_its_outline_as_a_polyline()
    {
        var (s, l) = Rectangle();
        s.AddConstraint(ConstraintKind.Coincident, [l[0].Points[0], Sketch.OriginId]);
        s.AddConstraint(ConstraintKind.Distance, [l[0].Id], expression: "40");
        s.AddConstraint(ConstraintKind.Distance, [l[1].Id], expression: "20");
        SketchSolver.Solve(s);
        var doc = new CadDocument();
        var entity = new SketchEntity();
        entity.SetSketch(s);
        doc.AddEntity(entity);
        var xml = BamCam.IO.Cb.CbWriter.ToString(doc);
        // CamBam sees a polyline with the outline.
        Assert.Contains("<pline", xml);
        var back = Assert.IsType<SketchEntity>(Assert.Single(BamCam.IO.Cb.CbReader.Parse(xml).AllEntities()));
        Assert.Equal(s.Constraints.Count, back.GetSketch().Constraints.Count);
        Assert.Equal(120, Assert.Single(back.ToPaths()).Length, 6);
        Assert.Equal(BamCam.Core.Localization.Tr.T("Fully constrained"), back.Status);
    }

    [Fact]
    public void Removing_a_line_removes_its_constraints_and_points()
    {
        var (s, l) = Rectangle();
        s.Remove([l[0].Id]);
        Assert.DoesNotContain(s.Constraints, c => c.Refs.Contains(l[0].Id) || c.Refs.Contains(l[0].Points[0]));
        Assert.Null(s.Point(l[0].Points[0]));
        Assert.NotNull(s.Point(Sketch.OriginId));
    }
}
