using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>
/// Material left of the turning stock after the turning operations: the stock section (lathe Z range × radius) minus
/// everything the tools sweep. Round-nosed tools (turning, facing) sweep their nose circle along the path, the grooving
/// blade its rectangle, the drill its diameter; threads remove V-shaped grooves (60°). Shown in the drawing
/// (<see cref="Remaining"/>) and, move by move and turned into a solid, in the 3D simulation (<see cref="Moves"/>).
/// </summary>
public static class TurningSimulation
{
    /// <summary>
    /// Where the lathe section lies in the drawing: lathe Z (along the spindle axis) is drawing X − origin X, the radius
    /// is drawing Y − origin Y times <see cref="Side"/>. The stock section is given in these lathe coordinates.
    /// </summary>
    public sealed record Frame(Point2 Origin, double Side, double Radius, Path2 Section);

    /// <summary>One move of a turning toolpath with the areas of the section it removes (lathe coordinates, counter-clockwise).</summary>
    public sealed record TurnMove(MachineOp Op, Move Move, int Index, Point3 End, double Side, IReadOnlyList<Path2> Removed);

    /// <summary>Remaining material in drawing coordinates, mirrored to both sides of the spindle axis; empty without stock.</summary>
    public static List<Path2> Remaining(CadDocument doc, IReadOnlyDictionary<MachineOp, Toolpath> toolpaths)
    {
        var turning = doc.Parts.Where(p => p.Enabled).SelectMany(p => p.Operations)
            .OfType<TurningOp>().Where(op => op.Enabled && toolpaths.ContainsKey(op)).ToList();
        if (FrameOf(doc, turning.Select(op => (op, toolpaths[op])).ToList()) is not { } frame) return [];
        var removed = Moves(doc, frame, turning.Select(op => ((MachineOp)op, toolpaths[op])).ToList()).SelectMany(m => m.Removed).ToList();
        var remaining = removed.Count == 0 ? [frame.Section] : Polygons.Difference([frame.Section], Polygons.Union(removed));
        // Back to the drawing, on both sides of the axis.
        var result = new List<Path2>();
        foreach (var p in remaining)
        {
            result.Add(p.Transformed(Matrix4.Translation(frame.Origin.X, frame.Origin.Y, 0)));
            result.Add(p.Transformed(Matrix4.Scale(1, -1, 1) * Matrix4.Translation(frame.Origin.X, frame.Origin.Y, 0)));
        }
        return result;
    }

    /// <summary>The stock section of the drawing; null without stock or without turning moves.</summary>
    public static Frame? FrameOf(CadDocument doc, IReadOnlyList<(TurningOp Op, Toolpath Toolpath)> jobs)
    {
        var stock = doc.Machining.Stock;
        if (stock.IsEmpty || jobs.Count == 0) return null;
        var origin = doc.Machining.MachiningOrigin.Point?.XY ?? Point2.Zero;
        var radius = Math.Max(Math.Abs(stock.PMax.Y), Math.Max(Math.Abs(stock.PMin.Y), stock.PMax.Z));
        if (radius <= 0 || stock.PMax.X <= stock.PMin.X) return null;
        var section = Path2.FromPoints(
            [new(stock.PMin.X, 0), new(stock.PMax.X, 0), new(stock.PMax.X, radius), new(stock.PMin.X, radius)], true);
        var side = jobs.Select(j => SideOf(j.Toolpath, origin)).FirstOrDefault(s => s != 0);
        return new Frame(origin, side == 0 ? 1 : side, radius, section);
    }

    /// <summary>The side of the axis a toolpath runs on (drawing Y above or below the origin); 0 without moves.</summary>
    private static double SideOf(Toolpath tp, Point2 origin)
    {
        var points = tp.Moves.Where(m => m.IsMotion).Select(m => m.To).ToList();
        if (points.Count == 0) return 0;
        return points.Average(p => p.Y - origin.Y) < 0 ? -1.0 : 1.0;
    }

