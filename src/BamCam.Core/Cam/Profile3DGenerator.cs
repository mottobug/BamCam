using BamCam.Core.Geometry;
using BamCam.Core.Localization;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

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

        var triangles = CollectTriangles(ctx, op, op.PrimitiveIds).ToList();
        if (triangles.Count == 0)
        {
            ctx.Warnings.Add(Tr.F("{0}: 3D profiles need surface (mesh) objects.", op.Name));
            ctx.Finish(tp);
            return;
        }
        triangles = TransformForSide(op, triangles);

        var dia = ctx.ToolDiameter;
        var mesh = new Mesh(triangles, Math.Max(dia, 1));
        var boundary = Boundary(ctx, op, mesh);
        if (boundary.Count == 0)
        {
            ctx.Warnings.Add(Tr.F("{0}: the machining boundary is empty.", op.Name));
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

    /// <summary>Back face: rotate 180° about the flip axis with BackFaceZeroZ becoming Z=0. Mold: invert Z within the model's range.</summary>
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
                var shapes = new List<Path2>(Silhouette(tris));
                foreach (var id in op.BoundaryShapes)
                    if (ctx.Doc.FindEntity(id) is { } e and not SurfaceEntity)
                        shapes.AddRange(e.ToPaths().Where(p => p.Closed));
                region = Polygons.Union(shapes);
                break;
            }
            default:
                region = Silhouette(mesh.Triangles);
                break;
        }
        if (Math.Abs(op.BoundaryMargin.Value) > 1e-9) region = Polygons.Offset(region, op.BoundaryMargin.Value);

        var min = op.ClipAreaMin.Value;
        var max = op.ClipAreaMax.Value;
        if (!min.AlmostEquals(max) && max.X > min.X && max.Y > min.Y)
            region = Polygons.Intersection(region, [Path2.FromPoints([min, new(max.X, min.Y), max, new(min.X, max.Y)], true)]);
        return region;
    }

    // Projected triangles are oriented CCW so overlapping up- and down-facing faces don't cancel in the union.
    private static List<Path2> Silhouette(IEnumerable<Triangle> tris) =>
        Polygons.Union(tris.Select(t => Path2.FromPoints([t.A.XY, t.B.XY, t.C.XY], true))
            .Where(p => Math.Abs(p.SignedArea()) > 1e-10)
            .Select(p => Polygons.EnsureOrientation(p, ccw: true)));

    private static CutterShape Shape(MachineOp op) =>
        op.ToolProfile.Value is ToolProfile.BallNose or ToolProfile.BullNose ? CutterShape.Ball : CutterShape.Flat;

    // ------------------------------------------------------------------ scanlines

    private static void Scanlines(GenContext ctx, Profile3DOp op, Mesh mesh, List<Path2> boundary, MoveBuilder mb, bool vertical)
    {
        var dia = ctx.ToolDiameter;
        var r = dia / 2 + Math.Max(op.RoughingClearance.Value, 0);
        var clearance = op.RoughingClearance.Value;
        var shape = Shape(op);
        var step = Math.Max(op.StepOver.Value, 0.01) * dia;
        var res = Math.Max(op.Resolution.Value, 0.01) * dia;
        var bb = boundary.Aggregate(BoundingBox.Empty, (b, p) => b.Union(p.Bounds()));
        var flat = boundary.Select(p => p.Flatten()).ToList();

        // Axis mapping: rows run along "u", stepping along "v".
        double U(Point2 p) => vertical ? p.Y : p.X;
        double V(Point2 p) => vertical ? p.X : p.Y;
        Point2 P(double u, double v) => vertical ? new Point2(v, u) : new Point2(u, v);
        var (u0, u1) = (U(bb.Min.XY), U(bb.Max.XY));
        var (v0, v1) = (V(bb.Min.XY), V(bb.Max.XY));

        var corner = op.StartCorner.Value;
        var vAscending = corner is StartCorner.BottomLeft or StartCorner.BottomRight;
        var uAscending = corner is StartCorner.BottomLeft or StartCorner.TopLeft;
        if (vertical) (vAscending, uAscending) = (uAscending, vAscending);

        var rows = new List<double>();
        for (var v = v0 + step / 2; v <= v1 + 1e-9; v += step) rows.Add(v);
        if (rows.Count == 0) rows.Add((v0 + v1) / 2);
        if (!vAscending) rows.Reverse();

        var maxSlope = op.ScanlineGradientThreshold.Value is > 0 and < 90 ? Math.Tan(Angles.ToRadians(op.ScanlineGradientThreshold.Value)) : double.PositiveInfinity;

        // Sample every row once: list of segments of (point, surface z).
        var rowSegments = new List<List<List<Point3>>>();
        var forward = uAscending;
        foreach (var v in rows)
        {
            var segments = new List<List<Point3>>();
            List<Point3>? current = null;
            var n = Math.Max(1, (int)Math.Ceiling((u1 - u0) / res));
            for (var i = 0; i <= n; i++)
            {
                var u = forward ? u0 + (u1 - u0) * i / n : u1 - (u1 - u0) * i / n;
                var p = P(u, v);
                if (!Inside(flat, p))
                {
                    current = null;
                    continue;
                }
                var z = mesh.DropCutter(p.X, p.Y, r, shape) is { } dz ? dz + clearance : double.NegativeInfinity;
                z = Math.Max(z, ctx.TargetDepth);
                if (current is { Count: > 0 } && Math.Abs(z - current[^1].Z) / res > maxSlope)
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
            rowSegments.Add(segments.Where(s => s.Count > 1).ToList());
            forward = !forward;
        }

        var levels = op.RoughingFinishing.Value == RoughingFinishing.Roughing && op.DepthIncrement.Value > 1e-9
            ? ctx.Levels(op)
            : [double.NegativeInfinity];
        var linkDist = step * 1.5 + res;
        var prev = ctx.StockSurface;
        foreach (var level in levels)
        {
            foreach (var segments in rowSegments)
            foreach (var seg in segments)
            {
                var pts = Simplify(seg.Select(p => p with { Z = Math.Max(p.Z, level) }).ToList(), 0.002);
                if (mb.Position is { } pos && Point2.Distance(pos.XY, pts[0].XY) <= linkDist && Math.Abs(pos.Z - pts[0].Z) < dia)
                {
                    mb.Line(pts[0], ctx.CutFeed);
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

    /// <summary>Drops points that lie on the straight line between their neighbours.</summary>
    private static List<Point3> Simplify(List<Point3> pts, double tol)
    {
        if (pts.Count < 3) return pts;
        var result = new List<Point3> { pts[0] };
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var a = result[^1];
            var b = pts[i + 1];
            var ab = b - a;
            var len = ab.Length;
            if (len < 1e-12) continue;
            var dist = Point3.Cross(pts[i] - a, ab).Length / len;
            if (dist > tol) result.Add(pts[i]);
        }
        result.Add(pts[^1]);
        return result;
    }

    // ------------------------------------------------------------------ waterlines

    private static List<double> WaterLevels(GenContext ctx, Profile3DOp op, Mesh mesh)
    {
        var levels = ctx.Levels(op);
        // Below the model's lowest point every level would be identical: stop at target depth.
        return levels.Where(z => z >= ctx.TargetDepth - 1e-9).ToList();
    }

    /// <summary>Area blocked for the tool centre at <paramref name="z"/>: model above z grown by radius + clearance.</summary>
    private static List<Path2> Blocked(Mesh mesh, double z, double grow) =>
        Polygons.Offset(Polygons.Union(mesh.SliceAbove(z)), grow);

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
        foreach (var z in WaterLevels(ctx, op, mesh))
        {
            var free = Polygons.Difference(TaperedBoundary(ctx, op, boundary, z), Blocked(mesh, z, grow));
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
    }

    private static void WaterlineFinish(GenContext ctx, Profile3DOp op, Mesh mesh, List<Path2> boundary, MoveBuilder mb)
    {
        var dia = ctx.ToolDiameter;
        var grow = dia / 2 + op.RoughingClearance.Value;
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var cutter = new LoopCutter(ctx, mb) { LeadIn = op.LeadInMove.Value };
        var prev = ctx.StockSurface;
        foreach (var z in WaterLevels(ctx, op, mesh))
        {
            var bound = TaperedBoundary(ctx, op, boundary, z);
            // Contours around the model (outer walls: CCW for conventional with a CW spindle).
            foreach (var path in Blocked(mesh, z, grow))
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
