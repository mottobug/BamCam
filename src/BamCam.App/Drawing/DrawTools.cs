using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>Common behaviour of drawing commands: Esc cancels, optional repeat after finishing.</summary>
public abstract class DrawTool : ViewTool
{
    protected Point2 Cursor;

    /// <summary>Restart the command after an entity is finished (config "Repeat Commands").</summary>
    public static bool RepeatCommands { get; set; } = true;

    protected abstract void Reset();
    protected abstract bool HasPendingInput { get; }

    // ---- Dimension input: while drawing, typed digits give the exact length / diameter / width and height.

    /// <summary>The digits typed so far (values separated by ';'), empty when nothing was typed.</summary>
    protected string Typed { get; private set; } = "";

    /// <summary>Typed values (invariant or comma decimal separator); empty entries are null.</summary>
    protected double?[] TypedValues => Typed.Length == 0 ? [] : Typed.Split(';').Select(s =>
        double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v : (double?)null).ToArray();

    /// <summary>Label of the typed values in the prompt ("Length", "Diameter", "Width; Height").</summary>
    protected virtual string DimensionLabel => T("Length");

    /// <summary>Second value separator allowed (rectangle: width and height).</summary>
    protected virtual bool TwoDimensions => false;

    /// <summary>An angle may follow the length ("25&lt;30", polyline and line).</summary>
    protected virtual bool AcceptsAngle => false;

    /// <summary>Shift held during the last mouse move or click (angle steps of 15°).</summary>
    protected bool AngleSnap { get; private set; }

    protected void ClearTyped()
    {
        if (Typed.Length == 0) return;
        Typed = "";
        Host.SetPrompt(Prompt);
    }

