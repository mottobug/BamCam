using System.Reflection;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>A tool number gets its diameter and profile from the tool library, whenever it is chosen.</summary>
[Collection(GlobalLibrariesCollection.Name)]
public sealed class ToolLibraryTests : IDisposable
{
    private readonly List<CamStyleLibrary> _styles = [.. CamLibraries.StyleLibraries];
    private readonly List<ToolLibrary> _tools = [.. CamLibraries.ToolLibraries];

    public ToolLibraryTests()
    {
        CamLibraries.StyleLibraries.Clear();
        CamLibraries.ToolLibraries.Clear();
        var mine = new ToolLibrary { Name = "Mine" };
        mine.Tools.Add(new ToolDefinition { Index = 5, Diameter = 12, ToolProfile = ToolProfile.BallNose });
        var other = new ToolLibrary { Name = "Other" };
        other.Tools.Add(new ToolDefinition { Index = 5, Diameter = 20, ToolProfile = ToolProfile.BullNose });
        CamLibraries.ToolLibraries.AddRange([mine, other]);
    }

    public void Dispose()
    {
        CamLibraries.StyleLibraries.Clear();
        CamLibraries.StyleLibraries.AddRange(_styles);
        CamLibraries.ToolLibraries.Clear();
        CamLibraries.ToolLibraries.AddRange(_tools);
    }

    private static (CadDocument Doc, Part Part, ProfileOp Op) NewOperation()
    {
        var doc = CadDocument.CreateNew();
        var shape = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 40), new(0, 40)], closed: true)));
        var op = new ProfileOp { Name = "cut" };
        op.PrimitiveIds.Add(shape.Id);
        op.TargetDepth.Set(-1);
        doc.ActivePart.Operations.Add(op);
        LibraryFiles.ApplyStyle(doc, op);
        return (doc, doc.ActivePart, op);
    }

    private static void AddStyleLibrary()
    {
        var lib = new CamStyleLibrary { Name = "Styles" };
        lib.Styles.Add(new CamStyle { Name = "" });
        CamLibraries.StyleLibraries.Add(lib);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Number_on_the_operation_uses_the_library_of_the_machining_options(bool withStyles)
    {
        if (withStyles) AddStyleLibrary();
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        op.ToolNumber.Set(5);

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(12, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.BallNose, op.ToolProfile.Value);
        // Outside profile of the 40 mm square: the tool centre runs half of 12 mm beside it.
        Assert.Equal(46, ToolpathGenerator.Generate(doc, op).Moves.Max(m => m.To.X), 3);
    }

    [Fact]
    public void Number_in_the_machining_options_reaches_an_existing_operation()
    {
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        doc.Machining.ToolNumber = 5;

        var changed = LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(5, op.ToolNumber.Value);
        Assert.Equal(12, op.EffectiveToolDiameter(doc));
        Assert.Contains("ToolDiameter", changed);
    }

    [Fact]
    public void Library_and_number_of_the_part_win_over_the_machining_options()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        doc.Machining.ToolNumber = 1;
        part.ToolLibrary = "Other";
        part.ToolNumber = 5;

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(20, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.BullNose, op.ToolProfile.Value);
    }

    [Fact]
    public void Number_on_the_operation_wins_over_part_and_machining_options()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        part.ToolNumber = 9;
        op.ToolNumber.Set(5);

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(5, op.EffectiveToolNumber(doc));
        Assert.Equal(12, op.EffectiveToolDiameter(doc));
    }

    [Fact]
    public void Values_typed_in_the_operation_are_kept()
    {
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        op.ToolNumber.Set(5);
        op.ToolDiameter.Set(7);
        op.ToolProfile.Set(ToolProfile.VCutter);

        Assert.Empty(LibraryFiles.ApplyStyle(doc, op));

        Assert.Equal(7, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.VCutter, op.ToolProfile.Value);
    }

    [Fact]
    public void Number_is_followed_even_with_a_style_unknown_on_this_computer()
    {
        AddStyleLibrary();
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        op.Style = "From another computer";
        op.CutFeedrate.SetDefault(777);
        op.ToolNumber.Set(5);

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(12, op.EffectiveToolDiameter(doc));
        Assert.Equal(777, op.CutFeedrate.Value); // the stored values stay
    }

    [Fact]
    public void A_new_operation_without_tool_keeps_the_fallback_diameter_and_can_still_get_one()
    {
        var (doc, _, op) = NewOperation();
        Assert.Equal(0, op.ToolDiameter.Value);
        Assert.Equal(3, op.EffectiveToolDiameter(doc));

        doc.Units = DrawingUnits.Inches;
        Assert.Equal(0.125, op.EffectiveToolDiameter(doc));
    }

    [Theory]
    [InlineData(typeof(MachiningOptions))]
    [InlineData(typeof(Part))]
    public void Tool_library_is_chosen_from_a_list(Type owner)
    {
        var attribute = owner.GetProperty("ToolLibrary")!.GetCustomAttribute<ChoicesAttribute>()!;
        Assert.Equal(["", "Mine", "Other"], attribute.GetChoices(owner).Select(c => c.Key));
    }
}
