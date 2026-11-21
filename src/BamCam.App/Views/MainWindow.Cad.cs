using BamCam.App.Drawing;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

public partial class MainWindow
{
    /// <summary>A small distance in drawing units for matching points: 0.01 mm or 0.0004 in.</summary>
    private double PointTolerance => _session.Document.Units == DrawingUnits.Inches ? 0.0004 : 0.01;

    /// <summary>
    /// Edit ▸ Intersection Points: a point list with the places where the selected shapes cross or touch, to snap to
    /// or to drill.
    /// </summary>
    private void IntersectionPoints()
    {
        if (!RequireSelection()) return;
        var points = CadOps.IntersectionPoints(Selected);
        if (points.Count == 0)
        {
            Log(T("Intersection Points: the selected objects do not cross."));
            return;
        }
        var list = new PointListEntity();
        list.Points.AddRange(points);
        var doc = _session.Document;
        _session.Execute(T("Intersection points"), () => doc.AddEntity(list, doc.ActiveLayer));
        _session.Select([list]);
        Log(F("Intersection Points: {0} point(s) added as a point list.", points.Count));
    }

    /// <summary>Edit ▸ Polyline ▸ Set Start Point: the selected closed polyline starts at the picked point.</summary>
    private void SetPolylineStartPoint()
    {
        if (Selected is not [PolylineEntity { Closed: true } polyline])
        {
            Log(T("Set Start Point: select one closed polyline first."));
            return;
        }
        var tolerance = PointTolerance;
        View.SetTool(new PickPointTool(T("Select start point"), p =>
        {
            _session.Execute(T("Set start point"), () => CadOps.SetStartPoint(polyline, p, tolerance));
            View.Invalidate();
        }));
    }

    /// <summary>Edit ▸ Polyline ▸ Break at Points: splits the selected shapes at the points of the selected point lists.</summary>
    private void BreakAtPoints()
    {
        var sources = Selected;
        if (!sources.OfType<PointListEntity>().Any() || sources.All(e => e is PointListEntity))
        {
            Log(T("Break at Points: select the shapes to split and one or more point lists."));
            return;
        }
        var broken = CadOps.BreakAtPoints(sources, PointTolerance);
        if (broken.Count == 0)
        {
            Log(T("Break at Points: no point lies on the selected shapes."));
            return;
        }
        var doc = _session.Document;
        _session.Execute(T("Break at points"), () =>
        {
            foreach (var (source, pieces) in broken)
            {
                var layer = source.Layer ?? doc.ActiveLayer;
                doc.RemoveEntity(source);
                foreach (var piece in pieces) doc.AddEntity(piece, layer);
            }
        });
        _session.Select(broken.SelectMany(b => b.Pieces).ToList());
        Log(F("Break at Points: {0} object(s) split into {1} pieces.", broken.Count, broken.Sum(b => b.Pieces.Count)));
        WarnBrokenReferences(broken.Select(b => b.Source), true);
    }

    /// <summary>Edit ▸ Polyline ▸ Arc Fit: runs of short straight segments of the selected polylines become arcs.</summary>
    private async Task ArcFitPolylines()
    {
        var polylines = Selected.OfType<PolylineEntity>().ToList();
        if (polylines.Count == 0)
        {
            Log(T("Arc Fit: select polylines first."));
            return;
        }
        if (await Dialogs.PromptNumber(this, T("Arc Fit"), T("Arc fit tolerance:"), PointTolerance) is not { } tolerance || tolerance <= 0) return;
        var fitted = new List<(PolylineEntity Polyline, List<Vertex> Points)>();
        int before = 0, after = 0;
        foreach (var p in polylines)
        {
            var copy = (PolylineEntity)p.CloneEntity();
            var (b, a) = CadOps.ArcFit(copy, tolerance);
            if (a >= b) continue;
            fitted.Add((p, copy.Points.ToList()));
            before += b;
            after += a;
        }
        if (fitted.Count == 0)
        {
            Log(T("Arc Fit: no run of segments lies on an arc within the tolerance."));
            return;
        }
        _session.Execute(T("Arc fit"), () =>
        {
            foreach (var (p, points) in fitted)
            {
                p.Points.Clear();
                p.Points.AddRange(points);
            }
        });
        View.Invalidate();
        Log(F("Arc Fit: {0} polyline(s) changed, {1} segments became {2}.", fitted.Count, before, after));
    }

