// Ported from matmill (https://github.com/jkmnt/matmill): segpool.cs, medial_builder.cs and branch.cs,
// MIT License, Copyright (c) 2016 jkmnt. See LICENSE-matmill.txt.
#nullable disable

using BamCam.Core.Geometry;
using Voronoi2;

namespace Matmill;

/// <summary>Messages of the generator: warnings and errors reach the user, the rest is dropped.</summary>
internal static class Logger
{
    [ThreadStatic] private static List<string> _messages;

    /// <summary>Collects the warnings of the calculation running on this thread.</summary>
    public static List<string> Collect() => _messages = [];

    public static void log(string s, params object[] args) { }

    public static void log(int level, string s, params object[] args) { }

    public static void warn(string s, params object[] args) => _messages?.Add(args.Length == 0 ? s : string.Format(s, args));

    public static void err(string s, params object[] args) => _messages?.Add(args.Length == 0 ? s : string.Format(s, args));
}

// special structure for a fast joining of chained line segments
// segments (references to them) are stored in dictionary under the long integer key, crafted from
// their coordinates. segment is stored twice, under the keys for start and end points.
// lookup should give a small range of nearby segments, then finepicked by the distance compare
// this way we may find next segment(s) in chain in almost O(1)

// pull operation removes chained segments from pool and return them.

// segments may map to different keys if their coordinates are rounded to neighbour integers,
// this is remedied by storing 4 hashes instead of one, with x and y coordinates floored and
// ceiled. it corresponds to 4 nearby cells in 2d grid
internal sealed class Segpool
{
    private readonly Dictionary<ulong, List<Line2F>> _pool;
    private readonly double _tolerance;

    public int N_hashes => _pool.Count;

    // As matmill, with the cells packed so that negative coordinates are fine too.
    private static ulong pack(double y, double x) => ((ulong)(uint)(long)y << 32) | (uint)(long)x;

    private ulong[] hash(Point2 pt)
    {
        var hashscale = 1 / (_tolerance * 10);
        var x = pt.X * hashscale;
        var y = pt.Y * hashscale;

        return
        [
            pack(Math.Floor(y), Math.Floor(x)),
            pack(Math.Floor(y), Math.Ceiling(x)),
            pack(Math.Ceiling(y), Math.Floor(x)),
            pack(Math.Ceiling(y), Math.Ceiling(x)),
        ];
    }

    private void insert_seg(ulong h, Line2F seg)
    {
        if (!_pool.TryGetValue(h, out var list))
            _pool[h] = list = [];
        if (!list.Contains(seg))
            list.Add(seg);
    }

    private void remove_seg(ulong h, Line2F seg)
    {
        if (_pool.TryGetValue(h, out var list))
            list.Remove(seg);
    }

    // add line hashed by the beginning or end point
    public void Add(Line2F seg, bool reverse)
    {
        var h = hash(reverse ? seg.p2 : seg.p1);
        foreach (var key in h) insert_seg(key, seg);
    }

    public void Remove(Line2F seg)
    {
        foreach (var key in hash(seg.p1)) remove_seg(key, seg);
        foreach (var key in hash(seg.p2)) remove_seg(key, seg);
    }

    public List<Point2> Pull_follow_points(Point2 join_pt)
    {
        var followers = new List<Point2>();
        var processed = new List<Line2F>();

        foreach (var key in hash(join_pt))
        {
            if (!_pool.TryGetValue(key, out var list))
                continue;

            foreach (var seg in list)
            {
                if (processed.Contains(seg))
                    continue;  // already got it

                var d1 = join_pt.DistanceTo(seg.p1);
                var d2 = join_pt.DistanceTo(seg.p2);

                if (d1 > _tolerance && d2 > _tolerance)
                    continue;

                followers.Add(d1 < d2 ? seg.p2 : seg.p1);
                processed.Add(seg);
            }
        }

        foreach (var seg in processed)
            Remove(seg);

        return followers;
    }

    public Segpool(int capacity, double tolerance)
    {
        _pool = new Dictionary<ulong, List<Line2F>>(capacity * 2 * 4);
        _tolerance = tolerance;
    }
}

internal interface Medial_branch
{
    Point2 Start { get; }
    Point2 End { get; }
    double Shallow_distance { get; }
    double Deep_distance { get; }

