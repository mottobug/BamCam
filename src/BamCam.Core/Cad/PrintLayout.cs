using BamCam.Core.Geometry;

namespace BamCam.Core.Cad;

public enum PaperSize { A4, A3, A2, A1, A0, Letter, Legal, Tabloid }

public enum PaperOrientation { Auto, Portrait, Landscape }

public enum PrintScale
{
    /// <summary>The drawing fills one page.</summary>
    FitToPage,
    /// <summary>True size (1:1); larger drawings are split over several pages.</summary>
    TrueSize,
    /// <summary>A given factor (e.g. 0.5 for 1:2); larger drawings are split over several pages.</summary>
    Custom,
}

/// <summary>One printed page: the drawing window it shows (drawing millimetres) and its place in the page grid.</summary>
public sealed record PrintPage(BoundingBox Window, int Row, int Column);

/// <summary>Page layout of a printout: paper, scale and the pages (tiles) the drawing needs.</summary>
public sealed record PrintLayout(double PaperWidth, double PaperHeight, double Margin, double Scale, IReadOnlyList<PrintPage> Pages)
{
    /// <summary>Printable width and height on the paper (millimetres).</summary>
    public double AreaWidth => PaperWidth - 2 * Margin;
    public double AreaHeight => PaperHeight - 2 * Margin;

    /// <summary>Paper size in millimetres, portrait.</summary>
    public static (double Width, double Height) Paper(PaperSize size) => size switch
    {
        PaperSize.A4 => (210, 297),
        PaperSize.A3 => (297, 420),
        PaperSize.A2 => (420, 594),
        PaperSize.A1 => (594, 841),
        PaperSize.A0 => (841, 1189),
        PaperSize.Letter => (215.9, 279.4),
        PaperSize.Legal => (215.9, 355.6),
        _ => (279.4, 431.8),
    };

    /// <summary>
    /// Layout for drawing extents <paramref name="extents"/> given in millimetres. <paramref name="reserved"/> is
    /// kept free at the bottom of each page for the caption. Auto orientation chooses the one that fits the
    /// drawing better (fewer pages, or a larger fit scale).
    /// </summary>
    public static PrintLayout Create(BoundingBox extents, PaperSize paper, PaperOrientation orientation, PrintScale mode,
        double factor, double margin, double reserved)
    {
        if (orientation == PaperOrientation.Auto)
        {
            var portrait = Create(extents, paper, PaperOrientation.Portrait, mode, factor, margin, reserved);
            var landscape = Create(extents, paper, PaperOrientation.Landscape, mode, factor, margin, reserved);
            if (mode == PrintScale.FitToPage) return landscape.Scale > portrait.Scale + 1e-12 ? landscape : portrait;
            return landscape.Pages.Count < portrait.Pages.Count ? landscape : portrait;
        }
        var (w, h) = Paper(paper);
        if (orientation == PaperOrientation.Landscape) (w, h) = (h, w);
        margin = Math.Clamp(margin, 0, Math.Min(w, h) / 3);
        var areaW = w - 2 * margin;
        var areaH = h - 2 * margin - reserved;
        var width = Math.Max(extents.Width, 1e-6);
        var height = Math.Max(extents.Height, 1e-6);

        var scale = mode switch
        {
            PrintScale.FitToPage => Math.Min(areaW / width, areaH / height),
            PrintScale.TrueSize => 1,
            _ => factor > 0 ? factor : 1,
        };
        // Drawing millimetres per page, then as many pages as the drawing needs (centred on the grid).
        var tileW = areaW / scale;
        var tileH = areaH / scale;
        var columns = mode == PrintScale.FitToPage ? 1 : Math.Max(1, (int)Math.Ceiling(width / tileW - 1e-9));
        var rows = mode == PrintScale.FitToPage ? 1 : Math.Max(1, (int)Math.Ceiling(height / tileH - 1e-9));
        var x0 = extents.Center.X - columns * tileW / 2;
        var y0 = extents.Center.Y + rows * tileH / 2;
        var pages = new List<PrintPage>();
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < columns; c++)
        {
            var min = new Point3(x0 + c * tileW, y0 - (r + 1) * tileH, 0);
            var max = new Point3(x0 + (c + 1) * tileW, y0 - r * tileH, 0);
            pages.Add(new PrintPage(new BoundingBox(min, max), r, c));
        }
        return new PrintLayout(w, h, margin, scale, pages);
    }
}
