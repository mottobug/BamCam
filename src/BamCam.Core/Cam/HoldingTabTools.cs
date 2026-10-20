using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>A holding tab as displayed in the drawing view: a rectangle centred on the toolpath.</summary>
public readonly record struct TabMarker(
    HoldingTab Tab,
    Point2 SourcePoint,
    Point2 Center,
    Point2 Tangent,
    double Length,
    double Breadth,
    bool Pending)
{
    /// <summary>Corners of the tab rectangle (drawing XY).</summary>
    public Point2[] Corners()
    {
        var t = Tangent * (Length / 2);
        var n = Tangent.Perp * (Breadth / 2);
        return [Center - t - n, Center + t - n, Center + t + n, Center - t + n];
    }

    public bool Contains(Point2 p)
    {
        var d = p - Center;
        return Math.Abs(Point2.Dot(d, Tangent)) <= Length / 2 && Math.Abs(Point2.Dot(d, Tangent.Perp)) <= Breadth / 2;
    }
}

/// <summary>
/// Holding tab geometry shared by the toolpath generator and the interactive tab editing:
/// tabs are stored as an arc-length fraction along the source shape plus its outward normal.
/// </summary>
public static class HoldingTabTools
{
    /// <summary>Closed source outlines of a profile (MOP transform applied), keyed by entity id.</summary>
    public static IEnumerable<(int Id, Path2 Path)> SourceShapes(CadDocument doc, ProfileOp op)
    {
        var mt = op.MOPTransform.Value;
        foreach (var id in op.PrimitiveIds)
        {
            if (doc.FindEntity(id) is not { } e) continue;
            foreach (var p in e.ToPaths())
                if (p.Closed && p.Count >= 2) yield return (id, mt.IsIdentity ? p : p.Transformed(mt));
        }
    }

    private static List<Point2> Outline(Path2 path)
    {
        var pts = path.Flatten(ToolpathGenerator.ChordTolerance);
        if (path.Closed && pts.Count > 0) pts.Add(pts[0]);
        return pts;
    }

