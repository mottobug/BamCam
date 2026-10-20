using System.ComponentModel;
using System.Xml.Linq;
using BamCam.Core.Geometry;

namespace BamCam.Core.Model;

/// <summary>RGB colour as stored in project files ("r,g,b").</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor White = new(255, 255, 255);
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor DefaultLayer = new(127, 255, 0);

    public override string ToString() => $"{R},{G},{B}";
}

/// <summary>Base class of all drawing objects.</summary>
public abstract class Entity
{
    [Category("General"), Description("Unique object identifier.")]
    public int Id { get; set; }

    [Category("General"), Description("Optional user tag.")]
    public string? Tag { get; set; }

    /// <summary>Object transform applied on top of the stored geometry.</summary>
    [Browsable(false)]
    public Matrix4 Transform { get; set; } = Matrix4.Identity;

    [Browsable(false)]
    public Layer? Layer { get; internal set; }

    /// <summary>Unknown XML children preserved for lossless round-tripping.</summary>
    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    [Browsable(false)]
    public abstract string TypeName { get; }

    /// <summary>Geometry as paths in world coordinates (transform applied).</summary>
    public abstract IEnumerable<Path2> ToPaths();

    public virtual BoundingBox Bounds()
    {
        var b = BoundingBox.Empty;
        foreach (var p in ToPaths()) b = b.Union(p.Bounds());
        return b;
    }

    /// <summary>Moves the transform into the stored geometry where the entity type allows it.</summary>
    public abstract void ApplyTransform(Matrix4 m);

    public abstract Entity CloneEntity();

    protected T CopyBase<T>(T e) where T : Entity
    {
        e.Tag = Tag;
        e.Transform = Transform;
        e.Extensions.AddRange(Extensions.Select(x => new XElement(x)));
        return e;
    }

    public override string ToString() => $"{TypeName} ({Id})";
}

public sealed class PolylineEntity : Entity
{
    public override string TypeName => "Polyline";

    [Category("Polyline"), Description("Whether the last point connects back to the first.")]
    public bool Closed { get; set; }

    [Category("Polyline"), Description("Vertices: X, Y, Z and the bulge of the segment to the next point. Click [...] to edit.")]
    public List<Vertex> Points { get; } = [];

    public PolylineEntity() { }

    public PolylineEntity(Path2 path)
    {
        Closed = path.Closed;
        Points.AddRange(path.Vertices);
    }

    public Path2 LocalPath => new(Points, Closed);

    public override IEnumerable<Path2> ToPaths()
    {
        yield return Transform.IsIdentity ? LocalPath : LocalPath.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var p = LocalPath.Transformed(Transform * m);
        Points.Clear();
        Points.AddRange(p.Vertices);
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity()
    {
        var e = CopyBase(new PolylineEntity { Closed = Closed });
        e.Points.AddRange(Points);
        return e;
    }
}

public sealed class LineEntity : Entity
{
    public override string TypeName => "Line";

    [Category("Line"), Description("Points of the line. Click [...] to edit.")]
    public List<Point3> Points { get; } = [];

    public override IEnumerable<Path2> ToPaths()
    {
        var p = new Path2(Points.Select(pt => new Vertex(pt)), false);
        yield return Transform.IsIdentity ? p : p.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        for (var i = 0; i < Points.Count; i++) Points[i] = t.Transform(Points[i]);
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity()
    {
        var e = CopyBase(new LineEntity());
        e.Points.AddRange(Points);
        return e;
    }
}

public sealed class CircleEntity : Entity
{
    public override string TypeName => "Circle";

    [Category("Circle")]
    public Point3 Center { get; set; }

    [Category("Circle")]
    public double Diameter { get; set; }

    public double Radius => Diameter / 2;

    public override IEnumerable<Path2> ToPaths()
    {
        // Two half-circle arcs (bulge 1) starting at angle 0.
        var c = Center;
        var p = new Path2([
            new Vertex(new Point3(c.X + Radius, c.Y, c.Z), 1),
            new Vertex(new Point3(c.X - Radius, c.Y, c.Z), 1),
        ], true);
        yield return Transform.IsIdentity ? p : p.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        Center = t.Transform(Center);
        Diameter *= t.ScaleXY;
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity() => CopyBase(new CircleEntity { Center = Center, Diameter = Diameter });
}

public sealed class ArcEntity : Entity
{
    public override string TypeName => "Arc";

    [Category("Arc")]
    public Point3 Center { get; set; }

    [Category("Arc")]
    public double Radius { get; set; }

    [Category("Arc"), Description("Start angle in degrees (counter-clockwise from +X).")]
    public double StartAngle { get; set; }

    [Category("Arc"), Description("Sweep in degrees; positive = counter-clockwise.")]
    public double Sweep { get; set; }

    public Arc2 Arc => new(Center.XY, Radius, Angles.ToRadians(StartAngle), Angles.ToRadians(Sweep));

