using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BamCam.App.Drawing;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Sketching;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Sketching;

/// <summary>
/// What the sketch mode is doing: selecting and dragging, or drawing a kind of geometry. Arc: start, end, a point on
/// the arc (as Draw ▸ Arc); ArcCenter: centre, start, end.
/// </summary>
public enum SketchMode { Select, Line, Rectangle, Circle, Arc, ArcCenter, Point }

/// <summary>
/// The sketch mode in the drawing view: draws lines, rectangles, circles, arcs and points into a
/// <see cref="SketchEntity"/>, adds constraints and dimensions to the selection, drags geometry with the solver and
/// shows how far the sketch is determined (green = fully, blue = can still move, red = contradicting). Finish keeps
/// the changes as one undo step, Cancel drops them.
/// </summary>
public sealed partial class SketchTool : ViewTool
{
    private readonly SketchEntity _entity;
    private readonly string _originalData;
    private readonly Func<string, string, string, Task<string?>> _ask;
    private readonly Func<string, Task<string?>> _editText;
    private readonly Action<string> _log;
    private readonly Matrix4 _m;
    private readonly bool _isNew;

    private Sketch _s;
    private SketchSolveResult _result;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private readonly HashSet<int> _selection = [];

    public SketchMode Mode { get; private set; } = SketchMode.Select;

    /// <summary>New geometry is construction geometry (helps with constraints, not part of the outlines).</summary>
    public bool ConstructionMode { get; private set; }

    /// <summary>Raised when the mode, the construction switch or the status changed (the sketch bar follows).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the sketch mode ends.</summary>
    public event EventHandler? Ended;

    public SketchTool(SketchEntity entity, bool isNew, Func<string, string, string, Task<string?>> ask, Func<string, Task<string?>> editText, Action<string> log)
    {
        _entity = entity;
        _isNew = isNew;
        _originalData = entity.Data;
        _ask = ask;
        _editText = editText;
        _log = log;
        _m = entity.Transform;
        _s = entity.GetSketch();
        _result = SketchSolver.Solve(_s);
    }

    public SketchEntity Entity => _entity;

    // While dragging a point, the snapped point is shown as when drawing.
    public override bool PicksPoints => Mode != SketchMode.Select || _drag is DragKind.Points or DragKind.Radius;

    public override bool WantsPlainKeys => true;

    public SketchSolveResult Result => _result;

    public override BoundingBox? ExtraBounds => _entity.Bounds();

    // ------------------------------------------------------------------ coordinates

    private Point2 ToLocal(Point2 world)
    {
        double a = _m.M11, b = _m.M12, c = _m.M21, d = _m.M22, e = _m.M41, f = _m.M42;
        var det = a * d - b * c;
        if (Math.Abs(det) < 1e-15) return world;
        var x = world.X - e;
        var y = world.Y - f;
        return new Point2((d * x - c * y) / det, (-b * x + a * y) / det);
    }

    private Point2 ToWorld(Point2 local) => _m.Transform(local);

    private Point ToScreen(Point2 local) => Host.View.ToScreen(ToWorld(local));

    /// <summary>A distance in screen pixels as sketch units.</summary>
    private double Pixels(double px) => Host.View.ToWorldLength(px) / Math.Max(_m.ScaleXY, 1e-12);

    // ------------------------------------------------------------------ state

    private void Remember()
    {
        _undo.Add(_s.Serialize());
        _redo.Clear();
        if (_undo.Count > 200) _undo.RemoveAt(0);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_s.Serialize());
        _s = Sketch.Deserialize(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        AfterChange(solve: true);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_s.Serialize());
        _s = Sketch.Deserialize(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        AfterChange(solve: true);
    }

