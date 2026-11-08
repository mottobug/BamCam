// Ported from matmill (https://github.com/jkmnt/matmill), MIT License, Copyright (c) 2016 jkmnt.
// See LICENSE-matmill.txt. matmill was written against the geometry of CamBam; this file stands in for the parts of it
// that matmill uses (points, lines, arcs, circles), on top of BamCam's Point2. Vector2d, Biarc2d and Curve2d are
// matmill's own (geom.cs).
#nullable disable

using BamCam.Core.Geometry;

namespace Matmill;

/// <summary>Direction of an arc; the values are those matmill multiplies with (CCW = 1, CW = -1, Unknown = mixed).</summary>
public enum RotationDirection { CW = -1, Unknown = 0, CCW = 1 }

/// <summary>An undefined point (NaN), as CamBam's Point2F.Undefined.</summary>
internal static class Pt
{
    public static readonly Point2 Undefined = new(double.NaN, double.NaN);

    public static bool IsUndefined(this Point2 p) => double.IsNaN(p.X) || double.IsNaN(p.Y);

    public static double DistanceTo(this Point2 a, Point2 b) => Point2.Distance(a, b);
}

/// <summary>A line from p1 to p2; also the pair of points an intersection returns (either may be undefined).</summary>
internal sealed class Line2F
{
    public Point2 p1;
    public Point2 p2;

    public Line2F() : this(Pt.Undefined, Pt.Undefined) { }

    public Line2F(Point2 a, Point2 b)
    {
        p1 = a;
        p2 = b;
    }

    public Line2F(double x1, double y1, double x2, double y2) : this(new Point2(x1, y1), new Point2(x2, y2)) { }

    public double Length() => Point2.Distance(p1, p2);

    /// <summary>The point of the line nearest to <paramref name="pt"/>, with its distance.</summary>
    public Point2 NearestPoint(Point2 pt, ref double dist)
    {
        var d = p2 - p1;
        var len2 = d.LengthSquared;
        var t = len2 > 0 ? Math.Clamp(Point2.Dot(pt - p1, d) / len2, 0, 1) : 0;
        var nearest = p1 + d * t;
        dist = Point2.Distance(pt, nearest);
        return nearest;
    }
}

internal readonly struct Circle2F
{
    public readonly Point2 Center;
    public readonly double Radius;

    public Circle2F(Point2 center, double radius)
    {
        Center = center;
        Radius = radius;
    }

    /// <summary>Both points where two circles cross (one twice when they touch), or two undefined points.</summary>
    public Line2F CircleIntersect(Circle2F other)
    {
        var d = Point2.Distance(Center, other.Center);
        var scale = Math.Max(Math.Max(Radius, other.Radius), 1e-9);
        if (d < 1e-12 * scale || d > Radius + other.Radius + 1e-12 * scale || d < Math.Abs(Radius - other.Radius) - 1e-12 * scale)
            return new Line2F();
        var a = (Radius * Radius - other.Radius * other.Radius + d * d) / (2 * d);
        var h = Math.Sqrt(Math.Max(Radius * Radius - a * a, 0));
        var dir = (other.Center - Center) / d;
        var foot = Center + dir * a;
        var perp = dir.Perp * h;
        return new Line2F(foot + perp, foot - perp);
    }

    /// <summary>Where the stretch of a line crosses the circle: none, one (p2 undefined) or two points.</summary>
    public Line2F LineIntersect(Line2F line, double tolerance = 0)
    {
        var d = line.p2 - line.p1;
        var f = line.p1 - Center;
        var a = d.LengthSquared;
        if (a < 1e-24) return new Line2F();
        var b = 2 * Point2.Dot(f, d);
        var c = f.LengthSquared - Radius * Radius;
        var disc = b * b - 4 * a * c;
        if (disc < 0) return new Line2F();
        var root = Math.Sqrt(disc);
        var slack = tolerance / Math.Sqrt(a);
        var found = new List<Point2>(2);
        foreach (var t in disc == 0 ? new[] { -b / (2 * a) } : new[] { (-b - root) / (2 * a), (-b + root) / (2 * a) })
            if (t >= -slack && t <= 1 + slack) found.Add(line.p1 + d * t);
        return found.Count switch
        {
            0 => new Line2F(),
            1 => new Line2F(found[0], Pt.Undefined),
            _ => new Line2F(found[0], found[1]),
        };
    }

    public Line2F LineIntersect(Line2F line) => LineIntersect(line, 0);
}

