using System.ComponentModel;
using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using SkiaSharp;
using static BamCam.Core.Localization.Tr;

namespace BamCam.App.Services;

/// <summary>Settings of File ▸ Print and File ▸ Export ▸ PDF.</summary>
public sealed class PrintOptions
{
    [Category("Page"), DisplayName("Paper Size")] public PaperSize Paper { get; set; } = PaperSize.A4;
    [Category("Page")] public PaperOrientation Orientation { get; set; } = PaperOrientation.Auto;
    [Category("Page"), Description("Free border around the printed area (mm).")] public double Margin { get; set; } = 10;

    [Category("Scale"), Description("Fit to page, true size 1:1 or a custom factor; a drawing larger than the page at 1:1 or the custom factor is split over several pages.")]
    public PrintScale Scale { get; set; } = PrintScale.FitToPage;

    [Category("Scale"), DisplayName("Scale Factor"), Description("Used with Scale = Custom, e.g. 0.5 for 1:2 or 2 for 2:1.")]
    public double Factor { get; set; } = 1;

    [Category("Content"), DisplayName("Toolpaths"), Description("Print the generated toolpaths (cut moves) too.")]
    public bool Toolpaths { get; set; }

    [Category("Content"), DisplayName("Layer Colours"), Description("Draw objects in their layer colour instead of black.")]
    public bool LayerColors { get; set; }

    [Category("Content"), DisplayName("Line Width"), Description("Width of the drawn lines (mm).")]
    public double LineWidth { get; set; } = 0.25;

    [Category("Content"), Description("Drawing name, scale, page and date below the drawing.")]
    public bool Caption { get; set; } = true;

    public override string ToString() => "Print";
}

/// <summary>Writes the drawing as PDF (vector, true to scale) for printing.</summary>
public static class PdfPrint
{
    private const float PointsPerMm = 72f / 25.4f;
    private const double CaptionHeight = 6;

    /// <summary>Writes the PDF and returns the number of pages.</summary>
    public static int Write(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths, PrintOptions o, string path)
    {
        var mm = CuttingData.MillimetresPerUnit(doc.Units);
        var entities = doc.Layers.Where(l => l.Visible).SelectMany(l => l.Entities.Where(e => !e.Hidden).Select(e => (Layer: l, Entity: e)))
            .Where(x => x.Entity is not SurfaceEntity).ToList();
        var cuts = o.Toolpaths
            ? toolpaths.Values.SelectMany(t => t.DisplaySegments()).Where(s => s.Kind != MoveKind.Rapid).ToList()
            : [];

        var extents = BoundingBox.Empty;
        foreach (var (_, e) in entities) extents = extents.Union(e.Bounds());
        foreach (var s in cuts) extents = extents.Include(s.From.XY).Include(s.To.XY);
        if (extents.IsEmpty) throw new InvalidOperationException(T("The drawing is empty."));
        extents = new BoundingBox(extents.Min * mm, extents.Max * mm);

        var layout = PrintLayout.Create(extents, o.Paper, o.Orientation, o.Scale, o.Factor, o.Margin, o.Caption ? CaptionHeight : 0);
        using var stream = File.Create(path);
        using var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = doc.Name, Creator = "BamCam" });
        var number = 0;
        foreach (var page in layout.Pages)
        {
            number++;
            using var canvas = pdf.BeginPage((float)layout.PaperWidth * PointsPerMm, (float)layout.PaperHeight * PointsPerMm);
            canvas.Scale(PointsPerMm); // from here on: paper millimetres, y downwards
            var area = new SKRect((float)layout.Margin, (float)layout.Margin,
                (float)(layout.Margin + layout.AreaWidth), (float)(layout.PaperHeight - layout.Margin - (o.Caption ? CaptionHeight : 0)));
            SKPoint Map(Point2 p) => new(
                (float)(layout.Margin + (p.X * mm - page.Window.Min.X) * layout.Scale),
                (float)(layout.Margin + (page.Window.Max.Y - p.Y * mm) * layout.Scale));

            canvas.Save();
            canvas.ClipRect(area);
            using var pen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(o.LineWidth, 0.05), IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            foreach (var (layer, e) in entities)
            {
                pen.Color = o.LayerColors ? new SKColor(layer.Color.R, layer.Color.G, layer.Color.B) : SKColors.Black;
                if (e is PointListEntity pl)
                {
                    foreach (var p in pl.WorldPoints())
                    {
                        var s = Map(p.XY);
                        canvas.DrawLine(s.X - 1, s.Y, s.X + 1, s.Y, pen);
                        canvas.DrawLine(s.X, s.Y - 1, s.X, s.Y + 1, pen);
                    }
                    continue;
                }
                foreach (var outline in e.ToPaths())
                {
                    var pts = outline.Flatten(0.02 / Math.Max(mm * layout.Scale, 1e-9));
                    if (pts.Count < 2) continue;
                    using var skPath = new SKPath();
                    skPath.MoveTo(Map(pts[0]));
                    foreach (var p in pts.Skip(1)) skPath.LineTo(Map(p));
                    if (outline.Closed) skPath.Close();
                    canvas.DrawPath(skPath, pen);
                }
            }
            if (cuts.Count > 0)
            {
                pen.Color = new SKColor(0, 90, 200);
                pen.StrokeWidth = (float)Math.Max(o.LineWidth * 0.6, 0.05);
                foreach (var s in cuts) canvas.DrawLine(Map(s.From.XY), Map(s.To.XY), pen);
            }
            canvas.Restore();

            if (o.Caption)
            {
                using var font = new SKFont(SKTypeface.Default, 3);
                using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                // Numbers and date in the interface language.
                var scaleText = layout.Scale >= 1 ? string.Format(Culture, "{0:0.###}:1", layout.Scale) : string.Format(Culture, "1:{0:0.###}", 1 / layout.Scale);
                var pageText = layout.Pages.Count > 1 ? $"   {F("Page {0}/{1} (row {2}, column {3})", number, layout.Pages.Count, page.Row + 1, page.Column + 1)}" : "";
                var caption = $"{doc.Name}   {T("Scale")} {scaleText}{pageText}   {DateTime.Now.ToString("d", Culture)}";
                canvas.DrawText(caption, (float)layout.Margin, (float)(layout.PaperHeight - layout.Margin - 1), font, text);
            }
            pdf.EndPage();
        }
        pdf.Close();
        return layout.Pages.Count;
    }
}
