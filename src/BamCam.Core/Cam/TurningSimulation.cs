using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>
/// Material left of the turning stock after the turning operations, for display: the stock section (lathe Z range ×
/// radius) minus everything the tools sweep. Round-nosed tools (turning, facing) sweep their nose circle along the
/// path, the grooving blade its rectangle, the drill its diameter; threads remove V-shaped grooves (60°).
/// </summary>
public static class TurningSimulation
{
    /// <summary>Remaining material in drawing coordinates, mirrored to both sides of the spindle axis; empty without stock.</summary>
    public static List<Path2> Remaining(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths)
    {
        var stock = doc.Machining.Stock;
        var turning = doc.Parts.Where(p => p.Enabled).SelectMany(p => p.Operations)
            .OfType<TurningOp>().Where(op => op.Enabled && toolpaths.ContainsKey(op)).ToList();
        if (stock.IsEmpty || turning.Count == 0) return [];
        var origin = doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
        var radius = Math.Max(Math.Abs(stock.PMax.Y), Math.Max(Math.Abs(stock.PMin.Y), stock.PMax.Z));
        var section = Path2.FromPoints(
            [new(stock.PMin.X, 0), new(stock.PMax.X, 0), new(stock.PMax.X, radius), new(stock.PMin.X, radius)], true);

        var removed = new List<Path2>();
        foreach (var op in turning)
        {
            var tp = toolpaths[op];
            var segments = tp.DisplaySegments(0.01).ToList();
            var moves = segments.Select(s => s.To).Concat(segments.Select(s => s.From)).ToList();
            if (moves.Count == 0) continue;
            var side = moves.Average(p => p.Y - origin.Y) < 0 ? -1.0 : 1.0;
            Point2 Lathe(Point3 p) => new(p.X - origin.X, (p.Y - origin.Y) * side);
            var r = op.EffectiveToolDiameter(doc) / 2;
            var cuts = Chains(segments.Select(s => (Lathe(s.From), Lathe(s.To), s.Kind)));

            switch (op)
            {
                case LatheGrooveOp:
                {
                    // Blade: from the tip (left corner) one tool width towards +Z, upwards from the tip.
                    var width = 2 * r;
                    foreach (var chain in cuts)
                    foreach (var (a, b) in chain.Zip(chain.Skip(1)))
                        removed.Add(Box(Math.Min(a.X, b.X), Math.Max(a.X, b.X) + width, Math.Min(a.Y, b.Y), radius + 1));
                    break;
                }
                case LatheDrillOp:
                    foreach (var chain in cuts)
                        removed.Add(Box(chain.Min(p => p.X), chain.Max(p => p.X), 0, r));
                    break;
                case LatheThreadOp:
                {
                    var cycle = tp.Moves.FirstOrDefault(m => m.Kind == MoveKind.ThreadCycle);
                    if (cycle.Kind != MoveKind.ThreadCycle) break;
                    var start = tp.Moves.TakeWhile(m => m.Kind != MoveKind.ThreadCycle).Last(m => m.IsMotion).To;
                    var end = Lathe(cycle.To);
                    var drive = Lathe(start);
                    var tc = cycle.Thread;
                    var peak = drive.Y + tc.PeakOffset;
                    var inward = tc.PeakOffset < 0 ? -1.0 : 1.0;
                    var half = tc.Depth * Math.Tan(Math.PI / 6); // half width of a 60° groove at the peak
                    var z0 = Math.Min(end.X, drive.X);
                    var z1 = Math.Max(end.X, drive.X);
                    for (var z = end.X; z <= z1 + 1e-9 && z >= z0 - 1e-9; z += tc.Pitch * Math.Sign(drive.X - end.X))
                        removed.Add(Path2.FromPoints([new(z - half, peak), new(z, peak + inward * tc.Depth), new(z + half, peak)], true));
                    break;
                }
                default:
                {
                    // Round nose: the output point is the virtual tip; the nose centre lies one radius above it and
                    // one radius against the cutting direction (right hand: towards +Z).
                    var dir = op is LatheOp lathe && lathe.LatheCutDirection.Value == LatheCutDirection.LeftHand ? 1.0 : -1.0;
                    var toCenter = new Point2(-dir * r, r);
                    foreach (var chain in cuts)
                    {
                        var path = Path2.FromPoints(chain.Select(p => p + toCenter), false);
                        removed.AddRange(chain.Count == 1 || path.Count < 2
                            ? [Circle(chain[0] + toCenter, r)]
                            : Polygons.OffsetOpen(path, Math.Max(r, 1e-3), 0.005));
                    }
                    break;
                }
            }
        }

        // Same orientation for all removed areas: with the non-zero rule opposite loops would cancel each other.
        var oriented = removed.Where(p => p.Count >= 3).Select(p => Polygons.EnsureOrientation(p, ccw: true)).ToList();
        var remaining = oriented.Count == 0 ? [section] : Polygons.Difference([section], Polygons.Union(oriented));
        // Back to the drawing, on both sides of the axis.
        var result = new List<Path2>();
        foreach (var p in remaining)
        {
            result.Add(p.Transformed(Matrix4.Translation(origin.X, origin.Y, 0)));
            result.Add(p.Transformed(Matrix4.Scale(1, -1, 1) * Matrix4.Translation(origin.X, origin.Y, 0)));
        }
        return result;
    }

    /// <summary>Consecutive cutting (non-rapid) segments joined into point chains.</summary>
    private static List<List<Point2>> Chains(IEnumerable<(Point2 From, Point2 To, MoveKind Kind)> segments)
    {
        var chains = new List<List<Point2>>();
        List<Point2>? current = null;
        foreach (var (from, to, kind) in segments)
        {
            if (kind == MoveKind.Rapid)
            {
                current = null;
                continue;
            }
            if (current == null || !current[^1].AlmostEquals(from, 1e-9))
            {
                current = [from];
                chains.Add(current);
            }
            current.Add(to);
        }
        return chains;
    }

    private static Path2 Box(double z0, double z1, double x0, double x1) =>
        Path2.FromPoints([new(z0, x0), new(z1, x0), new(z1, x1), new(z0, x1)], true);

    private static Path2 Circle(Point2 c, double r) =>
        Path2.FromPoints(Enumerable.Range(0, 24).Select(i => c + Point2.FromAngle(i * Math.PI / 12, r)), true);
}
