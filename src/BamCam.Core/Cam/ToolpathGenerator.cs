using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>Computes toolpaths for machining operations.</summary>
public static class ToolpathGenerator
{
    /// <summary>Chord tolerance used when flattening source geometry.</summary>
    /// <summary>
    /// Chord tolerance when flattening source geometry before offsetting. Exactness of the output does not come
    /// from this value but from OffsetSnapper (exact arcs, lines and junctions); finer values make offsets of dense
    /// outlines quadratically slower and arc fitting worse.
    /// </summary>
    public const double ChordTolerance = 1e-4;

    public static Toolpath Generate(CadDocument doc, MachineOp op)
    {
        var ctx = new GenContext(doc, op);
        var tp = new Toolpath();
        try
        {
            switch (op)
            {
                case ProfileOp p: ProfileGenerator.Generate(ctx, p, tp); break;
                case PocketOp p: PocketGenerator.Generate(ctx, p, tp); break;
                case EngraveOp e: EngraveGenerator.Generate(ctx, e, tp); break;
                case DrillOp d: DrillGenerator.Generate(ctx, d, tp); break;
                case Profile3DOp p3: Profile3DGenerator.Generate(ctx, p3, tp); break;
                case NcFileOp nc:
                {
                    var path = ResolveNcPath(doc, nc);
                    if (path == null) tp.Warnings.Add(Tr.F("{0}: G-code file '{1}' not found.", op.Name, nc.SourceFile.Value));
                    else
                    {
                        var parsed = GCodeParser.Parse(File.ReadAllText(path));
                        tp.Moves.AddRange(parsed.Moves);
                        tp.StartPosition = parsed.StartPosition;
                    }
                    break;
                }
                default: tp.Warnings.Add(Tr.F("{0}: toolpath generation for '{1}' is not supported yet.", op.Name, Tr.T(op.DisplayType))); break;
            }
        }
        catch (Exception ex)
        {
            tp.Warnings.Add(Tr.F("{0}: toolpath generation failed: {1}", op.Name, ex.Message));
        }
        if (op is not NcFileOp) tp.TrimTrailingRetracts();
        if (tp.Moves.Count == 0 && tp.Warnings.Count == 0)
            tp.Warnings.Add(Tr.F("{0}: no toolpath was generated (check the selected drawing objects).", op.Name));
        return tp;
    }

    /// <summary>Resolves an NC file path (absolute, or relative to the drawing).</summary>
    public static string? ResolveNcPath(CadDocument doc, NcFileOp op)
    {
        var src = op.SourceFile.Value;
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (File.Exists(src)) return Path.GetFullPath(src);
        if (doc.FilePath != null)
        {
            var rel = Path.Combine(Path.GetDirectoryName(doc.FilePath)!, src);
            if (File.Exists(rel)) return rel;
        }
        return null;
    }
}

/// <summary>Resolved inputs shared by the generators.</summary>
internal sealed class GenContext
{
    public GenContext(CadDocument doc, MachineOp op)
    {
        Doc = doc;
        Op = op;
        Part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        ToolDiameter = op.EffectiveToolDiameter(doc);
        var stock = Part is { Stock.IsEmpty: false } ? Part.Stock : doc.Machining.Stock;
        Stock = stock;

        StockSurface = op.StockSurface.State == ParamState.Auto && !stock.IsEmpty ? stock.SurfaceZ : op.StockSurface.Value;
        if (op is DepthOp d)
        {
            TargetDepth = d.TargetDepth.State == ParamState.Auto && !stock.IsEmpty ? stock.PMin.Z : d.TargetDepth.Value;
            if (TargetDepth > StockSurface)
            {
                Warnings.Add(Tr.F("{0}: Target Depth is above Stock Surface; using Stock Surface.", op.Name));
                TargetDepth = StockSurface;
            }
        }
        Clearance = op.ClearancePlane.Value;
        if (Clearance < StockSurface) Clearance = StockSurface + Math.Abs(op.ClearancePlane.Value);
        // -1 (default when absent from the file) = one minor grid unit.
        FastPlunge = doc.Machining.FastPlungeHeight < 0 ? (doc.Units == DrawingUnits.Inches ? 1.0 / 16 : 1) : doc.Machining.FastPlungeHeight;
    }

