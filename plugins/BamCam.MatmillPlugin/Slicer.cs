// Ported from matmill (https://github.com/jkmnt/matmill), slicer.cs and spiral.cs, MIT License,
// Copyright (c) 2016 jkmnt. See LICENSE-matmill.txt.
#nullable disable

using BamCam.Core.Geometry;

namespace Matmill;

internal sealed class Slice_sequence
{
    private readonly double _general_tolerance;
    private readonly Ballfield_topographer _topo;
    public List<Slice> Slices = [];

    public Slice Root_slice => Slices.Count > 0 ? Slices[0] : null;
    public Slice Last_slice => Slices.Count > 0 ? Slices[^1] : null;

    private static List<Slice> find_lca_path(Slice dst, Slice src)
    {
        var path = new List<Slice>();

        var src_ancestry = new List<Slice>();
        var dst_ancestry = new List<Slice>();

        for (var s = src.Parent; s != null; s = s.Parent)
            src_ancestry.Insert(0, s);

        for (var s = dst.Parent; s != null; s = s.Parent)
            dst_ancestry.Insert(0, s);

        int lca;
        for (lca = 0; lca < Math.Min(src_ancestry.Count, dst_ancestry.Count); lca++)
        {
            if (src_ancestry[lca] != dst_ancestry[lca])
                break;
        }

        if (lca != 0)
            lca -= 1;   // the first diverging slices in ancestries were detected, lca is the last same slice, so -1

        // now lca contains the lca of branches
        // collect path up from src to lca and down to dst
        for (var i = src_ancestry.Count - 1; i > lca; i--)
            path.Add(src_ancestry[i]);

        for (var i = lca; i < dst_ancestry.Count - 1; i++)
            path.Add(dst_ancestry[i]);

        return path;
    }

    public List<Slice> Find_slices_colliding_with(Slice s)
    {
        var min = Pt.Undefined;
        var max = Pt.Undefined;
        s.Get_extrema(ref min, ref max);

        return _topo.Get_colliding_objects<Slice>(min, max);
    }

    public List<Point2> Trace_branch_switch(Slice dst, Slice src)
    {
        // follow the lca path, while looking for a shortcut to reduce travel time
        var knots = new List<Point2>();

        var current = src.End;
        var end = dst.Start;

        foreach (var s in find_lca_path(dst, src))
        {
            if (_topo.Is_line_inside_region(new Line2F(current, end), _general_tolerance))
                break;
            current = s.Center;
            knots.Add(current);
        }

        return knots;
    }

    public void Add(Slice s)
    {
        Slices.Add(s);
        _topo.Add(s.Ball, s);
    }

    public Slice_sequence(Point2 min, Point2 max, double general_tolerance)
    {
        _general_tolerance = general_tolerance;
        _topo = new Ballfield_topographer(min, max);
    }
}

internal sealed class Branch_slicer
{
    public delegate double Get_radius_delegate(Point2 pt);

    private const double TED_TOLERANCE_PERCENTAGE = 0.001;  // 0.1 %
    private const double FINAL_ALLOWED_TED_OVERSHOOT_PERCENTAGE = 0.03;  // 3 %

    private readonly double _general_tolerance;
    private readonly Slice_sequence _sequence;
    private readonly CancellationToken _cancel;

    private double _min_ted;
    private double _max_ted;
    private double _tool_r;
    private RotationDirection _dir;

    public double Slice_leadin_angle = 3 * Math.PI / 180;
    public double Slice_leadout_angle = 0.5 * Math.PI / 180;

    // NOTE: radius getter may return 0 if radius is too small or invalid
    public Get_radius_delegate Get_radius = x => 0;

    private int evaluate_possible_slice(Slice parent, Point2 pt, ref Slice _candidate)
    {
        var radius = Get_radius(pt);

        if (radius <= 0) return -1; // assuming the branch is always starting from passable mics, so it's a narrow channel and we should be more conservative, go left

        var s = new Slice(parent, pt, radius, _dir, _tool_r, _sequence.Last_slice.End);

        if (s.Placement != Slice_placement.NORMAL)
        {
            if (s.Placement == Slice_placement.INSIDE_ANOTHER) return 1;    // go right
            if (s.Placement == Slice_placement.TOO_FAR) return -1;          // go left

            throw new Exception("unknown slice placement");
        }
        // intersection
        _candidate = s;
        s.Refine(_sequence.Find_slices_colliding_with(s), _tool_r, _tool_r);

        if (s.Max_ted > _max_ted) return -1;                                            // overshoot, go left
        if ((_max_ted - s.Max_ted) / _max_ted > TED_TOLERANCE_PERCENTAGE) return 1;     // undershoot outside the strict TED tolerance, go right

        return 0;                                                                       // good slice inside the tolerance, stop search
    }

