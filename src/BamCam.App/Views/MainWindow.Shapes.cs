using BamCam.App.Drawing;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Views;

/// <summary>Draw ▸ Point List patterns, Draw ▸ More shapes and Transform ▸ Center (Of Points).</summary>
public partial class MainWindow
{
    private double _lastStep = double.NaN;
    private double _lastStepover = double.NaN;
    private double _lastLoopRadius = double.NaN;
    private int _lastDirection;

    private double DefaultStep => _session.Document.Units == DrawingUnits.Inches ? 0.25 : 5;

    /// <summary>Adds one point list made from the selected objects (none when it would be empty).</summary>
    private void AddPointList(string description, IEnumerable<Point3> points, string emptyMessage)
    {
        var list = new PointListEntity();
        list.Points.AddRange(points);
        if (list.Points.Count == 0)
        {
            Log(emptyMessage);
            return;
        }
        var doc = _session.Document;
        _session.Execute(description, () => doc.AddEntity(list, doc.ActiveLayer));
        _session.Select([list]);
        Log(F("{0}: {1} point(s) added as a point list.", description, list.Points.Count));
    }

    private async Task<double?> AskStep(string title)
    {
        var step = await Dialogs.PromptNumber(this, title, T("Step:"), double.IsNaN(_lastStep) ? DefaultStep : _lastStep);
        if (step is not > 0) return null;
        _lastStep = step.Value;
        return step;
    }

    /// <summary>Draw ▸ Point List ▸ Step Around Geometry: a point every step along each selected shape.</summary>
    private async Task StepAroundPoints()
    {
        if (!RequireSelection()) return;
        var sources = Selected;
        if (await AskStep(T("Step Around Geometry")) is not { } step) return;
        var paths = sources.Where(e => e is not (PointListEntity or TextEntity)).SelectMany(e => e.ToPaths()).ToList();
        AddPointList(T("Step around geometry"), paths.SelectMany(p => Shapes.StepAround(p, step)),
            T("Step Around Geometry: select lines, arcs, circles or polylines."));
    }

    /// <summary>Draw ▸ Point List ▸ Fill Geometry / Offset Fill Geometry: a grid of points inside the selected closed shapes.</summary>
    private async Task FillPoints(bool offsetRows)
    {
        if (!RequireSelection()) return;
        var sources = Selected;
        var title = offsetRows ? T("Offset Fill Geometry") : T("Fill Geometry");
        if (await AskStep(title) is not { } step) return;
        var closed = sources.SelectMany(e => e.ToPaths()).Where(p => p.Closed).ToList();
        AddPointList(offsetRows ? T("Offset fill geometry") : T("Fill geometry"), Shapes.FillGrid(closed, step, offsetRows),
            F("{0}: no point of the grid lies inside the selected closed shapes.", title));
    }

    /// <summary>Draw ▸ Point List ▸ Extents: the corners and the centre of each selected object.</summary>
    private void ExtentsPoints()
    {
        if (!RequireSelection()) return;
        AddPointList(T("Extents"), Selected.SelectMany(e => Shapes.Extents(e.Bounds())), T("Extents: the selection has no size."));
    }

    /// <summary>Edit ▸ Transform ▸ Center (Of Points): the average of the points of the selection goes to the origin.</summary>
    private void CenterOfPoints()
    {
        if (!RequireSelection()) return;
        var sel = Selected;
        var points = sel.SelectMany(e => e is PointListEntity pl ? pl.WorldPoints()
            : e.ToPaths().SelectMany(p => p.Vertices.Select(v => v.Point))).ToList();
        if (points.Count == 0) return;
        var x = points.Average(p => p.X);
        var y = points.Average(p => p.Y);
        _session.Execute(T("Center"), () => CadOps.Transform(sel, Matrix4.Translation(-x, -y)));
    }

