using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BamCam.App.Drawing;

/// <summary>
/// A bitmap for geometry too large for vector lines: pixels are drawn on worker threads, and the bitmap is shown again
/// without any calculation as long as nothing it depends on changed.
/// </summary>
public sealed class PixelLayer
{
    private WriteableBitmap? _bitmap;
    private bool _uploaded;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int[] Pixels { get; private set; } = [];

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        if (Pixels.Length != width * height) Pixels = new int[width * height];
    }

    /// <summary>The pixels changed: the bitmap has to take them over before it is shown again.</summary>
    public void MarkChanged() => _uploaded = false;

    public void Draw(DrawingContext dc, double scaling)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != Width || _bitmap.PixelSize.Height != Height)
        {
            _bitmap?.Dispose();
            // 96 dpi whatever the display: the bitmap's size then equals its pixels, which is what DrawImage reads as the
            // source area. With the display's dpi it would show only a part of the bitmap, enlarged.
            _bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _uploaded = false;
        }
        if (!_uploaded)
        {
            using var frame = _bitmap.Lock();
            if (frame.RowBytes == Width * 4)
            {
                Marshal.Copy(Pixels, 0, frame.Address, Width * Height);
            }
            else
            {
                for (var y = 0; y < Height; y++) Marshal.Copy(Pixels, y * Width, frame.Address + y * frame.RowBytes, Width);
            }
            _uploaded = true;
        }
        dc.DrawImage(_bitmap, new Rect(0, 0, Width / scaling, Height / scaling));
    }

    /// <summary>The colour as a premultiplied BGRA pixel.</summary>
    public static int Pixel(Color c)
    {
        int Mul(byte v) => v * c.A / 255;
        return (c.A << 24) | (Mul(c.R) << 16) | (Mul(c.G) << 8) | Mul(c.B);
    }

    /// <summary>A line clipped to the pixels. Several threads may draw at once: at worst one colour replaces another.</summary>
    public static void DrawLine(int[] pixels, int width, int height, int colour, float x0, float y0, float x1, float y1)
    {
        // Both ends in one pixel: the usual case for a large mesh in a small view.
        int ix0 = (int)MathF.Floor(x0), iy0 = (int)MathF.Floor(y0);
        if (ix0 == (int)MathF.Floor(x1) && iy0 == (int)MathF.Floor(y1))
        {
            if (ix0 >= 0 && iy0 >= 0 && ix0 < width && iy0 < height) pixels[iy0 * width + ix0] = colour;
            return;
        }
        // Clipped to the bitmap (Liang-Barsky).
        float dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
        if (!Clip(-dx, x0, ref t0, ref t1) || !Clip(dx, width - x0, ref t0, ref t1) ||
            !Clip(-dy, y0, ref t0, ref t1) || !Clip(dy, height - y0, ref t0, ref t1)) return;
        float sx = x0 + dx * t0, sy = y0 + dy * t0, ex = x0 + dx * t1, ey = y0 + dy * t1;
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(ex - sx), MathF.Abs(ey - sy)));
        if (steps <= 0) steps = 1;
        float stepX = (ex - sx) / steps, stepY = (ey - sy) / steps;
        for (var i = 0; i <= steps; i++)
        {
            int x = (int)MathF.Floor(sx + stepX * i), y = (int)MathF.Floor(sy + stepY * i);
            if (x >= 0 && y >= 0 && x < width && y < height) pixels[y * width + x] = colour;
        }
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }
}
