// Author of the original CamBam plugin "CentreLines": EddyCurrent
// Original source: https://cambamcnc.com/forum/index.php?topic=6740.0/index.php
// Ported to BamCam with the author's permission; docs/porting-cambam-plugins.md goes through the port.

using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Plugins;

namespace CentreLines;

/// <summary>Draw ▸ Centre Lines: a horizontal and a vertical centre line for the selected circle or rectangle.</summary>
public sealed class CentreLinesPlugin : IBamCamPlugin
{
    public void Initialize(IPluginHost host)
    {
        var german = host.Language == "de";
        host.AddCommand(new PluginCommand
        {
            Header = german ? "_Mittellinien" : "_Centre Lines",
            Menu = "Draw",
            Description = german ? "Mittellinien für den ausgewählten Kreis oder das ausgewählte Rechteck" : "Centre lines for the selected circle or rectangle",
            Execute = context =>
            {
                Run(context, german);
                return Task.CompletedTask;
            },
        });
    }

    private static void Run(ICommandContext context, bool german)
    {
        var lines = context.Selection.Count == 1 ? LinesFor(context.Selection[0]) : null;
        if (lines == null)
        {
            context.Log(german ? "Bitte genau einen Kreis oder ein Rechteck auswählen." : "Please select 1 circle or rectangle.");
            return;
        }
        // One undo step; the view refreshes by itself.
        context.AddEntities(german ? "Mittellinien" : "Centre lines", lines);
    }

    /// <summary>The two centre lines of a circle or rectangle, in drawing coordinates; null for other objects.</summary>
    public static IReadOnlyList<PolylineEntity>? LinesFor(Entity entity) => entity switch
    {
        CircleEntity circle => CircleLines(circle),
        RectEntity rectangle => RectangleLines(rectangle),
        _ => null,
    };

    private static List<PolylineEntity> CircleLines(CircleEntity circle)
    {
        // Center and Diameter are stored values: the object's transform (moved, scaled) still has to be applied.
        var middle = circle.Transform.Transform(circle.Center).XY;
        var radius = circle.Radius * circle.Transform.ScaleXY;
        return
        [
            Line(middle - new Point2(radius, 0), middle + new Point2(radius, 0)),
            Line(middle + new Point2(0, radius), middle - new Point2(0, radius)),
        ];
    }

    private static List<PolylineEntity> RectangleLines(RectEntity rectangle)
    {
        // Corners from the rectangle's values, not from its outline: rounded corners give more than 4 points.
        var (x, y) = (rectangle.LowerLeft.X, rectangle.LowerLeft.Y);
        var m = rectangle.Transform;
        var bottomLeft = m.Transform(new Point2(x, y));
        var bottomRight = m.Transform(new Point2(x + rectangle.Width, y));
        var topRight = m.Transform(new Point2(x + rectangle.Width, y + rectangle.Height));
        var topLeft = m.Transform(new Point2(x, y + rectangle.Height));
        return
        [
            Line(Middle(bottomLeft, bottomRight), Middle(topRight, topLeft)),
            Line(Middle(bottomRight, topRight), Middle(topLeft, bottomLeft)),
        ];
    }

    private static Point2 Middle(Point2 a, Point2 b) => Point2.Lerp(a, b, 0.5);

    private static PolylineEntity Line(Point2 from, Point2 to) => new(Path2.FromPoints([from, to], closed: false));
}
