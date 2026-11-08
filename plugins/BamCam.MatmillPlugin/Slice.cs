// Ported from matmill (https://github.com/jkmnt/matmill), slice.cs, MIT License, Copyright (c) 2016 jkmnt.
// See LICENSE-matmill.txt.
#nullable disable

using BamCam.Core.Geometry;

// FYI: TED stands for Tool Engagement Depth (or Distance).

namespace Matmill;

internal enum Slice_placement
{
    NORMAL,
    TOO_FAR,
    INSIDE_ANOTHER,
}

internal sealed class Slice
{
    private readonly Circle2F _ball;
    private readonly List<Arc2F> _segments = [];
    private readonly Slice _parent;
    private readonly Slice_placement _placement;
    private double _entry_ted;
    private double _mid_ted;
    private double _max_ted;

    public List<Point2> Guide;

    public Circle2F Ball => _ball;
    public List<Arc2F> Segments => _segments;
    public Point2 Center => _ball.Center;
    // a slice is always made of two arcs (2.1.2): no arc sweeps over 180 degrees, and a refined slice
    // keeps a gap between them
    public Point2 Start => _segments[0].P1;
    public Point2 End => _segments[1].P2;
    public RotationDirection Dir => _segments[0].Direction;
    public Slice Parent => _parent;
    public Slice_placement Placement => _placement;
    public double Max_ted => _max_ted;
    public double Radius => _ball.Radius;

    private static double angle_between_vectors(Vector2d v0, Vector2d v1, RotationDirection dir)
    {
        var angle = v0.Ccw_angle_to(v1);
        return dir == RotationDirection.CCW ? angle : 2.0 * Math.PI - angle;
    }

    private double calc_ted(Point2 tool_center, double tool_r)
    {
        var parent_wall = new Circle2F(_parent.Center, _parent.Radius + tool_r);

        // find the points tool touching parent wall
        var cut_circle = new Circle2F(tool_center, tool_r);
        var insects = cut_circle.CircleIntersect(parent_wall);

        if (insects.p1.IsUndefined() || insects.p2.IsUndefined())
            return -1;

        // we're interested in tool head point, so choose more advanced point in mill direction

        var v1 = new Vector2d(_parent.Center, insects.p1);
        var v_parent_to_tool_center = new Vector2d(_parent.Center, tool_center);
        var cut_head = v_parent_to_tool_center.Det(v1) * (int)Dir > 0 ? insects.p1 : insects.p2;

        // project headpoint to the center find TED
        var v_me_to_tool_center = new Vector2d(Center, tool_center);
        var v_me_to_cut_head = new Vector2d(Center, cut_head);

        var ted = Radius + tool_r - v_me_to_cut_head * v_me_to_tool_center.Unit();
        return ted > 0 ? ted : 0;
    }

    private double calc_entry_ted(double tool_r)
    {
        // expand both slices to form walls
        var parent_wall = new Circle2F(_parent.Center, _parent.Radius + tool_r);
        var this_wall = new Circle2F(Center, Radius + tool_r);

        // find the point of cut start (intersection of walls)
        var wall_insects = parent_wall.CircleIntersect(this_wall);

        if (wall_insects.p1.IsUndefined() || wall_insects.p2.IsUndefined())
            return -1;

        var v1 = new Vector2d(_parent.Center, wall_insects.p1);
        var v_move = new Vector2d(_parent.Center, Center);
        var cut_tail = v_move.Det(v1) * (int)Dir < 0 ? wall_insects.p1 : wall_insects.p2;
        var v_tail = new Vector2d(Center, cut_tail);

        var entry_tool_center = Center + (v_tail.Unit() * Radius).Point;

        return calc_ted(entry_tool_center, tool_r);
    }

    public void Get_extrema(ref Point2 min, ref Point2 max)
    {
        Point2 seg0_min = default, seg0_max = default, seg1_min = default, seg1_max = default;

        _segments[0].GetExtrema(ref seg0_min, ref seg0_max);
        _segments[1].GetExtrema(ref seg1_min, ref seg1_max);

        min = new Point2(Math.Min(seg0_min.X, seg1_min.X), Math.Min(seg0_min.Y, seg1_min.Y));
        max = new Point2(Math.Max(seg0_max.X, seg1_max.X), Math.Max(seg0_max.Y, seg1_max.Y));
    }

