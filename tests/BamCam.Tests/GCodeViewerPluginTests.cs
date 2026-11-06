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
        Assert.Equal(ViewerPlugin.Viewers.Count, manager.Commands.Count);
        Assert.All(manager.Commands, c => Assert.Equal("Machining", c.Command.Menu));
        Assert.NotNull(manager.Commands[0].Command.Icon);
    }

    [Fact]
    public async Task Hosts_without_the_function_give_no_gcode()
    {
        ICommandContext context = new MinimalContext();
        Assert.Null(await context.PostGCode());
    }
}