    /// <summary>
    /// Edit ▸ Polyline ▸ Remove Overlaps: copies of the selected polylines without the places where they run back over
    /// themselves, on the active layer (the originals stay).
    /// </summary>
    private void RemoveOverlaps()
    {
        var polylines = Selected.OfType<PolylineEntity>().ToList();
        if (polylines.Count == 0)
        {
            Log(T("Remove Overlaps: select polylines first."));
            return;
        }
        var copies = new List<PolylineEntity>();
        var removed = 0;
        foreach (var p in polylines)
        {
            var (path, n) = CadOps.RemoveOverlaps(p.ToPaths().First(), PointTolerance);
            if (n == 0) continue;
            copies.Add(new PolylineEntity(path));
            removed += n;
        }
        if (copies.Count == 0)
        {
            Log(T("Remove Overlaps: the selected polylines do not run back over themselves."));
            return;
        }
        var doc = _session.Document;
        _session.Execute(T("Remove overlaps"), () => copies.ForEach(c => doc.AddEntity(c, doc.ActiveLayer)));
        _session.Select(copies);
        Log(F("Remove Overlaps: {0} point(s) removed; {1} cleaned copy(ies) on the active layer, the originals are unchanged.", removed, copies.Count));
    }

    /// <summary>
    /// Edit ▸ Polyline ▸ Fillet / Chamfer: corners of the one selected polyline. A rectangle or line is turned into a
    /// polyline first (one undoable step, same id so that machining operations keep referring to it).
    /// </summary>
    private void FilletChamfer()
    {
        var selected = Selected;
        if (selected.Count != 1 || selected[0] is not (PolylineEntity or RectEntity or LineEntity))
        {
            Log(T("Fillet / Chamfer: select one polyline, rectangle or line first."));
            return;
        }
        if (selected[0] is not PolylineEntity polyline)
        {
            var source = selected[0];
            if (CadOps.ToPolylines(source) is not [var converted])
            {
                Log(T("Fillet / Chamfer: select one polyline, rectangle or line first."));
                return;
            }
            var doc = _session.Document;
            _session.Execute(T("Convert to polyline"), () =>
            {
                var layer = source.Layer ?? doc.ActiveLayer;
                doc.RemoveEntity(source);
                converted.Id = source.Id;
                converted.Tag = source.Tag;
                doc.AddEntity(converted, layer);
            });
            _session.Select([converted]);
            polyline = converted;
        }
        View.SetTool(new CornerTool(polyline));
    }

    /// <summary>Draw ▸ Text on Polyline: one text and one guide object selected; the start point is clicked.</summary>
    private void TextOnPolyline()
    {
        var selected = Selected;
        var text = selected.OfType<TextEntity>().ToList();
        var guides = selected.Where(e => e is PolylineEntity or LineEntity or ArcEntity or CircleEntity or SplineEntity or RectEntity).ToList();
        if (text.Count != 1 || guides.Count != 1 || selected.Count != 2)
        {
            Log(T("Text on polyline: select one text and one polyline (or line, arc, circle, spline, rectangle) as guide."));
            return;
        }
        View.SetTool(new TextOnPathTool(text[0], guides[0]));
    }

    /// <summary>Reference of the align commands (toolbar list), kept in the settings.</summary>
    private AlignReference AlignTo
    {
        get => _settings.AlignReference;
        set
        {
            _settings.AlignReference = value;
            SaveSettings();
        }
    }

    /// <summary>Aligns the selected objects (as CamBam's Align Toolbar plugin).</summary>
    private void AlignSelection(AlignEdge edge)
    {
        var selected = Selected;
        if (selected.Count == 0)
        {
            Log(T("Align: select the objects first (in order: the last or first selected one can be the reference)."));
            return;
        }
        var ok = false;
        _session.Execute(T("Align"), () => ok = Align.Apply(_session.Document, selected, edge, AlignTo));
        if (!ok)
            Log(AlignTo == AlignReference.Stock ? T("Align: there is no stock.")
                : T("Align: select at least two objects to align them to the first or last one."));
    }

    private static readonly (AlignReference Value, string Text)[] AlignReferences =
    [
        (AlignReference.LastSelected, "To last selected"),
        (AlignReference.FirstSelected, "To first selected"),
        (AlignReference.Selection, "To selection"),
        (AlignReference.Drawing, "To drawing"),
        (AlignReference.Origin, "To origin"),
        (AlignReference.Stock, "To stock"),
    ];
}