    void Add_point(Point2 pt);
    Medial_branch Spawn_child();
    void Attach_to_parent();
    void Postprocess();
}

internal static class Medial_builder
{
    private const double VORONOI_MARGIN = 1.0;
    private const bool ANALIZE_INNER_INTERSECTIONS = false;

    // take 3 well-spaced samples, find the center, check for all points to lay on a circle
    private static bool recognize_perfect_circle(List<Point2> samples, double tolerance, ref Point2 center)
    {
        if (samples.Count < 4)
            return false;

        var sample0 = samples[0];
        var sample1 = samples[samples.Count / 4];
        var sample2 = samples[samples.Count / 2];

        double xc = 0;
        double yc = 0;

        if (!GeomUtils.Circle_by_3_pt(sample0.X, sample0.Y, sample1.X, sample1.Y, sample2.X, sample2.Y, ref xc, ref yc))
            return false;

        center = new Point2(xc, yc);
        var radius = center.DistanceTo(sample0);

        foreach (var pt in samples)
        {
            if (Math.Abs(pt.DistanceTo(center) - radius) > tolerance)
                return false;
        }

        return true;
    }

    private static List<Line2F> get_medial_axis_segments(Topographer topo, List<Point2> samples, double general_tolerance, CancellationToken cancel)
    {
        var xs = new double[samples.Count + 1];
        var ys = new double[samples.Count + 1];

        var min_x = double.MaxValue;
        var max_x = double.MinValue;
        var min_y = double.MaxValue;
        var max_y = double.MinValue;

        // HACK (matmill)
        // There is a bug in Voronoi generator implementation. Sometimes it produces a completely crazy partitioning.
        // Looks like its overly sensitive to the first processed points, their count and location. If first stages
        // go ok, then everything comes nice. Beeing a Steven Fortune's algorithm, it process points by a moving sweep line.
        // Looks like the line is moving from the bottom to the top, thus sensitive points are the most bottom ones.
        // We try to cheat and add one more point so it would be the single most bottom point.
        // Then generator initially will see just one point, do a right magic and continue with a sane result :-)
        // We place this initial point under the lefmost bottom point at the sufficient distance,
        // then these two points will form a separate Voronoi cite not influencing the remaining partition.
        // Sufficient distance is defined as region width / 2 for now.

        var lb_idx = 0;

        for (var i = 0; i < samples.Count; i++)
        {
            xs[i] = samples[i].X;
            ys[i] = samples[i].Y;
            if (xs[i] < min_x) min_x = xs[i];
            if (xs[i] > max_x) max_x = xs[i];
            if (ys[i] > max_y) max_y = ys[i];

            if (ys[i] <= min_y)
            {
                if (ys[i] < min_y)
                {
                    min_y = ys[i];
                    lb_idx = i;  // stricly less, it's a new leftmost bottom for sure
                }
                else if (xs[i] < xs[lb_idx])  // it's a new leftmost bottom if more lefty
                {
                    lb_idx = i;
                }
            }
        }

        var width = max_x - min_x;
        xs[samples.Count] = xs[lb_idx];
        ys[samples.Count] = ys[lb_idx] - width / 2;

        min_x -= VORONOI_MARGIN;
        max_x += VORONOI_MARGIN;
        min_y -= VORONOI_MARGIN + width / 2;
        max_y += VORONOI_MARGIN;

        cancel.ThrowIfCancellationRequested();
        var edges = new Voronoi(general_tolerance).generateVoronoi(xs, ys, min_x, max_x, min_y, max_y);
        cancel.ThrowIfCancellationRequested();

        Logger.log("voronoi partitioning completed. got {0} edges", edges.Count);

        var inner_segments = new List<Line2F>();

        foreach (var e in edges)
        {
            var seg = new Line2F(e.x1, e.y1, e.x2, e.y2);
            if (seg.Length() < double.Epsilon) continue;    // extra small segment, discard
            if (!topo.Is_line_inside_region(seg, ANALIZE_INNER_INTERSECTIONS, general_tolerance)) continue;
            inner_segments.Add(seg);
        }

        return inner_segments;
    }

