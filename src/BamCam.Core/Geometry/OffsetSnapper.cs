namespace BamCam.Core.Geometry;

/// <summary>
/// Makes an offset toolpath exact. Offsetting works on flattened polygons, so arcs and junctions come out with
/// small errors (micrometres) that show up in G-code coordinates. After arc fitting, each element is matched to
/// the source geometry it was offset from:
/// <list type="bullet">
/// <item>arcs concentric with a source arc get that centre and the source radius ± offset;</item>
/// <item>arcs centred on a source vertex (rounded corners) get that vertex and the offset as radius;</item>
/// <item>lines parallel to a source line at the offset distance are moved exactly onto it.</item>
/// </list>
/// Junctions between two matched elements are recomputed as exact intersections (tangent points for smooth
/// transitions). Elements that match nothing are left unchanged.
/// </summary>
public static class OffsetSnapper
{
    private sealed class Element
    {
        public bool IsArc;
        public Point2 Center;
        public double Radius;
        public Point2 LinePoint;
        public Point2 LineDir;
        public bool Snapped;
    }

    public static Path2 Snap(Path2 fitted, IReadOnlyList<Path2> sources, double offset, double tol = 0.01)
    {
        fitted = DropMicroLines(fitted, 2 * tol);
        var segs = fitted.Segments().ToList();
        if (segs.Count == 0 || sources.Count == 0) return fitted;
        offset = Math.Abs(offset);

        var srcArcs = new List<Arc2>();
        var srcLines = new List<(Point2 A, Point2 B)>();
        var srcVertices = new List<Point2>();
        foreach (var s in sources)
        {
            foreach (var v in s.Vertices) srcVertices.Add(v.XY);
            foreach (var seg in s.Segments())
            {
                if (seg.IsArc) srcArcs.Add(seg.ToArc());
                else if (!seg.Start.XY.AlmostEquals(seg.End.XY, 1e-9)) srcLines.Add((seg.Start.XY, seg.End.XY));
            }
        }

        var elements = new List<Element>();
        foreach (var seg in segs)
        {
            var e = new Element();
            if (seg.IsArc)
            {
                var arc = seg.ToArc();
                e.IsArc = true;
                e.Center = arc.Center;
                e.Radius = arc.Radius;
                SnapArc(e, srcArcs, srcVertices, offset, tol);
            }
            else
            {
                e.LinePoint = seg.Start.XY;
                e.LineDir = (seg.End.XY - seg.Start.XY).Normalized();
                SnapLine(e, srcLines, offset, tol);
            }
            elements.Add(e);
        }
        if (elements.All(e => !e.Snapped)) return fitted;

        var n = segs.Count;
        var verts = fitted.Vertices.Select(v => v.Point).ToList();
        var newVerts = new List<Point3>(verts);
        for (var k = 0; k < verts.Count; k++)
        {
            // Element ending at vertex k and element starting at it.
            int? before = fitted.Closed ? (k - 1 + n) % n : k - 1 >= 0 ? k - 1 : null;
            int? after = k < n ? k : null;
            var p = verts[k].XY;
            Point2? q = null;
            if (before is { } b0 && after is { } a0 && k > 0 && SameCircle(elements[b0], elements[a0]))
            {
                // Arcs of one circle (a full circle split into 120° arcs): rotate the previous exact vertex by
                // the arc's sweep so the split points are exact too.
                var sweep = 4 * Math.Atan(fitted.Vertices[k - 1].Bulge);
                q = newVerts[k - 1].XY.Rotate(sweep, elements[b0].Center);
            }
            else if (before is { } b && after is { } a && elements[b].Snapped && elements[a].Snapped)
                q = Intersect(elements[b], elements[a], p, tol);
            else if (before is { } b2 && elements[b2].Snapped && (after == null || !elements[after.Value].Snapped))
                q = Project(elements[b2], p);
            else if (after is { } a2 && elements[a2].Snapped)
                q = Project(elements[a2], p);
            // Arc fitting may absorb a few points of a neighbouring corner, so junctions can move by a fraction
            // of a millimetre; anything further means the match was wrong.
            if (q is { } qq && Point2.Distance(qq, p) < 50 * tol) newVerts[k] = qq.ToPoint3(verts[k].Z);
        }

        // Rebuild bulges of arcs from their (possibly moved) end points.
        var result = new List<Vertex>();
        for (var k = 0; k < newVerts.Count; k++)
        {
            var bulge = fitted.Vertices[k].Bulge;
            if (k < n && elements[k].IsArc && elements[k].Snapped)
            {
                var start = newVerts[k].XY;
                var end = newVerts[(k + 1) % newVerts.Count].XY;
                var oldSweep = 4 * Math.Atan(bulge);
                var a0 = (start - elements[k].Center).Angle;
                var a1 = (end - elements[k].Center).Angle;
                var sweep = oldSweep >= 0 ? Angles.Normalize(a1 - a0) : -Angles.Normalize(a0 - a1);
                if (Math.Abs(sweep) < 1e-12 && Math.Abs(oldSweep) > Math.PI) sweep = oldSweep >= 0 ? Angles.TwoPi : -Angles.TwoPi;
                // Keep the original if the recomputed sweep is not the same arc (e.g. wrapped around).
                if (Math.Abs(sweep - oldSweep) < Math.PI / 4) bulge = Math.Tan(sweep / 4);
            }
            result.Add(new Vertex(newVerts[k], bulge));
        }
        return new Path2(result, fitted.Closed);
    }