    public void Refine(List<Slice> trimming_slices, double clearance, double tool_r)
    {
        if (_segments[0].P2.DistanceTo(_segments[1].P1) != 0.0)
            throw new Exception("attempt to refine already refined slice");

        if (Radius == 0)
            throw new Exception("attempt to refine zero radius slice");

        if (trimming_slices.Contains(this))
            throw new Exception("attempt to trim slice by itself");

        if (trimming_slices.Count == 0)
            return;

        var v_start = new Vector2d(Center, Start);
        var v_mid = new Vector2d(Center, _segments[0].P2);

        // check if arc is small. refining is worthless in this case
        // criterion for smallness: there should be at least 4 segments with chord = clearance, plus
        // one segment to space ends far enough. A pentagon with a 5 segments with edge length = clearance
        // will define the min radius of circumscribed circle. clearance = 2 * R * sin (Pi / 5),
        // R = clearance / 2 / sin (Pi / 5)
        // arc perimeter should be > perimeter of such circle

        var r_min = clearance / 2 / Math.Sin(Math.PI / 5.0);
        var seg_perimeter = angle_between_vectors(v_start, v_mid, Dir) * Radius;

        if (seg_perimeter < r_min * Math.PI)  // two segments of slice are the same before refinement, so x2 is vanished from both sides
            return;

        // now apply the trimming slices. to keep things simple and robust, we apply just one slice - the one who trims
        // us most (removed length of arc is greatest).

        // end clearance adjustment:
        // to guarantee the tool will never hit the unmilled area while rapiding between segments,
        // arc will always have original ends, trimming will happen in the middle only.
        // to prevent the tool from milling extra small end segments and minimize numeric errors at small tangents,
        // original ends would always stick at least for a clearance (chordal) length.
        //
        // we achieve it by creating the smallest artifical arc (safe arc) with clearances applied.
        // as a bonus, this arc is guaranteed to be < 360 degress and well defined

        // there are a three cases of intersecting safe arc and trimming ball:
        // 1) no intersections: arc is either inside the ball (then its a maximum trim), or outside (ignored)
        // 2) single intersection. ball is breaking arc in two. remove the side which inside the ball
        // 3) double intersection. removed segment may be inside the ball (trim it) or outside (ignore)

        var c1_insects = _segments[0].CircleIntersect(new Circle2F(Start, clearance));
        var c2_insects = _segments[1].CircleIntersect(new Circle2F(End, clearance));
        var c1 = c1_insects.p1.IsUndefined() ? c1_insects.p2 : c1_insects.p1;
        var c2 = c2_insects.p1.IsUndefined() ? c2_insects.p2 : c2_insects.p1;
        if (c1.IsUndefined() || c2.IsUndefined())
            return; // the halves are too short for the end clearance (the perimeter check above should prevent it)
        var v_c1 = new Vector2d(Center, c1);
        var v_c2 = new Vector2d(Center, c2);
        var safe_arc = new Arc2F(Center, c1, c2, Dir);

        var max_secant = new Line2F();
        double max_sweep = 0;

        foreach (var s in trimming_slices)
        {
            if (s == _parent)
                continue;  // no reason to process it

            var secant = safe_arc.CircleIntersect(s.Ball);

            // not intersections. check if arc is completely inside the trimming circle
            if (secant.p1.IsUndefined() && secant.p2.IsUndefined())
            {
                if (safe_arc.Midpoint.DistanceTo(s.Center) > s.Radius)
                    continue;

                max_secant = new Line2F(c1, c2);
                max_sweep = angle_between_vectors(v_c1, v_c2, Dir);  // any value will do, in fact
                break;
            }

            if (secant.p1.IsUndefined() || secant.p2.IsUndefined())
            {
                // single intersection. find which side of safe arc is inside the trimming circle
                secant = new Line2F(c1.DistanceTo(s.Center) < c2.DistanceTo(s.Center) ? c1 : c2,
                    secant.p1.IsUndefined() ? secant.p2 : secant.p1);
            }
            // else: double intersection, ok

            // ignore the segments too short
            if (secant.p1.DistanceTo(secant.p2) < clearance * 2)
                continue;

            // sort insects by sweep
            // NOTE: to keep the good margin for numerical errors, we're sorting from original start point, not safe_arc start
            var v_ins1 = new Vector2d(Center, secant.p1);
            var v_ins2 = new Vector2d(Center, secant.p2);

            double sweep;

            if (angle_between_vectors(v_start, v_ins1, Dir) < angle_between_vectors(v_start, v_ins2, Dir))
            {
                sweep = angle_between_vectors(v_ins1, v_ins2, Dir);
            }
            else
            {
                sweep = angle_between_vectors(v_ins2, v_ins1, Dir);
                secant = new Line2F(secant.p2, secant.p1);
            }

            if (sweep > max_sweep)
            {
                // ok, a last check - removed arc midpoint should be inside the colliding circle
                var removed_sector = new Arc2F(Center, secant.p1, secant.p2, Dir);
                if (removed_sector.Midpoint.DistanceTo(s.Center) > s.Radius)
                    continue;

                max_sweep = sweep;
                max_secant = secant;
            }
        }

        if (max_sweep == 0)
            return;

        var seg0 = new Arc2F(Center, Start, max_secant.p1, Dir);
        var seg1 = new Arc2F(Center, max_secant.p2, End, Dir);

        _segments.Clear();
        _segments.Add(seg0);
        _segments.Add(seg1);

        // recalculate max TED accounting for the new endpoints.
        // this TED is 'virtual' and averaged with previous max_ted to make arcs look more pretty

        // if ends of removed segment are at the same side of direction vector,
        // midpoint is still present, _max_ted is valid and is maximum for sure
        var v_removed_p1 = new Vector2d(_parent.Center, max_secant.p1);
        var v_removed_p2 = new Vector2d(_parent.Center, max_secant.p2);

        if (v_mid.Det(v_removed_p1) * v_mid.Det(v_removed_p2) > 0)
            return;

        var seg0_end_ted = calc_ted(max_secant.p1, tool_r);
        var seg1_start_ted = calc_ted(max_secant.p2, tool_r);

        var ted = Math.Max(seg0_end_ted, seg1_start_ted);

        if (ted <= 0)
        {
            Logger.err("max TED vanished after refining the slice !");
            return;
        }

        ted = (ted + _mid_ted) / 2;
        _max_ted = Math.Max(ted, _entry_ted);
    }

