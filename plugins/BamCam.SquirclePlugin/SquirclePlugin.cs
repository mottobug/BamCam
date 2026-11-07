// Idea, formulas and parameters: GeoffreyGRoy, author of the Squircle Pro plugin for CamBam.
// Original: http://www.atelier-des-fougeres.fr/Cambam/Aide/Plugins/SquirclePlugin.html
// Written for BamCam from the documentation of that plugin; it contains no code of it.

using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace Squircle;

/// <summary>Draw ▸ Squircle: inserts a squircle, whose values are then changed in the properties.</summary>
public sealed class SquirclePlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddEntityType<SquircleEntity>();
        host.AddCommand(new PluginCommand
        {
            Header = "S_quircle",
            Menu = "Draw",
            Description = german
                ? "Squircle und Superellipse: Form und Werte im Eigenschaftenfenster wählen"
                : "Squircle and superellipse: choose the shape and values in the properties",
            Execute = context =>
            {
                context.AddEntities(german ? "Squircle zeichnen" : "Draw squircle", [new SquircleEntity()]);
                return Task.CompletedTask;
            },
        });
    }
}

/// <summary>Which formula makes the shape.</summary>
public enum SquircleShape { Simple, Complex }

/// <summary>What sets the number of straight segments.</summary>
public enum SquircleAccuracy { Angle, EdgeLength }

/// <summary>
/// A closed polyline from one of two formulas in polar coordinates, around the origin given by Orig-X and Orig-Y:
/// <list type="bullet">
/// <item>Simple: |(x - a) / ra|^n1 + |(y - b) / rb|^n1 = 1. n1 = 2 gives a circle or ellipse, more a rounded rectangle,
/// 1 to 2 a rounded diamond, below 1 a concave diamond.</item>
/// <item>Complex: r(t) = (|cos(m t / 4)|^n2 / a + |sin(m t / 4)|^n3 / b)^(-n1), scaled to the radii.</item>
/// </list>
/// </summary>
public sealed class SquircleEntity : ParametricEntity
{
    /// <summary>From this number of points the outline gets a note, and above the limit it is cut down to it.</summary>
    public const int ManyPoints = 4000, MostPoints = 20000;

    /// <summary>Samples for measuring a shape: its extents and its length, independent of the chosen accuracy.</summary>
    private const int MeasureSamples = 3600;

    private const double Smallest = 1e-3;

    public override string ParametricType => "bamcam.squircle";
    public override string TypeName => "Squircle";

    [Category("Squircle"), DisplayName("Shape Type"), Description("Simple: squircle or superellipse from n1. Complex: a wide variety of shapes from m, n1, n2, n3, a and b.")]
    public SquircleShape ShapeType { get; set; }

    [Category("Squircle"), DisplayName("m"), Description("Fold parameter, greater than 0: sets the basic outline (3 sided, 4 sided and so on).")]
    public double M { get; set; } = 4;

    [Category("Squircle"), DisplayName("n1"), Description("Exponent. Simple: 2 = circle or ellipse, above 2 = rounded rectangle, 1 to 2 = rounded diamond, below 1 = concave diamond.")]
    public double N1 { get; set; } = 4;

    [Category("Squircle"), DisplayName("n2"), Description("Exponent of the cosine part.")]
    public double N2 { get; set; } = 4;

    [Category("Squircle"), DisplayName("n3"), Description("Exponent of the sine part.")]
    public double N3 { get; set; } = 2;

    [Category("Squircle"), DisplayName("a"), Description("Shape parameter of the cosine part.")]
    public double A { get; set; } = 1;

    [Category("Squircle"), DisplayName("b"), Description("Shape parameter of the sine part.")]
    public double B { get; set; } = 1.2;

    [Category("Squircle"), DisplayName("Rad-X"), Description("Half width: the distance from the origin to the shape along X.")]
    public double RadX { get; set; } = 50;

    [Category("Squircle"), DisplayName("Rad-Y"), Description("Half height: the distance from the origin to the shape along Y.")]
    public double RadY { get; set; } = 50;

    [Category("Squircle"), DisplayName("Orig-X"), Description("X position of the centre of the shape.")]
    public double OrigX { get; set; }

    [Category("Squircle"), DisplayName("Orig-Y"), Description("Y position of the centre of the shape.")]
    public double OrigY { get; set; }

    [Category("Accuracy"), DisplayName("Accuracy"), Description("Sets the straight segments by their angle at the centre or by their length. Large values give polygon-like shapes.")]
    public SquircleAccuracy Accuracy { get; set; }

    [Category("Accuracy"), DisplayName("Angle"), Description("Average angle of a segment at the centre, in degrees. Below 1 gives a smooth curve.")]
    public double Angle { get; set; } = 1;

