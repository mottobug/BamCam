// Idea and formula: Geoff, author of the Squircle plugin for CamBam.
// Written for BamCam from his description of the formula; it contains no code of that plugin.

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
                ? "Superellipse von Kreis über abgerundetes Rechteck bis Raute; Werte im Eigenschaftenfenster ändern"
                : "Superellipse from circle to rounded rectangle or diamond; change the values in the properties",
            Execute = context =>
            {
                context.AddEntities(german ? "Squircle zeichnen" : "Draw squircle", [new SquircleEntity()]);
                return Task.CompletedTask;
            },
        });
    }
}

/// <summary>
/// A squircle (superellipse): |(x - a) / ra|^p + |(y - b) / rb|^p = 1, as a closed polyline.
/// p = 2 gives a circle or ellipse, p &gt; 2 a rounded rectangle, 1 &lt; p &lt; 2 a rounded diamond and 0 &lt; p &lt; 1 a concave diamond.
/// </summary>
public sealed class SquircleEntity : ParametricEntity
{
    /// <summary>Smallest values used for the size and the power: the formula has no shape at or below 0.</summary>
    private const double SmallestSize = 1e-3, SmallestPower = 0.05;

    public override string ParametricType => "bamcam.squircle";
    public override string TypeName => "Squircle";

    [Category("Squircle"), DisplayName("Center X")]
    public double CenterX { get; set; }

    [Category("Squircle"), DisplayName("Center Y")]
    public double CenterY { get; set; }

    [Category("Squircle"), DisplayName("Half Width"), Description("Distance from the centre to the shape along X.")]
    public double HalfWidth { get; set; } = 25;

    [Category("Squircle"), DisplayName("Half Height"), Description("Distance from the centre to the shape along Y.")]
    public double HalfHeight { get; set; } = 25;

    [Category("Squircle"), Description("2 = circle or ellipse. Above 2 = rounded rectangle, more square as it grows. Between 1 and 2 = rounded diamond. Below 1 = concave diamond.")]
    public double Power { get; set; } = 4;

    [Category("Squircle"), Description("Number of straight segments of the outline (16 to 20000).")]
    public int Segments { get; set; } = 720;

    protected override IEnumerable<Path2>? Generate()
    {
        var rx = Math.Max(HalfWidth, SmallestSize);
        var ry = Math.Max(HalfHeight, SmallestSize);
        var exponent = 2 / Math.Max(Power, SmallestPower);
        var segments = Math.Clamp(Segments, 16, 20000);

        double Curve(double value) => Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);

        // Evenly spaced parameter from the formula. The four tips of the shape are always among the points when the
        // number of segments can be divided by 4, so that the size is exact.
        var points = new List<Point2>(segments);
        for (var i = 0; i < segments; i++)
        {
            var t = 2 * Math.PI * i / segments;
            points.Add(new Point2(CenterX + rx * Curve(Math.Cos(t)), CenterY + ry * Curve(Math.Sin(t))));
        }
        return [Path2.FromPoints(points, closed: true)];
    }
}