/// <summary>An arc around a centre from P1 to P2 in a direction (CamBam's Arc2F).</summary>
internal readonly struct Arc2F
{
    public readonly Point2 Center;
    public readonly Point2 P1;
    public readonly Point2 P2;
    public readonly RotationDirection Direction;

    public Arc2F(Point2 center, Point2 p1, Point2 p2, RotationDirection direction)
    {
        Center = center;
        P1 = p1;
        P2 = p2;
        Direction = direction == RotationDirection.Unknown ? RotationDirection.CCW : direction;
    }

    public double Radius => Point2.Distance(Center, P1);

    private double StartAngle => (P1 - Center).Angle;

    /// <summary>Signed sweep, positive counter-clockwise; a whole turn when the ends meet.</summary>
    public double Sweep
    {
        get
        {
            var a1 = StartAngle;
            var a2 = (P2 - Center).Angle;
            if (Direction == RotationDirection.CCW)
            {
                var s = Angles.Normalize(a2 - a1);
                return s < 1e-12 ? 2 * Math.PI : s;
            }
            var cw = Angles.Normalize(a1 - a2);
            return -(cw < 1e-12 ? 2 * Math.PI : cw);
        }
    }

    public Point2 Midpoint => Center + Point2.FromAngle(StartAngle + Sweep / 2, Radius);

    public Arc2 ToArc2() => new(Center, Radius, StartAngle, Sweep);

    public bool ContainsAngle(double angle, double slack = 1e-9)
    {
        var sweep = Sweep;
        var from = sweep >= 0 ? StartAngle : StartAngle + sweep;
        var d = Angles.Normalize(angle - from);
        return d <= Math.Abs(sweep) + slack || d >= 2 * Math.PI - slack;
    }

    public void GetExtrema(ref Point2 min, ref Point2 max)
    {
        var b = ToArc2().Bounds();
        min = b.Min.XY;
        max = b.Max.XY;
    }

    /// <summary>Points where the circle crosses this arc: none, one (p2 undefined) or two.</summary>
    public Line2F CircleIntersect(Circle2F circle)
    {
        var both = new Circle2F(Center, Radius).CircleIntersect(circle);
        var found = new List<Point2>(2);
        foreach (var p in new[] { both.p1, both.p2 })
            if (!p.IsUndefined() && ContainsAngle((p - Center).Angle) && !found.Any(f => f.DistanceTo(p) < 1e-12))
                found.Add(p);
        return found.Count switch
        {
            0 => new Line2F(),
            1 => new Line2F(found[0], Pt.Undefined),
            _ => new Line2F(found[0], found[1]),
        };
    }

    public Point2 NearestPoint(Point2 pt, ref double dist)
    {
        var angle = (pt - Center).Angle;
        Point2 nearest;
        if (ContainsAngle(angle) && Point2.Distance(pt, Center) > 1e-15)
            nearest = Center + Point2.FromAngle(angle, Radius);
        else
            nearest = Point2.Distance(pt, P1) <= Point2.Distance(pt, P2) ? P1 : P2;
        dist = Point2.Distance(pt, nearest);
        return nearest;
    }
}

public struct Vector2d
{
    public double X;
    public double Y;

    public double Mag => Math.Sqrt(X * X + Y * Y);

    public double Angle => Math.Atan2(Y, X);

