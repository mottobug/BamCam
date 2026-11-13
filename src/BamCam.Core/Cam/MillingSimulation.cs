using BamCam.Core.Geometry;
using BamCam.Core.Cam;
using BamCam.Core.Model;

namespace BamCam.Core.Simulation;

/// <summary>
/// Material of the stock as a height field (Z map): one height per cell of a regular XY grid. Cutting lowers the
/// heights under the tool. <see cref="Target"/> holds the height each operation is meant to reach (NaN where no
/// operation defines one), for showing the material left above it.
/// </summary>
public sealed class HeightField
{
    public HeightField(double x0, double y0, double cell, int nx, int ny, double top, double bottom)
    {
        X0 = x0;
        Y0 = y0;
        Cell = cell;
        Nx = nx;
        Ny = ny;
        Top = top;
        Bottom = bottom;
        Z = new float[nx * ny];
        Target = new float[nx * ny];
        Reset();
        Array.Fill(Target, float.NaN);
    }

    public double X0 { get; }
    public double Y0 { get; }
    public double Cell { get; }
    public int Nx { get; }
    public int Ny { get; }
    public double Top { get; }
    public double Bottom { get; }
    public float[] Z { get; }
    public float[] Target { get; }

    public void Reset() => Array.Fill(Z, (float)Top);

    /// <summary>Centre of cell (i, j).</summary>
    public Point2 CellCentre(int i, int j) => new(X0 + (i + 0.5) * Cell, Y0 + (j + 0.5) * Cell);

    public float this[int i, int j] => Z[j * Nx + i];
}

/// <summary>Cutting part of a tool: height of its surface above the tip at a distance from its axis.</summary>
public sealed class SimTool
{
    public SimTool(double radius, ToolProfile profile, double cornerRadius = 0, double veeAngle = 0, double tipRadius = 0)
    {
        Radius = Math.Max(radius, 1e-3);
        Profile = profile;
        CornerRadius = profile == ToolProfile.BullNose ? Math.Clamp(cornerRadius <= 0 ? Radius / 2 : cornerRadius, 0, Radius) : 0;
        TipRadius = Math.Clamp(tipRadius, 0, Radius);
        VeeTan = Math.Tan(Angles.ToRadians(Math.Clamp(veeAngle <= 0 ? 90 : veeAngle, 1, 179) / 2));
    }

    public double Radius { get; }
    public ToolProfile Profile { get; }
    public double CornerRadius { get; }
    public double TipRadius { get; }
    private double VeeTan { get; }

    public double HeightAt(double d) => Profile switch
    {
        ToolProfile.BallNose => Radius - Math.Sqrt(Math.Max(0, Radius * Radius - d * d)),
        ToolProfile.BullNose when d > Radius - CornerRadius =>
            CornerRadius - Math.Sqrt(Math.Max(0, CornerRadius * CornerRadius - Math.Pow(d - (Radius - CornerRadius), 2))),
        ToolProfile.VCutter => Math.Max(0, d - TipRadius) / VeeTan,
        _ => 0,
    };

    public override string ToString() => $"{Profile} r{Radius:0.###}";
}

/// <summary>Milling simulation on a height field (3-axis: no undercuts).</summary>
public static class MillingSimulation
{
    /// <summary>Operations the simulation shows (milling; not turning, plasma or drag knife).</summary>
    public static bool IsMilling(MachineOp op) => op is not (TurningOp or PlasmaOp or DragKnifeOp);

    /// <summary>Operations the 3D simulation shows: milling on a block, or turning (see <see cref="Cam.TurningSimulation"/>).</summary>
    public static bool CanSimulate(MachineOp op) => IsMilling(op) || op is TurningOp;

