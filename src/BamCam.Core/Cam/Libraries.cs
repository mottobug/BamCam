using System.ComponentModel;
using System.Xml.Linq;

namespace BamCam.Core.Cam;

/// <summary>A named set of machining parameter values; unset values come from <see cref="ParentStyle"/>.</summary>
public sealed class CamStyle
{
    public string Name { get; set; } = "";

    /// <summary>Parent style name; empty = the library's root (default) style.</summary>
    public string ParentStyle { get; set; } = "";

    /// <summary>Parameter elements by name, kept as XML so every MOP type can read what applies to it.</summary>
    public Dictionary<string, XElement> Values { get; } = [];

    public override string ToString() => Name.Length == 0 ? "(default)" : Name;
}

public sealed class CamStyleLibrary
{
    public string Name { get; set; } = "";
    public string? SourcePath { get; set; }
    public List<CamStyle> Styles { get; } = [];

    public CamStyle? Root => Styles.FirstOrDefault(s => s.Name.Length == 0);

    public CamStyle? Find(string? name) =>
        string.IsNullOrEmpty(name) ? Root : Styles.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Style chain from the root down to <paramref name="name"/> (later entries override earlier ones).</summary>
    public List<CamStyle> Chain(string? name)
    {
        var chain = new List<CamStyle>();
        var guard = 0;
        for (var s = Find(name); s != null && guard++ < 32; s = s.Name.Length == 0 ? null : Find(s.ParentStyle))
        {
            if (chain.Contains(s)) break;
            chain.Insert(0, s);
        }
        if (Root is { } root && !chain.Contains(root)) chain.Insert(0, root);
        return chain;
    }
}

/// <summary>Kind of lathe tool (BamCam addition to the tool library).</summary>
public enum LatheToolType { None, Turning, Grooving, Threading, Drilling }

public sealed class ToolDefinition
{
    [Category("Tool")] public int Index { get; set; }
    [Category("Tool")] public string Name { get; set; } = "";
    [Category("Tool")] public double Diameter { get; set; }
    [Category("Tool"), DisplayName("Tool Profile")] public ToolProfile ToolProfile { get; set; } = ToolProfile.EndMill;
    [Category("Tool")] public int Flutes { get; set; }
    [Category("Tool"), DisplayName("Flute Length")] public double FluteLength { get; set; }
    [Category("Tool")] public double Length { get; set; }
    [Category("Tool"), DisplayName("Shank Diameter")] public double ShankDiameter { get; set; }
    [Category("Tool"), DisplayName("Helix Angle")] public double HelixAngle { get; set; }
    [Category("Tool"), DisplayName("Vee Angle")] public double VeeAngle { get; set; }
    [Category("Tool"), DisplayName("Max Ramp Angle")] public double MaxRampAngle { get; set; }
    [Category("Tool"), DisplayName("Tooth Load")] public double ToothLoad { get; set; }
    [Category("Tool"), DisplayName("Axial Depth Of Cut")] public double AxialDepthOfCut { get; set; }
    [Category("Tool"), DisplayName("Radial Depth Of Cut")] public double RadialDepthOfCut { get; set; }
    [Category("Tool")] public string Comment { get; set; } = "";

    [Category("Lathe"), DisplayName("Lathe Tool Type"),
     Description("Kind of lathe tool; turning operations warn when the tool does not fit. Diameter is the nose diameter (2 x nose radius) of turning and threading tools, the blade width of grooving tools and the drill diameter.")]
    public LatheToolType LatheToolType { get; set; }

    [Category("Lathe"), DisplayName("Tip Orientation"),
     Description("Imaginary tool tip number (1-9) to enter in the control's tool table when the machine compensates the nose radius (G41/G42); 3 = external turning towards the chuck. Kept for reference, not used in the tool path.")]
    public int TipOrientation { get; set; }

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    public override string ToString() => $"{Index}: {Name}";

    /// <summary>Copy with its own list of preserved unknown elements.</summary>
    public ToolDefinition Clone()
    {
        var copy = new ToolDefinition();
        foreach (var p in typeof(ToolDefinition).GetProperties().Where(p => p.CanWrite)) p.SetValue(copy, p.GetValue(this));
        copy.Extensions.AddRange(Extensions.Select(x => new XElement(x)));
        return copy;
    }
}

public sealed class ToolLibrary
{
    public string Name { get; set; } = "";
    public string? SourcePath { get; set; }
    public string ToolNameFormat { get; set; } = "";
    public string NumberFormat { get; set; } = "0.00";
    public List<ToolDefinition> Tools { get; } = [];

    public ToolDefinition? Find(int index) => Tools.FirstOrDefault(t => t.Index == index);
}

/// <summary>Global libraries available to tool path generation (set by the application).</summary>
public static class CamLibraries
{
    public static List<CamStyleLibrary> StyleLibraries { get; } = [];
    public static List<ToolLibrary> ToolLibraries { get; } = [];
    public static List<PlasmaCutTable> PlasmaTables { get; } = [];

