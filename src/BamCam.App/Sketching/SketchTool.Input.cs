using Avalonia;
using Avalonia.Input;
using BamCam.Core.Geometry;
using BamCam.Core.Sketching;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Sketching;

public sealed partial class SketchTool
{
    /// <summary>What lies under the cursor: a point, a geometry or a constraint / dimension (by id).</summary>
    private readonly record struct Hit(int? Point, int? Geo, int? Constraint)
    {
        public int? Id => Constraint ?? Point ?? Geo;
    }

    /// <summary>Where a click lands while drawing: the position and the sketch point or geometry it snapped to.</summary>
    private readonly record struct Snapped(Point2 Pos, int? Point, int? Geo);

    private enum DragKind { None, Points, Radius, Label, Box }

    // Drawing state.
    private Snapped? _first;
    private Snapped? _second;
    private Snapped? _lineStart;
    private Point2 _cursor;
    private Point _cursorScreen;
    private Hit _hover;

    // Dragging state.
    private DragKind _drag;
    private Point _pressScreen;
    private Point2 _pressLocal;
    private bool _moved;
    private Hit _pressHit;
    private Dictionary<int, Point2> _dragStart = [];
    private int _dragGeo;
    private (double X, double Y) _labelStart;

    private void ResetDrawing()
    {
        _first = null;
        _second = null;
        _lineStart = null;
    }

    // ------------------------------------------------------------------ hit testing