    /// <summary>The tool of an operation for the simulation.</summary>
    public static SimTool ToolOf(CadDocument doc, MachineOp op)
    {
        var radius = op.EffectiveToolDiameter(doc) / 2;
        return op switch
        {
            // V-bit: as wide as its cone at the deepest cut.
            VEngraveOp v => new SimTool(v.TipDiameter.Value / 2 + (Math.Abs(v.StockSurface.Value - v.TargetDepth.Value) + 0.5)
                * Math.Tan(Angles.ToRadians(Math.Clamp(v.VAngle.Value, 1, 179) / 2)), ToolProfile.VCutter, veeAngle: v.VAngle.Value, tipRadius: v.TipDiameter.Value / 2),
            Profile3DOp p3 => new SimTool(radius, p3.ToolProfile.Value, p3.CornerRadius.Value),
            _ => new SimTool(radius, op.ToolProfile.Value, veeAngle: ToolVeeAngle(doc, op)),
        };
    }

    private static double ToolVeeAngle(CadDocument doc, MachineOp op)
    {
        var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        return CamLibraries.FindTool(doc, part, op.EffectiveToolNumber(doc)) is { VeeAngle: > 0 } t ? t.VeeAngle : 90;
    }

    /// <summary>Most cells along a side of the height field; a smaller cell size is enlarged to stay within it.</summary>
    public const int MaxCells = 4000;

    /// <summary>
    /// Height field over the stock (or, without stock, over the cut moves plus the tool radius), with cells of
    /// <paramref name="cell"/> (0 = about 600 cells along the longer side). A cell size that would need more than
    /// <see cref="MaxCells"/> cells along a side is enlarged, so that the field still covers everything.
    /// </summary>
    public static HeightField Create(CadDocument doc, IReadOnlyList<(MachineOp Op, Toolpath Toolpath)> jobs, double cell = 0)
    {
        var part = jobs.Select(j => doc.Parts.FirstOrDefault(p => p.Operations.Contains(j.Op))).FirstOrDefault();
        var stock = part is { Stock.IsEmpty: false } ? part.Stock : doc.Machining.Stock;
        double x0, y0, x1, y1, top, bottom;
        var cuts = jobs.SelectMany(j => j.Toolpath.DisplaySegments(0.05).Where(s => s.Kind != MoveKind.Rapid)).ToList();
        var lowest = cuts.Count == 0 ? 0 : cuts.Min(s => Math.Min(s.From.Z, s.To.Z));
        if (!stock.IsEmpty)
        {
            (x0, y0, x1, y1) = (stock.PMin.X, stock.PMin.Y, stock.PMax.X, stock.PMax.Y);
            (top, bottom) = (stock.PMax.Z, Math.Min(stock.PMin.Z, lowest));
        }
        else
        {
            var margin = jobs.Select(j => ToolOf(doc, j.Op).Radius).DefaultIfEmpty(1).Max() * 1.5 + 1;
            if (cuts.Count == 0) (x0, y0, x1, y1) = (0, 0, 10, 10);
            else
            {
                x0 = cuts.Min(s => Math.Min(s.From.X, s.To.X)) - margin;
                y0 = cuts.Min(s => Math.Min(s.From.Y, s.To.Y)) - margin;
                x1 = cuts.Max(s => Math.Max(s.From.X, s.To.X)) + margin;
                y1 = cuts.Max(s => Math.Max(s.From.Y, s.To.Y)) + margin;
            }
            top = jobs.Select(j => j.Op.StockSurface.Value).DefaultIfEmpty(0).Max();
            bottom = Math.Min(lowest, top) - 1;
        }
        var widest = Math.Max(x1 - x0, y1 - y0);
        if (cell <= 0) cell = Math.Max(widest / 600, 0.01);
        // More cells than allowed: larger cells, not a field that ends before the stock does.
        if (widest / cell > MaxCells) cell = widest / MaxCells * (1 + 1e-9);
        var nx = Math.Clamp((int)Math.Ceiling((x1 - x0) / cell), 2, MaxCells);
        var ny = Math.Clamp((int)Math.Ceiling((y1 - y0) / cell), 2, MaxCells);
        return new HeightField(x0, y0, cell, nx, ny, top, bottom);
    }

