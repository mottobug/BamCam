using System.Reflection;
using BamCam.App.Drawing;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>Opens the points table for a polyline, line or point list ("Points [...]" in the property grid).</summary>
    private async void EditPoints(object target, PropertyInfo? property = null)
    {
        if (target is MachineOp op)
        {
            PickOperationObjects(op, property);
            return;
        }
        if (target is not Entity entity) return;
        List<Vertex>? vertices = entity switch
        {
            PolylineEntity p => p.Points.ToList(),
            LineEntity l => l.Points.Select(pt => new Vertex(pt)).ToList(),
            PointListEntity pl => pl.Points.Select(pt => new Vertex(pt)).ToList(),
            SplineEntity s => s.ControlPoints.Select(pt => new Vertex(pt)).ToList(),
            _ => null,
        };
        var label = F("{0} ({1})", T(entity.TypeName), entity.Id);
        if (vertices == null)
        {
            Log(F("{0} has no editable point list. Use double-click to edit it in the drawing.", label));
            return;
        }
        var editor = new PointsEditorWindow(F("Points - {0}", label), vertices, withBulge: entity is PolylineEntity,
            closed: entity is PolylineEntity poly ? poly.Closed : null);
        var result = await editor.ShowFor(this);
        if (result is not { } r) return;
        _session.Execute(T("Edit points"), () =>
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
                case SplineEntity s:
                    s.SetControlPoints(r.Points.Select(v => v.Point));
                    break;
            }
        });
        // Undo snapshots replace the document; re-inspect the (possibly new) entity with the same id.
        if (_session.Document.FindEntity(entity.Id) is { } updated) _session.Select([updated]);
        Log(F("{0}: {1} points.", label, r.Points.Count));
    }

    /// <summary>Interactive edit mode for one object (same as double-click).</summary>
    private void EditInView(Entity entity)
    {
        if (entity is TextEntity t) _ = EditTextEntity(t);
        else View.SetTool(new EditPointsTool(entity));
    }

    /// <summary>Operation whose drawing objects are being re-selected (keeps it in the property grid meanwhile).</summary>
    private MachineOp? _pickingOp;

    /// <summary>
    /// Interactive selection of the drawing objects of an operation (Primitive IDs [...] or Select Drawing Objects):
    /// the current objects are selected, the user changes the selection, Enter applies, Esc cancels.
    /// </summary>
    private void PickOperationObjects(MachineOp op, PropertyInfo? listProperty = null)
    {
        var doc = _session.Document;
        // Primitive IDs, or another id list of the operation (e.g. Boundary Shapes of a 3D profile).
        var list = listProperty?.GetValue(op) as List<int> ?? op.PrimitiveIds;
        if (!doc.Parts.Any(p => p.Operations.Contains(op)))
        {
            Log(T("Drawing objects can only be chosen for machining operations of the drawing."));
            return;
        }
        var missing = list.Where(id => doc.FindEntity(id) == null).ToList();
        if (missing.Count > 0) Log(F("{0}: objects {1} do not exist and will be removed when applying.", op.Name, string.Join(", ", missing)));
        _pickingOp = op;
        _session.Select(list.Select(doc.FindEntity).OfType<Entity>());
        _session.Inspect(op);
        View.SetTool(new PickObjectsTool(op.Name,
            selection =>
            {
                _pickingOp = null;
                // Keep the order of objects that stay, append new ones by id.
                var chosen = selection.Select(e => e.Id).ToHashSet();
                var ids = list.Where(chosen.Contains).Concat(chosen.Except(list).Order()).ToList();
                _session.Execute(T("Set MOP objects"), () =>
                {
                    list.Clear();
                    list.AddRange(ids);
                });
                _session.ClearSelection();
                _session.Inspect(op);
                Log(F("{0}: {1} drawing object(s).", op.Name, ids.Count));
            },
            () =>
            {
                _pickingOp = null;
                _session.ClearSelection();
                _session.Inspect(op);
            }));
        View.Focus();
    }

    /// <summary>After editing Primitive IDs as text: report ids that are not in the drawing.</summary>
    private void CheckPrimitiveIds(MachineOp op)
    {
        var missing = op.PrimitiveIds.Where(id => _session.Document.FindEntity(id) == null).ToList();
        if (missing.Count > 0) Log(F("{0}: objects {1} do not exist in the drawing.", op.Name, string.Join(", ", missing)));
    }
}