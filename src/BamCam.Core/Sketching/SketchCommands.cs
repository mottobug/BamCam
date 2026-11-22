using System.Globalization;
using BamCam.Core.Geometry;
using static BamCam.Core.Localization.Tr;

namespace BamCam.Core.Sketching;

/// <summary>
/// Constraints and dimensions made from what is selected in a sketch (ids of points, geometry and constraints), with
/// the side and direction data they need taken from the current positions.
/// </summary>
public static class SketchCommands
{
    private static bool IsLine(Sketch s, int id) => s.Geo(id) is { Kind: SketchGeometryKind.Line };
    private static bool IsRound(Sketch s, int id) => s.Geo(id) is { Kind: SketchGeometryKind.Circle or SketchGeometryKind.Arc };
    private static bool IsPointLike(Sketch s, int id) => s.IsPoint(id) || s.Geo(id) is { Kind: SketchGeometryKind.Point };

    /// <summary>
    /// Adds the constraints of <paramref name="kind"/> for the selection (no dimensions). Returns the new constraint
    /// ids, or an empty list and the reason in <paramref name="error"/> when the selection does not fit.
    /// </summary>
    public static List<int> AddConstraint(Sketch s, ConstraintKind kind, IReadOnlyList<int> selection, out string? error)
    {
        error = null;
        var points = selection.Where(id => IsPointLike(s, id)).Select(s.PointOf).Distinct().ToList();
        var lines = selection.Where(id => IsLine(s, id)).ToList();
        var rounds = selection.Where(id => IsRound(s, id)).ToList();
        var geometry = selection.Where(id => s.Geo(id) is { Kind: not SketchGeometryKind.Point }).ToList();
        var made = new List<int>();
        int Add(ConstraintKind k, int[] refs, double[]? data = null) { var c = s.AddConstraint(k, refs, data); made.Add(c.Id); return c.Id; }

        switch (kind)
        {
            case ConstraintKind.Coincident:
                if (points.Count >= 2 && geometry.Count == 0)
                    for (var i = 1; i < points.Count; i++) Add(ConstraintKind.Coincident, [points[0], points[i]]);
                else if (points.Count >= 1 && geometry.Count == 1)
                    foreach (var p in points) Add(ConstraintKind.PointOnObject, [p, geometry[0]]);
                else error = T("Coincident: select two or more points, or points and one line, circle or arc.");
                break;
            case ConstraintKind.Horizontal or ConstraintKind.Vertical:
                if (lines.Count > 0 && points.Count == 0)
                    foreach (var l in lines) Add(kind, [l]);
                else if (points.Count == 2 && geometry.Count == 0)
                    Add(kind, [points[0], points[1]]);
                else error = kind == ConstraintKind.Horizontal ? T("Horizontal: select lines or two points.") : T("Vertical: select lines or two points.");
                break;
            case ConstraintKind.Parallel or ConstraintKind.Perpendicular:
                if (lines.Count >= 2 && lines.Count == geometry.Count && points.Count == 0)
                    for (var i = 1; i < lines.Count; i++) Add(kind, [lines[kind == ConstraintKind.Parallel ? 0 : i - 1], lines[i]]);
                else error = kind == ConstraintKind.Parallel ? T("Parallel: select two or more lines.") : T("Perpendicular: select two lines.");
                break;
            case ConstraintKind.Tangent:
                if (geometry.Count == 2 && rounds.Count >= 1 && points.Count == 0)
                {
                    var first = lines.Count == 1 ? lines[0] : rounds[0];
                    var second = lines.Count == 1 ? rounds[0] : rounds[1];
                    Add(ConstraintKind.Tangent, [first, second], TangentData(s, first, second));
                }
                else error = T("Tangent: select a line and a circle or arc, or two circles or arcs.");
                break;
            case ConstraintKind.Equal:
                if (lines.Count >= 2 && rounds.Count == 0)
                    for (var i = 1; i < lines.Count; i++) Add(ConstraintKind.Equal, [lines[0], lines[i]]);
                else if (rounds.Count >= 2 && lines.Count == 0)
                    for (var i = 1; i < rounds.Count; i++) Add(ConstraintKind.Equal, [rounds[0], rounds[i]]);
                else error = T("Equal: select two or more lines, or two or more circles and arcs.");
                break;
            case ConstraintKind.Fix:
            {
                var all = points.Concat(geometry.SelectMany(g => s.Geo(g)!.Points)).Distinct().Where(p => s.Point(p) is { Locked: false }).ToList();
                foreach (var p in all)
                {
                    var pos = s.P(p);
                    Add(ConstraintKind.Fix, [p], [pos.X, pos.Y]);
                }
                if (all.Count == 0) error = T("Fix: select points or geometry.");
                break;
            }
            case ConstraintKind.Midpoint:
                if (points.Count == 1 && lines.Count == 1 && geometry.Count == 1) Add(ConstraintKind.Midpoint, [points[0], lines[0]]);
                else error = T("Midpoint: select a point and a line.");
                break;
            case ConstraintKind.Symmetric:
                if (points.Count == 2 && lines.Count == 1 && geometry.Count == 1) Add(ConstraintKind.Symmetric, [points[0], points[1], lines[0]]);
                else error = T("Symmetric: select two points and the line of symmetry.");
                break;
            case ConstraintKind.PointOnObject:
                if (points.Count >= 1 && geometry.Count == 1)
                    foreach (var p in points) Add(ConstraintKind.PointOnObject, [p, geometry[0]]);
                else error = T("Point on object: select points and one line, circle or arc.");
                break;
            default:
                error = T("Use Dimension for dimensions.");
                break;
        }
        return made;
    }

