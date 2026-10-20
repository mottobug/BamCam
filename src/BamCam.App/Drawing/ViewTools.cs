using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.App.Services;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Drawing;

/// <summary>Services a <see cref="ViewTool"/> can use while it is active.</summary>
public interface IToolHost
{
    DocumentSession Session { get; }
    ViewTransform View { get; }
    /// <summary>Applies grid/point snapping to a world point.</summary>
    Point2 Snap(Point2 world, Point screen);
    void Invalidate();
    void SetPrompt(string? text);
    /// <summary>Ends the current tool and returns to selection.</summary>
    void EndTool();
    IReadOnlyList<Entity> HitTest(Point screen, double pixelTolerance = 5);
    IReadOnlyList<Entity> WindowSelect(Point a, Point b, bool crossing);
}

/// <summary>Interactive mode of the drawing view (selection, drawing tools, ...).</summary>
public abstract class ViewTool
{
    protected IToolHost Host { get; private set; } = null!;

    public virtual string Prompt => "";

    public void Attach(IToolHost host)
    {
        Host = host;
        host.SetPrompt(Prompt);
        OnAttached();
    }

    protected virtual void OnAttached() { }
    public virtual void OnDetached() { }

    public virtual void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods) { }
    public virtual void PointerMoved(Point screen, Point2 world, KeyModifiers mods) { }
    public virtual void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods) { }

    /// <summary>Returns true if the key was handled.</summary>
    public virtual bool KeyDown(Key key, KeyModifiers mods) => false;

    /// <summary>Draws rubber-band previews on top of the drawing.</summary>
    public virtual void RenderOverlay(DrawingContext dc, ViewTransform view) { }

    protected static readonly IPen PreviewPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1, DashStyle.Dash);

    protected static void DrawPath(DrawingContext dc, ViewTransform view, Path2 path, IPen pen)
    {
        var pts = path.Flatten(view.ToWorldLength(0.5));
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(view.ToScreen(pts[0]), false);
            for (var i = 1; i < pts.Count; i++) ctx.LineTo(view.ToScreen(pts[i]));
            ctx.EndFigure(path.Closed);
        }
        dc.DrawGeometry(null, pen, geo);
    }
}

/// <summary>Default tool: click / window selection.</summary>
public sealed class SelectTool : ViewTool
{
    private Point? _dragStart;
    private Point _dragCurrent;

    public override string Prompt => "";

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        _dragStart = screen;
        _dragCurrent = screen;
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        if (_dragStart == null) return;
        _dragCurrent = screen;
        Host.Invalidate();
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button != MouseButton.Left || _dragStart is not { } start) return;
        _dragStart = null;
        var toggle = mods.HasFlag(KeyModifiers.Control);
        var add = mods.HasFlag(KeyModifiers.Shift);
        var dist = Math.Abs(screen.X - start.X) + Math.Abs(screen.Y - start.Y);
        if (dist < 4)
        {
            var hits = Host.HitTest(screen);
            if (hits.Count == 0)
            {
                if (!toggle && !add) Host.Session.ClearSelection();
            }
            else
            {
                Host.Session.Select([hits[0]], add, toggle);
            }
        }
        else
        {
            // Left-to-right: fully enclosed; right-to-left: crossing (touching) selection.
            var crossing = screen.X < start.X;
            Host.Session.Select(Host.WindowSelect(start, screen, crossing), add, toggle);
        }
        Host.Invalidate();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key == Key.Escape)
        {
            _dragStart = null;
            Host.Session.ClearSelection();
            Host.Invalidate();
            return true;
        }
        return false;
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (_dragStart is not { } s) return;
        var rect = new Rect(s, _dragCurrent).Normalize();
        var crossing = _dragCurrent.X < s.X;
        var fill = new SolidColorBrush(crossing ? Color.FromArgb(40, 80, 255, 80) : Color.FromArgb(40, 80, 140, 255));
        var pen = new Pen(Brushes.White, 1, crossing ? DashStyle.Dash : null);
        dc.DrawRectangle(fill, pen, rect);
    }
}
