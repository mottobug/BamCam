// Ported from matmill (https://github.com/jkmnt/matmill), topo.cs, MIT License, Copyright (c) 2016 jkmnt.
// See LICENSE-matmill.txt.
#nullable disable

using BamCam.Core.Geometry;
using Tree4;

namespace Matmill;

internal sealed class Topographer
{
    private const double T4_MARGIN = 1.0;

    private readonly Contour _outline;
    private readonly Contour[] _islands;
    private readonly T4 _t4;
    private bool is_t4_populated;

    public Point2 Min => new(_t4.Rect.Xmin + T4_MARGIN, _t4.Rect.Ymin + T4_MARGIN);
    public Point2 Max => new(_t4.Rect.Xmax - T4_MARGIN, _t4.Rect.Ymax - T4_MARGIN);

    private void populate_t4()
    {
        if (is_t4_populated)
            return;
        insert_in_t4(_outline);
        foreach (var island in _islands)
            insert_in_t4(island);
        is_t4_populated = true;
    }

    private void insert_in_t4(Contour p)
    {
        for (var i = 0; i < p.NumSegments; i++)
        {
            var seg = p.GetSegment(i);
            T4_rect rect;

            if (seg is Line2F line)
            {
                rect = new T4_rect(Math.Min(line.p1.X, line.p2.X),
                                    Math.Min(line.p1.Y, line.p2.Y),
                                    Math.Max(line.p1.X, line.p2.X),
                                    Math.Max(line.p1.Y, line.p2.Y));
            }
            else if (seg is Arc2F arc)
            {
                var min = Pt.Undefined;
                var max = Pt.Undefined;
                arc.GetExtrema(ref min, ref max);
                rect = new T4_rect(min.X, min.Y, max.X, max.Y);
            }
            else
            {
                throw new Exception("unknown segment type");
            }

            _t4.Add(rect, seg);
        }
    }

    public bool Is_line_inside_region(Line2F line, bool should_analize_inner_intersections, double tolerance)
    {
        if (!_outline.PointInPolyline(line.p1, tolerance))
            return false;     // p1 is outside of outer curve boundary
        if (!_outline.PointInPolyline(line.p2, tolerance))
            return false;  // p2 is outside of outer curve boundary
        if (should_analize_inner_intersections && _outline.LineIntersects(line, tolerance))
            return false; // both endpoints are inside, but there are intersections, outer curve must be concave

        foreach (var island in _islands)
        {
            if (island.PointInPolyline(line.p1, tolerance))
                return false;  // p1 is inside hole
            if (island.PointInPolyline(line.p2, tolerance))
                return false;  // p2 is inside hole
            if (should_analize_inner_intersections && island.LineIntersects(line, tolerance))
                return false; // p1, p2 are outside hole, but there are intersections
        }
        return true;
    }

    public static List<Point2> sample_curve(Contour p, double step)
    {
        // divide curve evenly (matmill: 'divide to n equal segments')
        var nsegs = (int)Math.Max(Math.Ceiling(p.Length / step), 1);
        return p.Divide(nsegs);
    }

    public static List<Point2> sample_curve_exact(Contour p, double step)
    {
        var points = p.DivideByStep(step);

        var last_sample = points[^1];
        var poly_end = p.LastPoint;

        if (last_sample.DistanceTo(poly_end) > step * 0.001)
            points.Add(poly_end);

        return points;
    }

    public double Get_dist_to_wall(Point2 pt)
    {
        populate_t4();

        var radius = double.MaxValue;
        foreach (var item in _t4.Get_nearest_objects(pt.X, pt.Y))
        {
            double dist = 0;
            if (item is Line2F line)
                line.NearestPoint(pt, ref dist);
            else
                ((Arc2F)item).NearestPoint(pt, ref dist);
            if (dist < radius)
                radius = dist;
        }

        return radius;
    }

    public bool Is_line_inside_region(Line2F line, double tolerance) => Is_line_inside_region(line, true, tolerance);

    public List<Point2> Get_samples(double sample_step)
    {
        var plist = new List<Point2>();
        plist.AddRange(sample_curve(_outline, sample_step));
        foreach (var p in _islands)
            plist.AddRange(sample_curve(p, sample_step));
        return plist;
    }

    public List<Point2> Get_samples_exact(double sample_step)
    {
        var plist = new List<Point2>();
        plist.AddRange(sample_curve_exact(_outline, sample_step));
        foreach (var p in _islands)
            plist.AddRange(sample_curve_exact(p, sample_step));
        return plist;
    }

