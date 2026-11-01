using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>
/// Text along a guide path (as CamBam's Text On Polyline plugin): the text outlines are bent so that the text's
/// baseline follows the guide. A point at distance u along the baseline and v above it goes to the guide point at
/// arc length start + u, moved v along the guide's left normal; holes and dots of letters stay in place.
/// </summary>
public static class TextOnPath
{
    /// <summary>Polyline approximation of a path with cumulative lengths, for points by arc length.</summary>
    public sealed class Sampler
    {
        private readonly List<Point2> _points;
        private readonly List<double> _lengths;

        public Sampler(Path2 path, double tolerance = 0.005)
        {
            Closed = path.Closed;
            _points = path.Flatten(tolerance).ToList();
            if (Closed && _points.Count > 1 && !_points[^1].AlmostEquals(_points[0], 1e-12)) _points.Add(_points[0]);
            _points = _points.Where((p, i) => i == 0 || !p.AlmostEquals(_points[i - 1], 1e-12)).ToList();
            _lengths = [0];
            for (var i = 1; i < _points.Count; i++) _lengths.Add(_lengths[^1] + Point2.Distance(_points[i - 1], _points[i]));
        }

        public bool Closed { get; }
        public double Length => _lengths[^1];

        /// <summary>Point and unit tangent at arc length <paramref name="s"/>; closed paths wrap round, open ones continue straight.</summary>
        public (Point2 Point, Point2 Tangent) At(double s)
        {
            if (_points.Count < 2) return (_points.Count == 1 ? _points[0] : Point2.Zero, new Point2(1, 0));
            if (Closed && Length > 0) s = ((s % Length) + Length) % Length;
            var i = _lengths.BinarySearch(s);
            if (i < 0) i = ~i - 1;
            i = Math.Clamp(i, 0, _points.Count - 2);
            var a = _points[i];
            var b = _points[i + 1];
            var t = (b - a).Normalized();
            return (a + t * (s - _lengths[i]), t);
        }

        /// <summary>Arc length of the path point nearest to <paramref name="p"/>.</summary>
        public double Project(Point2 p)
        {
            var best = 0.0;
            var bestDistance = double.MaxValue;
            for (var i = 0; i + 1 < _points.Count; i++)
            {
                var a = _points[i];
                var d = _points[i + 1] - a;
                var len2 = Point2.Dot(d, d);
                var t = len2 < 1e-24 ? 0 : Math.Clamp(Point2.Dot(p - a, d) / len2, 0, 1);
                var dist = Point2.Distance(p, a + d * t);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = _lengths[i] + Math.Sqrt(len2) * t;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Bends the outlines of <paramref name="text"/> along <paramref name="guide"/>, the text's left end at arc length
    /// <paramref name="start"/>. <paramref name="flip"/> puts the text on the other side (mirrored across the guide
    /// direction, still readable from that side).
    /// </summary>
    public static List<Path2> Bend(TextEntity text, Sampler guide, double start, bool flip)
    {
        var outlines = text.ToPaths().Where(p => p.Count >= 2).ToList();
        if (outlines.Count == 0) return [];
        // The text's own frame: origin at its insertion point, x along its baseline (rotation and scale included).
        var origin = text.Transform.Transform(text.Position).XY;
        var xAxis = (text.Transform.Transform(text.Position + new Point3(1, 0, 0)).XY - origin).Normalized();
        var yAxis = new Point2(-xAxis.Y, xAxis.X);
        var step = Math.Max(text.Height * text.Transform.ScaleXY / 25, 1e-3);

        var local = outlines.Select(o => Densify(o, step).Select(p => (U: Point2.Dot(p - origin, xAxis), V: Point2.Dot(p - origin, yAxis))).ToList()).ToList();
        var left = local.SelectMany(l => l).Min(p => p.U);
        var right = local.SelectMany(l => l).Max(p => p.U);
        var result = new List<Path2>();
        foreach (var contour in local)
        {
            var points = contour.Select(p =>
            {
                // Flipped: run the text the other way along the guide, below it, so it reads from the other side.
                var u = flip ? right - p.U : p.U - left;
                var (point, tangent) = guide.At(start + u);
                var normal = new Point2(-tangent.Y, tangent.X);
                return point + normal * (flip ? -p.V : p.V);
            }).ToList();
            result.Add(Path2.FromPoints(points, closed: true));
        }
        return result;
    }

    /// <summary>
    /// Whether the guide should be run backwards so that the text reads left to right: open guides drawn mainly from
    /// right to left, closed ones drawn counter-clockwise (the text then stands upright outside along the top).
    /// </summary>
    public static bool ReadsBetterReversed(Path2 guide) =>
        guide.Closed ? guide.SignedArea() > 0 : guide.EndPoint.X < guide.StartPoint.X;

    /// <summary>Contour points with arcs flattened and long lines split, so that the bent outline stays smooth.</summary>
    private static List<Point2> Densify(Path2 path, double step)
    {
        var pts = path.Flatten(step / 4);
        var result = new List<Point2>();
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            result.Add(a);
            if (i + 1 >= pts.Count) break;
            var b = pts[i + 1];
            var n = (int)Math.Ceiling(Point2.Distance(a, b) / step);
            for (var k = 1; k < n; k++) result.Add(a + (b - a) * ((double)k / n));
        }
        if (result.Count > 1 && result[^1].AlmostEquals(result[0], 1e-9)) result.RemoveAt(result.Count - 1);
        return result;
    }
}
