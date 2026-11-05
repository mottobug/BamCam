using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class DrawingMergeTests
{
    private static PolylineEntity Square(double size) =>
        new(Path2.FromPoints([new(0, 0), new(size, 0), new(size, size), new(0, size)], closed: true));

    /// <summary>Saved and read again, as a real file: ids and references are those of the file.</summary>
    private static CadDocument Source(string layerName = "Plate")
    {
        var doc = CadDocument.CreateNew();
        doc.Name = "Other";
        var layer = new Layer(layerName) { Color = new RgbColor(1, 2, 3) };
        doc.Layers.Add(layer);
        var square = doc.AddEntity(Square(40), layer);
        var op = new ProfileOp { Name = "cut" };
        op.PrimitiveIds.Add(square.Id);
        op.TargetDepth.Set(-2);
        op.DepthIncrement.Set(2);
        op.ToolDiameter.Set(3);
        op.Tabs.Add(new HoldingTab { ParentEntityID = square.Id, ParametricPoint = 0.5 });
        doc.ActivePart.Operations.Add(op);
        return CbReader.Parse(CbWriter.ToString(doc));
    }

    private static CadDocument Target()
    {
        var doc = CadDocument.CreateNew();
        doc.AddEntity(Square(10));
        doc.AddEntity(Square(20));
        return doc;
    }

    [Fact]
    public void Objects_get_new_ids_and_operations_follow_them()
    {
        var target = Target();
        var existing = target.AllEntities().Select(e => e.Id).ToList();

        var inserted = DrawingMerge.Insert(target, Source(), withOperations: true);

        var entity = Assert.Single(inserted.Entities);
        Assert.DoesNotContain(entity.Id, existing);
        Assert.Equal(3, target.AllEntities().Select(e => e.Id).Distinct().Count());
        var op = Assert.IsType<ProfileOp>(Assert.Single(inserted.Operations));
        Assert.Equal([entity.Id], op.PrimitiveIds);
        Assert.Equal(entity.Id, Assert.Single(op.Tabs).ParentEntityID);

        // The operation cuts the inserted shape, and everything survives saving.
        var toolpath = ToolpathGenerator.Generate(target, op);
        Assert.Empty(toolpath.Warnings);
        Assert.True(toolpath.Moves.Max(m => m.To.X) > 40);
        var back = CbReader.Parse(CbWriter.ToString(target));
        Assert.Equal(3, back.AllEntities().Count());
        Assert.Equal(entity.Id, back.Parts.SelectMany(p => p.Operations).Single().PrimitiveIds.Single());
    }

    [Fact]
    public void Layers_of_the_same_name_are_shared_and_others_added()
    {
        var target = Target();
        var count = target.ActiveLayer.Entities.Count;
        var color = target.ActiveLayer.Color;

        var same = DrawingMerge.Insert(target, Source("Default"), withOperations: false);
        Assert.Empty(same.NewLayers);
        Assert.Equal(count + 1, target.ActiveLayer.Entities.Count);
        Assert.Equal(color, target.ActiveLayer.Color);

        var other = DrawingMerge.Insert(target, Source("Plate"), withOperations: false);
        var layer = Assert.Single(other.NewLayers);
        Assert.Equal("Plate", layer.Name);
        Assert.Equal(new RgbColor(1, 2, 3), layer.Color);
        Assert.Same(layer, Assert.Single(other.Entities).Layer);
    }

    [Fact]
    public void Parts_keep_a_unique_name_without_the_other_output_file()
    {
        var target = Target();
        var source = Source();
        source.Parts.Single(p => p.Operations.Count > 0).OutFile = @"C:\jobs\other.nc";

        var first = Assert.Single(DrawingMerge.Insert(target, source, withOperations: true).NewParts);
        var second = Assert.Single(DrawingMerge.Insert(target, Source(), withOperations: true).NewParts);

        Assert.Equal("Part1 (Other)", first.Name);
        Assert.Equal("Part1 (Other 2)", second.Name);
        Assert.Empty(first.OutFile);
        Assert.Equal("Part1", target.ActivePart.Name);
        Assert.Empty(target.ActivePart.Operations);
    }

    [Fact]
    public void Without_operations_no_part_is_added()
    {
        var target = Target();
        var inserted = DrawingMerge.Insert(target, Source(), withOperations: false);
        Assert.Empty(inserted.NewParts);
        Assert.Empty(inserted.Operations);
        Assert.Single(target.Parts);
    }

    [Fact]
    public void Machining_options_of_the_other_drawing_stay_with_its_part()
    {
        var source = Source();
        source.Machining.Style = "Steel";
        source.Machining.MachiningOrigin = new OptionalPoint(new Point3(5, 6, 0));
        var part = Assert.Single(DrawingMerge.Insert(Target(), source, withOperations: true).NewParts);
        Assert.Equal("Steel", part.Style);
        Assert.Equal(new Point3(5, 6, 0), part.MachiningOrigin.Point);
    }

    [Fact]
    public void Other_units_scale_the_geometry_and_leave_operations_out()
    {
        var target = Target();
        var source = Source();
        source.Units = DrawingUnits.Inches;

        var inserted = DrawingMerge.Insert(target, source, withOperations: true);

        Assert.Equal(25.4, inserted.Scale, 9);
        Assert.Empty(inserted.Operations);
        Assert.Equal(40 * 25.4, Assert.Single(inserted.Entities).Bounds().Width, 6);
    }

    [Fact]
    public void A_dxf_file_is_inserted_with_its_layers()
    {
        var drawing = CadDocument.CreateNew();
        var layer = new Layer("Cuts");
        drawing.Layers.Add(layer);
        drawing.AddEntity(Square(40), layer);
        var file = Path.Combine(Path.GetTempPath(), $"bamcam-insert-{Guid.NewGuid():N}.dxf");
        try
        {
            BamCam.IO.Dxf.DxfWriter.Save(drawing, file);
            var source = BamCam.IO.Dxf.DxfReader.Load(file, out _);

            var target = Target();
            target.Units = DrawingUnits.Inches;
            source.Units = target.Units; // as File ▸ Insert File does: the file names no units
            var existing = target.AllEntities().Select(e => e.Id).ToList();
            var inserted = DrawingMerge.Insert(target, source, withOperations: false);

            Assert.Equal(1, inserted.Scale);
            Assert.Contains(target.Layers, l => l.Name == "Cuts");
            var entity = Assert.Single(inserted.Entities);
            Assert.DoesNotContain(entity.Id, existing);
            Assert.Equal(40, entity.Bounds().Width, 6);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Unsupported_operations_keep_their_reference_in_the_original_element()
    {
        var source = Source();
        var entityId = source.AllEntities().Single().Id;
        var raw = new System.Xml.Linq.XElement("future",
            new System.Xml.Linq.XElement("primitive", new System.Xml.Linq.XElement("prim", entityId)));
        var future = new UnsupportedOp("future") { RawXml = raw };
        future.PrimitiveIds.Add(entityId);
        source.Parts.First(p => p.Operations.Count > 0).Operations.Add(future);

        var inserted = DrawingMerge.Insert(Target(), source, withOperations: true);

        var id = Assert.Single(inserted.Entities).Id;
        Assert.NotEqual(entityId, id);
        Assert.Equal(id.ToString(), raw.Element("primitive")!.Element("prim")!.Value);
        Assert.Equal([id], future.PrimitiveIds);
    }
}
