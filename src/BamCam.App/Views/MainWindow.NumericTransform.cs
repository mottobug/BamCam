using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    private NumericTransformWindow? _numericTransform;

    /// <summary>Edit ▸ Transform ▸ Numeric: a window that stays open beside the drawing.</summary>
    private void ShowNumericTransform()
    {
        if (_numericTransform is { } open)
        {
            open.Activate();
            return;
        }
        var window = CreateNumericTransformWindow();
        window.Closed += (_, _) => _numericTransform = null;
        _numericTransform = window;
        window.Show(this);
    }

    private NumericTransformWindow CreateNumericTransformWindow() => new(
        () => CadOps.Bounds(Selected),
        () => _session.Selection.Count,
        ApplyNumericTransform,
        lines =>
        {
            View.TransformPreview = lines;
            View.Invalidate();
        });

    /// <summary>
    /// Applies a matrix to the selection, or to a copy of it, as one undo step. Objects with circles or arcs are left out
    /// with a warning when the transform would not keep them circles and arcs.
    /// </summary>
    private void ApplyNumericTransform(Matrix4 m, bool copy)
    {
        if (_generating) return;
        var selection = Selected;
        var targets = selection.Where(e => NumericTransform.CanApply(e, m)).ToList();
        var skipped = selection.Count - targets.Count;
        if (skipped > 0)
            Log(F("Warning: {0}", F("Transform: {0} object(s) with circles or arcs left out, as they cannot be rotated out of the XY plane or scaled unevenly: {1}.",
                skipped, string.Join(", ", selection.Except(targets).Take(10).Select(e => e.Id)))));
        if (targets.Count == 0) return;

        var created = new List<Entity>();
        _session.Execute(T("Transform"), () =>
        {
            if (copy)
            {
                foreach (var e in targets)
                {
                    var clone = e.CloneEntity();
                    clone.Id = 0;
                    _session.Document.AddEntity(clone, e.Layer);
                    clone.ApplyTransform(m);
                    created.Add(clone);
                }
            }
            else
            {
                CadOps.Transform(targets, m);
            }
        });
        if (copy) _session.Select(created);
        Log(F("Transform applied to {0} object(s).", targets.Count));
        View.Invalidate();
    }
}
