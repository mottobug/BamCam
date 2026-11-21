using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>Draw ▸ More ▸ Ellipse: two corners of the rectangle round it, like the rectangle; typed width x height.</summary>
public sealed class EllipseTool(double tolerance) : DrawTool
{
    private Point2? _corner;

    public override string Prompt => _corner == null ? T("Ellipse: select first corner") : T("Select opposite corner");
    protected override bool HasPendingInput => _corner != null;
    protected override void Reset() => _corner = null;
    protected override string DimensionLabel => T("Width x Height");
    protected override bool TwoDimensions => true;

    protected override string? LiveDimension
    {
        get
        {
            if (_corner is not { } a) return null;
            var o = Opposite(Cursor);
            return $"{Num(Math.Abs(o.X - a.X))} x {Num(Math.Abs(o.Y - a.Y))}";
        }
    }

    private Point2 Opposite(Point2 cursor)
    {
        var a = _corner!.Value;
        var v = TypedValues;
        var x = v.Length > 0 && v[0] is { } w ? a.X + (cursor.X < a.X ? -w : w) : cursor.X;
        var y = v.Length > 1 && v[1] is { } h ? a.Y + (cursor.Y < a.Y ? -h : h) : cursor.Y;
        return new Point2(x, y);
    }

    private void Finish(Point2 opposite)
    {
        if (_corner is not { } a || Shapes.Ellipse(a, opposite, tolerance) is not { } path) return;
        Commit(T("Draw ellipse"), new PolylineEntity(path));
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_corner == null)
        {
            _corner = world;
            Host.SetPrompt(Prompt);
            return;
        }
        Finish(Opposite(world));
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (DimensionKey(key)) return true;
        if (key is Key.Enter or Key.Return && _corner != null && TypedValues is [not null, not null])
        {
            Finish(Opposite(Cursor));
            return true;
        }
        return base.KeyDown(key, mods);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_corner is not { } a) return;
        var o = Opposite(Cursor);
        if (Shapes.Ellipse(a, o, tolerance * 10) is { } path) DrawPath(dc, view, path, PreviewPen);
        DrawPath(dc, view, Path2.FromPoints([a, new(o.X, a.Y), o, new(a.X, o.Y)], true), PreviewPen);
    }
}

/// <summary>Draw ▸ More ▸ Flat Spiral: centre, then radius (or a typed diameter), like the circle.</summary>
public sealed class FlatSpiralTool(double stepover, bool clockwise) : DrawTool
{
    private Point2? _center;

    public override string Prompt => _center == null ? T("Flat spiral: select center point") : T("Select radius");
    protected override bool HasPendingInput => _center != null;
    protected override void Reset() => _center = null;
    protected override string DimensionLabel => T("Diameter");
    protected override string? LiveDimension => _center == null ? null : $"Ø {Num(2 * Radius(Cursor))}";

    private double Radius(Point2 cursor) => TypedValues is [{ } d, ..] ? d / 2 : Point2.Distance(_center!.Value, cursor);

    private void Finish(double radius)
    {
        if (_center is not { } c || Shapes.FlatSpiral(c, radius, stepover, clockwise) is not { } path) return;
        Commit(T("Draw flat spiral"), new PolylineEntity(path));
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (_center == null)
        {
            _center = world;
            Host.SetPrompt(Prompt);
            return;
        }
        Finish(Radius(world));
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (DimensionKey(key)) return true;
        if (key is Key.Enter or Key.Return && _center != null && TypedValues is [{ } d, ..])
        {
            Finish(d / 2);
            return true;
        }
        return base.KeyDown(key, mods);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_center is not { } c) return;
        var r = Radius(Cursor);
        // Not more turns in the preview than can be told apart.
        if (r < Tolerance.Point || r / stepover > 2000) return;
        if (Shapes.FlatSpiral(c, r, stepover, clockwise) is { } path) DrawPath(dc, view, path, PreviewPen);
    }
}

/// <summary>
/// Draw ▸ More ▸ Tangent: the lines touching the two selected circles, arcs or points are shown; the one nearest to the
/// cursor is highlighted and a click draws it.
/// </summary>
public sealed class TangentTool(List<(Point2 A, Point2 B)> tangents) : DrawTool
{
    private static readonly IPen HotPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 2);

    public override string Prompt => T("Tangent: click the line to draw  [Esc = end]");
    protected override bool HasPendingInput => false;
    protected override void Reset() { }

    private int Nearest(Point2 p)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < tangents.Count; i++)
        {
            var (a, b) = tangents[i];
            var ab = b - a;
            var t = Math.Clamp(Point2.Dot(p - a, ab) / Point2.Dot(ab, ab), 0, 1);
            var d = Point2.Distance(p, a + ab * t);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        base.PointerMoved(screen, world, mods);
        Host.Invalidate();
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed || tangents.Count == 0) return;
        var (a, b) = tangents[Nearest(world)];
        var line = new LineEntity();
        line.Points.Add(a.ToPoint3());
        line.Points.Add(b.ToPoint3());
        Commit(T("Draw tangent"), line);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (tangents.Count == 0) return;
        var hot = Nearest(Cursor);
        for (var i = 0; i < tangents.Count; i++)
            dc.DrawLine(i == hot ? HotPen : PreviewPen, view.ToScreen(tangents[i].A), view.ToScreen(tangents[i].B));
    }
}
