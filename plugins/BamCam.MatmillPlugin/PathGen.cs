// Ported from matmill (https://github.com/jkmnt/matmill), path_gen.cs and matmill.cs, MIT License,
// Copyright (c) 2016 jkmnt. See LICENSE-matmill.txt.
#nullable disable

using BamCam.Core.Geometry;

namespace Matmill;

public enum Sliced_path_item_type
{
    SLICE,
    SPIRAL,
    CHORD,
    SMOOTH_CHORD,
    GUIDE,
    SLICE_SHORTCUT,
    RETURN_TO_BASE,
}

/// <summary>The flat trajectory of a pocket or a profile: items in order, each a run of lines and arcs.</summary>
internal sealed class Sliced_path : List<Sliced_path_item>
{
    /// <summary>Direction from the start of the trajectory to the start of the shape (matmill's start normal).</summary>
    public Vector2d Start_normal = Vector2d.Undefined;
}

internal sealed class Sliced_path_item
{
    public readonly Sliced_path_item_type Item_type;
    public Point2 Start = Pt.Undefined;
    public readonly List<Piece> Pieces = [];

    public Sliced_path_item(Sliced_path_item_type type)
    {
        Item_type = type;
    }

    public Point2 FirstPoint => Start;

    public Point2 LastPoint => Pieces.Count > 0 ? Pieces[^1].To : Start;

    public void Add(Point2 pt)
    {
        if (Start.IsUndefined()) Start = pt;
        else Pieces.Add(new Piece(pt, false, default, false));
    }

    public void Add(Curve2d curve)
    {
        foreach (var pt in curve.Points)
            Add(pt);
    }

    public void Add(Line2F line)
    {
        if (Start.IsUndefined()) Start = line.p1;
        else if (LastPoint.DistanceTo(line.p1) > 1e-9) Add(line.p1);
        Add(line.p2);
    }

    public void Add(Arc2F arc)
    {
        if (Start.IsUndefined()) Start = arc.P1;
        else if (LastPoint.DistanceTo(arc.P1) > 1e-9) Add(arc.P1);
        Pieces.Add(new Piece(arc.P2, true, arc.Center, arc.Direction != RotationDirection.CW));
    }

    public void Add(Biarc2d biarc)
    {
        foreach (var seg in new[] { biarc.Seg1, biarc.Seg2 })
        {
            if (seg is Arc2F arc) Add(arc);
            else Add((Line2F)seg);
        }
    }

    public double GetPerimeter()
    {
        var length = 0.0;
        var at = Start;
        foreach (var p in Pieces)
        {
            if (p.IsArc)
            {
                var arc = new Arc2F(p.Center, at, p.To, p.Ccw ? RotationDirection.CCW : RotationDirection.CW);
                length += Math.Abs(arc.Sweep) * arc.Radius;
            }
            else
            {
                length += at.DistanceTo(p.To);
            }
            at = p.To;
        }
        return length;
    }
}

internal class Sliced_path_generator
{
    protected double _general_tolerance;

    public Sliced_path Path = [];
    protected Slice _last_slice;

    protected void append_slice(Slice slice)
    {
        // emit segments
        for (var segidx = 0; segidx < slice.Segments.Count; segidx++)
        {
            // connect segments
            if (segidx > 0)
            {
                var shortcut = new Sliced_path_item(Sliced_path_item_type.SLICE_SHORTCUT);
                shortcut.Add(slice.Segments[segidx - 1].P2);
                shortcut.Add(slice.Segments[segidx].P1);
                Path.Add(shortcut);
            }

            var arc = new Sliced_path_item(Sliced_path_item_type.SLICE);
            arc.Add(slice.Segments[segidx]);
            Path.Add(arc);
        }

        _last_slice = slice;
    }

    protected static Sliced_path_item connect_slices(Slice dst, Slice src)
    {
        var path = new Sliced_path_item(Sliced_path_item_type.CHORD);
        path.Add(src.End);
        path.Add(dst.Start);
        return path;
    }

