using BamCam.Core.Geometry;

namespace BamCam.Core.Cam;

// Composite machining parameters. Records so that CamParam<T> copies stay independent.

public sealed record LeadMove(
    LeadMoveType LeadMoveType = LeadMoveType.None,
    double SpiralAngle = 0,
    double TangentRadius = 0,
    double LeadMoveFeedrate = 0)
{
    public static readonly LeadMove None = new();
    public bool IsNone => LeadMoveType == LeadMoveType.None;
    public override string ToString() => IsNone ? "None" : $"{LeadMoveType}";
}

public sealed record HoldingTabSettings(
    TabMethod TabMethod = TabMethod.None,
    double Width = 3,
    double Height = 3,
    int MinimumTabs = 3,
    int MaximumTabs = 6,
    double TabDistance = 40,
    double SizeThreshold = 4,
    bool UseLeadIns = false,
    TabStyle TabStyle = TabStyle.Triangle)
{
    public override string ToString() => TabMethod.ToString();
}

public sealed record SideProfile(
    SideProfileMethod Method = SideProfileMethod.None,
    double Value = 0,
    bool AdjustCutWidth = false)
{
    public override string ToString() => Method.ToString();
}

/// <summary>A manually placed or generated holding tab on a profile's source shape.</summary>
public sealed class HoldingTab
{
    public int ParentEntityID { get; set; }

    /// <summary>Position along the source shape, 0..1 of its length.</summary>
    public double ParametricPoint { get; set; }

    public Point2 Normal { get; set; }
    public bool NormalInverted { get; set; }

    public HoldingTab Clone() => (HoldingTab)MemberwiseClone();
}

/// <summary>An optional point (e.g. MOP start point); null = not set.</summary>
public sealed record OptionalPoint(Point3? Point)
{
    public static readonly OptionalPoint None = new((Point3?)null);
    public bool IsSet => Point.HasValue;
    public override string ToString() => Point?.ToString() ?? "";
}
