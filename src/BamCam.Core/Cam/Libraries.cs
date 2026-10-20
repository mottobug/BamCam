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

    [Browsable(false)]
    public List<XElement> Extensions { get; } = [];

    public override string ToString() => $"{Index}: {Name}";
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

    /// <summary>Tool lookup: Part tool library → Machining tool library → Default-{units}.</summary>
    public static ToolDefinition? FindTool(Model.CadDocument doc, Part? part, int index)
    {
        if (index <= 0) return null;
        var units = doc.Units == Model.DrawingUnits.Inches ? "in" : "mm";
        foreach (var name in new[] { part?.ToolLibrary, doc.Machining.ToolLibrary, $"Default-{units}" })
        {
            if (string.IsNullOrEmpty(name)) continue;
            var lib = ToolLibraries.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (lib?.Find(index) is { } tool) return tool;
        }
        return null;
    }

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
