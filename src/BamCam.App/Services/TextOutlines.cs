using BamCam.Core.Geometry;
using BamCam.Core.Model;
using SkiaSharp;

namespace BamCam.App.Services;

/// <summary>Builds TrueType glyph outlines for text entities (closed polylines in drawing units).</summary>
public static class TextOutlines
{
    public static string DefaultFont { get; set; } = "Arial";

    public static IEnumerable<Path2> Build(TextEntity t)
    {
        var result = new List<Path2>();
        if (string.IsNullOrEmpty(t.Text) || t.Height <= 0) return result;

        var family = string.IsNullOrWhiteSpace(t.Font) ? DefaultFont : t.Font;
        using var typeface = SKTypeface.FromFamilyName(family,
            t.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            t.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        // Work at a fixed size for good flattening precision, then scale to the requested height.
        const float workSize = 200f;
        using var font = new SKPaint { Typeface = typeface, TextSize = workSize, IsAntialias = false };
        var scale = t.Height / workSize;
        var lines = t.Text.Replace("\r\n", "\n").Split('\n');
        var lineHeight = font.FontSpacing;
        var tol = Math.Max(0.5, workSize / 400.0);

        var perLine = new List<(List<List<Point2>> Contours, float Width)>();
        foreach (var line in lines)
        {
            using var path = font.GetTextPath(line, 0, 0);
            var width = font.MeasureText(line);
            perLine.Add((Contours(path, tol), width));
        }
        var maxWidth = perLine.Count == 0 ? 0 : perLine.Max(l => l.Width);

        // Vertical alignment relative to the insertion point (bottom = first line's baseline).
        var totalHeight = lineHeight * (lines.Length - 1);
        var metrics = font.FontMetrics;
        double yShift = t.VerticalAlignment switch
        {
            TextVerticalAlignment.Top => metrics.Ascent,                  // ascent is negative in Skia
            TextVerticalAlignment.Center => (metrics.Ascent + totalHeight) / 2,
            _ => totalHeight,
        };

        for (var i = 0; i < perLine.Count; i++)
        {
            var (contours, width) = perLine[i];
            double xShift = t.HorizontalAlignment switch
            {
                TextAlignment.Center => -width / 2,
                TextAlignment.Right => -width,
                _ => 0,
            };
            _ = maxWidth;
            var y0 = i * lineHeight - yShift;
            foreach (var c in contours)
            {
                if (c.Count < 3) continue;
                var pts = c.Select(p => new Point2(
                    t.Position.X + (p.X + xShift) * scale,
                    t.Position.Y - (p.Y + y0) * scale)).ToList();
                result.Add(Path2.FromPoints(pts, closed: true, t.Position.Z));
            }
        }
        return result;
    }

    private static List<List<Point2>> Contours(SKPath path, double tol)
    {
        var contours = new List<List<Point2>>();
        List<Point2>? current = null;
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    current = [new Point2(pts[0].X, pts[0].Y)];
                    contours.Add(current);
                    break;
                case SKPathVerb.Line:
                    current?.Add(new Point2(pts[1].X, pts[1].Y));
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    if (current != null) Quad(current, pts[0], pts[1], pts[2], tol);
                    break;
                case SKPathVerb.Cubic:
                    if (current != null) Cubic(current, pts[0], pts[1], pts[2], pts[3], tol);
                    break;
                case SKPathVerb.Close:
                    if (current is { Count: > 1 } && current[0].AlmostEquals(current[^1], 1e-6)) current.RemoveAt(current.Count - 1);
                    current = null;
                    break;
            }
        }
        return contours;
    }

    private static int Steps(double length, double tol) => Math.Clamp((int)Math.Ceiling(Math.Sqrt(length / tol)), 2, 32);

    private static void Quad(List<Point2> c, SKPoint a, SKPoint b, SKPoint d, double tol)
    {
        var n = Steps(Dist(a, b) + Dist(b, d), tol);
        for (var i = 1; i <= n; i++)
        {
            var t = (double)i / n;
            var u = 1 - t;
            c.Add(new Point2(u * u * a.X + 2 * u * t * b.X + t * t * d.X, u * u * a.Y + 2 * u * t * b.Y + t * t * d.Y));
        }
    }

    private static void Cubic(List<Point2> c, SKPoint a, SKPoint b, SKPoint d, SKPoint e, double tol)
    {
        var n = Steps(Dist(a, b) + Dist(b, d) + Dist(d, e), tol);
        for (var i = 1; i <= n; i++)
        {
            var t = (double)i / n;
            var u = 1 - t;
            c.Add(new Point2(
                u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * d.X + t * t * t * e.X,
                u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * d.Y + t * t * t * e.Y));
        }
    }

    private static double Dist(SKPoint a, SKPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
