using System.Diagnostics;
using BamCam.Core.Model;
using BamCam.GCodeViewerPlugin;
using BamCam.Plugins;

namespace BamCam.Tests;

public class GCodeViewerPluginTests
{
    private sealed class FakeCommands(string? gcode, string name = "Part") : ICommandContext
    {
        public CadDocument Document { get; } = new() { Name = name };
        public IReadOnlyList<Entity> Selection => [];
        public List<string> Messages { get; } = [];
        public Task<string?> PostGCode() => Task.FromResult(gcode);
        public void Change(string description, Action change) => change();
        public void AddEntities(string description, IEnumerable<Entity> entities) { }
        public void Select(IEnumerable<Entity> entities) { }
        public void Log(string message) => Messages.Add(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(null);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult<string?>(null);
        public void StartTool(PluginTool tool) { }
    }

    private sealed class MinimalContext : ICommandContext
    {
        public CadDocument Document { get; } = new();
        public IReadOnlyList<Entity> Selection => [];
        public void Change(string description, Action change) { }
        public void AddEntities(string description, IEnumerable<Entity> entities) { }
        public void Select(IEnumerable<Entity> entities) { }
        public void Log(string message) { }
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(null);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult<string?>(null);
        public void StartTool(PluginTool tool) { }
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "bamcam-viewer-" + Guid.NewGuid().ToString("N"));

    private static readonly GCodeViewer Viewer = ViewerPlugin.Viewers[0];

    [Fact]
    public async Task Writes_the_file_opens_the_viewer_and_shows_the_file()
    {
        var folder = TempFolder();
        var started = new List<ProcessStartInfo>();
        var commands = new FakeCommands("G0 X1\r\n", "Flange");

        await ViewerPlugin.Open(commands, Viewer, german: true, started.Add, folder);

        var file = Path.Combine(folder, "Flange.nc");
        Assert.Equal("G0 X1\r\n", File.ReadAllText(file));
        Assert.Equal(2, started.Count);
        Assert.Contains(folder, started[0].Arguments + string.Join(" ", started[0].ArgumentList));
        Assert.Equal(Viewer.Url, started[1].FileName);
        Assert.True(started[1].UseShellExecute);
        Assert.Contains(file, Assert.Single(commands.Messages));
    }

