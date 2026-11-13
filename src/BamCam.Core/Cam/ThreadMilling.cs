using System.ComponentModel;
using BamCam.Core.Geometry;
using BamCam.Core.Localization;

namespace BamCam.Core.Cam;

/// <summary>Hand of a thread: right-hand threads turn clockwise into the part.</summary>
public enum ThreadHand { Right, Left }

/// <summary>Which way a thread mill runs along the thread.</summary>
public enum ThreadMillDirection { BottomUp, TopDown }

/// <summary>
/// Thread milling (issue 9): a helix around each hole (internal) or stud (external) from the target depth to the stock
/// surface. The helix turns so that it cuts the chosen hand of thread: a right-hand thread is cut counter-clockwise going
/// up or clockwise going down (with a clockwise spindle: internal bottom-up is climb milling, top-down conventional).
/// The tool comes in on a half circle from the centre of a hole (from outside a stud) at the start height and leaves the
/// same way. A single-point cutter runs one helix over the whole length; a cutter with several teeth needs one turn per
/// cutter length.
/// </summary>
public sealed class ThreadMillOp : DepthOp
{
    public override string XmlName => "BamCamThreadMill";
    public override string DisplayType => "Thread Milling";

    public ThreadMillOp()
    {
        DepthIncrement = new(0, ParamState.Value);
    }

    public override IReadOnlySet<string> HiddenParameters => Hidden;

    private static readonly HashSet<string> Hidden = [nameof(DepthIncrement), nameof(FinalDepthIncrement)];

    public override bool NeedsToolDiameter => true;

    [Param(60, "Thread"), DisplayName("Thread Type"), Description("Internal: a thread in a hole. External: a thread on a stud.")]
    public CamParam<ThreadType> ThreadType { get; set; } = new(Cam.ThreadType.Internal);

    [Param(61, "Thread"), DisplayName("Thread Diameter"),
     Description("Nominal (major) diameter of the thread, e.g. 8 for M8. 0 = the diameter of each selected circle.")]
    public CamParam<double> ThreadDiameter { get; set; } = new(0);

    [Param(62, "Thread"), Description("Thread pitch: distance per turn, e.g. 1.25 for M8.")]
    public CamParam<double> Pitch { get; set; } = new(1.5);

    [Param(63, "Thread"), DisplayName("Thread Depth"),
     Description("Radial depth of the thread; 0 = metric ISO depth from the pitch (0.5413 x pitch internal, 0.6134 x pitch external).")]
    public CamParam<double> ThreadDepth { get; set; } = new(0);

    [Param(64, "Thread"), DisplayName("Thread Hand"), Description("Right-hand or left-hand thread.")]
    public CamParam<ThreadHand> ThreadHand { get; set; } = new(Cam.ThreadHand.Right);

    [Param(65, "Thread"), DisplayName("Cut Direction"),
     Description("Bottom up or top down. Right-hand thread with a clockwise spindle: bottom up is climb milling inside a hole, top down conventional.")]
    public CamParam<ThreadMillDirection> CutDirection { get; set; } = new(ThreadMillDirection.BottomUp);

    [Param(66, "Thread"), DisplayName("Radial Passes"), Description("Number of passes to the full thread depth, each a little deeper.")]
    public CamParam<int> RadialPasses { get; set; } = new(1);

    [Param(67, "Thread"), DisplayName("Cutter Length"),
     Description("Length of the threaded part of a cutter with several teeth: one turn cuts this length. 0 = single-point cutter, one helix over the whole thread.")]
    public CamParam<double> CutterLength { get; set; } = new(0);
}

/// <summary>
/// Tapping with a canned cycle (G84), for a tapping head or rigid tapping: the feed is pitch × spindle speed, the tap
/// goes down to the target depth and back out with the spindle reversing (done by the machine).
/// </summary>
public sealed class TapOp : DepthOp
{
    public override string XmlName => "BamCamTap";
    public override string DisplayType => "Tapping";