    /// <summary>Digits, decimal separator, value separator and Backspace while a value is being typed.</summary>
    protected bool DimensionKey(Key key, KeyModifiers mods = KeyModifiers.None)
    {
        if (!HasPendingInput) return false;
        // "<" is its own key on German keyboards (OemBackslash), Shift+comma on US ones; W (Winkel) works everywhere.
        var angleKey = key is Key.OemBackslash or Key.W || key == Key.OemComma && mods.HasFlag(KeyModifiers.Shift);
        if (AcceptsAngle && angleKey)
        {
            if (Typed.Contains('<')) return true;
            Typed += "<";
            ShowTyped();
            return true;
        }
        string? add = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            Key.OemMinus or Key.Subtract when Typed.EndsWith('<') => "-",
            Key.OemPeriod or Key.OemComma or Key.Decimal => ".",
            Key.X or Key.Space or Key.OemSemicolon or Key.Multiply when TwoDimensions && Typed.Length > 0 && !Typed.Contains(';') => ";",
            _ => null,
        };
        if (add != null)
        {
            if (add == "." && Typed.Split(';', '<')[^1].Contains('.')) return true;
            Typed += add;
        }
        else if (key == Key.Back && Typed.Length > 0)
        {
            Typed = Typed[..^1];
        }
        else
        {
            return false;
        }
        ShowTyped();
        return true;
    }

    private void ShowTyped()
    {
        var shown = Typed.Replace(";", " x ").Replace("<", " < ") + (Typed.EndsWith('<') ? "…°" : Typed.Contains('<') ? "°" : "");
        Host.SetPrompt(Typed.Length == 0 ? Prompt : $"{Prompt}    {DimensionLabel}: {shown}  {T("[Backspace = correct]")}");
        Host.Invalidate();
    }

    /// <summary>Typed length and angle (degrees) of "length&lt;angle"; either may be missing.</summary>
    protected (double? Length, double? Angle) TypedPolar()
    {
        if (Typed.Length == 0) return (null, null);
        var parts = Typed.Split('<');
        static double? Parse(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        var length = Parse(parts[0]) is > 0 and var l ? l : (double?)null;
        var angle = parts.Length > 1 ? Parse(parts[1]) : null;
        return (length, angle);
    }

    protected void Commit(string description, Entity entity)
    {
        Typed = "";
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
        AngleSnap = mods.HasFlag(KeyModifiers.Shift);
        if (HasPendingInput) Host.Invalidate();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Escape) return false;
        if (HasPendingInput)
        {
            Reset();
            Typed = "";
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
    /// <summary>Line tool (single lines) rather than polyline.</summary>
    public bool LinesOnly => linesOnly;

    private readonly List<Point2> _pts = [];

    public override string Prompt => _pts.Count == 0
        ? (linesOnly ? T("Line: select first point") : T("Polyline: select first point"))
        : T("Select next point  [type length, < angle (or W); Shift = 15° steps; Enter / middle click = finish, C = close, Esc = cancel]");

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
        if (_pts.Count > 0) PointerMoved(screen, world, mods); // Shift state at the click
        world = Target(world);
        if (_pts.Count > 0 && world.AlmostEquals(_pts[^1], Tolerance.Point)) return;
        _pts.Add(world);
        ClearTyped();
        Host.SetPrompt(Prompt);
        Host.Invalidate();
    }

    protected override bool AcceptsAngle => true;
    protected override string DimensionLabel => T("Length < Angle");

    /// <summary>
    /// Next point: a typed length and / or angle (degrees from +X, counter-clockwise), else the cursor; with Shift
    /// the direction snaps to 15° steps. Without a typed length the cursor gives it (projected onto the direction).
    /// </summary>
    private Point2 Target(Point2 cursor)
    {
        if (_pts.Count == 0) return cursor;
        var (length, angle) = TypedPolar();
        if (length == null && angle == null && !AngleSnap) return cursor;
        var last = _pts[^1];
        var d = cursor - last;
        var a = angle is { } deg ? Angles.ToRadians(deg) : d.Length < 1e-12 ? 0 : d.Angle;
        if (angle == null && AngleSnap) a = Math.Round(a / (Math.PI / 12)) * (Math.PI / 12);
        var dir = Point2.FromAngle(a, 1);
        return last + dir * (length ?? (angle != null || AngleSnap ? Math.Max(0, Point2.Dot(d, dir)) : d.Length));
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle && HasPendingInput) Finish(false);
        else base.PointerReleased(screen, world, button, mods);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (DimensionKey(key, mods)) return true;
        if (key is Key.Enter or Key.Return && HasPendingInput)
        {
            // Enter with a typed length or angle places the point (drawing by keyboard); otherwise it finishes.
            if (TypedPolar() is not (null, null))
            {
                _pts.Add(Target(Cursor));
                ClearTyped();
                Host.Invalidate();
            }
            else
            {
                Finish(false);
            }
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
        Commit(linesOnly ? T("Draw line") : T("Draw polyline"), e);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_pts.Count == 0) return;
        DrawPath(dc, view, Path2.FromPoints(_pts.Append(Target(Cursor)), false), PreviewPen);
    }
}

/// <summary>
/// Spline tool (as in CamBam): click the points the curve passes through; tangents are set automatically and
/// can be adjusted afterwards (double-click). Enter or middle click finishes, C or clicking the first point closes.
/// </summary>
public sealed class SplineTool : DrawTool
{
    private readonly List<Point2> _pts = [];

    public override string Prompt => _pts.Count == 0
        ? T("Spline: select first point")
        : T("Select next point  [Enter / middle click = finish, C = close, Esc = cancel]");

    protected override bool HasPendingInput => _pts.Count > 0;
    protected override void Reset() => _pts.Clear();

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_pts.Count >= 3 && world.AlmostEquals(_pts[0], Tolerance.Point))
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
        Commit(T("Draw spline"), SplineEditing.FromAnchors(_pts.Select(p => p.ToPoint3()).ToList(), closed));
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_pts.Count == 0) return;
        var pts = _pts.Append(Cursor).Select(p => p.ToPoint3()).ToList();
        if (pts.Count >= 2 && !pts[^1].XY.AlmostEquals(pts[^2].XY, Tolerance.Point))
            DrawPath(dc, view, SplineEditing.FromAnchors(pts, false).LocalPath, PreviewPen);
        foreach (var p in _pts)
        {
            var s = view.ToScreen(p);
            dc.DrawEllipse(null, new Pen(Brushes.Red, 1.5), s, 4, 4);
        }
    }
}

