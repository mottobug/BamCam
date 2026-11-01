using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Trim / extend: pick boundary objects (Enter or middle click accepts; objects selected beforehand are taken as
/// boundaries), then click lines, arcs or open polylines near an end to extend them to the next boundary, or
/// Shift+click a piece to trim it away. Each click is one undoable step; Esc ends.
/// </summary>
public sealed class TrimExtendTool : ViewTool
{
    // By id: undo and redo replace the drawing's objects by copies.
    private readonly HashSet<int> _boundaries;
    private bool _picking;

    public TrimExtendTool(IEnumerable<Entity> selected)
    {
        _boundaries = selected.Where(IsBoundary).Select(e => e.Id).ToHashSet();
        _picking = _boundaries.Count == 0;
    }

    private IEnumerable<Entity> Boundaries => Host.Session.Document.AllEntities().Where(e => _boundaries.Contains(e.Id));

    public override string Prompt => _picking
        ? T("Trim / Extend: click the boundary objects  [Enter / middle click = done, Esc = cancel]")
        : T("Click lines or arcs near the end to extend them, Shift+click a piece to trim it  [Esc = end]");

    private static bool IsBoundary(Entity e) => e is not (PointListEntity or SurfaceEntity or TextEntity);

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        var hit = Host.HitTest(screen).FirstOrDefault();
        if (hit == null) return;
        if (_picking)
        {
            if (IsBoundary(hit) && !_boundaries.Remove(hit.Id)) _boundaries.Add(hit.Id);
            Host.Invalidate();
            return;
        }
        if (hit is not (LineEntity or ArcEntity or PolylineEntity) || hit.ToPaths().FirstOrDefault() is not { Closed: false } path)
        {
            Host.SetPrompt($"{T("Only lines, arcs and open polylines can be trimmed or extended.")}  {Prompt}");
            return;
        }
        var boundaries = Boundaries.Where(b => b != hit).SelectMany(b => b.ToPaths()).ToList();
        // The point clicked on the object (not the snapped cursor).
        var click = Host.View.ToWorld(screen);
        if (mods.HasFlag(KeyModifiers.Shift))
        {
            if (TrimExtend.Trim(path, click, boundaries) is not { } pieces)
            {
                Host.SetPrompt($"{T("The object does not cross a boundary.")}  {Prompt}");
                return;
            }
            Host.Session.Execute(T("Trim"), () => Replace(hit, pieces));
        }
        else
        {
            if (TrimExtend.Extend(path, click, boundaries) is not { } extended)
            {
                Host.SetPrompt($"{T("No boundary in the way.")}  {Prompt}");
                return;
            }
            Host.Session.Execute(T("Extend"), () => Replace(hit, [extended]));
        }
        Host.SetPrompt(Prompt);
        Host.Invalidate();
    }

    /// <summary>The object becomes the first piece, further pieces are copies of it; no piece removes it.</summary>
    private void Replace(Entity entity, List<Path2> pieces)
    {
        var doc = Host.Session.Document;
        var layer = entity.Layer;
        if (pieces.Count == 0)
        {
            doc.RemoveEntity(entity);
            _boundaries.Remove(entity.Id);
            return;
        }
        SetPath(entity, pieces[0]);
        foreach (var piece in pieces.Skip(1))
        {
            var copy = entity.CloneEntity();
            SetPath(copy, piece);
            doc.AddEntity(copy, layer);
            if (_boundaries.Contains(entity.Id)) _boundaries.Add(copy.Id);
        }
    }

    private static void SetPath(Entity e, Path2 path)
    {
        e.Transform = Matrix4.Identity;
        switch (e)
        {
            case LineEntity line:
                line.Points.Clear();
                line.Points.AddRange(path.Vertices.Select(v => v.Point));
                break;
            case ArcEntity arcEntity when path.Segments().Single() is { IsArc: true } s:
            {
                var arc = s.ToArc();
                arcEntity.Center = arc.Center.ToPoint3(s.Start.Z);
                arcEntity.Radius = arc.Radius;
                arcEntity.StartAngle = Angles.ToDegrees(arc.StartAngle);
                arcEntity.Sweep = Angles.ToDegrees(arc.Sweep);
                break;
            }
            case PolylineEntity poly:
                poly.Closed = false;
                poly.Points.Clear();
                poly.Points.AddRange(path.Vertices);
                break;
        }
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key == Key.Escape)
        {
            Host.EndTool();
            return true;
        }
        if (key is Key.Enter or Key.Return && _picking && _boundaries.Count > 0)
        {
            _picking = false;
            Host.SetPrompt(Prompt);
            Host.Invalidate();
            return true;
        }
        return false;
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button == MouseButton.Middle) KeyDown(Key.Enter, KeyModifiers.None);
    }

    public override void RenderOverlay(DrawingContext dc, ViewTransform view)
    {
        var pen = new Pen(Brushes.Orange, 2.5);
        foreach (var b in Boundaries)
        foreach (var path in b.ToPaths())
            DrawPath(dc, view, path, pen);
    }
}
