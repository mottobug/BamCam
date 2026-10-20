namespace BamCam.Core.Geometry;

public readonly record struct BoundingBox(Point3 Min, Point3 Max)
{
    public static readonly BoundingBox Empty = new(
        new Point3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Point3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y;

    public double Width => IsEmpty ? 0 : Max.X - Min.X;
    public double Height => IsEmpty ? 0 : Max.Y - Min.Y;
    public double Depth => IsEmpty ? 0 : Max.Z - Min.Z;
    public Point3 Center => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);

    public BoundingBox Include(Point3 p) => new(
        new Point3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z)),
        new Point3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z)));

    public BoundingBox Include(Point2 p) => Include(p.ToPoint3(IsEmpty ? 0 : Min.Z));

    public BoundingBox Union(BoundingBox o)
    {
        if (o.IsEmpty) return this;
        if (IsEmpty) return o;
        return Include(o.Min).Include(o.Max);
    }

    public BoundingBox Inflate(double d) => IsEmpty ? this : new(
        new Point3(Min.X - d, Min.Y - d, Min.Z), new Point3(Max.X + d, Max.Y + d, Max.Z));

    public bool Contains(Point2 p) => !IsEmpty && p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y;

    public bool ContainsXY(BoundingBox o) => !IsEmpty && !o.IsEmpty &&
        o.Min.X >= Min.X && o.Max.X <= Max.X && o.Min.Y >= Min.Y && o.Max.Y <= Max.Y;

    public bool IntersectsXY(BoundingBox o) => !IsEmpty && !o.IsEmpty &&
        o.Min.X <= Max.X && o.Max.X >= Min.X && o.Min.Y <= Max.Y && o.Max.Y >= Min.Y;

    public static BoundingBox FromPoints(IEnumerable<Point3> pts)
    {
        var b = Empty;
        foreach (var p in pts) b = b.Include(p);
        return b;
    }

    public static BoundingBox FromCorners(Point2 a, Point2 b) =>
        Empty.Include(a.ToPoint3()).Include(b.ToPoint3());
}
