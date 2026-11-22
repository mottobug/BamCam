using BamCam.Core.Geometry;

namespace BamCam.Core.Sketching;

/// <summary>How far a sketch is determined by its constraints and dimensions.</summary>
public enum SketchStatus
{
    /// <summary>Some geometry can still move (degrees of freedom left).</summary>
    UnderConstrained,
    /// <summary>Everything is determined.</summary>
    FullyConstrained,
    /// <summary>Constraints contradict each other or repeat one another; see the conflicting and redundant lists.</summary>
    OverConstrained,
}

/// <summary>Result of <see cref="SketchSolver.Solve"/>.</summary>
public sealed class SketchSolveResult
{
    /// <summary>All constraints are satisfied.</summary>
    public bool Solved { get; init; }
    public int DegreesOfFreedom { get; init; }
    /// <summary>Constraints not satisfied after solving (they contradict others).</summary>
    public IReadOnlyList<int> Conflicting { get; init; } = [];
    /// <summary>Constraints that repeat what others already fix (they add nothing).</summary>
    public IReadOnlyList<int> Redundant { get; init; } = [];
    /// <summary>Points whose position is fully determined.</summary>
    public IReadOnlySet<int> DeterminedPoints { get; init; } = new HashSet<int>();
    /// <summary>Geometry whose points (and radius) are all determined.</summary>
    public IReadOnlySet<int> DeterminedGeometry { get; init; } = new HashSet<int>();
    /// <summary>Dimensions whose formula could not be evaluated, with the reason.</summary>
    public IReadOnlyDictionary<int, string> ExpressionErrors { get; init; } = new Dictionary<int, string>();
    /// <summary>Variables that could not be evaluated, with the reason.</summary>
    public IReadOnlyDictionary<string, string> VariableErrors { get; init; } = new Dictionary<string, string>();

    public SketchStatus Status => Conflicting.Count > 0 || Redundant.Count > 0 ? SketchStatus.OverConstrained
        : DegreesOfFreedom > 0 ? SketchStatus.UnderConstrained : SketchStatus.FullyConstrained;
}

/// <summary>
/// Solves the constraints of a <see cref="Sketch"/>: moves its points and circle radii as little as possible until all
/// constraints and driving dimensions hold (Gauss-Newton with the least-norm step and Levenberg-Marquardt damping), and
/// tells which geometry is fully determined, how many degrees of freedom are left and which constraints contradict or
/// repeat others.
/// </summary>
public static class SketchSolver
{
    private const double Tolerance = 1e-9;

    private sealed record Equation(int ConstraintId, int[] Vars, Func<double[], double> F);

    /// <summary>
    /// Solves the sketch in place. Points in <paramref name="held"/> are moved to the given positions and kept there
    /// if the constraints allow it (dragging); otherwise they move as needed.
    /// </summary>
    public static SketchSolveResult Solve(Sketch sketch, IReadOnlyDictionary<int, Point2>? held = null)
    {
        if (held is { Count: > 0 })
        {
            var backup = sketch.Points.ToDictionary(p => p.Id, p => p.Position);
            var radii = sketch.Geometry.ToDictionary(g => g.Id, g => g.Radius);
            foreach (var (id, pos) in held)
                if (sketch.Point(id) is { Locked: false } p) p.Position = pos;
            var r = Run(sketch, held.Keys.ToHashSet());
            if (r.Solved) return Run(sketch, null, analyseOnly: true, previous: r);
            // The held points cannot stay where they are: back to the old positions, solved without holding.
            foreach (var p in sketch.Points) if (backup.TryGetValue(p.Id, out var b)) p.Position = b;
            foreach (var g in sketch.Geometry) if (radii.TryGetValue(g.Id, out var rr)) g.Radius = rr;
        }
        return Run(sketch, null);
    }