    /// <summary>Asks stepover and direction (and the loop radius); null when cancelled.</summary>
    private async Task<(double Stepover, bool Clockwise, double Radius)?> AskSpiral(string title, bool withRadius)
    {
        var stepover = await Dialogs.PromptNumber(this, title, T("Stepover:"), double.IsNaN(_lastStepover) ? DefaultStep / 5 : _lastStepover);
        if (stepover is not > 0) return null;
        _lastStepover = stepover.Value;
        var radius = 0.0;
        if (withRadius)
        {
            var r = await Dialogs.PromptNumber(this, title, T("Radius:"), double.IsNaN(_lastLoopRadius) ? DefaultStep / 2 : _lastLoopRadius);
            if (r is not > 0) return null;
            _lastLoopRadius = radius = r.Value;
        }
        var direction = await Dialogs.PromptChoice(this, title, T("Direction:"), [T("Counter-clockwise (CCW)"), T("Clockwise (CW)")], _lastDirection);
        if (direction is not { } d) return null;
        _lastDirection = d;
        return (stepover.Value, d == 1, radius);
    }

    private void DrawEllipse()
    {
        View.SetTool(new EllipseTool(PointTolerance / 2));
        View.Focus();
    }

    private async Task DrawFlatSpiral()
    {
        if (await AskSpiral(T("Flat Spiral"), withRadius: false) is not { } s) return;
        View.SetTool(new FlatSpiralTool(s.Stepover, s.Clockwise));
        View.Focus();
    }

    /// <summary>Draw ▸ More ▸ Spiral Path / Trochoidal Path along the selected shapes.</summary>
    private async Task DrawPathAlong(bool trochoidal)
    {
        var title = trochoidal ? T("Trochoidal Path") : T("Spiral Path");
        var sources = Selected.Where(e => e is not (PointListEntity or TextEntity)).ToList();
        if (sources.Count == 0)
        {
            Log(F("{0}: select the lines, arcs or polylines to follow first.", title));
            return;
        }
        if (await AskSpiral(title, withRadius: true) is not { } s) return;
        var created = new List<PolylineEntity>();
        foreach (var path in sources.SelectMany(e => e.ToPaths()))
        {
            var result = trochoidal ? Shapes.TrochoidalAlong(path, s.Radius, s.Stepover, s.Clockwise)
                : Shapes.SpiralAlong(path, s.Radius, s.Stepover, s.Clockwise, PointTolerance / 2);
            if (result != null) created.Add(new PolylineEntity(result));
        }
        if (created.Count == 0) return;
        var doc = _session.Document;
        _session.Execute(title, () => created.ForEach(c => doc.AddEntity(c, doc.ActiveLayer)));
        _session.Select(created);
        Log(F("{0}: {1} object(s) created.", title, created.Count));
    }

    /// <summary>Draw ▸ More ▸ Tangent: shows the lines touching the two selected circles, arcs or points.</summary>
    private void DrawTangent()
    {
        var sel = Selected;
        if (sel.Count != 2 || TangentCircle(sel[0]) is not { } a || TangentCircle(sel[1]) is not { } b)
        {
            Log(T("Tangent: select two circles, arcs or single points first."));
            return;
        }
        var lines = Shapes.Tangents(a.Center, a.Radius, b.Center, b.Radius)
            .Where(t => a.Touches(t.A) && b.Touches(t.B)).ToList();
        if (lines.Count == 0)
        {
            Log(T("Tangent: no line touches both (one lies inside the other)."));
            return;
        }
        View.SetTool(new TangentTool(lines));
        View.Focus();
    }

    /// <summary>A circle for tangents: its centre and radius, and whether a point on the circle lies on the object.</summary>
    private sealed record TangentShape(Point2 Center, double Radius, Arc2? Arc)
    {
        public bool Touches(Point2 p) => Arc is not { } arc || Radius < 1e-9 || arc.ContainsAngle((p - Center).Angle);
    }

    private static TangentShape? TangentCircle(Entity e)
    {
        if (e is PointListEntity pl)
            return pl.WorldPoints().ToList() is [var p] ? new TangentShape(p.XY, 0, null) : null;
        if (e is not (CircleEntity or ArcEntity)) return null;
        var segments = e.ToPaths().SelectMany(p => p.Segments()).Where(s => s.IsArc).ToList();
        if (segments.Count == 0) return null;
        var first = segments[0].ToArc();
        if (e is CircleEntity) return new TangentShape(first.Center, first.Radius, null);
        // One arc may come as several segments: the whole sweep from the first to the last.
        var sweep = segments.Sum(s => s.ToArc().Sweep);
        return new TangentShape(first.Center, first.Radius, new Arc2(first.Center, first.Radius, first.StartAngle, sweep));
    }
}