    /// <summary>Names of the available post processors, for the drop-down list of the machining options.</summary>
    public static List<string> PostProcessors { get; } = [];

    /// <summary>Name of the post processor used when a drawing does not choose one.</summary>
    public static string DefaultPostProcessor { get; set; } = "Default";

    /// <summary>All tool libraries for a drop-down list; empty = none chosen (see <see cref="FindTool"/>).</summary>
    public static IEnumerable<KeyValuePair<string, string>> ToolLibraryChoices() =>
        ToolLibraries.Select(l => new KeyValuePair<string, string>(l.Name, l.Name))
            .Prepend(new KeyValuePair<string, string>("", Localization.Tr.T("(default)")));

    /// <summary>Plasma cut data by name: tables in the drawing's units first, then all others.</summary>
    public static PlasmaCutData? FindCutData(Model.CadDocument doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var inches = doc.Units == Model.DrawingUnits.Inches;
        foreach (var table in PlasmaTables.OrderBy(t => (t.Units == Model.DrawingUnits.Inches) == inches ? 0 : 1))
            if (table.Find(name) is { } entry) return entry;
        return null;
    }

    /// <summary>
    /// Tool lookup: Part tool library → Machining tool library → (turning operations) BamCam-Lathe-{units} →
    /// Default-{units}.
    /// </summary>
    public static ToolDefinition? FindTool(Model.CadDocument doc, Part? part, int index, bool lathe = false)
    {
        if (index <= 0) return null;
        var units = doc.Units == Model.DrawingUnits.Inches ? "in" : "mm";
        foreach (var name in new[] { part?.ToolLibrary, doc.Machining.ToolLibrary, lathe ? LatheLibraryName(doc.Units) : null, $"Default-{units}" })
        {
            if (string.IsNullOrEmpty(name)) continue;
            var lib = ToolLibraries.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (lib?.Find(index) is { } tool) return tool;
        }
        return null;
    }

    /// <summary>
    /// What a tool setting of the part (or, without a part, of the machining options) means for the operations that do
    /// not choose their own tool. The fields of the part stay as typed, as in CamBam, so the message shows the result.
    /// </summary>
    public static string DescribeTool(Model.CadDocument doc, Part? part)
    {
        var owner = part?.Name ?? Localization.Tr.T("Machining");
        var number = part is { ToolNumber: > 0 } ? part.ToolNumber : doc.Machining.ToolNumber;
        var typed = part is { ToolDiameter: > 0 } ? part.ToolDiameter : doc.Machining.ToolDiameter;
        var typedProfile = part is { ToolProfile: not ToolProfile.Unspecified } ? part.ToolProfile : doc.Machining.ToolProfile;
        var tool = FindTool(doc, part, number);
        if (number > 0 && tool == null && typed <= 0)
            return Localization.Tr.F("{0}: tool {1} not found in the tool libraries.", owner, number);
        var profile = tool is { ToolProfile: not ToolProfile.Unspecified } ? tool.ToolProfile : typedProfile;
        var diameter = typed > 0 ? typed : tool?.Diameter ?? 0;
        if (number > 0)
            return Localization.Tr.F("{0}: operations without their own tool use tool {1}: diameter {2}, profile {3}.",
                owner, number, diameter, Localization.Tr.T(profile.ToString()));
        return diameter > 0
            ? Localization.Tr.F("{0}: operations without their own tool use diameter {1}, profile {2}.", owner, diameter, Localization.Tr.T(profile.ToString()))
            : Localization.Tr.F("{0}: no tool set for operations without their own tool.", owner);
    }

    /// <summary>
    /// A tool number was chosen on the operation: when a library has that tool, a Tool Diameter and Tool Profile typed in
    /// the operation go back to Default, so that the tool's own values are used (choosing a tool means that tool).
    /// Without such a tool they stay. Returns the display names of the parameters set back.
    /// </summary>
    public static List<string> FollowToolNumber(Model.CadDocument doc, MachineOp op)
    {
        var reset = new List<string>();
        var number = op.ToolNumber.Value;
        if (number <= 0) return reset;
        var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        if (FindTool(doc, part, number, op is TurningOp) == null) return reset;
        if (op.ToolDiameter.State == ParamState.Value)
        {
            op.ToolDiameter.State = ParamState.Default;
            reset.Add(Localization.Tr.T("Tool Diameter"));
        }
        if (op.ToolProfile.State == ParamState.Value)
        {
            op.ToolProfile.State = ParamState.Default;
            reset.Add(Localization.Tr.T("Tool Profile"));
        }
        return reset;
    }