    private static Point2 prepare_segments_w_auto_startpoint(Topographer topo, List<Line2F> segments, Segpool pool, double min_dist_to_wall)
    {
        // segments are analyzed for mic radius from both ends. passed segmens are inserted in segpool
        // hashed by one or both endpoints. if endpoint is not hashed, segment wouldn't be followed
        // from that side, preventing formation of bad tree.
        // segments are connected later in a greedy fashion, hopefully forming a medial axis covering all
        // pocket.
        // start segment is the one with the largest mic

        var max_r = double.MinValue;
        var tree_start = Pt.Undefined;

        foreach (var seg in segments)
        {
            var r1 = topo.Get_dist_to_wall(seg.p1);
            var r2 = topo.Get_dist_to_wall(seg.p2);

            if (r1 >= min_dist_to_wall)
            {
                pool.Add(seg, false);
                if (r1 > max_r)
                {
                    max_r = r1;
                    tree_start = seg.p1;
                }
            }
            if (r2 >= min_dist_to_wall)
            {
                pool.Add(seg, true);
                if (r2 > max_r)
                {
                    max_r = r2;
                    tree_start = seg.p2;
                }
            }
        }

        return tree_start;
    }

    // with the manual startpoint
    private static Point2 prepare_segments_w_manual_starpoint(Topographer topo, List<Line2F> segments, Segpool pool, double min_dist_to_wall, double general_tolerance, Point2 startpoint)
    {
        // same as automatic, but seek the segment with the closest end to startpoint
        if (!topo.Is_line_inside_region(new Line2F(startpoint, startpoint), general_tolerance))
        {
            Logger.warn("startpoint is outside the pocket");
            return Pt.Undefined;
        }

        if (topo.Get_dist_to_wall(startpoint) < min_dist_to_wall)
        {
            Logger.warn("startpoint radius < tool radius");
            return Pt.Undefined;
        }

        var min_dist = double.MaxValue;
        var tree_start = Pt.Undefined;

        foreach (var seg in segments)
        {
            var r1 = topo.Get_dist_to_wall(seg.p1);
            var r2 = topo.Get_dist_to_wall(seg.p2);

            if (r1 >= min_dist_to_wall)
            {
                pool.Add(seg, false);
                var dist = startpoint.DistanceTo(seg.p1);
                if (dist < min_dist && topo.Is_line_inside_region(new Line2F(startpoint, seg.p1), general_tolerance))
                {
                    min_dist = dist;
                    tree_start = seg.p1;
                }
            }
            if (r2 >= min_dist_to_wall)
            {
                pool.Add(seg, true);
                var dist = startpoint.DistanceTo(seg.p2);
                if (dist < min_dist && topo.Is_line_inside_region(new Line2F(startpoint, seg.p2), general_tolerance))
                {
                    min_dist = dist;
                    tree_start = seg.p2;
                }
            }
        }

        return tree_start;
    }

    // with the manual startpoint, which is a hint
    private static Point2 prepare_segments_w_hinted_starpoint(Topographer topo, List<Line2F> segments, Segpool pool, double min_dist_to_wall, Point2 startpoint)
    {
        // same as automatic, but seek the segment with the closest end to startpoint
        var min_dist = double.MaxValue;
        var tree_start = Pt.Undefined;

        foreach (var seg in segments)
        {
            var r1 = topo.Get_dist_to_wall(seg.p1);
            var r2 = topo.Get_dist_to_wall(seg.p2);

            if (r1 >= min_dist_to_wall)
            {
                pool.Add(seg, false);
                var dist = startpoint.DistanceTo(seg.p1);
                if (dist < min_dist)
                {
                    min_dist = dist;
                    tree_start = seg.p1;
                }
            }
            if (r2 >= min_dist_to_wall)
            {
                pool.Add(seg, true);
                var dist = startpoint.DistanceTo(seg.p2);
                if (dist < min_dist)
                {
                    min_dist = dist;
                    tree_start = seg.p2;
                }
            }
        }

        return tree_start;
    }

