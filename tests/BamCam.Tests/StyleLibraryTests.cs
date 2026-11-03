using System.Xml.Linq;
using BamCam.Core.Cam;
using BamCam.IO.Cb;

namespace BamCam.Tests;

public class StyleLibraryTests
{
    private static CamStyleLibrary Library()
    {
        var lib = new CamStyleLibrary { Name = "test" };
        var root = new CamStyle { Name = "" };
        root.Values["CutFeedrate"] = new XElement("CutFeedrate", new XAttribute("state", "Value"), "800");
        root.Values["DepthIncrement"] = new XElement("DepthIncrement", new XAttribute("state", "Value"), "0.4");
        var alu = new CamStyle { Name = "Alu" };
        alu.Values["CutFeedrate"] = new XElement("CutFeedrate", new XAttribute("state", "Value"), "500");
        lib.Styles.Add(root);
        lib.Styles.Add(alu);
        lib.Styles.Add(new CamStyle { Name = "Alu fine", ParentStyle = "Alu" });
        return lib;
    }

    [Fact]
    public void Style_shows_own_values_and_inherits_the_rest()
    {
        var lib = Library();
        var op = new ProfileOp();
        LibraryFiles.LoadStyle(lib, lib.Find("Alu fine")!, op);
        Assert.Equal(500, op.CutFeedrate.Value, 9); // from Alu
        Assert.Equal(ParamState.Default, op.CutFeedrate.State);
        Assert.Equal(0.4, op.DepthIncrement.Value, 9); // from the default style

        // Set a value in "Alu fine", then put the feedrate back to inherited.
        op.DepthIncrement.Set(0.2);
        LibraryFiles.StoreStyle(lib.Find("Alu fine")!, op);
        Assert.True(lib.Find("Alu fine")!.Values.ContainsKey("DepthIncrement"));
        Assert.False(lib.Find("Alu fine")!.Values.ContainsKey("CutFeedrate"));

        var again = new ProfileOp();
        LibraryFiles.LoadStyle(lib, lib.Find("Alu fine")!, again);
        Assert.Equal(0.2, again.DepthIncrement.Value, 9);
        Assert.Equal(ParamState.Value, again.DepthIncrement.State);
        again.DepthIncrement.State = ParamState.Default;
        LibraryFiles.StoreStyle(lib.Find("Alu fine")!, again);
        Assert.Empty(lib.Find("Alu fine")!.Values);
    }

    [Fact]
    public void Libraries_round_trip_through_files()
    {
        var lib = Library();
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-styles-{Guid.NewGuid():N}.xml");
        try
        {
            LibraryFiles.SaveStyles(lib, path);
            var back = LibraryFiles.LoadStyles(path);
            Assert.Equal(new[] { "", "Alu", "Alu fine" }, back.Styles.Select(s => s.Name));
            Assert.Equal("Alu", back.Find("Alu fine")!.ParentStyle);
            Assert.Equal("500", back.Find("Alu")!.Values["CutFeedrate"].Value);
            Assert.Equal("CAMStyleLibrary", XDocument.Load(path).Root!.Name.LocalName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_tool_library_can_be_saved_and_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bamcam-tools-{Guid.NewGuid():N}.xml");
        try
        {
            var lib = new ToolLibrary { Name = "mine" };
            lib.Tools.Add(new ToolDefinition { Index = 1, Name = "6 mm", Diameter = 6, ToolProfile = ToolProfile.EndMill });
            LibraryFiles.SaveTools(lib, path);
            var back = LibraryFiles.LoadTools(path);
            Assert.Equal(6, Assert.Single(back.Tools).Diameter, 9);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
