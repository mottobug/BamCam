using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;

namespace BamCam.App.Drawing;

/// <summary>
/// A large toolpath drawn into a bitmap of its own, for the same reason as a large mesh (<see cref="SurfaceRaster"/>):
/// repaints that do not change the view, such as a mouse move with object snap, cost nothing, and the others little.
/// Lines have the set width in whole pixels and rapids are solid here, unlike the vector drawing of smaller toolpaths.
/// </summary>
public sealed class ToolpathRaster
{
    /// <summary>From this number of moves a toolpath is drawn into a bitmap.</summary>
    public const int LargeMoveCount = 20000;

    private readonly record struct Key(int Width, int Height, double Scale, Point Pan, double Yaw, double Pitch, double Scaling, int Cut, int Arc, int Rapid, bool ShowRapids, double Tolerance, int Thickness);

    private readonly PixelLayer _layer = new();
    private Key? _key;
    private double _segmentTolerance = double.NaN;
    private Point3[] _from = [];
    private Point3[] _to = [];
    private MoveKind[] _kinds = [];

    /// <summary>Line width in pixels for a width in view units on this display.</summary>
    public static int Thickness(double lineWidth, double scaling) => Math.Clamp((int)Math.Round(lineWidth * scaling), 1, 5);

    /// <summary>Draws the segments, rapids first, into <paramref name="pixels"/> (cleared first).</summary>
    public static void Rasterize(Point3[] from, Point3[] to, MoveKind[] kinds, ViewTransform view, double scaling, int width, int height,
        int cutColour, int arcColour, int rapidColour, bool showRapids, int[] pixels, int thickness = 1)
    {
        Array.Clear(pixels);
        void Pass(Func<MoveKind, bool> take, int colour) => Parallel.ForEach(Partitioner.Create(0, kinds.Length), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                if (!take(kinds[i])) continue;
                var a = view.ToScreen(from[i]);
                var b = view.ToScreen(to[i]);
                float x0 = (float)(a.X * scaling), y0 = (float)(a.Y * scaling), x1 = (float)(b.X * scaling), y1 = (float)(b.Y * scaling);
                // Wider lines: copies beside the line, across its main direction.
                var alongX = MathF.Abs(x1 - x0) >= MathF.Abs(y1 - y0);
                for (var k = 0; k < thickness; k++)
                {
                    var offset = k - (thickness - 1) / 2f;
                    float ox = alongX ? 0 : offset, oy = alongX ? offset : 0;
                    PixelLayer.DrawLine(pixels, width, height, colour, x0 + ox, y0 + oy, x1 + ox, y1 + oy);
                }
            }
        });
        if (showRapids) Pass(k => k == MoveKind.Rapid, rapidColour);
        Pass(k => k == MoveKind.Feed, cutColour);
        Pass(k => k is MoveKind.ArcCW or MoveKind.ArcCCW, arcColour);
    }

    /// <summary>Shows the toolpath; it is only drawn again when something it depends on changed.</summary>
    public void Draw(DrawingContext dc, Toolpath toolpath, ViewTransform view, Size size, double scaling, double tolerance,
        Color cut, Color arc, Color rapid, bool showRapids, double lineWidth)
    {
        var thickness = Thickness(lineWidth, scaling);
        var width = Math.Max((int)Math.Ceiling(size.Width * scaling), 1);
        var height = Math.Max((int)Math.Ceiling(size.Height * scaling), 1);
        int cutPixel = PixelLayer.Pixel(cut), arcPixel = PixelLayer.Pixel(arc), rapidPixel = PixelLayer.Pixel(rapid);
        var key = new Key(width, height, view.Scale, view.Pan, view.Yaw, view.Pitch, scaling, cutPixel, arcPixel, rapidPixel, showRapids, tolerance, thickness);
        if (_key != key)
        {
            // The arcs are split according to the zoom: panning and rotating keep the segments.
            if (_segmentTolerance != tolerance)
            {
                var segments = toolpath.DisplaySegments(tolerance).ToList();
                _from = segments.Select(s => s.From).ToArray();
                _to = segments.Select(s => s.To).ToArray();
                _kinds = segments.Select(s => s.Kind).ToArray();
                _segmentTolerance = tolerance;
            }
            _layer.Resize(width, height);
            Rasterize(_from, _to, _kinds, view, scaling, width, height, cutPixel, arcPixel, rapidPixel, showRapids, _layer.Pixels, thickness);
            _layer.MarkChanged();
            _key = key;
        }
        _layer.Draw(dc, scaling);
    }
}
