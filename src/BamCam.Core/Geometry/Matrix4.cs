namespace BamCam.Core.Geometry;

/// <summary>
/// Row-major 4x4 affine transform. Points are treated as row vectors (p' = p * M),
/// so translation lives in M41..M43. This mirrors the common CAD convention.
/// </summary>
public readonly record struct Matrix4(
    double M11, double M12, double M13, double M14,
    double M21, double M22, double M23, double M24,
    double M31, double M32, double M33, double M34,
    double M41, double M42, double M43, double M44)
{
    public static readonly Matrix4 Identity = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public bool IsIdentity => this == Identity;

    public static Matrix4 Translation(double dx, double dy, double dz = 0) => Identity with { M41 = dx, M42 = dy, M43 = dz };

    public static Matrix4 Scale(double sx, double sy, double sz = 1) => Identity with { M11 = sx, M22 = sy, M33 = sz };

    public static Matrix4 Scale(double s, Point2 center) =>
        Translation(-center.X, -center.Y) * Scale(s, s, s) * Translation(center.X, center.Y);

    /// <summary>Rotation about the Z axis (counter-clockwise, radians).</summary>
    public static Matrix4 RotationZ(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return Identity with { M11 = c, M12 = s, M21 = -s, M22 = c };
    }

    public static Matrix4 RotationX(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return Identity with { M22 = c, M23 = s, M32 = -s, M33 = c };
    }

    public static Matrix4 RotationY(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        return Identity with { M11 = c, M13 = -s, M31 = s, M33 = c };
    }

    public static Matrix4 RotationZ(double angle, Point2 center) =>
        Translation(-center.X, -center.Y) * RotationZ(angle) * Translation(center.X, center.Y);

    /// <summary>Mirror across the line through <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static Matrix4 Mirror(Point2 a, Point2 b)
    {
        var angle = (b - a).Angle;
        return Translation(-a.X, -a.Y) * RotationZ(-angle) * Scale(1, -1) * RotationZ(angle) * Translation(a.X, a.Y);
    }

    public static Matrix4 operator *(Matrix4 a, Matrix4 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
        a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

    public Point3 Transform(Point3 p) => new(
        p.X * M11 + p.Y * M21 + p.Z * M31 + M41,
        p.X * M12 + p.Y * M22 + p.Z * M32 + M42,
        p.X * M13 + p.Y * M23 + p.Z * M33 + M43);

    public Point2 Transform(Point2 p) => new(
        p.X * M11 + p.Y * M21 + M41,
        p.X * M12 + p.Y * M22 + M42);

    /// <summary>Determinant of the XY part; negative means the transform mirrors (reverses arc direction).</summary>
    public double DeterminantXY => M11 * M22 - M12 * M21;

    /// <summary>Uniform XY scale factor (geometric mean), used to scale radii.</summary>
    public double ScaleXY => Math.Sqrt(Math.Abs(DeterminantXY));

    public double[] ToArray() =>
    [
        M11, M12, M13, M14, M21, M22, M23, M24,
        M31, M32, M33, M34, M41, M42, M43, M44,
    ];

    public static Matrix4 FromArray(IReadOnlyList<double> v)
    {
        if (v.Count != 16) throw new ArgumentException("Matrix requires 16 values", nameof(v));
        return new Matrix4(
            v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
            v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
    }
}
