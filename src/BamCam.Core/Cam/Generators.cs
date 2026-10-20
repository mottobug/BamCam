using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

internal static class ProfileGenerator
{
    public static void Generate(GenContext ctx, ProfileOp op, Toolpath tp)
    {
        var dia = ctx.ToolDiameter;
        var r = dia / 2;
        var outside = op.InsideOutside.Value == InsideOutside.Outside;
        var sign = outside ? 1 : -1;
        var baseOffset = r + op.RoughingClearance.Value;
        var cutWidth = Math.Max(op.CutWidth.Value, dia);
        var stepOver = Math.Max(op.StepOver.Value, 0.01) * dia;
        var passes = cutWidth > dia + 1e-9 ? (int)Math.Ceiling((cutWidth - dia) / stepOver - 1e-9) + 1 : 1;
        // Conventional milling with a CW spindle: outside profiles travel CCW, inside profiles CW.
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var ccw = outside ^ climb;

        var sources = ctx.SourcePaths().ToList();
        var loops = new List<Loop>();
        for (var pass = 0; pass < passes; pass++)
        {
            var extra = passes == 1 ? 0 : (cutWidth - dia) * (passes - 1 - pass) / (passes - 1);
            var passLoops = new List<Loop>();
            foreach (var (entity, src) in sources)
            {
                if (src.Count < 2) continue;
                var startTarget = op.StartPoint.Value.Point?.XY
                                  ?? (src.Closed ? DefaultStart(src, ccw, sign * (baseOffset + extra)) : src.StartPoint.XY);
                if (!src.Closed)
                {
                    // Open shapes are cut along their centreline.
                    passLoops.Add(new Loop
                    {
                        Points = LoopTools.Clean(src.Flatten(ToolpathGenerator.ChordTolerance)),
                        Closed = false, SourceId = entity.Id, Source = src,
                    });
                    continue;
                }
                var offsets = Polygons.Offset([src], sign * (baseOffset + extra), JoinStyle.Round, ToolpathGenerator.ChordTolerance);
                foreach (var o in offsets)
                {
                    var pts = LoopTools.Clean(o.Flatten());
                    if (pts.Count < 3) continue;
                    var loop = new Loop { Points = pts, SourceId = entity.Id, Source = src, IsOutside = outside }
                        .Oriented(ccw).StartNearest(startTarget);
                    passLoops.Add(loop);
                }
            }

            if (op.CollisionDetection.Value && passLoops.Count(l => l.Closed) > 1)
                passLoops = MergeOverlapping(passLoops, ccw);

            if (op.CornerOvercut.Value)
                passLoops = passLoops.Select(l => l.Closed && l.Source != null ? CornerOvercut(l, r) : l).ToList();
            loops.AddRange(passLoops);
        }

        if (loops.Count == 0)
        {
            ctx.Finish(tp);
            return;
        }

        var startPos = op.StartPoint.Value.Point?.XY ?? loops[0].Start;
        var ordered = LoopTools.OrderLoops(loops, startPos, op.OptimisationMode.Value);
        var levels = ctx.Levels(op);
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);

        var tabsSettings = op.HoldingTabs.Value;
        var tabTop = tabsSettings.TabMethod == TabMethod.None ? double.NegativeInfinity : ctx.TargetDepth + tabsSettings.Height;
        var cutter = new LoopCutter(ctx, mb)
        {
            TabTop = tabTop,
            TabStyle = tabsSettings.TabStyle,
            LeadIn = op.LeadInMove.Value,
            LeadOut = op.LeadOutMove.Value,
            AwayNormal = (loop, t) => AwayNormal(loop, t),
        };
        var tabMap = ordered.ToDictionary(l => l, l => ComputeTabs(op, l, dia, tabsSettings, tp));

