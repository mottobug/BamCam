using BamCam.Core.Cad;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Tests;

public class AlignTests
{
    private static (CadDocument Doc, CircleEntity Small, RectEntity Big) Drawing()
    {
        var doc = new CadDocument();
        var small = doc.AddEntity(new CircleEntity { Center = new Point3(5, 5, 0), Diameter = 4 });   // x 3..7, y 3..7
        var big = doc.AddEntity(RectEntity.FromCorners(new Point2(20, 10), new Point2(40, 30), 0));   // x 20..40, y 10..30
        return (doc, small, big);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(Entity e)
    {
        var b = e.Bounds();
        return (Math.Round(b.Min.X, 9), Math.Round(b.Min.Y, 9), Math.Round(b.Max.X, 9), Math.Round(b.Max.Y, 9));
    }

    [Fact]
    public void Aligns_to_the_last_selected_object_which_stays()
    {
        var (doc, small, big) = Drawing();
        Assert.True(Align.Apply(doc, [small, big], AlignEdge.Left, AlignReference.LastSelected));
        Assert.Equal((20.0, 3.0, 24.0, 7.0), Box(small));
        Assert.Equal((20.0, 10.0, 40.0, 30.0), Box(big));

        Assert.True(Align.Apply(doc, [small, big], AlignEdge.CenterY, AlignReference.LastSelected));
        Assert.Equal((20.0, 18.0, 24.0, 22.0), Box(small));
    }

    [Fact]
    public void Aligns_to_the_first_selected_object()
    {
        var (doc, small, big) = Drawing();
        Assert.True(Align.Apply(doc, [small, big], AlignEdge.Top, AlignReference.FirstSelected));
        Assert.Equal((20.0, -13.0, 40.0, 7.0), Box(big));
        Assert.Equal((3.0, 3.0, 7.0, 7.0), Box(small));
    }

    [Fact]
    public void Aligns_each_object_to_the_selection_extents_origin_or_stock()
    {
        var (doc, small, big) = Drawing();
        Assert.True(Align.Apply(doc, [small, big], AlignEdge.Right, AlignReference.Selection));
        Assert.Equal((36.0, 3.0, 40.0, 7.0), Box(small));
        Assert.Equal((20.0, 10.0, 40.0, 30.0), Box(big));

        Assert.True(Align.Apply(doc, [small], AlignEdge.CenterX, AlignReference.Origin));
        Assert.Equal((-2.0, 3.0, 2.0, 7.0), Box(small));

        Assert.False(Align.Apply(doc, [small], AlignEdge.Bottom, AlignReference.Stock)); // no stock
        doc.Machining.Stock = new Stock { PMin = new Point3(0, -5, -3), PMax = new Point3(50, 40, 0) };
        Assert.True(Align.Apply(doc, [small], AlignEdge.Bottom, AlignReference.Stock));
        Assert.Equal((-2.0, -5.0, 2.0, -1.0), Box(small));
    }

    [Fact]
    public void An_anchor_needs_a_second_object()
    {
        var (doc, small, _) = Drawing();
        Assert.False(Align.Apply(doc, [small], AlignEdge.Left, AlignReference.LastSelected));
    }
}