    /// <summary>
    /// Removes straight segments shorter than <paramref name="minLength"/> between two other elements (offset
    /// leftovers next to a tangent arc). Their direction is numerically meaningless, so they could not be matched;
    /// the following element simply starts where the micro line started, and snapping makes the junction exact.
    /// </summary>
    private static Path2 DropMicroLines(Path2 path, double minLength)
    {
        var v = path.Vertices;
        var n = v.Count;
        var segCount = path.SegmentCount;
        if (segCount < 3) return path;
        var drop = new HashSet<int>();
        for (var i = 0; i < segCount; i++)
        {
            if (!path.Closed && (i == 0 || i == segCount - 1)) continue; // keep the run's ends
            var a = v[i];
            var b = v[(i + 1) % n];
            if (Math.Abs(a.Bulge) > 1e-12 || Point2.Distance(a.XY, b.XY) >= minLength) continue;
            var endIndex = (i + 1) % n;
            if (!path.Closed && endIndex == n - 1) continue;
            if (drop.Contains(i)) continue;
            drop.Add(endIndex);
        }
        if (drop.Count == 0) return path;
        var result = new List<Vertex>();
        for (var i = 0; i < n; i++)
        {
            if (!drop.Contains(i)) { result.Add(v[i]); continue; }
            // The micro line's start vertex takes over the bulge of the element that followed it.
            result[^1] = result[^1] with { Bulge = v[i].Bulge };
        }
        return new Path2(result, path.Closed);
    }

    /// <summary>Exact centre and radius of a circular offset loop (source circle radius ± offset), if it matches one.</summary>
    public static bool ExactCircle(Point2 center, double radius, IReadOnlyList<Path2> sources, double offset,
        out Point2 exactCenter, out double exactRadius, double tol = 0.01)
    {
        var e = new Element { IsArc = true, Center = center, Radius = radius };
        var arcs = sources.SelectMany(s => s.Segments()).Where(s => s.IsArc).Select(s => s.ToArc()).ToList();
        SnapArc(e, arcs, [], Math.Abs(offset), tol);
        exactCenter = e.Center;
        exactRadius = e.Radius;
        return e.Snapped;
    }

    private static void SnapArc(Element e, List<Arc2> arcs, List<Point2> vertices, double offset, double tol)
    {
        foreach (var a in arcs)
        {
            if (Point2.Distance(a.Center, e.Center) > tol) continue;
            foreach (var r in new[] { a.Radius + offset, a.Radius - offset })
            {
                if (r <= 0 || Math.Abs(r - e.Radius) > tol) continue;
                e.Center = a.Center;
                e.Radius = r;
                e.Snapped = true;
                return;
            }
        }
        if (offset <= 0 || Math.Abs(e.Radius - offset) > tol) return;
        foreach (var v in vertices)
        {
            if (Point2.Distance(v, e.Center) > tol) continue;
            e.Center = v;
            e.Radius = offset;
            e.Snapped = true;
            return;
        }
    }

