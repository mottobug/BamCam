using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Text along a polyline: the preview follows the cursor's position on the guide; a click there replaces the text
/// by its bent outlines (closed polylines). U reverses the direction along the guide, S puts the text on the other
/// side (R and F would start other commands); Esc cancels.
/// </summary>
public sealed class TextOnPathTool(TextEntity text, Entity guide) : ViewTool
{
    private readonly int _textId = text.Id;
    private readonly int _guideId = guide.Id;
    // Starts with the direction in which the text reads left to right.
    private bool _reverse = guide.ToPaths().FirstOrDefault() is { } p && TextOnPath.ReadsBetterReversed(p);
    private bool _flip;
    private double _start;

    public override string Prompt =>
        T("Text on polyline: click the start point of the text on the guide  [U = reverse direction, S = other side, Esc = cancel]");

    private (TextEntity Text, TextOnPath.Sampler Guide)? Resolve()
    {
        var doc = Host.Session.Document;
        if (doc.FindEntity(_textId) is not TextEntity t || doc.FindEntity(_guideId)?.ToPaths().FirstOrDefault() is not { } path) return null;
        return (t, new TextOnPath.Sampler(_reverse ? path.Reversed() : path));
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        if (Resolve() is { } r) _start = r.Guide.Project(world);
        Host.Invalidate();
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed || Resolve() is not { } r) return;
        var outlines = TextOnPath.Bend(r.Text, r.Guide, r.Guide.Project(world), _flip);
        if (outlines.Count == 0) return;
        var doc = Host.Session.Document;
        var created = outlines.Select(o => (Entity)new PolylineEntity(o)).ToList();
        Host.Session.Execute(T("Text on polyline"), () =>
        {
            var layer = r.Text.Layer ?? doc.ActiveLayer;
            doc.RemoveEntity(r.Text);
            foreach (var e in created) doc.AddEntity(e, layer);
        });
        Host.Session.Select(created);
        Host.EndTool();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Escape:
                Host.EndTool();
                return true;
            case Key.U:
                _reverse = !_reverse;
                break;
            case Key.S:
                _flip = !_flip;
                break;
            default:
                return false;
        }
        Host.Invalidate();
        return true;
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        if (Resolve() is not { } r) return;
        foreach (var o in TextOnPath.Bend(r.Text, r.Guide, _start, _flip)) DrawPath(dc, view, o, PreviewPen);
        // Start point and direction on the guide.
        var (p, t) = r.Guide.At(_start);
        var s = view.ToScreen(p);
        var pen = new Pen(Brushes.Orange, 2);
        dc.DrawEllipse(null, pen, s, 5, 5);
        var tip = view.ToScreen(p + t * view.ToWorldLength(30));
        dc.DrawLine(pen, s, tip);
    }
}
