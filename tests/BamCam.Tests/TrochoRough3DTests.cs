using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Simulation;
using BamCam.IO.Cb;
using MatmillPlugin;

namespace BamCam.Tests;

/// <summary>
/// Trochoidal 3D roughing (matmill plugin, discussion 31): the areas of Waterline Rough, cleared with matmill's slices.
/// Checked on a hemisphere with BamCam's milling simulation.
/// </summary>
[Collection(GlobalLibrariesCollection.Name)]
public class TrochoRough3DTests
{
    private const double R = 10;
    private const double Tool = 4;

    private sealed class Host : BamCam.Plugins.IPluginHost
    {
        public Version HostVersion => new(1, 0);
        public string Language => "en";
        public void AddCommand(BamCam.Plugins.PluginCommand command) { }
        public void Log(string message) { }
        public void AddImporter(BamCam.Plugins.FileFormat format, Func<string, CadDocument> read) { }
        public void AddExporter(BamCam.Plugins.FileFormat format, Action<CadDocument, string> write) { }
        public void AddPostMacro(string name, Func<BamCam.Plugins.PostMacroContext, string> evaluate) { }
        public void AddEntityType<TEntity>() where TEntity : ParametricEntity, new() { }

        public void AddOperation<TOp>(Action<ToolpathContext, TOp, Toolpath> generator) where TOp : MachineOp, new()
        {
            MachineOpFactory.Register(new TOp().XmlName, () => new TOp());
            ToolpathGenerator.RegisterGenerator(generator);
        }
    }

    public TrochoRough3DTests() => new Plugin().Initialize(new Host());

    /// <summary>A hemisphere of radius R on Z = 0, centred on the origin.</summary>
    private static SurfaceEntity Hemisphere(int rings = 30, int segments = 60)
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

    private static (CadDocument Doc, TrochoRough3DOp Op) Setup()
    {
        var doc = CadDocument.CreateNew();
        var op = new TrochoRough3DOp { Name = "troch3d" };
        op.PrimitiveIds.Add(doc.AddEntity(Hemisphere()).Id);
        op.ToolDiameter.Set(Tool);
        op.ToolProfile.Set(ToolProfile.EndMill);
        op.StockSurface.Set(10);
        op.TargetDepth.Set(0);
        op.DepthIncrement.Set(2.5);
        op.ClearancePlane.Set(15);
        op.BoundaryMethod.Set(BoundaryMethod.BoundingBox);
        op.BoundaryMargin.Set(6);
        op.CutFeedrate.Set(1000);
        op.PlungeFeedrate.Set(200);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    private static HeightField Simulate(CadDocument doc, MachineOp op, Toolpath tp)
    {
        var field = new HeightField(-22, -22, 0.2, 220, 220, 10, -5);
        foreach (var move in MillingSimulation.Moves(doc, [(op, tp)], 0.01)) MillingSimulation.Cut(field, move);
        return field;
    }

    [Fact]
    public void Roughs_level_by_level_with_slices_and_does_not_cut_into_the_model()
    {
        var (doc, op) = Setup();
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.True(tp.Warnings.Count == 0, string.Join("\n", tp.Warnings));

        var cuts = tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.IsMotion).ToList();
        Assert.Contains(cuts, m => m.IsArc); // trochoidal slices, not offset rings
        Assert.Equal(new[] { 7.5, 5.0, 2.5, 0.0 }, cuts.Select(m => Math.Round(m.To.Z, 6)).Distinct().OrderDescending());

        var f = Simulate(doc, op, tp);
        var lowest = double.PositiveInfinity;
        var cleared = 0;
        var around = 0;
        for (var j = 0; j < f.Ny; j++)
        for (var i = 0; i < f.Nx; i++)
        {
            var c = f.CellCentre(i, j);
            var d = c.Length;
            // The model is never cut: over it the material stays at or above the sphere (faceting: 0.1).
            if (d < R - 0.3) lowest = Math.Min(lowest, f[i, j] - Math.Sqrt(R * R - d * d));
            // Around it, between the tool's reach at the bottom level and the boundary, everything is down at Z = 0.
            if (d > R + Tool / 2 + 0.5 && Math.Max(Math.Abs(c.X), Math.Abs(c.Y)) < R + 6 - 0.5)
            {
                around++;
                if (f[i, j] < 0.01) cleared++;
            }
        }
        Assert.True(lowest > -0.1, $"cut {lowest} into the model");
        Assert.True(cleared > 0.97 * around, $"{cleared} of {around} cells around the model cleared");
    }

    [Fact]
    public void Between_areas_and_levels_the_tool_goes_up_to_the_clearance_plane()
    {
        var (doc, op) = Setup();
        var tp = ToolpathGenerator.Generate(doc, op);
        // Each new level starts from above: never a feed move across at the height of the level before.
        for (var k = 1; k < tp.Moves.Count; k++)
        {
            var (a, b) = (tp.Moves[k - 1], tp.Moves[k]);
            if (b.Kind == MoveKind.Feed && b.To.Z < a.To.Z - 1e-9)
                Assert.True(Point2.Distance(a.To.XY, b.To.XY) < 1e-6 || a.Kind == MoveKind.Rapid || b.Feed == op.PlungeFeedrate.Value,
                    $"move {k} goes down while moving sideways");
        }
        Assert.True(tp.Moves.Count(m => m.Kind == MoveKind.Rapid && Math.Abs(m.To.Z - 15) < 1e-9) >= 4);
    }

    [Fact]
    public void Is_saved_and_read_back_as_a_3D_profile_with_its_own_values()
    {
        var (doc, op) = Setup();
        op.ChordFeedrate.Set(2500);
        op.BoundaryMargin.Set(4);
        var back = CbReader.Parse(CbWriter.ToString(doc));
        var read = Assert.IsType<TrochoRough3DOp>(Assert.Single(back.ActivePart.Operations));
        Assert.Equal(2500, read.ChordFeedrate.Value, 9);
        Assert.Equal(4, read.BoundaryMargin.Value, 9);
        Assert.Equal(BoundaryMethod.BoundingBox, read.BoundaryMethod.Value);
        // The scanline settings are hidden.
        Assert.Contains(nameof(Profile3DOp.ScanAngle), read.HiddenParameters);
    }
}