    private void trace_branch(Branch branch, Slice parent_slice)
    {
        double t = 0;

        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            Slice new_slice = null;

            branch.Bisect(pt => evaluate_possible_slice(parent_slice, pt, ref new_slice), ref t, _general_tolerance);

            if (new_slice == null)
                break;
            if (new_slice.Max_ted < _min_ted)   // discard slice if outside the specified min TED
                break;

            // discard slice if outside the final allowed percentage
            var err = (new_slice.Max_ted - _max_ted) / _max_ted;
            if (err > FINAL_ALLOWED_TED_OVERSHOOT_PERCENTAGE)
            {
                Logger.warn("failed to create slice within stepover limit. stopping slicing the branch");
                break;
            }

            // if last slice was a root slice, adjust root slice startpoint to remove extra travel and
            // append leadout to the candindate. leadin is not needed, since join with root will be exact
            // otherwise append both leadin and leadout
            if (_sequence.Last_slice == _sequence.Root_slice)
            {
                _sequence.Root_slice.Change_startpoint(new_slice.Start);
                new_slice.Append_leadin_and_leadout(0, Slice_leadout_angle);
            }
            else
            {
                new_slice.Append_leadin_and_leadout(Slice_leadin_angle, Slice_leadout_angle);
            }

            // generate guide move if this slice is not a simple continuation
            if (parent_slice != _sequence.Last_slice)
                new_slice.Guide = _sequence.Trace_branch_switch(new_slice, _sequence.Last_slice);

            _sequence.Add(new_slice);
            parent_slice = new_slice;
        }

        // need to go deeper
        foreach (var b in branch.Children)
            trace_branch(b, parent_slice);
    }

    public Slice_sequence Run(Branch root, double tool_r, double max_ted, double min_ted, RotationDirection dir)
    {
        _min_ted = min_ted;
        _max_ted = max_ted;
        _tool_r = tool_r;
        _dir = dir;

        var root_slice = new Slice(root.Start, Get_radius(root.Start), _dir == RotationDirection.Unknown ? RotationDirection.CCW : _dir);
        _sequence.Add(root_slice);

        trace_branch(root, root_slice);

        return _sequence;
    }

    public Branch_slicer(Point2 min, Point2 max, double general_tolerance, CancellationToken cancel)
    {
        _sequence = new Slice_sequence(min, max, general_tolerance);
        _general_tolerance = general_tolerance;
        _cancel = cancel;
    }
}

internal sealed class Bypoint_slicer
{
    private readonly Slice_sequence _sequence;
    private readonly CancellationToken _cancel;

    public double Slice_leadin_angle = 3 * Math.PI / 180;
    public double Slice_leadout_angle = 0.5 * Math.PI / 180;

    public Slice_sequence Run(List<Point2> points, double tool_r, double slice_radius, RotationDirection dir)
    {
        var root_slice = new Slice(points[0], slice_radius, dir == RotationDirection.Unknown ? RotationDirection.CCW : dir);
        _sequence.Add(root_slice);

        for (var i = 1; i < points.Count; i++)
        {
            _cancel.ThrowIfCancellationRequested();
            var new_slice = new Slice(_sequence.Last_slice, points[i], slice_radius, dir, tool_r, _sequence.Last_slice.End);

            if (new_slice.Placement != Slice_placement.NORMAL)
                throw new Exception("bad slice placement");

            new_slice.Refine(_sequence.Find_slices_colliding_with(new_slice), tool_r, tool_r);

            if (i == 1)
            {
                _sequence.Root_slice.Change_startpoint(new_slice.Start);
                new_slice.Append_leadin_and_leadout(0, Slice_leadout_angle);
            }
            else
            {
                new_slice.Append_leadin_and_leadout(Slice_leadin_angle, Slice_leadout_angle);
            }
            _sequence.Add(new_slice);
        }

        return _sequence;
    }

    public Bypoint_slicer(Point2 min, Point2 max, double general_tolerance, CancellationToken cancel)
    {
        _sequence = new Slice_sequence(min, max, general_tolerance);
        _cancel = cancel;
    }
}