    public bool Build_medial_tree(Medial_branch tree, double sample_step, double general_tolerance, Point2 startpoint, double min_dist_to_wall,
        bool startpoint_is_a_hint, CancellationToken cancel)
    {
        var samples = Get_samples(sample_step);
        Logger.log("got {0} points", samples.Count);

        return Medial_builder.Build(tree, this, samples, general_tolerance, startpoint, min_dist_to_wall, startpoint_is_a_hint, cancel);
    }

    public Topographer(Contour outline, Contour[] islands)
    {
        _outline = outline;
        _islands = islands;

        var min = Pt.Undefined;
        var max = Pt.Undefined;
        _outline.GetExtrema(ref min, ref max);

        _t4 = new T4(new T4_rect(min.X - T4_MARGIN, min.Y - T4_MARGIN, max.X + T4_MARGIN, max.Y + T4_MARGIN));
    }
}

internal sealed class Ballfield_topographer
{
    private readonly T4 _t4;

    private const double T4_MARGIN = 1.0;

    public Point2 Min => new(_t4.Rect.Xmin + T4_MARGIN, _t4.Rect.Ymin + T4_MARGIN);
    public Point2 Max => new(_t4.Rect.Xmax - T4_MARGIN, _t4.Rect.Ymax - T4_MARGIN);

    private List<Circle2F> find_intersecting_balls(Line2F line)
    {
        var rect = new T4_rect(Math.Min(line.p1.X, line.p2.X),
                               Math.Min(line.p1.Y, line.p2.Y),
                               Math.Max(line.p1.X, line.p2.X),
                               Math.Max(line.p1.Y, line.p2.Y));

        var balls = new List<Circle2F>();

        // since objects in t4 are generic, convert rects back to balls
        foreach (var ballrect in _t4.Get_colliding_obj_rects(rect))
            balls.Add(new Circle2F(new Point2(ballrect.Xc, ballrect.Yc), ballrect.W / 2));

        return balls;
    }

    // we are collecting all the intersections and tracking the list of balls we're inside
    // at any given point. If list becomes empty, we can't shortcut
    public bool Is_line_inside_region(Line2F line, double tolerance)
    {
        var a = line.p1;
        var b = line.p2;

        var intersections = new SortedList<double, List<Circle2F>>();
        var running_balls = new List<Circle2F>();

        foreach (var ball in find_intersecting_balls(line))
        {
            var insects = ball.LineIntersect(line, tolerance);

            if (insects.p1.IsUndefined() && insects.p2.IsUndefined())
            {
                // no intersections: check if whole path lay inside the circle
                if (a.DistanceTo(ball.Center) < ball.Radius + tolerance
                    && b.DistanceTo(ball.Center) < ball.Radius + tolerance)
                    return true;
            }
            else if (insects.p1.IsUndefined() || insects.p2.IsUndefined())
            {
                // single intersection. one of the path ends must be inside the circle, otherwise it is a tangent case
                // and should be ignored
                if (a.DistanceTo(ball.Center) < ball.Radius + tolerance)
                {
                    running_balls.Add(ball);
                }
                else if (b.DistanceTo(ball.Center) < ball.Radius + tolerance)
                {
                }
                else
                {
                    continue;
                }

                var c = insects.p1.IsUndefined() ? insects.p2 : insects.p1;
                var d = c.DistanceTo(a);
                if (!intersections.ContainsKey(d))
                    intersections.Add(d, []);
                intersections[d].Add(ball);
            }
            else
            {
                // double intersection
                var d = insects.p1.DistanceTo(a);
                if (!intersections.ContainsKey(d))
                    intersections.Add(d, []);
                intersections[d].Add(ball);

                d = insects.p2.DistanceTo(a);
                if (!intersections.ContainsKey(d))
                    intersections.Add(d, []);
                intersections[d].Add(ball);
            }
        }

        if (running_balls.Count == 0)
            return false;

        foreach (var ins in intersections)
        {
            foreach (var s in ins.Value)
            {
                if (running_balls.Contains(s))
                    running_balls.Remove(s);
                else
                    running_balls.Add(s);
            }

            if (running_balls.Count == 0 && ins.Key + tolerance < a.DistanceTo(b))
                return false;
        }

        return true;
    }

    public List<T> Get_colliding_objects<T>(Point2 min, Point2 max) => _t4.Get_colliding_objects<T>(new T4_rect(min.X, min.Y, max.X, max.Y));

    public void Add(Point2 center, double radius, object obj)
    {
        var rect = new T4_rect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        _t4.Add(rect, obj);
    }

    public void Add(Circle2F ball, object obj) => Add(ball.Center, ball.Radius, obj);

    public Ballfield_topographer(Point2 min, Point2 max)
    {
        _t4 = new T4(new T4_rect(min.X - 1, min.Y - 1, max.X + 1, max.Y + 1));
    }
}
