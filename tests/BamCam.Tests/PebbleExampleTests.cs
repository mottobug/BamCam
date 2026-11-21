using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Simulation;
using BamCam.IO.Cb;
using MatmillPlugin;

namespace BamCam.Tests;

/// <summary>
/// The example drawing <c>examples_to_test/3D profile (pebble).cb</c>: a pebble computed here (no third-party model),
/// machined with every kind of 3D profile: trochoidal and waterline roughing, waterline and scanline finishing, the
/// back face with the part turned over and the mould of it. Set BAMCAM_WRITE_EXAMPLES=1 to write the file again.
/// </summary>
[Collection(GlobalLibrariesCollection.Name)]
public class PebbleExampleTests
{
    private static readonly string File3D = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test/3D profile (pebble).cb"));

    // Half axes of the outline and the thickness above and below the middle (Z = 0).
    private const double A = 40, B = 26, H = 12;

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

    public PebbleExampleTests() => new Plugin().Initialize(new Host());

    /// <summary>Height of the top (a thumb hollow left of the middle) and of the underside (a flat foot right of it).</summary>
    private static double TopZ(double x, double y, double h) => h - 4 * Math.Exp(-((x + 12) * (x + 12) + y * y) / (2 * 9 * 9)) * h / H;
    private static double UnderZ(double x, double y, double h) => -h + 3 * Math.Exp(-((x - 14) * (x - 14) + y * y) / (2 * 8 * 8)) * h / H;

    /// <summary>
    /// The closed pebble: rings from the middle to the rim, denser at the rim where it is steep; top and underside meet
    /// at the rim at Z = 0. Faces point outwards.
    /// </summary>
    public static SurfaceEntity Pebble(int rings = 32, int segments = 112)
    {
        var s = new SurfaceEntity();
        double Thickness(double u) => H * Math.Sqrt(Math.Max(0, 1 - u * u));
        // Ring 0 is the middle (one point), ring `rings` the rim, shared by top and underside.
        var top = new List<int[]>();
        var under = new List<int[]>();
        for (var i = 0; i <= rings; i++)
        {
            var u = Math.Sin(Math.PI / 2 * i / rings);
            var h = Thickness(u);
            var count = i == 0 ? 1 : segments;
            var t = new int[count];
            var b = new int[count];
            for (var j = 0; j < count; j++)
            {
                var v = 2 * Math.PI * j / segments;
                var (x, y) = (A * u * Math.Cos(v), B * u * Math.Sin(v));
                t[j] = s.Vertices.Count;
                s.Vertices.Add(new Point3(x, y, i == rings ? 0 : TopZ(x, y, h)));
                if (i == rings) b[j] = t[j];
                else
                {
                    b[j] = s.Vertices.Count;
                    s.Vertices.Add(new Point3(x, y, UnderZ(x, y, h)));
                }
            }
            top.Add(t);
            under.Add(b);
        }
        for (var j = 0; j < segments; j++)
        {
            var k = (j + 1) % segments;
            s.Faces.AddRange([top[0][0], top[1][j], top[1][k]]);
            s.Faces.AddRange([under[0][0], under[1][k], under[1][j]]);
        }
        for (var i = 1; i < rings; i++)
        for (var j = 0; j < segments; j++)
        {
            var k = (j + 1) % segments;
            s.Faces.AddRange([top[i][j], top[i + 1][j], top[i + 1][k]]);
            s.Faces.AddRange([top[i][j], top[i + 1][k], top[i][k]]);
            s.Faces.AddRange([under[i][j], under[i + 1][k], under[i + 1][j]]);
            s.Faces.AddRange([under[i][j], under[i][k], under[i + 1][k]]);
        }
        return s;
    }

    private static T Op<T>(string name, int surface, double tool, ToolProfile profile) where T : Profile3DOp, new()
    {
        var op = new T { Name = name };
        op.PrimitiveIds.Add(surface);
        op.ToolDiameter.Set(tool);
        op.ToolProfile.Set(profile);
        op.ClearancePlane.Set(20);
        op.StockSurface.Set(13);
        op.TargetDepth.Set(0);
        op.BoundaryMethod.Set(BoundaryMethod.BoundingBox);
        op.BoundaryMargin.Set(tool);
        op.SpindleSpeed.Set(18000);
        op.CutFeedrate.Set(1500);
        op.PlungeFeedrate.Set(300);
        return op;
    }

    private static TrochoRough3DOp Trochoidal(string name, int surface)
    {
        var op = Op<TrochoRough3DOp>(name, surface, 6, ToolProfile.EndMill);
        op.DepthIncrement.Set(4);
        // Typed, so that a CAM style cannot change them (a style's step over for milling would be far too wide).
        op.StepOver.Set(0.2);
        op.MinStepover.Set(0.9);
        op.MillingDirection.Set(MillingDirection.Climb);
        op.RoughingClearance.Set(0.5);
        op.ChordFeedrate.Set(3000);
        return op;
    }

    private static Profile3DOp Finish(string name, int surface, Profile3DMethod method)
    {
        var op = Op<Profile3DOp>(name, surface, 3, ToolProfile.BallNose);
        op.Profile3DMethod.Set(method);
        op.RoughingFinishing.Set(RoughingFinishing.Finishing);
        op.StepOver.Set(0.15);
        op.Resolution.Set(0.1);
        op.DepthIncrement.Set(0.5);
        op.CutFeedrate.Set(1200);
        return op;
    }

    private static Profile3DOp Turned(Profile3DOp op)
    {
        // Turned over about X, the middle of the stock (Z = 0) staying the middle: the stock is again 13 above it.
        op.BackFace.Set(true);
        op.BackFaceZeroZ.Set(0);
        op.FlipAxis.Set(FlipAxis.X);
        return op;
    }

