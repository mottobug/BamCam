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
    public void The_same_plugin_in_two_folders_is_used_once_from_the_last_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamcam-plugins-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(dir, "bundled");
        var user = Path.Combine(dir, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        try
        {
            var name = Path.GetFileName(plugin.DllPath);
            File.Copy(plugin.DllPath, Path.Combine(bundled, name));
            File.Copy(plugin.DllPath, Path.Combine(user, name));
            var manager = new PluginManager();

            // Only the file that is used is asked about.
            manager.Discover([bundled, user], []);
            Assert.Equal(2, manager.Plugins.Count);
            var ignored = manager.Plugins[0];
            var used = manager.Plugins[1];
            Assert.Equal(PluginState.Duplicate, ignored.State);
            Assert.Equal(used.Path, ignored.DuplicateOf);
            Assert.StartsWith(user, used.Path);
            Assert.Equal(PluginState.NotApproved, used.State);

            // An older approval of the other copy does not load it.
            List<PluginApproval> approvals =
            [
                new() { File = ignored.Path, Hash = ignored.Hash, Enabled = true },
                new() { File = used.Path, Hash = used.Hash, Enabled = true },
            ];
            manager.Discover([bundled, user], approvals);
            Assert.Equal([manager.Plugins[1]], manager.Plugins.Where(p => PluginManager.IsEnabled(p, approvals)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_bundled_plugin_runs_without_asking_until_it_is_switched_off_or_changed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamcam-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var copy = Path.Combine(dir, "BamCam.SamplePlugin.dll");
            File.Copy(plugin.DllPath, copy);
            var hash = PluginManager.Inspect(copy)!.Hash;
            HashSet<string> bundled = [hash.ToLowerInvariant(), hash];
            var manager = new PluginManager();

            manager.Discover([dir], [], bundled);
            var found = Assert.Single(manager.Plugins);
            Assert.True(found.Bundled);
            Assert.NotEqual(PluginState.NotApproved, found.State);
            Assert.True(PluginManager.IsEnabled(found, []));

            // Switched off in Tools > Plugins.
            List<PluginApproval> off = [new() { File = found.Path, Hash = found.Hash, Enabled = false }];
            manager.Discover([dir], off, bundled);
            Assert.False(PluginManager.IsEnabled(manager.Plugins.Single(), off));
            Assert.NotEqual(PluginState.NotApproved, manager.Plugins.Single().State);

            // Without a matching hash it is a plugin like any other.
            manager.Discover([dir], [], new HashSet<string>());
            Assert.Equal(PluginState.NotApproved, manager.Plugins.Single().State);
            Assert.False(PluginManager.IsEnabled(manager.Plugins.Single(), []));

            // A changed file in the same place is asked about.
            File.AppendAllText(copy, " ");
            manager.Discover([dir], [], bundled);
            Assert.False(manager.Plugins.Single().Bundled);
            Assert.Equal(PluginState.NotApproved, manager.Plugins.Single().State);
            Assert.False(PluginManager.IsEnabled(manager.Plugins.Single(), []));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Plugins_with_other_file_names_are_no_duplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bamcam-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(plugin.DllPath, Path.Combine(dir, "One.dll"));
            File.Copy(plugin.DllPath, Path.Combine(dir, "Two.dll"));
            var manager = new PluginManager();

            manager.Discover([dir], []);

            Assert.All(manager.Plugins, p => Assert.Equal(PluginState.NotApproved, p.State));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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
        Assert.Equal(3, plugin.Manager.Commands.Count);
        Assert.All(plugin.Manager.Commands, c => Assert.Equal("Draw", c.Command.Menu));
        var format = Assert.Single(plugin.Manager.Importers);
        Assert.Equal([".xyz"], format.Format.Extensions);
        Assert.Single(plugin.Manager.Exporters);
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
        await Command("Polygon").Execute(context);
        var polygon = Assert.IsType<PolylineEntity>(Assert.Single(context.Added));
        Assert.True(polygon.Closed);
        Assert.Equal(6, polygon.Points.Count);
        Assert.All(polygon.Points, v => Assert.Equal(20, v.Point.XY.Length, 9));
    }

    private PluginCommand Command(string english) =>
        plugin.Manager.Commands.Select(c => c.Command).Single(c => c.Header.Replace("_", "").Contains(english, StringComparison.OrdinalIgnoreCase) ||
                                                               c.Header.Replace("_", "").Contains(english switch { "Polygon" => "Vieleck", "Gear" => "Zahnrad", _ => "Stern" }));

    [Fact]
    public async Task Plugin_object_types_are_parametric_and_survive_saving()
    {
        var context = new FakeContext([]);
        await Command("Gear").Execute(context);
        var gear = Assert.IsAssignableFrom<ParametricEntity>(Assert.Single(context.Added));
        Assert.Equal("sample.gear", gear.ParametricType);
        var doc = new CadDocument();
        doc.AddEntity(gear);
        var before = gear.Bounds().Width;
        // Change a parameter (as the property grid does) and save / read.
        gear.GetType().GetProperty("Teeth")!.SetValue(gear, 40);
        gear.Invalidate();
        Assert.True(gear.Bounds().Width > before * 1.8);
        var back = Assert.IsAssignableFrom<ParametricEntity>(Assert.Single(CbReader.Parse(CbWriter.ToString(doc)).AllEntities()));
        Assert.Equal(40, back.GetType().GetProperty("Teeth")!.GetValue(back));
        Assert.Equal(gear.Bounds().Width, back.Bounds().Width, 9);
    }

    [Fact]
    public async Task Plugin_tools_pick_points_with_preview()
    {
        var context = new FakeContext([]);
        await Command("Star").Execute(context);
        var tool = Assert.IsAssignableFrom<PluginTool>(context.Started);
        var toolContext = new FakeToolContext(context);
        tool.Started(toolContext);
        tool.Clicked(toolContext, new Point2(10, 10), ToolModifiers.None);
        toolContext.Cursor = new Point2(10, 30);
        Assert.Single(tool.Preview(toolContext));                 // rubber band star
        Assert.True(tool.KeyPressed(toolContext, "Up"));          // 6 points
        tool.Clicked(toolContext, new Point2(10, 30), ToolModifiers.None);
        var star = Assert.IsType<PolylineEntity>(Assert.Single(context.Added));
        Assert.Equal(12, star.Points.Count);
        Assert.Equal(20, Point2.Distance(new Point2(10, 10), star.Points[0].XY), 9);
        Assert.False(tool.KeyPressed(toolContext, "Escape"));     // not used: BamCam ends the tool
    }

    [Fact]
    public void Plugin_file_formats_read_and_write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-{Guid.NewGuid():N}.xyz");
        try
        {
            File.WriteAllText(path, "# test\n1 2\n3.5,4.5,-1\nnot a point\n");
            var doc = plugin.Manager.Importers.Single().Read!(path);
            var list = Assert.IsType<PointListEntity>(Assert.Single(doc.AllEntities()));
            Assert.Equal([new Point3(1, 2, 0), new Point3(3.5, 4.5, -1)], list.Points);
            plugin.Manager.Exporters.Single().Write!(doc, path);
            Assert.Equal(["# x y z", "1 2 0", "3.5 4.5 -1"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Plugin_post_macros_are_expanded()
    {
        var (doc, _) = FacingDrawing();
        var post = new BamCam.Core.Post.PostProcessorDefinition { Header = "(ops {$sample.opcount})", Footer = "" };
        var text = BamCam.Core.Post.GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath>(), post).Text;
        Assert.Contains("(ops 1)", text);
    }

    private sealed class FakeToolContext(FakeContext commands) : IToolContext
    {
        public Point2 Cursor { get; set; }
        public double PixelSize => 0.1;
        public IReadOnlyList<Entity> PickAt(Point2 point) => [];
        public void SetPrompt(string text) { }
        public void Redraw() { }
        public void End() { }
        public CadDocument Document => commands.Document;
        public IReadOnlyList<Entity> Selection => [];
        public void Change(string description, Action change) => change();
        public void AddEntities(string description, IEnumerable<Entity> entities) => commands.AddEntities(description, entities);
        public void Select(IEnumerable<Entity> entities) { }
        public void Log(string message) { }
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(defaultValue);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult<string?>(defaultValue);
        public void StartTool(PluginTool tool) { }
    }

    private sealed class FakeContext(double[] answers) : ICommandContext
    {
        private int _next;
        public PluginTool? Started { get; private set; }
        public void StartTool(PluginTool tool) => Started = tool;
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