    private static void build_branch(Medial_branch me, Segpool pool, double min_branch_len, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var running_end = me.End;
        List<Point2> followers;

        while (true)
        {
            followers = pool.Pull_follow_points(running_end);

            if (followers.Count != 1)
                break;

            running_end = followers[0];
            me.Add_point(running_end);         // continuation
        }

        if (followers.Count == 0) return; // end of branch, go out

        foreach (var pt in followers)
        {
            var b = me.Spawn_child();
            b.Add_point(running_end);
            b.Add_point(pt);
            build_branch(b, pool, min_branch_len, cancel);

            if (b.Deep_distance > min_branch_len) // attach only 'long enough'
                b.Attach_to_parent();
            else
                Logger.log("skipping short branch");
        }
        me.Postprocess();
    }

    public static bool Build(Medial_branch root, Topographer topo, List<Point2> samples, double general_tolerance, Point2 startpoint,
        double min_dist_to_wall, bool startpoint_is_a_hint, CancellationToken cancel)
    {
        // NOTE: circle is a special case for the medial builder - medial axis is just a single point.
        // worth to make a quick check.
        // circle should be handled in a special way, completely skipping the Voronoi partitioning

        List<Line2F> medial_axis_segments;

        var center = Pt.Undefined;
        if (!recognize_perfect_circle(samples, general_tolerance, ref center))
        {
            medial_axis_segments = get_medial_axis_segments(topo, samples, general_tolerance, cancel);
        }
        else
        {
            Logger.log("recognized perfect circle");
            medial_axis_segments = [new Line2F(center, center)];
        }

        Logger.log("analyzing segments");

        var pool = new Segpool(medial_axis_segments.Count, general_tolerance);

        Point2 tree_start;
        if (startpoint.IsUndefined())
            tree_start = prepare_segments_w_auto_startpoint(topo, medial_axis_segments, pool, min_dist_to_wall);
        else if (startpoint_is_a_hint)
            tree_start = prepare_segments_w_hinted_starpoint(topo, medial_axis_segments, pool, min_dist_to_wall, startpoint);
        else
            tree_start = prepare_segments_w_manual_starpoint(topo, medial_axis_segments, pool, min_dist_to_wall, general_tolerance, startpoint);

        if (tree_start.IsUndefined())
        {
            Logger.warn("failed to choose tree start point");
            return false;
        }

        if (!(startpoint.IsUndefined() || startpoint_is_a_hint))
            root.Add_point(startpoint);

        root.Add_point(tree_start);

        build_branch(root, pool, general_tolerance, cancel);
        return true;
    }
}

internal sealed class Branch : Medial_branch
{
    public delegate int Branch_visitor(Point2 pt);

    private readonly Curve2d _curve = new();
    private readonly Branch Parent;
    private readonly List<Branch> _children = [];

    //--- Medial_branch interface

    public double Deep_distance => calc_deep_distance();
    public double Shallow_distance => _curve.Length;
    public Point2 Start => _curve.Start;
    public Point2 End => _curve.End;

    public void Add_point(Point2 pt) => _curve.Add(pt);

    public Medial_branch Spawn_child() => new Branch(this);

    public void Postprocess()
    {
        // sort children so the shortest branch comes first in df traverse
        _children.Sort((a, b) => a.Deep_distance.CompareTo(b.Deep_distance));
    }

    public void Attach_to_parent()
    {
        if (Parent == null)
            throw new Exception("attempt to attach orphaned branch");
        Parent._children.Add(this);
    }

    //--- own interface

    public IEnumerable<Branch> Children => _children;

    public double calc_deep_distance()
    {
        var dist = _curve.Length;
        foreach (var b in _children)
            dist += b.Deep_distance;
        return dist;
    }

    public void Bisect(Branch_visitor visitor, ref double t, double stop_distance)
    {
        if (t < 0.0 || t > 1.0)
            throw new Exception("branch bisector was called with a wrong range");

        var left = t;
        var right = 1.0;

        double mid;

        while (true)
        {
            mid = (left + right) / 2;
            var pt = _curve.Get_parametric_pt(mid);

            var result = visitor(pt);

            if (result == 0)
                break;

            if (result < 0)
                right = mid;
            else
                left = mid;

            var other = _curve.Get_parametric_pt(left == mid ? right : left);
            if (pt.DistanceTo(other) < stop_distance)   // range has shrinked, stop search
                break;
        }

        t = mid;
    }

    public Branch(Branch parent)
    {
        Parent = parent;
    }
}