    public override IEnumerable<Path2> ToPaths()
    {
        var a = Arc;
        // Bulge is limited to < 180° per segment for robustness, so split large arcs.
        var parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(a.Sweep) / Math.PI - 1e-9));
        var verts = new List<Vertex>();
        for (var i = 0; i < parts; i++)
        {
            var sub = new Arc2(a.Center, a.Radius, a.StartAngle + a.Sweep * i / parts, a.Sweep / parts);
            verts.Add(new Vertex(sub.Start.ToPoint3(Center.Z), sub.ToBulge()));
        }
        verts.Add(new Vertex(a.End.ToPoint3(Center.Z)));
        var p = new Path2(verts, false);
        yield return Transform.IsIdentity ? p : p.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        var a = Arc;
        var s = t.Transform(a.Start);
        var c = t.Transform(a.Center);
        var mirrored = t.DeterminantXY < 0;
        Center = t.Transform(Center);
        Radius *= t.ScaleXY;
        StartAngle = Angles.ToDegrees((s - c).Angle);
        if (mirrored) Sweep = -Sweep;
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity() =>
        CopyBase(new ArcEntity { Center = Center, Radius = Radius, StartAngle = StartAngle, Sweep = Sweep });
}

public sealed class RectEntity : Entity
{
    public override string TypeName => "Rectangle";

    [Category("Rectangle"), DisplayName("Lower Left")]
    public Point3 LowerLeft { get; set; }

    [Category("Rectangle")]
    public double Width { get; set; }

    [Category("Rectangle")]
    public double Height { get; set; }

    [Category("Rectangle"), DisplayName("Corner Radius")]
    public double CornerRadius { get; set; }

    public static RectEntity FromCorners(Point2 a, Point2 b, double cornerRadius = 0) => new()
    {
        LowerLeft = new Point3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), 0),
        Width = Math.Abs(a.X - b.X),
        Height = Math.Abs(a.Y - b.Y),
        CornerRadius = cornerRadius,
    };

    public Path2 LocalPath
    {
        get
        {
            var (x, y, z) = (LowerLeft.X, LowerLeft.Y, LowerLeft.Z);
            var r = Math.Clamp(CornerRadius, 0, Math.Min(Width, Height) / 2);
            if (r <= Tolerance.Point)
            {
                return new Path2([
                    new Vertex(new Point3(x, y, z)), new Vertex(new Point3(x + Width, y, z)),
                    new Vertex(new Point3(x + Width, y + Height, z)), new Vertex(new Point3(x, y + Height, z)),
                ], true);
            }
            // Counter-clockwise with quarter-circle corners (bulge tan(22.5°)).
            var b = Math.Tan(Math.PI / 8);
            return new Path2([
                new Vertex(new Point3(x + r, y, z)),
                new Vertex(new Point3(x + Width - r, y, z), b),
                new Vertex(new Point3(x + Width, y + r, z)),
                new Vertex(new Point3(x + Width, y + Height - r, z), b),
                new Vertex(new Point3(x + Width - r, y + Height, z)),
                new Vertex(new Point3(x + r, y + Height, z), b),
                new Vertex(new Point3(x, y + Height - r, z)),
                new Vertex(new Point3(x, y + r, z), b),
            ], true);
        }
    }

    public override IEnumerable<Path2> ToPaths()
    {
        yield return Transform.IsIdentity ? LocalPath : LocalPath.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        // Rectangles stay axis-aligned only under translation / scale; otherwise keep the matrix.
        if (Math.Abs(t.M12) < 1e-12 && Math.Abs(t.M21) < 1e-12 && t.M11 > 0 && t.M22 > 0)
        {
            LowerLeft = t.Transform(LowerLeft);
            Width *= t.M11;
            Height *= t.M22;
            CornerRadius *= Math.Min(t.M11, t.M22);
            Transform = Matrix4.Identity;
        }
        else
        {
            Transform = t;
        }
    }

    public override Entity CloneEntity() => CopyBase(new RectEntity
    {
        LowerLeft = LowerLeft, Width = Width, Height = Height, CornerRadius = CornerRadius,
    });
}

public sealed class PointListEntity : Entity
{
    public override string TypeName => "Point List";

    [Category("Point List"), Description("Points of the list (e.g. drill positions). Click [...] to edit.")]
    public List<Point3> Points { get; } = [];

    public IEnumerable<Point3> WorldPoints() => Transform.IsIdentity ? Points : Points.Select(Transform.Transform);

    public override IEnumerable<Path2> ToPaths() =>
        WorldPoints().Select(p => new Path2([new Vertex(p)], false));

    public override BoundingBox Bounds() => BoundingBox.FromPoints(WorldPoints());

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        for (var i = 0; i < Points.Count; i++) Points[i] = t.Transform(Points[i]);
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity()
    {
        var e = CopyBase(new PointListEntity());
        e.Points.AddRange(Points);
        return e;
    }
}

/// <summary>Closed area with optional holes.</summary>
public sealed class RegionEntity : Entity
{
    public override string TypeName => "Region";

