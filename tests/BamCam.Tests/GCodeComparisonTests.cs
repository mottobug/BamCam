using BamCam.Core.Cam;
using BamCam.Core.Post;
using BamCam.IO.Cb;
using Xunit.Abstractions;

namespace BamCam.Tests;

/// <summary>
/// Black-box comparison against G-code that a licensed CamBam installation shipped together with the
/// matching drawing (skipped when not installed).
/// </summary>
public class GCodeComparisonTests(ITestOutputHelper output)
{
    private const string Dir = @"C:\ProgramData\CamBam plus 1.0";

    [Fact]
    public void Snap_box_gcode_matches_reference()
    {
        var cb = Path.Combine(Dir, "samples", "snap-together-box.cb");
        var nc = Path.Combine(Dir, "samples", "snap-together-box.nc");
        if (!File.Exists(cb) || !File.Exists(nc)) return;

        var doc = CbReader.Load(cb);
        var op = doc.Parts[0].Operations[0];
        var tp = ToolpathGenerator.Generate(doc, op);
        Assert.Empty(tp.Warnings);

        // Post format of the 2012 reference: Default post without the newer spindle-speed word.
        var post = new PostProcessorDefinition { MOP = new PostProcessorDefinition().MOP.Replace(" {$s}", "") };
        var result = GCodePoster.Post(doc, new Dictionary<MachineOp, Toolpath> { [op] = tp }, post);

        var expected = File.ReadAllLines(nc);
        var actual = result.Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        // Compare from the first MOP comment on (header contains the date).
        var e = expected.SkipWhile(l => l != "( Profile1 )").ToArray();
        var a = actual.SkipWhile(l => l != "( Profile1 )").ToArray();

        var mismatches = 0;
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<eof>";
            var al = i < a.Length ? a[i] : "<eof>";
            if (el == al) continue;
            if (mismatches++ < 15) output.WriteLine($"{i}: expected '{el}' actual '{al}'");
        }
        output.WriteLine($"expected {e.Length} lines, actual {a.Length} lines, {mismatches} mismatches");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bamcam-snapbox.nc"), result.Text);
        Assert.Equal(0, mismatches);
    }
}
