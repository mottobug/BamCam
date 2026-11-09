using System.Reflection;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;
using BamCam.Core.Simulation;
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
    public void Without_a_tool_the_path_runs_on_the_line_with_a_warning()
    {
        var (doc, _, op) = NewOperation();
        Assert.Equal(0, op.EffectiveToolDiameter(doc));

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Equal(40, tp.Moves.Max(m => m.To.X), 3);
        Assert.Contains(tp.Warnings, w => w.Contains("on the line"));
    }

    [Fact]
    public void Message_shows_the_tool_of_the_part_and_its_fields_stay_empty()
    {
        var (doc, part, op) = NewOperation();
        part.Name = "P";
        doc.Machining.ToolLibrary = "Mine";
        Assert.Contains("no tool", CamLibraries.DescribeTool(doc, part));

        part.ToolNumber = 5;
        LibraryFiles.ApplyStyle(doc, op);
        var message = CamLibraries.DescribeTool(doc, part);
        Assert.StartsWith("P:", message);
        Assert.Contains("tool 5", message);
        Assert.Contains("diameter 12", message);
        Assert.Equal(0, part.ToolDiameter);
        Assert.Equal(ToolProfile.Unspecified, part.ToolProfile);

        part.ToolDiameter = 8; // wins over the library, as for the operations
        Assert.Contains("diameter 8", CamLibraries.DescribeTool(doc, part));
        LibraryFiles.ApplyStyle(doc, op);
        Assert.Equal(8, op.EffectiveToolDiameter(doc));

        part.ToolDiameter = 0;
        part.ToolNumber = 99;
        Assert.Contains("tool 99 not found", CamLibraries.DescribeTool(doc, part));
    }

    [Fact]
    public void Simulation_works_without_a_diameter()
    {
        var (doc, _, op) = NewOperation();
        op.TargetDepth.Set(-2);
        var tp = ToolpathGenerator.Generate(doc, op);
        var jobs = new List<(MachineOp, Toolpath)> { (op, tp) };

        var field = MillingSimulation.Create(doc, jobs, 0.5);
        MillingSimulation.SetTargets(field, doc, [op]);
        foreach (var move in MillingSimulation.Moves(doc, jobs, 0.05)) MillingSimulation.Cut(field, move);
        Assert.NotEmpty(tp.Moves);
    }

    [Fact]
    public void Number_on_the_part_that_is_not_found_gives_no_diameter_and_a_warning()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        part.ToolNumber = 5;
        LibraryFiles.ApplyStyle(doc, op);
        Assert.Equal(12, op.ToolDiameter.Value);

        part.ToolNumber = 99; // the old tool must not stay
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(99, op.ToolNumber.Value);
        Assert.Equal(0, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.Unspecified, op.ToolProfile.Value);
        Assert.Contains(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("tool 99 not found"));
    }

    [Fact]
    public void Diameter_of_the_part_wins_over_the_library_and_the_profile_of_the_library_over_the_part()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        part.ToolDiameter = 8;
        part.ToolProfile = ToolProfile.VCutter;
        part.ToolNumber = 5;

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(8, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.BallNose, op.ToolProfile.Value);
    }

    [Fact]
    public void Number_on_the_operation_wins_over_the_diameter_of_the_part()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        part.ToolDiameter = 8;
        op.ToolNumber.Set(5);

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(12, op.EffectiveToolDiameter(doc));
    }

    [Fact]
    public void Diameter_and_profile_of_the_part_reach_the_operation_without_a_number()
    {
        var (doc, part, op) = NewOperation();
        part.ToolDiameter = 8;
        part.ToolProfile = ToolProfile.BallNose;

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(8, op.ToolDiameter.Value);
        Assert.Equal(ToolProfile.BallNose, op.ToolProfile.Value);
    }

    // ---------------------------------------------------------------- the message when an operation gets a tool number

    [Fact]
    public void A_number_of_the_operation_found_in_the_library_is_described()
    {
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        op.ToolNumber.Set(5);
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal("cut: tool 5 from the library: diameter 12, profile BallNose.", CamLibraries.DescribeOperationTool(doc, op));
        Assert.Equal(12, op.ToolDiameter.Value);
    }

    [Fact]
    public void A_number_of_the_operation_that_no_library_has_is_reported()
    {
        var (doc, _, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        op.ToolNumber.Set(7);
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal("cut: tool 7 not found in the tool libraries.", CamLibraries.DescribeOperationTool(doc, op));
    }

    [Fact]
    public void A_diameter_typed_in_the_operation_wins_and_the_message_says_how_to_use_the_library()
    {
        // Issue 4: tool number 8 and Tool Diameter 4, both typed in the operation; the library has 8 mm.
        var (doc, part, op) = NewOperation();
        var library = new ToolLibrary { Name = "Default-mm" };
        library.Tools.Add(new ToolDefinition { Index = 8, Diameter = 8, ToolProfile = ToolProfile.EndMill });
        CamLibraries.ToolLibraries.Add(library);
        doc.Machining.ToolLibrary = "Default-mm";
        doc.Machining.ToolNumber = 2;
        part.ToolLibrary = "Default-mm";
        part.ToolNumber = 8;
        op.ToolDiameter.Set(4);
        op.ToolNumber.Set(8);
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(4, op.EffectiveToolDiameter(doc));
        var message = CamLibraries.DescribeOperationTool(doc, op)!;
        Assert.Contains("diameter 8 in the library", message);
        Assert.Contains("Tool Diameter 4", message);

        // Set back to Default, as the message says: the library counts.
        op.ToolDiameter.State = ParamState.Default;
        LibraryFiles.ApplyStyle(doc, op);
        Assert.Equal(8, op.EffectiveToolDiameter(doc));
        Assert.Contains("from the library", CamLibraries.DescribeOperationTool(doc, op));
    }

    [Fact]
    public void Choosing_a_tool_number_brings_its_diameter_and_profile_over_typed_ones()
    {
        // Issue 4, step 11: diameter typed by hand, then another tool number chosen: the new tool's values are used.
        var (doc, _, op) = NewOperation();
        var library = new ToolLibrary { Name = "Default-mm" };
        library.Tools.Add(new ToolDefinition { Index = 8, Diameter = 12.6, ToolProfile = ToolProfile.BallNose });
        library.Tools.Add(new ToolDefinition { Index = 9, Diameter = 6, ToolProfile = ToolProfile.EndMill });
        CamLibraries.ToolLibraries.Add(library);
        op.ToolNumber.Set(8);
        LibraryFiles.ApplyStyle(doc, op);
        op.ToolDiameter.Set(4);
        op.ToolProfile.Set(ToolProfile.VCutter);

        op.ToolNumber.Set(9);
        var reset = CamLibraries.FollowToolNumber(doc, op);
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(["Tool Diameter", "Tool Profile"], reset);
        Assert.Equal(ParamState.Default, op.ToolDiameter.State);
        Assert.Equal(6, op.EffectiveToolDiameter(doc));
        Assert.Equal(ToolProfile.EndMill, op.ToolProfile.Value);
        Assert.Contains("from the library", CamLibraries.DescribeOperationTool(doc, op));

        // A number no library has leaves the typed diameter, which is then all there is.
        op.ToolDiameter.Set(5);
        op.ToolNumber.Set(77);
        Assert.Empty(CamLibraries.FollowToolNumber(doc, op));
        Assert.Equal(ParamState.Value, op.ToolDiameter.State);
        Assert.Equal(5, op.ToolDiameter.Value);
    }

    [Fact]
    public void Without_a_number_there_is_nothing_to_say()
    {
        var (doc, _, op) = NewOperation();

        Assert.Null(CamLibraries.DescribeOperationTool(doc, op));
    }

    [Fact]
    public void Inherited_values_go_away_with_the_setting_of_the_part()
    {
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        part.ToolNumber = 5;
        LibraryFiles.ApplyStyle(doc, op);
        Assert.Equal(12, op.ToolDiameter.Value);

        part.ToolNumber = 0;
        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(0, op.ToolNumber.Value);
        Assert.Equal(0, op.EffectiveToolDiameter(doc));
    }

    [Fact]
    public void Number_on_the_part_is_not_replaced_by_the_tool_of_the_machining_options()
    {
        // CamBam takes the diameter of the machining options' tool here; the path has to fit the tool in the G-code.
        var (doc, part, op) = NewOperation();
        doc.Machining.ToolLibrary = "Mine";
        doc.Machining.ToolNumber = 1;
        part.ToolNumber = 5;

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(12, op.EffectiveToolDiameter(doc));
    }

    [Fact]
    public void Unknown_tool_library_warns_and_uses_the_default_library()
    {
        var (doc, part, op) = NewOperation();
        var defaults = new ToolLibrary { Name = "Default-mm" };
        defaults.Tools.Add(new ToolDefinition { Index = 2, Diameter = 2, ToolProfile = ToolProfile.EndMill });
        CamLibraries.ToolLibraries.Add(defaults);
        part.ToolLibrary = "Gibt es nicht";
        part.ToolNumber = 2;

        LibraryFiles.ApplyStyle(doc, op);

        Assert.Equal(2, op.EffectiveToolDiameter(doc));
        Assert.Contains(ToolpathGenerator.Generate(doc, op).Warnings, w => w.Contains("Gibt es nicht"));
    }

    private static readonly string Examples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    private static void AddDefaultLibrary()
    {
        var defaults = new ToolLibrary { Name = "Default-mm" };
        for (var i = 1; i <= 3; i++) defaults.Tools.Add(new ToolDefinition { Index = i, Diameter = i, ToolProfile = ToolProfile.EndMill });
        CamLibraries.ToolLibraries.Add(defaults);
    }

    [Fact]
    public void Tool_values_are_those_cambam_saved()
    {
        var mine = Path.Combine(Examples, "Vergleich Werkzeug.cb");
        var saved = Path.Combine(Examples, "Werkzeug gespeichert.cb");
        if (!File.Exists(mine) || !File.Exists(saved)) return;
        AddDefaultLibrary();
        var doc = CbReader.Read(System.Xml.Linq.XDocument.Load(mine));
        var cambam = CbReader.Read(System.Xml.Linq.XDocument.Load(saved));

        Assert.Equal(10, doc.Parts.Count);
        foreach (var (part, other) in doc.Parts.Zip(cambam.Parts))
        {
            var op = Assert.Single(part.Operations);
            var expected = Assert.Single(other.Operations);
            LibraryFiles.ApplyStyle(doc, op);
            Assert.True(expected.ToolNumber.Value == op.ToolNumber.Value, $"{part.Name}: number {op.ToolNumber.Value}");
            Assert.True(expected.ToolDiameter.Value == op.ToolDiameter.Value, $"{part.Name}: diameter {op.ToolDiameter.Value}");
            // Different on purpose: an operation without any tool keeps its profile until one is chosen.
            if (!part.Name.StartsWith("J ", StringComparison.Ordinal))
                Assert.True(expected.ToolProfile.Value == op.ToolProfile.Value, $"{part.Name}: profile {op.ToolProfile.Value}");
        }
    }

    [Fact]
    public void Tool_changes_and_spindle_lines_are_those_of_the_gcode_cambam_made()
    {
        var mine = Path.Combine(Examples, "Vergleich Werkzeug.cb");
        var made = Path.Combine(Examples, "Vergleich Werkzeug.nc");
        if (!File.Exists(mine) || !File.Exists(made)) return;
        AddDefaultLibrary();
        var doc = CbReader.Read(System.Xml.Linq.XDocument.Load(mine));
        // As the program does before it generates: values in the Default state come from style, part and library.
        foreach (var op in doc.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(doc, op);
        var paths = doc.Parts.SelectMany(p => p.Operations).ToDictionary(op => op, op => ToolpathGenerator.Generate(doc, op));

        var ours = GCodePoster.Post(doc, paths, new PostProcessorDefinition()).Text.Replace("\r\n", "\n").Split('\n');

        // Tool changes, spindle lines and the names of the parts (the operations have none), not the header with its date.
        static string[] Lines(IEnumerable<string> lines) => [.. lines.Where(l =>
            l == "M3" || l.StartsWith("M3 ", StringComparison.Ordinal) || l.StartsWith('S') || l.EndsWith(" M6", StringComparison.Ordinal) ||
            System.Text.RegularExpressions.Regex.IsMatch(l, @"^\( [A-J] "))];
        Assert.Equal(Lines(File.ReadAllLines(made)), Lines(ours));
    }

    [Fact]
    public void Tools_of_the_machining_options_as_in_the_comparison_except_for_the_number_on_the_part()
    {
        var mine = Path.Combine(Examples, "Vergleich Werkzeug Machining.cb");
        if (!File.Exists(mine)) return;
        AddDefaultLibrary();
        var doc = CbReader.Read(System.Xml.Linq.XDocument.Load(mine));
        foreach (var op in doc.Parts.SelectMany(p => p.Operations)) LibraryFiles.ApplyStyle(doc, op);

        // CamBam: T3 3 mm, T2 3 mm (the diameter of T3 for T2, on purpose not followed), T1 1 mm.
        Assert.Equal([3, 2, 1], doc.Parts.Select(p => p.Operations[0].ToolNumber.Value));
        Assert.Equal([3.0, 2.0, 1.0], doc.Parts.Select(p => p.Operations[0].EffectiveToolDiameter(doc)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Operations_that_need_a_diameter_stop_with_a_warning(bool pocket)
    {
        var (doc, part, profile) = NewOperation();
        MachineOp op = profile;
        if (pocket)
        {
            var p = new PocketOp { Name = "pocket" };
            p.PrimitiveIds.AddRange(profile.PrimitiveIds);
            p.TargetDepth.Set(-1);
            part.Operations.Add(p);
            op = p;
        }
        else profile.CutWidth.Set(10);

        var tp = ToolpathGenerator.Generate(doc, op);

        Assert.Empty(tp.Moves);
        Assert.Contains(tp.Warnings, w => w.Contains("tool diameter"));
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