    public void Append_leadin_and_leadout(double leadin_angle, double leadout_angle)
    {
        var dir = Dir;
        var start = _segments[0].P1;
        var gap_start = _segments[0].P2;
        var gap_end = _segments[1].P1;
        var end = _segments[1].P2;
        var center = Center;

        var v_move = new Vector2d(_parent.Center, center);

        var v_new_start = new Vector2d(center, start).Rotated(-(int)dir * leadin_angle);
        var v_new_end = new Vector2d(center, end).Rotated((int)dir * leadout_angle);

        // do not allow segments to overlap:
        // get sides of v1, v2 vectors relative to the midpoint vector of slice, account for rotation direction too.
        // if no 180 degree segment overflow occured, v1_det should be negative while v2_should should pe positive
        // skip the exact 180 degrees too
        var v_new_start_det = v_move.Det(v_new_start) * (int)dir;
        var v_new_end_det = v_move.Det(v_new_end) * (int)dir;

        if (v_new_start_det < 0 && v_new_end_det > 0)
        {
            start = center + v_new_start.Point;
            end = center + v_new_end.Point;
        }

        // if there is a nonzero gap between segments (and space allows), apply leadin/leadout to gap too
        if (gap_start.DistanceTo(gap_end) != 0)   // exact compare is ok, points are created this way
        {
            var v_gap_start = new Vector2d(center, gap_start);
            var v_gap_end = new Vector2d(center, gap_end);

            if (angle_between_vectors(v_gap_start, v_gap_end, dir) > leadin_angle + leadout_angle)
            {
                gap_start = center + v_gap_start.Rotated((int)dir * leadout_angle).Point;
                gap_end = center + v_gap_end.Rotated(-(int)dir * leadin_angle).Point;
            }
        }

        _segments[0] = new Arc2F(center, start, gap_start, dir);
        _segments[1] = new Arc2F(center, gap_end, end, dir);
    }

    private void create_arc_circle(Point2 p1, RotationDirection dir)
    {
        var p2 = Center - new Vector2d(Center, p1).Point;

        _segments.Clear();
        _segments.Add(new Arc2F(Center, p1, p2, dir));
        _segments.Add(new Arc2F(Center, p2, p1, dir));
    }

    public void Change_startpoint(Point2 p1)
    {
        if (_parent != null)
            throw new Exception("startpoint may be changed for the root slice only");
        if (Math.Abs((p1.DistanceTo(Center) - Radius) / Radius) > 0.001)
            throw new Exception("new startpoint is outside the initial circle");

        create_arc_circle(p1, Dir);
    }

