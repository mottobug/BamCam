using System.Reflection;
using BamCam.App.Drawing;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Opens the points table for a polyline, line or point list ("Points [...]" in the property grid).</summary>
    private async void EditPoints(object target, PropertyInfo? property = null)
    {
        if (target is not Entity entity) return;
        List<Vertex>? vertices = entity switch
        {
            PolylineEntity p => p.Points.ToList(),
            LineEntity l => l.Points.Select(pt => new Vertex(pt)).ToList(),
            PointListEntity pl => pl.Points.Select(pt => new Vertex(pt)).ToList(),
            _ => null,
        };
        if (vertices == null)
        {
            Log($"{entity} has no editable point list. Use double-click to edit it in the drawing.");
            return;
        }
        var editor = new PointsEditorWindow($"Points - {entity}", vertices, withBulge: entity is PolylineEntity,
            closed: entity is PolylineEntity poly ? poly.Closed : null);
        var result = await editor.ShowFor(this);
        if (result is not { } r) return;
        _session.Execute("Edit points", () =>
        {
            switch (entity)
            {
                case PolylineEntity p:
                    p.Points.Clear();
                    p.Points.AddRange(r.Points);
                    p.Closed = r.Closed;
                    break;
                case LineEntity l:
                    l.Points.Clear();
                    l.Points.AddRange(r.Points.Select(v => v.Point));
                    break;
                case PointListEntity pl:
                    pl.Points.Clear();
                    pl.Points.AddRange(r.Points.Select(v => v.Point));
                    break;
            }
        });
        // Undo snapshots replace the document; re-inspect the (possibly new) entity with the same id.
        if (_session.Document.FindEntity(entity.Id) is { } updated) _session.Select([updated]);
        Log($"{entity}: {r.Points.Count} points.");
    }

    /// <summary>Interactive edit mode for one object (same as double-click).</summary>
    private void EditInView(Entity entity)
    {
        if (entity is TextEntity t) _ = EditTextEntity(t);
        else View.SetTool(new EditPointsTool(entity));
    }
}
