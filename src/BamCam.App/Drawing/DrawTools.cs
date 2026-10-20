using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>Common behaviour of drawing commands: Esc cancels, optional repeat after finishing.</summary>
public abstract class DrawTool : ViewTool
{
    protected Point2 Cursor;

    /// <summary>Restart the command after an entity is finished (config "Repeat Commands").</summary>
    public static bool RepeatCommands { get; set; } = true;

    protected abstract void Reset();
    protected abstract bool HasPendingInput { get; }

    protected void Commit(string description, Entity entity)
    {
        var session = Host.Session;
        session.Execute(description, () => session.Document.AddEntity(entity));
        session.Select([entity]);
        Reset();
        if (!RepeatCommands) Host.EndTool();
        else Host.SetPrompt(Prompt);
        Host.Invalidate();
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        Cursor = world;
        if (HasPendingInput) Host.Invalidate();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Escape) return false;
        if (HasPendingInput)
        {
            Reset();
            Host.SetPrompt(Prompt);
            Host.Invalidate();
        }
        else
        {
            Host.EndTool();
        }
        return true;
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        // Middle click with nothing in progress ends the command.
        if (button == MouseButton.Middle && !HasPendingInput) Host.EndTool();
    }
}

/// <summary>Polyline / line tool: click vertices, Enter or middle click finishes, C closes.</summary>
public sealed class PolylineTool(bool linesOnly = false) : DrawTool
{
    private readonly List<Point2> _pts = [];

    public override string Prompt => _pts.Count == 0
        ? (linesOnly ? "Line: select first point" : "Polyline: select first point")
        : "Select next point  [Enter / middle click = finish, C = close, Esc = cancel]";

    protected override bool HasPendingInput => _pts.Count > 0;
    protected override void Reset() => _pts.Clear();

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_pts.Count >= 2 && world.AlmostEquals(_pts[0], Tolerance.Point))
        {
            Finish(closed: true);
            return;
        }
        if (_pts.Count > 0 && world.AlmostEquals(_pts[^1], Tolerance.Point)) return;
        _pts.Add(world);
        Host.SetPrompt(Prompt);
        Host.Invalidate();
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle && HasPendingInput) Finish(false);
        else base.PointerReleased(screen, world, button, mods);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key is Key.Enter or Key.Return && HasPendingInput)
        {
            Finish(false);
            return true;
        }
        if (key == Key.C && _pts.Count >= 3)
        {
            Finish(true);
            return true;
        }
        if (key == Key.Back && _pts.Count > 0)
        {
            _pts.RemoveAt(_pts.Count - 1);
            Host.Invalidate();
            return true;
        }
        return base.KeyDown(key, mods);
    }

    private void Finish(bool closed)
    {
        if (_pts.Count < 2)
        {
            Reset();
            return;
        }
        var path = Path2.FromPoints(_pts, closed);
        Entity e;
        if (linesOnly && !closed)
        {
            var line = new LineEntity();
            line.Points.AddRange(_pts.Select(p => p.ToPoint3()));
            e = line;
        }
        else
        {
            e = new PolylineEntity(path);
        }
        Commit(linesOnly ? "Draw line" : "Draw polyline", e);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_pts.Count == 0) return;
        DrawPath(dc, view, Path2.FromPoints(_pts.Append(Cursor), false), PreviewPen);
    }
}

public sealed class CircleTool : DrawTool
{
    private Point2? _center;

    public override string Prompt => _center == null ? "Circle: select center point" : "Select radius";
    protected override bool HasPendingInput => _center != null;
    protected override void Reset() => _center = null;

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_center is not { } c)
        {
            _center = world;
            Host.SetPrompt(Prompt);
            return;
        }
        var r = Point2.Distance(c, world);
        if (r < Tolerance.Point) return;
        Commit("Draw circle", new CircleEntity { Center = c.ToPoint3(), Diameter = 2 * r });
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_center is not { } c) return;
        var r = Point2.Distance(c, Cursor);
        if (r < Tolerance.Point) return;
        DrawPath(dc, view, new CircleEntity { Center = c.ToPoint3(), Diameter = 2 * r }.ToPaths().First(), PreviewPen);
        dc.DrawLine(PreviewPen, view.ToScreen(c), view.ToScreen(Cursor));
    }
}

public sealed class RectangleTool : DrawTool
{
    private Point2? _corner;

    public override string Prompt => _corner == null ? "Rectangle: select first corner" : "Select opposite corner";
    protected override bool HasPendingInput => _corner != null;
    protected override void Reset() => _corner = null;

