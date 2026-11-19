using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Plasma ▸ Set Start Points (issue 27): a click on an outline of the operation sets where its cut (with the lead-in)
/// starts, anywhere on the outline; Del on an outline gives it back to the automatic start; Esc ends. The chosen points
/// are drawn as orange dots, the point under the cursor as a ring.
/// </summary>
public sealed class ShapeStartTool(PlasmaOp op, Action changed) : ViewTool
{
    private (int Id, Point2 Point, Path2 Outline)? _hover;

    public override string Prompt => T("Start points: click an outline where its cut should start (Del: automatic start again, Esc: end)");

    /// <summary>The closed outlines of the operation, as the generator sees them (with the operation's transform).</summary>
    private IEnumerable<(int Id, Path2 Outline)> Outlines()
    {
        var doc = Host.Session.Document;
        var m = op.MOPTransform.Value;
        foreach (var id in op.PrimitiveIds)
        {
            if (doc.FindEntity(id) is not { } e) continue;
            foreach (var p in e.ToPaths())
                if (p.Closed && p.Count > 1) yield return (id, m.IsIdentity ? p : p.Transformed(m));
        }
    }

    /// <summary>The outline point nearest the cursor, within 10 pixels.</summary>
    private (int, Point2, Path2)? Nearest(Point screen)
    {
        var world = Host.View.ToWorld(screen);
        var reach = Host.View.ToWorldLength(10);
        (int, Point2, Path2)? best = null;
        var bestDistance = reach;
        foreach (var (id, outline) in Outlines())
        {
            var d = outline.DistanceTo(world);
            if (d > bestDistance) continue;
            bestDistance = d;
            best = (id, outline.StartAt(world).Vertices[0].XY, outline);
        }
        return best;
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        _hover = Nearest(screen);
        Host.Invalidate();
    }

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed || Nearest(screen) is not { } hit) return;
        Set(hit.Item1, hit.Item3, hit.Item2);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        switch (key)
        {
            case Key.Escape:
                Host.EndTool();
                return true;
            case Key.Delete or Key.Back when _hover is { } h:
                Set(h.Id, h.Outline, null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Replaces the start point of one outline (null: automatic again) as one undo step.</summary>
    private void Set(int id, Path2 outline, Point2? point)
    {
        var tolerance = 1e-4 + 1e-6 * Math.Max(outline.Bounds().Width, outline.Bounds().Height);
        var starts = op.ReadShapeStarts().Where(s => s.Id != id || outline.DistanceTo(s.Point) > tolerance).ToList();
        if (point is { } p) starts.Add((id, p));
        Host.Session.Execute(point != null ? T("Set start point") : T("Remove start point"), () => op.WriteShapeStarts(starts));
        Host.Invalidate();
        changed();
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        var dot = new SolidColorBrush(Color.FromRgb(255, 150, 0));
        foreach (var (_, p) in op.ReadShapeStarts())
            dc.DrawEllipse(dot, new Pen(Brushes.Black, 1), view.ToScreen(p), 4, 4);
        if (_hover is { } h)
            dc.DrawEllipse(null, new Pen(dot, 2), view.ToScreen(h.Point), 7, 7);
    }
}
