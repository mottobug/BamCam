using System.Globalization;

namespace BamCam.Core.Geometry;

public readonly record struct Point2(double X, double Y)
{
    public static readonly Point2 Zero = new(0, 0);

    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator -(Point2 a) => new(-a.X, -a.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);
    public static Point2 operator *(double s, Point2 a) => new(a.X * s, a.Y * s);
    public static Point2 operator /(Point2 a, double s) => new(a.X / s, a.Y / s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public double Angle => Math.Atan2(Y, X);

    public Point2 Normalized()
    {
        var len = Length;
        return len < Tolerance.Epsilon ? Zero : this / len;
    }

    /// <summary>Left-hand perpendicular (rotated +90°).</summary>
    public Point2 Perp => new(-Y, X);

    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    public static double Distance(Point2 a, Point2 b) => (a - b).Length;
    public static Point2 Lerp(Point2 a, Point2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    public static Point2 FromAngle(double angle, double radius = 1) => new(Math.Cos(angle) * radius, Math.Sin(angle) * radius);

    public Point2 Rotate(double angle, Point2 center)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var d = this - center;
        return new Point2(center.X + d.X * c - d.Y * s, center.Y + d.X * s + d.Y * c);
    }

    public bool AlmostEquals(Point2 o, double tol = Tolerance.Epsilon) => Distance(this, o) <= tol;

    public Point3 ToPoint3(double z = 0) => new(X, Y, z);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y}");
}

public readonly record struct Point3(double X, double Y, double Z)
{
    public static readonly Point3 Zero = new(0, 0, 0);

    public static Point3 operator +(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3 operator -(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3 operator *(Point3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public Point2 XY => new(X, Y);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static double Distance(Point3 a, Point3 b) => (a - b).Length;
    public static Point3 Cross(Point3 a, Point3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public Point3 Normalized()
    {
        var len = Length;
        return len < Tolerance.Epsilon ? Zero : this * (1 / len);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Z}");
}

public static class Tolerance
{
    public const double Epsilon = 1e-9;
    /// <summary>Default distance under which two points are considered coincident.</summary>
    public const double Point = 1e-6;
    /// <summary>Default chord tolerance used when flattening arcs.</summary>
    public const double Chord = 0.005;
}
