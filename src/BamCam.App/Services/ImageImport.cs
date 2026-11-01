using System.ComponentModel;
using SkiaSharp;

namespace BamCam.App.Services;

/// <summary>Settings of File ▸ Import Image ▸ As Height Map.</summary>
public sealed class HeightMapOptions
{
    [Category("Size"), Description("Width of the image in the drawing; the height follows the aspect ratio.")]
    public double Width { get; set; } = 100;

    [Category("Height"), DisplayName("Top Z"), Description("Height of white (the stock surface).")]
    public double TopZ { get; set; }

    [Category("Height"), Description("Height difference between white and black.")]
    public double Depth { get; set; } = 3;

    [Category("Height"), Description("Black is high and white low.")]
    public bool Invert { get; set; }

    public override string ToString() => "Height Map";

    [Category("Size"), Description("Distance between the sample points of the surface (smaller = finer, slower).")]
    public double Resolution { get; set; } = 0.5;
}

/// <summary>Settings of File ▸ Import Image ▸ Trace Outlines.</summary>
public sealed class TraceOptions
{
    [Category("Size"), Description("Width of the image in the drawing; the height follows the aspect ratio.")]
    public double Width { get; set; } = 100;

    [Category("Trace"), Description("Brightness limit between 0 (black) and 1 (white): darker areas are traced.")]
    public double Threshold { get; set; } = 0.5;

    [Category("Trace"), Description("Trace the light areas instead of the dark ones.")]
    public bool Invert { get; set; }

    [Category("Trace"), Description("Allowed deviation from the pixel outline in pixels (larger = fewer points).")]
    public double Smoothing { get; set; } = 0.5;

    [Category("Trace"), DisplayName("As Splines"), Description("Create smooth splines through the outline points instead of polylines.")]
    public bool AsSplines { get; set; }

    public override string ToString() => "Trace Outlines";
}

/// <summary>Reads raster images (PNG, JPEG, BMP, GIF, WebP) as brightness values.</summary>
public static class ImageImport
{
    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"];

    /// <summary>
    /// Brightness 0 (black) … 1 (white) per pixel, <c>[x, y]</c> with row 0 at the top. Transparent pixels count as
    /// white. Images larger than <paramref name="maxSide"/> pixels are scaled down.
    /// </summary>
    public static float[,] LoadGray(string path, int maxSide = 2000)
    {
        using var original = SKBitmap.Decode(path) ?? throw new InvalidDataException("Unsupported or damaged image file.");
        var bitmap = original;
        if (Math.Max(original.Width, original.Height) > maxSide)
        {
            var f = (double)maxSide / Math.Max(original.Width, original.Height);
            bitmap = original.Resize(new SKImageInfo((int)(original.Width * f), (int)(original.Height * f)), SKFilterQuality.Medium) ?? original;
        }
        try
        {
            var gray = new float[bitmap.Width, bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                var lum = (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;
                var alpha = c.Alpha / 255f;
                gray[x, y] = lum * alpha + (1 - alpha);
            }
            return gray;
        }
        finally
        {
            if (!ReferenceEquals(bitmap, original)) bitmap.Dispose();
        }
    }
}
