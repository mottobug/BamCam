using Avalonia;
using Avalonia.Input;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Drawing tools driven without a window: clicks and keys go straight to the tool.</summary>
public class DrawToolTests
{
    private sealed class Host : IToolHost
    {
        public DocumentSession Session { get; } = new(new CadDocument());
        public ViewTransform View { get; } = new();
        public string? Prompt { get; private set; }
        public Point2 Snap(Point2 world, Point screen) => world;
        public void Invalidate() { }
        public void SetPrompt(string? text) => Prompt = text;
        public void EndTool() { }
        public IReadOnlyList<Entity> HitTest(Point screen, double pixelTolerance = 5) => [];
        public IReadOnlyList<Entity> WindowSelect(Point a, Point b, bool crossing) => [];
    }

    private static readonly PointerPointProperties Left = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);

    private static (Host Host, T Tool) Start<T>(T tool) where T : ViewTool
    {
        var host = new Host();
        tool.Attach(host);
        return (host, tool);
    }

    private static void Click(ViewTool tool, double x, double y)
    {
        tool.PointerMoved(default, new Point2(x, y), KeyModifiers.None);
        tool.PointerPressed(default, new Point2(x, y), Left, KeyModifiers.None);
    }

    private static void Type(ViewTool tool, string text)
    {
        foreach (var c in text)
        {
            var key = c switch
            {
                >= '0' and <= '9' => Key.D0 + (c - '0'),
                '.' => Key.OemPeriod,
                ',' => Key.OemComma,
                'x' => Key.X,
                _ => throw new ArgumentException(c.ToString()),
            };
            Assert.True(tool.KeyDown(key, KeyModifiers.None), $"key {c}");
        }
    }

    [Fact]
    public void Polyline_segment_gets_the_typed_length_towards_the_cursor()
    {
        var (host, tool) = Start(new PolylineTool());
        Click(tool, 0, 0);
        Type(tool, "25");
        Assert.Contains("25", host.Prompt);
        Click(tool, 3, 4); // direction (0.6, 0.8): the point lies at (15, 20)
        Type(tool, "12,5");
        tool.PointerMoved(default, new Point2(15, 30), KeyModifiers.None);
        tool.KeyDown(Key.Enter, KeyModifiers.None); // Enter with a value places the point straight up
        tool.KeyDown(Key.Enter, KeyModifiers.None); // and without one finishes
        var poly = Assert.IsType<PolylineEntity>(Assert.Single(host.Session.Document.AllEntities()));
        var pts = poly.ToPaths().First().Vertices.Select(v => v.XY).ToList();
        Assert.Equal(3, pts.Count);
        Assert.True(pts[1].AlmostEquals(new(15, 20)), pts[1].ToString());
        Assert.True(pts[2].AlmostEquals(new(15, 32.5)), pts[2].ToString());
    }

    [Fact]
    public void Backspace_corrects_the_typed_value_before_removing_points()
    {
        var (host, tool) = Start(new PolylineTool());
        Click(tool, 0, 0);
        Type(tool, "107");
        tool.KeyDown(Key.Back, KeyModifiers.None); // "10"
        Click(tool, 1, 0);
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        var poly = Assert.IsType<PolylineEntity>(Assert.Single(host.Session.Document.AllEntities()));
        Assert.True(poly.ToPaths().First().Vertices[1].XY.AlmostEquals(new(10, 0)));
    }

    [Fact]
    public void Circle_with_typed_diameter()
    {
        var (host, tool) = Start(new CircleTool());
        Click(tool, 5, 5);
        Type(tool, "8");
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        var circle = Assert.IsType<CircleEntity>(Assert.Single(host.Session.Document.AllEntities()));
        Assert.Equal(8, circle.Diameter, 9);
        Assert.Equal(new Point3(5, 5, 0), circle.Center);
    }

    [Fact]
    public void Rectangle_with_typed_width_and_height_towards_the_cursor()
    {
        var (host, tool) = Start(new RectangleTool());
        Click(tool, 10, 10);
        Type(tool, "40x20");
        tool.PointerMoved(default, new Point2(0, 0), KeyModifiers.None); // down and to the left
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        var rect = Assert.Single(host.Session.Document.AllEntities());
        var box = rect.Bounds();
        Assert.Equal((-30.0, -10.0, 10.0, 10.0), (box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
    }
}
