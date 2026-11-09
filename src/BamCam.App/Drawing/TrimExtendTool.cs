using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Drawing;

/// <summary>
/// Trim / extend: pick boundary objects (Enter or middle click accepts; with none picked every object is a boundary),
/// then click lines, arcs or open polylines near an end to extend them to the next boundary, or Shift+click a piece
/// to trim it away. Each click is one undoable step; Esc ends. A selection made beforehand is not used: it usually
/// holds the object to be extended, which cannot be its own boundary.
/// </summary>
public sealed class TrimExtendTool : ViewTool
{
    // By id: undo and redo replace the drawing's objects by copies.
    private readonly HashSet<int> _boundaries = [];
    private bool _picking = true;
    /// <summary>No boundary picked: every (visible) object is a boundary.</summary>
    private bool _all;

    private IEnumerable<Entity> Boundaries => Host.Session.Document.Layers.Where(l => l.Visible).SelectMany(l => l.Entities).Where(e => !e.Hidden)
        .Where(e => IsBoundary(e) && (_all || _boundaries.Contains(e.Id)));

    public override string Prompt => _picking
        ? F("Trim / Extend: click the boundary objects ({0} picked)  [Enter / middle click = continue; nothing picked = all objects are boundaries; Esc = cancel]", _boundaries.Count)
        : (_all ? T("All objects are boundaries.") : F("{0} boundary objects.", _boundaries.Count)) + "  " +
          T("Click lines or arcs near the end to extend them, Shift+click a piece to trim it  [Esc = end]");

    private static bool IsBoundary(Entity e) => e is not (PointListEntity or SurfaceEntity or TextEntity);

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        var hit = Host.HitTest(screen).FirstOrDefault();
        if (hit == null) return;
        if (_picking)
        {
            if (IsBoundary(hit) && !_boundaries.Remove(hit.Id)) _boundaries.Add(hit.Id);
            Host.SetPrompt(Prompt);
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
        if (key is Key.Enter or Key.Return && _picking)
        {
            _picking = false;
            _all = _boundaries.Count == 0;
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
        // With all objects as boundaries nothing needs highlighting.
        if (_all) return;
        var pen = new Pen(Brushes.Orange, 2.5);
        foreach (var b in Boundaries)
        foreach (var path in b.ToPaths())
            DrawPath(dc, view, path, pen);
    }
}
