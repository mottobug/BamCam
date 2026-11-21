using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;
using BamCam.Core.Simulation;

namespace BamCam.Core.Cam;

/// <summary>
/// One water level of a 3D roughing: its Z, the level above it (or the stock surface) and the areas to clear there,
/// as pocket outlines (see <see cref="ToolpathContext.WaterlineAreas"/>).
/// </summary>
public sealed record WaterlineLevel(double Z, double Top, IReadOnlyList<Shape> Areas);

internal static class Profile3DGenerator
{
    public static void Generate(GenContext ctx, Profile3DOp op, Toolpath tp)
    {
        if (op.Additive.Value)
        {
            // Additive (bottom-up extrusion) is a different kind of toolpath; don't silently substitute a mill path.
            ctx.Warnings.Add(Tr.F("{0}: additive (experimental) toolpaths are not supported; no toolpath generated.", op.Name));
            ctx.Finish(tp);
            return;
        }

        if (Prepare(ctx, op, out var problem) is not var (mesh, boundary))
        {
            ctx.Warnings.Add(problem!);
            ctx.Finish(tp);
            return;
        }

        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        switch (op.Profile3DMethod.Value)
        {
            case Profile3DMethod.Horizontal:
            case Profile3DMethod.Vertical:
                Scanlines(ctx, op, mesh, boundary, mb, op.Profile3DMethod.Value == Profile3DMethod.Vertical);
                break;
            case Profile3DMethod.WaterlineRough:
                WaterlineRough(ctx, op, mesh, boundary, mb);
                break;
            case Profile3DMethod.WaterlineFinish:
                WaterlineFinish(ctx, op, mesh, boundary, mb);
                break;
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>The surface mesh (back face and mold applied) and the boundary of the tool centre, or the reason why not.</summary>
    private static (Mesh Mesh, List<Path2> Boundary)? Prepare(GenContext ctx, Profile3DOp op, out string? problem)
    {
        problem = null;
        var triangles = CollectTriangles(ctx, op, op.PrimitiveIds).ToList();
        if (triangles.Count == 0)
        {
            problem = Tr.F("{0}: 3D profiles need surface (mesh) objects.", op.Name);
            return null;
        }
        triangles = TransformForSide(op, triangles);

        var mesh = new Mesh(triangles, Math.Max(ctx.ToolDiameter, 1));
        ctx.Cancel.ThrowIfCancellationRequested();
        var boundary = Boundary(ctx, op, mesh);
        ctx.Cancel.ThrowIfCancellationRequested();
        if (boundary.Count == 0)
        {
            problem = Tr.F("{0}: the machining boundary is empty.", op.Name);
            return null;
        }
        return (mesh, boundary);
    }

    /// <summary>
    /// The areas a roughing tool may clear at each water level, as for Waterline Rough: within the (tapered) boundary,
    /// away from the model above the level by the tool radius plus Roughing Clearance. They are pocket outlines: a tool
    /// centre a tool radius inside them stays clear of the model. Null with a reason when there is nothing to machine.
    /// </summary>
    internal static List<WaterlineLevel>? WaterlineAreas(GenContext ctx, Profile3DOp op, out string? problem)
    {
        if (Prepare(ctx, op, out problem) is not var (mesh, boundary)) return null;
        // The outlines of a faceted model have very many points, which makes a medial axis slow: they are simplified by
        // up to this much, and the model is kept that much further away, so that the simplification cannot cut into it.
        var simplify = 0.02 * ctx.ToolDiameter;
        var grow = ctx.ToolRadius + op.RoughingClearance.Value + simplify;
        var silhouettes = new MeshSilhouettes(mesh, ctx.Cancel);
        var rest = RestMachining.For(ctx, op);
        var levels = new List<WaterlineLevel>();
        var top = ctx.StockSurface;
        foreach (var z in WaterLevels(ctx, op, mesh))
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var centre = Polygons.Difference(TaperedBoundary(ctx, op, boundary, z), Polygons.Offset(silhouettes.Above(z), grow));
            if (rest != null) centre = rest.Only(centre, z);
            levels.Add(new WaterlineLevel(z, top, Polygons.BuildShapes(Polygons.Offset(centre, ctx.ToolRadius, simplify: simplify))));
            top = z;
        }
        if (rest != null && levels.All(l => l.Areas.Count == 0)) ctx.Warnings.Add(RestDone(op));
        return levels;
    }

    private static string RestDone(Profile3DOp op) =>
        Tr.F("{0}: '{1}' left no rest material for this tool, so there is nothing to cut.", op.Name, op.RestFromOperation.Value.Trim());

    /// <summary>
    /// Rest machining (Rest From Operation): what the earlier roughing operation cut, taken from its toolpath, so that
    /// also the strips a trochoidal roughing left out count as rest. At a height Z the earlier tool removed the material
    /// within its reach around every cut move at or below Z (a ball nose reaches less wide just above its tip). Its
    /// Roughing Clearance counts as cut: that skin is for the finishing, not for the rest machining.
    /// </summary>
    private sealed class RestMachining
    {
        private readonly GenContext _ctx;
        private readonly List<(double Z, List<Path2> Chains)> _levels;
        private readonly SimTool _tool;
        private readonly double _clearance;
        private readonly Dictionary<(double Z, double R), List<Path2>> _swept = [];

        private RestMachining(GenContext ctx, List<(double, List<Path2>)> levels, SimTool tool, double clearance)
        {
            _ctx = ctx;
            _levels = levels;
            _tool = tool;
            _clearance = clearance;
        }

        [ThreadStatic] private static HashSet<MachineOp>? _inProgress;

        /// <summary>The rest machining of the operation, or null for everything (with a warning when the name is wrong).</summary>
        public static RestMachining? For(GenContext ctx, Profile3DOp op)
        {
            var name = (op.RestFromOperation.Value ?? "").Trim();
            if (name.Length == 0) return null;
            string Warn(string message)
            {
                ctx.Warnings.Add(Tr.F("{0}: Rest From Operation: {1} Everything is cut.", op.Name, message));
                return message;
            }
            var earlier = ctx.Doc.Parts.SelectMany(p => p.Operations).OfType<Profile3DOp>().FirstOrDefault(o => o != op && o.Name == name);
            if (earlier == null)
            {
                Warn(Tr.F("there is no 3D profile named '{0}'.", name));
                return null;
            }
            if (earlier.Profile3DMethod.Value != Profile3DMethod.WaterlineRough)
            {
                Warn(Tr.F("'{0}' is no Waterline Rough.", name));
                return null;
            }
            // Both must cut the model in the same position, or their coordinates do not match.
            if (earlier.BackFace.Value != op.BackFace.Value || earlier.Mold.Value != op.Mold.Value ||
                op.BackFace.Value && (earlier.FlipAxis.Value != op.FlipAxis.Value || Math.Abs(earlier.BackFaceZeroZ.Value - op.BackFaceZeroZ.Value) > 1e-9) ||
                !earlier.MOPTransform.Value.Equals(op.MOPTransform.Value))
            {
                Warn(Tr.F("'{0}' cuts another side or position (Back Face, Mold or Transform differ).", name));
                return null;
            }
            _inProgress ??= [];
            if (!_inProgress.Add(op))
            {
                Warn(Tr.F("'{0}' takes its rest from this operation again.", name));
                return null;
            }
            Toolpath toolpath;
            try
            {
                toolpath = ToolpathGenerator.Generate(ctx.Doc, earlier, ctx.Cancel);
            }
            finally
            {
                _inProgress.Remove(op);
            }
            if (toolpath.Moves.Count == 0)
            {
                Warn(Tr.F("'{0}' has no toolpath.", name));
                return null;
            }

            // The cut moves of the earlier operation as polylines per Z (moves that change Z only lead to the next level).
            var earlierTool = MillingSimulation.ToolOf(ctx.Doc, earlier);
            var tolerance = Math.Max(Tolerance.Chord, 0.02 * earlierTool.Radius);
            var levels = new Dictionary<double, List<Path2>>();
            List<Point2>? chain = null;
            var chainZ = double.NaN;
            void Close()
            {
                if (chain is { Count: >= 2 })
                {
                    if (!levels.TryGetValue(chainZ, out var list)) levels[chainZ] = list = [];
                    list.Add(Path2.FromPoints(chain, closed: false));
                }
                chain = null;
            }
            foreach (var (from, to, kind) in toolpath.DisplaySegments(tolerance))
            {
                var level = kind != MoveKind.Rapid && Math.Abs(from.Z - to.Z) < 1e-6;
                if (!level || chain != null && (Math.Abs(from.Z - chainZ) > 1e-6 || !chain[^1].AlmostEquals(from.XY, 1e-9))) Close();
                if (!level) continue;
                if (chain == null)
                {
                    chain = [from.XY];
                    chainZ = Math.Round(from.Z, 6);
                }
                chain.Add(to.XY);
            }
            Close();
            return new RestMachining(ctx, levels.Select(kv => (kv.Key, kv.Value)).OrderBy(l => l.Item1).ToList(), earlierTool,
                Math.Max(0, earlier.RoughingClearance.Value));
        }

        /// <summary>Width the earlier tool cuts at a height above its tip (its radius once the height reaches its shoulder).</summary>
        private double Reach(double height)
        {
            if (_tool.HeightAt(_tool.Radius) <= height + 1e-9) return _tool.Radius;
            double lo = 0, hi = _tool.Radius;
            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) / 2;
                if (_tool.HeightAt(mid) <= height) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>Where the earlier operation removed the material at height <paramref name="z"/> (plus its clearance).</summary>
        public List<Path2> Cut(double z)
        {
            var parts = new List<Path2>();
            foreach (var (zp, chains) in _levels)
            {
                if (zp > z + 1e-6) break;
                var r = Math.Round(Reach(z - zp) + _clearance, 4);
                if (r <= 1e-6) continue;
                if (!_swept.TryGetValue((zp, r), out var swept))
                    _swept[(zp, r)] = swept = Polygons.OffsetOpen(chains, r, Math.Max(Tolerance.Chord, 0.02 * r));
                parts.AddRange(swept);
                _ctx.Cancel.ThrowIfCancellationRequested();
            }
            return parts.Count == 0 ? [] : Polygons.Union(parts);
        }

        /// <summary>
        /// The tool centre area <paramref name="centre"/> of a level cut down to the places from which the tool reaches
        /// material the earlier operation left. Slivers thinner than 2 % of the tool, left by rounding, do not count.
        /// </summary>
        public List<Path2> Only(List<Path2> centre, double z)
        {
            var r = _ctx.ToolRadius;
            var left = Polygons.Difference(Polygons.Offset(centre, r), Cut(z));
            var sliver = 0.02 * _ctx.ToolDiameter;
            left = Polygons.Offset(Polygons.Offset(left, -sliver), sliver);
            return left.Count == 0 ? [] : Polygons.Intersection(centre, Polygons.Offset(left, r));
        }
    }

    // ------------------------------------------------------------------ input geometry

    private static IEnumerable<Triangle> CollectTriangles(GenContext ctx, MachineOp op, IEnumerable<int> ids)
    {
        var mt = op.MOPTransform.Value;
        foreach (var id in ids)
        {
            if (ctx.Doc.FindEntity(id) is not SurfaceEntity s) continue;
            var v = s.WorldVertices().Select(p => mt.IsIdentity ? p : mt.Transform(p)).ToList();
            for (var i = 0; i + 2 < s.Faces.Count; i += 3)
                yield return new Triangle(v[s.Faces[i]], v[s.Faces[i + 1]], v[s.Faces[i + 2]]);
        }
    }

    /// <summary>Back face: rotate 180Â° about the flip axis with BackFaceZeroZ becoming Z=0. Mold: invert Z within the model's range.</summary>
    private static List<Triangle> TransformForSide(Profile3DOp op, List<Triangle> tris)
    {
        if (op.BackFace.Value)
        {
            var zz = op.BackFaceZeroZ.Value;
            var flipX = op.FlipAxis.Value == FlipAxis.X;
            Point3 F(Point3 p) => flipX ? new Point3(p.X, -p.Y, zz - p.Z) : new Point3(-p.X, p.Y, zz - p.Z);
            tris = tris.Select(t => new Triangle(F(t.A), F(t.B), F(t.C))).ToList();
        }
        if (op.Mold.Value)
        {
            var min = tris.Min(t => t.MinZ);
            var max = tris.Max(t => t.MaxZ);
            Point3 M(Point3 p) => p with { Z = min + max - p.Z };
            tris = tris.Select(t => new Triangle(M(t.A), M(t.C), M(t.B))).ToList();
        }
        return tris;
    }

    /// <summary>Area where the tool centre may move (closed paths, non-zero fill).</summary>
    private static List<Path2> Boundary(GenContext ctx, Profile3DOp op, Mesh mesh)
    {
        List<Path2> region;
        switch (op.BoundaryMethod.Value)
        {
            case BoundaryMethod.BoundingBox:
            {
                var b = mesh.Bounds;
                region = [Path2.FromPoints([b.Min.XY, new(b.Max.X, b.Min.Y), b.Max.XY, new(b.Min.X, b.Max.Y)], true)];
                break;
            }
            case BoundaryMethod.SelectedShapes when op.BoundaryShapes.Count > 0:
            {
                var tris = CollectTriangles(ctx, op, op.BoundaryShapes).ToList();
                if (tris.Count > 0) tris = TransformForSide(op, tris);
                var shapes = new List<Path2>(Silhouette(tris, ctx.Cancel));
                // The boundary moves with the surface: the transform of the operation applies to both (issue 13).
                var mt = op.MOPTransform.Value;
                foreach (var id in op.BoundaryShapes)
                    if (ctx.Doc.FindEntity(id) is { } e and not SurfaceEntity)
                        shapes.AddRange(e.ToPaths().Where(p => p.Closed).Select(p => mt.IsIdentity ? p : Polygons.EnsureOrientation(p.Transformed(mt), ccw: true)));
                region = Polygons.Union(shapes);
                break;
            }
            default:
                region = Silhouette(mesh.Triangles, ctx.Cancel);
                break;
        }
        if (Math.Abs(op.BoundaryMargin.Value) > 1e-9) region = Polygons.Offset(region, op.BoundaryMargin.Value);

        var min = op.ClipAreaMin.Value;
        var max = op.ClipAreaMax.Value;
        if (!min.AlmostEquals(max) && max.X > min.X && max.Y > min.Y)
        {
            // The clip area is drawn around the untransformed surface: it moves with it.
            var clip = Path2.FromPoints([min, new(max.X, min.Y), max, new(min.X, max.Y)], true);
            if (!op.MOPTransform.Value.IsIdentity) clip = Polygons.EnsureOrientation(clip.Transformed(op.MOPTransform.Value), ccw: true);
            region = Polygons.Intersection(region, [clip]);
        }
        return region;
    }

    // Projected triangles are oriented CCW so overlapping up- and down-facing faces don't cancel in the union.
    private static List<Path2> Silhouette(IEnumerable<Triangle> tris, CancellationToken cancel) =>
        Polygons.UnionLarge(tris.Select(t => Path2.FromPoints([t.A.XY, t.B.XY, t.C.XY], true))
            .Where(p => Math.Abs(p.SignedArea()) > 1e-10)
            .Select(p => Polygons.EnsureOrientation(p, ccw: true)), cancel);

    /// <summary>
    /// Cutter for the drop cutter. A bull nose without a corner radius is computed as a ball nose (as CamBam does;
    /// never cuts too deep), with a corner radius exactly.
    /// </summary>
    private static CutterShape Shape(Profile3DOp op) => op.ToolProfile.Value switch
    {
        ToolProfile.BallNose => CutterShape.Ball,
        ToolProfile.BullNose => op.CornerRadius.Value > 0 ? CutterShape.Bull : CutterShape.Ball,
        _ => CutterShape.Flat,
    };

    private static double CornerRadius(GenContext ctx, Profile3DOp op) => Math.Min(op.CornerRadius.Value, ctx.ToolRadius);

    // ------------------------------------------------------------------ scanlines

    private static void Scanlines(GenContext ctx, Profile3DOp op, Mesh mesh, List<Path2> boundary, MoveBuilder mb, bool vertical)
    {
        var dia = ctx.ToolDiameter;
        var r = dia / 2 + Math.Max(op.RoughingClearance.Value, 0);
        var clearance = op.RoughingClearance.Value;
        var shape = Shape(op);
        // Roughing clearance grows the whole cutter, the corner radius included.
        var cornerRadius = shape == CutterShape.Bull ? CornerRadius(ctx, op) + Math.Max(op.RoughingClearance.Value, 0) : 0;
        var step = Math.Max(op.StepOver.Value, 0.01) * dia;
        var res = Math.Max(op.Resolution.Value, 0.01) * dia;
        // Scan Angle (issue 15): the rows are laid out in a grid turned by this angle about the origin; boundary and limits
        // are taken in the turned grid, the surface is sampled at the real points.
        var angle = Angles.ToRadians(op.ScanAngle.Value);
        var (cosA, sinA) = (Math.Cos(angle), Math.Sin(angle));
        Point2 ToGrid(Point2 p) => new(p.X * cosA + p.Y * sinA, -p.X * sinA + p.Y * cosA);
        Point2 ToWorld(Point2 p) => new(p.X * cosA - p.Y * sinA, p.X * sinA + p.Y * cosA);
        var flat = boundary.Select(p => p.Flatten().Select(ToGrid).ToList()).ToList();
        var bb = angle == 0 ? boundary.Aggregate(BoundingBox.Empty, (b, p) => b.Union(p.Bounds()))
            : BoundingBox.FromPoints(flat.SelectMany(p => p).Select(p => p.ToPoint3(0)));

        // Axis mapping: rows run along "u", stepping along "v".
        double U(Point2 p) => vertical ? p.Y : p.X;
        double V(Point2 p) => vertical ? p.X : p.Y;
        Point2 P(double u, double v) => vertical ? new Point2(v, u) : new Point2(u, v);
        var (u0, u1) = (U(bb.Min.XY), U(bb.Max.XY));
        var (v0, v1) = (V(bb.Min.XY), V(bb.Max.XY));

        // As CamBam (compared in examples_to_test/Vergleich Startecke): Horizontal starts at the top for every value with
        // Top and then cuts its first row from right to left, else at the bottom from left to right; Vertical starts at the
        // right for every value with Right and cuts its first column upwards, else at the left downwards.
        var corner = op.StartCorner.Value;
        var top = corner is StartCorner.Top or StartCorner.TopBottom or StartCorner.TopLeft or StartCorner.TopRight;
        var right = corner is StartCorner.Right or StartCorner.LeftRight or StartCorner.BottomRight or StartCorner.TopRight;
        var vAscending = vertical ? !right : !top;
        // Row direction as CamBam (examples_to_test/Vergleich Zickzack): Conventional cuts every row the same way, Climb the
        // other way, with a retract between rows; Mixed goes back and forth, its first row always left to right (Horizontal)
        // or downwards (Vertical), whatever the start corner.
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var mixed = op.MillingDirection.Value == MillingDirection.Mixed;
        var uAscending = mixed ? !vertical : (vertical ? !vAscending : vAscending) ^ climb;

        // Rows a stepover apart, centred, so that at most half a stepover is left on either side (before, they went on from
        // the near side and could stop up to a stepover short of the far side).
        var rowCount = Math.Max(1, (int)Math.Ceiling((v1 - v0) / step - 1e-9));
        var firstRow = (v0 + v1) / 2 - (rowCount - 1) * step / 2;
        var rows = Enumerable.Range(0, rowCount).Select(k => firstRow + k * step).ToList();
        if (!vAscending) rows.Reverse();

        var maxSlope = op.ScanlineGradientThreshold.Value is > 0 and < 90 ? Math.Tan(Angles.ToRadians(op.ScanlineGradientThreshold.Value)) : double.PositiveInfinity;

        // Sample every row once: list of segments of (point, surface z). A row depends on nothing else, so they run in parallel.
        var rowSegments = new List<List<Point3>>[rows.Count];
        Parallel.For(0, rows.Count, new ParallelOptions { CancellationToken = ctx.Cancel }, row =>
        {
            var v = rows[row];
            var forward = mixed ? uAscending ^ (row % 2 == 1) : uAscending;
            var segments = new List<List<Point3>>();
            // The row runs inside the boundary between its crossings with it (even-odd, as Inside): sampled evenly from
            // crossing to crossing, so that it reaches the boundary also where the grid is turned or the boundary is curved.
            var crossings = new List<double>();
            foreach (var pts in flat)
                for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                {
                    double va = V(pts[i]), vb = V(pts[j]);
                    if ((va > v) != (vb > v)) crossings.Add(U(pts[i]) + (v - va) * (U(pts[j]) - U(pts[i])) / (vb - va));
                }
            crossings.Sort();
            var spans = new List<(double From, double To)>();
            for (var k = 0; k + 1 < crossings.Count; k += 2)
                if (crossings[k + 1] - crossings[k] > 1e-9) spans.Add((crossings[k], crossings[k + 1]));
            if (!forward)
            {
                spans.Reverse();
                spans = spans.Select(s => (s.To, s.From)).ToList();
            }
            foreach (var (from, to) in spans)
            {
                List<Point3>? current = null;
                var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / res));
                var spacing = Math.Abs(to - from) / n;
                for (var i = 0; i <= n; i++)
                {
                    var p = ToWorld(P(from + (to - from) * i / n, v));
                    var z = mesh.DropCutter(p.X, p.Y, r, shape, cornerRadius) is { } dz ? dz + clearance : double.NegativeInfinity;
                    z = Math.Max(z, ctx.TargetDepth);
                    if (current is { Count: > 0 } && Math.Abs(z - current[^1].Z) / spacing > maxSlope)
                    {
                        current = null;
                        continue;
                    }
                    if (current == null)
                    {
                        current = [];
                        segments.Add(current);
                    }
                    current.Add(p.ToPoint3(z));
                }
            }
            rowSegments[row] = segments.Where(s => s.Count > 1).ToList();
        });