    public void Append_spiral(Point2 start, Point2 end, Vector2d start_tangent, double ted, double tool_r, RotationDirection dir)
    {
        var spiral = new Sliced_path_item(Sliced_path_item_type.SPIRAL);

        var spacing = Spiral_generator.Calc_reverse_spacing(end.DistanceTo(start), tool_r, ted, _general_tolerance);

        foreach (var biarc in Spiral_generator.Gen_archimedean_spiral(start, end, start_tangent, spacing, dir))
            spiral.Add(biarc);

        Path.Add(spiral);
    }

    public void Append_root_slice(Slice slice) => append_slice(slice);

    public virtual void Append_slice(Slice slice, List<Point2> guide)
    {
        if (_last_slice == null)
            throw new Exception("attempt to install slice without the root slice");

        if (guide == null)
        {
            Path.Add(connect_slices(slice, _last_slice));
        }
        else
        {
            var p = new Sliced_path_item(Sliced_path_item_type.GUIDE);

            p.Add(_last_slice.End);
            foreach (var pt in guide)
                p.Add(pt);
            p.Add(slice.Start);

            Path.Add(p);
        }

        append_slice(slice);
    }

    public void Append_slice_sequence(Slice_sequence sequence)
    {
        Append_root_slice(sequence.Root_slice);

        for (var i = 1; i < sequence.Slices.Count; i++)
        {
            var s = sequence.Slices[i];
            Append_slice(s, s.Guide);
        }
    }

    public Sliced_path_generator(double general_tolerance)
    {
        _general_tolerance = general_tolerance;
    }
}

internal sealed class Sliced_path_smooth_generator : Sliced_path_generator
{
    private readonly double _min_arc_len;

    private bool is_biarc_inside_ball(Biarc2d biarc, Circle2F ball)
    {
        if (biarc.Pm.DistanceTo(ball.Center) > ball.Radius)
            return false;

        var start = biarc.P1;
        var end = biarc.P2;

        var insects = biarc.Seg1 is Line2F line1 ? ball.LineIntersect(line1) : ((Arc2F)biarc.Seg1).CircleIntersect(ball);

        if (!insects.p1.IsUndefined() && insects.p1.DistanceTo(start) < _general_tolerance)
            insects.p1 = Pt.Undefined;
        if (!insects.p2.IsUndefined() && insects.p2.DistanceTo(start) < _general_tolerance)
            insects.p2 = Pt.Undefined;

        if (!(insects.p1.IsUndefined() && insects.p2.IsUndefined()))
            return false;

        insects = biarc.Seg2 is Line2F line2 ? ball.LineIntersect(line2) : ((Arc2F)biarc.Seg2).CircleIntersect(ball);

        if (!insects.p1.IsUndefined() && insects.p1.DistanceTo(end) < _general_tolerance)
            insects.p1 = Pt.Undefined;
        if (!insects.p2.IsUndefined() && insects.p2.DistanceTo(end) < _general_tolerance)
            insects.p2 = Pt.Undefined;

        return insects.p1.IsUndefined() && insects.p2.IsUndefined();
    }

    private Sliced_path_item connect_slices_with_biarc(Slice dst, Slice src)
    {
        var start = src.End;
        var end = dst.Start;
        // unit normals to points
        var vn_start = new Vector2d(src.Center, start).Unit();
        var vn_end = new Vector2d(dst.Center, end).Unit();
        // tangents to points
        var vt_start = src.Dir == RotationDirection.CW ? new Vector2d(vn_start.Y, -vn_start.X) : new Vector2d(-vn_start.Y, vn_start.X);
        var vt_end = dst.Dir == RotationDirection.CW ? new Vector2d(vn_end.Y, -vn_end.X) : new Vector2d(-vn_end.Y, vn_end.X);

        var biarc = new Biarc2d(start, vt_start, end, vt_end);

        if (!is_biarc_inside_ball(biarc, src.Ball))
            return null;

        var path = new Sliced_path_item(Sliced_path_item_type.SMOOTH_CHORD);
        path.Add(biarc);
        return path;
    }

    private Sliced_path_item smooth_connect_slices(Slice dst, Slice src)
    {
        Sliced_path_item path = null;

        // do not emit biarcs if distance is too small
        if (src.End.DistanceTo(dst.Start) > _min_arc_len)
            path = connect_slices_with_biarc(dst, src); // otherwise (outside the slice) a chord, as matmill does
        return path;
    }