    public double Ccw_angle
    {
        get
        {
            var angle = Angle;
            return angle >= 0 ? angle : angle + 2.0 * Math.PI;
        }
    }

    public Point2 Point => new(X, Y);

    public Vector2d(double x, double y)
    {
        X = x;
        Y = y;
    }

    public Vector2d(Point2 pt)
    {
        X = pt.X;
        Y = pt.Y;
    }

    public Vector2d(Point2 start, Point2 end)
    {
        X = end.X - start.X;
        Y = end.Y - start.Y;
    }

    public static Vector2d Undefined => new(double.NaN, double.NaN);

    public Vector2d Normal() => new(-Y, X);

    public Vector2d Inverted() => new(-X, -Y);

    public Vector2d Unit()
    {
        var mag = Mag;
        return new Vector2d(X / mag, Y / mag);
    }

    public Vector2d Rotated(double theta)
    {
        var x = X * Math.Cos(theta) - Y * Math.Sin(theta);
        var y = X * Math.Sin(theta) + Y * Math.Cos(theta);
        return new Vector2d(x, y);
    }

    public double Det(Vector2d b) => X * b.Y - Y * b.X;

    public double Angle_to(Vector2d b) => Math.Atan2(Det(b), this * b);

    public double Ccw_angle_to(Vector2d b)
    {
        var angle = Angle_to(b);
        return angle >= 0 ? angle : angle + 2.0 * Math.PI;
    }

    public static double operator *(Vector2d a, Vector2d b) => a.X * b.X + a.Y * b.Y;
    public static Vector2d operator *(Vector2d a, double d) => new(a.X * d, a.Y * d);
    public static Vector2d operator *(double d, Vector2d a) => a * d;
    public static Vector2d operator +(Vector2d a, Vector2d b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2d operator -(Vector2d a, Vector2d b) => new(a.X - b.X, a.Y - b.Y);
}

internal static class GeomUtils
{
    private static bool solve_cramer2(double a1, double b1, double a2, double b2, double c1, double c2, ref double x, ref double y)
    {
        var d = a1 * b2 - b1 * a2;

        if (d == 0)
            return false;

        x = (c1 * b2 - b1 * c2) / d;
        y = (a1 * c2 - c1 * a2) / d;
        return true;
    }

    public static bool Circle_by_3_pt(double x1, double y1, double x2, double y2, double x3, double y3, ref double xc, ref double yc)
    {
        var a1 = 2 * (x1 - x2);
        var b1 = 2 * (y1 - y2);
        var c1 = x1 * x1 + y1 * y1 - x2 * x2 - y2 * y2;

        var a2 = 2 * (x1 - x3);
        var b2 = 2 * (y1 - y3);
        var c2 = x1 * x1 + y1 * y1 - x3 * x3 - y3 * y3;

        return solve_cramer2(a1, b1, a2, b2, c1, c2, ref xc, ref yc);
    }
}

/// <summary>A segment of a toolpath item: a line or an arc to a point.</summary>
internal readonly record struct Piece(Point2 To, bool IsArc, Point2 Center, bool Ccw);

internal struct Biarc2d
{
    public object Seg1;
    public object Seg2;

    public Point2 P1 => Seg1 is Arc2F a ? a.P1 : ((Line2F)Seg1).p1;
    public Point2 Pm => Seg1 is Arc2F a ? a.P2 : ((Line2F)Seg1).p2;
    public Point2 P2 => Seg2 is Arc2F a ? a.P2 : ((Line2F)Seg2).p2;

