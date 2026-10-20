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

        // Raw offsets per (source index, extra distance), filled in one incremental series per source.
        var offsetCache = new Dictionary<(int, double), List<Path2>>();
        void PrepareOffsets(IEnumerable<double> extras)
        {
            var list = extras.Distinct().ToList();
            for (var s = 0; s < sources.Count; s++)
            {
                var src = sources[s].Path;
                if (!src.Closed || src.Count < 2) continue;
                var missing = list.Where(e => !offsetCache.ContainsKey((s, e))).ToList();
                if (missing.Count == 0) continue;
                var series = Polygons.OffsetSeries([src], missing.Select(e => sign * (baseOffset + e)).ToList(), JoinStyle.Round, ToolpathGenerator.ChordTolerance);
                for (var k = 0; k < missing.Count; k++) offsetCache[(s, missing[k])] = series[k];
            }
        }

        // Loops for all source shapes at one offset distance (beyond tool radius + clearance).
        List<Loop> BuildLoops(double extra)
        {
            var passLoops = new List<Loop>();
            for (var si = 0; si < sources.Count; si++)
            {
                var (entity, src) = sources[si];
                if (src.Count < 2) continue;
                var startTarget = op.StartPoint.Value.Point?.XY
                                  ?? (src.Closed ? DefaultStart(src, ccw, sign * (baseOffset + extra)) : src.StartPoint.XY);
                if (!src.Closed)
                {
                    // Open shapes are cut along their centreline.
                    passLoops.Add(new Loop
                    {
                        Points = LoopTools.Clean(src.Flatten(ToolpathGenerator.ChordTolerance)),
                        Closed = false, SourceId = entity.Id, Source = src, SnapSources = [src], SnapOffset = 0,
                    });
                    continue;
                }
                if (!offsetCache.TryGetValue((si, extra), out var offsets))
                    offsets = offsetCache[(si, extra)] = Polygons.Offset([src], sign * (baseOffset + extra), JoinStyle.Round, ToolpathGenerator.ChordTolerance);
                foreach (var o in offsets)
                {
                    var pts = LoopTools.Clean(o.Flatten());
                    if (pts.Count < 3) continue;
                    var loop = LoopTools.StartAt(new Loop
                    {
                        Points = pts, SourceId = entity.Id, Source = src, IsOutside = outside,
                        SnapSources = [src], SnapOffset = baseOffset + extra,
                    }.Oriented(ccw), startTarget);
                    passLoops.Add(loop);
                }
            }

            if (op.CollisionDetection.Value && passLoops.Count(l => l.Closed) > 1)
                passLoops = MergeOverlapping(passLoops, ccw);

            if (op.CornerOvercut.Value)
                passLoops = passLoops.Select(l => l.Closed && l.Source != null ? CornerOvercut(l, r) : l).ToList();
            return passLoops;
        }

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
            AwayNormal = AwayNormal,
        };
        var mixed = op.MillingDirection.Value == MillingDirection.Mixed;

        if (op.SideProfile.Value.Method != SideProfileMethod.None)
        {
            SideProfileCut(ctx, op, tp, mb, cutter, levels, BuildLoops, PrepareOffsets, passes > 1 ? cutWidth - dia : 0, stepOver, mixed);
            mb.Retract();
            ctx.Finish(tp);
            return;
        }

        var passExtras = Enumerable.Range(0, passes).Select(pass => passes == 1 ? 0 : (cutWidth - dia) * (passes - 1 - pass) / (passes - 1)).ToList();
        if (passes > 1) PrepareOffsets(passExtras);
        var loops = new List<Loop>();
        foreach (var extra in passExtras) loops.AddRange(BuildLoops(extra));

        if (loops.Count == 0)
        {
            ctx.Finish(tp);
            return;
        }

        var startPos = op.StartPoint.Value.Point?.XY ?? loops[0].Start;
        var ordered = LoopTools.OrderLoops(loops, startPos, op.OptimisationMode.Value);
        var tabMap = ordered.ToDictionary(l => l, l => ComputeTabs(op, l, dia, tabsSettings, tp));

        if (op.CutOrdering.Value == CutOrdering.LevelFirst)
        {
            var prev = ctx.StockSurface;
            for (var li = 0; li < levels.Count; li++)
            {
                foreach (var loop in ordered) CutDirected(cutter, mb, loop, levels[li], prev, tabMap[loop], mixed && li % 2 == 1);
                prev = levels[li];
            }
        }
        else
        {
            foreach (var loop in ordered)
            {
                var prev = ctx.StockSurface;
                for (var li = 0; li < levels.Count; li++)
                {
                    var z = levels[li];
                    var reverse = mixed && li % 2 == 1;
                    var entry = reverse && !loop.Closed ? loop.End : loop.Start;
                    if (!loop.Closed && mb.Position is { } p && !p.XY.AlmostEquals(entry, 1e-7)) mb.Retract();
                    CutDirected(cutter, mb, loop, z, prev, tabMap[loop], reverse);
                    prev = z;
                }
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>
    /// Cuts a loop, optionally in the opposite direction (Mixed milling). Tab intervals are mirrored to the
    /// reversed arc length. Open paths reversed start at their far end, saving the trip back.
    /// </summary>
    private static void CutDirected(LoopCutter cutter, MoveBuilder mb, Loop loop, double z, double prev,
        IReadOnlyList<TabInterval> tabs, bool reverse)
    {
        if (!reverse)
        {
            cutter.Cut(loop, z, prev, tabs);
            return;
        }
        Loop rev;
        if (loop.Closed) rev = loop.Reversed();
        else
        {
            var pts = new List<Point2>(loop.Points);
            pts.Reverse();
            rev = loop.WithPoints(pts);
        }
        var len = loop.Length;
        cutter.Cut(rev, z, prev, tabs.Select(t => new TabInterval(len - t.End, len - t.Start)).ToList());
    }

    /// <summary>
    /// Extra offset (away from the part) of the profile wall at level <paramref name="z"/> for side profiles.
    /// Slope: Value is the wall angle from vertical (wider offset towards the top for positive values).
    /// Convex radius: rounds over the top edge. Concave radius: a cove at the bottom. The sign of Value flips the effect.
    /// </summary>
    internal static double SideProfileExtra(SideProfile sp, double z, double top, double bottom)
    {
        var sign = Math.Sign(sp.Value);
        var r = Math.Abs(sp.Value);
        switch (sp.Method)
        {
            case SideProfileMethod.Slope:
                return (z - bottom) * Math.Tan(Angles.ToRadians(sp.Value));
            case SideProfileMethod.ConvexRadius:
            {
                var h = top - z;
                return h < r ? -sign * (r - Math.Sqrt(r * r - (r - h) * (r - h))) : 0;
            }
            case SideProfileMethod.ConcaveRadius:
            {
                var u = z - bottom;
                return u < r ? sign * (r - Math.Sqrt(r * r - (r - u) * (r - u))) : 0;
            }
            default:
                return 0;
        }
    }

    /// <summary>
    /// Side profile: the offset changes with depth, so loops are rebuilt per level. With Adjust Cut Width the
    /// stock above the profile is cleared too (extra passes out to the widest offset).
    /// </summary>
    private static void SideProfileCut(GenContext ctx, ProfileOp op, Toolpath tp, MoveBuilder mb, LoopCutter cutter,
        List<double> levels, Func<double, List<Loop>> buildLoops, Action<IEnumerable<double>> prepareOffsets,
        double cutWidthExtra, double stepOver, bool mixed)
    {
        var sp = op.SideProfile.Value;
        var top = ctx.StockSurface;
        var bottom = ctx.TargetDepth;
        var extras = levels.ToDictionary(z => z, z => SideProfileExtra(sp, z, top, bottom));
        var maxExtra = extras.Values.DefaultIfEmpty(0).Max() + cutWidthExtra;
        var dia = ctx.ToolDiameter;
        var tabsSettings = op.HoldingTabs.Value;

        // Offsets cut at each level, far passes first.
        List<double> OffsetsAt(double z)
        {
            var near = extras[z];
            var far = sp.AdjustCutWidth ? maxExtra : near + cutWidthExtra;
            var result = new List<double>();
            if (far - near > 1e-9)
            {
                var n = (int)Math.Ceiling((far - near) / stepOver - 1e-9);
                for (var i = 0; i < n; i++) result.Add(far - (far - near) * i / n);
            }
            result.Add(near);
            return result;
        }

        prepareOffsets(levels.SelectMany(OffsetsAt));
        // Group by source shape for depth-first cutting.
        var perLevel = levels.ToDictionary(z => z, z => OffsetsAt(z).SelectMany(buildLoops).ToList());
        var sourceIds = perLevel.Values.SelectMany(l => l).Select(l => l.SourceId).Distinct().ToList();
        if (op.CutOrdering.Value == CutOrdering.LevelFirst || sourceIds.Count <= 1)
        {
            var prev = ctx.StockSurface;
            for (var li = 0; li < levels.Count; li++)
            {
                var z = levels[li];
                foreach (var loop in perLevel[z])
                    CutDirected(cutter, mb, loop, z, prev, ComputeTabs(op, loop, dia, tabsSettings, tp), mixed && li % 2 == 1);
                mb.Retract();
                prev = z;
            }
            return;
        }
        foreach (var id in sourceIds)
        {
            var prev = ctx.StockSurface;
            for (var li = 0; li < levels.Count; li++)
            {
                var z = levels[li];
                foreach (var loop in perLevel[z].Where(l => l.SourceId == id))
                    CutDirected(cutter, mb, loop, z, prev, ComputeTabs(op, loop, dia, tabsSettings, tp), mixed && li % 2 == 1);
                prev = z;
            }
            mb.Retract();
        }
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
            result.Add(new Loop
            {
                Points = pts, IsOutside = nearest.IsOutside, SourceId = 0,
                SnapSources = closed.Select(l => l.Source).OfType<Path2>().ToList(), SnapOffset = nearest.SnapOffset,
            }.Oriented(ccw).StartNearest(nearest.Start));
        }
        return result;
    }

    internal static Point2 AwayNormal(Loop loop, Point2 tangent)
    {
        // Left normal of travel direction; for a CCW loop that's inward.
        var left = tangent.Perp;
        // Open paths have no inside: leads go to the right of travel (the side a CW spindle cuts conventionally).
        if (!loop.Closed) return -left;
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
            var prevPt = pts[(i - 1 + n) % n];
            var nextPt = pts[(i + 1) % n];
            // Micro segments have meaningless directions; they are not corners.
            if (Point2.Distance(p, prevPt) < 1e-3 || Point2.Distance(p, nextPt) < 1e-3) continue;
            var d1 = (p - prevPt).Normalized();
            var d2 = (nextPt - p).Normalized();
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
        var finishStep = op.FinishStepover.Value > 1e-9 ? op.FinishStepover.Value * dia : 0;
        var finishOnlyAtBottom = op.FinishStepoverAtTargetDepth.Value;
        var style = op.RegionFillStyle.Value;

        // Each region → (normal sequence, sequence with the finish stepover before the wall pass).
        var regions = new List<(List<Loop> Normal, List<Loop> Finish)>();
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
                List<Loop> Build(double wallStep) => style switch
                {
                    RegionFillStyle.HorizontalHatch or RegionFillStyle.VerticalHatch =>
                        HatchSequence(region, step, style == RegionFillStyle.VerticalHatch, climb, wallStep),
                    RegionFillStyle.OutsideOffsets when region.Holes.Count > 0 => OutsideOffsetSequence(region, step, climb, wallStep),
                    RegionFillStyle.InsideOffsets when region.Holes.Count > 0 => InsideOffsetSequence(region, step, climb, wallStep),
                    _ => OffsetSequence(region, step, climb, wallStep, closed, r + op.RoughingClearance.Value),
                };
                var normal = Build(0);
                var finish = finishStep > 0 ? Build(finishStep) : normal;
                if (normal.Count > 0) regions.Add((normal, finish));
            }
        }

        var levels = ctx.Levels(op);
        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        var cutter = new LoopCutter(ctx, mb)
        {
            LeadIn = op.LeadInMove.Value,
            LeadOut = op.LeadOutMove.Value,
            AwayNormal = ProfileGenerator.AwayNormal,
        };
        var maxCross = op.MaxCrossoverDistance.Value * dia;
        var stepFeed = op.StepoverFeedrate.Value == StepoverFeedrate.CutFeedrate ? ctx.CutFeed : ctx.PlungeFeed;
        var mixed = op.MillingDirection.Value == MillingDirection.Mixed;

        var firstPoint = op.StartPoint.Value.Point?.XY ?? closed[0].StartPoint.XY;

        List<Loop> SequenceFor((List<Loop> Normal, List<Loop> Finish) region, int levelIndex)
        {
            var useFinish = !finishOnlyAtBottom || levelIndex == levels.Count - 1;
            var seq = useFinish ? region.Finish : region.Normal;
            if (!mixed || levelIndex % 2 == 0) return seq;
            // Mixed: every other level runs the rings the other way round.
            return seq.Select(l => l.Closed ? l.Reversed() : l.WithPoints(l.Points.AsEnumerable().Reverse().ToList())).ToList();
        }

        void CutSequence(List<Loop> seq, double z, double prev)
        {
            for (var i = 0; i < seq.Count; i++)
            {
                var loop = seq[i];
                // First pass: start nearest the shape's first point (a circle starts at +X, like the reference);
                // afterwards nearest the current position to keep stepovers short.
                if (loop.Closed) loop = LoopTools.StartAt(loop, mb.Position?.XY ?? firstPoint);
                double? link = null;
                if (i > 0 && mb.Position is { } q && Math.Abs(q.Z - z) < 1e-9 && Point2.Distance(q.XY, loop.Start) <= maxCross + 1e-9)
                    link = stepFeed;
                cutter.Cut(loop, z, prev, [], link);
            }
        }

        var ordered = regions.OrderBy(s => s.Normal[0].Start.X).ToList();
        if (op.CutOrdering.Value == CutOrdering.LevelFirst)
        {
            var prev = ctx.StockSurface;
            for (var li = 0; li < levels.Count; li++)
            {
                foreach (var region in ordered)
                {
                    CutSequence(SequenceFor(region, li), levels[li], prev);
                    mb.Retract();
                }
                prev = levels[li];
            }
        }
        else
        {
            foreach (var region in ordered)
            {
                var prev = ctx.StockSurface;
                for (var li = 0; li < levels.Count; li++)
                {
                    var seq = SequenceFor(region, li);
                    CutSequence(seq, levels[li], prev);
                    // Next level starts at the first (innermost) ring: retract if it's far away.
                    var nextStart = li + 1 < levels.Count ? SequenceFor(region, li + 1)[0].Start : seq[0].Start;
                    if (mb.Position is { } p && Point2.Distance(p.XY, nextStart) > maxCross) mb.Retract();
                    prev = levels[li];
                }
                mb.Retract();
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>
    /// Outside offsets: rings parallel to the outer wall only, moving inwards; the islands are cut around
    /// separately at the end. Cut innermost first, outer wall last.
    /// </summary>
    private static List<Loop> OutsideOffsetSequence(Shape region, double step, bool climb, double wallStep)
    {
        var loops = new List<Loop>();
        var islands = region.Holes.Select(h => Polygons.EnsureOrientation(h, ccw: true)).ToList();
        var rings = new List<List<Path2>>();
        var distance = 0.0;
        for (var k = 0; k < 10000; k++)
        {
            var inset = Polygons.Offset([region.Outer], -distance);
            var clipped = Polygons.Difference(inset, islands);
            if (clipped.Count == 0) break;
            rings.Add(clipped.Where(p => !p.IsClockwise).ToList());
            distance += k == 0 && wallStep > 0 ? wallStep : step;
        }
        rings.Reverse();
        foreach (var ring in rings)
        foreach (var p in ring)
            loops.Add(ToLoop(p, ccw: climb, outside: false));
        foreach (var h in region.Holes) loops.Add(ToLoop(h, ccw: !climb, outside: true));
        return loops;
    }

    /// <summary>
    /// Inside offsets: rings growing outwards from the islands, clipped to the pocket; the outer wall is the
    /// last pass.
    /// </summary>
    private static List<Loop> InsideOffsetSequence(Shape region, double step, bool climb, double wallStep)
    {
        var loops = new List<Loop>();
        var outer = new List<Path2> { Polygons.EnsureOrientation(region.Outer, ccw: true) };
        var islands = region.Holes.Select(h => Polygons.EnsureOrientation(h, ccw: true)).ToList();
        // Islands first (their walls), then growing rings.
        foreach (var h in region.Holes) loops.Add(ToLoop(h, ccw: !climb, outside: true));
        var distance = step;
        // Stop the island rings short of the wall by the finish stepover (or one stepover).
        var wallGap = wallStep > 0 ? wallStep : step;
        var wallInset = Polygons.Offset(outer, -wallGap);
        for (var k = 0; k < 10000; k++)
        {
            var grown = Polygons.Offset(islands, distance);
            var free = Polygons.Difference(wallInset, grown);
            if (free.Count == 0) break;
            var rings = Polygons.Intersection(grown, wallInset);
            foreach (var p in rings.Where(p => !p.IsClockwise)) loops.Add(ToLoop(p, ccw: !climb, outside: true));
            distance += step;
        }
        loops.Add(ToLoop(region.Outer, ccw: climb, outside: false));
        return loops;
    }

    /// <summary>Concentric offsets from the centre outwards; the boundary (first inset) is cut last.</summary>
    /// <param name="sources">Original pocket outlines, for exact snapping of the rings (optional).</param>
    /// <param name="baseOffset">Distance of <paramref name="region"/> from <paramref name="sources"/>.</param>
    internal static List<Loop> OffsetSequence(Shape region, double step, bool climb, double wallStep = 0,
        IReadOnlyList<Path2>? sources = null, double baseOffset = 0, double tolerance = ToolpathGenerator.ChordTolerance)
    {
        var rings = new List<(List<Shape> Shapes, double Offset)> { (new() { region }, baseOffset) };
        var current = new List<Shape> { region };
        var distance = 0.0;
        var previousArea = Math.Abs(region.Outer.SignedArea());
        // Slivers with (almost) no area are not worth a pass.
        var minArea = Math.Max(step * step * 1e-3, 1e-6);
        for (var guard = 0; guard < 10000; guard++)
        {
            // The first ring after the wall uses the finish stepover when one is set.
            var d = guard == 0 && wallStep > 0 ? wallStep : step;
            distance += d;
            // Each ring is offset from the previous one (small offsets are fast; one large offset of a dense
            // outline is quadratic). Simplifying within 1e-5 stops the vertex count from growing ring by ring;
            // the cut geometry is made exact afterwards by OffsetSnapper.
            var next = current.SelectMany(s => Polygons.InsetShape(s, d, tolerance, simplify: tolerance))
                .Where(s => Math.Abs(s.Outer.SignedArea()) > minArea).ToList();
            if (next.Count == 0) break;
            var area = next.Sum(s => Math.Abs(s.Outer.SignedArea()));
            if (area >= previousArea - 1e-9) break;
            previousArea = area;
            rings.Add((next, baseOffset + distance));
            current = next;
        }
        rings.Reverse();
        var loops = new List<Loop>();
        foreach (var (ring, ringOffset) in rings)
        foreach (var shape in ring)
        {
            // Conventional (CW spindle): pocket walls clockwise, island walls counter-clockwise.
            loops.Add(ToLoop(shape.Outer, ccw: climb, outside: false, sources, ringOffset));
            foreach (var h in shape.Holes) loops.Add(ToLoop(h, ccw: !climb, outside: true, sources, ringOffset));
        }
        return loops;
    }

    private static Loop ToLoop(Path2 p, bool ccw, bool outside, IReadOnlyList<Path2>? sources = null, double offset = 0) =>
        new Loop { Points = LoopTools.Clean(p.Flatten()), IsOutside = outside, SnapSources = sources, SnapOffset = offset }.Oriented(ccw);

    /// <summary>Zig-zag hatch lines clipped to the region, followed by a boundary pass.</summary>
    private static List<Loop> HatchSequence(Shape region, double step, bool vertical, bool climb, double wallStep = 0)
    {
        // With a finish stepover the hatch stays that far away from the walls; the wall pass cleans up.
        var hatchArea = wallStep > 0 ? Polygons.InsetShape(region, wallStep) : [region];
        var polys = hatchArea.SelectMany(s => s.AllPaths()).Select(p => p.Flatten()).ToList();
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