    public CadDocument Doc { get; }
    public MachineOp Op { get; }
    public Part? Part { get; }
    public Stock Stock { get; }
    public double ToolDiameter { get; }
    public double ToolRadius => ToolDiameter / 2;
    public double StockSurface { get; }
    public double TargetDepth { get; }
    public double Clearance { get; }
    public double FastPlunge { get; }
    public List<string> Warnings { get; } = [];

    public double PlungeFeed => Op.PlungeFeedrate.Value;
    public double CutFeed => Op.CutFeedrate.Value;

    public IEnumerable<(Entity Entity, Path2 Path)> SourcePaths()
    {
        var mt = Op.MOPTransform.Value;
        foreach (var id in Op.PrimitiveIds)
        {
            var e = Doc.FindEntity(id);
            if (e == null)
            {
                Warnings.Add(Tr.F("{0}: drawing object {1} not found.", Op.Name, id));
                continue;
            }
            foreach (var p in e.ToPaths())
                yield return (e, mt.IsIdentity ? p : p.Transformed(mt));
        }
    }

    /// <summary>Depth levels with the given increment rules (from just below stock surface to target depth).</summary>
    public List<double> Levels(DepthOp op)
    {
        var levels = new List<double>();
        var top = StockSurface;
        var bottom = TargetDepth;
        if (bottom >= top - 1e-9)
        {
            levels.Add(top);
            return levels;
        }
        var inc = op.DepthIncrement.Value <= 1e-9 ? top - bottom : op.DepthIncrement.Value;
        var finalInc = Math.Clamp(op.FinalDepthIncrement.Value, 0, top - bottom);
        var roughBottom = bottom + finalInc;
        var z = top;
        while (z - inc > roughBottom + 1e-9)
        {
            z = Math.Round(z - inc, 9);
            levels.Add(z);
        }
        if (finalInc > 1e-9 && roughBottom < top - 1e-9 && (levels.Count == 0 || Math.Abs(levels[^1] - roughBottom) > 1e-9))
            levels.Add(roughBottom);
        levels.Add(bottom);
        return levels;
    }

    public void Finish(Toolpath tp) => tp.Warnings.AddRange(Warnings);
}

/// <summary>A closed or open cutting loop as a flattened point list (XY), with its cut metadata.</summary>
internal sealed class Loop
{
    public List<Point2> Points { get; init; } = [];
    public bool Closed { get; init; } = true;
    public int SourceId { get; init; }
    /// <summary>Source outline the loop was offset from (for tabs and corner overcuts).</summary>
    public Path2? Source { get; init; }
    public bool IsOutside { get; init; }

    /// <summary>Geometry this loop was offset from, and the distance, for exact snapping (see OffsetSnapper).</summary>
    public IReadOnlyList<Path2>? SnapSources { get; init; }
    public double SnapOffset { get; init; }

    public double Length
    {
        get
        {
            double len = 0;
            for (var i = 1; i < Points.Count; i++) len += Point2.Distance(Points[i - 1], Points[i]);
            if (Closed && Points.Count > 1) len += Point2.Distance(Points[^1], Points[0]);
            return len;
        }
    }

    public Point2 Start => Points[0];
    public Point2 End => Closed ? Points[0] : Points[^1];

    public double SignedArea()
    {
        double a = 0;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
            a += (Points[j].X * Points[i].Y - Points[i].X * Points[j].Y) / 2;
        return a;
    }

    public Loop WithPoints(List<Point2> pts) => new()
    {
        Points = pts, Closed = Closed, SourceId = SourceId, Source = Source, IsOutside = IsOutside,
        SnapSources = SnapSources, SnapOffset = SnapOffset,
    };

    public Loop Reversed()
    {
        var pts = new List<Point2>(Points);
        pts.Reverse();
        if (Closed && pts.Count > 0)
        {
            // Keep the same start point.
            pts.Insert(0, pts[^1]);
            pts.RemoveAt(pts.Count - 1);
        }
        return WithPoints(pts);
    }

    public Loop Oriented(bool ccw) => (SignedArea() > 0) == ccw ? this : Reversed();

