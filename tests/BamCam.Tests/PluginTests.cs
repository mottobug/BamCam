using BamCam.App.Services;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;
using BamCam.Plugins;

namespace BamCam.Tests;

/// <summary>Loads the sample plugin once (like a third-party plugin from a folder) for all tests of the class.</summary>
public sealed class SamplePluginFixture
{
    public string DllPath { get; }
    public PluginManager Manager { get; } = new();
    public PluginInfo Info { get; }
    public List<string> Log { get; } = [];

    public SamplePluginFixture()
    {
        var sep = Path.DirectorySeparatorChar;
        DllPath = Path.Combine(AppContext.BaseDirectory.Replace($"tests{sep}BamCam.Tests", $"samples{sep}BamCam.SamplePlugin"), "BamCam.SamplePlugin.dll");
        Info = PluginManager.Inspect(DllPath) ?? throw new InvalidOperationException("sample plugin not found: " + DllPath);
        Manager.Plugins.Add(Info);
        Manager.Load(Info, new Version(1, 0), Log.Add);
    }
}

public class PluginTests(SamplePluginFixture plugin) : IClassFixture<SamplePluginFixture>
{
    [Fact]
    public void Plugins_are_recognised_from_metadata_only()
    {
        Assert.Equal("BamCam Sample Plugin", plugin.Info.Name);
        Assert.Equal("BamCam project", plugin.Info.Vendor);
        Assert.Equal("1.0.0", plugin.Info.Version);
        // BamCam's own assemblies and other DLLs are no plugins.
        Assert.Null(PluginManager.Inspect(typeof(MachineOp).Assembly.Location));
        Assert.Null(PluginManager.Inspect(typeof(IBamCamPlugin).Assembly.Location));
    }

    [Fact]
    public void An_approval_is_valid_for_exactly_this_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamcam-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sample"));
        try
        {
            var copy = Path.Combine(dir, "sample", "BamCam.SamplePlugin.dll");
            File.Copy(plugin.DllPath, copy);
            var manager = new PluginManager();

            manager.Discover([dir], []);
            var found = Assert.Single(manager.Plugins);
            Assert.Equal(PluginState.NotApproved, found.State);

            List<PluginApproval> approvals = [new() { File = found.Path, Hash = found.Hash, Enabled = true }];
            manager.Discover([dir], approvals);
            Assert.True(PluginManager.IsEnabled(manager.Plugins.Single(), approvals));

            // A changed file needs a new approval.
            File.AppendAllText(copy, " ");
            manager.Discover([dir], approvals);
            Assert.Equal(PluginState.NotApproved, manager.Plugins.Single().State);
            Assert.False(PluginManager.IsEnabled(manager.Plugins.Single(), approvals));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loading_registers_commands_and_operations()
    {
        Assert.Equal(PluginState.Loaded, plugin.Info.State);
        var command = Assert.Single(plugin.Manager.Commands);
        Assert.Equal("Draw", command.Command.Menu);
        var operation = Assert.Single(plugin.Manager.Operations);
        Assert.Equal("MOPFacingSample", operation.XmlName);
        var op = MachineOpFactory.Create("MOPFacingSample");
        Assert.NotNull(op);
        Assert.Equal("FacingOp", op!.GetType().Name);
        Assert.IsAssignableFrom<DepthOp>(op);
    }

    private static (CadDocument Doc, MachineOp Op) FacingDrawing()
    {
        var doc = new CadDocument();
        var rect = new RectEntity { Width = 20, Height = 10 };
        doc.AddEntity(rect);
        var op = (DepthOp)MachineOpFactory.Create("MOPFacingSample")!;
        op.Name = "face";
        op.PrimitiveIds.Add(rect.Id);
        op.ToolDiameter.Set(4);
        op.StockSurface.Set(0);
        op.TargetDepth.Set(-1);
        op.DepthIncrement.Set(0.5);
        doc.ActivePart.Operations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void Plugin_operations_generate_toolpaths()
    {
        var (doc, op) = FacingDrawing();
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);
        var cuts = tp.Moves.Where(m => m.Kind == MoveKind.Feed).Select(m => m.To).ToList();
        Assert.Equal(new[] { -0.5, -1.0 }, cuts.Select(p => p.Z).Distinct().OrderDescending());
        Assert.True(cuts.Min(p => p.X) < -2 && cuts.Max(p => p.X) > 22, "rows run over the whole width");
        Assert.Equal(0, cuts.Min(p => p.Y), 9);
        Assert.Equal(10, cuts.Max(p => p.Y), 9);
    }

    [Fact]
    public void Plugin_operations_are_saved_like_CamBam_plugin_operations()
    {
        var (doc, op) = FacingDrawing();
        op.FindParameter("StepOver")!.BoxedValue = 0.5;
        op.FindParameter("StepOver")!.State = ParamState.Value;
        var xml = CbWriter.ToString(doc);
        Assert.Contains("<mop xsi:type=\"MOPFacingSample\">", xml);
        var again = CbReader.Parse(xml).Parts.SelectMany(p => p.Operations).Single();
        Assert.Equal("FacingOp", again.GetType().Name);
        Assert.Equal(0.5, again.FindParameter("StepOver")!.BoxedValue);
        Assert.Equal(-1.0, ((DepthOp)again).TargetDepth.Value);
    }

    [Fact]
    public async Task Plugin_commands_change_the_drawing_through_the_context()
    {
        var context = new FakeContext([6, 20]);
        await plugin.Manager.Commands.Single().Command.Execute(context);
        var polygon = Assert.IsType<PolylineEntity>(Assert.Single(context.Added));
        Assert.True(polygon.Closed);
        Assert.Equal(6, polygon.Points.Count);
        Assert.All(polygon.Points, v => Assert.Equal(20, v.Point.XY.Length, 9));
    }

    private sealed class FakeContext(double[] answers) : ICommandContext
    {
        private int _next;
        public List<Entity> Added { get; } = [];
        public CadDocument Document { get; } = new();
        public IReadOnlyList<Entity> Selection => [];
        public void Change(string description, Action change) => change();
        public void AddEntities(string description, IEnumerable<Entity> entities) => Added.AddRange(entities);
        public void Select(IEnumerable<Entity> entities) { }
        public void Log(string message) { }
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(answers[_next++]);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult<string?>(defaultValue);
    }
}
