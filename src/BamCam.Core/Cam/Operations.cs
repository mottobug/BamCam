namespace BamCam.Core.Cam;

/// <summary>Operations with stepped depth passes (profile, pocket, engrave, drill, 3D).</summary>
public abstract class DepthOp : MachineOp
{
    [Param(40, "Depth")] public CamParam<double> TargetDepth { get; set; } = new(-1);
    [Param(41, "Depth")] public CamParam<double> DepthIncrement { get; set; } = new(0.4);
    [Param(42, "Depth")] public CamParam<double> FinalDepthIncrement { get; set; } = new(0);

    /// <summary>Z levels from just below the stock surface down to target depth.</summary>
    public List<double> DepthLevels()
    {
        var top = StockSurface.Value;
        var bottom = TargetDepth.Value;
        var levels = new List<double>();
        if (bottom >= top - 1e-9)
        {
            levels.Add(top);
            return levels;
        }
        var inc = DepthIncrement.Value <= 0 ? top - bottom : DepthIncrement.Value;
        var finalInc = Math.Clamp(FinalDepthIncrement.Value, 0, top - bottom);
        var roughBottom = bottom + finalInc;
        var z = top;
        while (z - inc > roughBottom + 1e-9)
        {
            z -= inc;
            levels.Add(z);
        }
        if (finalInc > 1e-9)
        {
            if (roughBottom < top - 1e-9 && (levels.Count == 0 || Math.Abs(levels[^1] - roughBottom) > 1e-9)) levels.Add(roughBottom);
        }
        levels.Add(bottom);
        return levels;
    }
}

/// <summary>Operations with offsets / passes (profile, pocket, 3D).</summary>
public abstract class AreaOp : DepthOp
{
    [Param(50, "Cutting")] public CamParam<CutOrdering> CutOrdering { get; set; } = new(Cam.CutOrdering.DepthFirst);
    [Param(52, "Cutting")] public CamParam<double> StepOver { get; set; } = new(0.4);
    [Param(53, "Feedrates")] public CamParam<StepoverFeedrate> StepoverFeedrate { get; set; } = new(Cam.StepoverFeedrate.PlungeFeedrate);
    [Param(55, "Cutting")] public CamParam<MillingDirection> MillingDirection { get; set; } = new(Cam.MillingDirection.Conventional);
    [Param(56, "Options")] public CamParam<bool> CollisionDetection { get; set; } = new(true);
    [Param(58, "Lead Moves")] public CamParam<LeadMove> LeadInMove { get; set; } = new(LeadMove.None);
    [Param(59, "Lead Moves")] public CamParam<LeadMove> LeadOutMove { get; set; } = new(LeadMove.None);
}

public sealed class ProfileOp : AreaOp
{
    public override string XmlName => "profile";
    public override string DisplayType => "Profile";

    [Param(51, "Cutting")] public CamParam<double> CutWidth { get; set; } = new(0);
    [Param(54, "Cutting")] public CamParam<InsideOutside> InsideOutside { get; set; } = new(Cam.InsideOutside.Outside);
    [Param(57, "Cutting")] public CamParam<bool> CornerOvercut { get; set; } = new(false);
    [Param(60, "Holding Tabs")] public CamParam<HoldingTabSettings> HoldingTabs { get; set; } = new(new HoldingTabSettings());
    [Param(62, "Cutting")] public CamParam<SideProfile> SideProfile { get; set; } = new(new SideProfile());

    /// <summary>Holding tab positions (the "Tabs" list; not a stateful parameter).</summary>
    public List<HoldingTab> Tabs { get; } = [];

    protected override void CloneExtra(MachineOp clone) =>
        ((ProfileOp)clone).Tabs.AddRange(Tabs.Select(t => t.Clone()));
}

public sealed class PocketOp : AreaOp
{
    public override string XmlName => "pocket";
    public override string DisplayType => "Pocket";

    [Param(60, "Cutting")] public CamParam<RegionFillStyle> RegionFillStyle { get; set; } = new(Cam.RegionFillStyle.InsideOutsideOffsets);
    [Param(61, "Cutting")] public CamParam<double> FinishStepover { get; set; } = new(0);
    [Param(62, "Cutting")] public CamParam<bool> FinishStepoverAtTargetDepth { get; set; } = new(false);
}

public sealed class EngraveOp : DepthOp
{
    public override string XmlName => "engrave";
    public override string DisplayType => "Engrave";
}

public sealed class DrillOp : DepthOp
{
    public override string XmlName => "drill";
    public override string DisplayType => "Drill";

    [Param(60, "Drilling")] public CamParam<DrillingMethod> DrillingMethod { get; set; } = new(Cam.DrillingMethod.CannedCycle);
    [Param(61, "Drilling")] public CamParam<double> HoleDiameter { get; set; } = new(0);
    [Param(62, "Drilling")] public CamParam<bool> SpiralFlatBase { get; set; } = new(true);
    [Param(63, "Drilling")] public CamParam<bool> DrillLeadOut { get; set; } = new(false);
    [Param(64, "Drilling")] public CamParam<double> LeadOutLength { get; set; } = new(0);
    [Param(65, "Drilling")] public CamParam<double> PeckDistance { get; set; } = new(0);
    [Param(66, "Drilling")] public CamParam<double> RetractHeight { get; set; } = new(1.5);
    [Param(67, "Drilling")] public CamParam<double> Dwell { get; set; } = new(0);
    [Param(68, "Drilling")] public CamParam<string> CustomScript { get; set; } = new("");
}

/// <summary>Embeds an existing G-code file (back-plotted for display, copied verbatim on output).</summary>
public sealed class NcFileOp : MachineOp
{
    public override string XmlName => "ncfile";
    public override string DisplayType => "NC File";

    [Param(60, "NC File")] public CamParam<string> SourceFile { get; set; } = new("");
}

/// <summary>
/// An operation type this version does not machine (3D profile, lathe, plugins). Its parameters
/// are kept in <see cref="MachineOp.Extensions"/> and written back unchanged.
/// </summary>
public sealed class UnsupportedOp(string xmlName) : MachineOp
{
    private string _xmlName = xmlName;

    public UnsupportedOp() : this("unknown") { }

    public override string XmlName => _xmlName;
    public override string DisplayType => XmlName;

    /// <summary>The complete original element, written back unchanged (with Name/Enabled updated).</summary>
    public System.Xml.Linq.XElement? RawXml { get; set; }

    protected override void CloneExtra(MachineOp clone)
    {
        var c = (UnsupportedOp)clone;
        c._xmlName = _xmlName;
        c.RawXml = RawXml == null ? null : new System.Xml.Linq.XElement(RawXml);
    }
}

public static class MachineOpFactory
{
    public static MachineOp? Create(string xmlName) => xmlName switch
    {
        "profile" => new ProfileOp(),
        "pocket" => new PocketOp(),
        "engrave" => new EngraveOp(),
        "drill" => new DrillOp(),
        "ncfile" => new NcFileOp(),
        _ => null,
    };
}