    public static double CornerRadius { get; set; }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_corner is not { } a)
        {
            _corner = world;
            Host.SetPrompt(Prompt);
            return;
        }
        if (Math.Abs(a.X - world.X) < Tolerance.Point || Math.Abs(a.Y - world.Y) < Tolerance.Point) return;
        Commit("Draw rectangle", RectEntity.FromCorners(a, world, CornerRadius));
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_corner is not { } a) return;
        DrawPath(dc, view, RectEntity.FromCorners(a, Cursor, CornerRadius).ToPaths().First(), PreviewPen);
    }
}

/// <summary>Arc by start point, end point and a third point defining the bulge.</summary>
public sealed class ArcTool : DrawTool
{
    private readonly List<Point2> _pts = [];

    public override string Prompt => _pts.Count switch
    {
        0 => "Arc: select start point",
        1 => "Select end point",
        _ => "Select a point on the arc",
    };

    protected override bool HasPendingInput => _pts.Count > 0;
    protected override void Reset() => _pts.Clear();

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_pts.Count < 2)
        {
            if (_pts.Count == 1 && world.AlmostEquals(_pts[0], Tolerance.Point)) return;
            _pts.Add(world);
            Host.SetPrompt(Prompt);
            return;
        }
        if (Build(world) is { } arc) Commit("Draw arc", arc);
    }

    private ArcEntity? Build(Point2 through)
    {
        var a = Arc2.FromThreePoints(_pts[0], through, _pts[1]);
        if (a is not { } arc) return null;
        return new ArcEntity
        {
            Center = arc.Center.ToPoint3(),
            Radius = arc.Radius,
            StartAngle = Angles.ToDegrees(arc.StartAngle),
            Sweep = Angles.ToDegrees(arc.Sweep),
        };
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_pts.Count == 1)
            dc.DrawLine(PreviewPen, view.ToScreen(_pts[0]), view.ToScreen(Cursor));
        else if (_pts.Count == 2 && Build(Cursor) is { } arc)
            DrawPath(dc, view, arc.ToPaths().First(), PreviewPen);
    }
}

public sealed class PointListTool : DrawTool
{
    private readonly List<Point2> _pts = [];

    public override string Prompt => "Point list: select points  [Enter / middle click = finish]";
    protected override bool HasPendingInput => _pts.Count > 0;
    protected override void Reset() => _pts.Clear();

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        _pts.Add(world);
        Host.Invalidate();
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle && HasPendingInput) Finish();
        else base.PointerReleased(screen, world, button, mods);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key is Key.Enter or Key.Return && HasPendingInput)
        {
            Finish();
            return true;
        }
        return base.KeyDown(key, mods);
    }

    private void Finish()
    {
        var e = new PointListEntity();
        e.Points.AddRange(_pts.Select(p => p.ToPoint3()));
        Commit("Draw points", e);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        foreach (var p in _pts)
        {
            var s = view.ToScreen(p);
            dc.DrawLine(PreviewPen, new Point(s.X - 4, s.Y - 4), new Point(s.X + 4, s.Y + 4));
            dc.DrawLine(PreviewPen, new Point(s.X - 4, s.Y + 4), new Point(s.X + 4, s.Y - 4));
        }
    }
}

/// <summary>Places a text entity at the clicked point (text supplied by a dialog beforehand).</summary>
public sealed class TextTool(string text, double height) : DrawTool
{
    public override string Prompt => "Text: select location";
    protected override bool HasPendingInput => false;
    protected override void Reset() { }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        var saved = RepeatCommands;
        RepeatCommands = false;
        Commit("Draw text", new TextEntity { Text = text, Height = height, Position = world.ToPoint3() });
        RepeatCommands = saved;
    }
}

/// <summary>Measure: two clicks, reports distance via callback.</summary>
public sealed class MeasureTool(Action<Point2, Point2> report) : DrawTool
{
    private Point2? _first;

    public override string Prompt => _first == null ? "Measure: select first point" : "Select second point";
    protected override bool HasPendingInput => _first != null;
    protected override void Reset() => _first = null;

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_first is not { } a)
        {
            _first = world;
            Host.SetPrompt(Prompt);
            return;
        }
        report(a, world);
        Reset();
        Host.EndTool();
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_first is { } a) dc.DrawLine(PreviewPen, view.ToScreen(a), view.ToScreen(Cursor));
    }
}