public sealed class CircleTool : DrawTool
{
    private Point2? _center;

    public override string Prompt => _center == null ? T("Circle: select center point") : T("Select radius");
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
        var r = Radius(world);
        if (r < Tolerance.Point) return;
        Commit(T("Draw circle"), new CircleEntity { Center = c.ToPoint3(), Diameter = 2 * r });
    }

    protected override string DimensionLabel => T("Diameter");

    /// <summary>Typed diameter, else the distance to the cursor.</summary>
    private double Radius(Point2 cursor) => TypedValues is [{ } d, ..] ? d / 2 : Point2.Distance(_center!.Value, cursor);

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (DimensionKey(key)) return true;
        if (key is Key.Enter or Key.Return && _center is { } c && TypedValues is [{ } d, ..])
        {
            Commit(T("Draw circle"), new CircleEntity { Center = c.ToPoint3(), Diameter = d });
            return true;
        }
        return base.KeyDown(key, mods);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_center is not { } c) return;
        var r = Radius(Cursor);
        if (r < Tolerance.Point) return;
        DrawPath(dc, view, new CircleEntity { Center = c.ToPoint3(), Diameter = 2 * r }.ToPaths().First(), PreviewPen);
        dc.DrawLine(PreviewPen, view.ToScreen(c), view.ToScreen(Cursor));
    }
}

public sealed class RectangleTool : DrawTool
{
    private Point2? _corner;

    public override string Prompt => _corner == null ? T("Rectangle: select first corner") : T("Select opposite corner");
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
        world = Opposite(world);
        if (Math.Abs(a.X - world.X) < Tolerance.Point || Math.Abs(a.Y - world.Y) < Tolerance.Point) return;
        Commit(T("Draw rectangle"), RectEntity.FromCorners(a, world, CornerRadius));
    }

    protected override string DimensionLabel => T("Width x Height");
    protected override bool TwoDimensions => true;

    /// <summary>Opposite corner: typed width (and height) towards the cursor; missing values follow the cursor.</summary>
    private Point2 Opposite(Point2 cursor)
    {
        var a = _corner!.Value;
        var v = TypedValues;
        var sx = cursor.X < a.X ? -1 : 1;
        var sy = cursor.Y < a.Y ? -1 : 1;
        var x = v.Length > 0 && v[0] is { } w ? a.X + sx * w : cursor.X;
        var y = v.Length > 1 && v[1] is { } h ? a.Y + sy * h : cursor.Y;
        return new Point2(x, y);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (DimensionKey(key)) return true;
        if (key is Key.Enter or Key.Return && _corner is { } a && TypedValues is [not null, not null])
        {
            Commit(T("Draw rectangle"), RectEntity.FromCorners(a, Opposite(Cursor), CornerRadius));
            return true;
        }
        return base.KeyDown(key, mods);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_corner is not { } a) return;
        DrawPath(dc, view, RectEntity.FromCorners(a, Opposite(Cursor), CornerRadius).ToPaths().First(), PreviewPen);
    }
}

/// <summary>Arc by start point, end point and a third point defining the bulge.</summary>
public sealed class ArcTool : DrawTool
{
    private readonly List<Point2> _pts = [];

    public override string Prompt => _pts.Count switch
    {
        0 => T("Arc: select start point"),
        1 => T("Select end point"),
        _ => T("Select a point on the arc"),
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
        if (Build(world) is { } arc) Commit(T("Draw arc"), arc);
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

    public override string Prompt => T("Point list: select points  [Enter / middle click = finish]");
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
        Commit(T("Draw points"), e);
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
    public override string Prompt => T("Text: select location");
    protected override bool HasPendingInput => false;
    protected override void Reset() { }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        var saved = RepeatCommands;
        RepeatCommands = false;
        Commit(T("Draw text"), new TextEntity { Text = text, Height = height, Position = world.ToPoint3() });
        RepeatCommands = saved;
    }
}

/// <summary>Measure: two clicks, reports distance via callback.</summary>
public sealed class MeasureTool(Action<Point2, Point2> report) : DrawTool
{
    private Point2? _first;

    public override string Prompt => _first == null ? T("Measure: select first point") : T("Select second point");
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
