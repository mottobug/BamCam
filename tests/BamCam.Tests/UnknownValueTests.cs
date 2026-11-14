using BamCam.Core.Cam;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>Issue 14: values BamCam does not know must not silently become the default.</summary>
public class UnknownValueTests
{
    private static readonly string ExampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    /// <summary>A drawing with a 3D profile whose Start Corner BamCam does not know ("Diagonal").</summary>
    private static CadDocument WithUnknownStartCorner()
    {
        var doc = new CadDocument();
        var op = new Profile3DOp { Name = "3DSurface1" };
        op.StartCorner.Set(StartCorner.TopLeft);
        doc.ActivePart.Operations.Add(op);
        var xml = CbWriter.ToString(doc);
        Assert.Contains("<StartCorner state=\"Value\">TopLeft</StartCorner>", xml);
        return CbReader.Parse(xml.Replace("<StartCorner state=\"Value\">TopLeft</StartCorner>", "<StartCorner state=\"Default\">Diagonal</StartCorner>"));
    }

    [Fact]
    public void An_unknown_value_is_reported_and_gives_no_toolpath()
    {
        var doc = WithUnknownStartCorner();
        var op = doc.ActivePart.Operations.Single();

        Assert.Equal([("StartCorner", "Diagonal")], op.UnknownValues());
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Moves);
        var warning = Assert.Single(tp.Warnings);
        Assert.Contains("Start Corner 'Diagonal'", warning);
    }

    [Fact]
    public void The_unknown_value_is_saved_back_unchanged()
    {
        var xml = CbWriter.ToString(WithUnknownStartCorner());
        Assert.Contains("<StartCorner state=\"Default\">Diagonal</StartCorner>", xml);
    }

    [Fact]
    public void Choosing_a_value_makes_it_known_again()
    {
        var doc = WithUnknownStartCorner();
        var op = (Profile3DOp)doc.ActivePart.Operations.Single();

        op.StartCorner.Set(StartCorner.TopRight);

        Assert.Empty(op.UnknownValues());
        Assert.Empty(ToolpathGenerator.UnknownValueWarnings(op));
        Assert.Null(op.StartCorner.CloneParam().UnknownText);
    }

    [Fact]
    public void The_example_drawings_have_no_unknown_values()
    {
        foreach (var file in Directory.GetFiles(ExampleDir, "*.cb"))
        {
            var doc = CbReader.Load(file);
            foreach (var op in doc.Parts.SelectMany(p => p.Operations))
                Assert.True(op.UnknownValues().Count == 0,
                    $"{Path.GetFileName(file)} / {op.Name}: {string.Join(", ", op.UnknownValues())}");
        }
    }
}
