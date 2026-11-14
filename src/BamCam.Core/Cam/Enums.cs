using System.Runtime.Serialization;

namespace BamCam.Core.Cam;

// Enum member names (or [EnumMember] values) match the strings used in project files.

public enum ToolProfile { Unspecified, EndMill, BullNose, BallNose, VCutter, Drill, Lathe }

public enum InsideOutside { Inside, Outside }

public enum MillingDirection { Conventional, Climb, Mixed }

public enum CutOrdering { DepthFirst, LevelFirst }

public enum SpindleDirection { CW, CCW, Off }

public enum VelocityMode { Undefined, ExactStop, ConstantVelocity, Default }

public enum WorkPlane { XY, XZ, YZ }

public enum OptimisationMode { Experimental, Standard, None }

public enum RoughingFinishing { Roughing, Finishing }

/// <summary>Turning direction: right hand cuts from +Z towards the chuck (-Z), left hand the other way.</summary>
public enum LatheCutDirection { RightHand, LeftHand }

public enum StepoverFeedrate
{
    [EnumMember(Value = "Plunge Feedrate")] PlungeFeedrate,
    [EnumMember(Value = "Cut Feedrate")] CutFeedrate,
}

public enum LeadMoveType { None, Spiral, Tangent }

public enum TabMethod
{
    None,
    Automatic,
    Manual,
    AutomaticOuter,
    AutomaticInner,
}

public enum TabStyle { Square, Triangle, Skip }

public enum SideProfileMethod { None, Slope, ConvexRadius, ConcaveRadius }

public enum RegionFillStyle { HorizontalHatch, VerticalHatch, InsideOutsideOffsets, OutsideOffsets, InsideOffsets }

public enum DrillingMethod
{
    CannedCycle,
    SpiralMill_CW,
    SpiralMill_CCW,
    CustomScript,
}

public enum ArcCenterMode
{
    Default,
    Absolute,
    IncrementalFromP1,
    IncrementalToP1,
    IncrementalFromP2,
    IncrementalToP2,
}

public enum RebuildToolpath { Always, Prompt, IfNeeded }

public enum NestMethod { None, Grid, IsoGrid, Manual, PointList }

public enum Profile3DMethod { Horizontal, Vertical, WaterlineRough, WaterlineFinish }

/// <summary>
/// Where the scanline 3D profile starts, with CamBam's ten values (issue 14). As in CamBam only one half counts: the
/// Horizontal method starts at the top for every value with Top, else at the bottom; the Vertical method starts at the
/// right for every value with Right, else at the left.
/// </summary>
public enum StartCorner
{
    [System.ComponentModel.Description("Bottom + Left")] BottomLeft,
    [System.ComponentModel.Description("Bottom + Right")] BottomRight,
    [System.ComponentModel.Description("Top + Left")] TopLeft,
    [System.ComponentModel.Description("Top + Right")] TopRight,
    Bottom,
    Top,
    [System.ComponentModel.Description("Top + Bottom")] TopBottom,
    Left,
    Right,
    [System.ComponentModel.Description("Left + Right")] LeftRight,
}

public enum FlipAxis { X, Y }

public enum BoundaryMethod { ShapeOutline, BoundingBox, SelectedShapes }

/// <summary>Grid nesting order: the first direction, then the direction of the next row/column.</summary>
public enum NestGridOrder
{
    RightUp,
    RightDown,
    LeftUp,
    LeftDown,
    UpRight,
    UpLeft,
    DownRight,
    DownLeft,
}