    [Browsable(false)]
    public PolylineEntity Outer { get; set; } = new() { Closed = true };

    [Browsable(false)]
    public List<PolylineEntity> Holes { get; } = [];

    public override IEnumerable<Path2> ToPaths()
    {
        foreach (var p in Outer.ToPaths()) yield return Transform.IsIdentity ? p : p.Transformed(Transform);
        foreach (var h in Holes)
        foreach (var p in h.ToPaths()) yield return Transform.IsIdentity ? p : p.Transformed(Transform);
    }

    public override void ApplyTransform(Matrix4 m)
    {
        var t = Transform * m;
        Outer.ApplyTransform(t);
        foreach (var h in Holes) h.ApplyTransform(t);
        Transform = Matrix4.Identity;
    }

    public override Entity CloneEntity()
    {
        var e = CopyBase(new RegionEntity { Outer = (PolylineEntity)Outer.CloneEntity() });
        e.Holes.AddRange(Holes.Select(h => (PolylineEntity)h.CloneEntity()));
        return e;
    }
}

public enum TextAlignment { Left, Center, Right }

public enum TextVerticalAlignment { Top, Center, Bottom }

public sealed class TextEntity : Entity
{
    public override string TypeName => "Text";

    [Category("Text")]
    public string Text { get; set; } = "Text";

    [Category("Text")]
    public Point3 Position { get; set; }

    [Category("Text")]
    public double Height { get; set; } = 10;

    [Category("Text")]
    public string Font { get; set; } = "Arial";

    [Category("Text")]
    public bool Bold { get; set; }

    [Category("Text")]
    public bool Italic { get; set; }

    [Category("Text"), DisplayName("Text Alignment Horizontal")]
    public TextAlignment HorizontalAlignment { get; set; }

    [Category("Text"), DisplayName("Text Alignment Vertical")]
    public TextVerticalAlignment VerticalAlignment { get; set; } = TextVerticalAlignment.Bottom;

    /// <summary>Second text point from files (meaning unknown; preserved verbatim).</summary>
    [Browsable(false)]
    public string? P2 { get; set; }

    /// <summary>Outlines produced by a font renderer; filled in by the UI layer (fonts are platform services).</summary>
    [Browsable(false)]
    public List<Path2> Outlines { get; } = [];

    public static Func<TextEntity, IEnumerable<Path2>>? OutlineProvider { get; set; }

    public override IEnumerable<Path2> ToPaths()
    {
        if (Outlines.Count == 0 && OutlineProvider != null) Outlines.AddRange(OutlineProvider(this));
        return Transform.IsIdentity ? Outlines : Outlines.Select(o => o.Transformed(Transform));
    }

    public override BoundingBox Bounds()
    {
        var b = base.Bounds();
        if (!b.IsEmpty) return b;
        var p = Transform.Transform(Position);
        return BoundingBox.FromCorners(p.XY, p.XY + new Point2(Height * 0.6 * Text.Length, Height));
    }

    public void InvalidateOutlines() => Outlines.Clear();

    public override void ApplyTransform(Matrix4 m) => Transform *= m;

    public override Entity CloneEntity() => CopyBase(new TextEntity
    {
        Text = Text, Position = Position, Height = Height, Font = Font, Bold = Bold, Italic = Italic,
        HorizontalAlignment = HorizontalAlignment,
    });
}

/// <summary>Triangle mesh (from STL import or 3D operations).</summary>
public sealed class SurfaceEntity : Entity
{
    public override string TypeName => "Surface";

    [Browsable(false)]
    public List<Point3> Vertices { get; } = [];

    /// <summary>Triangle vertex indices, three per face.</summary>
    [Browsable(false)]
    public List<int> Faces { get; } = [];

    public IEnumerable<Point3> WorldVertices() => Transform.IsIdentity ? Vertices : Vertices.Select(Transform.Transform);

    public override IEnumerable<Path2> ToPaths()
    {
        var v = WorldVertices().ToList();
        for (var i = 0; i + 2 < Faces.Count; i += 3)
            yield return new Path2([new Vertex(v[Faces[i]]), new Vertex(v[Faces[i + 1]]), new Vertex(v[Faces[i + 2]])], true);
    }

    public override BoundingBox Bounds() => BoundingBox.FromPoints(WorldVertices());

    public override void ApplyTransform(Matrix4 m) => Transform *= m;

    public override Entity CloneEntity()
    {
        var e = CopyBase(new SurfaceEntity());
        e.Vertices.AddRange(Vertices);
        e.Faces.AddRange(Faces);
        return e;
    }
}

/// <summary>An element type this version doesn't understand; kept verbatim for round-tripping.</summary>
public sealed class UnknownEntity(XElement xml) : Entity
{
    public XElement Xml { get; } = xml;
    public override string TypeName => Xml.Name.LocalName;
    public override IEnumerable<Path2> ToPaths() => [];
    public override void ApplyTransform(Matrix4 m) { }
    public override Entity CloneEntity() => CopyBase(new UnknownEntity(new XElement(Xml)));
}
