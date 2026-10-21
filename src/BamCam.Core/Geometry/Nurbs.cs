namespace BamCam.Core.Geometry;

/// <summary>
/// Non-uniform rational B-spline curve (as in DXF SPLINE): degree, control points, optional weights and a knot
/// vector with <c>controlPoints + degree + 1</c> entries. Evaluation uses the rational de Boor algorithm.
/// </summary>
public static class Nurbs
{
    /// <summary>Clamped uniform knot vector (curve starts and ends at the first and last control point).</summary>
    public static List<double> ClampedKnots(int controlPoints, int degree)
    {
        var knots = new List<double>();
        var spans = controlPoints - degree;
        for (var i = 0; i <= degree; i++) knots.Add(0);
        for (var i = 1; i < spans; i++) knots.Add(i);
        for (var i = 0; i <= degree; i++) knots.Add(Math.Max(spans, 1));
        return knots;
    }

    /// <summary>True when degree, control point, weight and knot counts fit together.</summary>
    public static bool IsValid(int degree, IReadOnlyList<Point3> ctrl, IReadOnlyList<double> weights, IReadOnlyList<double> knots) =>
        degree >= 1 && ctrl.Count > degree && knots.Count == ctrl.Count + degree + 1 &&
        (weights.Count == 0 || weights.Count == ctrl.Count);

    /// <summary>Point at parameter <paramref name="u"/> (clamped to the valid range).</summary>
    public static Point3 Evaluate(int p, IReadOnlyList<Point3> ctrl, IReadOnlyList<double> weights, IReadOnlyList<double> knots, double u)
    {
        var n = ctrl.Count - 1;
        u = Math.Clamp(u, knots[p], knots[n + 1]);
        // Knot span k with knots[k] <= u < knots[k+1] (the last span includes its end).
        var k = p;
        while (k < n && u >= knots[k + 1]) k++;
        Span<double> x = stackalloc double[p + 1], y = stackalloc double[p + 1], z = stackalloc double[p + 1], w = stackalloc double[p + 1];
        for (var j = 0; j <= p; j++)
        {
            var i = j + k - p;
            var wi = weights.Count > 0 ? weights[i] : 1;
            x[j] = ctrl[i].X * wi;
            y[j] = ctrl[i].Y * wi;
            z[j] = ctrl[i].Z * wi;
            w[j] = wi;
        }
        for (var r = 1; r <= p; r++)
        for (var j = p; j >= r; j--)
        {
            var i = j + k - p;
            var denom = knots[i + p - r + 1] - knots[i];
            var a = denom < 1e-14 ? 0 : (u - knots[i]) / denom;
            x[j] = (1 - a) * x[j - 1] + a * x[j];
            y[j] = (1 - a) * y[j - 1] + a * y[j];
            z[j] = (1 - a) * z[j - 1] + a * z[j];
            w[j] = (1 - a) * w[j - 1] + a * w[j];
        }
        var wd = Math.Abs(w[p]) < 1e-14 ? 1 : w[p];
        return new Point3(x[p] / wd, y[p] / wd, z[p] / wd);
    }

    /// <summary>
    /// Points along the curve whose chords deviate at most <paramref name="tolerance"/> from it: every knot span
    /// is subdivided recursively until the mid point of each piece lies within the tolerance of its chord.
    /// </summary>
    public static List<Point3> Flatten(int p, IReadOnlyList<Point3> ctrl, IReadOnlyList<double> weights, IReadOnlyList<double> knots, double tolerance)
    {
        var pts = new List<Point3>();
        if (!IsValid(p, ctrl, weights, knots)) return [.. ctrl];
        tolerance = Math.Max(tolerance, 1e-6);
        var n = ctrl.Count - 1;
        pts.Add(Evaluate(p, ctrl, weights, knots, knots[p]));
        for (var k = p; k <= n; k++)
        {
            var u0 = knots[k];
            var u1 = knots[k + 1];
            if (u1 - u0 < 1e-14) continue;
            // A few uniform samples per span so that S-shaped spans are not mistaken for straight ones.
            const int seeds = 4;
            var a = pts[^1];
            for (var s = 1; s <= seeds; s++)
            {
                var ua = u0 + (u1 - u0) * (s - 1) / seeds;
                var ub = u0 + (u1 - u0) * s / seeds;
                var b = Evaluate(p, ctrl, weights, knots, ub);
                Subdivide(p, ctrl, weights, knots, ua, a, ub, b, tolerance, pts, 0);
                a = b;
            }
        }
        return pts;
    }

    private static void Subdivide(int p, IReadOnlyList<Point3> ctrl, IReadOnlyList<double> weights, IReadOnlyList<double> knots,
        double ua, Point3 a, double ub, Point3 b, double tol, List<Point3> output, int depth)
    {
        var um = (ua + ub) / 2;
        var m = Evaluate(p, ctrl, weights, knots, um);
        if (depth < 18 && DistanceToSegment(m, a, b) > tol)
        {
            Subdivide(p, ctrl, weights, knots, ua, a, um, m, tol, output, depth + 1);
            Subdivide(p, ctrl, weights, knots, um, m, ub, b, tol, output, depth + 1);
            return;
        }
        output.Add(b);
    }

    private static double DistanceToSegment(Point3 q, Point3 a, Point3 b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z;
        var t = len2 < 1e-24 ? 0 : Math.Clamp(((q.X - a.X) * ab.X + (q.Y - a.Y) * ab.Y + (q.Z - a.Z) * ab.Z) / len2, 0, 1);
        var dx = a.X + ab.X * t - q.X;
        var dy = a.Y + ab.Y * t - q.Y;
        var dz = a.Z + ab.Z * t - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
