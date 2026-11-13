namespace BamCam.Core.Geometry;

/// <summary>
/// A transform as numbers a user types: scale X/Y/Z and rotate about Z, both about the origin, then move. A scale of
/// -1 mirrors (X -1: about the Y axis, Y -1: about the X axis). Used for the transform of machining operations.
/// </summary>
public readonly record struct TransformParts(
    double ScaleX = 1, double ScaleY = 1, double ScaleZ = 1, double RotateZ = 0,
    double MoveX = 0, double MoveY = 0, double MoveZ = 0)
{
    public static readonly TransformParts None = new(1, 1, 1, 0, 0, 0, 0);

    /// <summary>Scale, then rotate (degrees, counter-clockwise), then move.</summary>
    public Matrix4 ToMatrix() =>
        Matrix4.Scale(ScaleX, ScaleY, ScaleZ) * Matrix4.RotationZ(RotateZ * Math.PI / 180) * Matrix4.Translation(MoveX, MoveY, MoveZ);

    /// <summary>
    /// The parts of a matrix, if it has no other rotation or skew (it is then exactly <see cref="ToMatrix"/> of them).
    /// A mirror shows as a negative Y scale with the matching rotation; pure mirrors in X or Y come out as such.
    /// </summary>
    public static TransformParts? From(Matrix4 m)
    {
        const double eps = 1e-9;
        if (Math.Abs(m.M13) > eps || Math.Abs(m.M23) > eps || Math.Abs(m.M31) > eps || Math.Abs(m.M32) > eps
            || Math.Abs(m.M14) > eps || Math.Abs(m.M24) > eps || Math.Abs(m.M34) > eps || Math.Abs(m.M44 - 1) > eps)
            return null;
        var sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        if (sx < eps || Math.Abs(m.M33) < eps) return null;
        var angle = Math.Atan2(m.M12, m.M11);
        var sy = (m.M11 * m.M22 - m.M12 * m.M21) / sx;
        // A mirror in X (scale X -1) gives angle 180° with Y -1: write it as X -1 instead.
        if (Math.Abs(Math.Abs(angle) - Math.PI) < eps && sy < 0)
        {
            sx = -sx;
            sy = -sy;
            angle = 0;
        }
        var parts = new TransformParts(sx, sy, m.M33, Clean(angle * 180 / Math.PI), Clean(m.M41), Clean(m.M42), Clean(m.M43));
        // No skew: the matrix must come back from the parts.
        var back = parts.ToMatrix();
        return Math.Abs(back.M21 - m.M21) < 1e-7 * Math.Max(1, sx) && Math.Abs(back.M22 - m.M22) < 1e-7 * Math.Max(1, sx) ? parts : null;
    }

    private static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Round(v, 9);
}
