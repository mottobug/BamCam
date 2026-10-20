using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class Profile3DTests
{
    private const double R = 10;

    /// <summary>Upper hemisphere of radius 10 centred at the origin (top at Z = 10).</summary>
    private static SurfaceEntity Hemisphere(int rings = 60, int segments = 120)
    {
        var s = new SurfaceEntity();
        s.Vertices.Add(new Point3(0, 0, R));
        for (var i = 1; i <= rings; i++)
        {
            var polar = Math.PI / 2 * i / rings;
            for (var j = 0; j < segments; j++)
            {
                var az = 2 * Math.PI * j / segments;
                s.Vertices.Add(new Point3(R * Math.Sin(polar) * Math.Cos(az), R * Math.Sin(polar) * Math.Sin(az), R * Math.Cos(polar)));
            }
        }
        int V(int ring, int seg) => 1 + (ring - 1) * segments + (seg % segments);
        for (var j = 0; j < segments; j++) s.Faces.AddRange([0, V(1, j), V(1, j + 1)]);
        for (var i = 1; i < rings; i++)
        for (var j = 0; j < segments; j++)
        {
            s.Faces.AddRange([V(i, j), V(i + 1, j), V(i + 1, j + 1)]);
            s.Faces.AddRange([V(i, j), V(i + 1, j + 1), V(i, j + 1)]);
        }
        return s;
    }

    private static Mesh MeshOf(SurfaceEntity s)
    {
        var v = s.Vertices;
        var tris = new List<Triangle>();
        for (var i = 0; i < s.Faces.Count; i += 3) tris.Add(new Triangle(v[s.Faces[i]], v[s.Faces[i + 1]], v[s.Faces[i + 2]]));
        return new Mesh(tris, 2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 4)]
    [InlineData(7, -2)]
    [InlineData(-9, 1)]
    [InlineData(11, 0)]
    public void Ball_drop_cutter_matches_analytic_sphere(double x, double y)
    {
        var mesh = MeshOf(Hemisphere());
        const double rb = 3;
        var d = Math.Sqrt(x * x + y * y);
        var z = mesh.DropCutter(x, y, rb, CutterShape.Ball);
        Assert.NotNull(z);
        // Ball centre lies on a sphere of radius R + rb; tip = centre - rb.
        var expected = Math.Sqrt((R + rb) * (R + rb) - d * d) - rb;
        if (d > R) expected = Math.Max(expected, double.NegativeInfinity);
        Assert.Equal(expected, z!.Value, 1); // within 0.05 for this tessellation
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(2, 0, 10)]
    [InlineData(8, 0, 8.660)]   // touches at horizontal distance 5: sqrt(100 - 25)
    [InlineData(0, 12.5, 3.122)] // touches at horizontal distance 9.5: sqrt(100 - 90.25)
    public void Flat_drop_cutter_matches_analytic_sphere(double x, double y, double expected)
    {
        var mesh = MeshOf(Hemisphere());
        const double rf = 3;
        var d = Math.Sqrt(x * x + y * y);
        var exact = d <= rf ? R : Math.Sqrt(R * R - (d - rf) * (d - rf));
        Assert.Equal(exact, expected, 2);
        var z = mesh.DropCutter(x, y, rf, CutterShape.Flat);
        Assert.NotNull(z);
        Assert.Equal(exact, z!.Value, 1);
    }

    [Fact]
    public void Drop_cutter_outside_mesh_returns_null()
    {
        Assert.Null(MeshOf(Hemisphere()).DropCutter(30, 30, 3, CutterShape.Flat));
    }

    private static (CadDocument Doc, Profile3DOp Op) Setup(Profile3DMethod method, RoughingFinishing rf, ToolProfile tool)
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(Hemisphere(30, 60));
        var op = new Profile3DOp { Name = "3D" };
        op.PrimitiveIds.Add(1);
        op.Profile3DMethod.Set(method);
        op.RoughingFinishing.Set(rf);
        op.ToolProfile.Set(tool);
        op.ToolDiameter.Set(4);
        op.StockSurface.Set(10);
        op.TargetDepth.Set(0);
        op.DepthIncrement.Set(2.5);
        op.ClearancePlane.Set(15);
        op.BoundaryMethod.Set(BoundaryMethod.BoundingBox);
        op.BoundaryMargin.Set(3);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void Scanline_finish_follows_the_surface_without_gouging()
    {
        var (doc, op) = Setup(Profile3DMethod.Horizontal, RoughingFinishing.Finishing, ToolProfile.BallNose);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = tp.Moves.Where(m => m.Kind == MoveKind.Feed).Select(m => m.To).ToList();
        Assert.True(cuts.Count > 100);
        // Ball tip never below the analytic offset surface (tolerance for faceting).
        foreach (var p in cuts)
        {
            var d = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            var surface = d < R + 2 ? Math.Sqrt(Math.Pow(R + 2, 2) - d * d) - 2 : 0;
            Assert.True(p.Z >= Math.Max(surface, 0) - 0.1, $"gouge at {p}: {p.Z} < {surface}");
        }
        Assert.Equal(R, cuts.Max(p => p.Z), 1);
    }

    [Fact]
    public void Scanline_roughing_passes_respect_depth_levels()
    {
        var (doc, op) = Setup(Profile3DMethod.Vertical, RoughingFinishing.Roughing, ToolProfile.EndMill);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var zs = tp.Moves.Where(m => m.Kind == MoveKind.Feed).Select(m => Math.Round(m.To.Z, 6)).ToHashSet();
        Assert.Contains(7.5, zs);
        Assert.Contains(5.0, zs);
        Assert.Contains(0.0, zs);
    }

    [Fact]
    public void Waterline_rough_clears_around_the_model()
    {
        var (doc, op) = Setup(Profile3DMethod.WaterlineRough, RoughingFinishing.Roughing, ToolProfile.EndMill);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.IsMotion).Select(m => m.To).ToList();
        Assert.NotEmpty(cuts);
        foreach (var p in cuts)
        {
            // At level z the model's radius is sqrt(R² - z²); the tool centre stays at least radius 2 away.
            var modelR = p.Z < R ? Math.Sqrt(R * R - p.Z * p.Z) : 0;
            var d = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.True(d >= modelR + 2 - 0.05, $"tool too close at {p}: {d} < {modelR + 2}");
        }
        Assert.Equal(new[] { 7.5, 5.0, 2.5, 0.0 }, cuts.Select(p => Math.Round(p.Z, 6)).Distinct().OrderDescending());
    }

    [Fact]
    public void Waterline_finish_produces_one_contour_per_level()
    {
        var (doc, op) = Setup(Profile3DMethod.WaterlineFinish, RoughingFinishing.Finishing, ToolProfile.EndMill);
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var levels = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.IsMotion).Select(m => Math.Round(m.To.Z, 6)).Distinct().Count();
        Assert.Equal(4, levels);
    }

    [Fact]
    public void Skull_sample_surface3d_operations_roundtrip_exactly()
    {
        var path = @"C:\ProgramData\CamBam plus 1.0\samples\skull-big-foam.cb";
        if (!File.Exists(path)) return;
        var doc = CbReader.Load(path);
        var ops = doc.Parts.SelectMany(p => p.Operations).ToList();
        Assert.Equal(9, ops.Count);
        Assert.All(ops, o => Assert.IsType<Profile3DOp>(o));
        Assert.Equal([1, 2, 3, 4], ((Profile3DOp)ops[0]).BoundaryShapes);

        var original = XDocument.Load(path).Descendants("machineops").SelectMany(m => m.Elements()).ToList();
        var written = CbWriter.ToXml(doc).Descendants("machineops").SelectMany(m => m.Elements()).ToList();
        Assert.Equal(original.Count, written.Count);
        for (var i = 0; i < original.Count; i++)
            Assert.Equal(original[i].ToString(SaveOptions.DisableFormatting), written[i].ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Skull_sample_generates_toolpaths()
    {
        var path = @"C:\ProgramData\CamBam plus 1.0\samples\skull-big-foam.cb";
        if (!File.Exists(path)) return;
        var doc = CbReader.Load(path);
        foreach (var op in doc.Parts.SelectMany(p => p.Operations).Take(2))
        {
            var tp = ToolpathGenerator.Generate(doc, op);
            Assert.Empty(tp.Warnings);
            Assert.True(tp.Moves.Count > 100, $"{op.Name}: {tp.Moves.Count} moves");
            Assert.True(tp.Bounds().Min.Z >= -50 - 1e-6);
        }
    }
}
