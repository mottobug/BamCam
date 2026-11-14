using System.Globalization;
using System.Text.RegularExpressions;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>
/// Issue 14: the ten start corners of the scanline 3D profile, compared with CamBam's G-code of
/// examples_to_test/Vergleich Startecke (a ramp, 10 Horizontal and 10 Vertical operations, one per value).
/// </summary>
public class StartCornerTests
{
    private static readonly string ExampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    /// <summary>The cutting passes (XY start and end of each run between rapids) of every operation in CamBam's G-code.</summary>
    private static Dictionary<string, List<(Point2 From, Point2 To)>> CamBamPasses()
    {
        var result = new Dictionary<string, List<(Point2, Point2)>>();
        List<(Point2, Point2)>? passes = null;
        Point2 pos = default;
        Point2? start = null;
        void Flush()
        {
            if (start is { } s && passes != null && !s.AlmostEquals(pos, 1e-9)) passes.Add((s, pos));
            start = null;
        }
        foreach (var line in File.ReadLines(Path.Combine(ExampleDir, "Vergleich Startecke.nc")))
        {
            if (Regex.Match(line, @"^\( ((Horizontal|Vertical) \d+) \)") is { Success: true } name)
            {
                Flush();
                result[name.Groups[1].Value] = passes = [];
                continue;
            }
            double Get(char axis, double old) => Regex.Match(line, axis + @"(-?[\d.]+)") is { Success: true } m
                ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : old;
            var next = new Point2(Get('X', pos.X), Get('Y', pos.Y));
            if (line.StartsWith("G0")) Flush();
            else if (!next.AlmostEquals(pos, 1e-9)) start ??= pos;
            pos = next;
        }
        Flush();
        return result;
    }

    /// <summary>First cutting row: on which side of the area it lies and which way it runs.</summary>
    private static (bool HighSide, bool Ascending) FirstRow(IReadOnlyList<(Point2 From, Point2 To)> passes, bool vertical)
    {
        double Across(Point2 p) => vertical ? p.X : p.Y;
        double Along(Point2 p) => vertical ? p.Y : p.X;
        var rows = passes.Where(p => Math.Abs(Along(p.To) - Along(p.From)) > 1).ToList();
        var first = rows[0];
        var mid = rows.Average(p => Across(p.From));
        return (Across(first.From) > mid, Along(first.To) > Along(first.From));
    }

    /// <summary>The feed moves of a BamCam toolpath (BamCam links the rows, so a run between rapids holds several).</summary>
    private static List<(Point2 From, Point2 To)> Passes(Toolpath tp)
    {
        var passes = new List<(Point2, Point2)>();
        Point2? pos = null;
        foreach (var m in tp.Moves.Where(m => m.IsMotion))
        {
            if (m.Kind != MoveKind.Rapid && pos is { } p && !m.To.XY.AlmostEquals(p, 1e-9)) passes.Add((p, m.To.XY));
            pos = m.To.XY;
        }
        return passes;
    }

    [Fact]
    public void All_ten_start_corners_start_where_CamBam_starts()
    {
        var doc = CbReader.Load(Path.Combine(ExampleDir, "Vergleich Startecke.cb"));
        var cambam = CamBamPasses();
        var ops = doc.ActivePart.Operations.OfType<Profile3DOp>().ToList();
        Assert.Equal(20, ops.Count);
        Assert.Equal(Enum.GetValues<StartCorner>().Length, ops.Select(o => o.StartCorner.Value).Distinct().Count());

        foreach (var op in ops)
        {
            Assert.Empty(op.UnknownValues());
            var vertical = op.Profile3DMethod.Value == Profile3DMethod.Vertical;
            var expected = FirstRow(cambam[op.Name], vertical);
            var actual = FirstRow(Passes(ToolpathGenerator.Generate(doc, op)), vertical);
            Assert.True(expected == actual, $"{op.Name} ({op.StartCorner.Value}): CamBam {expected}, BamCam {actual}");
        }
    }

    [Fact]
    public void The_values_are_read_and_written_with_CamBams_names()
    {
        var doc = CbReader.Load(Path.Combine(ExampleDir, "Vergleich Startecke.cb"));
        var xml = CbWriter.ToString(doc);
        foreach (var name in new[] { "Bottom", "Top", "TopBottom", "Left", "BottomLeft", "TopLeft", "Right", "BottomRight", "TopRight", "LeftRight" })
            Assert.Contains($"<StartCorner state=\"Value\">{name}</StartCorner>", xml);
    }
}