    /// <summary>
    /// The material as a closed triangle mesh: the cut surface through the centres of the cells, the sides down to the
    /// bottom of the field and the bottom. With more than <paramref name="maxTriangles"/> it takes every n-th cell.
    /// </summary>
    public static SurfaceEntity ToSurface(HeightField f, int maxTriangles = 1_000_000)
    {
        static List<int> Samples(int count, int step)
        {
            var list = new List<int>();
            for (var i = 0; i < count - 1; i += step) list.Add(i);
            list.Add(count - 1);
            return list;
        }
        var step = 1;
        List<int> xs, ys;
        while (true)
        {
            xs = Samples(f.Nx, step);
            ys = Samples(f.Ny, step);
            long ring = 2L * (xs.Count - 1 + ys.Count - 1);
            if (2L * (xs.Count - 1) * (ys.Count - 1) + 3 * ring <= Math.Max(maxTriangles, 100) || step >= Math.Max(f.Nx, f.Ny)) break;
            step++;
        }

        var surface = new SurfaceEntity();
        var (gx, gy) = (xs.Count, ys.Count);
        foreach (var j in ys)
        foreach (var i in xs)
        {
            var c = f.CellCentre(i, j);
            surface.Vertices.Add(new Point3(c.X, c.Y, f[i, j]));
        }
        int Top(int i, int j) => j * gx + i;
        void Face(int a, int b, int c)
        {
            var (p, q, r) = (surface.Vertices[a], surface.Vertices[b], surface.Vertices[c]);
            if (Point3.Cross(q - p, r - p).Length < 1e-12) return; // a side where the cut reached the bottom
            surface.Faces.AddRange([a, b, c]);
        }
        // The cut surface, counter-clockwise seen from above.
        for (var j = 0; j + 1 < gy; j++)
        for (var i = 0; i + 1 < gx; i++)
        {
            Face(Top(i, j), Top(i + 1, j), Top(i + 1, j + 1));
            Face(Top(i, j), Top(i + 1, j + 1), Top(i, j + 1));
        }
        // Its border, counter-clockwise seen from above, and the sides from it down to the bottom.
        var border = new List<int>();
        for (var i = 0; i < gx - 1; i++) border.Add(Top(i, 0));
        for (var j = 0; j < gy - 1; j++) border.Add(Top(gx - 1, j));
        for (var i = gx - 1; i > 0; i--) border.Add(Top(i, gy - 1));
        for (var j = gy - 1; j > 0; j--) border.Add(Top(0, j));
        var below = new int[border.Count];
        for (var k = 0; k < border.Count; k++)
        {
            below[k] = surface.Vertices.Count;
            surface.Vertices.Add(surface.Vertices[border[k]] with { Z = f.Bottom });
        }
        var centre = surface.Vertices.Count;
        var middle = f.CellCentre(0, 0) + (f.CellCentre(f.Nx - 1, f.Ny - 1) - f.CellCentre(0, 0)) * 0.5;
        surface.Vertices.Add(new Point3(middle.X, middle.Y, f.Bottom));
        for (var k = 0; k < border.Count; k++)
        {
            var next = (k + 1) % border.Count;
            Face(border[k], below[k], below[next]);
            Face(border[k], below[next], border[next]);
            Face(centre, below[next], below[k]); // the bottom, facing down
        }
        return surface;
    }

    /// <summary>One straight piece of a move.</summary>
    public readonly record struct Step(Point3 From, Point3 To, SimTool Tool, MachineOp Op);

    /// <summary>
    /// One move of a toolpath (G0, G1, G2/G3, a drilling cycle) as the straight pieces it is cut in; arcs are split
    /// into lines. Rapids are moves too but cut nothing.
    /// </summary>
    public sealed record SimMove(MachineOp Op, Move Move, int Index, Point3 From, IReadOnlyList<Step> Pieces)
    {
        public Point3 End => Pieces.Count > 0 ? Pieces[^1].To : Move.To;
    }

