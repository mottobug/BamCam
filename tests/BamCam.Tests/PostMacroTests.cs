using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.Core.Model;
using BamCam.Core.Post;

namespace BamCam.Tests;

public class PostMacroTests
{
    /// <summary>Two operations with the given spindle speeds, posted with "M3 {$s}" as the text of the spindle.</summary>
    private static List<string> SpindleLines(double firstSpeed, double secondSpeed)
    {
        var doc = new CadDocument();
        var shape = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 30), new(0, 30)], closed: true)));
        var toolpaths = new Dictionary<MachineOp, Toolpath>();
        foreach (var speed in new[] { firstSpeed, secondSpeed })
        {
            var op = new ProfileOp { Name = "op" + speed };
            op.PrimitiveIds.Add(shape.Id);
            op.ToolDiameter.Set(4);
            op.TargetDepth.Set(-1);
            op.SpindleSpeed.Set(speed);
            doc.ActivePart.Operations.Add(op);
            toolpaths[op] = ToolpathGenerator.Generate(doc, op);
        }
        var defaults = new PostProcessorDefinition();
        var post = new PostProcessorDefinition { SpindleCW = "M3 {$s}", MOP = defaults.MOP.Replace("{$spindle} {$s}", "{$spindle}") };

        var result = GCodePoster.Post(doc, toolpaths, post);

        Assert.DoesNotContain("{$", result.Text);
        return result.Text.Split("\r\n").Where(l => l.StartsWith("M3 ", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Macros_in_the_spindle_text_are_expanded()
    {
        Assert.Equal(["M3 S20000"], SpindleLines(20000, 20000));
    }

    [Fact]
    public void Spindle_text_with_the_speed_is_written_again_for_a_new_speed()
    {
        Assert.Equal(["M3 S20000", "M3 S12000"], SpindleLines(20000, 12000));
    }

    // ---------------------------------------------------------------- issue 3: "{$spindle} {$s}" with "M3 {$s}"

    /// <summary>Operations with the given speeds and tools, posted with the post of the report.</summary>
    private static List<string> Posted(PostProcessorDefinition post, params (double Speed, int Tool)[] operations)
    {
        var doc = new CadDocument();
        var shape = doc.AddEntity(new PolylineEntity(Path2.FromPoints([new(0, 0), new(40, 0), new(40, 30), new(0, 30)], closed: true)));
        var toolpaths = new Dictionary<MachineOp, Toolpath>();
        var n = 0;
        foreach (var (speed, tool) in operations)
        {
            var op = new ProfileOp { Name = "Profile" + ++n };
            op.PrimitiveIds.Add(shape.Id);
            op.ToolDiameter.Set(6);
            op.ToolNumber.Set(tool);
            op.TargetDepth.Set(-1);
            op.SpindleSpeed.Set(speed);
            doc.ActivePart.Operations.Add(op);
            toolpaths[op] = ToolpathGenerator.Generate(doc, op);
        }
        var text = GCodePoster.Post(doc, toolpaths, post).Text;
        Assert.DoesNotContain("{$", text);
        return [.. text.Split("\r\n")];
    }

    /// <summary>The spindle lines, with "G4" pauses and operation names to see where they are.</summary>
    private static List<string> Spindle(List<string> lines) =>
        [.. lines.Where(l => l == "M3" || l.StartsWith("M3 ", StringComparison.Ordinal) || l.StartsWith('S') || l.StartsWith("( Profile", StringComparison.Ordinal) || l.Contains("M6"))];

    /// <summary>As in the report: the spindle started in the header, its text with the speed, and "{$spindle} {$s}" per operation.</summary>
    private static PostProcessorDefinition ReportedPost()
    {
        var defaults = new PostProcessorDefinition();
        return new PostProcessorDefinition
        {
            SpindleCW = "M3 {$s}",
            Header = defaults.Header + "\r\n{$spindle}\r\nG4 P5",
            MOP = defaults.MOP.Replace("{$spindle} {$s}", "{$spindle} {$s}\r\nG4 P5"),
        };
    }

    [Fact]
    public void The_spindle_started_in_the_header_gets_the_speed_of_the_first_operation()
    {
        var lines = Spindle(Posted(ReportedPost(), (20000, 9), (20000, 9)));

        Assert.Equal(["T9 M6", "M3 S20000", "( Profile1 )", "S20000", "( Profile2 )", "S20000"], lines);
    }

    [Fact]
    public void A_new_speed_starts_the_spindle_again_with_the_speed_written_once()
    {
        var lines = Spindle(Posted(ReportedPost(), (20000, 9), (12000, 9)));

        Assert.Equal(["T9 M6", "M3 S20000", "( Profile1 )", "S20000", "( Profile2 )", "M3 S12000"], lines);
    }

    [Fact]
    public void Without_the_speed_in_the_spindle_text_nothing_changes()
    {
        // CamBam's default: "{$spindle} {$s}" with "M3": M3 once, then only the speed.
        var lines = Spindle(Posted(new PostProcessorDefinition(), (1000, 2), (1000, 2)));

        Assert.Equal(["T2 M6", "( Profile1 )", "M3 S1000", "( Profile2 )", "S1000"], lines);
    }

    [Fact]
    public void After_a_tool_change_the_spindle_is_started_again_as_in_cambam()
    {
        // "Vergleich Werkzeug.nc", made by CamBam: "T1 M6" is followed by "M3 S1000", not by "S1000".
        var lines = Spindle(Posted(new PostProcessorDefinition(), (1000, 2), (1000, 1), (1000, 1)));

        Assert.Equal(["T2 M6", "( Profile1 )", "M3 S1000", "( Profile2 )", "T1 M6", "M3 S1000", "( Profile3 )", "S1000"], lines);
    }

    [Fact]
    public void A_speed_elsewhere_in_the_operation_stays()
    {
        var defaults = new PostProcessorDefinition();
        var post = new PostProcessorDefinition { SpindleCW = "M3 {$s}", MOP = defaults.MOP.Replace("{$spindle} {$s}", "{$spindle}\r\n( speed {$s} )") };

        var lines = Posted(post, (20000, 9));

        Assert.Contains("M3 S20000", lines);
        Assert.Contains("( speed S20000 )", lines);
    }
}