internal static class Spiral_generator
{
    private static double get_ted(double prev_r, double this_r, double tool_r)
    {
        var prev_wall_r = prev_r + tool_r;
        var this_wall_r = this_r + tool_r;

        if (this_r - prev_wall_r >= tool_r)
            return double.MaxValue;

        if (prev_wall_r - this_r >= tool_r)
            return double.MinValue;

        // get intersection of two cicles = cutter circle and prev wall
        var prev_wall = new Circle2F(new Point2(0, 0), prev_wall_r);
        var tool_circle = new Circle2F(new Point2(this_r, 0), tool_r);

        var insects = prev_wall.CircleIntersect(tool_circle);
        if (insects.p1.IsUndefined() || insects.p2.IsUndefined())
            return 0;

        return this_wall_r - insects.p1.X;
    }

    public static double Calc_reverse_spacing(double this_r, double tool_r, double needed_ted, double tolerance)
    {
        var left = Math.Max(this_r - tool_r * 1.99, 0);
        var right = this_r * 0.99;
        double mid;

        while (true)
        {
            mid = (left + right) / 2;
            var ted = get_ted(mid, this_r, tool_r);

            if (ted == double.MaxValue || ted > needed_ted) // too far
                left = mid;
            else if (ted == double.MinValue || ted < needed_ted) // too close
                right = mid;
            else
                break;  // ted = needed ted. unlikely, but ok

            if (Math.Abs(left - right) < tolerance)   // range has shrinked, stop search
                break;
        }

        return this_r - mid;
    }

    // simple Archimedean spiral:
    // r = a * theta + c
    //
    // in cartesian coordinates:
    // x = center_x + (a * theta + c) * cos(theta)
    // y = center_y + (a * theta + c) * sin(theta)
    //
    // tangent vector (derivate relative to theta):
    // x' = a * cos(theta) - sin(theta) * r
    // y' = a * sin(theta) + cos(theta) * r

    // we output biarcs to approximate spiral, 6 biarcs (12 arcs) per loop.
    // real number of biarcs would be more, since in general case there would be incomplete loops
    // last biarc is exact to the endpoint

    // if start tangent is defined, spiral will start from it (spacing will be reduced a little to make a fit).
    // otherwise start tangent is choosed automatically and spacing is exact.
    public static List<Biarc2d> Gen_archimedean_spiral(Point2 center, Point2 end, Vector2d start_tangent, double spacing, RotationDirection dir)
    {
        var r_max = center.DistanceTo(end);
        var v_end = new Vector2d(center, end);

        var theta_end = v_end.Ccw_angle;
        var theta_step = 2 * Math.PI / 6;
        var a = spacing / (2 * Math.PI);
        double theta_start;

        if (dir == RotationDirection.CW)
        {
            a = -a;
            theta_step = -theta_step;
        }

        if (double.IsNaN(start_tangent.X))
        {
            theta_start = theta_end - r_max / a;
        }
        else
        {
            theta_start = start_tangent.Ccw_angle;
            double candidate_a;

            if (dir == RotationDirection.CCW)
            {
                while (theta_start >= theta_end)
                    theta_start -= 2 * Math.PI;

                while (true)
                {
                    candidate_a = r_max / (theta_end - theta_start);
                    if (candidate_a <= a)
                        break;
                    theta_start -= 2 * Math.PI;
                }
            }
            else
            {
                while (theta_start <= theta_end)
                    theta_start += 2 * Math.PI;

                while (true)
                {
                    candidate_a = r_max / (theta_end - theta_start);
                    if (candidate_a >= a)
                        break;
                    theta_start += 2 * Math.PI;
                }
            }
            a = candidate_a;
        }

        var nsegments = (int)Math.Floor((theta_end - theta_start) / theta_step);

        var c = -theta_start * a;

        var p1 = new Point2();
        var t1 = new Vector2d();
        var spiral = new List<Biarc2d>();

        for (var segidx = 0; segidx < nsegments; segidx++)
        {
            var theta = theta_start + segidx * theta_step;
            var r = a * theta + c;

            var sin = Math.Sin(theta);
            var cos = Math.Cos(theta);

            var p2 = center + new Point2(r * cos, r * sin);
            var t2 = new Vector2d(a * cos - r * sin, a * sin + r * cos).Unit();

            if (dir == RotationDirection.CW)
                t2 = t2.Inverted();

            if (segidx != 0)
                spiral.Add(new Biarc2d(p1, t1, p2, t2));

            p1 = p2;
            t1 = t2;
        }

        var t_end = v_end.Normal().Unit();
        if (dir == RotationDirection.CW)
            t_end = t_end.Inverted();

        // a spiral of less than one step: from the centre straight to the end
        if (nsegments == 0)
        {
            p1 = center;
            t1 = v_end.Unit();
        }

        spiral.Add(new Biarc2d(p1, t1, end, t_end));

        return spiral;
    }
}
