using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>Drawing edit operations. Each returns the entities to add; callers remove sources when appropriate.</summary>
public static class CadOps
{
    /// <summary>Converts any entity to polylines (one per path).</summary>
    public static List<PolylineEntity> ToPolylines(Entity e) =>
        e.ToPaths().Where(p => p.Count >= 2).Select(p => new PolylineEntity(p)).ToList();

    /// <summary>Splits entities into their parts: polylines → lines/arcs, point lists → points, regions → polylines.</summary>
    public static List<Entity> Explode(Entity e)
    {
        var result = new List<Entity>();
        switch (e)
        {
            case PointListEntity pl:
                foreach (var p in pl.WorldPoints())
                {
                    var single = new PointListEntity();
                    single.Points.Add(p);
                    result.Add(single);
                }
                break;
            case RegionEntity or TextEntity:
                result.AddRange(ToPolylines(e));
                break;
            default:
                foreach (var path in e.ToPaths())
                foreach (var s in path.Segments())
                {
                    if (s.IsArc)
                    {
                        var a = s.ToArc();
                        result.Add(new ArcEntity
                        {
                            Center = a.Center.ToPoint3(s.Start.Z), Radius = a.Radius,
                            StartAngle = Angles.ToDegrees(a.StartAngle), Sweep = Angles.ToDegrees(a.Sweep),
                        });
                    }
                    else
                    {
                        var line = new LineEntity();
                        line.Points.Add(s.Start);
                        line.Points.Add(s.End);
                        result.Add(line);
                    }
                }
                break;
        }
        return result;
    }

    /// <summary>Joins open shapes whose end points are within <paramref name="tolerance"/>.</summary>
    public static List<PolylineEntity> Join(IEnumerable<Entity> entities, double tolerance)
    {
        var open = new List<Path2>();
        var closed = new List<Path2>();
        foreach (var e in entities)
        foreach (var p in e.ToPaths())
        {
            if (p.Count < 2) continue;
            (p.Closed ? closed : open).Add(p);
        }

        var chains = new List<Path2>();
        while (open.Count > 0)
        {
            var chain = open[0];
            open.RemoveAt(0);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < open.Count; i++)
                {
                    var o = open[i];
                    Path2? merged = null;
                    if (Near(chain.EndPoint, o.StartPoint, tolerance)) merged = Concat(chain, o);
                    else if (Near(chain.EndPoint, o.EndPoint, tolerance)) merged = Concat(chain, o.Reversed());
                    else if (Near(chain.StartPoint, o.EndPoint, tolerance)) merged = Concat(o, chain);
                    else if (Near(chain.StartPoint, o.StartPoint, tolerance)) merged = Concat(o.Reversed(), chain);
                    if (merged == null) continue;
                    chain = merged;
                    open.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            if (chain.Count > 2 && Near(chain.StartPoint, chain.EndPoint, tolerance))
            {
                chain.Vertices.RemoveAt(chain.Count - 1);
                chain.Closed = true;
            }
            chains.Add(chain);
        }
        return chains.Concat(closed).Select(p => new PolylineEntity(p)).ToList();
    }

    private static bool Near(Point3 a, Point3 b, double tol) => Point2.Distance(a.XY, b.XY) <= tol;

    private static Path2 Concat(Path2 a, Path2 b)
    {
        var verts = new List<Vertex>(a.Vertices);
        // The end vertex of a carries no outgoing bulge; replace it with b's first vertex (which carries b's first bulge).
        verts[^1] = b.Vertices[0] with { Point = verts[^1].Point };
        verts.AddRange(b.Vertices.Skip(1));
        return new Path2(verts, false);
    }

    /// <summary>Offsets closed shapes (positive = outside). Open shapes are offset as an outline around the path when <paramref name="openOutline"/>.</summary>
    public static List<PolylineEntity> Offset(IEnumerable<Entity> entities, double distance, bool openOutline = false)
    {
        var result = new List<PolylineEntity>();
        foreach (var e in entities)
        foreach (var p in e.ToPaths())
        {
            if (p.Count < 2) continue;
            var paths = p.Closed
                ? Polygons.Offset([p], distance)
                : openOutline ? Polygons.OffsetOpen(p, Math.Abs(distance)) : OffsetOpenSide(p, distance);
            result.AddRange(paths.Select(x => new PolylineEntity(ArcFitter.Fit(x, 0.005))));
        }
        return result;
    }

