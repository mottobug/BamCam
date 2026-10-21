using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using BamCam.IO.Dxf;

namespace BamCam.Tests;

public class SplineTests
{
    private static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    [Fact]
    public void Cubic_bezier_is_evaluated_exactly()
    {
        // Single cubic Bezier (0,0) (0,1) (1,1) (1,0): midpoint (0.5, 0.75).
        Point3[] ctrl = [new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)];
        var knots = Nurbs.ClampedKnots(4, 3);
        Assert.Equal(new double[] { 0, 0, 0, 0, 1, 1, 1, 1 }, knots);
        var m = Nurbs.Evaluate(3, ctrl, [], knots, 0.5);
        Assert.Equal(0.5, m.X, 12);
        Assert.Equal(0.75, m.Y, 12);
    }

    [Fact]
    public void Rational_quadratic_gives_an_exact_quarter_circle()
    {
        Point3[] ctrl = [new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        double[] weights = [1, Math.Sqrt(0.5), 1];
        var knots = Nurbs.ClampedKnots(3, 2);
        for (var u = 0.0; u <= 1; u += 0.125)
        {
            var p = Nurbs.Evaluate(2, ctrl, weights, knots, u);
            Assert.Equal(1, Math.Sqrt(p.X * p.X + p.Y * p.Y), 9);
        }
    }

    [Fact]
    public void Flattened_spline_stays_within_tolerance()
    {
        var s = new SplineEntity(3, [new(0, 0, 0), new(10, 30, 0), new(40, -20, 0), new(50, 10, 0), new(70, 0, 0)]);
        var path = s.ToPaths().Single();
        var pts = path.Flatten(0.0001);
        if (path.Closed) pts.Add(pts[0]);
        var knots = s.Knots;
        for (var i = 0; i <= 200; i++)
        {
            var q = Nurbs.Evaluate(3, s.ControlPoints, s.Weights, knots, knots[3] + (knots[s.ControlPoints.Count] - knots[3]) * i / 200).XY;
            var d = pts.Zip(pts.Skip(1), (a, b) => DistanceToSegment(q, a, b)).Min();
            Assert.True(d <= SplineEntity.Tolerance * 1.01, $"deviation {d} at sample {i}");
        }
    }

    private static double DistanceToSegment(Point2 q, Point2 a, Point2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(((q.X - a.X) * ab.X + (q.Y - a.Y) * ab.Y) / Math.Max(ab.X * ab.X + ab.Y * ab.Y, 1e-24), 0, 1);
        return (a + ab * t - q).Length;
    }

    [Fact]
    public void CamBam_spline_drawing_is_read_and_written_back()
    {
        var file = Path.Combine(Dir, "spline.cb");
        if (!File.Exists(file)) return;
        var doc = CbReader.Load(file);
        var splines = doc.AllEntities().OfType<SplineEntity>().ToList();
        Assert.Equal(2, splines.Count);

        var closed = splines.Single(s => s.Id == 1);
        Assert.True(closed.Closed);
        Assert.Equal(3, closed.Degree);
        Assert.Equal(37, closed.ControlPoints.Count);
        Assert.Equal(41, closed.Knots.Count);
        Assert.True(closed.ToPaths().Single().Closed);

        // CamBam splines are chains of cubic Beziers: every third control point (anchor) lies on the curve.
        foreach (var s in splines)
        {
            var path = s.ToPaths().Single();
            var pts = path.Flatten(0.0001);
            if (path.Closed) pts.Add(pts[0]);
            for (var i = 0; i < s.ControlPoints.Count; i += 3)
            {
                var anchor = s.ControlPoints[i].XY;
                var d = pts.Zip(pts.Skip(1), (a, b) => DistanceToSegment(anchor, a, b)).Min();
                Assert.True(d <= SplineEntity.Tolerance, $"spline {s.Id}: anchor {i} is {d} from the curve");
            }
        }

        var again = CbReader.Parse(CbWriter.ToString(doc));
        var open = again.AllEntities().OfType<SplineEntity>().Single(s => s.Id == 3);
        var original = splines.Single(s => s.Id == 3);
        Assert.False(open.Closed);
        Assert.Equal(original.ControlPoints, open.ControlPoints);
        Assert.Equal(original.Knots, open.Knots);
    }

    [Fact]
    public void Dxf_splines_stay_splines()
    {
        var dxf = Path.Combine(Dir, "T-Bar F102.DXF");
        if (!File.Exists(dxf)) return;
        var doc = DxfReader.Load(dxf, out var warnings);
        Assert.Empty(warnings);
        var splines = doc.AllEntities().OfType<SplineEntity>().ToList();
        Assert.NotEmpty(splines);
        Assert.All(splines, s => Assert.True(Nurbs.IsValid(s.Degree, s.ControlPoints, s.Weights, s.Knots)));
        Assert.All(splines, s => Assert.True(s.ToPaths().Single().Vertices.Count >= 2));
    }

    [Fact]
    public void Editing_control_points_rebuilds_the_knots()
    {
        var s = new SplineEntity(3, [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 1, 0)]);
        s.SetControlPoints([new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 1, 0), new(4, 0, 0)]);
        Assert.Equal(5 + 3 + 1, s.Knots.Count);
        s.SetControlPoints([new(0, 0, 0), new(1, 1, 0)]);
        Assert.Equal(1, s.Degree);
        Assert.Equal(2 + 1 + 1, s.Knots.Count);
    }
}
