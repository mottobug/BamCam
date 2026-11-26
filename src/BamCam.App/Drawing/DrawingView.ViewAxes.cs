using System.Globalization;
using Avalonia;
using Avalonia.Media;
using BamCam.Core.Geometry;

namespace BamCam.App.Drawing;

/// <summary>
/// The axis indicator in the lower left corner: X, Y and Z as arrows turning with the view, so that the direction of
/// view can be seen when the axes through the origin are out of sight. A click on an arrow tip looks along that axis
/// (Z: XY plane, Y: XZ plane, X: YZ plane), a click on its centre gives the isometric view.
/// </summary>
public sealed partial class DrawingView
{
    private const double AxisIndicatorLength = 34;
    private const double AxisIndicatorMargin = 56;
    private const double AxisIndicatorHitRadius = 10;

    private static readonly (Point3 Direction, string Label, IBrush Brush, string Plane)[] IndicatorAxes =
    [
        (new Point3(1, 0, 0), "X", Brushes.Red, "YZ"),
        (new Point3(0, 1, 0), "Y", Brushes.Lime, "XZ"),
        (new Point3(0, 0, 1), "Z", Brushes.DodgerBlue, "XY"),
    ];

    private Point AxisIndicatorCentre => new(AxisIndicatorMargin, Bounds.Height - AxisIndicatorMargin);

    /// <summary>Screen end of an axis arrow and its depth (towards the viewer is positive).</summary>
    private (Point Tip, double Depth) AxisIndicatorTip(Point3 direction)
    {
        var (x, y, z) = _view.Rotate(direction);
        var c = AxisIndicatorCentre;
        return (new Point(c.X + x * AxisIndicatorLength, c.Y - y * AxisIndicatorLength), z);
    }

    private void RenderAxisIndicator(DrawingContext dc)
    {
        var c = AxisIndicatorCentre;
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), null, c, AxisIndicatorLength + 14, AxisIndicatorLength + 14);
        // Arrows pointing away from the viewer first, so that the nearer ones are drawn over them.
        foreach (var axis in IndicatorAxes.OrderBy(a => AxisIndicatorTip(a.Direction).Depth))
        {
            var (tip, depth) = AxisIndicatorTip(axis.Direction);
            var pen = new Pen(axis.Brush, 2);
            var along = tip - c;
            var length = Math.Sqrt(along.X * along.X + along.Y * along.Y);
            if (length > 4)
            {
                dc.DrawLine(pen, c, tip);
                var u = along / length;
                var n = new Vector(-u.Y, u.X);
                var head = new StreamGeometry();
                using (var ctx = head.Open())
                {
                    ctx.BeginFigure(tip + u * 2, true);
                    ctx.LineTo(tip - u * 7 + n * 4);
                    ctx.LineTo(tip - u * 7 - n * 4);
                    ctx.EndFigure(true);
                }
                dc.DrawGeometry(axis.Brush, null, head);
            }
            else
            {
                // Pointing straight at the viewer or away: a dot, filled when towards the viewer.
                dc.DrawEllipse(depth > 0 ? axis.Brush : null, pen, c, 4, 4);
            }
            var text = new FormattedText(axis.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), 12, axis.Brush);
            var labelAt = length > 4 ? tip + (tip - c) / length * 9 : c + new Vector(7, -7);
            dc.DrawText(text, new Point(labelAt.X - text.Width / 2, labelAt.Y - text.Height / 2));
        }
        dc.DrawEllipse(Brushes.Gray, null, c, 3, 3);
    }

    /// <summary>
    /// A left click on the axis indicator: the plane or "ISO" it chooses, or null when the click is elsewhere. An arrow
    /// pointing at the viewer covers the centre, so the arrow tips are tried first.
    /// </summary>
    private string? AxisIndicatorHit(Point pos)
    {
        if (!Settings.ShowAxisIndicator) return null;
        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        var c = AxisIndicatorCentre;
        string? best = null;
        var bestDistance = AxisIndicatorHitRadius;
        foreach (var axis in IndicatorAxes)
        {
            var (tip, _) = AxisIndicatorTip(axis.Direction);
            if (Distance(tip, c) < 8) continue;
            var d = Distance(tip, pos);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = axis.Plane;
            }
        }
        return best ?? (Distance(c, pos) <= 8 ? "ISO" : null);
    }
}