    public TapOp()
    {
        DepthIncrement = new(0, ParamState.Value);
    }

    public override IReadOnlySet<string> HiddenParameters => Hidden;

    private static readonly HashSet<string> Hidden = [nameof(DepthIncrement), nameof(FinalDepthIncrement)];

    [Param(60, "Tapping"), Description("Thread pitch of the tap: the feed is pitch × spindle speed.")]
    public CamParam<double> Pitch { get; set; } = new(1.5);

    [Param(61, "Tapping"), DisplayName("Retract Height"), Description("R plane of the cycle: where the tap starts and returns to.")]
    public CamParam<double> RetractHeight { get; set; } = new(1.5);

    [Param(62, "Tapping"), Description("Dwell at the bottom in seconds (0 = none), for controls that use it.")]
    public CamParam<double> Dwell { get; set; } = new(0);

    [Param(63, "Tapping"), DisplayName("Feed Factor"),
     Description("Feed in percent of pitch × spindle speed. 100 for rigid tapping; a little less (e.g. 95) lets a tension-compression tapping head take up the difference when the spindle does not follow the feed exactly. Try it out on the machine.")]
    public CamParam<double> FeedFactor { get; set; } = new(100);
}

internal static class ThreadMillGenerator
{
    /// <summary>ISO metric thread depth as a share of the pitch (as for the lathe).</summary>
    private const double InternalDepth = 0.5413, ExternalDepth = 0.6134;

    public static void Generate(GenContext ctx, ThreadMillOp op, Toolpath tp)
    {
        var pitch = op.Pitch.Value;
        if (pitch <= 0)
        {
            ctx.Warnings.Add(Tr.F("{0}: the pitch must be larger than 0.", op.Name));
            ctx.Finish(tp);
            return;
        }
        var top = ctx.StockSurface;
        var bottom = ctx.TargetDepth;
        if (bottom >= top - 1e-9)
        {
            ctx.Warnings.Add(Tr.F("{0}: the target depth must be below the stock surface.", op.Name));
            ctx.Finish(tp);
            return;
        }
        var inside = op.ThreadType.Value == ThreadType.Internal;
        var toolR = ctx.ToolDiameter / 2;
        var depth = op.ThreadDepth.Value > 0 ? op.ThreadDepth.Value : (inside ? InternalDepth : ExternalDepth) * pitch;
        var up = op.CutDirection.Value == ThreadMillDirection.BottomUp;
        // Right-hand helix: counter-clockwise going up, clockwise going down.
        var ccw = (op.ThreadHand.Value == ThreadHand.Right) == up;
        var passes = Math.Max(1, op.RadialPasses.Value);
        var levels = Levels(top, bottom, pitch, op.CutterLength.Value, up);

        var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
        foreach (var (c, holeDiameter, _) in DrillGenerator.Holes(ctx, op))
        {
            var major = op.ThreadDiameter.Value > 0 ? op.ThreadDiameter.Value : holeDiameter;
            if (major <= 0)
            {
                ctx.Warnings.Add(Tr.F("{0}: a point has no diameter; set the Thread Diameter.", op.Name));
                continue;
            }
            for (var k = 1; k <= passes; k++)
            {
                var cut = depth * k / passes;
                // Tool centre: inside, from the minor diameter out to the major one; outside, from the major one in.
                var r = inside ? major / 2 - depth + cut - toolR : major / 2 - cut + toolR;
                if (r <= 1e-6)
                {
                    ctx.Warnings.Add(Tr.F("{0}: the tool is too large for a thread of diameter {1}.", op.Name, major));
                    break;
                }
                foreach (var (z0, z1) in levels) Helix(ctx, mb, c, r, z0, z1, pitch, ccw, inside, toolR);
            }
        }
        mb.Retract();
        ctx.Finish(tp);
    }