    /// <summary>All moves of the turning jobs in order, each with the areas it removes.</summary>
    public static List<TurnMove> Moves(CadDocument doc, Frame frame, IReadOnlyList<(MachineOp Op, Toolpath Toolpath)> jobs)
    {
        var result = new List<TurnMove>();
        foreach (var (op, tp) in jobs)
        {
            if (op is not TurningOp) continue;
            var origin = frame.Origin;
            var side = SideOf(tp, origin);
            if (side == 0) continue;
            Point2 Lathe(Point3 p) => new(p.X - origin.X, (p.Y - origin.Y) * side);
            var r = op.EffectiveToolDiameter(doc) / 2;
            var tool = ToolOf(doc, op);
            Point3? pos = tp.StartPosition;
            var index = 0;
            foreach (var m in tp.Moves)
            {
                if (!m.IsMotion && m.Kind is not (MoveKind.ThreadCycle or MoveKind.DrillCycle)) continue;
                index++;
                var single = new Toolpath { StartPosition = pos };
                single.Moves.Add(m);
                var points = new List<Point2>();
                foreach (var s in single.DisplaySegments(0.01).Where(s => s.Kind != MoveKind.Rapid))
                {
                    if (points.Count == 0) points.Add(Lathe(s.From));
                    points.Add(Lathe(s.To));
                }
                var removed = new List<Path2>();
                if (m.Kind == MoveKind.ThreadCycle)
                {
                    if (pos is { } drive) removed.AddRange(ThreadGrooves(Lathe(drive), Lathe(m.To), m.Thread));
                }
                else if (points.Count > 0 && m.Kind != MoveKind.Rapid)
                    removed.AddRange(Swept(op, r, points, frame.Radius + 1, tool));
                result.Add(new TurnMove(op, m, index, m.To, side,
                    removed.Where(p => p.Count >= 3).Select(p => Polygons.EnsureOrientation(p, ccw: true)).ToList()));
                pos = m.To;
            }
        }
        return result;
    }

    /// <summary>The section after one more move.</summary>
    public static List<Path2> Cut(List<Path2> remaining, TurnMove move) =>
        move.Removed.Count == 0 || remaining.Count == 0 ? remaining : Polygons.Difference(remaining, move.Removed);

    /// <summary>The library tool of a turning operation (insert shape, grooving corner radius), or null.</summary>
    private static ToolDefinition? ToolOf(CadDocument doc, MachineOp op) =>
        CamLibraries.FindTool(doc, doc.Parts.FirstOrDefault(p => p.Operations.Contains(op)), op.EffectiveToolNumber(doc), lathe: true);

    private static bool LeftHand(MachineOp op) => op is LatheOp { LatheCutDirection.Value: LatheCutDirection.LeftHand };

    private static bool Internal(MachineOp op) => op is LatheOp { TurningSide.Value: TurningSide.Internal };

    /// <summary>What the tool sweeps along the points of one move (lathe coordinates).</summary>
    private static IEnumerable<Path2> Swept(MachineOp op, double r, List<Point2> points, double top, ToolDefinition? tool)
    {
        switch (op)
        {
            case LatheGrooveOp when tool is { CornerRadius: > 0 }:
            {
                // Blade with rounded corners, its tip (left corner) at the points.
                var low = points.Min(p => p.Y);
                var blade = LatheInsert.GroovingBlade(2 * r, tool.CornerRadius, Math.Max(top - low, 2 * r));
                foreach (var p in Polygons.MinkowskiSum(blade, Path2.FromPoints(points, false), closed: false, 0.005)) yield return p;
                break;
            }
            case LatheGrooveOp:
                // Blade: from the tip (left corner) one tool width towards +Z, upwards from the tip.
                foreach (var (a, b) in points.Zip(points.Skip(1)))
                    yield return Box(Math.Min(a.X, b.X), Math.Max(a.X, b.X) + 2 * r, Math.Min(a.Y, b.Y), top);
                break;
            case not LatheDrillOp when LatheInsert.Outline(tool, r, LeftHand(op), Internal(op)) is { } insert:
            {
                // The whole insert around the nose centre, along the path of the nose centre.
                var toCenter = NoseOffset(op, r);
                var centres = Path2.FromPoints(points.Select(p => p + toCenter), false);
                foreach (var p in Polygons.MinkowskiSum(insert, centres, closed: false, 0.005)) yield return p;
                break;
            }
            case LatheDrillOp:
                if (points.Max(p => p.X) - points.Min(p => p.X) > 1e-9)
                    yield return Box(points.Min(p => p.X), points.Max(p => p.X), 0, r);
                break;
            default:
            {
                var toCenter = NoseOffset(op, r);
                var path = Path2.FromPoints(points.Select(p => p + toCenter), false);
                if (points.Count == 1 || path.Count < 2 || path.Length < 1e-9)
                    yield return Circle(points[0] + toCenter, r);
                else
                    foreach (var p in Polygons.OffsetOpen(path, Math.Max(r, 1e-3), 0.005)) yield return p;
                break;
            }
        }
    }