    /// <summary>
    /// What the tool number chosen on an operation gives: the tool of the library, a number that no library has, or a
    /// diameter typed on the operation that wins over the library. Null without a number.
    /// </summary>
    public static string? DescribeOperationTool(Model.CadDocument doc, MachineOp op)
    {
        var number = op.ToolNumber.Value;
        if (number <= 0) return null;
        var part = doc.Parts.FirstOrDefault(p => p.Operations.Contains(op));
        if (FindTool(doc, part, number, op is TurningOp) is not { } tool)
            return Localization.Tr.F("{0}: tool {1} not found in the tool libraries.", op.Name, number);
        if (op.ToolDiameter.State == ParamState.Value && Math.Abs(op.ToolDiameter.Value - tool.Diameter) > 1e-9)
            return Localization.Tr.F("{0}: tool {1} has diameter {2} in the library, but the Tool Diameter {3} typed in this operation wins. Set Tool Diameter to Default to use the library.",
                op.Name, number, tool.Diameter, op.ToolDiameter.Value);
        return Localization.Tr.F("{0}: tool {1} from the library: diameter {2}, profile {3}.",
            op.Name, number, tool.Diameter, Localization.Tr.T(tool.ToolProfile.ToString()));
    }

    /// <summary>Name of BamCam's lathe tool library for the drawing units.</summary>
    public static string LatheLibraryName(Model.DrawingUnits units) =>
        units == Model.DrawingUnits.Inches ? "BamCam-Lathe-in" : "BamCam-Lathe-mm";

    /// <summary>
    /// Lathe tool templates (BamCam-Lathe-mm / -in), written to the tool folder when missing: turning tools for
    /// roughing and finishing, grooving and parting blades, a 60° threading tool and a drill.
    /// </summary>
    public static ToolLibrary LatheTemplates(bool inches)
    {
        var lib = new ToolLibrary { Name = LatheLibraryName(inches ? Model.DrawingUnits.Inches : Model.DrawingUnits.Millimeters), NumberFormat = "0.0##" };
        void Add(int index, string name, double diameter, LatheToolType type, int tip, ToolProfile profile = ToolProfile.Lathe) =>
            lib.Tools.Add(new ToolDefinition
            {
                Index = index, Name = name, Diameter = diameter, ToolProfile = profile, LatheToolType = type, TipOrientation = tip,
            });
        if (inches)
        {
            Add(1, "Turning R1/32 (roughing)", 0.0625, LatheToolType.Turning, 3);
            Add(2, "Turning R1/64 (finishing)", 0.03125, LatheToolType.Turning, 3);
            Add(3, "Grooving 1/8", 0.125, LatheToolType.Grooving, 3);
            Add(4, "Parting 1/16", 0.0625, LatheToolType.Grooving, 3);
            Add(5, "Threading 60°", 0.008, LatheToolType.Threading, 8);
            Add(6, "Drill 1/4", 0.25, LatheToolType.Drilling, 0, ToolProfile.Drill);
        }
        else
        {
            Add(1, "Turning R0.8 (roughing)", 1.6, LatheToolType.Turning, 3);
            Add(2, "Turning R0.4 (finishing)", 0.8, LatheToolType.Turning, 3);
            Add(3, "Grooving 3 mm", 3, LatheToolType.Grooving, 3);
            Add(4, "Parting 2 mm", 2, LatheToolType.Grooving, 3);
            Add(5, "Threading 60°", 0.2, LatheToolType.Threading, 8);
            Add(6, "Drill 5 mm", 5, LatheToolType.Drilling, 0, ToolProfile.Drill);
        }
        return lib;
    }

    /// <summary>The tool type a turning operation needs, or None for other operations.</summary>
    public static LatheToolType RequiredLatheTool(MachineOp op) => op switch
    {
        LatheOp or LatheFacingOp => LatheToolType.Turning,
        LatheGrooveOp => LatheToolType.Grooving,
        LatheThreadOp => LatheToolType.Threading,
        LatheDrillOp => LatheToolType.Drilling,
        _ => LatheToolType.None,
    };

    /// <summary>Style library lookup: explicit name, then {Material}-{units}, then Standard-{units}.</summary>
    public static CamStyleLibrary? FindStyleLibrary(Model.CadDocument doc, Part? part)
    {
        var units = doc.Units == Model.DrawingUnits.Inches ? "in" : "mm";
        var material = (part?.Stock is { IsEmpty: false } ps ? ps : doc.Machining.Stock).Material;
        var explicitName = !string.IsNullOrEmpty(part?.StyleLibrary) ? part!.StyleLibrary : doc.Machining.StyleLibrary;
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitName))
            candidates.Add(explicitName.Replace("{$Material}", material, StringComparison.OrdinalIgnoreCase)
                .Replace("{$Units}", units, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(material)) candidates.Add($"{material}-{units}");
        candidates.Add($"Standard-{units}");
        foreach (var c in candidates)
        {
            var lib = StyleLibraries.FirstOrDefault(l => l.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (lib != null) return lib;
        }
        return null;
    }
}