    /// <summary>All moves of the jobs in order (<see cref="SimMove.Index"/> counts within each toolpath, from 1).</summary>
    public static List<SimMove> Moves(CadDocument doc, IReadOnlyList<(MachineOp Op, Toolpath Toolpath)> jobs, double tolerance)
    {
        var moves = new List<SimMove>();
        foreach (var (op, toolpath) in jobs)
        {
            var tool = ToolOf(doc, op);
            Point3? pos = toolpath.StartPosition;
            var index = 0;
            foreach (var m in toolpath.Moves)
            {
                if (!m.IsMotion && m.Kind is not (MoveKind.DrillCycle or MoveKind.ThreadCycle)) continue;
                index++;
                var single = new Toolpath { StartPosition = pos };
                single.Moves.Add(m);
                var pieces = single.DisplaySegments(tolerance)
                    .Where(s => s.Kind != MoveKind.Rapid).Select(s => new Step(s.From, s.To, tool, op)).ToList();
                var start = pos ?? m.To;
                moves.Add(new SimMove(op, m, index, start, pieces));
                pos = m.Kind == MoveKind.DrillCycle ? start with { X = m.To.X, Y = m.To.Y } : m.To;
            }
        }
        return moves;
    }

    /// <summary>Applies one move to the height field.</summary>
    public static void Cut(HeightField f, SimMove move)
    {
        foreach (var piece in move.Pieces) Cut(f, piece);
    }

