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
        /// <summary>Objects returned by the next hit test.</summary>
        public List<Entity> Hits { get; } = [];
        public IReadOnlyList<Entity> HitTest(Point screen, double pixelTolerance = 5) => Hits;
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
                '<' => Key.OemBackslash,
                '-' => Key.OemMinus,
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

    private static List<Point2> Points(Host host) =>
        Assert.IsType<PolylineEntity>(Assert.Single(host.Session.Document.AllEntities())).ToPaths().First().Vertices.Select(v => v.XY).ToList();

    [Fact]
    public void Length_and_angle_place_the_point_by_keyboard()
    {
        var (host, tool) = Start(new PolylineTool());
        Click(tool, 0, 0);
        Type(tool, "25<30");
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        Type(tool, "10<-90");
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        var pts = Points(host);
        var a = Point2.FromAngle(Math.PI / 6, 25);
        Assert.True(pts[1].AlmostEquals(a), pts[1].ToString());
        Assert.True(pts[2].AlmostEquals(a + new Point2(0, -10)), pts[2].ToString());
    }

    [Fact]
    public void Angle_alone_takes_the_length_from_the_cursor()
    {
        var (host, tool) = Start(new PolylineTool());
        Click(tool, 0, 0);
        Type(tool, "<90");
        Click(tool, 3, 7); // projected onto the vertical: (0, 7)
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        Assert.True(Points(host)[1].AlmostEquals(new(0, 7)), Points(host)[1].ToString());
    }

    [Fact]
    public void Shift_snaps_the_direction_to_15_degree_steps()
    {
        var (host, tool) = Start(new PolylineTool());
        Click(tool, 0, 0);
        // 14° with Shift: snaps to 15°, length from the cursor projected onto that direction.
        var cursor = Point2.FromAngle(Angles.ToRadians(14), 20);
        tool.PointerMoved(default, cursor, KeyModifiers.Shift);
        tool.PointerPressed(default, cursor, Left, KeyModifiers.Shift);
        tool.KeyDown(Key.Enter, KeyModifiers.None);
        var p = Points(host)[1];
        Assert.Equal(15, Angles.ToDegrees(p.Angle), 9);
        Assert.Equal(20 * Math.Cos(Angles.ToRadians(1)), p.Length, 9);
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
    public void Corner_tool_uses_the_value_typed_just_before_the_click()
    {
        var host = new Host();
        var square = new PolylineEntity(Path2.FromPoints([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], closed: true));
        host.Session.Document.AddEntity(square);
        BamCam.App.Drawing.CornerTool.Kind = BamCam.Core.Cad.CornerKind.Fillet;
        BamCam.App.Drawing.CornerTool.FilletRadius = 1;
        var tool = new BamCam.App.Drawing.CornerTool(square);
        tool.Attach(host);
        Type(tool, "3");
        var corner = host.View.ToScreen(new Point2(10, 0));
        tool.PointerMoved(corner, new Point2(10, 0), KeyModifiers.None);
        tool.PointerPressed(corner, new Point2(10, 0), Left, KeyModifiers.None);
        var arc = Assert.Single(square.ToPaths().First().Segments(), s => s.IsArc).ToArc();
        Assert.Equal(3, arc.Radius, 9);
    }

    [Fact]
    public void Trim_extend_with_all_objects_as_boundaries_after_Enter()
    {
        var host = new Host();
        var doc = host.Session.Document;
        doc.AddEntity(RectEntity.FromCorners(new Point2(0, 0), new Point2(50, 30), 0));
        var line = new LineEntity();
        line.Points.AddRange([new Point3(10, 15, 0), new Point3(20, 15, 0)]);
        doc.AddEntity(line);
        var tool = new TrimExtendTool();
        tool.Attach(host);
        tool.KeyDown(Key.Enter, KeyModifiers.None); // nothing picked: every object is a boundary
        host.Hits.Add(line);
        var end = host.View.ToScreen(new Point2(19, 15));
        tool.PointerPressed(end, new Point2(19, 15), Left, KeyModifiers.None);
        Assert.Equal(new Point3(50, 15, 0), line.Points[^1]);
        // A click near the other end extends it to the left side of the rectangle.
        var start = host.View.ToScreen(new Point2(11, 15));
        tool.PointerPressed(start, new Point2(11, 15), Left, KeyModifiers.None);
        Assert.Equal(new Point3(0, 15, 0), line.Points[0]);
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