    /// <summary>
    /// External or internal tangency and the side, from the current positions; when they touch at an end point of an
    /// arc that lies on the other geometry's end (a fillet, a line going on into an arc), also which end points touch
    /// (index into the points of the geometry), so that the solver can use the well-conditioned form there.
    /// </summary>
    public static double[] TangentData(Sketch s, int first, int second)
    {
        var g2 = s.Geo(second)!;
        var (c2, r2) = s.CircleOf(g2);
        var g1 = s.Geo(first)!;
        // Arc end points touching an end of the other geometry: [index in first, index in second].
        int Touch(SketchGeometry g, SketchGeometry other, out int otherIndex)
        {
            otherIndex = 0;
            int[] mine = g.Kind == SketchGeometryKind.Arc ? [1, 2] : g.Kind == SketchGeometryKind.Line ? [0, 1] : [];
            int[] theirs = other.Kind == SketchGeometryKind.Arc ? [1, 2] : other.Kind == SketchGeometryKind.Line ? [0, 1] : [];
            foreach (var i in mine)
            foreach (var j in theirs)
                if (Point2.Distance(s.P(g.Points[i]), s.P(other.Points[j])) < 1e-6)
                {
                    otherIndex = j;
                    return i;
                }
            return -1;
        }
        var i1 = Touch(g1, g2, out var i2);
        if (g1.Kind == SketchGeometryKind.Line)
        {
            var side = Sketch.SignedDistance(c2, s.P(g1.Points[0]), s.P(g1.Points[1]));
            return g2.Kind == SketchGeometryKind.Arc && i1 >= 0 ? [side < 0 ? -1 : 1, i2] : [side < 0 ? -1 : 1];
        }
        var (c1, r1) = s.CircleOf(g1);
        var dist = Point2.Distance(c1, c2);
        var external = Math.Abs(dist - (r1 + r2)) <= Math.Abs(dist - Math.Abs(r1 - r2));
        return i1 >= 0 && g1.Kind == SketchGeometryKind.Arc && g2.Kind == SketchGeometryKind.Arc
            ? [external ? 1 : -1, r1 >= r2 ? 1 : -1, i1, i2]
            : [external ? 1 : -1, r1 >= r2 ? 1 : -1];
    }

