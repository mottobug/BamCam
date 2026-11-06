using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;
using CentreLines;

namespace BamCam.Tests;

/// <summary>The ported CamBam plugin of docs/porting-cambam-plugins.md.</summary>
public class CentreLinesPluginTests
{
    private static (Point2 Start, Point2 End) Ends(PolylineEntity line)
    {
        Assert.False(line.Closed);
        var path = Assert.Single(line.ToPaths());
        Assert.Equal(2, path.Count);
        return (path.StartPoint.XY, path.EndPoint.XY);
    }

    private static void AssertPoint(double x, double y, Point2 actual) =>
        Assert.True(actual.AlmostEquals(new Point2(x, y), 1e-9), $"expected {x},{y}, got {actual}");

    [Fact]
    public void Circle_gets_a_horizontal_and_a_vertical_line_through_its_centre()
    {
        var lines = CentreLinesPlugin.LinesFor(new CircleEntity { Center = new Point3(10, 20, 0), Diameter = 8 })!;

        Assert.Equal(2, lines.Count);
        AssertPoint(6, 20, Ends(lines[0]).Start);
        AssertPoint(14, 20, Ends(lines[0]).End);
        AssertPoint(10, 24, Ends(lines[1]).Start);
        AssertPoint(10, 16, Ends(lines[1]).End);
    }

    [Fact]
    public void Moved_and_scaled_circle_counts()
    {
        var circle = new CircleEntity { Center = new Point3(0, 0, 0), Diameter = 10, Transform = Matrix4.Scale(2, 2) * Matrix4.Translation(100, 0) };

        var lines = CentreLinesPlugin.LinesFor(circle)!;

        AssertPoint(90, 0, Ends(lines[0]).Start);
        AssertPoint(110, 0, Ends(lines[0]).End);
    }

    [Fact]
    public void Rectangle_gets_lines_through_the_middles_of_its_sides()
    {
        var lines = CentreLinesPlugin.LinesFor(new RectEntity { LowerLeft = new Point3(0, 0, 0), Width = 40, Height = 20 })!;

        AssertPoint(20, 0, Ends(lines[0]).Start);
        AssertPoint(20, 20, Ends(lines[0]).End);
        AssertPoint(40, 10, Ends(lines[1]).Start);
        AssertPoint(0, 10, Ends(lines[1]).End);
    }

    [Fact]
    public void Rounded_corners_do_not_matter()
    {
        var lines = CentreLinesPlugin.LinesFor(new RectEntity { Width = 40, Height = 20, CornerRadius = 5 })!;

        AssertPoint(20, 0, Ends(lines[0]).Start);
        AssertPoint(20, 20, Ends(lines[0]).End);
    }

    [Fact]
    public void Rotated_rectangle_keeps_its_own_axes()
    {
        var rectangle = new RectEntity { Width = 40, Height = 20, Transform = Matrix4.RotationZ(Math.PI / 2) };

        var lines = CentreLinesPlugin.LinesFor(rectangle)!;

        AssertPoint(0, 20, Ends(lines[0]).Start);
        AssertPoint(-20, 20, Ends(lines[0]).End);
    }

    [Fact]
    public void Other_objects_are_not_handled()
    {
        Assert.Null(CentreLinesPlugin.LinesFor(new PolylineEntity()));
    }

    private sealed class FakeCommands(CadDocument document, params Entity[] selected) : ICommandContext
    {
        public CadDocument Document { get; } = document;
        public IReadOnlyList<Entity> Selection { get; } = selected;
        public List<string> Messages { get; } = [];
        public List<string> Steps { get; } = [];
        public void Change(string description, Action change) => change();

        public void AddEntities(string description, IEnumerable<Entity> entities)
        {
            Steps.Add(description);
            foreach (var e in entities) Document.AddEntity(e);
        }

        public void Select(IEnumerable<Entity> entities) { }
        public void Log(string message) => Messages.Add(message);
        public Task<double?> AskNumber(string title, string label, double defaultValue) => Task.FromResult<double?>(null);
        public Task<string?> AskText(string title, string label, string defaultValue = "") => Task.FromResult<string?>(null);
        public void StartTool(PluginTool tool) { }
    }

    private static PluginCommand Command()
    {
        var manager = new PluginManager();
        var info = PluginManager.Inspect(typeof(CentreLinesPlugin).Assembly.Location)!;
        Assert.Equal("Centre Lines", info.Name);
        Assert.Equal("EddyCurrent", info.Vendor);
        manager.Plugins.Add(info);
        manager.Load(info, new Version(1, 0), _ => { });
        Assert.Equal(PluginState.Loaded, info.State);
        var command = Assert.Single(manager.Commands).Command;
        Assert.Equal("Draw", command.Menu);
        return command;
    }

    [Fact]
    public async Task The_command_adds_two_lines_as_one_step()
    {
        var doc = CadDocument.CreateNew();
        var circle = doc.AddEntity(new CircleEntity { Diameter = 10 });
        var commands = new FakeCommands(doc, circle);

        await Command().Execute(commands);

        Assert.Single(commands.Steps);
        Assert.Equal(3, doc.AllEntities().Count());
        Assert.Empty(commands.Messages);
    }

    [Fact]
    public async Task The_command_needs_exactly_one_circle_or_rectangle()
    {
        var doc = CadDocument.CreateNew();
        var a = doc.AddEntity(new CircleEntity { Diameter = 10 });
        var b = doc.AddEntity(new CircleEntity { Diameter = 20 });
        var other = doc.AddEntity(new PolylineEntity());

        foreach (var selected in new[] { Array.Empty<Entity>(), [a, b], [other] })
        {
            var commands = new FakeCommands(doc, selected);
            await Command().Execute(commands);
            Assert.Single(commands.Messages);
            Assert.Empty(commands.Steps);
        }
        Assert.Equal(3, doc.AllEntities().Count());
    }
}