    /// <summary>Lowers the heights under the tool moving along one straight step.</summary>
    public static void Cut(HeightField f, Step step)
    {
        var tool = step.Tool;
        var r = tool.Radius;
        if (Math.Abs(step.From.Z - step.To.Z) < 1e-9)
        {
            // Level move: every cell is cut by the tool at its nearest point of the move (exact and fast).
            var z0 = step.From.Z;
            if (z0 >= f.Top) return;
            Point2 a = step.From.XY, b = step.To.XY;
            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(a.X, b.X) - r - f.X0) / f.Cell));
            int i1 = Math.Min(f.Nx - 1, (int)Math.Floor((Math.Max(a.X, b.X) + r - f.X0) / f.Cell));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(a.Y, b.Y) - r - f.Y0) / f.Cell));
            int j1 = Math.Min(f.Ny - 1, (int)Math.Floor((Math.Max(a.Y, b.Y) + r - f.Y0) / f.Cell));
            for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
            {
                var d = Path2.DistanceToSegment(new Point2(f.X0 + (i + 0.5) * f.Cell, f.Y0 + (j + 0.5) * f.Cell), a, b);
                if (d > r) continue;
                var z = (float)(z0 + tool.HeightAt(d));
                var idx = j * f.Nx + i;
                if (z < f.Z[idx]) f.Z[idx] = z;
            }
            return;
        }
        var stamp = Stamp(f.Cell, tool);
        var length = Point3.Distance(step.From, step.To);
        var n = Math.Max(1, (int)Math.Ceiling(length / (f.Cell * 0.5)));
        for (var k = 0; k <= n; k++)
        {
            var p = step.From + (step.To - step.From) * ((double)k / n);
            if (p.Z >= f.Top) continue;
            var ci = (int)Math.Floor((p.X - f.X0) / f.Cell);
            var cj = (int)Math.Floor((p.Y - f.Y0) / f.Cell);
            // Offset of the tool axis from the centre of its cell: distances to the cell centres are exact.
            var fx = p.X - (f.X0 + (ci + 0.5) * f.Cell);
            var fy = p.Y - (f.Y0 + (cj + 0.5) * f.Cell);
            foreach (var (di, dj) in stamp)
            {
                int i = ci + di, j = cj + dj;
                if ((uint)i >= (uint)f.Nx || (uint)j >= (uint)f.Ny) continue;
                double dx = di * f.Cell - fx, dy = dj * f.Cell - fy;
                var d2 = dx * dx + dy * dy;
                if (d2 > r * r) continue;
                var z = (float)(p.Z + tool.HeightAt(Math.Sqrt(d2)));
                var idx = j * f.Nx + i;
                if (z < f.Z[idx]) f.Z[idx] = z;
            }
        }
    }

    private static readonly Dictionary<(double, double), List<(int, int)>> Stamps = [];

    /// <summary>Cells the tool may reach, as offsets from the cell of its axis.</summary>
    private static List<(int Di, int Dj)> Stamp(double cell, SimTool tool)
    {
        lock (Stamps)
        {
            if (Stamps.TryGetValue((cell, tool.Radius), out var s)) return s;
            s = [];
            var reach = (int)Math.Ceiling(tool.Radius / cell) + 1;
            for (var di = -reach; di <= reach; di++)
            for (var dj = -reach; dj <= reach; dj++)
                if (Math.Sqrt(di * di + dj * dj) * cell <= tool.Radius + cell) s.Add((di, dj));
            if (Stamps.Count > 64) Stamps.Clear();
            Stamps[(cell, tool.Radius)] = s;
            return s;
        }
    }

    /// <summary>
    /// Heights the operations are meant to reach: pockets their whole area, profiles the band of their cut width,
    /// drill holes their circle, all at target depth; 3D profiles the surface of their meshes. Areas are shrunk by
    /// a cell so that walls are not reported as rest material.
    /// </summary>
    public static void SetTargets(HeightField f, CadDocument doc, IEnumerable<MachineOp> ops)
    {
        Array.Fill(f.Target, float.NaN);
        foreach (var op in ops)
        {
            var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
            var stock = part is { Stock.IsEmpty: false } ? part.Stock : doc.Machining.Stock;
            var paths = op.PrimitiveIds.Select(doc.FindEntity).Where(e => e != null).SelectMany(e => e!.ToPaths())
                .Select(p => op.MOPTransform.Value.IsIdentity ? p : p.Transformed(op.MOPTransform.Value)).ToList();
            var closed = paths.Where(p => p.Closed && p.Count > 1).ToList();
            var dia = op.EffectiveToolDiameter(doc);
            var depth = op is DepthOp d
                ? (d.TargetDepth.State == ParamState.Auto && !stock.IsEmpty ? stock.PMin.Z : d.TargetDepth.Value)
                : double.NaN;
            List<Shape>? region = op switch
            {
                PocketOp or MachineOp { XmlName: "MOPTrochopock" } => Polygons.BuildShapes(closed), // and the pocket of the matmill plugin
                ProfileOp p when closed.Count > 0 => ProfileBand(closed, p, dia),
                DrillOp drill => Polygons.BuildShapes(DrillPoints(paths).Select(c =>
                    Circle(c, (drill.HoleDiameter.Value > 0 ? drill.HoleDiameter.Value : dia) / 2))),
                _ => null,
            };
            if (region != null && !double.IsNaN(depth))
            {
                var inner = region.SelectMany(s => Polygons.InsetShape(s, f.Cell * 1.5)).ToList();
                Fill(f, inner, (float)depth);
            }
            else if (op is Profile3DOp { BackFace.Value: false, Mold.Value: false, Additive.Value: false })
            {
                foreach (var s in op.PrimitiveIds.Select(doc.FindEntity).OfType<SurfaceEntity>()) Surface(f, s, op.MOPTransform.Value);
            }
        }
    }

    private static List<Shape> ProfileBand(List<Path2> closed, ProfileOp op, double dia)
    {
        var width = Math.Max(op.CutWidth.Value, dia);
        var outer = op.InsideOutside.Value == InsideOutside.Outside;
        var moved = Polygons.Offset(closed, outer ? width : -width);
        var band = outer ? Polygons.Difference(moved, closed) : Polygons.Difference(closed, moved);
        return Polygons.BuildShapes(band);
    }

    private static IEnumerable<Point2> DrillPoints(List<Path2> paths)
    {
        foreach (var p in paths)
        {
            if (p.Closed && p.Count > 1) yield return p.Bounds() is var b ? new Point2((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2) : default;
            else foreach (var v in p.Vertices) yield return v.XY;
        }
    }

    private static Path2 Circle(Point2 c, double r) => new([
        new Vertex(new Point3(c.X + r, c.Y, 0), 1), new Vertex(new Point3(c.X - r, c.Y, 0), 1)], true);

    /// <summary>Sets the target of the cells whose centre lies in the shapes (lowest target wins).</summary>
    private static void Fill(HeightField f, List<Shape> shapes, float z)
    {
        foreach (var shape in shapes)
        {
            var b = shape.Outer.Bounds();
            int i0 = Math.Max(0, (int)((b.Min.X - f.X0) / f.Cell)), i1 = Math.Min(f.Nx - 1, (int)((b.Max.X - f.X0) / f.Cell));
            int j0 = Math.Max(0, (int)((b.Min.Y - f.Y0) / f.Cell)), j1 = Math.Min(f.Ny - 1, (int)((b.Max.Y - f.Y0) / f.Cell));
            var loops = new[] { shape.Outer }.Concat(shape.Holes).Select(l => l.Flatten(f.Cell / 4)).ToList();
            for (var j = j0; j <= j1; j++)
            {
                // Crossings of the row through the cell centres.
                var y = f.Y0 + (j + 0.5) * f.Cell;
                var xs = new List<double>();
                foreach (var pts in loops)
                    for (int a = 0, c = pts.Count - 1; a < pts.Count; c = a++)
                        if ((pts[a].Y > y) != (pts[c].Y > y))
                            xs.Add(pts[a].X + (y - pts[a].Y) * (pts[c].X - pts[a].X) / (pts[c].Y - pts[a].Y));
                xs.Sort();
                for (var k = 0; k + 1 < xs.Count; k += 2)
                {
                    var from = Math.Max(i0, (int)Math.Ceiling((xs[k] - f.X0) / f.Cell - 0.5));
                    var to = Math.Min(i1, (int)Math.Floor((xs[k + 1] - f.X0) / f.Cell - 0.5));
                    for (var i = from; i <= to; i++)
                    {
                        var idx = j * f.Nx + i;
                        if (float.IsNaN(f.Target[idx]) || z < f.Target[idx]) f.Target[idx] = z;
                    }
                }
            }
        }
    }

    /// <summary>Target of the cells under a mesh: its highest surface there.</summary>
    /// <summary>The surface as target, moved, turned or mirrored with the transform of the operation, as it is cut.</summary>
    private static void Surface(HeightField f, SurfaceEntity s, Matrix4 transform)
    {
        var v = s.WorldVertices().Select(p => transform.IsIdentity ? p : transform.Transform(p)).ToList();
        var surface = new float[f.Target.Length];
        Array.Fill(surface, float.NaN);
        for (var t = 0; t + 2 < s.Faces.Count; t += 3)
        {
            Point3 a = v[s.Faces[t]], b = v[s.Faces[t + 1]], c = v[s.Faces[t + 2]];
            var det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(det) < 1e-12) continue;
            int i0 = Math.Max(0, (int)((Math.Min(a.X, Math.Min(b.X, c.X)) - f.X0) / f.Cell));
            int i1 = Math.Min(f.Nx - 1, (int)((Math.Max(a.X, Math.Max(b.X, c.X)) - f.X0) / f.Cell));
            int j0 = Math.Max(0, (int)((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - f.Y0) / f.Cell));
            int j1 = Math.Min(f.Ny - 1, (int)((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - f.Y0) / f.Cell));
            for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
            {
                var p = f.CellCentre(i, j);
                var l1 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
                var l2 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
                var l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                var z = (float)(l1 * a.Z + l2 * b.Z + l3 * c.Z);
                var idx = j * f.Nx + i;
                if (float.IsNaN(surface[idx]) || z > surface[idx]) surface[idx] = z;
            }
        }
        for (var idx = 0; idx < surface.Length; idx++)
            if (!float.IsNaN(surface[idx]) && (float.IsNaN(f.Target[idx]) || surface[idx] < f.Target[idx])) f.Target[idx] = surface[idx];
    }
}