        if (op.CutOrdering.Value == CutOrdering.LevelFirst)
        {
            var prev = ctx.StockSurface;
            foreach (var z in levels)
            {
                foreach (var loop in ordered) cutter.Cut(loop, z, prev, tabMap[loop]);
                prev = z;
            }
        }
        else
        {
            foreach (var loop in ordered)
            {
                var prev = ctx.StockSurface;
                foreach (var z in levels)
                {
                    if (!loop.Closed && mb.Position is { } p && !p.XY.AlmostEquals(loop.Start, 1e-7)) mb.Retract();
                    cutter.Cut(loop, z, prev, tabMap[loop]);
                    prev = z;
                }
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>
    /// Default start: the shape's first vertex moved by the offset along the normal of the first
    /// segment in cutting direction (matches the reference output).
    /// </summary>
    private static Point2 DefaultStart(Path2 src, bool ccw, double signedOffset)
    {
        var oriented = (src.SignedArea() > 0) == ccw ? src : src.Reversed();
        var first = oriented.Segments().First();
        var v0 = first.Start.XY;
        Point2 t;
        if (first.IsArc)
        {
            var arc = first.ToArc();
            var radial = (v0 - arc.Center).Normalized();
            t = arc.IsClockwise ? -radial.Perp : radial.Perp;
        }
        else
        {
            t = (first.End.XY - v0).Normalized();
        }
        // Outward normal: right of travel for a CCW path, left for CW.
        var outward = ccw ? new Point2(t.Y, -t.X) : t.Perp;
        return v0 + outward * signedOffset;
    }

    /// <summary>Unions overlapping offset loops so neighbouring shapes don't gouge each other.</summary>
    private static List<Loop> MergeOverlapping(List<Loop> loops, bool ccw)
    {
        var closed = loops.Where(l => l.Closed).ToList();
        var union = Polygons.Union(closed.Select(l => l.ToPath(0)));
        if (union.Count >= closed.Count) return loops;
        var result = loops.Where(l => !l.Closed).ToList();
        foreach (var u in union)
        {
            var pts = LoopTools.Clean(u.Flatten());
            // Keep the source of the original loop the merged outline starts closest to.
            var nearest = closed.OrderBy(l => u.DistanceTo(l.Start)).First();
            result.Add(new Loop { Points = pts, IsOutside = nearest.IsOutside, SourceId = 0 }
                .Oriented(ccw).StartNearest(nearest.Start));
        }
        return result;
    }

    private static Point2 AwayNormal(Loop loop, Point2 tangent)
    {
        // Left normal of travel direction; for a CCW loop that's inward.
        var left = tangent.Perp;
        var ccw = loop.SignedArea() > 0;
        // Away from material: outside profile → outward; inside profile → inward (hole centre).
        var outward = ccw ? -left : left;
        return loop.IsOutside ? outward : -outward;
    }

    /// <summary>Inserts dog-bone style moves into the concave corners of a profile loop.</summary>
    private static Loop CornerOvercut(Loop loop, double r)
    {
        var pts = loop.Points;
        var n = pts.Count;
        var area = loop.SignedArea();
        var result = new List<Point2>(n * 2);
        for (var i = 0; i < n; i++)
        {
            var p = pts[i];
            result.Add(p);
            var d1 = (p - pts[(i - 1 + n) % n]).Normalized();
            var d2 = (pts[(i + 1) % n] - p).Normalized();
            var cross = Point2.Cross(d1, d2);
            var turn = Math.Atan2(cross, Point2.Dot(d1, d2));
            if (Math.Abs(turn) < Angles.ToRadians(10) || Math.Sign(cross) == Math.Sign(area)) continue;
            // Interior angle of the nook between the incoming and outgoing edges.
            var phi = Math.PI - Math.Abs(turn);
            var dir = (d1 - d2).Normalized();
            var dist = r / Math.Sin(phi / 2) - r;
            if (dist <= 1e-6) continue;
            result.Add(p + dir * dist);
            result.Add(p);
        }
        return loop.WithPoints(result);
    }

    private static List<TabInterval> ComputeTabs(ProfileOp op, Loop loop, double dia, HoldingTabSettings s, Toolpath tp)
    {
        var result = new List<TabInterval>();
        if (s.TabMethod == TabMethod.None || !loop.Closed) return result;
        if (s.TabMethod == TabMethod.AutomaticOuter && !loop.IsOutside) return result;
        if (s.TabMethod == TabMethod.AutomaticInner && loop.IsOutside) return result;

        var len = loop.Length;
        var half = (s.Width + dia) / 2;
        var centres = new List<double>();
        if (s.TabMethod == TabMethod.Manual)
        {
            if (loop.Source == null) return result;
            foreach (var tab in op.Tabs.Where(t => t.ParentEntityID == loop.SourceId))
            {
                var p = LoopTools.PointAtFraction(loop.Source, tab.ParametricPoint);
                centres.Add(LoopTools.ProjectDistance(loop.Points, true, p));
                RecordTab(tp, tab.Clone());
            }
        }
        else
        {
            var perimeter = loop.Source?.Length ?? len;
            if (perimeter < s.SizeThreshold) return result;
            var count = s.TabDistance > 0 ? (int)Math.Round(perimeter / s.TabDistance) : s.MinimumTabs;
            count = Math.Clamp(count, s.MinimumTabs, Math.Max(s.MinimumTabs, s.MaximumTabs));
            for (var i = 0; i < count; i++) centres.Add((i + 0.5) * len / count);
            if (loop.Source != null)
                foreach (var c in centres)
                    RecordTab(tp, HoldingTabTools.CreateTab(loop.SourceId, loop.Source, LoopTools.PointAtDistance(loop.Points, c)));
        }

        foreach (var c in centres)
        {
            var a = c - half;
            var b = c + half;
            // Split intervals that wrap around the loop start.
            if (a < 0)
            {
                result.Add(new TabInterval(0, b));
                result.Add(new TabInterval(len + a, len));
            }
            else if (b > len)
            {
                result.Add(new TabInterval(a, len));
                result.Add(new TabInterval(0, b - len));
            }
            else
            {
                result.Add(new TabInterval(a, b));
            }
        }
        return result;
    }

    /// <summary>Keeps the applied tab positions for display (once per source position, across passes).</summary>
    private static void RecordTab(Toolpath tp, HoldingTab tab)
    {
        if (tp.TabPlacements.Any(t => t.ParentEntityID == tab.ParentEntityID && Math.Abs(t.ParametricPoint - tab.ParametricPoint) < 1e-6)) return;
        tp.TabPlacements.Add(tab);
    }
}

internal static class PocketGenerator
{
    public static void Generate(GenContext ctx, PocketOp op, Toolpath tp)
    {
        var dia = ctx.ToolDiameter;
        var r = dia / 2;
        var step = Math.Max(op.StepOver.Value, 0.01) * dia;
        var climb = op.MillingDirection.Value == MillingDirection.Climb;
        var closed = ctx.SourcePaths().Where(s => s.Path.Closed && s.Path.Count >= 2).Select(s => s.Path).ToList();
        if (closed.Count == 0)
        {
            ctx.Warnings.Add($"{op.Name}: pockets need closed shapes.");
            ctx.Finish(tp);
            return;
        }

        var shapes = Polygons.BuildShapes(closed, ToolpathGenerator.ChordTolerance);
        // Each shape → ordered loop sequence (inner rings first, boundary last).
        var sequences = new List<List<Loop>>();
        foreach (var shape in shapes)
        {
            var first = Polygons.InsetShape(shape, r + op.RoughingClearance.Value, ToolpathGenerator.ChordTolerance);
            if (first.Count == 0)
            {
                ctx.Warnings.Add($"{op.Name}: tool is too large for the pocket.");
                continue;
            }
            foreach (var region in first)
            {
                var seq = op.RegionFillStyle.Value is RegionFillStyle.HorizontalHatch or RegionFillStyle.VerticalHatch
                    ? HatchSequence(region, step, op.RegionFillStyle.Value == RegionFillStyle.VerticalHatch, climb)
                    : OffsetSequence(region, step, climb);
                if (seq.Count > 0) sequences.Add(seq);
            }
        }

        var levels = ctx.Levels(op);
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var cutter = new LoopCutter(ctx, mb) { LeadIn = op.LeadInMove.Value };
        var maxCross = op.MaxCrossoverDistance.Value * dia;
        var stepFeed = op.StepoverFeedrate.Value == StepoverFeedrate.CutFeedrate ? ctx.CutFeed : ctx.PlungeFeed;

        void CutSequence(List<Loop> seq, double z, double prev)
        {
            for (var i = 0; i < seq.Count; i++)
            {
                var loop = seq[i];
                if (mb.Position is { } p && loop.Closed) loop = loop.StartNearest(p.XY);
                double? link = null;
                if (i > 0 && mb.Position is { } q && Math.Abs(q.Z - z) < 1e-9 && Point2.Distance(q.XY, loop.Start) <= maxCross + 1e-9)
                    link = stepFeed;
                cutter.Cut(loop, z, prev, [], link);
            }
        }

        var ordered = sequences.OrderBy(s => s[0].Start.X).ToList();
        if (op.CutOrdering.Value == CutOrdering.LevelFirst)
        {
            var prev = ctx.StockSurface;
            foreach (var z in levels)
            {
                foreach (var seq in ordered)
                {
                    CutSequence(seq, z, prev);
                    mb.Retract();
                }
                prev = z;
            }
        }
        else
        {
            foreach (var seq in ordered)
            {
                var prev = ctx.StockSurface;
                foreach (var z in levels)
                {
                    CutSequence(seq, z, prev);
                    // Next level starts at the first (innermost) ring: retract if it's far away.
                    if (mb.Position is { } p && Point2.Distance(p.XY, seq[0].Start) > maxCross) mb.Retract();
                    prev = z;
                }
                mb.Retract();
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>Concentric offsets from the centre outwards; the boundary (first inset) is cut last.</summary>
    private static List<Loop> OffsetSequence(Shape region, double step, bool climb)
    {
        var rings = new List<List<Shape>> { new() { region } };
        var current = new List<Shape> { region };
        for (var guard = 0; guard < 10000 && current.Count > 0; guard++)
        {
            var next = current.SelectMany(s => Polygons.InsetShape(s, step, ToolpathGenerator.ChordTolerance)).ToList();
            if (next.Count == 0) break;
            rings.Add(next);
            current = next;
        }
        rings.Reverse();
        var loops = new List<Loop>();
        foreach (var ring in rings)
        foreach (var shape in ring)
        {
            // Conventional (CW spindle): pocket walls clockwise, island walls counter-clockwise.
            loops.Add(ToLoop(shape.Outer, ccw: climb, outside: false));
            foreach (var h in shape.Holes) loops.Add(ToLoop(h, ccw: !climb, outside: true));
        }
        return loops;
    }

    private static Loop ToLoop(Path2 p, bool ccw, bool outside) =>
        new Loop { Points = LoopTools.Clean(p.Flatten()), IsOutside = outside }.Oriented(ccw);

    /// <summary>Zig-zag hatch lines clipped to the region, followed by a boundary pass.</summary>
    private static List<Loop> HatchSequence(Shape region, double step, bool vertical, bool climb)
    {
        var polys = region.AllPaths().Select(p => p.Flatten()).ToList();
        var bounds = region.Outer.Bounds();
        var loops = new List<Loop>();
        var (min, max) = vertical ? (bounds.Min.X, bounds.Max.X) : (bounds.Min.Y, bounds.Max.Y);
        var flip = false;
        for (var c = min + step / 2; c < max; c += step)
        {
            var xs = new List<double>();
            foreach (var poly in polys)
            {
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    var a = vertical ? new Point2(poly[j].Y, poly[j].X) : poly[j];
                    var b = vertical ? new Point2(poly[i].Y, poly[i].X) : poly[i];
                    if ((a.Y > c) == (b.Y > c)) continue;
                    xs.Add(a.X + (c - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }
            xs.Sort();
            var segs = new List<(double A, double B)>();
            for (var i = 0; i + 1 < xs.Count; i += 2) segs.Add((xs[i], xs[i + 1]));
            if (flip) segs.Reverse();
            foreach (var (a, b) in segs)
            {
                var (s, e) = flip ? (b, a) : (a, b);
                Point2 P(double v) => vertical ? new Point2(c, v) : new Point2(v, c);
                loops.Add(new Loop { Points = [P(s), P(e)], Closed = false });
            }
            flip = !flip;
        }
        loops.Add(ToLoop(region.Outer, ccw: climb, outside: false));
        foreach (var h in region.Holes) loops.Add(ToLoop(h, ccw: !climb, outside: true));
        return loops;
    }
}

internal static class EngraveGenerator
{
    public static void Generate(GenContext ctx, EngraveOp op, Toolpath tp)
    {
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var levels = ctx.Levels(op);
        var paths = ctx.SourcePaths().Select(s => s.Path).Where(p => p.Count >= 2).ToList();
        var start = op.StartPoint.Value.Point?.XY ?? (paths.Count > 0 ? paths[0].StartPoint.XY : Point2.Zero);
        var remaining = new List<Path2>(paths);
        var pos = start;
        while (remaining.Count > 0)
        {
            var next = remaining.OrderBy(p => Point2.Distance(p.StartPoint.XY, pos)).First();
            remaining.Remove(next);
            var prev = ctx.StockSurface;
            foreach (var z in levels)
            {
                // Engrave adds each point's own Z to the depth (follows 3D lines).
                var startPt = next.StartPoint;
                if (mb.Position is { } p && !p.XY.AlmostEquals(startPt.XY, 1e-7)) mb.Retract();
                mb.MoveToAndPlunge(new Point3(startPt.X, startPt.Y, z + startPt.Z), ctx.PlungeFeed, prev);
                foreach (var seg in next.Segments())
                    mb.Segment(seg, ctx.CutFeed, z);
                prev = z;
            }
            mb.Retract();
            pos = next.EndPoint.XY;
        }
        mb.Retract();
        ctx.Finish(tp);
    }
}

internal static class DrillGenerator
{
    public static void Generate(GenContext ctx, DrillOp op, Toolpath tp)
    {
        var holes = new List<(Point2 P, double Diameter)>();
        foreach (var id in op.PrimitiveIds)
        {
            switch (ctx.Doc.FindEntity(id))
            {
                case PointListEntity pl:
                    holes.AddRange(pl.WorldPoints().Select(p => (p.XY, 0.0)));
                    break;
                case CircleEntity c:
                    holes.Add((c.Transform.Transform(c.Center).XY, c.Diameter * c.Transform.ScaleXY));
                    break;
                case { } e:
                    var b = e.Bounds();
                    if (!b.IsEmpty) holes.Add((b.Center.XY, Math.Min(b.Width, b.Height)));
                    break;
                default:
                    ctx.Warnings.Add($"{op.Name}: drawing object {id} not found.");
                    break;
            }
        }

        var mt = op.MOPTransform.Value;
        if (!mt.IsIdentity) holes = holes.Select(h => (mt.Transform(h.P), h.Diameter)).ToList();

        // Nearest-neighbour ordering.
        var ordered = new List<(Point2 P, double Diameter)>();
        var pos = op.StartPoint.Value.Point?.XY ?? (holes.Count > 0 ? holes[0].P : Point2.Zero);
        while (holes.Count > 0)
        {
            var next = op.OptimisationMode.Value == OptimisationMode.None ? holes[0] : holes.OrderBy(h => Point2.Distance(h.P, pos)).First();
            holes.Remove(next);
            ordered.Add(next);
            pos = next.P;
        }

        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var method = op.DrillingMethod.Value;
        foreach (var (p, holeDia) in ordered)
        {
            if (method is DrillingMethod.SpiralMill_CW or DrillingMethod.SpiralMill_CCW)
                SpiralMill(ctx, op, mb, p, op.HoleDiameter.Value > 0 ? op.HoleDiameter.Value : holeDia, method == DrillingMethod.SpiralMill_CW);
            else
            {
                mb.Rapid(p.ToPoint3(ctx.Clearance));
                var retract = op.RetractHeight.State == ParamState.Auto ? ctx.StockSurface + ctx.FastPlunge : op.RetractHeight.Value;
                tp.Add(Move.Drill(p.ToPoint3(ctx.TargetDepth), ctx.PlungeFeed,
                    new DrillCycle(retract, op.PeckDistance.Value, op.Dwell.Value)));
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    private static void SpiralMill(GenContext ctx, DrillOp op, MoveBuilder mb, Point2 c, double holeDia, bool clockwise)
    {
        var radius = (holeDia - ctx.ToolDiameter) / 2;
        if (radius <= 1e-6)
        {
            ctx.Warnings.Add($"{op.Name}: hole diameter must be larger than the tool for spiral milling.");
            return;
        }
        var pitch = op.DepthIncrement.Value > 0 ? op.DepthIncrement.Value : 1;
        var start = c + new Point2(radius, 0);
        var opposite = c - new Point2(radius, 0);
        mb.MoveToAndPlunge(start.ToPoint3(ctx.StockSurface), ctx.PlungeFeed, ctx.StockSurface);
        var z = ctx.StockSurface;
        var feed = ctx.CutFeed;
        while (z > ctx.TargetDepth + 1e-9)
        {
            var zMid = Math.Max(z - pitch / 2, ctx.TargetDepth);
            var zEnd = Math.Max(z - pitch, ctx.TargetDepth);
            mb.Arc(opposite.ToPoint3(zMid), c, clockwise, feed);
            mb.Arc(start.ToPoint3(zEnd), c, clockwise, feed);
            z = zEnd;
        }
        if (op.SpiralFlatBase.Value)
        {
            mb.Arc(opposite.ToPoint3(z), c, clockwise, feed);
            mb.Arc(start.ToPoint3(z), c, clockwise, feed);
        }
        if (op.DrillLeadOut.Value && Math.Abs(op.LeadOutLength.Value) > 1e-9)
        {
            var toward = (c - start).Normalized() * op.LeadOutLength.Value;
            mb.Line((start + toward).ToPoint3(z), feed);
        }
        mb.Retract();
    }
}
