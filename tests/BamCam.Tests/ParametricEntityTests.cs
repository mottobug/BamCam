using System.ComponentModel;
using System.Xml.Linq;
using BamCam.Core.Cad;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class ParametricEntityTests
{
    /// <summary>A square of the given size, optionally with a hole; Unavailable makes Generate give nothing.</summary>
    public sealed class TestSquare : ParametricEntity
    {
        public override string ParametricType => "test.square";
        [Category("Test")] public double Size { get; set; } = 10;
        [Category("Test")] public bool Hole { get; set; }
        [Category("Test")] public ParamKind Kind { get; set; } = ParamKind.A;
        [Browsable(false)] public static bool Unavailable { get; set; }

        protected override IEnumerable<Path2>? Generate()
        {
            if (Unavailable) return null;
            var s = Size;
            var list = new List<Path2> { Path2.FromPoints([new(0, 0), new(s, 0), new(s, s), new(0, s)], true) };
            if (Hole) list.Add(Path2.FromPoints([new(s / 4, s / 4), new(s / 2, s / 4), new(s / 2, s / 2)], true));
            return list;
        }
    }

    public enum ParamKind { A, B }

    static ParametricEntityTests() => ParametricEntities.Register("test.square", () => new TestSquare());

    private static (CadDocument Doc, TestSquare Square) Drawing()
    {
        var doc = new CadDocument();
        var sq = doc.AddEntity(new TestSquare { Size = 20, Hole = true, Kind = ParamKind.B, Tag = "keep" });
        sq.ApplyTransform(Matrix4.Translation(100, 50));
        return (doc, sq);
    }

    [Fact]
    public void Saved_as_polylines_with_the_object_data()
    {
        var (doc, _) = Drawing();
        var xml = XDocument.Parse(CbWriter.ToString(doc));
        var plines = xml.Descendants("pline").ToList();
        Assert.Equal(2, plines.Count); // square and hole
        var data = Assert.Single(plines[0].Elements(CbWriter.ParametricData));
        Assert.Equal("test.square", (string?)data.Attribute("type"));
        Assert.Equal("20", data.Elements("param").Single(p => (string?)p.Attribute("name") == "Size").Value);
        Assert.NotNull(plines[1].Element(CbWriter.ParametricPart));
        // The fallback geometry is in drawing coordinates (moved by 100, 50).
        Assert.Contains("100,50", plines[0].ToString());
    }

    [Fact]
    public void Reading_restores_the_object_with_its_parameters_and_transform()
    {
        var (doc, sq) = Drawing();
        var back = CbReader.Parse(CbWriter.ToString(doc));
        var restored = Assert.IsType<TestSquare>(Assert.Single(back.AllEntities()));
        Assert.Equal((20.0, true, ParamKind.B, "keep", sq.Id), (restored.Size, restored.Hole, restored.Kind, restored.Tag, restored.Id));
        Assert.True(restored.IsComputed);
        var box = restored.Bounds();
        Assert.Equal((100.0, 50.0, 120.0, 70.0), (box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
        // Writing again gives the same file (no duplicated data).
        Assert.Equal(CbWriter.ToString(doc), CbWriter.ToString(back));
    }

    [Fact]
    public void Unknown_types_stay_polylines_and_keep_their_data()
    {
        var (doc, _) = Drawing();
        var xml = CbWriter.ToString(doc).Replace("test.square", "other.vendor.thing");
        var back = CbReader.Parse(xml);
        Assert.All(back.AllEntities(), e => Assert.IsType<PolylineEntity>(e));
        Assert.Equal(2, back.AllEntities().Count());
        Assert.Contains("other.vendor.thing", CbWriter.ToString(back));
    }

    [Fact]
    public void Saved_outlines_are_shown_when_the_geometry_cannot_be_computed()
    {
        var (doc, _) = Drawing();
        var xml = CbWriter.ToString(doc);
        try
        {
            TestSquare.Unavailable = true;
            var back = CbReader.Parse(xml);
            var obj = Assert.IsType<TestSquare>(Assert.Single(back.AllEntities()));
            Assert.False(obj.IsComputed);
            Assert.Equal(2, obj.ToPaths().Count());
            // Moving the object moves the saved outlines too.
            obj.ApplyTransform(Matrix4.Translation(-100, 0));
            Assert.Equal(0, obj.Bounds().Min.X, 9);
        }
        finally
        {
            TestSquare.Unavailable = false;
        }
    }

    [Fact]
    public void Parameters_change_the_geometry_and_clone_and_explode_work()
    {
        var (_, sq) = Drawing();
        sq.Size = 40;
        sq.Invalidate();
        Assert.Equal(40, sq.Bounds().Width, 9);
        var copy = Assert.IsType<TestSquare>(sq.CloneEntity());
        Assert.Equal((40.0, ParamKind.B), (copy.Size, copy.Kind));
        Assert.Equal(sq.Bounds(), copy.Bounds());
        Assert.All(CadOps.Explode(sq), e => Assert.IsType<PolylineEntity>(e));
        Assert.Equal(2, CadOps.Explode(sq).Count);
    }
}
