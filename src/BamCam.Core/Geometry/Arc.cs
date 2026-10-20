namespace BamCam.Core.Geometry;

/// <summary>
/// Circular arc in the XY plane. Angles in radians; <see cref="Sweep"/> is positive for
/// counter-clockwise and negative for clockwise arcs.
/// </summary>
public readonly record struct Arc2(Point2 Center, double Radius, double StartAngle, double Sweep)
{
    public double EndAngle => StartAngle + Sweep;
    public bool IsClockwise => Sweep < 0;
    public Point2 Start => Center + Point2.FromAngle(StartAngle, Radius);
    public Point2 End => Center + Point2.FromAngle(EndAngle, Radius);
    public double Length => Math.Abs(Sweep) * Radius;

    public Point2 PointAt(double t) => Center + Point2.FromAngle(StartAngle + Sweep * t, Radius);

    /// <summary>Number of segments needed so that the chord error stays below <paramref name="tolerance"/>.</summary>
    public int SegmentCount(double tolerance = Tolerance.Chord)
    {
        if (Radius <= tolerance) return 1;
        var maxStep = 2 * Math.Acos(1 - tolerance / Radius);
        if (!(maxStep > 0)) maxStep = Math.PI / 18;
        return Math.Max(1, (int)Math.Ceiling(Math.Abs(Sweep) / maxStep));
    }

    public IEnumerable<Point2> Flatten(double tolerance = Tolerance.Chord, bool includeStart = true)
    {
        var n = SegmentCount(tolerance);
        for (var i = includeStart ? 0 : 1; i <= n; i++)
            yield return PointAt((double)i / n);
    }

    public BoundingBox Bounds()
    {
        var b = BoundingBox.Empty.Include(Start).Include(End);
        // Include axis extreme points covered by the sweep.
        for (var k = 0; k < 4; k++)
        {
            var a = k * Math.PI / 2;
            if (ContainsAngle(a)) b = b.Include(Center + Point2.FromAngle(a, Radius));
        }
        return b;
    }

    public bool ContainsAngle(double angle)
    {
        var s = Sweep >= 0 ? StartAngle : StartAngle + Sweep;
        var span = Math.Abs(Sweep);
        var d = Angles.Normalize(angle - s);
        return d <= span + Tolerance.Epsilon;
    }

    /// <summary>Converts a polyline bulge (tan(sweep/4)) between two points into an arc.</summary>
    public static Arc2 FromBulge(Point2 p1, Point2 p2, double bulge)
    {
        var sweep = 4 * Math.Atan(bulge);
        var chord = Point2.Distance(p1, p2);
        var radius = chord / (2 * Math.Sin(Math.Abs(sweep) / 2));
        // Distance from chord midpoint to centre, signed by bulge direction.
        var mid = Point2.Lerp(p1, p2, 0.5);
        var sagittaDir = (p2 - p1).Normalized().Perp;
        var h = radius * Math.Cos(sweep / 2);
        var center = mid + sagittaDir * (Math.Sign(bulge) * h);
        var startAngle = (p1 - center).Angle;
        return new Arc2(center, radius, startAngle, sweep);
    }

    public double ToBulge() => Math.Tan(Sweep / 4);

    /// <summary>Arc through three points, or null if they are collinear.</summary>
    public static Arc2? FromThreePoints(Point2 a, Point2 b, Point2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < Tolerance.Epsilon) return null;
        var a2 = a.LengthSquared;
        var b2 = b.LengthSquared;
        var c2 = c.LengthSquared;
        var center = new Point2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        var r = Point2.Distance(center, a);
        var sa = (a - center).Angle;
        var ccw = Point2.Cross(b - a, c - b) > 0;
        var ea = (c - center).Angle;
        var sweep = ccw ? Angles.Normalize(ea - sa) : -Angles.Normalize(sa - ea);
        return new Arc2(center, r, sa, sweep);
    }
}

public static class Angles
{
    public const double TwoPi = Math.PI * 2;

    /// <summary>Normalises an angle into [0, 2π).</summary>
    public static double Normalize(double a)
    {
        a %= TwoPi;
        if (a < 0) a += TwoPi;
        return a;
    }

    public static double ToRadians(double degrees) => degrees * Math.PI / 180;
    public static double ToDegrees(double radians) => radians * 180 / Math.PI;
}