    /// <summary>Rotates a closed loop so that it starts at the point closest to <paramref name="target"/> (inserting it on a segment).</summary>
    public Loop StartNearest(Point2 target)
    {
        if (!Closed || Points.Count < 2) return this;
        var bestDist = double.PositiveInfinity;
        var bestIdx = 0;
        var bestPt = Points[0];
        for (var i = 0; i < Points.Count; i++)
        {
            var a = Points[i];
            var b = Points[(i + 1) % Points.Count];
            var ab = b - a;
            var len2 = ab.LengthSquared;
            var t = len2 < 1e-18 ? 0 : Math.Clamp(Point2.Dot(target - a, ab) / len2, 0, 1);
            var p = a + ab * t;
            var d = Point2.Distance(p, target);
            if (d < bestDist - 1e-9)
            {
                bestDist = d;
                bestIdx = i;
                bestPt = p;
            }
        }
        var result = new List<Point2> { bestPt };
        var n = Points.Count;
        for (var k = 1; k <= n; k++)
        {
            var p = Points[(bestIdx + k) % n];
            if (!p.AlmostEquals(result[^1], 1e-7) && !(k == n && p.AlmostEquals(bestPt, 1e-7))) result.Add(p);
        }
        if (result.Count > 1 && result[^1].AlmostEquals(result[0], 1e-7)) result.RemoveAt(result.Count - 1);
        return WithPoints(result);
    }

    public Path2 ToPath(double z) => Path2.FromPoints(Points, Closed, z);
}

