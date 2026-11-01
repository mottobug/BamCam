using System.ComponentModel;
using System.Runtime.Serialization;
using BamCam.Core.Geometry;
using BamCam.Core.Model;

namespace BamCam.Core.Cam;

/// <summary>Which side of the drawn line the kerf lies on.</summary>
public enum PlasmaKerfSide
{
    /// <summary>Outlines outside, holes inside (by how the closed shapes nest).</summary>
    Automatic,
    Outside,
    Inside,
    [EnumMember(Value = "On Line")] OnLine,
}

/// <summary>How the torch finds its height above the material.</summary>
public enum TorchHeightControl
{
    /// <summary>Pierce and cut heights as fixed Z values above the stock surface.</summary>
    [EnumMember(Value = "Fixed Heights")] FixedHeights,
    /// <summary>Touch the material with the torch switch before every pierce, heights measured from there.</summary>
    Probe,
}

/// <summary>Shape of the lead-in and lead-out moves.</summary>
public enum PlasmaLead { None, Line, Arc }

/// <summary>Direction of travel around closed shapes.</summary>
public enum PlasmaDirection
{
    /// <summary>Outlines clockwise, holes counter-clockwise: the part is on the right (torches with clockwise swirl).</summary>
    [EnumMember(Value = "Part On Right")] PartOnRight,
    /// <summary>Outlines counter-clockwise, holes clockwise.</summary>
    [EnumMember(Value = "Part On Left")] PartOnLeft,
}

/// <summary>
/// Plasma (and laser / oxy-fuel) cutting: every shape is cut once at cut height with the torch switched on and off
/// around it. The path is offset by half the kerf width, enters and leaves through the scrap side with lead moves and
/// cuts holes before the outline that contains them, so that parts don't move before they are finished.
/// </summary>
public sealed class PlasmaOp : MachineOp
{
    public override string XmlName => "BamCamPlasma";
    public override string DisplayType => "Plasma";

    /// <summary>No milling tool and no spindle: the kerf width and the torch commands take their place.</summary>
    public override IReadOnlySet<string> HiddenParameters => Hidden;

    private static readonly HashSet<string> Hidden =
    [
        nameof(ToolDiameter), nameof(ToolNumber), nameof(ToolProfile), nameof(RoughingFinishing), nameof(RoughingClearance),
        nameof(SpindleDirection), nameof(SpindleSpeed), nameof(SpindleRange), nameof(MaxCrossoverDistance), nameof(OptimisationMode),
    ];

    public PlasmaOp()
    {
        // The torch is switched per shape (Torch On / Torch Off of the post), never with the operation's spindle codes.
        SpindleDirection = new(Cam.SpindleDirection.Off, ParamState.Value);
        SpindleSpeed = new(0, ParamState.Value);
        // Default state: taken from the cut data (or a CAM style) unless typed in.
        CutFeedrate = new(2000);
        PlungeFeedrate = new(1000, ParamState.Value);
        ClearancePlane = new(10, ParamState.Value);
    }

    /// <summary>Starting values for inch drawings.</summary>
    public void UseInchDefaults()
    {
        CutFeedrate.Value = 80;
        PlungeFeedrate.Set(40);
        ClearancePlane.Set(0.4);
        KerfWidth.Value = 0.06;
        PierceHeight.Value = 0.15;
        CutHeight.Value = 0.06;
        ProbeStartHeight.Value = 0.2;
        ProbeDepth.Value = 0.4;
        ProbeFeedrate.Value = 12;
        LeadInLength.Value = 0.16;
        LeadOutLength.Value = 0.08;
        Overcut.Value = 0.02;
    }

    [Param(59, "Plasma"), DisplayName("Cut Data"), Choices(nameof(CutDataChoices), Browse = true),
     Description("Entry of the plasma cut data tables (System tab) for material, thickness and current. Its kerf width, feedrate and heights are used wherever this operation keeps the default value; values typed here override them.")]
    public CamParam<string> CutData { get; set; } = new("");

