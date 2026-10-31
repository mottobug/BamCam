using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.Tests;

public class BullNoseTests
{
    /// <summary>Square pyramid 20 x 20, apex height 8: a vertex, sloped facets and edges.</summary>
    private static Mesh Pyramid()
    {
        Point3 apex = new(0, 0, 8);
        Point3[] b = [new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)];
        var tris = Enumerable.Range(0, 4).Select(i => new Triangle(b[i], b[(i + 1) % 4], apex)).ToList();
        return new Mesh(tris, 2);
    }

    /// <summary>Reference: highest ball (corner radius) tip over many centres of the flat end disc.</summary>
    private static double BruteForce(Mesh mesh, Point2 p, double r, double rc)
    {
        var flat = r - rc;
        var best = double.NegativeInfinity;
        const int n = 60;
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
        {
            var q = p + new Point2(-flat + 2 * flat * i / n, -flat + 2 * flat * j / n);
            if (Point2.Distance(q, p) > flat + 1e-12) continue;
            if (mesh.DropCutter(q.X, q.Y, rc, CutterShape.Ball) is { } z) best = Math.Max(best, z);
        }
        // The rim itself, finely.
        for (var k = 0; k < 720; k++)
        {
            var q = p + Point2.FromAngle(2 * Math.PI * k / 720, flat);
            if (mesh.DropCutter(q.X, q.Y, rc, CutterShape.Ball) is { } z) best = Math.Max(best, z);
        }
        return best;
    }

    [Theory]
    [InlineData(0, 0)]      // over the apex: touches with the flat end
    [InlineData(3, 0.5)]    // on a facet near the apex
    [InlineData(5, 5)]      // over an edge (diagonal)
    [InlineData(7, -2)]
    [InlineData(-9.5, 9)]   // near a base corner
    [InlineData(12, 3)]     // beside the pyramid, touching with the rim
    public void Bull_nose_matches_a_ball_swept_over_the_flat_end(double x, double y)
    {
        var mesh = Pyramid();
        const double r = 3, rc = 1;
        var exact = mesh.DropCutter(x, y, r, CutterShape.Bull, rc)!.Value;
        var reference = BruteForce(mesh, new Point2(x, y), r, rc);
        // The sampled reference can only be lower (it misses points between samples).
        Assert.InRange(exact - reference, -1e-6, 0.01);
    }

    [Fact]
    public void Limits_are_flat_end_mill_and_ball_nose()
    {
        var mesh = Pyramid();
        foreach (var (x, y) in new[] { (0.0, 0.0), (4.0, 1.0), (6.0, 6.0), (11.0, 2.0) })
        {
            Assert.Equal(mesh.DropCutter(x, y, 3, CutterShape.Flat)!.Value, mesh.DropCutter(x, y, 3, CutterShape.Bull, 0)!.Value, 9);
            Assert.Equal(mesh.DropCutter(x, y, 3, CutterShape.Ball)!.Value, mesh.DropCutter(x, y, 3, CutterShape.Bull, 3)!.Value, 9);
        }
    }

    [Fact]
    public void Bull_nose_lies_between_flat_and_ball()
    {
        // On sloped surfaces a flat end touches highest, a ball lowest; the bull nose lies between.
        var mesh = Pyramid();
        foreach (var (x, y) in new[] { (3.0, 0.5), (5.0, 5.0), (7.0, -2.0) })
        {
            var flat = mesh.DropCutter(x, y, 3, CutterShape.Flat)!.Value;
            var ball = mesh.DropCutter(x, y, 3, CutterShape.Ball)!.Value;
            var bull = mesh.DropCutter(x, y, 3, CutterShape.Bull, 1)!.Value;
            Assert.InRange(bull, ball - 1e-9, flat + 1e-9);
        }
    }
}
