using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Issue 15: Scan Angle turns the rows of the Horizontal and Vertical 3D profile.</summary>
public class ScanAngleTests
{
    /// <summary>A flat square 40 x 40 at Z -1 and a Horizontal finishing profile over it.</summary>
    private static (CadDocument Doc, Profile3DOp Op) Square(double angle, Profile3DMethod method = Profile3DMethod.Horizontal)
    {
        var doc = new CadDocument();
        var mesh = new SurfaceEntity
        {
            Vertices = { new Point3(0, 0, -1), new Point3(40, 0, -1), new Point3(40, 40, -1), new Point3(0, 40, -1) },
            Faces = { 0, 1, 2, 0, 2, 3 },
        };
        doc.AddEntity(mesh);
        var op = new Profile3DOp { Name = "scan" };
        op.PrimitiveIds.Add(mesh.Id);
        op.Profile3DMethod.Set(method);
        op.RoughingFinishing.Set(RoughingFinishing.Finishing);
        op.ToolDiameter.Set(6);
        op.ToolProfile.Set(ToolProfile.BallNose);
        op.StepOver.Set(0.5);
        op.Resolution.Set(0.5);
        op.StockSurface.Set(0);
        op.TargetDepth.Set(-2);
        op.ClearancePlane.Set(5);
        op.ScanAngle.Set(angle);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    /// <summary>Directions (degrees, 0 to 180) of the cutting moves longer than 5 mm.</summary>
    private static List<double> RowAngles(Toolpath tp)
    {
        var angles = new List<double>();
        Point3? pos = null;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.Kind != MoveKind.Rapid && pos is { } p && Point2.Distance(p.XY, m.To.XY) > 5)
            {
                var a = Math.Atan2(m.To.Y - p.Y, m.To.X - p.X) * 180 / Math.PI;
                angles.Add((a + 360) % 180);
            }
            pos = m.To;
        }
        return angles;
    }

    [Theory]
    [InlineData(0, Profile3DMethod.Horizontal, 0)]
    [InlineData(30, Profile3DMethod.Horizontal, 30)]
    [InlineData(-45, Profile3DMethod.Horizontal, 135)]
    [InlineData(30, Profile3DMethod.Vertical, 120)]
    public void Rows_run_at_the_scan_angle(double angle, Profile3DMethod method, double expected)
    {
        var (doc, op) = Square(angle, method);
        var tp = ToolpathGenerator.Generate(doc, op);

        var angles = RowAngles(tp);
        Assert.NotEmpty(angles);
        Assert.All(angles, a => Assert.True(Math.Min(Math.Abs(a - expected), 180 - Math.Abs(a - expected)) < 0.5, $"row at {a:0.##}°"));
        // The rows stay within the surface (the boundary is its outline) and on it.
        Assert.All(tp.Moves.Where(m => m.Kind is MoveKind.Feed), m =>
        {
            Assert.InRange(m.To.X, -1e-6, 40 + 1e-6);
            Assert.InRange(m.To.Y, -1e-6, 40 + 1e-6);
        });
        Assert.Contains(tp.Moves, m => m.Kind == MoveKind.Feed && Math.Abs(m.To.Z + 1) < 1e-6);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(45)]
    public void Turned_rows_reach_the_boundary_and_the_corners(double angle)
    {
        var (doc, op) = Square(angle);
        var tp = ToolpathGenerator.Generate(doc, op);

        // Every row starts and ends on the outline of the square, not up to a resolution before it.
        static bool OnEdge(Point3 p) => Math.Min(Math.Min(Math.Abs(p.X), Math.Abs(p.X - 40)), Math.Min(Math.Abs(p.Y), Math.Abs(p.Y - 40))) < 1e-6;
        var rows = new List<(Point3 From, Point3 To)>();
        Point3? start = null, pos = null;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.Kind == MoveKind.Rapid)
            {
                if (start is { } s && pos is { } e && Point2.Distance(s.XY, e.XY) > 1e-6) rows.Add((s, e));
                start = null;
            }
            else if (start == null && pos is { } p && Point2.Distance(p.XY, m.To.XY) > 1e-6) start = p;
            pos = m.To;
        }
        if (start is { } last && pos is { } end && Point2.Distance(last.XY, end.XY) > 1e-6) rows.Add((last, end));
        Assert.True(rows.Count > 5);
        Assert.All(rows, r => Assert.True(OnEdge(r.From) && OnEdge(r.To), $"row {r.From.XY} - {r.To.XY}"));
        // No strip is left: the first and the last row lie at most half a stepover (1.5 mm here) from the corners that are
        // outermost across the rows, and every corner is at most a stepover from a row.
        var corners = new[] { new Point2(0, 0), new Point2(40, 0), new Point2(40, 40), new Point2(0, 40) };
        double Across(Point2 p) => -p.X * Math.Sin(angle * Math.PI / 180) + p.Y * Math.Cos(angle * Math.PI / 180);
        var outermost = new[] { corners.MinBy(Across), corners.MaxBy(Across) };
        foreach (var corner in corners)
        {
            var nearest = rows.Min(r => Path2.DistanceToSegment(corner, r.From.XY, r.To.XY));
            var limit = outermost.Contains(corner) ? 1.5 : 3;
            Assert.True(nearest <= limit + 1e-6, $"corner {corner}: nearest row {nearest:0.###}");
        }
    }
}