    /// <summary>
    /// Round nose: the output point is the virtual tip; the nose centre lies one radius above it and one radius against
    /// the cutting direction (right hand: towards +Z). Internal turning: the tip is on the outward side of the nose.
    /// </summary>
    private static Point2 NoseOffset(MachineOp op, double r)
    {
        var dir = op is LatheOp lathe && lathe.LatheCutDirection.Value == LatheCutDirection.LeftHand ? 1.0 : -1.0;
        return new Point2(-dir * r, op is LatheOp { TurningSide.Value: TurningSide.Internal } ? -r : r);
    }

    private static IEnumerable<Path2> ThreadGrooves(Point2 drive, Point2 end, ThreadCycle tc)
    {
        var peak = drive.Y + tc.PeakOffset;
        var inward = tc.PeakOffset < 0 ? -1.0 : 1.0;
        var half = tc.Depth * Math.Tan(Math.PI / 6); // half width of a 60° groove at the peak
        var z0 = Math.Min(end.X, drive.X);
        var z1 = Math.Max(end.X, drive.X);
        if (tc.Pitch <= 0) yield break;
        for (var z = end.X; z <= z1 + 1e-9 && z >= z0 - 1e-9; z += tc.Pitch * Math.Sign(drive.X - end.X))
        {
            yield return Path2.FromPoints([new(z - half, peak), new(z, peak + inward * tc.Depth), new(z + half, peak)], true);
            if (drive.X == end.X) break;
        }
    }

