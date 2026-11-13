using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Svg;

namespace BamCam.Tests;

/// <summary>SVG import: page size and units, Y up, shapes, curves, transforms, layers.</summary>
public class SvgReaderTests
{
    private static CadDocument Read(string body, string root = "width=\"100mm\" height=\"50mm\" viewBox=\"0 0 100 50\"", DrawingUnits units = DrawingUnits.Millimeters)
    {
        var reader = new SvgReader(units);
        return reader.Read($"<svg xmlns=\"http://www.w3.org/2000/svg\" {root}>{body}</svg>", "test");
    }

    private static BoundingBox Bounds(Entity e) => e.Bounds();

    [Fact]
    public void A_line_is_placed_on_the_page_with_Y_up()
    {
        var doc = Read("<line x1=\"10\" y1=\"10\" x2=\"30\" y2=\"10\"/>");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        // 10 mm below the top of a 50 mm page = 40 mm above its bottom.
        Assert.Equal(new Point2(10, 40), pl.Points[0].XY);
        Assert.Equal(new Point2(30, 40), pl.Points[1].XY);
    }

    [Fact]
    public void Plain_numbers_are_css_pixels_at_96_per_inch()
    {
        var doc = Read("<line x1=\"0\" y1=\"0\" x2=\"96\" y2=\"0\"/>", "width=\"96\" height=\"96\"");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        Assert.Equal(25.4, Point2.Distance(pl.Points[0].XY, pl.Points[1].XY), 9);
        Assert.Equal(25.4, pl.Points[0].Point.Y, 9);
    }

    [Fact]
    public void The_viewbox_scales_to_the_page_size_and_inches_are_converted()
    {
        // 200 user units across a 2 inch page.
        var doc = Read("<line x1=\"0\" y1=\"0\" x2=\"100\" y2=\"0\"/>", "width=\"2in\" height=\"1in\" viewBox=\"0 0 200 100\"", DrawingUnits.Inches);

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        Assert.Equal(1, pl.Points[1].Point.X, 9);
        Assert.Equal(1, pl.Points[1].Point.Y, 9);
        Assert.Equal(DrawingUnits.Inches, doc.Units);
    }

    [Fact]
    public void Circles_and_rectangles_stay_circles_and_rectangles()
    {
        var doc = Read("<circle cx=\"20\" cy=\"20\" r=\"5\"/><rect x=\"40\" y=\"10\" width=\"30\" height=\"20\" rx=\"2\"/>");

        var circle = Assert.Single(doc.AllEntities().OfType<CircleEntity>());
        Assert.Equal(10, circle.Diameter, 9);
        Assert.Equal(new Point3(20, 30, 0), circle.Center);
        var rect = Assert.Single(doc.AllEntities().OfType<RectEntity>());
        Assert.Equal(new Point3(40, 20, 0), rect.LowerLeft);
        Assert.Equal(30, rect.Width, 9);
        Assert.Equal(20, rect.Height, 9);
        Assert.Equal(2, rect.CornerRadius, 9);
    }

    [Fact]
    public void A_rotated_rectangle_becomes_a_closed_polyline()
    {
        var doc = Read("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" transform=\"rotate(45)\"/>");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        Assert.True(pl.Closed);
        Assert.Equal(4, pl.Points.Count);
    }

    [Fact]
    public void A_bezier_circle_comes_back_as_arcs_on_the_circle()
    {
        // A circle of radius 10 around (50, 25) drawn with the usual four cubic curves (k = 0.5523).
        const string d = "M 60,25 C 60,30.523 55.523,35 50,35 C 44.477,35 40,30.523 40,25 C 40,19.477 44.477,15 50,15 C 55.523,15 60,19.477 60,25 Z";
        var doc = Read($"<path d=\"{d}\"/>");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        Assert.True(pl.Closed);
        Assert.Contains(pl.Points, v => Math.Abs(v.Bulge) > 1e-6);
        Assert.True(pl.Points.Count < 40, $"{pl.Points.Count} vertices");
        foreach (var p in pl.LocalPath.Flatten(0.001))
            Assert.InRange(Point2.Distance(p, new Point2(50, 25)), 9.99, 10.01);
    }

    [Fact]
    public void Arc_commands_with_flags_written_together_are_read()
    {
        // Half circle of radius 10 from (10, 25) to (30, 25), flags "01" without separator.
        var doc = Read("<path d=\"M10 25a10 10 0 01 20 0\"/>");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        var b = Bounds(pl);
        Assert.Equal(10, b.Min.X, 3);
        Assert.Equal(30, b.Max.X, 3);
        Assert.Equal(10, b.Height, 2);
        // SVG sweep 1 goes through y = 15 (above in SVG) = 35 in the drawing.
        Assert.Equal(35, b.Max.Y, 2);
    }

    [Fact]
    public void Relative_commands_and_transforms_of_groups_are_applied()
    {
        var doc = Read("<g transform=\"translate(10,5)\"><path d=\"m0,0 h10 v10 h-10 z\" transform=\"scale(2)\"/></g>");

        var pl = Assert.IsType<PolylineEntity>(Assert.Single(doc.AllEntities()));
        var b = Bounds(pl);
        Assert.Equal(10, b.Min.X, 9);
        Assert.Equal(30, b.Max.X, 9);
        Assert.Equal(50 - 25, b.Min.Y, 9);
        Assert.Equal(50 - 5, b.Max.Y, 9);
        Assert.True(pl.Closed);
    }

    [Fact]
    public void Inkscape_layers_become_layers_and_hidden_ones_are_left_out()
    {
        const string root = "width=\"100mm\" height=\"50mm\" viewBox=\"0 0 100 50\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"";
        var doc = Read(
            "<g inkscape:groupmode=\"layer\" inkscape:label=\"Cut\"><g id=\"g12\"><line x1=\"0\" y1=\"0\" x2=\"5\" y2=\"0\"/></g></g>" +
            "<g inkscape:groupmode=\"layer\" inkscape:label=\"Engrave\"><circle cx=\"5\" cy=\"5\" r=\"1\"/></g>" +
            "<g inkscape:groupmode=\"layer\" inkscape:label=\"Notes\" style=\"display:none\"><circle cx=\"5\" cy=\"5\" r=\"1\"/></g>" +
            "<line x1=\"0\" y1=\"1\" x2=\"5\" y2=\"1\"/>", root);

        Assert.Equal(["Cut", "Engrave", "test"], doc.Layers.Select(l => l.Name).ToArray());
        Assert.Single(doc.Layers[0].Entities);
        Assert.IsType<CircleEntity>(Assert.Single(doc.Layers[1].Entities));
        Assert.Single(doc.Layers[2].Entities);
    }

    [Fact]
    public void Use_references_and_text_warnings()
    {
        var reader = new SvgReader();
        var doc = reader.Read("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"100mm\" height=\"50mm\" viewBox=\"0 0 100 50\">" +
                              "<defs><circle id=\"hole\" r=\"2\"/></defs>" +
                              "<use xlink:href=\"#hole\" x=\"10\" y=\"10\"/><use href=\"#hole\" x=\"20\" y=\"10\"/>" +
                              "<text x=\"5\" y=\"5\">Hi</text></svg>", "test");

        Assert.Equal([10.0, 20.0], doc.AllEntities().OfType<CircleEntity>().Select(c => c.Center.X).Order().ToArray());
        Assert.Contains(reader.Warnings, w => w.Contains("texts were skipped"));
    }
}