    /// <summary>Entries of all plasma cut data tables, for the drop-down list.</summary>
    public static IEnumerable<KeyValuePair<string, string>> CutDataChoices() =>
        CamLibraries.PlasmaTables.SelectMany(t => t.Entries).Select(e => e.Name).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new KeyValuePair<string, string>(n, n))
            .Prepend(new KeyValuePair<string, string>("", Localization.Tr.T("(none)")));

    [Param(60, "Plasma"), DisplayName("Kerf Width"),
     Description("Width of the cut. The torch runs half of it beside the drawn line, so that parts get their drawn size.")]
    public CamParam<double> KerfWidth { get; set; } = new(1.5);

    [Param(61, "Plasma"), DisplayName("Kerf Side"),
     Description("Side of the drawn line the kerf lies on. Automatic: outlines outside, holes inside (shapes inside an outline are holes, shapes inside a hole are parts again). Open shapes are always cut on the line.")]
    public CamParam<PlasmaKerfSide> KerfSide { get; set; } = new(PlasmaKerfSide.Automatic);

    [Param(62, "Plasma"), DisplayName("Cut Direction"),
     Description("Direction around closed shapes. Part On Right (outlines clockwise) gives the square edge on the part with the usual clockwise swirl of the plasma gas.")]
    public CamParam<PlasmaDirection> CutDirection { get; set; } = new(PlasmaDirection.PartOnRight);

    [Param(63, "Torch Height"), DisplayName("Height Control"),
     Description("Fixed Heights: pierce and cut heights above the stock surface. Probe: before every pierce the torch moves down until its switch triggers on the material; the heights are measured from there.")]
    public CamParam<TorchHeightControl> HeightControl { get; set; } = new(TorchHeightControl.Probe);

    [Param(64, "Torch Height"), DisplayName("Pierce Height"),
     Description("Height of the torch above the material while piercing (usually 1.5 to 2 x cut height).")]
    public CamParam<double> PierceHeight { get; set; } = new(3.8);

    [Param(65, "Torch Height"), DisplayName("Pierce Delay"),
     Description("Wait in seconds after the torch is switched on, until the arc has pierced the material.")]
    public CamParam<double> PierceDelay { get; set; } = new(0.5);

    [Param(66, "Torch Height"), DisplayName("Cut Height"),
     Description("Height of the torch above the material while cutting. The torch moves down from pierce height with the plunge feedrate.")]
    public CamParam<double> CutHeight { get; set; } = new(1.5);

    [Param(67, "Torch Height"), DisplayName("THC"),
     Description("Switch the torch height control of the machine on after reaching cut height and off before the torch is switched off (Torch Height Control On / Off of the post processor).")]
    public CamParam<bool> UseThc { get; set; } = new(true);

    [Param(68, "Probe"), DisplayName("Probe Start Height"),
     Description("Height above the stock surface down to which the torch moves fast before probing slowly.")]
    public CamParam<double> ProbeStartHeight { get; set; } = new(5);

    [Param(69, "Probe"), DisplayName("Probe Depth"),
     Description("How far below the stock surface the probing move may go before the machine stops with an error.")]
    public CamParam<double> ProbeDepth { get; set; } = new(10);

    [Param(70, "Probe"), DisplayName("Probe Feedrate"),
     Description("Feedrate of the probing move.")]
    public CamParam<double> ProbeFeedrate { get; set; } = new(300);

    [Param(71, "Probe"), DisplayName("Switch Offset"),
     Description("Distance the torch carriage travels after the torch tip touches the material until the switch triggers (floating head travel). Measure it once: touch off, then jog up until the switch releases.")]
    public CamParam<double> SwitchOffset { get; set; } = new(0);

    [Param(72, "Lead Moves"), DisplayName("Lead-In"),
     Description("Move from the pierce point to the cut, through the scrap side: the pierce crater stays off the part.")]
    public CamParam<PlasmaLead> LeadIn { get; set; } = new(PlasmaLead.Arc);

    [Param(73, "Lead Moves"), DisplayName("Lead-In Length"),
     Description("Length of a line lead-in, radius of an arc lead-in. Shortened in small holes.")]
    public CamParam<double> LeadInLength { get; set; } = new(4);

    [Param(74, "Lead Moves"), DisplayName("Lead-Out"),
     Description("Move from the end of the cut into the scrap side before the torch is switched off.")]
    public CamParam<PlasmaLead> LeadOut { get; set; } = new(PlasmaLead.None);

    [Param(75, "Lead Moves"), DisplayName("Lead-Out Length"),
     Description("Length of a line lead-out, radius of an arc lead-out.")]
    public CamParam<double> LeadOutLength { get; set; } = new(2);

    [Param(76, "Lead Moves"),
     Description("Closed shapes are cut this far beyond their start point, so that the cut closes cleanly.")]
    public CamParam<double> Overcut { get; set; } = new(0.5);

    [Param(77, "Small Holes"), DisplayName("Small Hole Size"),
     Description("Holes up to this size (larger side of their bounds) are cut slower and without THC, which would otherwise dive into them. 0 = off.")]
    public CamParam<double> SmallHoleSize { get; set; } = new(0);

    [Param(78, "Small Holes"), DisplayName("Small Hole Feedrate"),
     Description("Feedrate in small holes, in percent of the cut feedrate.")]
    public CamParam<double> SmallHoleFeedrate { get; set; } = new(60);
}

internal static class PlasmaGenerator
{
    private sealed class Cut
    {
        public required Path2 Path;
        public required Path2 Source;
        public bool Closed => Path.Closed;
        /// <summary>Number of closed source shapes around this one: odd = hole.</summary>
        public int Depth;
        public bool SmallHole;
        public readonly List<Cut> Inside = [];
    }