    private Hit HitAt(Point screen)
    {
        foreach (var (rect, id) in _labels)
            if (rect.Inflate(2).Contains(screen)) return new Hit(null, null, id);
        int? point = null;
        var best = 7.0;
        foreach (var p in _s.Points)
        {
            var d = Distance(ToScreen(p.Position), screen);
            if (d < best)
            {
                best = d;
                point = p.Id;
            }
        }
        if (point != null) return new Hit(point, null, null);
        int? geo = null;
        var bestGeo = Pixels(6);
        foreach (var g in _s.Geometry.Where(g => g.Kind != SketchGeometryKind.Point))
        {
            var d = _s.DistanceTo(g, ToLocal(Host.View.ToWorld(screen)));
            if (d < bestGeo)
            {
                bestGeo = d;
                geo = g.Id;
            }
        }
        return new Hit(null, geo, null);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>A click while drawing: onto a sketch point if one is near, else onto geometry, else the (grid) point.</summary>
    private Snapped SnapAt(Point screen, Point2 world)
    {
        var hit = HitAt(screen);
        if (hit.Point is { } p) return new Snapped(_s.P(p), p, null);
        if (hit.Geo is { } g && _s.Geo(g) is { } geo)
        {
            // On the geometry: the nearest point of it, so the constraint holds at once.
            var local = ToLocal(Host.View.ToWorld(screen));
            return new Snapped(Project(geo, local), null, g);
        }
        return new Snapped(ToLocal(world), null, null);
    }

    private Point2 Project(SketchGeometry g, Point2 p)
    {
        switch (g.Kind)
        {
            case SketchGeometryKind.Line:
            {
                var a = _s.P(g.Points[0]);
                var d = _s.P(g.Points[1]) - a;
                var len2 = Point2.Dot(d, d);
                return len2 < 1e-24 ? a : a + d * Math.Clamp(Point2.Dot(p - a, d) / len2, 0, 1);
            }
            case SketchGeometryKind.Circle or SketchGeometryKind.Arc:
            {
                var (c, r) = _s.CircleOf(g);
                var dir = p - c;
                return dir.Length < 1e-12 ? c + new Point2(r, 0) : c + dir.Normalized() * r;
            }
        }
        return p;
    }

    // ------------------------------------------------------------------ building geometry

    /// <summary>Ties a new point to the point or geometry it was clicked on.</summary>
    private void Attach(List<int> added, int newPoint, Snapped at)
    {
        if (at.Point is { } p) added.Add(_s.AddConstraint(ConstraintKind.Coincident, [newPoint, p]).Id);
        else if (at.Geo is { } g) added.Add(_s.AddConstraint(ConstraintKind.PointOnObject, [newPoint, g]).Id);
    }

    /// <summary>Settles the automatic constraints quietly (those that repeat others are dropped) and shows the result.</summary>
    private void Built(List<int> added)
    {
        _result = SketchCommands.Settle(_s, added, out _);
        AfterChange(solve: false);
    }

    /// <summary>Nearly horizontal or vertical (within 2°): made exact and constrained.</summary>
    private Point2 Straighten(Point2 a, Point2 b, out ConstraintKind? kind)
    {
        kind = null;
        var d = b - a;
        if (d.Length < 1e-9) return b;
        var limit = Math.Tan(2 * Math.PI / 180);
        if (Math.Abs(d.Y) <= limit * Math.Abs(d.X))
        {
            kind = ConstraintKind.Horizontal;
            return new Point2(b.X, a.Y);
        }
        if (Math.Abs(d.X) <= limit * Math.Abs(d.Y))
        {
            kind = ConstraintKind.Vertical;
            return new Point2(a.X, b.Y);
        }
        return b;
    }

    private SketchGeometry AddLine(Snapped a, Snapped b, List<int> added, bool straighten = true)
    {
        ConstraintKind? hv = null;
        var end = straighten ? Straighten(a.Pos, b.Pos, out hv) : b.Pos;
        // On an existing point the end stays there; the solver makes it exact.
        if (b.Point != null) end = b.Pos;
        var line = _s.AddLine(a.Pos, end, ConstructionMode);
        Attach(added, line.Points[0], a);
        Attach(added, line.Points[1], b with { Pos = end });
        if (hv is { } k) added.Add(_s.AddConstraint(k, [line.Id]).Id);
        return line;
    }

    private void Click(Snapped at)
    {
        switch (Mode)
        {
            case SketchMode.Point:
            {
                Remember();
                var added = new List<int>();
                var g = _s.AddPointGeometry(at.Pos, ConstructionMode);
                Attach(added, g.Points[0], at);
                Built(added);
                break;
            }
            case SketchMode.Line:
                if (_lineStart is not { } start)
                {
                    _lineStart = at;
                    break;
                }
                if (Point2.Distance(start.Pos, at.Pos) < Pixels(2)) break;
                {
                    Remember();
                    var added = new List<int>();
                    var line = AddLine(start, at, added);
                    Built(added);
                    // The chain goes on from the end of this line, unless it ended on an existing point (closed).
                    _lineStart = at.Point != null ? null : new Snapped(_s.P(line.Points[1]), line.Points[1], null);
                }
                break;
            case SketchMode.Rectangle:
                if (_first is not { } corner)
                {
                    _first = at;
                    break;
                }
                if (Math.Abs(corner.Pos.X - at.Pos.X) < Pixels(2) || Math.Abs(corner.Pos.Y - at.Pos.Y) < Pixels(2)) break;
                {
                    Remember();
                    var added = new List<int>();
                    var a = corner.Pos;
                    var c = at.Pos;
                    Point2[] p = [a, new(c.X, a.Y), c, new(a.X, c.Y)];
                    var lines = new SketchGeometry[4];
                    for (var i = 0; i < 4; i++) lines[i] = _s.AddLine(p[i], p[(i + 1) % 4], ConstructionMode);
                    for (var i = 0; i < 4; i++)
                        added.Add(_s.AddConstraint(ConstraintKind.Coincident, [lines[i].Points[1], lines[(i + 1) % 4].Points[0]]).Id);
                    added.Add(_s.AddConstraint(ConstraintKind.Horizontal, [lines[0].Id]).Id);
                    added.Add(_s.AddConstraint(ConstraintKind.Horizontal, [lines[2].Id]).Id);
                    added.Add(_s.AddConstraint(ConstraintKind.Vertical, [lines[1].Id]).Id);
                    added.Add(_s.AddConstraint(ConstraintKind.Vertical, [lines[3].Id]).Id);
                    Attach(added, lines[0].Points[0], corner);
                    Attach(added, lines[1].Points[1], at);
                    Built(added);
                    _first = null;
                }
                break;
            case SketchMode.Circle:
                if (_first is not { } center)
                {
                    _first = at;
                    break;
                }
                {
                    var r = Point2.Distance(center.Pos, at.Pos);
                    if (r < Pixels(2)) break;
                    Remember();
                    var added = new List<int>();
                    var circle = _s.AddCircle(center.Pos, r, ConstructionMode);
                    Attach(added, circle.Points[0], center);
                    Built(added);
                    _first = null;
                }
                break;
            case SketchMode.Arc:
                if (_first is not { } arcCenter)
                {
                    _first = at;
                    break;
                }
                if (_second is not { } arcStart)
                {
                    if (Point2.Distance(arcCenter.Pos, at.Pos) < Pixels(2)) break;
                    _second = at;
                    break;
                }
                {
                    if (Point2.Distance(arcCenter.Pos, at.Pos) < Pixels(2)) break;
                    Remember();
                    var added = new List<int>();
                    var arc = _s.AddArc(arcCenter.Pos, arcStart.Pos, at.Pos, ConstructionMode);
                    Attach(added, arc.Points[0], arcCenter);
                    Attach(added, arc.Points[1], arcStart);
                    Attach(added, arc.Points[2], at with { Pos = _s.P(arc.Points[2]) });
                    Built(added);
                    _first = null;
                    _second = null;
                }
                break;
        }
        UpdatePrompt();
        Host.Invalidate();
    }

    // ------------------------------------------------------------------ pointer

    public override void PointerPressed(Point screen, Point2 world, PointerPointProperties props, KeyModifiers mods)
    {
        if (!props.IsLeftButtonPressed) return;
        if (Mode != SketchMode.Select)
        {
            Click(SnapAt(screen, world));
            return;
        }
        _pressScreen = screen;
        _pressLocal = ToLocal(Host.View.ToWorld(screen));
        _moved = false;
        _pressHit = HitAt(screen);
        _drag = DragKind.None;
        if (_pressHit.Constraint is { } cid && _s.Constraint(cid) is { IsDimension: true } dim)
        {
            _drag = DragKind.Label;
            _labelStart = (dim.LabelX, dim.LabelY);
        }
        else if (_pressHit.Point is { } pid && _s.Point(pid) is { Locked: false })
        {
            _drag = DragKind.Points;
            _dragStart = new() { [pid] = _s.P(pid) };
        }
        else if (_pressHit.Geo is { } gid && _s.Geo(gid) is { } g)
        {
            if (g.Kind == SketchGeometryKind.Circle)
            {
                _drag = DragKind.Radius;
                _dragGeo = gid;
            }
            else
            {
                var pts = g.Kind == SketchGeometryKind.Arc ? [g.Points[0]] : g.Points;
                _dragStart = pts.Where(p => _s.Point(p) is { Locked: false }).ToDictionary(p => p, _s.P);
                _drag = _dragStart.Count > 0 ? DragKind.Points : DragKind.None;
            }
        }
        else if (_pressHit.Id == null)
        {
            _drag = DragKind.Box;
        }
    }

    public override void PointerMoved(Point screen, Point2 world, KeyModifiers mods)
    {
        _cursorScreen = screen;
        _cursor = Mode == SketchMode.Select ? ToLocal(Host.View.ToWorld(screen)) : SnapAt(screen, world).Pos;
        if (Mode == SketchMode.Select && _drag == DragKind.None) _hover = HitAt(screen);
        if (Mode == SketchMode.Select && _drag != DragKind.None)
        {
            if (!_moved && Distance(screen, _pressScreen) < 3)
            {
                Host.Invalidate();
                return;
            }
            if (!_moved && _drag != DragKind.Box) Remember();
            _moved = true;
            var delta = _cursor - _pressLocal;
            switch (_drag)
            {
                case DragKind.Points:
                    _result = SketchSolver.Solve(_s, _dragStart.ToDictionary(kv => kv.Key, kv => kv.Value + delta));
                    AfterChange(solve: false, notify: false);
                    break;
                case DragKind.Radius when _s.Geo(_dragGeo) is { } circle:
                    circle.Radius = Math.Max(Point2.Distance(_s.P(circle.Points[0]), _cursor), Pixels(1));
                    _result = SketchSolver.Solve(_s);
                    AfterChange(solve: false, notify: false);
                    break;
                case DragKind.Label when _pressHit.Constraint is { } cid && _s.Constraint(cid) is { } dim:
                    dim.LabelX = _labelStart.X + delta.X;
                    dim.LabelY = _labelStart.Y + delta.Y;
                    AfterChange(solve: false, notify: false);
                    break;
            }
        }
        Host.Invalidate();
    }

    public override void PointerReleased(Point screen, Point2 world, MouseButton button, KeyModifiers mods)
    {
        if (button != MouseButton.Left || Mode != SketchMode.Select) return;
        var add = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift);
        if (_drag == DragKind.Box && _moved)
        {
            var a = _pressLocal;
            var b = _cursor;
            bool Inside(Point2 p) => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
            if (!add) _selection.Clear();
            foreach (var g in _s.Geometry.Where(g => g.Kind != SketchGeometryKind.Point && g.Points.All(p => Inside(_s.P(p)))
                         && (g.Kind != SketchGeometryKind.Circle || Inside(_s.P(g.Points[0]) + new Point2(g.Radius, g.Radius)) && Inside(_s.P(g.Points[0]) - new Point2(g.Radius, g.Radius)))))
                _selection.Add(g.Id);
            foreach (var g in _s.Geometry.Where(g => g.Kind == SketchGeometryKind.Point && Inside(_s.P(g.Points[0]))))
                _selection.Add(g.Id);
        }
        else if (_moved)
        {
            AfterChange(solve: false);
        }
        else
        {
            // A click selects (Ctrl / Shift adds or removes).
            if (_pressHit.Id is { } id)
            {
                if (!add) _selection.Clear();
                if (!_selection.Add(id)) _selection.Remove(id);
            }
            else if (!add)
            {
                _selection.Clear();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        _drag = DragKind.None;
        _moved = false;
        Host.Invalidate();
    }

    public override bool DoubleClick(Point screen, Point2 world)
    {
        if (Mode != SketchMode.Select || HitAt(screen).Constraint is not { } id || _s.Constraint(id) is not { IsDimension: true } dim) return false;
        _ = EditDimension(dim.Id);
        return true;
    }

    private async Task EditDimension(int id)
    {
        if (_s.Constraint(id) is not { } dim) return;
        var current = dim.Reference ? SketchCommands.Format(_s.Measure(dim)) : dim.Expression ?? "";
        var value = await _ask(T("Dimension"), T("Value or formula (variables of the sketch can be used); empty = reference dimension:"), current);
        if (value == null || _s.Constraint(id) is not { } c) return;
        Remember();
        if (value.Trim().Length == 0)
        {
            c.Reference = true;
            AfterChange(solve: true);
            return;
        }
        var wasReference = c.Reference;
        c.Expression = value.Trim();
        c.Reference = false;
        if (wasReference)
        {
            // A reference dimension made driving: kept as reference when it would over-constrain.
            _result = SketchCommands.Settle(_s, [c.Id], out var error);
            if (error != null) _log(error);
        }
        else
        {
            // A changed value; one the other constraints do not allow is not taken.
            _result = SketchSolver.Solve(_s);
            if (!_result.Solved)
            {
                _s = Sketch.Deserialize(_undo[^1]);
                _undo.RemoveAt(_undo.Count - 1);
                _result = SketchSolver.Solve(_s);
                _log(F("The value '{0}' cannot be reached with the other constraints, or the formula has an error: not changed.", value.Trim()));
            }
        }
        AfterChange(solve: false);
    }

    /// <summary>Default place of a new dimension's label: a little to the side of what it measures.</summary>
    private void PlaceLabel(Sketch s, SketchConstraint c)
    {
        var offset = Pixels(30);
        // Outside: away from the middle of the sketch.
        var others = s.Points.Where(p => p.Id != Sketch.OriginId).ToList();
        var centre = others.Count == 0 ? default : new Point2(others.Average(p => p.X), others.Average(p => p.Y));
        switch (c.Kind)
        {
            case ConstraintKind.Distance or ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance:
            {
                var (a, b) = c.Refs.Length == 2 && s.Geo(c.Refs[1]) is { Kind: SketchGeometryKind.Line } line
                    ? (s.P(s.PointOf(c.Refs[0])), Project(s, line, s.P(s.PointOf(c.Refs[0]))))
                    : s.Ends(c.Refs);
                var d = c.Kind == ConstraintKind.HorizontalDistance ? new Point2(1, 0)
                    : c.Kind == ConstraintKind.VerticalDistance ? new Point2(0, 1)
                    : (b - a).Length < 1e-12 ? new Point2(1, 0) : (b - a).Normalized();
                var n = new Point2(-d.Y, d.X);
                if (Point2.Dot(n, (a + b) * 0.5 - centre) < 0) n = new Point2(-n.X, -n.Y);
                c.LabelX = n.X * offset;
                c.LabelY = n.Y * offset;
                break;
            }
            case ConstraintKind.Radius or ConstraintKind.Diameter when s.Geo(c.Refs[0]) is { } g:
            {
                // Beyond the rim, at 45°.
                var r = s.CircleOf(g).Radius;
                var dir = new Point2(Math.Sqrt(0.5), Math.Sqrt(0.5));
                c.LabelX = dir.X * (r + offset);
                c.LabelY = dir.Y * (r + offset);
                break;
            }
            default:
                c.LabelX = offset;
                c.LabelY = offset;
                break;
        }
    }

    private static Point2 Project(Sketch s, SketchGeometry line, Point2 p)
    {
        var a = s.P(line.Points[0]);
        var d = s.P(line.Points[1]) - a;
        var len2 = Point2.Dot(d, d);
        return len2 < 1e-24 ? a : a + d * (Point2.Dot(p - a, d) / len2);
    }
}