    /// <summary>
    /// The helices as (start height, end height): one over the whole length for a single-point cutter, or one turn per
    /// cutter length, in the order of the cut direction. Heights are whole turns above the bottom, so that all turns of a
    /// thread lie on one helix.
    /// </summary>
    internal static List<(double Z0, double Z1)> Levels(double top, double bottom, double pitch, double cutterLength, bool up)
    {
        var length = top - bottom;
        var result = new List<(double, double)>();
        if (cutterLength <= 0)
        {
            var turns = Math.Max(1, (int)Math.Ceiling(length / pitch - 1e-9));
            result.Add(up ? (bottom, bottom + turns * pitch) : (bottom + turns * pitch, bottom));
            return result;
        }
        var step = Math.Max(1, Math.Floor(cutterLength / pitch + 1e-9)) * pitch;
        for (var zb = bottom; zb < top - 1e-9 || result.Count == 0; zb += step)
        {
            result.Add(up ? (zb, zb + pitch) : (zb + pitch, zb));
            if (zb + cutterLength >= top - 1e-9) break;
        }
        if (!up) result.Reverse();
        return result;
    }

    private static void Helix(GenContext ctx, MoveBuilder mb, Point2 c, double r, double z0, double z1, double pitch, bool ccw, bool inside, double toolR)
    {
        var entry = c + new Point2(r, 0);
        // Half circle onto the thread, tangent to it: inside from the centre, outside from beyond the stud.
        var lead = inside ? r / 2 : Math.Max(toolR, r * 0.1);
        var leadCentre = inside ? c + new Point2(r / 2, 0) : c + new Point2(r + lead, 0);
        var start = inside ? c : c + new Point2(r + 2 * lead, 0);
        var leadClockwise = inside ? !ccw : ccw;

        mb.MoveToAndPlunge(start.ToPoint3(z0), ctx.PlungeFeed, ctx.StockSurface);
        mb.Arc(entry.ToPoint3(z0), leadCentre, leadClockwise, ctx.CutFeed);
        var halfTurns = Math.Max(2, (int)Math.Round(Math.Abs(z1 - z0) / pitch * 2));
        for (var i = 1; i <= halfTurns; i++)
        {
            var p = i % 2 == 1 ? c - new Point2(r, 0) : entry;
            mb.Arc(p.ToPoint3(z0 + (z1 - z0) * i / halfTurns), c, !ccw, ctx.CutFeed);
        }
        mb.Arc(start.ToPoint3(z1), leadCentre, leadClockwise, ctx.CutFeed);
        mb.Retract();
    }
}

internal static class TapGenerator
{
    public static void Generate(GenContext ctx, TapOp op, Toolpath tp)
    {
        var pitch = op.Pitch.Value;
        var speed = op.SpindleSpeed.Value;
        if (pitch <= 0) ctx.Warnings.Add(Tr.F("{0}: the pitch must be larger than 0.", op.Name));
        else if (speed <= 0) ctx.Warnings.Add(Tr.F("{0}: tapping needs a spindle speed: the feed is pitch × spindle speed.", op.Name));
        else if (op.FeedFactor.Value is <= 0 or > 200) ctx.Warnings.Add(Tr.F("{0}: the feed factor must be between 0 and 200 %.", op.Name));
        else
        {
            if (op.SpindleDirection.Value != SpindleDirection.CW)
                ctx.Warnings.Add(Tr.F("{0}: G84 taps a right-hand thread with the spindle turning clockwise (CW).", op.Name));
            var mb = new MoveBuilder(tp, ctx.Clearance, ctx.FastPlunge);
            var feed = pitch * speed * op.FeedFactor.Value / 100;
            foreach (var (p, _, _) in DrillGenerator.Holes(ctx, op))
            {
                mb.Rapid(p.ToPoint3(ctx.Clearance));
                tp.Add(Move.Drill(p.ToPoint3(ctx.TargetDepth), feed, new DrillCycle(op.RetractHeight.Value, 0, op.Dwell.Value, pitch)));
            }
            mb.Retract();
        }
        ctx.Finish(tp);
    }
}