    /// <summary>
    /// A dimension for the selection, with its measured value as the expression: one line → its length, two points →
    /// their distance, a point and a line → the distance between them, a circle → its diameter, an arc → its radius,
    /// two lines → the angle between them (or their distance when they are parallel). <paramref name="kind"/> may ask
    /// for a horizontal or vertical distance, a radius or a diameter instead. Null and the reason when it does not fit.
    /// </summary>
    public static SketchConstraint? AddDimension(Sketch s, IReadOnlyList<int> selection, ConstraintKind? kind, out string? error)
    {
        error = null;
        var points = selection.Where(id => IsPointLike(s, id)).Select(s.PointOf).Distinct().ToList();
        var lines = selection.Where(id => IsLine(s, id)).ToList();
        var rounds = selection.Where(id => IsRound(s, id)).ToList();
        SketchConstraint? c = null;

        if (kind is ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance)
        {
            int[]? refs = points.Count == 2 && lines.Count == 0 && rounds.Count == 0 ? [points[0], points[1]]
                : lines.Count == 1 && points.Count == 0 && rounds.Count == 0 ? [lines[0]] : null;
            if (refs == null)
            {
                error = T("Horizontal / vertical distance: select two points or one line.");
                return null;
            }
            var (a, b) = s.Ends(refs);
            var d = kind == ConstraintKind.HorizontalDistance ? b.X - a.X : b.Y - a.Y;
            c = s.AddConstraint(kind.Value, refs, [d < 0 ? -1 : 1]);
        }
        else if (rounds.Count == 1 && points.Count == 0 && lines.Count == 0)
        {
            var g = s.Geo(rounds[0])!;
            var k = kind is ConstraintKind.Radius or ConstraintKind.Diameter ? kind.Value
                : g.Kind == SketchGeometryKind.Circle ? ConstraintKind.Diameter : ConstraintKind.Radius;
            c = s.AddConstraint(k, [g.Id]);
        }
        else if (lines.Count == 1 && points.Count == 0 && rounds.Count == 0)
            c = s.AddConstraint(ConstraintKind.Distance, [lines[0]]);
        else if (points.Count == 2 && lines.Count == 0 && rounds.Count == 0)
            c = s.AddConstraint(ConstraintKind.Distance, [points[0], points[1]]);
        else if (points.Count == 1 && lines.Count == 1 && rounds.Count == 0)
        {
            var l = s.Geo(lines[0])!;
            var side = Sketch.SignedDistance(s.P(points[0]), s.P(l.Points[0]), s.P(l.Points[1]));
            c = s.AddConstraint(ConstraintKind.Distance, [points[0], lines[0]], [side < 0 ? -1 : 1]);
        }
        else if (lines.Count == 2 && points.Count == 0 && rounds.Count == 0)
        {
            var l1 = s.Geo(lines[0])!;
            var l2 = s.Geo(lines[1])!;
            var d1 = s.P(l1.Points[1]) - s.P(l1.Points[0]);
            var d2 = s.P(l2.Points[1]) - s.P(l2.Points[0]);
            var cross = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(cross) < 1e-9 * d1.Length * d2.Length)
            {
                // Parallel: the distance of the second line's start from the first line.
                var side = Sketch.SignedDistance(s.P(l2.Points[0]), s.P(l1.Points[0]), s.P(l1.Points[1]));
                c = s.AddConstraint(ConstraintKind.Distance, [l2.Points[0], l1.Id], [side < 0 ? -1 : 1]);
            }
            else
                c = s.AddConstraint(ConstraintKind.Angle, [l1.Id, l2.Id], [cross < 0 ? -1 : 1]);
        }
        if (c == null)
        {
            error = T("Dimension: select a line, two points, a point and a line, a circle or arc, or two lines.");
            return null;
        }
        c.Expression = Format(s.Measure(c));
        return c;
    }

    /// <summary>A corner where two lines meet: the lines and their end points there.</summary>
    private readonly record struct Corner(SketchGeometry A, int PointA, SketchGeometry B, int PointB);

    /// <summary>The corners in the selection: selected points where two lines meet, or two selected lines that meet.</summary>
    private static List<Corner> Corners(Sketch s, IReadOnlyList<int> selection)
    {
        const double tol = 1e-6;
        var lines = s.Geometry.Where(g => g.Kind == SketchGeometryKind.Line).ToList();
        IEnumerable<(SketchGeometry Line, int Point)> Ends(SketchGeometry l) => [(l, l.Points[0]), (l, l.Points[1])];
        Corner? At(int point)
        {
            var here = lines.SelectMany(Ends).Where(e => Point2.Distance(s.P(e.Point), s.P(point)) <= tol).ToList();
            var a = here.FirstOrDefault(e => e.Point == point);
            if (a.Line == null) a = here.FirstOrDefault();
            var b = here.FirstOrDefault(e => e.Line != a.Line);
            return a.Line != null && b.Line != null && here.Count == 2 ? new Corner(a.Line, a.Point, b.Line, b.Point) : null;
        }
        var result = new List<Corner>();
        var selectedLines = selection.Select(s.Geo).Where(g => g is { Kind: SketchGeometryKind.Line }).Cast<SketchGeometry>().ToList();
        if (selectedLines.Count == 2)
        {
            foreach (var ea in Ends(selectedLines[0]))
            foreach (var eb in Ends(selectedLines[1]))
                if (Point2.Distance(s.P(ea.Point), s.P(eb.Point)) <= tol) result.Add(new Corner(ea.Line, ea.Point, eb.Line, eb.Point));
            return result;
        }
        foreach (var id in selection.Where(id => s.IsPoint(id) || s.Geo(id) is { Kind: SketchGeometryKind.Point }).Select(s.PointOf).Distinct())
            if (At(id) is { } c) result.Add(c);
        return result;
    }

    /// <summary>
    /// Rounds (<paramref name="chamfer"/> false) or chamfers the corners in the selection with <paramref name="size"/>
    /// (a number or formula: the radius, or the length cut off along each line). The lines are shortened, a tangent arc
    /// or a straight chamfer joins them, and the size becomes a driving dimension. The old corner stays as a
    /// construction point on both lines, and the constraints and dimensions that held the corner now hold it.
    /// Returns the ids of the new constraints; none and the reason when a corner cannot be rounded.
    /// </summary>
    public static List<int> RoundCorners(Sketch s, IReadOnlyList<int> selection, string size, bool chamfer, out string? error)
    {
        error = null;
        var corners = Corners(s, selection);
        if (corners.Count == 0)
        {
            error = T("Select corners: points where two lines meet, or two lines that meet.");
            return [];
        }
        double value;
        try
        {
            var variables = SketchExpression.EvaluateVariables(s.Variables, out _);
            value = SketchExpression.Evaluate(size, n => variables.TryGetValue(n, out var v) ? v : null);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return [];
        }
        if (value <= 0)
        {
            error = T("The size must be greater than 0.");
            return [];
        }

        var made = new List<int>();
        foreach (var (a, pa, b, pb) in corners)
        {
            var c = s.P(pa);
            var farA = s.P(a.Points[0] == pa ? a.Points[1] : a.Points[0]);
            var farB = s.P(b.Points[0] == pb ? b.Points[1] : b.Points[0]);
            var u = farA - c;
            var v = farB - c;
            if (u.Length < 1e-9 || v.Length < 1e-9) continue;
            var lenA = u.Length;
            var lenB = v.Length;
            u = u.Normalized();
            v = v.Normalized();
            var angle = Math.Acos(Math.Clamp(Point2.Dot(u, v), -1, 1));
            if (angle < 1e-6 || angle > Math.PI - 1e-6)
            {
                error = T("The lines at the corner are in line: nothing to round.");
                continue;
            }
            // Distance from the corner along each line to where the round or chamfer starts.
            var t = chamfer ? value : value / Math.Tan(angle / 2);
            if (t >= lenA - 1e-9 || t >= lenB - 1e-9)
            {
                error = T("The radius or chamfer is too large for the lines at the corner.");
                continue;
            }
            var ta = c + u * t;
            var tb = c + v * t;

            // The old corner as a construction point on both lines; constraints of the corner go to it.
            var corner = s.AddPointGeometry(c, construction: true);
            var cp = corner.Points[0];
            // A length of a shortened line is measured from the old corner on: to the far end.
            foreach (var k in s.Constraints.Where(k => k.Kind is ConstraintKind.Distance or ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance && k.Refs.Length == 1))
            {
                foreach (var (line, end) in new[] { (a, pa), (b, pb) })
                {
                    if (k.Refs[0] != line.Id) continue;
                    var far = line.Points[0] == end ? line.Points[1] : line.Points[0];
                    k.Refs = [end, far];
                    // Horizontal / vertical distances keep their direction: from the line's first point to its second.
                    if (line.Points[1] == end && k.Data.Length > 0) k.Data = [-k.Data[0]];
                    break;
                }
            }
            foreach (var k in s.Constraints)
            {
                if (k.Kind == ConstraintKind.Coincident && k.Refs.Select(s.PointOf).ToHashSet().SetEquals([pa, pb])) continue;
                k.Refs = k.Refs.Select(r => r == pa || r == pb ? cp : r).ToArray();
            }
            s.Constraints.RemoveAll(k => k.Kind == ConstraintKind.Coincident && k.Refs.Select(s.PointOf).ToHashSet().SetEquals([pa, pb]));
            // Coincident with itself after the redirection (both ends were tied to the same point): gone.
            s.Constraints.RemoveAll(k => k.Kind == ConstraintKind.Coincident && k.Refs.Select(s.PointOf).Distinct().Count() < 2);
            made.Add(s.AddConstraint(ConstraintKind.PointOnObject, [cp, a.Id]).Id);
            made.Add(s.AddConstraint(ConstraintKind.PointOnObject, [cp, b.Id]).Id);

            s.Point(pa)!.Position = ta;
            s.Point(pb)!.Position = tb;
            if (chamfer)
            {
                var line = s.AddLine(ta, tb, a.Construction && b.Construction);
                made.Add(s.AddConstraint(ConstraintKind.Coincident, [line.Points[0], pa]).Id);
                made.Add(s.AddConstraint(ConstraintKind.Coincident, [line.Points[1], pb]).Id);
                var da = s.AddConstraint(ConstraintKind.Distance, [cp, pa], expression: size);
                var db = s.AddConstraint(ConstraintKind.Distance, [cp, pb], expression: size);
                // The labels outside the corner, each beside its own line.
                (da.LabelX, da.LabelY) = (-v.X * value * 1.5, -v.Y * value * 1.5);
                (db.LabelX, db.LabelY) = (-u.X * value * 1.5, -u.Y * value * 1.5);
                made.Add(da.Id);
                made.Add(db.Id);
            }
            else
            {
                var bis = (u + v).Normalized();
                var center = c + bis * (value / Math.Sin(angle / 2));
                // Counter-clockwise the short way round.
                var (start, end, ps, pe) = Sketch.ArcSweep(center, ta, tb) <= Math.PI ? (ta, tb, pa, pb) : (tb, ta, pb, pa);
                var arc = s.AddArc(center, start, end, a.Construction && b.Construction);
                made.Add(s.AddConstraint(ConstraintKind.Coincident, [arc.Points[1], ps]).Id);
                made.Add(s.AddConstraint(ConstraintKind.Coincident, [arc.Points[2], pe]).Id);
                made.Add(s.AddConstraint(ConstraintKind.Tangent, [a.Id, arc.Id], TangentData(s, a.Id, arc.Id)).Id);
                made.Add(s.AddConstraint(ConstraintKind.Tangent, [b.Id, arc.Id], TangentData(s, b.Id, arc.Id)).Id);
                var radius = s.AddConstraint(ConstraintKind.Radius, [arc.Id], expression: size);
                // The label outside the corner, beyond the arc.
                (radius.LabelX, radius.LabelY) = (-bis.X * value * 2.5, -bis.Y * value * 2.5);
                made.Add(radius.Id);
            }
        }
        if (made.Count > 0) error = null;
        return made;
    }

    /// <summary>A value as a dimension shows it: up to 4 decimals, invariant.</summary>
    public static string Format(double v)
    {
        var t = Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return t == "-0" ? "0" : t;
    }

    /// <summary>
    /// Adds constraints and keeps only those that the sketch can satisfy without repeating others: a constraint that
    /// contradicts or adds nothing is removed again (its reason in <paramref name="error"/>). A dimension that would
    /// over-constrain becomes a reference dimension instead. Returns the solve result.
    /// </summary>
    public static SketchSolveResult Settle(Sketch s, IReadOnlyCollection<int> added, out string? error)
    {
        error = null;
        var backup = s.Clone();
        var r = SketchSolver.Solve(s);
        var bad = added.Where(id => r.Conflicting.Contains(id) || r.Redundant.Contains(id)).ToList();
        if (bad.Count == 0) return r;

        // Back to the positions before, then drop or turn into reference what did not fit.
        Restore(s, backup);
        var dims = bad.Select(s.Constraint).Where(c => c is { IsDimension: true }).Cast<SketchConstraint>().ToList();
        foreach (var d in dims) d.Reference = true;
        var dropped = bad.Except(dims.Select(d => d.Id)).ToList();
        s.Constraints.RemoveAll(c => dropped.Contains(c.Id));
        error = dims.Count > 0 && dropped.Count == 0
            ? T("The dimension would over-constrain the sketch: it is shown as a reference dimension.")
            : T("The constraint contradicts the others or is already given by them: it was not added.");
        return SketchSolver.Solve(s);
    }

    /// <summary>Puts the positions, radii and constraints of <paramref name="from"/> back into <paramref name="s"/>.</summary>
    public static void Restore(Sketch s, Sketch from)
    {
        s.Points = from.Clone().Points;
        foreach (var g in s.Geometry)
            if (from.Geo(g.Id) is { } old) g.Radius = old.Radius;
    }
}
