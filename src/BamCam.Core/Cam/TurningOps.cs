using System.ComponentModel;

namespace BamCam.Core.Cam;

/// <summary>
/// Base of all turning operations: the drawing shows the part on one side of the spindle axis, which runs through
/// the machining origin parallel to drawing X (lathe Z); lathe X is the radius. Work plane XZ, lathe tool.
/// </summary>
public abstract class TurningOp : MachineOp
{
    protected TurningOp()
    {
        WorkPlane = new(Cam.WorkPlane.XZ) { State = ParamState.Value };
        ToolProfile = new(Cam.ToolProfile.Lathe);
    }
}

/// <summary>
/// Turning operations that may cut with constant cutting speed (G96): turning, facing, grooving. Threading and
/// drilling on the axis always run with constant spindle speed.
/// </summary>
public abstract class SurfaceSpeedTurningOp : TurningOp
{
    [Param(80, "Spindle"), DisplayName("Surface Speed"),
     Description("Constant cutting speed (G96) in m/min (ft/min in inch drawings); 0 = constant spindle speed. The Spindle Speed starts the spindle and applies again after the operation (G97).")]
    public CamParam<double> SurfaceSpeed { get; set; } = new(0);

    [Param(81, "Spindle"), DisplayName("Max Spindle Speed"),
     Description("Spindle speed limit (rpm) with constant cutting speed; 0 = no limit (not recommended: towards the axis the speed rises sharply).")]
    public CamParam<double> MaxSpindleSpeed { get; set; } = new(0);
}

/// <summary>Outside of the part (external turning) or inside a bore (internal turning, boring).</summary>
public enum TurningSide { External, Internal }

/// <summary>Who allows for the tool nose radius: BamCam in the tool path, or the control (G41/G42).</summary>
public enum NoseCompensation { Computed, Machine }

/// <summary>Thread on the outside of the part or inside a bore.</summary>
public enum ThreadType { External, Internal }

/// <summary>
/// Facing: turns the end of the part flat. The selected drawing objects mark the end face (their largest lathe Z);
/// the stock in front of it is removed in axial steps from the stock radius down to the inner radius.
/// </summary>
public sealed class LatheFacingOp : SurfaceSpeedTurningOp
{
    public override string XmlName => "BamCamLatheFacing";
    public override string DisplayType => "Lathe Facing";

    [Param(41, "Depth"), Description("Axial (Z) step between facing passes.")]
    public CamParam<double> DepthIncrement { get; set; } = new(0.5);

    [Param(70, "Lathe"), DisplayName("Inner Radius"), Description("Radius where facing stops; 0 = across the axis.")]
    public CamParam<double> InnerRadius { get; set; } = new(0);
}

/// <summary>
/// Grooving and parting off with a grooving (parting) tool whose blade width is the Tool Diameter. The selected
/// drawing objects outline the groove: their Z range is the groove width, their smallest radius the groove bottom
/// (0 = parting off). The tool reference point is the left (chuck side) corner of the blade.
/// </summary>
public sealed class LatheGrooveOp : SurfaceSpeedTurningOp
{
    public override string XmlName => "BamCamLatheGroove";
    public override string DisplayType => "Lathe Grooving";

    [Param(41, "Depth"), Description("Radial depth per peck; 0 = plunge to the bottom in one go.")]
    public CamParam<double> DepthIncrement { get; set; } = new(1);

    [Param(70, "Lathe"), DisplayName("Peck Retract"), Description("Retract after each peck to break the chip.")]
    public CamParam<double> PeckRetract { get; set; } = new(0.2);

    [Param(71, "Lathe"), DisplayName("Step Over"), Description("Axial step between plunges as a fraction of the blade width.")]
    public CamParam<double> StepOver { get; set; } = new(0.8);

    [Param(72, "Lathe"), DisplayName("Finish Pass"), Description("Clean the groove bottom with one pass after the plunges.")]
    public CamParam<bool> FinishPass { get; set; } = new(true);
}

/// <summary>
/// Thread turning. The selected drawing object is the threaded length on the major diameter (external) or the
/// bore (internal). Output as a threading cycle (G76) when the post processor defines one, otherwise as single
/// synchronised passes (G33).
/// </summary>
public sealed class LatheThreadOp : TurningOp
{
    public override string XmlName => "BamCamLatheThread";
    public override string DisplayType => "Lathe Threading";

    [Param(70, "Thread"), Description("Thread pitch (distance per turn).")]
    public CamParam<double> Pitch { get; set; } = new(1.5);

    [Param(71, "Thread"), DisplayName("Thread Depth"), Description("Radial thread depth; 0 = metric ISO depth from the pitch (0.6134 x pitch external, 0.5413 x pitch internal).")]
    public CamParam<double> ThreadDepth { get; set; } = new(0);

    [Param(72, "Thread"), DisplayName("Thread Type")]
    public CamParam<ThreadType> ThreadType { get; set; } = new(Cam.ThreadType.External);

    [Param(73, "Thread"), DisplayName("First Cut Depth"), Description("Depth of the first pass; later passes get thinner (degression).")]
    public CamParam<double> FirstCutDepth { get; set; } = new(0.1);

    [Param(74, "Thread"), DisplayName("Depth Degression"), Description("1 = equal depth per pass, 2 = equal chip area per pass.")]
    public CamParam<double> DepthDegression { get; set; } = new(2);

    [Param(75, "Thread"), DisplayName("Infeed Angle"), Description("Compound infeed angle in degrees (29.5 for 60° threads, 0 = radial).")]
    public CamParam<double> InfeedAngle { get; set; } = new(29.5);

    [Param(76, "Thread"), DisplayName("Spring Passes"), Description("Extra passes at full depth.")]
    public CamParam<int> SpringPasses { get; set; } = new(1);

    [Param(77, "Thread"), DisplayName("Run In"), Description("Start this far in front of the thread (in pitches) so that the feed is up to speed.")]
    public CamParam<double> RunIn { get; set; } = new(2);
}

/// <summary>
/// Drilling on the spindle axis (centre drill, drill, peck drill). The selected drawing object spans the hole along
/// the axis: the hole starts at its largest Z and ends at its smallest.
/// </summary>
public sealed class LatheDrillOp : TurningOp
{
    public override string XmlName => "BamCamLatheDrill";
    public override string DisplayType => "Lathe Drilling";

    [Param(70, "Drilling"), DisplayName("Peck Distance"), Description("Depth per peck with full retract; 0 = drill in one go.")]
    public CamParam<double> PeckDistance { get; set; } = new(0);

    [Param(71, "Drilling"), DisplayName("Retract Height"), Description("Distance in front of the hole for rapid approach and retracts.")]
    public CamParam<double> RetractHeight { get; set; } = new(2);
}
