using System.Globalization;
using System.Text.RegularExpressions;
using BamCam.Core.Cam;
using BamCam.Core.Geometry;
using BamCam.IO.Cb;

namespace BamCam.Tests;

/// <summary>
/// Scanline 3D profile: row direction and links between rows by milling direction, compared with CamBam's G-code of
/// examples_to_test/Vergleich Zickzack (a ramp; Conventional, Climb and Mixed, Horizontal and Vertical, rows 3 mm apart,
/// within the crossover distance of 0.7 x 6 = 4.2 mm, and two Mixed ones 4.8 mm apart, beyond it).
/// </summary>
public class ScanlineDirectionTests
{
    private static readonly string ExampleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples_to_test"));

    private readonly record struct Step(Point3 To, bool Rapid);

    /// <summary>The moves of every operation in CamBam's G-code.</summary>
    private static Dictionary<string, List<Step>> CamBamMoves()
    {
        var result = new Dictionary<string, List<Step>>();
        List<Step>? moves = null;
        var pos = new Point3(0, 0, 0);
        var rapid = true;
        foreach (var line in File.ReadLines(Path.Combine(ExampleDir, "Vergleich Zickzack.nc")))
        {
            if (Regex.Match(line, @"^\( ((Horizontal|Vertical) .+) \)$") is { Success: true } name)
            {
                result[name.Groups[1].Value] = moves = [];
                continue;
            }
            if (line.StartsWith("G0")) rapid = true;
            else if (line.StartsWith("G1")) rapid = false;
            double Get(char axis, double old) => Regex.Match(line, axis + @"(-?[\d.]+)") is { Success: true } m
                ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : old;
            var next = new Point3(Get('X', pos.X), Get('Y', pos.Y), Get('Z', pos.Z));
            if (next != pos) moves?.Add(new Step(next, rapid));
            pos = next;
        }
        return result;
    }

    private static List<Step> BamCamMoves(Toolpath tp) =>
        tp.Moves.Where(m => m.IsMotion).Select(m => new Step(m.To, m.Kind == MoveKind.Rapid)).ToList();

    /// <summary>
    /// The rows in cutting order with their direction (+1 / -1 along the row), and how many times the tool goes up to the
    /// clearance plane between the first and the last row.
    /// </summary>
    private static (List<int> Directions, int Retracts) Rows(List<Step> moves, bool vertical, double clearance)
    {
        double Along(Point3 p) => vertical ? p.Y : p.X;
        var directions = new List<int>();
        var retracts = 0;
        var retractsBeforeRow = 0;
        Point3? pos = null;
        foreach (var m in moves)
        {
            if (m.Rapid && m.To.Z >= clearance - 1e-6) retracts++;
            if (!m.Rapid && pos is { } p && Math.Abs(Along(m.To) - Along(p)) > 1)
            {
                var dir = Math.Sign(Along(m.To) - Along(p));
                // A row is a run of cuts along it in one direction; a cut the other way is the next row.
                if (directions.Count == 0 || directions[^1] != dir || retracts > retractsBeforeRow)
                {
                    directions.Add(dir);
                    retractsBeforeRow = retracts;
                }
            }
            pos = m.To;
        }
        return (directions, retracts);
    }

    [Fact]
    public void Rows_run_and_link_as_in_CamBam()
    {
        var doc = CbReader.Load(Path.Combine(ExampleDir, "Vergleich Zickzack.cb"));
        var cambam = CamBamMoves();
        var ops = doc.ActivePart.Operations.OfType<Profile3DOp>().ToList();
        Assert.Equal(14, ops.Count);

        foreach (var op in ops)
        {
            var vertical = op.Profile3DMethod.Value == Profile3DMethod.Vertical;
            var expected = Rows(cambam[op.Name], vertical, op.ClearancePlane.Value);
            var actual = Rows(BamCamMoves(ToolpathGenerator.Generate(doc, op)), vertical, op.ClearancePlane.Value);
            var info = $"{op.Name}: CamBam [{string.Join(" ", expected.Directions)}] {expected.Retracts} retracts, " +
                       $"BamCam [{string.Join(" ", actual.Directions)}] {actual.Retracts} retracts";

            static bool Alternating(List<int> d) => d.Zip(d.Skip(1)).All(p => p.First != p.Second);
            if (op.MillingDirection.Value == MillingDirection.Mixed)
            {
                Assert.True(Alternating(expected.Directions), info);
                Assert.True(Alternating(actual.Directions), info);
                // Within the crossover distance the rows are linked, beyond it the tool goes up between them.
                var linked = op.StepOver.Value * op.ToolDiameter.Value <= op.MaxCrossoverDistance.Value * op.ToolDiameter.Value;
                Assert.True(linked ? expected.Retracts <= 3 : expected.Retracts >= expected.Directions.Count, info);
                Assert.True(linked ? actual.Retracts <= 3 : actual.Retracts >= actual.Directions.Count, info);
                if (linked) Assert.True(expected.Directions[0] == actual.Directions[0], info);
            }
            else
            {
                // One way: every row the same direction, the tool goes up after each row.
                Assert.True(expected.Directions.Distinct().Count() == 1, info);
                Assert.True(actual.Directions.Distinct().Count() == 1, info);
                Assert.True(expected.Directions[0] == actual.Directions[0], info);
                Assert.True(actual.Retracts >= actual.Directions.Count, info);
            }
        }
    }
}