    public override void Append_slice(Slice slice, List<Point2> guide)
    {
        if (guide == null)
        {
            var biarc = smooth_connect_slices(slice, _last_slice);
            if (biarc != null)
            {
                Path.Add(biarc);
                append_slice(slice);
                return;
            }
        }

        base.Append_slice(slice, guide);
    }

    public Sliced_path_smooth_generator(double general_tolerance, double min_arc_len) : base(general_tolerance)
    {
        _min_arc_len = min_arc_len;
    }
}

/// <summary>Clears a pocket (an outline with islands) with slices along its medial axis.</summary>
internal sealed class Pocket_generator
{
    private readonly Topographer _topo;

    private double _general_tolerance = 0.001;
    private double _tool_r = 1.5;
    private double _margin;
    private double _max_ted = 3.0 * 0.4;
    private double _min_ted = 3.0 * 0.1;
    private Point2 _startpoint = Pt.Undefined;
    private RotationDirection _dir = RotationDirection.CW;
    private bool _should_smooth_chords;
    private bool _startpoint_is_a_hint;
    private double _slice_leadin_angle = 3 * Math.PI / 180;
    private double _slice_leadout_angle = 0.5 * Math.PI / 180;
    private Vector2d _spiral_tangent = new(); // as matmill: a zero vector unless the profile sets one

    public double Tool_d { set => _tool_r = value / 2.0; }
    public double General_tolerance { set => _general_tolerance = value; }
    public double Margin { set => _margin = value; }
    public double Max_ted { set => _max_ted = value; }
    public double Min_ted { set => _min_ted = value; }
    public Point2 Startpoint { set => _startpoint = value; }
    public RotationDirection Mill_direction { set => _dir = value; }
    public bool Should_smooth_chords { set => _should_smooth_chords = value; }
    public bool Startpoint_is_a_hint { set => _startpoint_is_a_hint = value; }
    public Vector2d Spiral_tangent { set => _spiral_tangent = value; }
    public CancellationToken Cancel { get; set; }

    private double _min_passable_mic_radius => 0.1 * _tool_r; // 5 % of tool diameter is seems to be ok

    private double get_mic_radius(Point2 pt) => _topo.Get_dist_to_wall(pt) - _tool_r - _margin;

    private Sliced_path generate_path(Slice_sequence sequence)
    {
        var gen = !_should_smooth_chords
            ? new Sliced_path_generator(_general_tolerance)
            : new Sliced_path_smooth_generator(_general_tolerance, 0.1 * _tool_r);

        gen.Append_spiral(sequence.Root_slice.Center, sequence.Root_slice.End, _spiral_tangent, _max_ted, _tool_r,
            _dir == RotationDirection.Unknown ? RotationDirection.CCW : _dir);
        gen.Append_slice_sequence(sequence);

        return gen.Path;
    }

    private double radius_getter(Point2 pt)
    {
        var radius = get_mic_radius(pt);
        return radius < _min_passable_mic_radius ? 0 : radius;
    }

    public Sliced_path run()
    {
        if (_dir == RotationDirection.Unknown && _should_smooth_chords)
            throw new Exception("smooth chords are not allowed for the variable mill direction");

        var tree = new Branch(null);
        var is_ok = _topo.Build_medial_tree(tree, _tool_r / 10, _general_tolerance, _startpoint, _min_passable_mic_radius + _tool_r + _margin,
            _startpoint_is_a_hint, Cancel);

        if (!is_ok)
        {
            Logger.warn("failed to build tree");
            return null;
        }

        var slicer = new Branch_slicer(_topo.Min, _topo.Max, _general_tolerance, Cancel)
        {
            Slice_leadin_angle = _slice_leadin_angle,
            Slice_leadout_angle = _slice_leadout_angle,
            Get_radius = radius_getter,
        };

        var sequence = slicer.Run(tree, _tool_r, _max_ted, _min_ted, _dir);

        return generate_path(sequence);
    }

    public Pocket_generator(Contour outline, Contour[] islands)
    {
        _topo = new Topographer(outline, islands);
    }
}