internal static class LoopTools
{
    public static List<Point2> Clean(IEnumerable<Point2> pts)
    {
        var result = new List<Point2>();
        foreach (var p in pts)
            if (result.Count == 0 || !result[^1].AlmostEquals(p, 1e-7)) result.Add(p);
        if (result.Count > 2 && result[^1].AlmostEquals(result[0], 1e-7)) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>
    /// Orders loops by nearest neighbour from <paramref name="from"/>, cutting nested (inner) loops first. Distance is
    /// measured to the nearest point of each loop, and closed loops then start there (reference behaviour).
    /// </summary>
    public static List<Loop> OrderLoops(IEnumerable<Loop> loops, Point2 from, OptimisationMode mode)
    {
        var list = loops.ToList();
        if (mode == OptimisationMode.None || list.Count < 2) return list;
        // Loops whose bounds are contained in another loop's bounds are cut before it.
        var depth = list.ToDictionary(l => l, l => list.Count(o => !ReferenceEquals(o, l) && Contains(o, l)));
        var result = new List<Loop>();
        var pos = from;
        while (list.Count > 0)
        {
            var maxDepth = list.Max(l => depth[l]);
            var candidates = list.Where(l => depth[l] == maxDepth).ToList();
            var next = candidates.OrderBy(l => DistanceTo(l, pos)).First();
            list.Remove(next);
            var started = next.Closed ? StartAt(next, pos) : next;
            result.Add(started);
            pos = started.End;
        }
        return result;
    }

    /// <summary>Distance from <paramref name="p"/> to the nearest point of the loop (end points for open loops).</summary>
    private static double DistanceTo(Loop loop, Point2 p)
    {
        if (!loop.Closed) return Math.Min(Point2.Distance(loop.Start, p), Point2.Distance(loop.End, p));
        var best = double.PositiveInfinity;
        var pts = loop.Points;
        for (var i = 0; i < pts.Count; i++)
            best = Math.Min(best, Path2.DistanceToSegment(p, pts[i], pts[(i + 1) % pts.Count]));
        return best;
    }
    /// <summary>True when <paramref name="inner"/> lies inside the closed <paramref name="outer"/> loop (geometrically, not just by bounds).</summary>
    private static bool Contains(Loop outer, Loop inner)
    {
        if (!outer.Closed || ReferenceEquals(outer, inner)) return false;
        var bo = BoundingBox.FromPoints(outer.Points.Select(p => p.ToPoint3()));
        var bi = BoundingBox.FromPoints(inner.Points.Select(p => p.ToPoint3()));
        if (!bo.ContainsXY(bi) || !(bo.Width > bi.Width + 1e-6 || bo.Height > bi.Height + 1e-6)) return false;
        var pts = outer.Points;
        var p = inner.Points[0];
        var inside = false;
        for (int i = 0, k = pts.Count - 1; i < pts.Count; k = i++)
            if ((pts[i].Y > p.Y) != (pts[k].Y > p.Y) &&
                p.X < (pts[k].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[k].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// Starts a closed loop near <paramref name="target"/>. Circular loops start exactly on the ray from the
    /// centre through the target (a radial stepover, like the reference output).
    /// </summary>
    public static Loop StartAt(Loop loop, Point2 target)
    {
        var l = loop.StartNearest(target);
        if (!l.Closed || l.Points.Count < 8) return l;
        // The polygon only approximates arcs, so its nearest point is off by up to half a chord. If the start lies
        // on an arc (a whole circle or part of a curve), use the exact nearest point of that circle instead.
        if (LocalCircle(l.Points) is not { } circle) return l;
        var (c, r) = circle;
        // Exact circle the loop was offset from (source radius ± offset), so the start lies on the snapped path.
        if (l.SnapSources is { Count: > 0 } src && OffsetSnapper.ExactCircle(c, r, src, l.SnapOffset, out var ec, out var er))
            (c, r) = (ec, er);
        if (Point2.Distance(target, c) < 1e-9) return l;
        var start = c + (target - c).Normalized() * r;
        if (Point2.Distance(start, l.Points[0]) > 0.05) return l;
        // Replace the start and drop neighbours closer than 1 µm (they would form a micro kink).
        var pts = new List<Point2> { start };
        pts.AddRange(l.Points.Skip(1).Where(p => Point2.Distance(p, start) > 1e-3));
        return l.WithPoints(pts);
    }

    /// <summary>Circle through the points around index 0 of a closed loop, if they lie on one.</summary>
    private static (Point2 Center, double Radius)? LocalCircle(List<Point2> pts)
    {
        var n = pts.Count;
        // A whole circle is fitted best from the whole loop.
        if (ArcFitter.TryCircle(pts, 1e-3, out var fc, out var fr)) return (fc, fr);
        // Otherwise the widest span around the start that still lies on one circle (wider = more accurate centre).
        foreach (var m in new[] { 64, 32, 16, 8, 4, 2 })
        {
            if (2 * m + 1 > n) continue;
            if (Arc2.FromThreePoints(pts[n - m], pts[0], pts[m]) is not { } arc || arc.Radius > 1e4) continue;
            var ok = true;
            for (var k = -m; k <= m && ok; k++)
                ok = Math.Abs(Point2.Distance(pts[(k + n) % n], arc.Center) - arc.Radius) < 1e-3;
            if (ok) return (arc.Center, arc.Radius);
        }
        return null;
    }
    /// <summary>Point at arc-length fraction <paramref name="t"/> (0..1) along a path.</summary>
    public static Point2 PointAtFraction(Path2 path, double t)
    {
        var pts = path.Flatten(ToolpathGenerator.ChordTolerance);
        if (path.Closed) pts.Add(pts[0]);
        double total = 0;
        for (var i = 1; i < pts.Count; i++) total += Point2.Distance(pts[i - 1], pts[i]);
        var target = Math.Clamp(t, 0, 1) * total;
        double acc = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var seg = Point2.Distance(pts[i - 1], pts[i]);
            if (acc + seg >= target - 1e-12)
                return seg < 1e-12 ? pts[i] : Point2.Lerp(pts[i - 1], pts[i], (target - acc) / seg);
            acc += seg;
        }
        return pts[^1];
    }

    /// <summary>Point at arc-length <paramref name="s"/> along a closed point loop.</summary>
    public static Point2 PointAtDistance(List<Point2> pts, double s)
    {
        double acc = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var len = Point2.Distance(a, b);
            if (acc + len >= s && len > 1e-12) return Point2.Lerp(a, b, (s - acc) / len);
            acc += len;
        }
        return pts[0];
    }

    /// <summary>Arc-length position of the point on the loop closest to <paramref name="p"/>.</summary>
    public static double ProjectDistance(List<Point2> pts, bool closed, Point2 p)
    {
        double best = double.PositiveInfinity, bestS = 0, acc = 0;
        var n = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var ab = b - a;
            var len = ab.Length;
            var t = len < 1e-12 ? 0 : Math.Clamp(Point2.Dot(p - a, ab) / (len * len), 0, 1);
            var d = Point2.Distance(a + ab * t, p);
            if (d < best)
            {
                best = d;
                bestS = acc + t * len;
            }
            acc += len;
        }
        return bestS;
    }
}