    private static Profile3DOp AsMold(Profile3DOp op)
    {
        op.Mold.Set(true);
        return op;
    }

    /// <summary>The example drawing.</summary>
    public static CadDocument Build()
    {
        var doc = CadDocument.CreateNew();
        doc.Name = "3D profile (pebble)";
        var pebble = doc.AddEntity(Pebble()).Id;
        var stock = new Stock { PMin = new Point3(-50, -36, -13), PMax = new Point3(50, 36, 13), Material = "Wood" };
        doc.Parts.Clear();

        var front = new Part { Name = "Front", Stock = stock.Clone() };
        front.Operations.Add(Trochoidal("Front trochoidal rough", pebble));
        var waterline = Op<Profile3DOp>("Front waterline rough (to compare, off)", pebble, 6, ToolProfile.EndMill);
        waterline.Profile3DMethod.Set(Profile3DMethod.WaterlineRough);
        waterline.DepthIncrement.Set(4);
        waterline.StepOver.Set(0.4);
        waterline.RoughingClearance.Set(0.5);
        waterline.Enabled = false;
        front.Operations.Add(waterline);
        front.Operations.Add(Finish("Front waterline finish", pebble, Profile3DMethod.WaterlineFinish));
        front.Operations.Add(Finish("Front scanline finish", pebble, Profile3DMethod.Horizontal));

        // The underside with the part turned over (Back Face): the 3D simulation shows both sides on one stock.
        var back = new Part { Name = "Back (turned over)", Stock = stock.Clone() };
        back.Operations.Add(Turned(Trochoidal("Back trochoidal rough", pebble)));
        back.Operations.Add(Turned(Finish("Back scanline finish", pebble, Profile3DMethod.Vertical)));

        // The mould of the pebble: the model upside down within its height, cut as a cavity into a block of its own. Off,
        // so that simulating the drawing shows front and back on one stock; select the part to simulate it alone.
        var mold = new Part { Name = "Mould (own block, off)", Stock = stock.Clone(), Enabled = false };
        mold.Operations.Add(AsMold(Trochoidal("Mould trochoidal rough", pebble)));
        var angled = AsMold(Finish("Mould scanline finish, rows at 30°", pebble, Profile3DMethod.Horizontal));
        angled.ScanAngle.Set(30);
        mold.Operations.Add(angled);

        foreach (var part in new[] { front, back, mold }) doc.Parts.Add(part);
        doc.ActivePartName = front.Name;
        return doc;
    }

    [Fact]
    public void The_example_drawing_cuts_with_every_operation()
    {
        if (Environment.GetEnvironmentVariable("BAMCAM_WRITE_EXAMPLES") == "1") CbWriter.Save(Build(), File3D);
        Assert.True(File.Exists(File3D), File3D);
        var doc = CbReader.Load(File3D);
        var ops = doc.Parts.SelectMany(p => p.Operations).ToList();
        Assert.Equal(8, ops.Count);
        foreach (var op in ops.Where(o => o.Enabled))
        {
            var tp = ToolpathGenerator.Generate(doc, op);
            Assert.True(tp.Warnings.Count == 0, $"{op.Name}: {string.Join(" | ", tp.Warnings)}");
            Assert.True(tp.Moves.Count > 100, $"{op.Name}: {tp.Moves.Count} moves");
        }
    }

    [Fact]
    public void Front_and_back_are_simulated_on_one_stock_cut_from_both_sides()
    {
        var doc = Build();
        var jobs = doc.Parts.Take(2).SelectMany(p => p.Operations).Where(o => o.Enabled && o is TrochoRough3DOp)
            .Select(o => (Op: (MachineOp)o, Toolpath: ToolpathGenerator.Generate(doc, o))).ToList();
        Assert.Equal(2, jobs.Count);
        var f = MillingSimulation.Create(doc, jobs, 0.5);
        Assert.Equal(13, f.Top, 6);
        Assert.Equal(-13, f.Bottom, 6);
        var moves = MillingSimulation.Moves(doc, jobs, 0.05);
        Assert.Contains(moves, m => m.FromBelow);
        foreach (var m in moves) MillingSimulation.Cut(f, m);

        int Cell(double x, double y) => (int)Math.Floor((y - f.Y0) / f.Cell) * f.Nx + (int)Math.Floor((x - f.X0) / f.Cell);
        // On the slope of the pebble both sides are cut in steps of the levels, down to it but never into it.
        var slope = Cell(30.25, 0.25);
        var h = H * Math.Sqrt(1 - Math.Pow(30.25 / A, 2));
        Assert.InRange(f.Z[slope], TopZ(30.25, 0.25, h) - 0.05, 13 - 1);
        Assert.InRange(f.Low[slope], -13 + 1, UnderZ(30.25, 0.25, h) + 0.05);
        // Beside it both sides reach the middle of the stock: cut through, top and underside meet at Z = 0.
        var beside = Cell(A + 5, 0.25);
        Assert.InRange(f.Z[beside], -0.01, 0.01);
        Assert.InRange(f.Low[beside], -0.01, 0.01);
        // The back face never cut into the top: wherever the underside was raised, the top still lies above it.
        for (var k = 0; k < f.Z.Length; k++) Assert.True(f.Low[k] <= f.Z[k] + 0.01, $"cell {k}: underside {f.Low[k]} above top {f.Z[k]}");
        // The mesh of the material has the cut underside.
        var mesh = MillingSimulation.ToSurface(f, 200_000);
        Assert.True(mesh.Vertices.Min(v => v.Z) < -1 && mesh.Vertices.Where(v => Math.Abs(v.X - (A + 5)) < 1 && Math.Abs(v.Y) < 1).All(v => Math.Abs(v.Z) < 0.02));
    }
}
