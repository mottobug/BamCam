using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class VEngraveTests
{
    private static Path2 Square(double size) => Path2.FromPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)], closed: true);

    [Fact]
    public void Medial_axis_of_a_square_is_its_diagonals()
    {
        var chains = MedialAxis.Compute([Square(10)], 0.05);
        var points = chains.SelectMany(c => c).ToList();
        Assert.NotEmpty(points);
        // Every axis point lies near a diagonal and its radius is the distance to the nearest side.
        foreach (var m in points)
        {
            var toDiagonal = Math.Min(Math.Abs(m.P.X - m.P.Y), Math.Abs(m.P.X + m.P.Y - 10)) / Math.Sqrt(2);
            Assert.True(toDiagonal < 0.1, $"{m.P} off the diagonals");
            var side = Math.Min(Math.Min(m.P.X, 10 - m.P.X), Math.Min(m.P.Y, 10 - m.P.Y));
            Assert.True(Math.Abs(m.R - side) < 0.05, $"{m.P}: r {m.R} vs {side}");
        }
        Assert.Contains(points, m => Point2.Distance(m.P, new(5, 5)) < 0.1 && Math.Abs(m.R - 5) < 0.05);
        Assert.Contains(points, m => m.P.X < 0.3 && m.P.Y < 0.3); // reaches the corners
    }

    private static (CadDocument, VEngraveOp) Doc(Path2 shape, Action<VEngraveOp> setup)
    {
        var doc = new CadDocument();
        var e = new PolylineEntity(shape);
        doc.AddEntity(e);
        var op = new VEngraveOp { Name = "v" };
        op.PrimitiveIds.Add(e.Id);
        setup(op);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void Cone_never_cuts_beyond_the_outline()
    {
        var shape = Square(10);
        var (doc, op) = Doc(shape, o =>
        {
            o.VAngle.Set(90);
            o.TargetDepth.Set(-10);
        });
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = tp.Moves.Where(m => m.Kind == MoveKind.Feed && m.To.Z < 0).ToList();
        Assert.NotEmpty(cuts);
        // 90°: the cone radius at the surface equals the depth.
        foreach (var m in cuts)
            Assert.True(-m.To.Z <= shape.DistanceTo(m.To.XY) + 0.05, $"{m.To} too deep");
        Assert.Contains(cuts, m => m.To.Z < -4.9); // centre of the square: 5 deep
    }

    [Fact]
    public void Wide_areas_get_a_flat_bottom_at_target_depth()
    {
        var (doc, op) = Doc(Square(20), o =>
        {
            o.VAngle.Set(60);
            o.TargetDepth.Set(-2);
            o.FlatStepover.Set(0.5);
        });
        var tp = ToolpathGenerator.Generate(doc, op);
        var cuts = tp.Moves.Where(m => m.Kind == MoveKind.Feed).ToList();
        Assert.All(cuts, m => Assert.True(m.To.Z >= -2 - 1e-9));
        var rMax = 2 * Math.Tan(Math.PI / 6);
        // Outline of the flat bottom and clearing passes at -2, inside the inset square.
        var flat = cuts.Where(m => Math.Abs(m.To.Z + 2) < 1e-9).ToList();
        Assert.Contains(flat, m => Math.Abs(m.To.X - rMax) < 0.01);
        Assert.Contains(flat, m => Math.Abs(m.To.X - 10) < 0.6 && Math.Abs(m.To.Y - 10) < 0.6);
        Assert.All(flat, m => Assert.InRange(m.To.X, rMax - 0.01, 20 - rMax + 0.01));
    }

    [Fact]
    public void Depth_passes_and_round_trip()
    {
        var (doc, op) = Doc(Square(10), o =>
        {
            o.TargetDepth.Set(-3);
            o.DepthIncrement.Set(1);
            o.TipDiameter.Set(0.2);
        });
        var tp = ToolpathGenerator.Generate(doc, op);
        var depths = tp.Moves.Where(m => m.Kind == MoveKind.Feed).Select(m => Math.Round(m.To.Z, 6)).Distinct().ToList();
        Assert.Contains(-1.0, depths);
        Assert.Contains(-2.0, depths);
        Assert.Contains(-3.0, depths);

        var back = CbReader.Read(System.Xml.Linq.XDocument.Parse(CbWriter.ToString(doc)));
        var v = Assert.IsType<VEngraveOp>(Assert.Single(back.ActivePart.Operations));
        Assert.Equal(0.2, v.TipDiameter.Value, 9);
        Assert.Equal(-3, v.TargetDepth.Value, 9);
    }
}
