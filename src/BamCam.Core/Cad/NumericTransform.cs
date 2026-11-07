using System.Globalization;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cad;

/// <summary>
/// A transform from numbers, in this order: scale and rotate about X, then Y, then Z around a centre, then move.
/// For what cannot be picked with the mouse in a large 3D model.
/// </summary>
public sealed record NumericTransform(Point3 Move, Point3 RotateDegrees, Point3 Scale, Point3 Center)
{
    private const double Epsilon = 1e-9;

    public Matrix4 Matrix =>
        Matrix4.Translation(-Center.X, -Center.Y, -Center.Z)
        * Matrix4.Scale(Scale.X, Scale.Y, Scale.Z)
        * Matrix4.RotationX(Angles.ToRadians(RotateDegrees.X))
        * Matrix4.RotationY(Angles.ToRadians(RotateDegrees.Y))
        * Matrix4.RotationZ(Angles.ToRadians(RotateDegrees.Z))
        * Matrix4.Translation(Center.X, Center.Y, Center.Z)
        * Matrix4.Translation(Move.X, Move.Y, Move.Z);

    /// <summary>A scale of 0 would flatten the objects for good.</summary>
    public bool IsValid => Math.Abs(Scale.X) > Epsilon && Math.Abs(Scale.Y) > Epsilon && Math.Abs(Scale.Z) > Epsilon;

    /// <summary>
    /// Circles and arcs stay circles and arcs: nothing leaves the XY plane and it is scaled evenly in it.
    /// A half turn about X or Y counts, as it only mirrors them.
    /// </summary>
    public static bool KeepsArcs(Matrix4 m) =>
        Math.Abs(m.M13) < Epsilon && Math.Abs(m.M23) < Epsilon && Math.Abs(m.M31) < Epsilon && Math.Abs(m.M32) < Epsilon &&
        Math.Abs(m.M11 * m.M11 + m.M12 * m.M12 - m.M21 * m.M21 - m.M22 * m.M22) < Epsilon &&
        Math.Abs(m.M11 * m.M21 + m.M12 * m.M22) < Epsilon;

    /// <summary>Whether the object contains circles or arcs, which are only stored for the XY plane.</summary>
    public static bool HasArcs(Entity entity) => entity switch
    {
        CircleEntity or ArcEntity or TextEntity => true,
        SurfaceEntity or PointListEntity or LineEntity or SplineEntity => false,
        RectEntity r => r.CornerRadius > 0,
        _ => entity.ToPaths().Any(p => p.Vertices.Any(v => Math.Abs(v.Bulge) > 1e-12)),
    };

    /// <summary>Whether the transform gives the right shape for this object.</summary>
    public static bool CanApply(Entity entity, Matrix4 m) => KeepsArcs(m) || !HasArcs(entity);

    /// <summary>The matrix as four rows of text, for showing and editing.</summary>
    public static string FormatMatrix(Matrix4 m)
    {
        static string N(double v) => (Math.Abs(v) < 1e-12 ? 0 : v).ToString("0.######", CultureInfo.InvariantCulture);
        return string.Join("\n", m.ToArray().Chunk(4).Select(row => string.Join("  ", row.Select(N))));
    }

    /// <summary>
    /// A matrix from 16 numbers separated by spaces or lines (a comma is a decimal point here). Null when it has fewer or
    /// more numbers, is not a usable transform of positions (the last column has to be 0 0 0 1), or flattens the objects.
    /// </summary>
    public static Matrix4? ParseMatrix(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 16) return null;
        var values = new double[16];
        for (var i = 0; i < 16; i++)
            if (!double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return null;
        var m = Matrix4.FromArray(values);
        if (Math.Abs(m.M14) > Epsilon || Math.Abs(m.M24) > Epsilon || Math.Abs(m.M34) > Epsilon || Math.Abs(m.M44 - 1) > Epsilon) return null;
        var determinant =
            m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
        return Math.Abs(determinant) > Epsilon ? m : null;
    }

    /// <summary>The 12 edges of a box after the transform, for a preview that costs nothing for a large object.</summary>
    public static List<(Point3 From, Point3 To)> BoxEdges(BoundingBox box, Matrix4 m)
    {
        var (a, b) = (box.Min, box.Max);
        Point3[] corners =
        [
            new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z),
            new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
        ];
        var moved = corners.Select(m.Transform).ToArray();
        int[][] edges = [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]];
        return edges.Select(e => (moved[e[0]], moved[e[1]])).ToList();
    }
}
