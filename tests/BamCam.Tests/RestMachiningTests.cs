using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Simulation;
using BamCam.IO.Cb;
using MatmillPlugin;

namespace BamCam.Tests;

/// <summary>
/// Rest machining of 3D profiles (Rest From Operation, discussion 31): two blocks with a 4 mm gap, roughed with a 6 mm
/// end mill that cannot enter the gap, then with a 2 mm end mill taking only the rest.
/// </summary>
[Collection(GlobalLibrariesCollection.Name)]
public class RestMachiningTests
{
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

    public RestMachiningTests() => new Plugin().Initialize(new Host());

    /// <summary>A closed box from Z = 0 to <paramref name="height"/>.</summary>
    private static SurfaceEntity Box(double x0, double y0, double x1, double y1, double height)
    {
        var s = new SurfaceEntity();
        foreach (var z in new[] { 0.0, height })
        {
            s.Vertices.Add(new Point3(x0, y0, z));
            s.Vertices.Add(new Point3(x1, y0, z));
            s.Vertices.Add(new Point3(x1, y1, z));
            s.Vertices.Add(new Point3(x0, y1, z));
        }
        int[] faces =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, // bottom, top
            0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7, // sides
        ];
        s.Faces.AddRange(faces);
        return s;
    }

    private static T Waterline<T>(CadDocument doc, string name, double tool, double depthIncrement) where T : Profile3DOp, new()
    {
        var op = new T { Name = name };
        op.PrimitiveIds.AddRange(doc.AllEntities().OfType<SurfaceEntity>().Select(e => e.Id));
        op.Profile3DMethod.Set(Profile3DMethod.WaterlineRough);
        op.ToolDiameter.Set(tool);
        op.ToolProfile.Set(ToolProfile.EndMill);
        op.StockSurface.Set(10);
        op.TargetDepth.Set(0);
        op.DepthIncrement.Set(depthIncrement);
        op.ClearancePlane.Set(15);
        op.BoundaryMethod.Set(BoundaryMethod.BoundingBox);
        op.BoundaryMargin.Set(5);
        op.CutFeedrate.Set(1000);
        op.PlungeFeedrate.Set(200);
        doc.ActivePart.Operations.Add(op);
        return op;
    }

    /// <summary>Two blocks 8 high, 4 apart: the gap between them is x -2 to 2.</summary>
    private static (CadDocument Doc, Profile3DOp Big) Blocks()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(Box(-20, -10, -2, 10, 8));
        doc.AddEntity(Box(2, -10, 20, 10, 8));
        return (doc, Waterline<Profile3DOp>(doc, "big", 6, 4));
    }

    private static List<Move> Cuts(Toolpath tp) => tp.Moves.Where(m => m.Kind != MoveKind.Rapid && m.IsMotion && m.To.Z < 9.99).ToList();

    [Fact]
    public void The_small_tool_cuts_only_the_gap_the_large_one_could_not_enter()
    {
        var (doc, big) = Blocks();
        var small = Waterline<Profile3DOp>(doc, "rest", 2, 2);
        var full = ToolpathGenerator.Generate(doc, small);
        small.RestFromOperation.Set("big");
        var rest = ToolpathGenerator.Generate(doc, small);

        Assert.True(rest.Warnings.Count == 0, string.Join("\n", rest.Warnings));
        var cuts = Cuts(rest);
        Assert.NotEmpty(cuts);
        Assert.True(cuts.Count < Cuts(full).Count / 3, $"{cuts.Count} rest moves of {Cuts(full).Count}");
        // In the gap (the moves run along it), on every level down to the bottom, and nowhere far from it.
        Assert.Equal(new[] { 8.0, 6, 4, 2, 0 }, cuts.Where(m => Math.Abs(m.To.X) < 1.01).Select(m => Math.Round(m.To.Z, 6)).Distinct().OrderDescending());
        Assert.All(cuts, m => Assert.True(Math.Abs(m.To.X) <= 1 + 2 + 0.1, $"rest move far from the gap at {m.To}"));

        // Both together leave the gap cleared: the simulation finds the material between the blocks gone.
        var jobs = new List<(MachineOp, Toolpath)> { (big, ToolpathGenerator.Generate(doc, big)), (small, rest) };
        var f = new HeightField(-30, -20, 0.1, 600, 400, 10, -1);
        foreach (var m in MillingSimulation.Moves(doc, jobs, 0.01)) MillingSimulation.Cut(f, m);
        for (var y = -5.0; y <= 5; y += 1)
        {
            var i = (int)((0 + 30) / 0.1);
            var j = (int)((y + 20) / 0.1);
            Assert.True(f[i, j] < 0.01, $"gap not cleared at y {y}: {f[i, j]}");
        }
        // The blocks stay whole.
        for (var j = 0; j < f.Ny; j++)
        for (var i = 0; i < f.Nx; i++)
        {
            var c = f.CellCentre(i, j);
            var inBlock = Math.Abs(c.Y) < 10 - 0.15 && Math.Abs(c.X) > 2 + 0.15 && Math.Abs(c.X) < 20 - 0.15;
            if (inBlock) Assert.True(f[i, j] > 8 - 0.01, $"cut into a block at {c}: {f[i, j]}");
        }
    }

    [Fact]
    public void Without_rest_left_there_is_nothing_to_cut_and_it_says_so()
    {
        var (doc, _) = Blocks();
        var same = Waterline<Profile3DOp>(doc, "again", 6, 4);
        same.RestFromOperation.Set("big");
        var tp = ToolpathGenerator.Generate(doc, same);
        Assert.Empty(Cuts(tp));
        Assert.Contains("left no rest material", Assert.Single(tp.Warnings));
    }

    [Fact]
    public void A_wrong_name_cuts_everything_with_a_warning()
    {
        var (doc, _) = Blocks();
        var small = Waterline<Profile3DOp>(doc, "rest", 2, 2);
        small.RestFromOperation.Set("no such operation");
        var tp = ToolpathGenerator.Generate(doc, small);
        Assert.Contains("there is no 3D profile named", Assert.Single(tp.Warnings));
        Assert.True(Cuts(tp).Count > 100);
    }

    [Fact]
    public void Trochoidal_roughing_takes_its_rest_from_a_waterline_rough_too_and_the_value_is_saved()
    {
        var (doc, _) = Blocks();
        var small = Waterline<TrochoRough3DOp>(doc, "troch rest", 2, 2);
        small.StepOver.Set(0.2);
        small.RestFromOperation.Set("big");
        var tp = ToolpathGenerator.Generate(doc, small);
        var cuts = Cuts(tp);
        Assert.NotEmpty(cuts);
        Assert.All(cuts, m => Assert.True(Math.Abs(m.To.X) <= 1 + 2 + 0.1, $"rest move far from the gap at {m.To}"));

        var read = CbReader.Parse(CbWriter.ToString(doc)).ActivePart.Operations.OfType<TrochoRough3DOp>().Single();
        Assert.Equal("big", read.RestFromOperation.Value);
    }
}