    // Adapted from http://www.ryanjuckett.com/programming/biarc-interpolation (as matmill did).
    private static Point2 calc_pm(Point2 p1, Vector2d t1, Point2 p2, Vector2d t2)
    {
        var v = new Vector2d(p2 - p1);
        var t = t1 + t2;
        var v_dot_t = v * t;
        var t1_dot_t2 = t1 * t2;

        double d2;
        var d2_denom = 2 * (1 - t1_dot_t2);

        if (d2_denom == 0)  // equal tangents
        {
            d2_denom = 4.0 * (v * t2);
            d2 = v * v / d2_denom;
            if (d2_denom == 0)  // v perpendicular to tangents
                return p1 + (v * 0.5).Point;
        }
        else    // normal case
        {
            var d2_num = -v_dot_t + Math.Sqrt(v_dot_t * v_dot_t + 2 * (1 - t1_dot_t2) * (v * v));
            d2 = d2_num / d2_denom;
        }

        return (p1 + p2 + (d2 * (t1 - t2)).Point) * 0.5;
    }

    private static object[] calc_arcs(Point2 p1, Vector2d t1, Point2 p2, Vector2d t2, Point2 pm)
    {
        var segs = new object[2];

        var n1 = t1.Normal();
        var pm_minus_p1 = new Vector2d(pm - p1);
        var c1_denom = 2 * (n1 * pm_minus_p1);
        if (c1_denom == 0)  // c1 is at infinity, replace arc with line
        {
            segs[0] = new Line2F(p1, pm);
        }
        else
        {
            var c1 = p1 + (pm_minus_p1 * pm_minus_p1 / c1_denom * n1).Point;
            var dir1 = new Vector2d(p1 - c1) * n1 > 0 ? RotationDirection.CW : RotationDirection.CCW;
            segs[0] = new Arc2F(c1, p1, pm, dir1);
        }

        var n2 = t2.Normal();
        var pm_minus_p2 = new Vector2d(pm - p2);
        var c2_denom = 2 * (n2 * pm_minus_p2);
        if (c2_denom == 0)  // c2 is at infinity, replace arc with line
        {
            segs[1] = new Line2F(pm, p2);
        }
        else
        {
            var c2 = p2 + (pm_minus_p2 * pm_minus_p2 / c2_denom * n2).Point;
            var dir2 = new Vector2d(p2 - c2) * n2 > 0 ? RotationDirection.CW : RotationDirection.CCW;
            segs[1] = new Arc2F(c2, pm, p2, dir2);
        }

        return segs;
    }

    public Biarc2d(Point2 p1, Vector2d t1, Point2 p2, Vector2d t2)
    {
        var pm = calc_pm(p1, t1, p2, t2);
        var segs = calc_arcs(p1, t1, p2, t2, pm);
        Seg1 = segs[0];
        Seg2 = segs[1];
    }
}

internal sealed class Curve2d
{
    private readonly List<Point2> _points = [];
    private readonly List<double> _seg_offsets = [];
    private double _len;

    public List<Point2> Points => _points;
    public Point2 Start => _points[0];
    public Point2 End => _points[^1];
    public double Length => _len;

    public void Add(Point2 pt)
    {
        if (_points.Count != 0)
        {
            var seg_len = End.DistanceTo(pt);
            _seg_offsets.Add(_len);
            _len += seg_len;
        }
        _points.Add(pt);
    }

    public Point2 Get_parametric_pt(double u)
    {
        if (_points.Count < 2)
            return _points[0];

        var offset = u * _len;

        var seg = _seg_offsets.BinarySearch(offset);

        // if there is no exact element found, binary search returns 1's complemented index of the
        // first larger element, so we use it to find last smaller element
        if (seg < 0)
            seg = ~seg - 1;
        seg = Math.Clamp(seg, 0, _seg_offsets.Count - 1);

        offset -= _seg_offsets[seg];

        var p1 = _points[seg];
        var p2 = _points[seg + 1];
        var dist = p2.DistanceTo(p1);
        if (dist <= 0) return p1;
        var x = p1.X + offset / dist * (p2.X - p1.X);
        var y = p1.Y + offset / dist * (p2.Y - p1.Y);

        return new Point2(x, y);
    }
}