    private static SketchSolveResult Run(Sketch sketch, HashSet<int>? hold, bool analyseOnly = false, SketchSolveResult? previous = null)
    {
        var variables = SketchExpression.EvaluateVariables(sketch.Variables, out var variableErrors);
        var expressionErrors = new Dictionary<int, string>();

        // Variables of the solver: x and y of each point that may move, the radius of each circle.
        var x = new List<double>();
        var pointVar = new Dictionary<int, int>();
        foreach (var p in sketch.Points)
        {
            if (p.Locked || hold?.Contains(p.Id) == true) continue;
            pointVar[p.Id] = x.Count;
            x.Add(p.X);
            x.Add(p.Y);
        }
        var radiusVar = new Dictionary<int, int>();
        foreach (var g in sketch.Geometry.Where(g => g.Kind == SketchGeometryKind.Circle))
        {
            radiusVar[g.Id] = x.Count;
            x.Add(g.Radius);
        }

        var eqs = new List<Equation>();
        var builder = new Builder(sketch, pointVar, radiusVar);
        foreach (var g in sketch.Geometry.Where(g => g.Kind == SketchGeometryKind.Arc))
            builder.ArcRadius(eqs, g);
        foreach (var c in sketch.Constraints)
        {
            double value = 0;
            if (c.IsDimension)
            {
                if (c.Reference) continue;
                try
                {
                    value = SketchExpression.Evaluate(c.Expression ?? "0", n => variables.TryGetValue(n, out var v) ? v : null);
                }
                catch (FormatException ex)
                {
                    expressionErrors[c.Id] = ex.Message;
                    continue;
                }
            }
            builder.Add(eqs, c, value);
        }

        var xs = x.ToArray();
        if (!analyseOnly) Iterate(eqs, xs);

        // Write back.
        foreach (var (id, i) in pointVar)
            if (sketch.Point(id) is { } p) { p.X = xs[i]; p.Y = xs[i + 1]; }
        foreach (var (id, i) in radiusVar)
            if (sketch.Geo(id) is { } g) g.Radius = xs[i];

        var conflicting = eqs.Where(e => Math.Abs(e.F(xs)) > 1e-6).Select(e => e.ConstraintId).Where(id => id > 0).Distinct().ToList();
        var (rank, redundantRows, determined) = Analyse(eqs, xs);
        var redundant = redundantRows.Select(r => eqs[r].ConstraintId).Where(id => id > 0).Distinct().Except(conflicting).ToList();

        var determinedPoints = sketch.Points.Where(p => p.Locked || hold?.Contains(p.Id) == true
            || pointVar.TryGetValue(p.Id, out var i) && determined[i] && determined[i + 1]).Select(p => p.Id).ToHashSet();
        var determinedGeometry = sketch.Geometry.Where(g => g.Points.All(determinedPoints.Contains)
            && (!radiusVar.TryGetValue(g.Id, out var ri) || determined[ri])).Select(g => g.Id).ToHashSet();

        return new SketchSolveResult
        {
            Solved = conflicting.Count == 0 && expressionErrors.Count == 0,
            DegreesOfFreedom = xs.Length - rank,
            Conflicting = previous?.Conflicting ?? conflicting,
            Redundant = redundant,
            DeterminedPoints = determinedPoints,
            DeterminedGeometry = determinedGeometry,
            ExpressionErrors = expressionErrors,
            VariableErrors = variableErrors,
        };
    }

    // ------------------------------------------------------------------ numerics