    private static void SnapLine(Element e, List<(Point2 A, Point2 B)> lines, double offset, double tol)
    {
        foreach (var (a, b) in lines)
        {
            var dir = (b - a).Normalized();
            if (Math.Abs(Point2.Cross(dir, e.LineDir)) > 1e-4) continue;
            var normal = dir.Perp;
            var dist = Point2.Dot(e.LinePoint - a, normal);
            if (Math.Abs(Math.Abs(dist) - offset) > tol) continue;
            e.LinePoint = a + normal * (Math.Sign(dist) * offset);
            e.LineDir = Point2.Dot(dir, e.LineDir) >= 0 ? dir : -dir;
            e.Snapped = true;
            return;
        }
    }

    private static bool SameCircle(Element a, Element b) =>
        a.IsArc && b.IsArc && a.Snapped && b.Snapped && a.Center.AlmostEquals(b.Center, 1e-9) && Math.Abs(a.Radius - b.Radius) < 1e-9;

    private static Point2 Project(Element e, Point2 p)
    {
        if (e.IsArc)
        {
            var d = p - e.Center;
            return d.Length < 1e-12 ? p : e.Center + d.Normalized() * e.Radius;
        }
        return e.LinePoint + e.LineDir * Point2.Dot(p - e.LinePoint, e.LineDir);
    }

    /// <summary>Intersection of two elements nearest <paramref name="near"/>; tangent contacts resolve to the touching point.</summary>
    private static Point2? Intersect(Element a, Element b, Point2 near, double tol)
    {
        if (!a.IsArc && !b.IsArc)
        {
            var cross = Point2.Cross(a.LineDir, b.LineDir);
            if (Math.Abs(cross) < 1e-9) return Project(b, near); // collinear continuation
            var t = Point2.Cross(b.LinePoint - a.LinePoint, b.LineDir) / cross;
            return a.LinePoint + a.LineDir * t;
        }
        if (a.IsArc && b.IsArc)
        {
            var d = Point2.Distance(a.Center, b.Center);
            if (d < 1e-9) return Project(a, near); // same circle (e.g. a circle split into arcs)
            return CircleCircle(a.Center, a.Radius, b.Center, b.Radius, near, tol);
        }
        var line = a.IsArc ? b : a;
        var arc = a.IsArc ? a : b;
        return LineCircle(line.LinePoint, line.LineDir, arc.Center, arc.Radius, near, tol);
    }

    private static Point2? LineCircle(Point2 p, Point2 dir, Point2 c, double r, Point2 near, double tol)
    {
        var foot = p + dir * Point2.Dot(c - p, dir);
        var h = Point2.Distance(foot, c);
        if (h > r + tol) return null;
        if (h >= r) return foot; // tangent
        var s = Math.Sqrt(r * r - h * h);
        var q1 = foot + dir * s;
        var q2 = foot - dir * s;
        // Nearly tangent: both roots are the touching point.
        if (s < tol * 0.5) return foot;
        return Point2.Distance(q1, near) <= Point2.Distance(q2, near) ? q1 : q2;
    }

    private static Point2? CircleCircle(Point2 c1, double r1, Point2 c2, double r2, Point2 near, double tol)
    {
        var d = Point2.Distance(c1, c2);
        var u = (c2 - c1) / d;
        // Tangent (external or internal) within tolerance: touching point on the centre line.
        if (Math.Abs(d - (r1 + r2)) < tol * 0.5) return c1 + u * r1;
        if (Math.Abs(d - Math.Abs(r1 - r2)) < tol * 0.5) return c1 + u * (r1 >= r2 ? r1 : -r1);
        if (d > r1 + r2 || d < Math.Abs(r1 - r2)) return null;
        var a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        var h = Math.Sqrt(Math.Max(r1 * r1 - a * a, 0));
        var m = c1 + u * a;
        var q1 = m + u.Perp * h;
        var q2 = m - u.Perp * h;
        return Point2.Distance(q1, near) <= Point2.Distance(q2, near) ? q1 : q2;
    }
}
