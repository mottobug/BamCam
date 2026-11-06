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
}
