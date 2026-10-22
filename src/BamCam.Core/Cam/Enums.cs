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

public enum OptimisationMode { Experimental, Standard, Legacy, None }

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
    [EnumMember(Value = "Automatic (Outer)")] AutomaticOuter,
    [EnumMember(Value = "Automatic (Inner)")] AutomaticInner,
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
    [EnumMember(Value = "Incremental (C-P1)")] IncrementalCP1,
    [EnumMember(Value = "Incremental (P1-C)")] IncrementalP1C,
    [EnumMember(Value = "Incremental (C-P2)")] IncrementalCP2,
    [EnumMember(Value = "Incremental (P2-C)")] IncrementalP2C,
}

public enum RebuildToolpath { Always, Prompt, IfNeeded }

public enum NestMethod { None, Grid, ISOGrid, Manual, PointList }

public enum Profile3DMethod { Horizontal, Vertical, WaterlineRough, WaterlineFinish }

public enum StartCorner { BottomLeft, BottomRight, TopLeft, TopRight }

public enum FlipAxis { X, Y }

public enum BoundaryMethod { ShapeOutline, BoundingBox, SelectedShapes }

/// <summary>Grid nesting order: the first direction, then the direction of the next row/column.</summary>
public enum NestGridOrder
{
    [EnumMember(Value = "Right Up")] RightUp,
    [EnumMember(Value = "Right Down")] RightDown,
    [EnumMember(Value = "Left Up")] LeftUp,
    [EnumMember(Value = "Left Down")] LeftDown,
    [EnumMember(Value = "Up Right")] UpRight,
    [EnumMember(Value = "Up Left")] UpLeft,
    [EnumMember(Value = "Down Right")] DownRight,
    [EnumMember(Value = "Down Left")] DownLeft,
}
