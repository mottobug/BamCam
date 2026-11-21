using BamCam.Core.Geometry;

namespace BamCam.Core.Cad;

/// <summary>
/// Shapes of Draw ▸ More (ellipse, flat spiral, spiral and trochoidal path, tangent lines) and the point patterns of
/// Draw ▸ Point List (step around, fill, offset fill, extents).
/// </summary>
public static class Shapes
{
    /// <summary>
    /// The ellipse in the rectangle with the corners <paramref name="a"/> and <paramref name="b"/>, as a closed
    /// polyline of arcs that stays within <paramref name="tolerance"/> of it. Null when the rectangle has no area.
    /// </summary>
    public static Path2? Ellipse(Point2 a, Point2 b, double tolerance)
    {
        var rx = Math.Abs(b.X - a.X) / 2;
        var ry = Math.Abs(b.Y - a.Y) / 2;
        if (rx < 1e-9 || ry < 1e-9) return null;
        var c = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        // Dense enough that the chords stay well inside the tolerance (sagitta at the tightest curvature b² / a at most a
        // quarter of it), then arcs fitted through the points.
        var tightest = Math.Min(rx, ry) * Math.Min(rx, ry) / Math.Max(rx, ry);
        var chord = Math.Sqrt(2 * tightest * tolerance);
        var n = Math.Clamp((int)Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / chord), 64, 8192);
        n += n % 4 == 0 ? 0 : 4 - n % 4;
        var pts = Enumerable.Range(0, n).Select(i => c + new Point2(rx * Math.Cos(2 * Math.PI * i / n), ry * Math.Sin(2 * Math.PI * i / n))).ToList();
        if (Math.Abs(rx - ry) < 1e-9) return ArcFitter.Fit(Path2.FromPoints(pts, true), tolerance);
        // Quarter by quarter, so that the ends of the axes are vertices (symmetric arcs, clean snapping).
        var vertices = new List<Vertex>();
        for (var q = 0; q < 4; q++)
        {
            var quarter = Enumerable.Range(q * n / 4, n / 4 + 1).Select(i => pts[i % n]).ToList();
            var fitted = ArcFitter.Fit(Path2.FromPoints(quarter, false), tolerance, 3);
            vertices.AddRange(fitted.Vertices.Take(fitted.Count - 1));
        }
        return new Path2(vertices, true);
    }

    /// <summary>
    /// Flat spiral from <paramref name="center"/> out to <paramref name="radius"/>, made of half circles: the distance
    /// between the turns is <paramref name="stepover"/> or a little less, so that the spiral ends exactly on the
    /// radius, followed by one full circle there. Null when the radius or stepover is not positive.
    /// </summary>
    public static Path2? FlatSpiral(Point2 center, double radius, double stepover, bool clockwise)
    {
        if (radius < 1e-9 || stepover < 1e-9) return null;
        // Half circles around two centres d / 2 left and right of the centre: the radius grows by d each half turn.
        var halves = (int)Math.Ceiling(2 * radius / stepover - 1e-9);
        var d = radius / halves;
        var bulge = clockwise ? -1.0 : 1.0;
        var vertices = new List<Vertex>();
        var x = 0.0;
        for (var k = 0; k < halves; k++)
        {
            vertices.Add(new Vertex(new Point3(center.X + x, center.Y, 0), bulge));
            var c = k % 2 == 0 ? -d / 2 : d / 2;
            x = 2 * c - x;
        }
        // One full circle on the radius.
        vertices.Add(new Vertex(new Point3(center.X + x, center.Y, 0), bulge));
        vertices.Add(new Vertex(new Point3(center.X - x, center.Y, 0), bulge));
        vertices.Add(new Vertex(new Point3(center.X + x, center.Y, 0)));
        return new Path2(vertices, false);
    }

    /// <summary>
    /// A circle of <paramref name="radius"/> going round while its centre moves along <paramref name="path"/> by
    /// <paramref name="stepover"/> per turn (a looped line, for slots with a small tool), arcs fitted within
    /// <paramref name="tolerance"/>. Null for a path without length or a radius or stepover that is not positive.
    /// </summary>
    public static Path2? SpiralAlong(Path2 path, double radius, double stepover, bool clockwise, double tolerance)
    {
        if (radius < 1e-9 || stepover < 1e-9) return null;
        var line = new Walk(path);
        if (line.Length < 1e-9) return null;
        var turns = line.Length / stepover;
        var n = Math.Max(16, (int)Math.Ceiling(turns * 72));
        var sign = clockwise ? -1 : 1;
        var pts = new List<Point2>(n + 1);
        for (var i = 0; i <= n; i++)
        {
            var s = line.Length * i / n;
            var angle = sign * 2 * Math.PI * s / stepover;
            pts.Add(line.At(s) + Point2.FromAngle(angle, radius));
        }
        return ArcFitter.Fit(Path2.FromPoints(pts, false), tolerance);
    }

    /// <summary>
    /// Full circles of <paramref name="radius"/> every <paramref name="stepover"/> along <paramref name="path"/>
    /// (and at its end), each starting and ending on the left of the path, joined by straight lines: a chain of arcs
    /// for slotting with engrave operations. Null for a path without length or a radius or stepover that is not
    /// positive.
    /// </summary>
    public static Path2? TrochoidalAlong(Path2 path, double radius, double stepover, bool clockwise)
    {
        if (radius < 1e-9 || stepover < 1e-9) return null;
        var line = new Walk(path);
        if (line.Length < 1e-9) return null;
        var count = (int)Math.Floor(line.Length / stepover + 1e-9);
        var stations = Enumerable.Range(0, count + 1).Select(i => i * stepover).ToList();
        if (line.Length - stations[^1] > 1e-6) stations.Add(line.Length);
        var bulge = clockwise ? -1.0 : 1.0;
        var vertices = new List<Vertex>();
        foreach (var s in stations)
        {
            var c = line.At(s);
            var t = line.Direction(s);
            var left = new Point2(-t.Y, t.X) * radius;
            vertices.Add(new Vertex((c + left).ToPoint3(), bulge));
            vertices.Add(new Vertex((c - left).ToPoint3(), bulge));
            vertices.Add(new Vertex((c + left).ToPoint3()));
        }
        return new Path2(vertices, false);
    }

    /// <summary>
    /// The lines touching both circles (a radius of 0 is a point): up to four, each from its point on the first
    /// circle to its point on the second.
    /// </summary>
    public static List<(Point2 A, Point2 B)> Tangents(Point2 c1, double r1, Point2 c2, double r2)
    {
        var result = new List<(Point2, Point2)>();
        var d = c2 - c1;
        var dist = d.Length;
        if (dist < 1e-9) return result;
        var v = d * (1 / dist);
        var perp = new Point2(-v.Y, v.X);
        foreach (var e2 in new[] { 1.0, -1.0 })
        {
            // Line n·x = c with n·c1 − c = r1 and n·c2 − c = e2·r2.
            var h = (e2 * r2 - r1) / dist;
            if (Math.Abs(h) > 1 + 1e-12) continue;
            var q = Math.Sqrt(Math.Max(0, 1 - h * h));
            foreach (var side in new[] { 1.0, -1.0 })
            {
                var n = v * h + perp * (q * side);
                var a = c1 - n * r1;
                var b = c2 - n * (e2 * r2);
                if (Point2.Distance(a, b) < 1e-9) continue;
                if (!result.Any(x => x.Item1.AlmostEquals(a, 1e-9) && x.Item2.AlmostEquals(b, 1e-9))) result.Add((a, b));
                if (q < 1e-12) break;
            }
        }
        return result;
    }

    /// <summary>
    /// Points every <paramref name="step"/> along the path from its start (Point List ▸ Step Around Geometry); an open
    /// path also gets its end point when the last step falls short of it by more than a tenth of a step.
    /// </summary>
    public static List<Point3> StepAround(Path2 path, double step)
    {
        var result = new List<Point3>();
        if (step < 1e-9) return result;
        var line = new Walk(path);
        if (line.Length < 1e-9) return result;
        var z = path.StartPoint.Z;
        var count = (int)Math.Floor(line.Length / step + 1e-9);
        for (var i = 0; i <= count; i++)
        {
            var s = i * step;
            if (path.Closed && line.Length - s < 1e-6 && i > 0) break;
            result.Add(line.At(s).ToPoint3(z));
        }
        if (!path.Closed && line.Length - count * step > step / 10) result.Add(path.EndPoint);
        return result;
    }

    /// <summary>
    /// A grid of points <paramref name="step"/> apart inside the closed paths (Point List ▸ Fill Geometry): a point is
    /// inside when it lies within an odd number of them, so holes stay empty. The grid is centred on the paths;
    /// with <paramref name="offsetRows"/> every other row moves by half a step (Offset Fill Geometry).
    /// </summary>
    public static List<Point3> FillGrid(IReadOnlyList<Path2> closed, double step, bool offsetRows)
    {
        var result = new List<Point3>();
        if (step < 1e-9) return result;
        var polygons = closed.Where(p => p.Closed && p.SegmentCount > 0).Select(p => p.Flatten(Math.Min(step / 50, Tolerance.Chord))).Where(p => p.Count >= 3).ToList();
        if (polygons.Count == 0) return result;
        var z = closed[0].StartPoint.Z;
        var minX = polygons.Min(p => p.Min(q => q.X));
        var maxX = polygons.Max(p => p.Max(q => q.X));
        var minY = polygons.Min(p => p.Min(q => q.Y));
        var maxY = polygons.Max(p => p.Max(q => q.Y));
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var nx = (int)Math.Ceiling((maxX - minX) / 2 / step) + 1;
        var ny = (int)Math.Ceiling((maxY - minY) / 2 / step) + 1;
        for (var j = -ny; j <= ny; j++)
        {
            var y = cy + j * step;
            if (y < minY || y > maxY) continue;
            var shift = offsetRows && Math.Abs(j) % 2 == 1 ? step / 2 : 0;
            for (var i = -nx - 1; i <= nx; i++)
            {
                var p = new Point2(cx + i * step + shift, y);
                if (p.X < minX || p.X > maxX) continue;
                if (polygons.Count(poly => Inside(poly, p)) % 2 == 1) result.Add(p.ToPoint3(z));
            }
        }
        return result;
    }

    /// <summary>The corners and the centre of a bounding box (Point List ▸ Extents).</summary>
    public static List<Point3> Extents(BoundingBox b) =>
    [
        new(b.Min.X, b.Min.Y, b.Min.Z), new(b.Max.X, b.Min.Y, b.Min.Z), new(b.Max.X, b.Max.Y, b.Min.Z),
        new(b.Min.X, b.Max.Y, b.Min.Z), new(b.Center.X, b.Center.Y, b.Min.Z),
    ];

    /// <summary>Even-odd point in polygon.</summary>
    private static bool Inside(List<Point2> poly, Point2 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>A path walked by length: the point and the direction at a distance from its start.</summary>
    private sealed class Walk
    {
        private readonly List<Point2> _pts;
        private readonly List<double> _at = [0];

        public Walk(Path2 path)
        {
            _pts = path.Flatten(0.0005);
            if (path.Closed && _pts.Count > 0) _pts.Add(_pts[0]);
            for (var i = 1; i < _pts.Count; i++) _at.Add(_at[^1] + Point2.Distance(_pts[i - 1], _pts[i]));
        }

        public double Length => _at[^1];

        private int Index(double s)
        {
            var i = _at.BinarySearch(s);
            if (i < 0) i = ~i;
            return Math.Clamp(i, 1, _pts.Count - 1);
        }

        public Point2 At(double s)
        {
            if (_pts.Count == 1) return _pts[0];
            var i = Index(s);
            var len = _at[i] - _at[i - 1];
            return len < 1e-12 ? _pts[i] : Point2.Lerp(_pts[i - 1], _pts[i], Math.Clamp((s - _at[i - 1]) / len, 0, 1));
        }

        public Point2 Direction(double s)
        {
            if (_pts.Count < 2) return new Point2(1, 0);
            var i = Index(s);
            // Skip zero-length pieces.
            while (i < _pts.Count - 1 && Point2.Distance(_pts[i - 1], _pts[i]) < 1e-12) i++;
            return (_pts[i] - _pts[i - 1]).Normalized();
        }
    }
}