    public static void Generate(GenContext ctx, PlasmaOp op, Toolpath tp)
    {
        var surface = ctx.StockSurface;
        var halfKerf = Math.Max(0, op.KerfWidth.Value) / 2;
        var sources = ctx.SourcePaths().Select(s => s.Path).Where(p => p.SegmentCount > 0).ToList();
        var closed = sources.Where(p => p.Closed).ToList();

        var cuts = new List<Cut>();
        foreach (var src in sources)
        {
            var depth = src.Closed ? closed.Count(o => !ReferenceEquals(o, src) && Encloses(o, src)) : 0;
            var hole = depth % 2 == 1;
            var small = hole && op.SmallHoleSize.Value > 0 && Math.Max(src.Bounds().Width, src.Bounds().Height) <= op.SmallHoleSize.Value + 1e-9;
            foreach (var path in KerfPaths(src, hole, halfKerf, op.KerfSide.Value))
            {
                // Part on the right: outlines clockwise, holes counter-clockwise (reversed for Part On Left).
                var ccw = hole ^ (op.CutDirection.Value == PlasmaDirection.PartOnLeft);
                var oriented = path.Closed ? Polygons.EnsureOrientation(path, ccw) : path;
                cuts.Add(new Cut { Path = oriented, Source = src, Depth = depth, SmallHole = small });
            }
        }
        if (cuts.Count == 0)
        {
            ctx.Finish(tp);
            return;
        }
        // Shapes are cut only after everything inside them.
        foreach (var c in cuts)
        foreach (var o in cuts)
            if (!ReferenceEquals(c, o) && o.Closed && Encloses(o.Source, c.Source) && !ReferenceEquals(o.Source, c.Source))
                o.Inside.Add(c);

        var mb = new MoveBuilder(tp, ctx.Clearance, 0);
        var pos = op.StartPoint.Value.Point?.XY ?? cuts[0].Path.StartPoint.XY;
        var done = new HashSet<Cut>();
        while (done.Count < cuts.Count)
        {
            var next = cuts.Where(c => !done.Contains(c) && c.Inside.All(done.Contains))
                .OrderBy(c => c.Closed ? c.Path.DistanceTo(pos) : Point2.Distance(c.Path.StartPoint.XY, pos)).First();
            done.Add(next);
            var path = next.Closed ? StartPath(next.Path, op.StartPoint.Value.Point?.XY) : next.Path;
            CutShape(ctx, op, mb, next, path, surface);
            pos = mb.Position?.XY ?? pos;
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>True when closed <paramref name="outer"/> contains <paramref name="inner"/> (tested at its first vertex).</summary>
    private static bool Encloses(Path2 outer, Path2 inner) =>
        Math.Abs(outer.SignedArea()) > Math.Abs(inner.SignedArea()) && outer.Contains(inner.StartPoint.XY);

    private static IEnumerable<Path2> KerfPaths(Path2 src, bool hole, double halfKerf, PlasmaKerfSide side)
    {
        if (!src.Closed || halfKerf <= 1e-9 || side == PlasmaKerfSide.OnLine)
        {
            yield return src.WithZ(0);
            yield break;
        }
        var outside = side switch { PlasmaKerfSide.Outside => true, PlasmaKerfSide.Inside => false, _ => !hole };
        foreach (var o in Polygons.Offset([src], outside ? halfKerf : -halfKerf, JoinStyle.Round, ToolpathGenerator.ChordTolerance))
        {
            if (o.Count < 3) continue;
            yield return OffsetSnapper.Snap(ArcFitter.Fit(o, 0.001), [src], halfKerf, 0.005);
        }
    }

    /// <summary>
    /// Starts a closed path in the middle of its longest segment (a straight, calm place for the lead-in) or, when
    /// the operation has a start point, at the vertex nearest to it.
    /// </summary>
    private static Path2 StartPath(Path2 path, Point2? target)
    {
        if (target is { } t) return path.StartNearest(t);
        var segments = path.Segments().ToList();
        var longest = Enumerable.Range(0, segments.Count).MaxBy(i => segments[i].Length);
        var s = segments[longest];
        var vertices = path.Vertices.ToList();
        Point2 mid;
        if (s.IsArc)
        {
            var arc = s.ToArc();
            mid = arc.PointAt(0.5);
            var half = Math.Tan(arc.Sweep / 8);
            vertices[longest] = vertices[longest] with { Bulge = half };
            vertices.Insert(longest + 1, new Vertex(mid.ToPoint3(), half));
        }
        else
        {
            mid = (s.Start.XY + s.End.XY) / 2;
            vertices.Insert(longest + 1, new Vertex(mid.ToPoint3()));
        }
        var split = new Path2(vertices, true);
        return split.StartNearest(mid);
    }

    private static void CutShape(GenContext ctx, PlasmaOp op, MoveBuilder mb, Cut cut, Path2 path, double surface)
    {
        var segments = path.Segments().Where(s => s.Length > 1e-9).ToList();
        if (segments.Count == 0) return;
        if (cut.Closed && op.Overcut.Value > 0) segments.AddRange(DragKnifeGenerator.Overcut(segments, op.Overcut.Value));
        var feed = cut.SmallHole ? ctx.CutFeed * Math.Clamp(op.SmallHoleFeedrate.Value, 1, 100) / 100 : ctx.CutFeed;
        var thc = op.UseThc.Value && !cut.SmallHole;

        // Lead moves only for closed shapes (open ones have no scrap side); shortened to fit into small holes.
        var room = cut.Closed && cut.Depth % 2 == 1 ? Math.Min(path.Bounds().Width, path.Bounds().Height) / 4 : double.PositiveInfinity;
        var leadIn = cut.Closed ? op.LeadIn.Value : PlasmaLead.None;
        var leadOut = cut.Closed ? op.LeadOut.Value : PlasmaLead.None;
        var inLength = Math.Min(Math.Max(0, op.LeadInLength.Value), room);
        var outLength = Math.Min(Math.Max(0, op.LeadOutLength.Value), room);
        // Scrap is on the left when the part is on the right.
        var left = op.CutDirection.Value == PlasmaDirection.PartOnRight;

        var start = segments[0].Start.XY;
        var startDir = DragKnifeGenerator.StartTangent(segments[0]);
        var pierce = Lead(start, startDir, left, leadIn, inLength, entering: true, out var inArc);

        var cutZ = surface + op.CutHeight.Value;
        mb.Retract();
        mb.Rapid(pierce.ToPoint3(mb.Position?.Z ?? mb.ClearanceZ));
        if (op.HeightControl.Value == TorchHeightControl.Probe)
        {
            var probeFrom = Math.Min(mb.ClearanceZ, surface + op.ProbeStartHeight.Value);
            mb.Rapid(pierce.ToPoint3(probeFrom));
            mb.Toolpath.Add(Move.TorchProbe(pierce.ToPoint3(surface - Math.Abs(op.ProbeDepth.Value)), op.ProbeFeedrate.Value,
                surface - op.SwitchOffset.Value));
            mb.Rapid(pierce.ToPoint3(surface + op.PierceHeight.Value));
        }
        else
        {
            mb.Rapid(pierce.ToPoint3(surface + op.PierceHeight.Value));
        }
        mb.Toolpath.Add(Move.TorchSwitch(TorchAction.On, Math.Max(0, op.PierceDelay.Value)));
        mb.Line(pierce.ToPoint3(cutZ), ctx.PlungeFeed);
        if (thc) mb.Toolpath.Add(Move.TorchSwitch(TorchAction.ThcOn));

        if (inArc is { } a) mb.Arc(start.ToPoint3(cutZ), a.Center, a.Clockwise, feed);
        else mb.Line(start.ToPoint3(cutZ), feed);
        foreach (var s in segments) mb.Segment(s, feed, cutZ);

        var end = segments[^1].End.XY;
        var endDir = DragKnifeGenerator.EndTangent(segments[^1]);
        var exit = Lead(end, endDir, left, leadOut, outLength, entering: false, out var outArc);
        if (outArc is { } b) mb.Arc(exit.ToPoint3(cutZ), b.Center, b.Clockwise, feed);
        else mb.Line(exit.ToPoint3(cutZ), feed);

        if (thc) mb.Toolpath.Add(Move.TorchSwitch(TorchAction.ThcOff));
        mb.Toolpath.Add(Move.TorchSwitch(TorchAction.Off));
        mb.Retract();
    }

    /// <summary>
    /// Start (entering) or end point of a lead move at <paramref name="at"/> with travel direction
    /// <paramref name="dir"/>, on the scrap side. Arcs are quarter circles tangent to the cut.
    /// </summary>
    private static Point2 Lead(Point2 at, Point2 dir, bool scrapLeft, PlasmaLead kind, double length, bool entering,
        out (Point2 Center, bool Clockwise)? arc)
    {
        arc = null;
        if (kind == PlasmaLead.None || length <= 1e-9) return at;
        var normal = scrapLeft ? new Point2(-dir.Y, dir.X) : new Point2(dir.Y, -dir.X);
        if (kind == PlasmaLead.Line) return at + normal * length;
        var center = at + normal * length;
        // Turning towards the scrap: counter-clockwise when the scrap is on the left.
        arc = (center, !scrapLeft);
        return entering ? center - dir * length : center + dir * length;
    }
}
