using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

[Collection(GlobalLibrariesCollection.Name)]
public class LatheToolTests
{
    [Fact]
    public void Tool_library_round_trip_keeps_lathe_values_and_unknown_elements()
    {
        var lib = CamLibraries.LatheTemplates(inches: false);
        lib.Tools[0].Extensions.Add(new XElement("SomethingElse", "kept"));
        lib.Tools[0].Comment = "CNMG 120408";
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-tools-{Guid.NewGuid():N}.xml");
        try
        {
            LibraryFiles.SaveTools(lib, path);
            var back = LibraryFiles.LoadTools(path);
            Assert.Equal(lib.Tools.Count, back.Tools.Count);
            foreach (var (a, b) in lib.Tools.Zip(back.Tools))
            {
                Assert.Equal((a.Index, a.Name, a.Diameter, a.ToolProfile, a.LatheToolType, a.TipOrientation),
                    (b.Index, b.Name, b.Diameter, b.ToolProfile, b.LatheToolType, b.TipOrientation));
            }
            Assert.Equal("CNMG 120408", back.Tools[0].Comment);
            Assert.Equal("kept", Assert.Single(back.Tools[0].Extensions).Value);
            // Without lathe values nothing BamCam-specific is written (CamBam's own elements only).
            var xml = File.ReadAllText(path);
            Assert.Equal(lib.Tools.Count, System.Text.RegularExpressions.Regex.Matches(xml, "<LatheToolType>").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Templates_cover_every_turning_operation()
    {
        foreach (var inches in new[] { false, true })
        {
            var lib = CamLibraries.LatheTemplates(inches);
            foreach (var op in new MachineOp[] { new LatheOp(), new LatheFacingOp(), new LatheGrooveOp(), new LatheThreadOp(), new LatheDrillOp() })
                Assert.Contains(lib.Tools, t => t.LatheToolType == CamLibraries.RequiredLatheTool(op));
            Assert.Equal(lib.Tools.Count, lib.Tools.Select(t => t.Index).Distinct().Count());
        }
    }

    [Fact]
    public void Turning_operations_take_the_tool_from_the_lathe_library_and_warn_about_unsuitable_tools()
    {
        var lib = CamLibraries.LatheTemplates(inches: false);
        lib.Name = CamLibraries.LatheLibraryName(DrawingUnits.Millimeters);
        CamLibraries.ToolLibraries.Add(lib);
        try
        {
            var doc = new CadDocument();
            doc.Machining.MachiningOrigin = new OptionalPoint(new Point3(0, 0, 0));
            var shape = new PolylineEntity(Path2.FromPoints([new(-20, 10), new(-20, 6), new(-14, 6), new(-14, 10)], false));
            doc.AddEntity(shape);
            var groove = new LatheGrooveOp { Name = "groove" };
            groove.PrimitiveIds.Add(shape.Id);
            groove.StockSurface.Set(10);
            groove.ClearancePlane.Set(12);
            doc.ActivePart.Operations.Add(groove);

            // Tool 3: grooving blade 3 mm, found in BamCam-Lathe-mm although no library is selected.
            groove.ToolNumber.Set(3);
            Assert.Equal(3, groove.EffectiveToolDiameter(doc));
            Assert.Empty(ToolpathGenerator.Generate(doc, groove).Warnings);

            // Tool 1 is a turning tool.
            groove.ToolNumber.Set(1);
            var warning = Assert.Single(ToolpathGenerator.Generate(doc, groove).Warnings);
            Assert.Contains("Grooving", warning);

            // Milling operations do not look into the lathe library.
            var profile = new ProfileOp();
            doc.ActivePart.Operations.Add(profile);
            profile.ToolNumber.Set(5); // threading tool, nose diameter 0.2
            Assert.NotEqual(0.2, profile.EffectiveToolDiameter(doc));
        }
        finally
        {
            CamLibraries.ToolLibraries.Remove(lib);
        }
    }
}