/// <summary>Cuts along a polyline with slices of a fixed radius (a trochoidal slot or profile).</summary>
internal sealed class Engrave_generator
{
    private const double TED_TOLERANCE_PERCENTAGE = 0.001;  // 0.1 %

    private readonly Contour _poly;
    private Topographer _topo;

    private double _general_tolerance = 0.001;
    private double _tool_r = 1.5;
    private double _max_ted = 3.0 * 0.4;
    private RotationDirection _dir = RotationDirection.CW;
    private bool _should_smooth_chords;
    private double _slice_leadin_angle = 3 * Math.PI / 180;
    private double _slice_leadout_angle = 0.5 * Math.PI / 180;
    private double _slice_radius = 1.5;
    private Vector2d _spiral_tangent = new(); // as matmill: a zero vector unless the profile sets one

    public Vector2d Spiral_tangent { set => _spiral_tangent = value; }
    public double Tool_d { set => _tool_r = value / 2.0; }
    public double General_tolerance { set => _general_tolerance = value; }
    public double Max_ted { set => _max_ted = value; }
    public double Slice_radius { set => _slice_radius = value; }
    public RotationDirection Mill_direction { set => _dir = value; }
    public bool Should_smooth_chords { set => _should_smooth_chords = value; }
    public CancellationToken Cancel { get; set; }

    private int find_optimal_slice(Slice parent, Point2 pt, ref Slice _candidate)
    {
        var s = new Slice(parent, pt, _slice_radius, RotationDirection.CCW, _tool_r, Pt.Undefined);

        if (s.Placement == Slice_placement.INSIDE_ANOTHER) return 1;    // go right
        if (s.Placement == Slice_placement.TOO_FAR) return -1;          // go left

        _candidate = s;

        if (s.Max_ted > _max_ted) return -1;                                            // overshoot, go left
        if ((_max_ted - s.Max_ted) / _max_ted > TED_TOLERANCE_PERCENTAGE) return 1;     // undershoot outside the strict TED tolerance, go right

        return 0;                                                                       // good slice inside the tolerance, stop search
    }

    // NOTE: funny way to calc it: try a lot of slices on a test line, approaching the best placement with the correct TED
    private double calc_optimal_step()
    {
        var branch = new Branch(null);
        branch.Add_point(new Point2(0, 0));
        branch.Add_point(new Point2(0, _tool_r * 2));

        var slice_a = new Slice(new Point2(0, 0), _slice_radius, RotationDirection.CCW);
        Slice slice_b = null;

        var t = 0.0;
        branch.Bisect(pt => find_optimal_slice(slice_a, pt, ref slice_b), ref t, _general_tolerance);

        if (slice_b == null) throw new Exception("no step along the path keeps the stepover");
        return slice_a.Center.DistanceTo(slice_b.Center);
    }

    private Sliced_path generate_path(Slice_sequence sequence)
    {
        var gen = !_should_smooth_chords
            ? new Sliced_path_generator(_general_tolerance)
            : new Sliced_path_smooth_generator(_general_tolerance, 0.1 * _tool_r);

        gen.Append_spiral(sequence.Root_slice.Center, sequence.Root_slice.End, _spiral_tangent, _max_ted, _tool_r,
            _dir == RotationDirection.Unknown ? RotationDirection.CCW : _dir);
        gen.Append_slice_sequence(sequence);

        return gen.Path;
    }

    public Sliced_path run()
    {
        if (_dir == RotationDirection.Unknown && _should_smooth_chords)
            throw new Exception("smooth chords are not allowed for the variable mill direction");

        _topo = new Topographer(_poly, []);

        var step = calc_optimal_step();
        var slice_centers = _topo.Get_samples_exact(step);
        if (slice_centers.Count < 2) return null;

        var slicer = new Bypoint_slicer(_topo.Min, _topo.Max, _general_tolerance, Cancel)
        {
            Slice_leadin_angle = _slice_leadin_angle,
            Slice_leadout_angle = _slice_leadout_angle,
        };

        var sequence = slicer.Run(slice_centers, _tool_r, _slice_radius, _dir);

        return generate_path(sequence);
    }

    public Engrave_generator(Contour poly)
    {
        _poly = poly;
    }
}