        var levels = op.RoughingFinishing.Value == RoughingFinishing.Roughing && op.DepthIncrement.Value > 1e-9
            ? ctx.Levels(op)
            : [double.NegativeInfinity];
        // Mixed links the next row without a retract when it starts within the crossover distance, as CamBam: up first if
        // it starts higher, across at the plunge feed, then down. Conventional and Climb always retract.
        var linkDist = mixed ? Math.Max(op.MaxCrossoverDistance.Value, 0) * dia : -1;
        var prev = ctx.StockSurface;
        // 2 µm in the units of the drawing (in inches the same length, not 0.002 inches).
        var tolerance = ctx.Doc.Units == DrawingUnits.Inches ? 0.002 / 25.4 : 0.002;
        foreach (var level in levels)
        {
            foreach (var segments in rowSegments)
            foreach (var seg in segments)
            {
                var pts = Simplify(seg.Select(p => p with { Z = Math.Max(p.Z, level) }).ToList(), tolerance);
                if (mb.Position is { } pos && Point2.Distance(pos.XY, pts[0].XY) <= linkDist + 1e-9)
                {
                    var across = Math.Max(pos.Z, pts[0].Z);
                    if (across > pos.Z) mb.Rapid(pos with { Z = across });
                    mb.Line(pts[0] with { Z = across }, ctx.PlungeFeed);
                    if (pts[0].Z < across) mb.Line(pts[0], ctx.PlungeFeed);
                }
                else
                {
                    mb.Retract();
                    mb.MoveToAndPlunge(pts[0], ctx.PlungeFeed, double.IsNegativeInfinity(level) ? ctx.StockSurface : prev);
                }
                for (var i = 1; i < pts.Count; i++) mb.Line(pts[i], ctx.CutFeed);
            }
            mb.Retract();
            if (!double.IsNegativeInfinity(level)) prev = level;
        }
    }

    private static bool Inside(List<List<Point2>> polys, Point2 p)
    {
        var inside = false;
        foreach (var pts in polys)
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y) &&
                    p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>
    /// Fewer points, where the straight moves between the points kept stay within <paramref name="tol"/> of every point
    /// dropped (Douglas-Peucker). Checking each point only against the line to its next neighbour, as before, dropped
    /// whole curves point by point: in inches the tool then cut up to 0.05 into the surface between the points.
    /// </summary>
    internal static List<Point3> Simplify(List<Point3> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last - first < 2) continue;
            var a = pts[first];
            var ab = pts[last] - a;
            var len = ab.Length;
            var worst = -1.0;
            var worstAt = -1;
            for (var i = first + 1; i < last; i++)
            {
                var d = len < 1e-12 ? (pts[i] - a).Length : Point3.Cross(pts[i] - a, ab).Length / len;
                if (d > worst)
                {
                    worst = d;
                    worstAt = i;
                }
            }
            if (worst <= tol) continue;
            keep[worstAt] = true;
            stack.Push((first, worstAt));
            stack.Push((worstAt, last));
        }
        var result = new List<Point3>();
        for (var i = 0; i < pts.Count; i++)
            if (keep[i]) result.Add(pts[i]);
        return result;
    }

    // ------------------------------------------------------------------ waterlines

    private static List<double> WaterLevels(GenContext ctx, Profile3DOp op, Mesh mesh)
    {
        var levels = ctx.Levels(op);
        // Below the model's lowest point every level would be identical: stop at target depth.
        return levels.Where(z => z >= ctx.TargetDepth - 1e-9).ToList();
    }

    private static List<Path2> TaperedBoundary(GenContext ctx, Profile3DOp op, List<Path2> boundary, double z)
    {
        if (Math.Abs(op.BoundaryTaper.Value) < 1e-9) return boundary;
        var inset = (ctx.StockSurface - z) * Math.Tan(Angles.ToRadians(op.BoundaryTaper.Value));
        return Polygons.Offset(boundary, -inset);
    }

    private static void WaterlineRough(GenContext ctx, Profile3DOp op, Mesh mesh, List<Path2> boundary, MoveBuilder mb)
    {
        var dia = ctx.ToolDiameter;
        var grow = dia / 2 + op.RoughingClearance.Value;
        var step = Math.Max(op.StepOver.Value, 0.01) * dia;
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var cutter = new LoopCutter(ctx, mb) { LeadIn = op.LeadInMove.Value };
        var maxCross = op.MaxCrossoverDistance.Value * dia;
        var stepFeed = op.StepoverFeedrate.Value == StepoverFeedrate.CutFeedrate ? ctx.CutFeed : ctx.PlungeFeed;

        var prev = ctx.StockSurface;
        var silhouettes = new MeshSilhouettes(mesh, ctx.Cancel);
        var rest = RestMachining.For(ctx, op);
        foreach (var z in WaterLevels(ctx, op, mesh))
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var blocked = Polygons.Offset(silhouettes.Above(z), grow); // area blocked for the tool centre
            var free = Polygons.Difference(TaperedBoundary(ctx, op, boundary, z), blocked);
            if (rest != null) free = rest.Only(free, z);
            foreach (var shape in Polygons.BuildShapes(free))
            {
                // Mesh outlines are faceted anyway: a coarse arc tolerance keeps point counts manageable.
                var seq = PocketGenerator.OffsetSequence(shape, step, climb, tolerance: Tolerance.Chord);
                for (var i = 0; i < seq.Count; i++)
                {
                    var loop = seq[i];
                    if (mb.Position is { } p && loop.Closed) loop = loop.StartNearest(p.XY);
                    double? link = null;
                    if (i > 0 && mb.Position is { } q && Math.Abs(q.Z - z) < 1e-9 && Point2.Distance(q.XY, loop.Start) <= maxCross + 1e-9)
                        link = stepFeed;
                    cutter.Cut(loop, z, prev, [], link);
                }
                mb.Retract();
            }
            prev = z;
        }
        if (rest != null && mb.Position == null) ctx.Warnings.Add(RestDone(op));
    }

    private static void WaterlineFinish(GenContext ctx, Profile3DOp op, Mesh mesh, List<Path2> boundary, MoveBuilder mb)
    {
        var dia = ctx.ToolDiameter;
        var grow = dia / 2 + op.RoughingClearance.Value;
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var cutter = new LoopCutter(ctx, mb) { LeadIn = op.LeadInMove.Value };
        var prev = ctx.StockSurface;
        var silhouettes = new MeshSilhouettes(mesh, ctx.Cancel);
        foreach (var z in WaterLevels(ctx, op, mesh))
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var bound = TaperedBoundary(ctx, op, boundary, z);
            // Contours around the model (outer walls: CCW for conventional with a CW spindle).
            foreach (var path in Polygons.Offset(silhouettes.Above(z), grow))
            {
                var pts = LoopTools.Clean(path.Flatten());
                if (pts.Count < 3) continue;
                var loop = new Loop { Points = pts, IsOutside = true };
                var isHole = loop.SignedArea() < 0;
                loop = loop.Oriented(isHole ? climb : !climb);
                // Skip contours entirely outside the boundary.
                if (!pts.Any(p => bound.Any(b => b.Contains(p)))) continue;
                if (mb.Position is { } pos) loop = loop.StartNearest(pos.XY);
                cutter.Cut(loop, z, prev, []);
                mb.Retract();
            }
            prev = z;
        }
    }
}