    /// <summary>Arc-length fraction (0..1) of the point on <paramref name="path"/> nearest to <paramref name="p"/>.</summary>
    public static double FractionAt(Path2 path, Point2 p, out Point2 nearest, out double distance)
    {
        var pts = Outline(path);
        double total = 0, acc = 0, bestS = 0;
        distance = double.PositiveInfinity;
        nearest = pts.Count > 0 ? pts[0] : p;
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var ab = pts[i] - a;
            var len = ab.Length;
            var t = len < 1e-12 ? 0 : Math.Clamp(Point2.Dot(p - a, ab) / (len * len), 0, 1);
            var q = a + ab * t;
            var d = Point2.Distance(q, p);
            if (d < distance)
            {
                distance = d;
                nearest = q;
                bestS = acc + t * len;
            }
            acc += len;
        }
        total = acc;
        return total < 1e-12 ? 0 : bestS / total;
    }

    /// <summary>Point and unit tangent (in path direction) at arc-length fraction <paramref name="fraction"/>.</summary>
    public static (Point2 Point, Point2 Tangent) Evaluate(Path2 path, double fraction)
    {
        var pts = Outline(path);
        if (pts.Count < 2) return (pts.Count > 0 ? pts[0] : Point2.Zero, new Point2(1, 0));
        double total = 0;
        for (var i = 1; i < pts.Count; i++) total += Point2.Distance(pts[i - 1], pts[i]);
        var target = Math.Clamp(fraction, 0, 1) * total;
        double acc = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var seg = Point2.Distance(pts[i - 1], pts[i]);
            if (seg < 1e-12) continue;
            if (acc + seg >= target - 1e-12 || i == pts.Count - 1)
                return (Point2.Lerp(pts[i - 1], pts[i], Math.Clamp((target - acc) / seg, 0, 1)), (pts[i] - pts[i - 1]) / seg);
            acc += seg;
        }
        return (pts[^1], (pts[^1] - pts[^2]).Normalized());
    }

    /// <summary>Unit outward normal of a closed outline at the given fraction.</summary>
    public static Point2 OutwardNormal(Path2 path, double fraction)
    {
        var (_, t) = Evaluate(path, fraction);
        // Left of the direction of travel is inside for counter-clockwise outlines.
        return path.SignedArea() > 0 ? -t.Perp : t.Perp;
    }

    /// <summary>A tab on <paramref name="path"/> at the point nearest to <paramref name="near"/>.</summary>
    public static HoldingTab CreateTab(int entityId, Path2 path, Point2 near)
    {
        var f = FractionAt(path, near, out _, out _);
        return new HoldingTab { ParentEntityID = entityId, ParametricPoint = f, Normal = OutwardNormal(path, f) };
    }

    /// <summary>Moves <paramref name="tab"/> to the point of its parent shape nearest to <paramref name="p"/>.</summary>
    public static void MoveTab(HoldingTab tab, Path2 path, Point2 p)
    {
        tab.ParametricPoint = FractionAt(path, p, out _, out _);
        tab.Normal = OutwardNormal(path, tab.ParametricPoint);
    }

    /// <summary>The closest closed source shape of <paramref name="op"/> to a point.</summary>
    public static (int Id, Path2 Path, double Distance)? NearestShape(CadDocument doc, ProfileOp op, Point2 p)
    {
        (int, Path2, double)? best = null;
        foreach (var (id, path) in SourceShapes(doc, op))
        {
            FractionAt(path, p, out _, out var d);
            if (best == null || d < best.Value.Item3) best = (id, path, d);
        }
        return best;
    }

    /// <summary>
    /// The tabs currently in effect: the op's own list for Manual, otherwise the placements the last
    /// toolpath generation computed.
    /// </summary>
    public static List<HoldingTab> EffectiveTabs(ProfileOp op, Toolpath? toolpath)
    {
        var method = op.HoldingTabs.Value.TabMethod;
        if (method == TabMethod.None) return [];
        if (method == TabMethod.Manual) return op.Tabs;
        return toolpath?.TabPlacements ?? [];
    }

    /// <summary>
    /// Switches to Manual tabs, copying any automatically placed tabs into the op's tab list so they
    /// stay where they are.
    /// </summary>
    public static void MakeManual(ProfileOp op, Toolpath? toolpath)
    {
        var s = op.HoldingTabs.Value;
        if (s.TabMethod == TabMethod.Manual) return;
        if (s.TabMethod != TabMethod.None)
        {
            op.Tabs.Clear();
            op.Tabs.AddRange(EffectiveTabs(op, toolpath).Select(t => t.Clone()));
        }
        op.HoldingTabs.Set(s with { TabMethod = TabMethod.Manual });
    }

    /// <summary>Display rectangles for the tabs of a profile.</summary>
    public static List<TabMarker> Markers(CadDocument doc, ProfileOp op, Toolpath? toolpath)
    {
        var result = new List<TabMarker>();
        var tabs = EffectiveTabs(op, toolpath);
        if (tabs.Count == 0) return result;
        var shapes = SourceShapes(doc, op).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Path);
        var dia = op.EffectiveToolDiameter(doc);
        var s = op.HoldingTabs.Value;
        var offset = dia / 2 + op.RoughingClearance.Value;
        var side = op.InsideOutside.Value == InsideOutside.Outside ? 1 : -1;
        var manual = s.TabMethod == TabMethod.Manual;
        foreach (var tab in tabs)
        {
            if (!shapes.TryGetValue(tab.ParentEntityID, out var path)) continue;
            var (p, tangent) = Evaluate(path, tab.ParametricPoint);
            var n = OutwardNormal(path, tab.ParametricPoint);
            var pending = manual && toolpath != null && !toolpath.TabPlacements.Any(t =>
                t.ParentEntityID == tab.ParentEntityID && Math.Abs(t.ParametricPoint - tab.ParametricPoint) < 1e-6);
            result.Add(new TabMarker(tab, p, p + n * (offset * side), tangent, s.Width + dia, dia * 1.5, pending));
        }
        return result;
    }
}
