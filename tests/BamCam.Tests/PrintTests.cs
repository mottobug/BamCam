using BamCam.App.Services;
using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class PrintTests
{
    private static BoundingBox Box(double w, double h) => new(new Point3(0, 0, 0), new Point3(w, h, 0));

    [Fact]
    public void Fit_to_page_uses_one_page_and_the_better_orientation()
    {
        // 400 x 100 mm on A4 with 10 mm margin: landscape (277 x 190 area) fits better than portrait (190 x 277).
        var layout = PrintLayout.Create(Box(400, 100), PaperSize.A4, PaperOrientation.Auto, PrintScale.FitToPage, 1, 10, 0);
        Assert.Single(layout.Pages);
        Assert.Equal(297, layout.PaperWidth);
        Assert.Equal(277.0 / 400, layout.Scale, 9);
    }

    [Fact]
    public void True_size_splits_large_drawings_into_tiles()
    {
        // 500 x 200 mm at 1:1 on A4 landscape (277 x 190 area): 2 columns x 2 rows; portrait would need 3 x 1.
        var layout = PrintLayout.Create(Box(500, 200), PaperSize.A4, PaperOrientation.Landscape, PrintScale.TrueSize, 1, 10, 0);
        Assert.Equal(1, layout.Scale);
        Assert.Equal(4, layout.Pages.Count);
        // The tiles cover the drawing without gaps and are as large as the printable area.
        var all = layout.Pages.Aggregate(BoundingBox.Empty, (b, p) => b.Union(p.Window));
        Assert.True(all.Min.X <= 0 && all.Min.Y <= 0 && all.Max.X >= 500 && all.Max.Y >= 200);
        Assert.All(layout.Pages, p => Assert.Equal(277, p.Window.Width, 9));

        var auto = PrintLayout.Create(Box(500, 200), PaperSize.A4, PaperOrientation.Auto, PrintScale.TrueSize, 1, 10, 0);
        Assert.Equal(3, auto.Pages.Count); // portrait: 3 columns x 1 row is fewer pages
    }

    [Fact]
    public void Custom_factor_scales_the_tiles()
    {
        var layout = PrintLayout.Create(Box(500, 200), PaperSize.A4, PaperOrientation.Landscape, PrintScale.Custom, 0.5, 10, 0);
        var page = Assert.Single(layout.Pages); // 250 x 100 on paper fits
        Assert.Equal(277 / 0.5, page.Window.Width, 9);
    }

    [Fact]
    public void Pdf_has_one_page_per_tile()
    {
        var doc = new CadDocument { Name = "Print test" };
        doc.AddEntity(RectEntity.FromCorners(new Point2(0, 0), new Point2(500, 200), 5));
        doc.AddEntity(new CircleEntity { Center = new Point3(250, 100, 0), Diameter = 80 });
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-{Guid.NewGuid():N}.pdf");
        try
        {
            var options = new PrintOptions { Scale = PrintScale.TrueSize, Orientation = PaperOrientation.Landscape };
            var pages = PdfPrint.Write(doc, new Dictionary<MachineOp, Toolpath>(), options, path);
            Assert.True(pages >= 4);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.Equal(pages, System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