    /// <summary>Shows the current sketch in the drawing (outlines of the object) and the status.</summary>
    private void AfterChange(bool solve, bool notify = true)
    {
        if (solve) _result = SketchSolver.Solve(_s);
        _selection.RemoveWhere(id => _s.Point(id) == null && _s.Geo(id) == null && _s.Constraint(id) == null);
        _entity.SetSketch(_s);
        if (notify) Host.Session.NotifyChanged();
        UpdatePrompt();
        Host.Invalidate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The status of the sketch for the prompt and the sketch bar.</summary>
    public string StatusText
    {
        get
        {
            if (_result.ExpressionErrors.Count > 0) return F("Formula error: {0}", _result.ExpressionErrors.Values.First());
            if (_result.VariableErrors.Count > 0) return F("Variable error: {0}", _result.VariableErrors.Values.First());
            return _result.Status switch
            {
                SketchStatus.FullyConstrained => T("Fully constrained"),
                SketchStatus.OverConstrained => _result.Conflicting.Count > 0 ? T("Over-constrained: constraints contradict each other (red)")
                    : T("Over-constrained: some constraints repeat others (red)"),
                _ => F("{0} degrees of freedom", _result.DegreesOfFreedom),
            };
        }
    }

    public override string Prompt => (Mode switch
    {
        SketchMode.Line => _lineStart == null ? T("Sketch line: click the start point") : T("Sketch line: click the next point  [Enter / Esc = end the chain]"),
        SketchMode.Rectangle => _first == null ? T("Sketch rectangle: click the first corner") : T("Sketch rectangle: click the opposite corner"),
        SketchMode.Circle => _first == null ? T("Sketch circle: click the centre") : T("Sketch circle: click a point on the circle"),
        SketchMode.Arc => _first == null ? T("Sketch arc: click the start point") : _second == null ? T("Sketch arc: click the end point") : T("Sketch arc: click a point on the arc"),
        SketchMode.ArcCenter => _first == null ? T("Sketch arc: click the centre") : _second == null ? T("Sketch arc: click the start point") : T("Sketch arc: click the end point (counter-clockwise)"),
        SketchMode.Point => T("Sketch point: click a point"),
        _ => T("Sketch: select and drag geometry; double-click a dimension to change it  [Enter = finish, Del = delete]"),
    }) + "    — " + StatusText;

    // A finished or cancelled sketch writes no prompt: Esc finishes and then updated the prompt, which put the sketch's
    // text back into the prompt bar after the next tool had cleared it.
    private void UpdatePrompt()
    {
        if (!_closed) Host.SetPrompt(Prompt);
    }

    private bool _wasHidden;

    protected override void OnAttached()
    {
        // The sketch mode draws the sketch itself (coloured by how far it is determined): the object is hidden meanwhile.
        _wasHidden = _entity.Hidden;
        _entity.Hidden = true;
        Host.Session.ClearSelection();
        UpdatePrompt();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void OnDetached()
    {
        // Another tool was started: the sketch is kept as if finished.
        if (!_closed)
        {
            _closed = true;
            Commit();
        }
        Ended?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ commands (sketch bar)

    public void SetMode(SketchMode mode)
    {
        Mode = mode;
        ResetDrawing();
        if (mode != SketchMode.Select) _selection.Clear();
        UpdatePrompt();
        Host.Invalidate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleConstruction()
    {
        var geometry = _selection.Select(_s.Geo).Where(g => g is { Kind: not SketchGeometryKind.Point }).Cast<SketchGeometry>().ToList();
        if (geometry.Count > 0)
        {
            Remember();
            var to = !geometry.All(g => g.Construction);
            foreach (var g in geometry) g.Construction = to;
            AfterChange(solve: false);
            return;
        }
        ConstructionMode = !ConstructionMode;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a constraint of <paramref name="kind"/> to the selection.</summary>
    public void Constrain(ConstraintKind kind)
    {
        Remember();
        var made = SketchCommands.AddConstraint(_s, kind, _selection.ToList(), out var error);
        if (made.Count == 0)
        {
            _undo.RemoveAt(_undo.Count - 1);
            _log(error ?? T("The selection does not fit this constraint."));
            return;
        }
        _result = SketchCommands.Settle(_s, made, out var settleError);
        if (settleError != null) _log(settleError);
        AfterChange(solve: false);
    }

    /// <summary>Adds a dimension to the selection and asks for its value.</summary>
    public async void Dimension(ConstraintKind? kind = null)
    {
        var copy = _s.Clone();
        var c = SketchCommands.AddDimension(copy, _selection.ToList(), kind, out var error);
        if (c == null)
        {
            _log(error ?? "");
            return;
        }
        PlaceLabel(copy, c);
        var value = await _ask(T("Dimension"), T("Value or formula (variables of the sketch can be used):"), c.Expression ?? "");
        if (value == null) return;
        Remember();
        c.Expression = value.Trim();
        _s = copy;
        _result = SketchCommands.Settle(_s, [c.Id], out var settleError);
        if (settleError != null) _log(settleError);
        AfterChange(solve: false);
    }

    private static string _lastFillet = "2";
    private static string _lastChamfer = "1";

    /// <summary>Rounds or chamfers the selected corners; asks for the radius or the chamfer length.</summary>
    public async void RoundCorners(bool chamfer)
    {
        var selection = _selection.ToList();
        var size = await _ask(chamfer ? T("Chamfer") : T("Fillet"),
            chamfer ? T("Chamfer length along each line (value or formula):") : T("Radius (value or formula):"),
            chamfer ? _lastChamfer : _lastFillet);
        if (size == null || size.Trim().Length == 0) return;
        size = size.Trim();
        Remember();
        var made = SketchCommands.RoundCorners(_s, selection, size, chamfer, out var error);
        if (made.Count == 0)
        {
            _undo.RemoveAt(_undo.Count - 1);
            _log(error ?? "");
            return;
        }
        if (error != null) _log(error);
        if (chamfer) _lastChamfer = size;
        else _lastFillet = size;
        _selection.Clear();
        _result = SketchCommands.Settle(_s, made, out var settleError);
        if (settleError != null) _log(settleError);
        AfterChange(solve: false);
    }

    /// <summary>Deletes the selected geometry and constraints.</summary>
    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        Remember();
        // Selected points delete the geometry they belong to.
        var ids = _selection.SelectMany(id => _s.IsPoint(id) ? _s.GeometryOf(id).Select(g => g.Id) : [id]).ToList();
        _s.Remove(ids);
        _selection.Clear();
        AfterChange(solve: true);
    }

    /// <summary>Edits the variables of the sketch as lines "name = formula".</summary>
    public async void EditVariables()
    {
        var text = string.Join("\n", _s.Variables.Select(v => $"{v.Name} = {v.Expression}"));
        var edited = await _editText(text);
        if (edited == null) return;
        var list = new List<SketchVariable>();
        foreach (var raw in edited.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            var eq = line.IndexOf('=');
            var name = (eq < 0 ? line : line[..eq]).Trim();
            var expr = eq < 0 ? "0" : line[(eq + 1)..].Trim();
            if (!SketchExpression.IsValidName(name))
            {
                _log(F("'{0}' is not a valid variable name (letters, digits and _, starting with a letter).", name));
                continue;
            }
            list.Add(new SketchVariable { Name = name, Expression = expr.Length == 0 ? "0" : expr });
        }
        Remember();
        _s.Variables = list;
        AfterChange(solve: true);
        foreach (var (name, err) in _result.VariableErrors) _log(F("Variable {0}: {1}", name, err));
    }

    private bool _closed;

    /// <summary>Keeps the sketch (one undo step) and leaves the sketch mode.</summary>
    public void Finish()
    {
        _closed = true;
        Commit();
        Host.EndTool();
    }

    /// <summary>
    /// Stores the sketch as one undo step (the live updates while editing are not recorded); an empty new sketch is
    /// removed.
    /// </summary>
    private void Commit()
    {
        ResetDrawing();
        var empty = !_s.Geometry.Any(g => !g.Points.Contains(Sketch.OriginId));
        var data = _s.Serialize();
        _entity.Data = _originalData;
        _entity.Hidden = _wasHidden;
        var doc = Host.Session.Document;
        if (empty && _isNew)
            Host.Session.Execute(T("Delete empty sketch"), () => doc.RemoveEntity(_entity));
        else if (data != _originalData)
            Host.Session.Execute(T("Edit sketch"), () => _entity.Data = data);
        else
            Host.Session.NotifyChanged();
    }

    /// <summary>Drops the changes and leaves the sketch mode.</summary>
    public void Cancel()
    {
        _closed = true;
        ResetDrawing();
        _entity.Data = _originalData;
        _entity.Hidden = _wasHidden;
        if (_isNew && !_s.Geometry.Any(g => !g.Points.Contains(Sketch.OriginId)))
        {
            var doc = Host.Session.Document;
            Host.Session.Execute(T("Delete empty sketch"), () => doc.RemoveEntity(_entity));
        }
        else
        {
            Host.Session.NotifyChanged();
        }
        Host.EndTool();
    }

    // ------------------------------------------------------------------ keys

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (mods == KeyModifiers.Control && key == Key.Z) { Undo(); return true; }
        if (mods == KeyModifiers.Control && key == Key.Y) { Redo(); return true; }
        if (mods != KeyModifiers.None && mods != KeyModifiers.Shift) return false;
        switch (key)
        {
            case Key.Escape:
                if (Mode == SketchMode.Line && _lineStart != null || _first != null) ResetDrawing();
                else if (Mode != SketchMode.Select) SetMode(SketchMode.Select);
                else if (_selection.Count > 0) _selection.Clear();
                else
                {
                    Finish();
                    return true;
                }
                UpdatePrompt();
                Host.Invalidate();
                return true;
            case Key.Enter or Key.Return:
                if (Mode == SketchMode.Line && _lineStart != null) { ResetDrawing(); UpdatePrompt(); Host.Invalidate(); }
                else Finish();
                return true;
            case Key.Delete or Key.Back: DeleteSelection(); return true;
            case Key.S when mods == KeyModifiers.None && Mode != SketchMode.Select: SetMode(SketchMode.Select); return true;
            case Key.L: SetMode(SketchMode.Line); return true;
            case Key.R: SetMode(SketchMode.Rectangle); return true;
            case Key.C: SetMode(SketchMode.Circle); return true;
            case Key.A: SetMode(mods == KeyModifiers.Shift ? SketchMode.ArcCenter : SketchMode.Arc); return true;
            case Key.P: SetMode(SketchMode.Point); return true;
            case Key.X: ToggleConstruction(); return true;
            case Key.K: Constrain(ConstraintKind.Coincident); return true;
            case Key.H: Constrain(ConstraintKind.Horizontal); return true;
            case Key.V: Constrain(ConstraintKind.Vertical); return true;
            case Key.T: Constrain(ConstraintKind.Tangent); return true;
            case Key.E: Constrain(ConstraintKind.Equal); return true;
            case Key.F: Constrain(ConstraintKind.Fix); return true;
            case Key.M: Constrain(ConstraintKind.Midpoint); return true;
            case Key.D: Dimension(); return true;
            case Key.G: RoundCorners(chamfer: false); return true;
            case Key.N: RoundCorners(chamfer: true); return true;
        }
        return false;
    }
}