    [Category("Accuracy"), DisplayName("Edge Length"), Description("Average length of a segment, in drawing units. Below 1 mm gives a smooth curve.")]
    public double EdgeLength { get; set; } = 0.87266;

    [Category("Accuracy"), DisplayName("No of Points"), Description("Number of straight segments of the outline.")]
    public int NumberOfPoints => Math.Min(RequestedPoints(), MostPoints);

    [Category("Accuracy"), DisplayName("Note")]
    public string Note
    {
        get
        {
            var points = RequestedPoints();
            if (points > MostPoints) return $"{points} points asked for: limited to {MostPoints}.";
            return points > ManyPoints ? $"Many points ({points}): the object gets slow." : "";
        }
    }

    public override IReadOnlySet<string> HiddenParameters
    {
        get
        {
            var hidden = new HashSet<string> { Accuracy == SquircleAccuracy.Angle ? nameof(EdgeLength) : nameof(Angle) };
            if (ShapeType == SquircleShape.Simple) hidden.UnionWith([nameof(M), nameof(N2), nameof(N3), nameof(A), nameof(B)]);
            if (Note.Length == 0) hidden.Add(nameof(Note));
            return hidden;
        }
    }

    private double Rx => Math.Max(RadX, Smallest);
    private double Ry => Math.Max(RadY, Smallest);

    /// <summary>A point of the simple shape, at an angle over the whole curve.</summary>
    private Point2 SimplePoint(double t)
    {
        var exponent = 2 / Math.Max(N1, 0.05);
        double Curve(double value) => Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);
        return new Point2(Rx * Curve(Math.Cos(t)), Ry * Curve(Math.Sin(t)));
    }

    /// <summary>A point of the complex shape before scaling, or null where the formula has no value.</summary>
    private Point2? ComplexPoint(double t)
    {
        var angle = Math.Max(M, Smallest) * t / 4;
        var sum = Math.Pow(Math.Abs(Math.Cos(angle)), N2) / Math.Max(A, Smallest) + Math.Pow(Math.Abs(Math.Sin(angle)), N3) / Math.Max(B, Smallest);
        var r = Math.Pow(sum, -N1);
        return double.IsFinite(r) ? new Point2(r * Math.Cos(t), r * Math.Sin(t)) : null;
    }

    /// <summary>Scale that brings a complex shape to the radii: its largest distances from the origin along X and Y.</summary>
    private (double X, double Y) ComplexScale()
    {
        double maxX = 0, maxY = 0;
        for (var i = 0; i < MeasureSamples; i++)
            if (ComplexPoint(2 * Math.PI * i / MeasureSamples) is { } p)
            {
                maxX = Math.Max(maxX, Math.Abs(p.X));
                maxY = Math.Max(maxY, Math.Abs(p.Y));
            }
        return (maxX > 0 ? Rx / maxX : 1, maxY > 0 ? Ry / maxY : 1);
    }

    private Point2? PointAt(double t, (double X, double Y) scale)
    {
        if (ShapeType == SquircleShape.Simple) return SimplePoint(t);
        return ComplexPoint(t) is { } p ? new Point2(p.X * scale.X, p.Y * scale.Y) : null;
    }

    private int RequestedPoints()
    {
        double wanted;
        if (Accuracy == SquircleAccuracy.Angle)
        {
            wanted = 360 / Math.Max(Angle, Smallest);
        }
        else
        {
            var scale = ShapeType == SquircleShape.Complex ? ComplexScale() : (1, 1);
            var length = 0.0;
            Point2? previous = null, first = null;
            for (var i = 0; i < MeasureSamples; i++)
            {
                if (PointAt(2 * Math.PI * i / MeasureSamples, scale) is not { } p) continue;
                if (previous is { } q) length += Point2.Distance(q, p);
                first ??= p;
                previous = p;
            }
            if (previous is { } last && first is { } start) length += Point2.Distance(last, start);
            wanted = length / Math.Max(EdgeLength, Smallest);
        }
        return (int)Math.Clamp(Math.Ceiling(wanted - 1e-9), 3, int.MaxValue / 2);
    }

    protected override IEnumerable<Path2>? Generate()
    {
        var count = NumberOfPoints;
        var scale = ShapeType == SquircleShape.Complex ? ComplexScale() : (X: 1.0, Y: 1.0);
        var origin = new Point2(OrigX, OrigY);
        var points = new List<Point2>(count);
        for (var i = 0; i < count; i++)
            if (PointAt(2 * Math.PI * i / count, scale) is { } p)
                points.Add(origin + p);
        return points.Count >= 3 ? [Path2.FromPoints(points, closed: true)] : null;
    }
}