    /// <summary>One-sided offset of an open path (positive = right side of travel direction).</summary>
    private static List<Path2> OffsetOpenSide(Path2 p, double distance)
    {
        var pts = p.Flatten();
        if (pts.Count < 2) return [];
        var result = new List<Point2>();
        for (var i = 0; i < pts.Count; i++)
        {
            var dPrev = i > 0 ? (pts[i] - pts[i - 1]).Normalized() : (pts[1] - pts[0]).Normalized();
            var dNext = i < pts.Count - 1 ? (pts[i + 1] - pts[i]).Normalized() : dPrev;
            var nPrev = new Point2(dPrev.Y, -dPrev.X);
            var nNext = new Point2(dNext.Y, -dNext.X);
            var bis = (nPrev + nNext).Normalized();
            var cos = Point2.Dot(bis, nNext);
            var len = Math.Abs(cos) < 0.2 ? distance : distance / cos;
            result.Add(pts[i] + bis * len);
        }
        return [ArcFitter.Fit(Path2.FromPoints(result, false), 0.005)];
    }

    public static List<PolylineEntity> Union(IEnumerable<Entity> entities) =>
        Polygons.Union(ClosedPaths(entities)).Select(p => new PolylineEntity(ArcFitter.Fit(p, 0.005))).ToList();

    /// <summary>First entity minus all others.</summary>
    public static List<PolylineEntity> Subtract(IReadOnlyList<Entity> entities)
    {
        if (entities.Count < 2) return [];
        return Polygons.Difference(ClosedPaths([entities[0]]), ClosedPaths(entities.Skip(1)))
            .Select(p => new PolylineEntity(ArcFitter.Fit(p, 0.005))).ToList();
    }

    /// <summary>Intersection of the first two shapes.</summary>
    public static List<PolylineEntity> Intersect(IReadOnlyList<Entity> entities)
    {
        if (entities.Count < 2) return [];
        return Polygons.Intersection(ClosedPaths([entities[0]]), ClosedPaths([entities[1]]))
            .Select(p => new PolylineEntity(ArcFitter.Fit(p, 0.005))).ToList();
    }

    private static List<Path2> ClosedPaths(IEnumerable<Entity> entities) =>
        entities.SelectMany(e => e.ToPaths()).Where(p => p.Closed && p.Count >= 2).ToList();

    /// <summary>Builds regions (outer shape + holes) from closed shapes.</summary>
    public static List<RegionEntity> ToRegions(IEnumerable<Entity> entities)
    {
        var shapes = Polygons.BuildShapes(ClosedPaths(entities));
        return shapes.Select(s =>
        {
            var r = new RegionEntity { Outer = new PolylineEntity(ArcFitter.Fit(s.Outer, 0.005)) };
            r.Holes.AddRange(s.Holes.Select(h => new PolylineEntity(ArcFitter.Fit(h, 0.005))));
            return r;
        }).ToList();
    }

    public static void Reverse(PolylineEntity p)
    {
        var r = p.LocalPath.Reversed();
        p.Points.Clear();
        p.Points.AddRange(r.Vertices);
    }

    /// <summary>Removes consecutive duplicate points.</summary>
    public static void Clean(PolylineEntity p, double tol = 1e-6)
    {
        var src = p.Points.ToList();
        p.Points.Clear();
        foreach (var v in src)
            if (p.Points.Count == 0 || Point3.Distance(p.Points[^1].Point, v.Point) > tol) p.Points.Add(v);
        if (p.Closed && p.Points.Count > 1 && Point3.Distance(p.Points[^1].Point, p.Points[0].Point) <= tol)
            p.Points.RemoveAt(p.Points.Count - 1);
    }

    /// <summary>Bounding box of a set of entities.</summary>
    public static BoundingBox Bounds(IEnumerable<Entity> entities)
    {
        var b = BoundingBox.Empty;
        foreach (var e in entities) b = b.Union(e.Bounds());
        return b;
    }

    /// <summary>Applies a transform to entities (baking it into geometry where possible).</summary>
    public static void Transform(IEnumerable<Entity> entities, Matrix4 m)
    {
        foreach (var e in entities) e.ApplyTransform(m);
    }

    /// <summary>Evenly spaced points along shapes (Point List ▸ Divide Geometry).</summary>
    public static PointListEntity Divide(IEnumerable<Entity> entities, int count)
    {
        var result = new PointListEntity();
        foreach (var e in entities)
        foreach (var p in e.ToPaths())
        {
            var n = p.Closed ? count : Math.Max(count - 1, 1);
            for (var i = 0; i < count; i++)
                result.Points.Add(PointAt(p, (double)i / n).ToPoint3());
        }
        return result;
    }

    private static Point2 PointAt(Path2 path, double t)
    {
        var pts = path.Flatten(0.001);
        if (path.Closed) pts.Add(pts[0]);
        double total = 0;
        for (var i = 1; i < pts.Count; i++) total += Point2.Distance(pts[i - 1], pts[i]);
        var target = t * total;
        double acc = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var s = Point2.Distance(pts[i - 1], pts[i]);
            if (acc + s >= target) return s < 1e-12 ? pts[i] : Point2.Lerp(pts[i - 1], pts[i], (target - acc) / s);
            acc += s;
        }
        return pts[^1];
    }
}