    /// <summary>Gauss-Newton with the least-norm step dx = −Jᵀ (J Jᵀ + λ I)⁻¹ r, damped while the error grows.</summary>
    private static void Iterate(List<Equation> eqs, double[] x)
    {
        if (eqs.Count == 0 || x.Length == 0) return;
        var m = eqs.Count;
        var r = new double[m];
        double Error(double[] at)
        {
            double s = 0;
            for (var i = 0; i < m; i++)
            {
                r[i] = eqs[i].F(at);
                s += r[i] * r[i];
            }
            return s;
        }
        var lambda = 1e-6;
        var error = Error(x);
        for (var iter = 0; iter < 200 && error > Tolerance * Tolerance; iter++)
        {
            var jac = Jacobian(eqs, x);
            var residual = (double[])r.Clone();
            var improved = false;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var a = new double[m, m];
                for (var i = 0; i < m; i++)
                for (var j = i; j < m; j++)
                {
                    double s = 0;
                    foreach (var (k, v) in jac[i]) if (jac[j].TryGetValue(k, out var w)) s += v * w;
                    a[i, j] = a[j, i] = s;
                }
                for (var i = 0; i < m; i++) a[i, i] += lambda * (1 + a[i, i]);
                if (SolveSymmetric(a, residual) is not { } y)
                {
                    lambda *= 10;
                    continue;
                }
                var candidate = (double[])x.Clone();
                for (var i = 0; i < m; i++)
                    foreach (var (k, v) in jac[i]) candidate[k] -= v * y[i];
                var newError = Error(candidate);
                if (newError < error)
                {
                    Array.Copy(candidate, x, x.Length);
                    error = newError;
                    lambda = Math.Max(lambda / 10, 1e-12);
                    improved = true;
                    break;
                }
                lambda *= 10;
            }
            if (!improved) break;
        }
        Error(x);
    }

    /// <summary>Sparse rows of the Jacobian by central differences over the variables each equation uses.</summary>
    private static Dictionary<int, double>[] Jacobian(List<Equation> eqs, double[] x)
    {
        var rows = new Dictionary<int, double>[eqs.Count];
        for (var i = 0; i < eqs.Count; i++)
        {
            var row = new Dictionary<int, double>();
            foreach (var k in eqs[i].Vars)
            {
                var old = x[k];
                var h = 1e-7 * Math.Max(1, Math.Abs(old));
                x[k] = old + h;
                var fp = eqs[i].F(x);
                x[k] = old - h;
                var fm = eqs[i].F(x);
                x[k] = old;
                var d = (fp - fm) / (2 * h);
                if (d != 0) row[k] = d;
            }
            rows[i] = row;
        }
        return rows;
    }

    /// <summary>Cholesky solution of a symmetric positive definite system; null when it is not.</summary>
    private static double[]? SolveSymmetric(double[,] a, double[] b)
    {
        var n = b.Length;
        var l = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var s = a[i, j];
                for (var k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 1e-300) return null;
                    l[i, i] = Math.Sqrt(s);
                }
                else
                {
                    l[i, j] = s / l[j, j];
                }
            }
        }
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = b[i];
            for (var k = 0; k < i; k++) s -= l[i, k] * y[k];
            y[i] = s / l[i, i];
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var k = i + 1; k < n; k++) s -= l[k, i] * y[k];
            y[i] = s / l[i, i];
        }
        return y;
    }

    /// <summary>
    /// Rank of the Jacobian, the equations that add nothing to the ones before them (in order) and which variables
    /// are determined (lie in the row space): Gram-Schmidt over the normalised rows.
    /// </summary>
    private static (int Rank, List<int> Redundant, bool[] Determined) Analyse(List<Equation> eqs, double[] x)
    {
        var n = x.Length;
        var basis = new List<double[]>();
        var redundant = new List<int>();
        var jac = Jacobian(eqs, x);
        for (var i = 0; i < eqs.Count; i++)
        {
            var v = new double[n];
            foreach (var (k, d) in jac[i]) v[k] = d;
            var norm = Math.Sqrt(v.Sum(t => t * t));
            if (norm < 1e-12)
            {
                redundant.Add(i);
                continue;
            }
            for (var k = 0; k < n; k++) v[k] /= norm;
            // Twice, for numerical stability.
            for (var pass = 0; pass < 2; pass++)
                foreach (var q in basis)
                {
                    double dot = 0;
                    for (var k = 0; k < n; k++) dot += v[k] * q[k];
                    for (var k = 0; k < n; k++) v[k] -= dot * q[k];
                }
            var rest = Math.Sqrt(v.Sum(t => t * t));
            if (rest < 1e-6)
            {
                redundant.Add(i);
                continue;
            }
            for (var k = 0; k < n; k++) v[k] /= rest;
            basis.Add(v);
        }
        var determined = new bool[n];
        for (var k = 0; k < n; k++)
        {
            double s = 0;
            foreach (var q in basis) s += q[k] * q[k];
            determined[k] = s > 1 - 1e-6;
        }
        return (basis.Count, redundant, determined);
    }

    // ------------------------------------------------------------------ equations

    private sealed class Builder(Sketch sketch, Dictionary<int, int> pointVar, Dictionary<int, int> radiusVar)
    {
        private Func<double[], Point2> Pt(int pointId)
        {
            if (pointVar.TryGetValue(pointId, out var i)) return x => new Point2(x[i], x[i + 1]);
            var fixedP = sketch.P(pointId);
            return _ => fixedP;
        }

        private IEnumerable<int> PointVars(int pointId) => pointVar.TryGetValue(pointId, out var i) ? [i, i + 1] : [];

        private int[] VarsOf(params int[] pointIds) => pointIds.SelectMany(PointVars).Distinct().ToArray();

        /// <summary>Centre, radius function and variables of a circle or arc.</summary>
        private (Func<double[], Point2> C, Func<double[], double> R, int[] Vars)? Round(int geoId)
        {
            if (sketch.Geo(geoId) is not { } g) return null;
            if (g.Kind == SketchGeometryKind.Circle)
            {
                var c = Pt(g.Points[0]);
                Func<double[], double> r = radiusVar.TryGetValue(g.Id, out var ri) ? x => x[ri] : _ => g.Radius;
                var vars = VarsOf(g.Points[0]).Concat(radiusVar.TryGetValue(g.Id, out var rv) ? [rv] : []).ToArray();
                return (c, r, vars);
            }
            if (g.Kind == SketchGeometryKind.Arc)
            {
                var c = Pt(g.Points[0]);
                var s = Pt(g.Points[1]);
                return (c, x => Point2.Distance(c(x), s(x)), VarsOf(g.Points[0], g.Points[1]));
            }
            return null;
        }

        private (Func<double[], Point2> A, Func<double[], Point2> B, int[] Vars)? LineOf(int geoId) =>
            sketch.Geo(geoId) is { Kind: SketchGeometryKind.Line } l ? (Pt(l.Points[0]), Pt(l.Points[1]), VarsOf(l.Points[0], l.Points[1])) : null;

        private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

        private static double SafeLength(Point2 d) => Math.Max(d.Length, 1e-12);

        private static int[] Union(params int[][] sets) => sets.SelectMany(s => s).Distinct().ToArray();

        public void ArcRadius(List<Equation> eqs, SketchGeometry arc)
        {
            var c = Pt(arc.Points[0]);
            var s = Pt(arc.Points[1]);
            var e = Pt(arc.Points[2]);
            eqs.Add(new Equation(-arc.Id, VarsOf(arc.Points[0], arc.Points[1], arc.Points[2]),
                x => Point2.Distance(c(x), e(x)) - Point2.Distance(c(x), s(x))));
        }

        public void Add(List<Equation> eqs, SketchConstraint c, double value)
        {
            var r = c.Refs;
            int P(int i) => sketch.PointOf(r[i]);
            double D(int i, double fallback = 1) => c.Data.Length > i ? c.Data[i] : fallback;
            void Eq(int[] vars, Func<double[], double> f) => eqs.Add(new Equation(c.Id, vars, f));

            switch (c.Kind)
            {
                case ConstraintKind.Coincident when r.Length == 2:
                {
                    var a = Pt(P(0));
                    var b = Pt(P(1));
                    var vars = VarsOf(P(0), P(1));
                    Eq(vars, x => a(x).X - b(x).X);
                    Eq(vars, x => a(x).Y - b(x).Y);
                    break;
                }
                case ConstraintKind.Horizontal or ConstraintKind.Vertical:
                {
                    var (a, b, vars) = Ends(r);
                    if (a == null) break;
                    if (c.Kind == ConstraintKind.Horizontal) Eq(vars, x => a(x).Y - b!(x).Y);
                    else Eq(vars, x => a(x).X - b!(x).X);
                    break;
                }
                case ConstraintKind.Parallel or ConstraintKind.Perpendicular when r.Length == 2 && LineOf(r[0]) is { } l1 && LineOf(r[1]) is { } l2:
                {
                    var vars = Union(l1.Vars, l2.Vars);
                    var parallel = c.Kind == ConstraintKind.Parallel;
                    Eq(vars, x =>
                    {
                        var d1 = l1.B(x) - l1.A(x);
                        var d2 = l2.B(x) - l2.A(x);
                        var n = SafeLength(d1) * SafeLength(d2);
                        return (parallel ? Cross(d1, d2) : Point2.Dot(d1, d2)) / n;
                    });
                    break;
                }
                case ConstraintKind.Angle when r.Length == 2 && LineOf(r[0]) is { } l1 && LineOf(r[1]) is { } l2:
                {
                    var vars = Union(l1.Vars, l2.Vars);
                    var a = value * Math.PI / 180 * D(0);
                    var (sin, cos) = Math.SinCos(a);
                    Eq(vars, x =>
                    {
                        var d1 = l1.B(x) - l1.A(x);
                        var d2 = l2.B(x) - l2.A(x);
                        var n = SafeLength(d1) * SafeLength(d2);
                        // sin(θ − a) with θ the angle from line 1 to line 2.
                        return (Cross(d1, d2) * cos - Point2.Dot(d1, d2) * sin) / n;
                    });
                    break;
                }
                case ConstraintKind.Tangent when r.Length == 2 && Round(r[1]) is { } circle:
                {
                    // Touching at an end point of the arc (a fillet): the radius there is at right angles to the line,
                    // or the two centres and the end point are in line. Same tangency, but well-conditioned where the
                    // distance form has a flat extremum.
                    if (c.Data.Length == 2 && LineOf(r[0]) is { } endLine && sketch.Geo(r[1]) is { Kind: SketchGeometryKind.Arc } endArc
                        && c.Data[1] is 1 or 2)
                    {
                        var tp = Pt(endArc.Points[(int)c.Data[1]]);
                        Eq(Union(endLine.Vars, circle.Vars, VarsOf(endArc.Points[(int)c.Data[1]])), x =>
                        {
                            var d = endLine.B(x) - endLine.A(x);
                            var radius = tp(x) - circle.C(x);
                            return Point2.Dot(radius, d) / (SafeLength(radius) * SafeLength(d));
                        });
                        break;
                    }
                    if (c.Data.Length == 4 && Round(r[0]) is { } first && sketch.Geo(r[0]) is { Kind: SketchGeometryKind.Arc } arc1
                        && c.Data[2] is 1 or 2)
                    {
                        var tp = Pt(arc1.Points[(int)c.Data[2]]);
                        Eq(Union(first.Vars, circle.Vars, VarsOf(arc1.Points[(int)c.Data[2]])), x =>
                        {
                            var a = tp(x) - first.C(x);
                            var b = tp(x) - circle.C(x);
                            return Cross(a, b) / (SafeLength(a) * SafeLength(b));
                        });
                        break;
                    }
                    if (LineOf(r[0]) is { } line)
                    {
                        var side = D(0);
                        Eq(Union(line.Vars, circle.Vars), x =>
                        {
                            var a = line.A(x);
                            var d = line.B(x) - a;
                            return side * Cross(d, circle.C(x) - a) / SafeLength(d) - circle.R(x);
                        });
                    }
                    else if (Round(r[0]) is { } other)
                    {
                        var external = D(0) > 0;
                        var order = D(1);
                        Eq(Union(other.Vars, circle.Vars), x =>
                        {
                            var dist = Point2.Distance(other.C(x), circle.C(x));
                            return external ? dist - (other.R(x) + circle.R(x)) : dist - order * (other.R(x) - circle.R(x));
                        });
                    }
                    break;
                }
                case ConstraintKind.Equal when r.Length == 2:
                {
                    if (LineOf(r[0]) is { } l1 && LineOf(r[1]) is { } l2)
                        Eq(Union(l1.Vars, l2.Vars), x => Point2.Distance(l1.A(x), l1.B(x)) - Point2.Distance(l2.A(x), l2.B(x)));
                    else if (Round(r[0]) is { } c1 && Round(r[1]) is { } c2)
                        Eq(Union(c1.Vars, c2.Vars), x => c1.R(x) - c2.R(x));
                    break;
                }
                case ConstraintKind.Fix when r.Length == 1:
                {
                    var p = Pt(P(0));
                    var vars = VarsOf(P(0));
                    double fx = D(0, 0), fy = D(1, 0);
                    Eq(vars, x => p(x).X - fx);
                    Eq(vars, x => p(x).Y - fy);
                    break;
                }
                case ConstraintKind.PointOnObject when r.Length == 2:
                {
                    var p = Pt(P(0));
                    var pv = VarsOf(P(0));
                    if (LineOf(r[1]) is { } line)
                        Eq(Union(pv, line.Vars), x =>
                        {
                            var a = line.A(x);
                            var d = line.B(x) - a;
                            return Cross(d, p(x) - a) / SafeLength(d);
                        });
                    else if (Round(r[1]) is { } circle)
                        Eq(Union(pv, circle.Vars), x => Point2.Distance(p(x), circle.C(x)) - circle.R(x));
                    break;
                }
                case ConstraintKind.Midpoint when r.Length == 2 && LineOf(r[1]) is { } line:
                {
                    var p = Pt(P(0));
                    var vars = Union(VarsOf(P(0)), line.Vars);
                    Eq(vars, x => p(x).X - (line.A(x).X + line.B(x).X) / 2);
                    Eq(vars, x => p(x).Y - (line.A(x).Y + line.B(x).Y) / 2);
                    break;
                }
                case ConstraintKind.Symmetric when r.Length == 3 && LineOf(r[2]) is { } line:
                {
                    var p = Pt(P(0));
                    var q = Pt(P(1));
                    var vars = Union(VarsOf(P(0), P(1)), line.Vars);
                    Eq(vars, x =>
                    {
                        var a = line.A(x);
                        var d = line.B(x) - a;
                        var mid = (p(x) + q(x)) * 0.5;
                        return Cross(d, mid - a) / SafeLength(d);
                    });
                    Eq(vars, x =>
                    {
                        var d = line.B(x) - line.A(x);
                        return Point2.Dot(q(x) - p(x), d) / SafeLength(d);
                    });
                    break;
                }
                case ConstraintKind.Distance:
                {
                    if (r.Length == 1 && LineOf(r[0]) is { } own)
                        Eq(own.Vars, x => Point2.Distance(own.A(x), own.B(x)) - value);
                    else if (r.Length == 2 && LineOf(r[1]) is { } line)
                    {
                        var p = Pt(P(0));
                        var side = D(0);
                        Eq(Union(VarsOf(P(0)), line.Vars), x =>
                        {
                            var a = line.A(x);
                            var d = line.B(x) - a;
                            return side * Cross(d, p(x) - a) / SafeLength(d) - value;
                        });
                    }
                    else if (r.Length == 2)
                    {
                        var a = Pt(P(0));
                        var b = Pt(P(1));
                        Eq(VarsOf(P(0), P(1)), x => Point2.Distance(a(x), b(x)) - value);
                    }
                    break;
                }
                case ConstraintKind.HorizontalDistance or ConstraintKind.VerticalDistance:
                {
                    var (a, b, vars) = Ends(r);
                    if (a == null) break;
                    var sign = D(0);
                    var horizontal = c.Kind == ConstraintKind.HorizontalDistance;
                    Eq(vars, x => sign * (horizontal ? b!(x).X - a(x).X : b!(x).Y - a(x).Y) - value);
                    break;
                }
                case ConstraintKind.Radius or ConstraintKind.Diameter when r.Length == 1 && Round(r[0]) is { } circle:
                {
                    var factor = c.Kind == ConstraintKind.Diameter ? 2 : 1;
                    Eq(circle.Vars, x => factor * circle.R(x) - value);
                    break;
                }
            }
        }

        private (Func<double[], Point2>? A, Func<double[], Point2>? B, int[] Vars) Ends(int[] r)
        {
            if (r.Length == 1 && LineOf(r[0]) is { } l) return (l.A, l.B, l.Vars);
            if (r.Length == 2)
            {
                var p = sketch.PointOf(r[0]);
                var q = sketch.PointOf(r[1]);
                return (Pt(p), Pt(q), VarsOf(p, q));
            }
            return (null, null, []);
        }
    }
}
