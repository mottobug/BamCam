using Avalonia;
using Avalonia.Input;
using BamCam.App.Drawing;
using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

/// <summary>Issue 21: Shift + left drag on a selected object moves the selection, as in CamBam.</summary>
public class SelectMoveTests
{
    private sealed class Host : IToolHost
    {
        public DocumentSession Session { get; } = new(new CadDocument());
        public ViewTransform View { get; } = new();
        public Point2 Snap(Point2 world, Point screen) => world;
        public void Invalidate() { }
        public void SetPrompt(string? text) { }
        public void EndTool() { }
        public List<Entity> Hits { get; } = [];
        public IReadOnlyList<Entity> HitTest(Point screen, double pixelTolerance = 5) => Hits;
        public IReadOnlyList<Entity> WindowSelect(Point a, Point b, bool crossing) => [];
    }

    private static readonly PointerPointProperties Left = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);

    /// <summary>Two circles, both selected; the hit test finds the first one.</summary>
    private static (Host Host, SelectTool Tool, CircleEntity A, CircleEntity B) Setup()
    {
        var host = new Host();
        var a = (CircleEntity)host.Session.Document.AddEntity(new CircleEntity { Center = new Point3(10, 10, 0), Diameter = 4 });
        var b = (CircleEntity)host.Session.Document.AddEntity(new CircleEntity { Center = new Point3(30, 10, 0), Diameter = 4 });
        host.Session.Select([a, b]);
        host.Hits.Add(a);
        var tool = new SelectTool();
        tool.Attach(host);
        return (host, tool, a, b);
    }

    private static void Drag(SelectTool tool, Point2 from, Point2 to, KeyModifiers mods)
    {
        tool.PointerPressed(new Point(0, 0), from, Left, mods);
        tool.PointerMoved(new Point(50, 0), to, mods);
        tool.PointerReleased(new Point(50, 0), to, MouseButton.Left, mods);
    }

    [Fact]
    public void Shift_drag_on_a_selected_object_moves_the_whole_selection()
    {
        var (host, tool, a, b) = Setup();

        Drag(tool, new Point2(12, 10), new Point2(17, 3), KeyModifiers.Shift);

        Assert.True(Point3.Distance(a.Bounds().Center, new Point3(15, 3, 0)) < 1e-9, $"{a.Bounds().Center}");
        Assert.True(Point3.Distance(b.Bounds().Center, new Point3(35, 3, 0)) < 1e-9, $"{b.Bounds().Center}");
        Assert.Equal(2, host.Session.Selection.Count);
    }

    [Fact]
    public void Without_shift_or_off_the_selection_nothing_moves()
    {
        var (host, tool, a, _) = Setup();
        Drag(tool, new Point2(12, 10), new Point2(17, 3), KeyModifiers.None);
        Assert.True(Point3.Distance(a.Bounds().Center, new Point3(10, 10, 0)) < 1e-9);

        // Shift on an object that is not selected: window selection as before, no move.
        host.Session.ClearSelection();
        Drag(tool, new Point2(12, 10), new Point2(17, 3), KeyModifiers.Shift);
        Assert.True(Point3.Distance(a.Bounds().Center, new Point3(10, 10, 0)) < 1e-9);
    }

    [Fact]
    public void Escape_while_dragging_leaves_the_selection_where_it_was()
    {
        var (_, tool, a, _) = Setup();
        tool.PointerPressed(new Point(0, 0), new Point2(12, 10), Left, KeyModifiers.Shift);
        tool.PointerMoved(new Point(50, 0), new Point2(17, 3), KeyModifiers.Shift);
        Assert.True(tool.KeyDown(Key.Escape, KeyModifiers.None));
        tool.PointerReleased(new Point(50, 0), new Point2(17, 3), MouseButton.Left, KeyModifiers.Shift);

        Assert.True(Point3.Distance(a.Bounds().Center, new Point3(10, 10, 0)) < 1e-9);
    }
}