    public Slice(Slice parent, Point2 center, double radius, RotationDirection dir, double tool_r, Point2 magnet)
    {
        _parent = parent;
        _ball = new Circle2F(center, radius);

        var dist = Point2.Distance(center, parent.Center);
        var delta_r = Radius - parent.Radius;
        var coarse_ted = dist + delta_r;

        // possible placements for the slice:
        // 1) too far from parent and do not intersect - overshoot
        // 2) far from parent and intersect in a single intermediate point - overshoot
        // 3) inside the parent and intersect in a single point - undershoot
        // 4) contains the parent and intersect in a single point - ok, but handle with care
        // 5) inside the parent and do not intersect - undershoot
        // 6) contains the parent and do not intersect - undershoot (?)
        // 7) completely matches the parent and intersect in infinite number of points - undershoot (?)
        // 8) intersects the parent in two points - ok

        // case 7
        if (dist == 0 && delta_r == 0)
        {
            _placement = Slice_placement.INSIDE_ANOTHER;
            return;
        }
        // case 1 (and case 2 if calculation is unlikely precise)
        if (dist >= Radius + parent.Radius)
        {
            _placement = Slice_placement.TOO_FAR;
            return;
        }
        // discard the slices with TED >= tool diameter early
        // these slices are not valid and check prevents fails during the calculation of real angle-based TED
        // this should cover case 2 too
        if (coarse_ted > tool_r * 1.999)
        {
            _placement = Slice_placement.TOO_FAR;
            return;
        }
        // now we either have no intersections (cases 5, 6), one intersection (cases 3, 4), two intersections (case 8)
        var insects = _parent.Ball.CircleIntersect(_ball);
        // cases 5, 6
        if (insects.p1.IsUndefined() && insects.p2.IsUndefined())
        {
            _placement = Slice_placement.INSIDE_ANOTHER;
            return;
        }
        // cases 3, 4
        if (insects.p1.IsUndefined() || insects.p2.IsUndefined())
        {
            // case 3
            if (Radius <= parent.Radius)
            {
                _placement = Slice_placement.INSIDE_ANOTHER;
                return;
            }
            // case 4
            _placement = Slice_placement.NORMAL;

            if (insects.p1.IsUndefined())
                insects.p1 = insects.p2;
            else
                insects.p2 = insects.p1;
        }
        else
        {
            // case 8
            _placement = Slice_placement.NORMAL;
        }

        var v_move = new Vector2d(_parent.Center, Center);
        var v1 = new Vector2d(_parent.Center, insects.p1);
        var default_dir = v_move.Det(v1) > 0 ? RotationDirection.CW : RotationDirection.CCW;

        if (dir == RotationDirection.Unknown)
        {
            if (magnet.IsUndefined())
            {
                dir = RotationDirection.CCW;    // just something
            }
            else
            {
                if (insects.p1.DistanceTo(magnet) < insects.p2.DistanceTo(magnet))  // match, use existing dir
                    dir = default_dir;
                else
                    dir = default_dir == RotationDirection.CCW ? RotationDirection.CW : RotationDirection.CCW;   // flip
            }
        }

        var midpoint = Center + (v_move.Unit() * Radius).Point;

        if (default_dir == dir)
        {
            _segments.Add(new Arc2F(Center, insects.p1, midpoint, dir));
            _segments.Add(new Arc2F(Center, midpoint, insects.p2, dir));
        }
        else
        {
            _segments.Add(new Arc2F(Center, insects.p2, midpoint, dir));
            _segments.Add(new Arc2F(Center, midpoint, insects.p1, dir));
        }

        _mid_ted = calc_ted(midpoint, tool_r);

        if (_mid_ted <= 0)
        {
            Logger.warn("forced to patch mid_ted");
            _mid_ted = coarse_ted;    // shouldn't be, but ok
        }

        _entry_ted = calc_entry_ted(tool_r);

        if (_entry_ted < 0)
            Logger.log("can't determine entry ted");

        _max_ted = Math.Max(_mid_ted, _entry_ted);
    }

    public Slice(Point2 center, double radius, RotationDirection dir)
    {
        _ball = new Circle2F(center, radius);
        _placement = Slice_placement.NORMAL;
        create_arc_circle(new Point2(center.X + radius, center.Y), dir);
    }
}