    [Fact]
    public async Task Nothing_happens_without_gcode()
    {
        var folder = TempFolder();
        var started = new List<ProcessStartInfo>();

        await ViewerPlugin.Open(new FakeCommands(null), Viewer, german: false, started.Add, folder);

        Assert.Empty(started);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_program_that_cannot_start_gives_a_message()
    {
        var commands = new FakeCommands("G0");

        await ViewerPlugin.Open(commands, Viewer, german: false,
            _ => throw new System.ComponentModel.Win32Exception("no browser"), TempFolder());

        Assert.Contains("no browser", Assert.Single(commands.Messages));
    }

    [Theory]
    [InlineData("a/b", "a_b.nc")]
    [InlineData("", "drawing.nc")]
    [InlineData("Teil 1", "Teil 1.nc")]
    public void File_name_comes_from_the_drawing(string drawing, string expected)
    {
        var folder = TempFolder();
        Assert.Equal(Path.Combine(folder, expected), ViewerPlugin.WriteFile("x", drawing, folder));
    }

    [Fact]
    public void An_earlier_file_is_replaced()
    {
        var folder = TempFolder();
        ViewerPlugin.WriteFile("long text", "a", folder);
        var path = ViewerPlugin.WriteFile("new", "a", folder);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void The_plugin_is_recognised_and_adds_its_commands_to_the_machining_menu()
    {
        var dll = typeof(ViewerPlugin).Assembly.Location;
        var manager = new BamCam.App.Services.PluginManager();
        var info = BamCam.App.Services.PluginManager.Inspect(dll)!;
        Assert.Equal("BamCam G-Code Viewer", info.Name);
        manager.Plugins.Add(info);

        manager.Load(info, new Version(1, 0), _ => { });

        Assert.Equal(BamCam.App.Services.PluginState.Loaded, info.State);
        // The browser viewers and CAMotics.
        Assert.Equal(ViewerPlugin.Viewers.Count + 1, manager.Commands.Count);
        Assert.All(manager.Commands, c => Assert.Equal("Machining", c.Command.Menu));
        Assert.NotNull(manager.Commands[0].Command.Icon);
        // One toolbar button (the eye) with a menu of all three.
        var button = Assert.Single(BamCam.App.Services.PluginManager.ToolbarButtons(manager.Commands));
        Assert.Equal(manager.Commands.Select(c => c.Command.Header), button.Select(c => c.Command.Header));
    }

    [Fact]
    public void Toolbar_buttons_group_commands_of_one_plugin()
    {
        var a = new BamCam.App.Services.PluginInfo { Path = "a.dll", Name = "A", Vendor = "", Version = "1", Hash = "" };
        var b = new BamCam.App.Services.PluginInfo { Path = "b.dll", Name = "B", Vendor = "", Version = "1", Hash = "" };
        static BamCam.App.Services.PluginCommandEntry Entry(BamCam.App.Services.PluginInfo p, string header, string? icon, string? group) =>
            new(p, new PluginCommand { Header = header, Icon = icon, ToolbarGroup = group, Execute = _ => Task.CompletedTask });
        var commands = new[]
        {
            Entry(a, "single", "M0,0 L1,1", null),
            Entry(a, "no icon", null, null),
            Entry(a, "g1", null, "g"),
            Entry(b, "other plugin, same group name", "M0,0 L1,1", "g"),
            Entry(a, "g2", "M0,0 L1,1", "g"),
            Entry(a, "group without icon", null, "h"),
        };

        var buttons = BamCam.App.Services.PluginManager.ToolbarButtons(commands);

        Assert.Equal([["single"], ["g1", "g2"], ["other plugin, same group name"]],
            buttons.Select(x => x.Select(c => c.Command.Header).ToArray()).ToArray());
    }

    // ------------------------------------------------------------------ CAMotics

    /// <summary>A drawing with a ball nose profile (T3 Ø6), a V-carve (60°, tip 0.5) and a stock, origin at (10, 20).</summary>
    private static CadDocument CamoticsDrawing()
    {
        var doc = CadDocument.CreateNew();
        doc.Name = "Sign";
        var circle = doc.AddEntity(new CircleEntity { Center = new Core.Geometry.Point3(30, 40, 0), Diameter = 20 });
        var profile = new Core.Cam.ProfileOp { Name = "outline" };
        profile.PrimitiveIds.Add(circle.Id);
        profile.ToolNumber.Set(3);
        profile.ToolDiameter.Set(6);
        profile.ToolProfile.Set(Core.Cam.ToolProfile.BallNose);
        var vcarve = new Core.Cam.VEngraveOp { Name = "letters" };
        vcarve.PrimitiveIds.Add(circle.Id);
        vcarve.ToolNumber.Set(5);
        vcarve.VAngle.Set(60);
        vcarve.TipDiameter.Set(0.5);
        vcarve.StockSurface.Set(0);
        vcarve.TargetDepth.Set(-2);
        doc.ActivePart.Operations.Add(profile);
        doc.ActivePart.Operations.Add(vcarve);
        doc.Machining.Stock = new Core.Cam.Stock { PMin = new Core.Geometry.Point3(0, 0, -12), PMax = new Core.Geometry.Point3(100, 80, 0) };
        doc.Machining.MachiningOrigin = new Core.Cam.OptionalPoint(new Core.Geometry.Point3(10, 20, 0));
        return doc;
    }

    [Fact]
    public void The_CAMotics_project_has_the_tools_the_workpiece_and_the_gcode_file()
    {
        var json = System.Text.Json.JsonDocument.Parse(Camotics.Project(CamoticsDrawing(), Path.Combine(Path.GetTempPath(), "Sign.nc"))).RootElement;

        Assert.Equal("metric", json.GetProperty("units").GetString());
        var ball = json.GetProperty("tools").GetProperty("3");
        Assert.Equal("ballnose", ball.GetProperty("shape").GetString());
        Assert.Equal(6, ball.GetProperty("diameter").GetDouble());
        var vbit = json.GetProperty("tools").GetProperty("5");
        Assert.Equal("snubnose", vbit.GetProperty("shape").GetString());
        Assert.Equal(0.5, vbit.GetProperty("snub_diameter").GetDouble());
        // Cone of 60°: (diameter - tip) / 2 / tan 30° long.
        var d = vbit.GetProperty("diameter").GetDouble();
        Assert.Equal((d - 0.5) / 2 / Math.Tan(Math.PI / 6), vbit.GetProperty("length").GetDouble(), 5);
        // The stock in G-code coordinates: moved by the machining origin.
        var bounds = json.GetProperty("workpiece").GetProperty("bounds");
        Assert.False(json.GetProperty("workpiece").GetProperty("automatic").GetBoolean());
        Assert.Equal([-10.0, -20, -12], bounds.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray());
        Assert.Equal([90.0, 60, 0], bounds.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray());
        Assert.Equal("Sign.nc", Assert.Single(json.GetProperty("files").EnumerateArray()).GetString());
    }

    [Fact]
    public void Without_stock_CAMotics_sizes_the_workpiece_itself()
    {
        var doc = CamoticsDrawing();
        doc.Machining.Stock = new Core.Cam.Stock();

        var workpiece = System.Text.Json.JsonDocument.Parse(Camotics.Project(doc, "a.nc")).RootElement.GetProperty("workpiece");

        Assert.True(workpiece.GetProperty("automatic").GetBoolean());
        Assert.False(workpiece.TryGetProperty("bounds", out _));
    }

    [Fact]
    public async Task CAMotics_is_started_with_the_project_next_to_the_gcode()
    {
        var folder = TempFolder();
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "camotics.exe");
        File.WriteAllText(program, "");
        var pathFile = Path.Combine(folder, "camotics-path.txt");
        File.WriteAllText(pathFile, program);
        var commands = new FakeCommands("G0 X1\r\n", "Sign");
        var started = new List<ProcessStartInfo>();

        await Camotics.Open(commands, german: false, started.Add, folder, pathFile);

        var info = Assert.Single(started);
        Assert.Equal(program, info.FileName);
        var project = Assert.Single(info.ArgumentList);
        Assert.Equal(Path.Combine(folder, "Sign.camotics"), project);
        Assert.True(File.Exists(project));
        Assert.Equal("G0 X1\r\n", File.ReadAllText(Path.Combine(folder, "Sign.nc")));
        Assert.Contains("CAMotics", Assert.Single(commands.Messages));
    }

    [Fact]
    public async Task Without_CAMotics_it_asks_for_the_program_and_starts_nothing_when_cancelled()
    {
        var folder = TempFolder();
        var commands = new FakeCommands("G0");
        var started = new List<ProcessStartInfo>();
        // A remembered program that no longer exists does not count.
        Directory.CreateDirectory(folder);
        var pathFile = Path.Combine(folder, "camotics-path.txt");
        File.WriteAllText(pathFile, Path.Combine(folder, "gone.exe"));

        if (Camotics.FindProgram(pathFile) != null) return; // CAMotics installed on this computer: nothing to ask
        await Camotics.Open(commands, german: false, started.Add, folder, pathFile);

        Assert.Empty(started);
    }

    [Fact]
    public async Task Hosts_without_the_function_give_no_gcode()
    {
        ICommandContext context = new MinimalContext();
        Assert.Null(await context.PostGCode());
    }
}