    /// <summary>
    /// The section turned about the spindle axis, as triangles in drawing coordinates (nine floats each): the axis runs
    /// along drawing X through the origin, the cutting plane (angle 0) is the drawing plane on the side of the toolpath.
    /// </summary>
    public static float[] Revolve(Frame frame, IReadOnlyList<Path2> section, int steps = 72)
    {
        var tris = new List<float>();
        var cos = new double[steps + 1];
        var sin = new double[steps + 1];
        for (var k = 0; k <= steps; k++)
        {
            cos[k] = Math.Cos(2 * Math.PI * k / steps);
            sin[k] = Math.Sin(2 * Math.PI * k / steps);
        }
        void Point(double z, double r, int k)
        {
            tris.Add((float)(frame.Origin.X + z));
            tris.Add((float)(frame.Origin.Y + frame.Side * r * cos[k]));
            tris.Add((float)(r * sin[k]));
        }
        foreach (var path in section)
        {
            var pts = Simplify(path.Vertices.Select(v => v.XY).ToList(), 0.002);
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                var ra = Math.Max(0, a.Y);
                var rb = Math.Max(0, b.Y);
                if (ra < 1e-9 && rb < 1e-9) continue; // on the axis
                for (var k = 0; k < steps; k++)
                {
                    if (ra >= 1e-9)
                    {
                        Point(a.X, ra, k); Point(b.X, rb, k); Point(a.X, ra, k + 1);
                    }
                    if (rb >= 1e-9)
                    {
                        Point(a.X, ra, k + 1); Point(b.X, rb, k); Point(b.X, rb, k + 1);
                    }
                }
            }
        }
        return [.. tris];
    }

    /// <summary>A closed loop without the points that lie within the tolerance of the line between their neighbours.</summary>
    private static List<Point2> Simplify(List<Point2> pts, double tolerance)
    {
        if (pts.Count <= 4) return pts;
        var kept = new List<Point2> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            var prev = kept[^1];
            var next = pts[(i + 1) % pts.Count];
            var d = next - prev;
            var len = d.Length;
            var off = len < 1e-12 ? Point2.Distance(pts[i], prev) : Math.Abs(Point2.Cross(d, pts[i] - prev)) / len;
            if (off > tolerance) kept.Add(pts[i]);
        }
        return kept.Count >= 3 ? kept : pts;
    }

    /// <summary>
    /// The outline of the turning tool at the end of a move, as lines in drawing coordinates in the cutting plane: the
    /// nose circle with the insert behind it, the grooving blade, the drill or the 60° thread tool.
    /// </summary>
    public static List<(Point3 From, Point3 To)> ToolLines(CadDocument doc, TurnMove move, Frame frame)
    {
        var op = move.Op;
        var r = Math.Max(op.EffectiveToolDiameter(doc) / 2, 0.2);
        var tip = move.End;
        var side = move.Side;
        var origin = frame.Origin;
        var lines = new List<(Point3, Point3)>();
        Point3 World(Point2 lathe) => new(origin.X + lathe.X, origin.Y + lathe.Y * side, tip.Z);
        var t = new Point2(tip.X - origin.X, (tip.Y - origin.Y) * side);
        void Poly(params Point2[] pts)
        {
            for (var i = 0; i + 1 < pts.Length; i++) lines.Add((World(pts[i]), World(pts[i + 1])));
        }
        var length = Math.Max(6 * r, frame.Radius * 0.6);
        var tool = ToolOf(doc, op);
        void Outline(Path2 shape, Point2 at)
        {
            var pts = shape.Flatten(0.01);
            Poly([.. pts.Select(p => at + p), at + pts[0]]);
        }
        switch (op)
        {
            case LatheGrooveOp when tool is { CornerRadius: > 0 }:
                Outline(LatheInsert.GroovingBlade(2 * r, tool.CornerRadius, length), t);
                break;
            case LatheGrooveOp:
                Poly(t, t + new Point2(2 * r, 0), t + new Point2(2 * r, length), t + new Point2(0, length), t);
                break;
            case not (LatheDrillOp or LatheThreadOp) when LatheInsert.Outline(tool, op.EffectiveToolDiameter(doc) / 2, LeftHand(op), Internal(op)) is { } insert:
                // The real insert in its holder angle, around the nose centre.
                Outline(insert, t + NoseOffset(op, op.EffectiveToolDiameter(doc) / 2));
                break;
            case LatheDrillOp:
                Poly(t + new Point2(0, -r), t + new Point2(length, -r));
                Poly(t + new Point2(0, r), t + new Point2(length, r));
                Poly(t + new Point2(0, -r), t + new Point2(-r * 0.6, 0), t + new Point2(0, r));
                break;
            case LatheThreadOp:
            {
                var half = length * Math.Tan(Math.PI / 6) * 0.3;
                var up = op is LatheThreadOp { ThreadType.Value: ThreadType.Internal } ? -1.0 : 1.0;
                Poly(t, t + new Point2(-half, up * length * 0.3), t + new Point2(-half, up * length), t + new Point2(half, up * length),
                    t + new Point2(half, up * length * 0.3), t);
                break;
            }
            default:
            {
                var c = t + NoseOffset(op, r);
                const int n = 32;
                for (var k = 0; k < n; k++)
                    Poly(c + Point2.FromAngle(2 * Math.PI * k / n, r), c + Point2.FromAngle(2 * Math.PI * (k + 1) / n, r));
                // The insert behind the nose, away from the work: one flank upright, the other leaning back.
                var up = op is LatheOp { TurningSide.Value: TurningSide.Internal } ? -1.0 : 1.0;
                var back = NoseOffset(op, r).X >= 0 ? 1.0 : -1.0;
                Poly(c + new Point2(-back * r, 0), c + new Point2(-back * r, up * length));
                Poly(c + new Point2(back * r, 0), c + new Point2(back * (r + length * 0.4), up * length));
                Poly(c + new Point2(-back * r, up * length), c + new Point2(back * (r + length * 0.4), up * length));
                break;
            }
        }
        return lines;
    }

    private static Path2 Box(double z0, double z1, double x0, double x1) =>
        Path2.FromPoints([new(z0, x0), new(z1, x0), new(z1, x1), new(z0, x1)], true);

    private static Path2 Circle(Point2 c, double r) =>
        Path2.FromPoints(Enumerable.Range(0, 24).Select(i => c + Point2.FromAngle(i * Math.PI / 12, r)), true);
}
